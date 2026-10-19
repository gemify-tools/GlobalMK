using System;
using System.Drawing;
using System.Windows.Forms;

namespace GlobalMK
{
    /// <summary>Main window: construction of controls. Behaviour lives in MainForm.Logic.cs.</summary>
    internal sealed partial class MainForm : Form
    {
        private readonly AppController _app;
        private readonly bool _preview;

        private Label _statusDot, _statusTitle, _statusSub;
        private TabControl _tabs;
        private TabPage _tabSetup, _tabLayout, _tabStatus;

        private RadioButton _rbServer, _rbClient;
        private TextBox _txtName, _txtPass, _txtPeers, _txtMyIps;
        private CheckBox _chkShowPass, _chkClipboard, _chkHideCursor, _chkAutoConnect, _chkOtherNetworks, _chkPrivateOnly, _chkStartup, _chkStartMin;
        private NumericUpDown _numPort, _numDelay, _numCorner;
        private Button _btnApply, _btnFirewall, _btnAdmin;
        private Label _lblAdmin, _lblHelper;
        private Button _btnHelperInstall, _btnHelperRemove;

        private LayoutEditor _editor;
        private Label _lblLayoutHelp, _lblClientLayout;

        private ListView _lvPeers, _lvDiscovered;
        private TextBox _txtLog;

        private NotifyIcon _tray;
        private ToolStripMenuItem _miOpen, _miLock, _miHome, _miExit;
        private Timer _timer;

        public MainForm(AppController app, bool preview = false)
        {
            _app = app;
            _preview = preview;

            Text = "Global Mouse & Keyboard";
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(800, 640);
            MinimumSize = new Size(660, 540);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = AppIcon.Large;
            BackColor = Color.White;

            SuspendLayout();
            BuildHeader();
            BuildTabs();
            ResumeLayout(true);

            if (!preview) BuildTray();
            WireEvents();
        }

        private void BuildHeader()
        {
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 2,
                Padding = new Padding(14, 12, 14, 8),
                BackColor = Color.White,
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _statusDot = new Label { Text = "●", AutoSize = true, Font = new Font(Font.FontFamily, 14F), ForeColor = Color.Gray, Margin = new Padding(0, 0, 6, 0) };
            _statusTitle = new Label { AutoSize = true, Font = new Font(Font.FontFamily, 12F, FontStyle.Bold), Margin = new Padding(0, 2, 0, 0), Text = "Starting…" };
            _statusSub = new Label { AutoSize = true, ForeColor = Color.FromArgb(75, 85, 99), Margin = new Padding(0, 2, 0, 0) };
            header.Controls.Add(_statusDot, 0, 0);
            header.Controls.Add(_statusTitle, 1, 0);
            header.Controls.Add(_statusSub, 1, 1);
            Controls.Add(header);
        }

        private void BuildTabs()
        {
            _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
            _tabSetup = new TabPage("Setup") { BackColor = Color.White };
            _tabLayout = new TabPage("Screen layout") { BackColor = Color.White };
            _tabStatus = new TabPage("Connections & log") { BackColor = Color.White };
            _tabs.TabPages.AddRange(new[] { _tabSetup, _tabLayout, _tabStatus });
            Controls.Add(_tabs);
            _tabs.BringToFront();

            BuildSetupTab();
            BuildLayoutTab();
            BuildStatusTab();
        }

        private void BuildSetupTab()
        {
            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10, 10, 10, 10) };
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scroller.Controls.Add(grid);
            _tabSetup.Controls.Add(scroller);

            _rbServer = new RadioButton { Text = "Server — its mouse and keyboard control the other computers", AutoSize = true };
            _rbClient = new RadioButton { Text = "Client — controlled by the server's mouse and keyboard", AutoSize = true };
            AddRow(grid, "This computer is", Stack(_rbServer, _rbClient));

            _txtName = new TextBox { Width = 260 };
            AddRow(grid, "Computer name", Stack(_txtName, Hint("Shown in the screen layout. Each computer needs a different name.")));

            _txtPass = new TextBox { Width = 260, UseSystemPasswordChar = true };
            _chkShowPass = new CheckBox { Text = "Show", AutoSize = true, Margin = new Padding(8, 5, 0, 0) };
            AddRow(grid, "Passphrase", Stack(Row(_txtPass, _chkShowPass),
                Hint("Use the same passphrase on every computer. It authenticates computers and encrypts all mouse, keyboard and clipboard traffic.")));

            _txtPeers = new TextBox { Width = 400 };
            AddRow(grid, "Connect to", Stack(_txtPeers,
                Hint("Usually empty: computers find each other automatically on 192.168.0.x and 192.168.1.x, including when they are behind different routers. If one isn't found, enter its IP address here, for example 192.168.0.20 (separate several with commas). Only one side has to list the other.")));

            _txtMyIps = new TextBox { ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White, Width = 400 };
            AddRow(grid, "This computer's address", _txtMyIps);

            _numPort = new NumericUpDown { Minimum = 1024, Maximum = 65534, Width = 90 };
            AddRow(grid, "Port", Stack(_numPort, Hint("TCP port (and the next one for UDP discovery). Must match on all computers.")));

            _chkClipboard = Check("Share the clipboard (text and images)");
            _chkHideCursor = Check("Hide this computer's cursor while controlling another computer (server)");
            _chkAutoConnect = Check("Connect automatically to computers that are found");
            _chkOtherNetworks = Check("Also look for computers on the other home network (192.168.0.x ↔ 192.168.1.x)");
            _chkPrivateOnly = Check("Only accept connections from home/private network addresses");
            _chkStartup = Check("Start with Windows");
            _chkStartMin = Check("Start minimized to the notification area");
            AddRow(grid, "Options", Stack(_chkClipboard, _chkHideCursor, _chkAutoConnect, _chkOtherNetworks, _chkPrivateOnly, _chkStartup, _chkStartMin));

