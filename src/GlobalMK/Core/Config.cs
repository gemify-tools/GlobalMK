using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace GlobalMK
{
    public enum Role { Server = 0, Client = 1 }

    [DataContract]
    public sealed class ScreenRect
    {
        [DataMember] public int X;
        [DataMember] public int Y;
        [DataMember] public int W;
        [DataMember] public int H;
        [DataMember] public bool Primary;
    }

    [DataContract]
    public sealed class MachineLayout
    {
        [DataMember] public string Name;
        [DataMember] public int X;
        [DataMember] public int Y;
        [DataMember] public List<ScreenRect> Screens = new List<ScreenRect>();
    }

    [DataContract]
    public sealed class AppConfig
    {
        public const int DefaultPort = 24860;

        [DataMember] public string Role;
        [DataMember] public string MachineName;
        [DataMember] public string PassphraseProtected;
        [DataMember] public int Port;
        [DataMember] public List<string> Peers;
        [DataMember] public bool AutoConnectDiscovered;
        [DataMember] public bool OnlyPrivateNetworks;
        [DataMember] public bool ClipboardSync;
        [DataMember] public bool HideCursorWhenRemote;
        [DataMember] public int SwitchDelayMs;
        [DataMember] public int CornerDeadZone;
        [DataMember] public bool StartMinimized;
        [DataMember] public bool DiscoverOtherNetworks;
        [DataMember] public List<string> KnownPeers;
        [DataMember] public List<MachineLayout> Layout;

        public AppConfig() => SetDefaults();

        [OnDeserializing]
        private void OnDeserializing(StreamingContext _) => SetDefaults();

        private void SetDefaults()
        {
            Role = "server";
            MachineName = Environment.MachineName;
            PassphraseProtected = null;
            Port = DefaultPort;
            Peers = new List<string>();
            AutoConnectDiscovered = true;
            OnlyPrivateNetworks = true;
            ClipboardSync = true;
            HideCursorWhenRemote = true;
            SwitchDelayMs = 0;
            CornerDeadZone = 0;
            StartMinimized = false;
            DiscoverOtherNetworks = true;
            KnownPeers = new List<string>();
            Layout = new List<MachineLayout>();
        }

        /// <summary>Remember an address this computer successfully connected to, so it can reconnect directly.</summary>
        public bool RememberPeer(string ipAndPort)
        {
            string ip = ipAndPort.Substring(0, ipAndPort.LastIndexOf(':') + 1);
            if (KnownPeers.Count > 0 && KnownPeers[0] == ipAndPort) return false;
            KnownPeers.RemoveAll(k => k == ipAndPort || k.StartsWith(ip, StringComparison.Ordinal));
            KnownPeers.Insert(0, ipAndPort);
            if (KnownPeers.Count > 16) KnownPeers.RemoveRange(16, KnownPeers.Count - 16);
            return true;
        }

        public Role RoleValue
        {
            get => string.Equals(Role, "client", StringComparison.OrdinalIgnoreCase) ? GlobalMK.Role.Client : GlobalMK.Role.Server;
            set => Role = value == GlobalMK.Role.Client ? "client" : "server";
        }

        /// <summary>Passphrase, stored encrypted for the current Windows user (DPAPI).</summary>
        public string Passphrase
        {
            get
            {
                if (string.IsNullOrEmpty(PassphraseProtected)) return "";
                try
                {
                    var bytes = ProtectedData.Unprotect(Convert.FromBase64String(PassphraseProtected), Entropy, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(bytes);
                }
                catch
                {
                    return "";
                }
            }
            set
            {
                if (string.IsNullOrEmpty(value)) { PassphraseProtected = null; return; }
                var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
                PassphraseProtected = Convert.ToBase64String(bytes);
            }
        }

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GlobalMK.passphrase.v1");

        public MachineLayout FindLayout(string name) =>
            Layout.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

        public static string Directory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlobalMK");

        public static string FilePath => Path.Combine(Directory, "config.json");

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    using (var fs = File.OpenRead(FilePath))
                    {
                        var cfg = (AppConfig)new DataContractJsonSerializer(typeof(AppConfig)).ReadObject(fs);
                        cfg.Normalize();
                        return cfg;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read settings, using defaults: " + ex.Message);
            }
            return new AppConfig();
        }

        public void Save()
        {
            Normalize();
            System.IO.Directory.CreateDirectory(Directory);
            string tmp = FilePath + ".tmp";
            using (var fs = File.Create(tmp))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, true, true, "  "))
            {
                new DataContractJsonSerializer(typeof(AppConfig)).WriteObject(writer, this);
            }
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }

        public AppConfig Clone()
        {
            using (var ms = new MemoryStream())
            {
                var ser = new DataContractJsonSerializer(typeof(AppConfig));
                ser.WriteObject(ms, this);
                ms.Position = 0;
                return (AppConfig)ser.ReadObject(ms);
            }
        }

        private void Normalize()
        {
            if (string.IsNullOrWhiteSpace(MachineName)) MachineName = Environment.MachineName;
            MachineName = MachineName.Trim();
            if (Port < 1 || Port > 65534) Port = DefaultPort;
            Peers = (Peers ?? new List<string>()).Select(p => p.Trim()).Where(p => p.Length > 0).Distinct().ToList();
            KnownPeers = (KnownPeers ?? new List<string>()).Where(k => k.Contains(":")).Distinct().Take(16).ToList();
            Layout = Layout ?? new List<MachineLayout>();
            foreach (var l in Layout) l.Screens = l.Screens ?? new List<ScreenRect>();
            SwitchDelayMs = Math.Max(0, Math.Min(SwitchDelayMs, 2000));
            CornerDeadZone = Math.Max(0, Math.Min(CornerDeadZone, 200));
        }
    }
}
