using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GlobalMK
{
    public sealed class PeerAuthenticationException : Exception
    {
        public PeerAuthenticationException(string message) : base(message) { }
    }

    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
    }

    /// <summary>
    /// Encrypted, authenticated framing over a TCP stream.
    ///
    /// Handshake: each side sends an ephemeral ECDH P-256 public key and a random nonce. The session secret
    /// mixes the ECDH result with a key derived from the shared passphrase (PBKDF2-SHA256), so only peers
    /// that know the passphrase can talk, and recorded traffic can't be decrypted later.
    ///
    /// Frames: [u32 length][AES-256-CTR ciphertext][16-byte truncated HMAC-SHA256 tag]. Each direction has its
    /// own keys and an implicit 64-bit sequence number (used as the CTR nonce and covered by the tag), so frames
    /// can't be replayed, reordered or dropped without detection.
    /// </summary>
    public sealed class SecureChannel : IDisposable
    {
        public const int MaxFrame = 4 * 1024 * 1024;
        private const byte Version = 1;
        private const int NonceSize = 32;
        private const int TagSize = 16;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("GMK1");

        private readonly Stream _stream;
        private readonly ICryptoTransform _sendEcb;
        private readonly ICryptoTransform _recvEcb;
        private readonly HMACSHA256 _sendMac;
        private readonly HMACSHA256 _recvMac;
        private readonly SymmetricAlgorithm _sendAes;
        private readonly SymmetricAlgorithm _recvAes;
        private ulong _sendSeq;
        private ulong _recvSeq;

        /// <summary>Derive the long-term key from the passphrase. Slow on purpose; do it once.</summary>
        public static byte[] DerivePassphraseKey(string passphrase)
        {
            var salt = Encoding.UTF8.GetBytes("GlobalMK passphrase salt v1");
            using (var kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(passphrase ?? ""), salt, 200000, HashAlgorithmName.SHA256))
                return kdf.GetBytes(32);
        }

        private SecureChannel(Stream stream, byte[] sendEnc, byte[] sendMac, byte[] recvEnc, byte[] recvMac)
        {
            _stream = stream;
            _sendAes = NewAes(sendEnc);
            _recvAes = NewAes(recvEnc);
            _sendEcb = _sendAes.CreateEncryptor();
            _recvEcb = _recvAes.CreateEncryptor(); // CTR mode only ever encrypts the counter
            _sendMac = new HMACSHA256(sendMac);
            _recvMac = new HMACSHA256(recvMac);
        }

        private static SymmetricAlgorithm NewAes(byte[] key)
        {
            var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.KeySize = 256;
            aes.Key = key;
            return aes;
        }

        public static SecureChannel Handshake(Stream stream, byte[] passphraseKey, bool initiator)
        {
            using (var ecdh = new ECDiffieHellmanCng(256))
            {
                ecdh.KeyDerivationFunction = ECDiffieHellmanKeyDerivationFunction.Hash;
                ecdh.HashAlgorithm = CngAlgorithm.Sha256;
                byte[] myPub = ecdh.PublicKey.ToByteArray();
                byte[] myNonce = new byte[NonceSize];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(myNonce);

                var hello = new byte[4 + 1 + 2 + myPub.Length + NonceSize];
                Buffer.BlockCopy(Magic, 0, hello, 0, 4);
                hello[4] = Version;
                hello[5] = (byte)(myPub.Length >> 8);
                hello[6] = (byte)myPub.Length;
                Buffer.BlockCopy(myPub, 0, hello, 7, myPub.Length);
                Buffer.BlockCopy(myNonce, 0, hello, 7 + myPub.Length, NonceSize);
                stream.Write(hello, 0, hello.Length);
                stream.Flush();

                var head = new byte[7];
                ReadExactly(stream, head, 0, 7);
                if (head[0] != Magic[0] || head[1] != Magic[1] || head[2] != Magic[2] || head[3] != Magic[3])
                    throw new ProtocolException("The other side is not Global Mouse & Keyboard (wrong port?).");
                if (head[4] != Version)
                    throw new ProtocolException($"Incompatible protocol version {head[4]} (this computer uses {Version}). Use the same app version on both computers.");
                int peerPubLen = (head[5] << 8) | head[6];
                if (peerPubLen < 16 || peerPubLen > 512) throw new ProtocolException("Malformed handshake.");
                var peerPub = new byte[peerPubLen];
                var peerNonce = new byte[NonceSize];
                ReadExactly(stream, peerPub, 0, peerPubLen);
                ReadExactly(stream, peerNonce, 0, NonceSize);

                byte[] shared;
                try
                {
                    using (var peerKey = ECDiffieHellmanCngPublicKey.FromByteArray(peerPub, CngKeyBlobFormat.EccPublicBlob))
                        shared = ecdh.DeriveKeyMaterial(peerKey);
                }
                catch (CryptographicException)
                {
                    throw new ProtocolException("Malformed handshake key.");
                }

                byte[] transcriptHash;
                using (var sha = SHA256.Create())
                using (var ms = new MemoryStream())
                {
                    ms.Write(Magic, 0, 4);
                    if (initiator) { Append(ms, myPub, myNonce, peerPub, peerNonce); }
                    else { Append(ms, peerPub, peerNonce, myPub, myNonce); }
                    transcriptHash = sha.ComputeHash(ms.ToArray());
                }

                byte[] master;
                using (var h = new HMACSHA256(passphraseKey))
                {
                    var input = new byte[shared.Length + transcriptHash.Length];
                    Buffer.BlockCopy(shared, 0, input, 0, shared.Length);
                    Buffer.BlockCopy(transcriptHash, 0, input, shared.Length, transcriptHash.Length);
                    master = h.ComputeHash(input);
                }

                byte[] i2rEnc = Expand(master, "i2r enc"), i2rMac = Expand(master, "i2r mac");
                byte[] r2iEnc = Expand(master, "r2i enc"), r2iMac = Expand(master, "r2i mac");
                return initiator
                    ? new SecureChannel(stream, i2rEnc, i2rMac, r2iEnc, r2iMac)
                    : new SecureChannel(stream, r2iEnc, r2iMac, i2rEnc, i2rMac);
            }
        }

        private static void Append(Stream s, params byte[][] parts)
        {
            foreach (var p in parts) s.Write(p, 0, p.Length);
        }

        private static byte[] Expand(byte[] master, string label)
        {
            using (var h = new HMACSHA256(master)) return h.ComputeHash(Encoding.ASCII.GetBytes(label));
        }

        /// <summary>Encrypt one frame and append it to <paramref name="output"/>. Single writer thread only.</summary>
        public void EncryptFrameTo(Stream output, byte[] payload, int offset, int count)
        {
            if (count > MaxFrame) throw new ArgumentException("Frame too large");
            var frame = new byte[4 + count + TagSize];
            frame[0] = (byte)(count >> 24);
            frame[1] = (byte)(count >> 16);
            frame[2] = (byte)(count >> 8);
            frame[3] = (byte)count;
            Buffer.BlockCopy(payload, offset, frame, 4, count);
            ApplyKeystream(_sendEcb, _sendSeq, frame, 4, count);
            var tag = ComputeTag(_sendMac, _sendSeq, frame, 4 + count);
            Buffer.BlockCopy(tag, 0, frame, 4 + count, TagSize);
            _sendSeq++;
            output.Write(frame, 0, frame.Length);
        }

        public void WriteFrame(byte[] payload)
        {
            EncryptFrameTo(_stream, payload, 0, payload.Length);
            _stream.Flush();
        }

        /// <summary>Read and authenticate one frame. Single reader thread only.</summary>
        public byte[] ReadFrame()
        {
            var lenBytes = new byte[4];
            ReadExactly(_stream, lenBytes, 0, 4);
            int len = (lenBytes[0] << 24) | (lenBytes[1] << 16) | (lenBytes[2] << 8) | lenBytes[3];
            if (len < 0 || len > MaxFrame) throw new PeerAuthenticationException("Received an invalid frame (wrong passphrase?).");
            var frame = new byte[4 + len + TagSize];
            Buffer.BlockCopy(lenBytes, 0, frame, 0, 4);
            ReadExactly(_stream, frame, 4, len + TagSize);
            var expected = ComputeTag(_recvMac, _recvSeq, frame, 4 + len);
            int diff = 0;
            for (int i = 0; i < TagSize; i++) diff |= expected[i] ^ frame[4 + len + i];
            if (diff != 0) throw new PeerAuthenticationException("Message authentication failed — the passphrases don't match.");
            ApplyKeystream(_recvEcb, _recvSeq, frame, 4, len);
            _recvSeq++;
            var payload = new byte[len];
            Buffer.BlockCopy(frame, 4, payload, 0, len);
            return payload;
        }

        private static byte[] ComputeTag(HMACSHA256 mac, ulong seq, byte[] data, int count)
        {
            var seqBytes = new byte[8];
            for (int i = 0; i < 8; i++) seqBytes[i] = (byte)(seq >> (56 - 8 * i));
            mac.Initialize();
            mac.TransformBlock(seqBytes, 0, 8, null, 0);
            mac.TransformFinalBlock(data, 0, count);
            return mac.Hash;
        }

        private static void ApplyKeystream(ICryptoTransform ecb, ulong seq, byte[] data, int offset, int count)
        {
            if (count == 0) return;
            int blocks = (count + 15) / 16;
            var ctr = new byte[blocks * 16];
            for (int b = 0; b < blocks; b++)
            {
                int o = b * 16;
                for (int i = 0; i < 8; i++) ctr[o + i] = (byte)(seq >> (56 - 8 * i));
                ulong bi = (ulong)b;
                for (int i = 0; i < 8; i++) ctr[o + 8 + i] = (byte)(bi >> (56 - 8 * i));
            }
            var ks = new byte[ctr.Length];
            ecb.TransformBlock(ctr, 0, ctr.Length, ks, 0);
            for (int i = 0; i < count; i++) data[offset + i] ^= ks[i];
        }

        public static void ReadExactly(Stream s, byte[] buf, int offset, int count)
        {
            while (count > 0)
            {
                int n = s.Read(buf, offset, count);
                if (n <= 0) throw new EndOfStreamException("Connection closed by the other computer.");
                offset += n;
                count -= n;
            }
        }

        public void Dispose()
        {
            _sendEcb.Dispose();
            _recvEcb.Dispose();
            _sendAes.Dispose();
            _recvAes.Dispose();
            _sendMac.Dispose();
            _recvMac.Dispose();
        }
    }
}