            _numDelay = new NumericUpDown { Minimum = 0, Maximum = 2000, Increment = 50, Width = 70 };
            _numCorner = new NumericUpDown { Minimum = 0, Maximum = 200, Width = 60 };
            AddRow(grid, "Switching", Stack(
                Row(new Label { Text = "Wait", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, _numDelay,
                    new Label { Text = "ms at the edge before switching;  ignore", AutoSize = true, Margin = new Padding(4, 6, 4, 0) }, _numCorner,
                    new Label { Text = "px near corners", AutoSize = true, Margin = new Padding(4, 6, 0, 0) }),
                Hint("Raise these if the cursor jumps to another computer when you reach for a window's close button or the Start button.")));

            _lblHelper = new Label { AutoSize = true, MaximumSize = new Size(500, 0), Margin = new Padding(0, 6, 0, 4) };
            _btnHelperInstall = new Button { Text = "Install helper service…", AutoSize = true, FlatStyle = FlatStyle.System };
            _btnHelperRemove = new Button { Text = "Remove helper service…", AutoSize = true, FlatStyle = FlatStyle.System };
            AddRow(grid, "Lock screen and UAC prompts", Stack(_lblHelper, Row(_btnHelperInstall, _btnHelperRemove)));

            _btnApply = new Button { Text = "Save && apply", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
            _btnFirewall = new Button { Text = "Allow through Windows Firewall…", AutoSize = true, FlatStyle = FlatStyle.System };
            _btnAdmin = new Button { Text = "Restart as administrator", AutoSize = true, FlatStyle = FlatStyle.System };
            AcceptButton = _btnApply;
            _lblAdmin = Hint("");
            AddRow(grid, "", Stack(Row(_btnApply, _btnFirewall, _btnAdmin), _lblAdmin));
        }

        private void BuildLayoutTab()
        {
            _lblLayoutHelp = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 58,
                Padding = new Padding(10, 8, 10, 4),
                ForeColor = Color.FromArgb(55, 65, 81),
                Text = "Drag the computers so their screens touch where you want the mouse to cross over. The keyboard follows the mouse. " +
                       "Scroll Lock (or Ctrl+Alt+Shift+L) locks the cursor to the computer it is on; Ctrl+Alt+Shift+H brings it back to the server.",
            };
            _editor = new LayoutEditor { Dock = DockStyle.Fill, Font = Font };
            _lblClientLayout = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(75, 85, 99),
                Text = "This computer is a client.\nArrange the screens on the server computer — the one whose mouse and keyboard you share.",
                Visible = false,
            };
            _tabLayout.Controls.Add(_editor);
            _tabLayout.Controls.Add(_lblClientLayout);
            _tabLayout.Controls.Add(_lblLayoutHelp);
        }

        private void BuildStatusTab()
        {
            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(10) };
            split.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            split.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
            split.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            split.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            split.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _lvPeers = NewList(("Computer", 150), ("Role", 70), ("Address", 130), ("Screens", 200), ("Ping", 60), ("Since", 80));
            _lvDiscovered = NewList(("Computer", 150), ("Role", 70), ("Address", 130), ("Network", 200));
            _txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = false,
                BackColor = Color.FromArgb(249, 250, 251),
                Font = new Font("Consolas", 9F),
            };

            split.Controls.Add(Caption("Connected computers"), 0, 0);
            split.Controls.Add(_lvPeers, 0, 1);
            split.Controls.Add(Caption("Found automatically"), 0, 2);
            split.Controls.Add(_lvDiscovered, 0, 3);
            split.Controls.Add(Caption("Log"), 0, 4);
            split.Controls.Add(_txtLog, 0, 5);
            _tabStatus.Controls.Add(split);
        }

        private void BuildTray()
        {
            _miOpen = new ToolStripMenuItem("Open Global Mouse && Keyboard") { Font = new Font(Font, FontStyle.Bold) };
            _miLock = new ToolStripMenuItem("Lock cursor to current computer");
            _miHome = new ToolStripMenuItem("Bring cursor back to this computer");
            _miExit = new ToolStripMenuItem("Exit");
            var menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[] { _miOpen, _miLock, _miHome, new ToolStripSeparator(), _miExit });
            _tray = new NotifyIcon { Icon = AppIcon.Small(Color.Gray), Text = "Global Mouse & Keyboard", ContextMenuStrip = menu, Visible = true };
            _timer = new Timer { Interval = 1000 };
        }

        // ------------------------------------------------------------------ small builders

        private static void AddRow(TableLayoutPanel grid, string label, Control content)
        {
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(3, 7, 16, 10),
                ForeColor = Color.FromArgb(55, 65, 81),
            }, 0, row);
            content.Margin = new Padding(3, 3, 3, 10);
            grid.Controls.Add(content, 1, row);
        }

        private static FlowLayoutPanel Stack(params Control[] items)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            p.Controls.AddRange(items);
            return p;
        }

        private static FlowLayoutPanel Row(params Control[] items)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            p.Controls.AddRange(items);
            return p;
        }

        private static Label Hint(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            ForeColor = Color.FromArgb(107, 114, 128),
            Margin = new Padding(0, 3, 0, 0),
        };

        private static CheckBox Check(string text) => new CheckBox { Text = text, AutoSize = true, Margin = new Padding(0, 2, 0, 2) };

        private static Label Caption(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Margin = new Padding(0, 8, 0, 4),
        };

        private static ListView NewList(params (string title, int width)[] columns)
        {
            var lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
            foreach (var c in columns) lv.Columns.Add(c.title, c.width);
            return lv;
        }
    }
}
