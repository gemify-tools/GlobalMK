using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace GlobalMK
{
    /// <summary>
    /// Watches this computer's clipboard (text and images) and applies clipboard content received from peers.
    /// Lives on the UI thread because the Windows clipboard needs an STA thread.
    /// </summary>
    internal sealed class ClipboardSync : NativeWindow, IDisposable
    {
        private const int SuppressMs = 600;
        private readonly string _origin;
        private readonly Action<ClipData> _onLocalChange;
        private string _lastHash;
        private int _lastSetTick = Environment.TickCount - 10000;

        public bool Enabled { get; set; } = true;

        public ClipboardSync(string origin, Action<ClipData> onLocalChange)
        {
            _origin = origin;
            _onLocalChange = onLocalChange;
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // message-only window
            if (!Native.AddClipboardFormatListener(Handle))
                Log.Warn("Clipboard sharing unavailable: could not watch the clipboard.");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_CLIPBOARDUPDATE) OnClipboardUpdate();
            base.WndProc(ref m);
        }

        private void OnClipboardUpdate()
        {
            if (!Enabled) return;
            if (unchecked(Environment.TickCount - _lastSetTick) < SuppressMs) return; // our own write
            ClipData clip;
            try { clip = Read(); }
            catch (Exception ex)
            {
                Log.Warn("Could not read the clipboard: " + ex.Message);
                return;
            }
            if (clip == null) return;
            string hash = clip.HashKey();
            if (hash == _lastHash) return;
            _lastHash = hash;
            _onLocalChange(clip);
        }

        private ClipData Read()
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
                    {
                        string text = Clipboard.GetText(TextDataFormat.UnicodeText);
                        if (string.IsNullOrEmpty(text) || text.Length > ClipboardTransfer.MaxTextBytes / 2) return null;
                        return ClipData.FromText(text, _origin);
                    }
                    if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is MemoryStream pngStream)
                    {
                        var png = pngStream.ToArray();
                        return png.Length > 0 && png.Length <= ClipboardTransfer.MaxWireBytes ? new ClipData(ClipFormat.Png, png, _origin) : null;
                    }
                    if (Clipboard.ContainsImage())
                    {
                        using (var img = Clipboard.GetImage())
                        {
                            if (img == null) return null;
                            using (var ms = new MemoryStream())
                            {
                                img.Save(ms, ImageFormat.Png);
                                return ms.Length <= ClipboardTransfer.MaxWireBytes ? new ClipData(ClipFormat.Png, ms.ToArray(), _origin) : null;
                            }
                        }
                    }
                    return null;
                }
                catch (ExternalException) when (attempt < 5)
                {
                    Thread.Sleep(50); // another program has the clipboard open
                }
            }
        }

        /// <summary>Put content received from another computer on this computer's clipboard.</summary>
        public void Apply(ClipData clip)
        {
            if (!Enabled) return;
            _lastHash = clip.HashKey();
            _lastSetTick = Environment.TickCount;
            try
            {
                if (clip.Format == ClipFormat.Text)
                {
                    string text = clip.Text;
                    if (text.Length == 0) return;
                    var data = new DataObject();
                    data.SetText(text, TextDataFormat.UnicodeText);
                    Clipboard.SetDataObject(data, true, 10, 50);
                }
                else
                {
                    using (var ms = new MemoryStream(clip.Data))
                    using (var img = Image.FromStream(ms))
                    using (var bmp = new Bitmap(img))
                    {
                        var data = new DataObject();
                        data.SetData(DataFormats.Bitmap, true, bmp);
                        data.SetData("PNG", false, new MemoryStream(clip.Data));
                        Clipboard.SetDataObject(data, true, 10, 50);
                    }
                }
                _lastSetTick = Environment.TickCount;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not set the clipboard: " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                Native.RemoveClipboardFormatListener(Handle);
                DestroyHandle();
            }
        }
    }
}
