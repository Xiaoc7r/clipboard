using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

[assembly: AssemblyTitle("PicoPaste")]
[assembly: AssemblyDescription("A small edge-docked clipboard for Windows")]
[assembly: AssemblyProduct("PicoPaste")]
[assembly: AssemblyVersion("0.4.0.0")]
[assembly: AssemblyFileVersion("0.4.0.0")]

namespace PicoPaste
{
    internal static class Program
    {
        private const string MutexName = "Local\\PicoPaste.SingleInstance";
        private const string ShowEventName = "Local\\PicoPaste.ShowWindow";

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length == 2 && string.Equals(args[0], "--preview", StringComparison.OrdinalIgnoreCase))
            {
                using (ClipboardDockForm preview = new ClipboardDockForm(null, true))
                    preview.RenderPreview(args[1]);
                return;
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    try
                    {
                        using (EventWaitHandle signal = EventWaitHandle.OpenExisting(ShowEventName))
                            signal.Set();
                    }
                    catch { }
                    return;
                }

                using (EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                using (ClipboardDockForm form = new ClipboardDockForm(showEvent))
                    Application.Run(form);
            }
        }
    }

    [DataContract]
    internal sealed class ClipEntry
    {
        [DataMember(Name = "text")]
        public string Text { get; set; }

        [DataMember(Name = "createdAt")]
        public DateTime CreatedAt { get; set; }
    }

    [DataContract]
    internal sealed class UserPreferences
    {
        [DataMember(Name = "settingsVersion")]
        public int SettingsVersion { get; set; }

        [DataMember(Name = "dockSide")]
        public string DockSide { get; set; }

        [DataMember(Name = "topRatio")]
        public double TopRatio { get; set; }

        [DataMember(Name = "translucent")]
        public bool Translucent { get; set; }

        [DataMember(Name = "pinned")]
        public bool Pinned { get; set; }

        [DataMember(Name = "alwaysOnTop")]
        public bool AlwaysOnTop { get; set; }

        [DataMember(Name = "performanceMode")]
        public bool PerformanceMode { get; set; }

        public static UserPreferences CreateDefault()
        {
            return new UserPreferences
            {
                SettingsVersion = 3,
                DockSide = "Right",
                TopRatio = 0.5,
                Translucent = true,
                Pinned = false,
                AlwaysOnTop = true,
                PerformanceMode = true
            };
        }
    }

    internal static class LocalStore
    {
        private static readonly string AppDirectory = ResolveAppDirectory();
        private static readonly string HistoryFile = Path.Combine(AppDirectory, "history.json");
        private static readonly string SettingsFile = Path.Combine(AppDirectory, "settings.json");

        private static string ResolveAppDirectory()
        {
            string overridePath = Environment.GetEnvironmentVariable("PICOPASTE_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overridePath)) return Path.GetFullPath(overridePath);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PicoPaste");
        }

        public static List<ClipEntry> LoadHistory()
        {
            List<ClipEntry> entries = ReadJson<List<ClipEntry>>(HistoryFile);
            return entries ?? new List<ClipEntry>();
        }

        public static void SaveHistory(List<ClipEntry> entries)
        {
            WriteJson(HistoryFile, entries);
        }

        public static void ClearHistory()
        {
            try { if (File.Exists(HistoryFile)) File.Delete(HistoryFile); }
            catch { }
        }

        public static UserPreferences LoadPreferences()
        {
            UserPreferences preferences = ReadJson<UserPreferences>(SettingsFile);
            if (preferences == null) return UserPreferences.CreateDefault();
            bool migrated = false;
            if (preferences.SettingsVersion < 2)
            {
                preferences.SettingsVersion = 2;
                preferences.PerformanceMode = true;
                preferences.Translucent = false;
                migrated = true;
            }
            if (preferences.SettingsVersion < 3)
            {
                preferences.SettingsVersion = 3;
                preferences.DockSide = "Right";
                preferences.Translucent = true;
                migrated = true;
            }
            if (preferences.DockSide != "Left" && preferences.DockSide != "Right")
                preferences.DockSide = "Right";
            if (preferences.TopRatio < 0 || preferences.TopRatio > 1)
                preferences.TopRatio = 0.5;
            if (migrated) SavePreferences(preferences);
            return preferences;
        }

        public static void SavePreferences(UserPreferences preferences)
        {
            WriteJson(SettingsFile, preferences);
        }

        private static T ReadJson<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (FileStream stream = File.OpenRead(path))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
                    return serializer.ReadObject(stream) as T;
                }
            }
            catch { return null; }
        }

        private static void WriteJson<T>(string path, T value)
        {
            try
            {
                Directory.CreateDirectory(AppDirectory);
                string temp = path + ".tmp";
                using (FileStream stream = File.Create(temp))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
                    serializer.WriteObject(stream, value);
                    stream.Flush();
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch { }
        }
    }

    internal enum DockEdge
    {
        Left,
        Right,
        Floating
    }

    internal sealed class ClipboardDockForm : Form
    {
        private const int WindowWidth = 304;
        private const int WindowHeight = 370;
        private const int VisibleHandle = 8;
        private const int VisibleCards = 4;
        private const int MaxHistory = 40;
        private const int MaxTextLength = 131072;
        private const int MaxHistoryCharacters = 524288;
        private const int PerformanceAnimationDuration = 95;
        private const int SmoothAnimationDuration = 175;
        private const int SnapDistance = 58;
        private const int WmClipboardUpdate = 0x031D;
        private const int WmHotkey = 0x0312;
        private const int WmDpiChanged = 0x02E0;
        private const int HotkeyId = 0x5043;
        private const uint ModShift = 0x0004;
        private const uint ModNoRepeat = 0x4000;

        private readonly bool _previewMode;
        private readonly List<ClipEntry> _entries;
        private readonly UserPreferences _preferences;
        private readonly Rectangle[] _cardBounds = new Rectangle[VisibleCards];
        private readonly Timer _autoHideTimer;
        private readonly Timer _animationTimer;
        private readonly Timer _feedbackTimer;
        private readonly Timer _saveTimer;
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _trayMenu;
        private readonly ContextMenuStrip _entryMenu;
        private readonly EventWaitHandle _showEvent;
        private ToolStripMenuItem _pinMenuItem;
        private ToolStripMenuItem _translucentMenuItem;
        private ToolStripMenuItem _topmostMenuItem;
        private ToolStripMenuItem _performanceMenuItem;
        private RegisteredWaitHandle _showEventRegistration;

        private Rectangle _pinBounds;
        private Rectangle _minimizeBounds;
        private Rectangle _closeBounds;
        private Rectangle _previousBounds;
        private Rectangle _nextBounds;
        private Rectangle _clearBounds;
        private Rectangle _headerDragBounds;

        private DockEdge _dockEdge;
        private Screen _screen;
        private ClipEntry _copiedEntry;
        private ClipEntry _contextEntry;
        private string _suppressClipboardText;
        private string _statusText;
        private Color _statusColor;
        private string _pressedControl;
        private int _hoveredCard = -1;
        private int _pressedCard = -1;
        private int _pageOffset;
        private int _animationFrom;
        private int _animationTo;
        private int _animationDuration;
        private DateTime _animationStarted;
        private bool _expanded;
        private bool _dragging;
        private bool _minimizedToTray;
        private bool _exiting;
        private bool _clipboardListenerAttached;
        private bool _hotkeyAttached;
        private bool _historyDirty;
        private Point _dragOffset;
        private float _scale = 1f;
        private uint _dpi = 96;

        [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

        public ClipboardDockForm(EventWaitHandle showEvent) : this(showEvent, false) { }

        internal ClipboardDockForm(EventWaitHandle showEvent, bool previewMode)
        {
            _showEvent = showEvent;
            _previewMode = previewMode;
            _preferences = previewMode ? UserPreferences.CreateDefault() : LocalStore.LoadPreferences();
            _entries = previewMode ? CreatePreviewEntries() : LocalStore.LoadHistory();
            bool historyTrimmed = TrimHistoryToLimits();
            if (historyTrimmed && !previewMode) LocalStore.SaveHistory(_entries);
            _dockEdge = ParseDockEdge(_preferences.DockSide);
            _statusColor = Color.FromArgb(126, 119, 108);

            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(WindowWidth, WindowHeight);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = _preferences.AlwaysOnTop;
            BackColor = Color.FromArgb(248, 246, 241);
            DoubleBuffered = true;
            KeyPreview = true;
            Text = "PicoPaste";

            _autoHideTimer = new Timer();
            _autoHideTimer.Interval = 560;
            _autoHideTimer.Tick += delegate
            {
                _autoHideTimer.Stop();
                TryAutoHide();
            };

            _animationTimer = new Timer();
            _animationTimer.Interval = 15;
            _animationTimer.Tick += AnimationTimerTick;

            _feedbackTimer = new Timer();
            _feedbackTimer.Interval = 1100;
            _feedbackTimer.Tick += delegate
            {
                _feedbackTimer.Stop();
                _copiedEntry = null;
                _suppressClipboardText = null;
                _statusText = null;
                Invalidate();
            };

            _saveTimer = new Timer();
            _saveTimer.Interval = 400;
            _saveTimer.Tick += delegate
            {
                _saveTimer.Stop();
                FlushHistorySave();
            };

            if (!previewMode)
            {
                _trayMenu = BuildTrayMenu();
                _entryMenu = BuildEntryMenu();
                _trayIcon = new NotifyIcon();
                _trayIcon.Icon = CreateTrayIcon();
                _trayIcon.Text = "PicoPaste · 移到屏幕边缘呼出";
                _trayIcon.ContextMenuStrip = _trayMenu;
                _trayIcon.Visible = true;
                _trayIcon.DoubleClick += delegate { ExpandOnCursorScreen(true); };
            }

            MouseMove += HandleMouseMove;
            MouseEnter += delegate
            {
                CancelAutoHide();
                if (!_expanded) ExpandOnCursorScreen(false);
            };
            MouseDown += HandleMouseDown;
            MouseUp += HandleMouseUp;
            MouseWheel += HandleMouseWheel;
            MouseLeave += delegate
            {
                if (!_dragging) { _hoveredCard = -1; Invalidate(); }
                BeginAutoHide();
            };
            KeyDown += HandleKeyDown;
            SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WsExToolWindow = 0x00000080;
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WsExToolWindow;
                return cp;
            }
        }

        private static List<ClipEntry> CreatePreviewEntries()
        {
            List<ClipEntry> entries = new List<ClipEntry>();
            entries.Add(new ClipEntry { Text = "拖动顶部，可以把窗口移到任意位置。", CreatedAt = DateTime.Now });
            entries.Add(new ClipEntry { Text = "靠近左右屏幕边缘时会自动吸附。", CreatedAt = DateTime.Now.AddMinutes(-3) });
            entries.Add(new ClipEntry { Text = "滚轮可以继续浏览更早的复制记录。", CreatedAt = DateTime.Now.AddMinutes(-18) });
            entries.Add(new ClipEntry { Text = "移动鼠标到屏幕边缘即可自然滑出。", CreatedAt = DateTime.Now.AddHours(-2) });
            entries.Add(new ClipEntry { Text = "右键卡片可以置顶或者删除这一条。", CreatedAt = DateTime.Now.AddHours(-5) });
            entries.Add(new ClipEntry { Text = "Shift + P 也可以快速呼出或收起。", CreatedAt = DateTime.Now.AddDays(-1) });
            return entries;
        }

        internal void RenderPreview(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            _expanded = true;
            _dockEdge = DockEdge.Right;
            _dpi = 96;
            _scale = 1f;
            ClientSize = new Size(WindowWidth, WindowHeight);
            using (Bitmap bitmap = new Bitmap(WindowWidth, WindowHeight))
            {
                bitmap.SetResolution(96, 96);
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(BackColor);
                    OnPaint(new PaintEventArgs(graphics, new Rectangle(0, 0, WindowWidth, WindowHeight)));
                }
                bitmap.Save(fullPath, ImageFormat.Png);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpiScale();
            if (_previewMode) return;
            _clipboardListenerAttached = AddClipboardFormatListener(Handle);
            _hotkeyAttached = RegisterHotKey(Handle, HotkeyId, ModShift | ModNoRepeat, (uint)Keys.P);
            if (!_hotkeyAttached) SetStatus("Shift+P 已被其他程序占用", Color.FromArgb(176, 92, 68));
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (_clipboardListenerAttached) RemoveClipboardFormatListener(Handle);
            if (_hotkeyAttached) UnregisterHotKey(Handle, HotkeyId);
            _clipboardListenerAttached = false;
            _hotkeyAttached = false;
            base.OnHandleDestroyed(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _screen = Screen.PrimaryScreen;
            PlaceAtSavedPosition(false);
            UpdateOpacity();
            UpdateWindowRegion();
            RefreshMenuChecks();

            if (_showEvent != null)
            {
                _showEventRegistration = ThreadPool.RegisterWaitForSingleObject(
                    _showEvent,
                    delegate
                    {
                        if (!_exiting && IsHandleCreated)
                            BeginInvoke((MethodInvoker)delegate { ExpandOnCursorScreen(true); });
                    },
                    null,
                    Timeout.Infinite,
                    false);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateWindowRegion();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmDpiChanged)
            {
                base.WndProc(ref m);
                _dpi = (uint)(m.WParam.ToInt32() & 0xFFFF);
                ApplyDpiScale();
                if (!_dragging) PlaceAtSavedPosition(_expanded);
                return;
            }

            if (m.Msg == WmClipboardUpdate)
                BeginInvoke((MethodInvoker)delegate { CaptureClipboard(0); });
            else if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
                ToggleFromHotkey();

            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.ScaleTransform(_scale, _scale);

            Color surface = Color.FromArgb(248, 246, 241);
            Color ink = Color.FromArgb(45, 42, 38);
            Color muted = Color.FromArgb(126, 119, 108);
            Color border = Color.FromArgb(228, 222, 212);
            Color accent = Color.FromArgb(201, 105, 76);

            using (SolidBrush background = new SolidBrush(surface))
                g.FillRectangle(background, 0, 0, WindowWidth, WindowHeight);

            DrawDockHandle(g, accent);
            DrawHeader(g, ink, muted, accent);

            int cardY = 60;
            for (int slot = 0; slot < VisibleCards; slot++)
            {
                _cardBounds[slot] = new Rectangle(14, cardY + slot * 64, 272, 57);
                int entryIndex = _pageOffset + slot;
                if (entryIndex < _entries.Count)
                    DrawEntryCard(g, slot, _entries[entryIndex], _cardBounds[slot], ink, muted, border, accent);
                else
                    DrawEmptyCard(g, _cardBounds[slot], border, muted);
            }

            DrawFooter(g, muted, border, accent);
        }

        private void DrawDockHandle(Graphics g, Color accent)
        {
            if (_dockEdge == DockEdge.Floating) return;

            int railX = _dockEdge == DockEdge.Left ? 0 : WindowWidth - VisibleHandle;
            using (SolidBrush accentBrush = new SolidBrush(accent))
                g.FillRectangle(accentBrush, railX, 0, VisibleHandle, WindowHeight);

            int cx = railX + VisibleHandle / 2;
            int cy = WindowHeight / 2;
            bool pointLeft = (_dockEdge == DockEdge.Right && !_expanded) ||
                (_dockEdge == DockEdge.Left && _expanded);
            int direction = pointLeft ? -1 : 1;
            using (Pen pen = new Pen(Color.FromArgb(250, 239, 233), 1.5f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, cx + direction * 2, cy - 4, cx - direction * 2, cy);
                g.DrawLine(pen, cx - direction * 2, cy, cx + direction * 2, cy + 4);
            }
        }

        private void DrawHeader(Graphics g, Color ink, Color muted, Color accent)
        {
            _headerDragBounds = new Rectangle(8, 0, 196, 57);
            _pinBounds = new Rectangle(207, 15, 23, 23);
            _minimizeBounds = new Rectangle(236, 15, 23, 23);
            _closeBounds = new Rectangle(265, 15, 23, 23);

            using (SolidBrush logoBrush = new SolidBrush(Color.FromArgb(240, 225, 215)))
                g.FillEllipse(logoBrush, 16, 13, 32, 32);
            using (Pen logoPen = new Pen(accent, 1.5f))
            {
                DrawRoundedRectangle(g, logoPen, new Rectangle(24, 19, 13, 15), 3);
                DrawRoundedRectangle(g, logoPen, new Rectangle(28, 23, 13, 15), 3);
            }

            using (Font titleFont = ScaledFont("Microsoft YaHei UI", 10.8f, FontStyle.Bold))
            using (Font metaFont = ScaledFont("Microsoft YaHei UI", 7.6f, FontStyle.Regular))
            using (SolidBrush inkBrush = new SolidBrush(ink))
            using (SolidBrush mutedBrush = new SolidBrush(muted))
            {
                g.DrawString("PicoPaste", titleFont, inkBrush, 57, 10);
                string subtitle;
                if (!string.IsNullOrEmpty(_statusText)) subtitle = _statusText;
                else if (_entries.Count == 0) subtitle = "等待复制内容";
                else subtitle = "共 " + _entries.Count + " 条 · 滚轮浏览";
                using (SolidBrush statusBrush = new SolidBrush(string.IsNullOrEmpty(_statusText) ? muted : _statusColor))
                    g.DrawString(subtitle, metaFont, statusBrush, 58, 31);
            }

            DrawHeaderButton(g, _pinBounds, "pin", _preferences.Pinned, muted, accent);
            DrawHeaderButton(g, _minimizeBounds, "minimize", false, muted, accent);
            DrawHeaderButton(g, _closeBounds, "close", false, muted, accent);
        }

        private void DrawHeaderButton(Graphics g, Rectangle bounds, string kind, bool active, Color muted, Color accent)
        {
            bool hovered = _pressedControl == kind || IsMouseOver(bounds);
            if (hovered || active)
            {
                using (GraphicsPath path = RoundedRectangle(bounds, 7))
                using (SolidBrush brush = new SolidBrush(active ? Color.FromArgb(240, 222, 212) : Color.FromArgb(239, 235, 229)))
                    g.FillPath(brush, path);
            }

            Color lineColor = active ? accent : muted;
            using (Pen pen = new Pen(lineColor, 1.35f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                int cx = bounds.X + bounds.Width / 2;
                int cy = bounds.Y + bounds.Height / 2;
                if (kind == "close")
                {
                    g.DrawLine(pen, cx - 4, cy - 4, cx + 4, cy + 4);
                    g.DrawLine(pen, cx + 4, cy - 4, cx - 4, cy + 4);
                }
                else if (kind == "minimize")
                {
                    g.DrawLine(pen, cx - 5, cy + 3, cx + 5, cy + 3);
                }
                else
                {
                    g.DrawLine(pen, cx - 4, cy - 5, cx + 4, cy - 5);
                    g.DrawLine(pen, cx - 3, cy - 4, cx - 2, cy + 1);
                    g.DrawLine(pen, cx + 3, cy - 4, cx + 2, cy + 1);
                    g.DrawLine(pen, cx - 4, cy + 1, cx + 4, cy + 1);
                    g.DrawLine(pen, cx, cy + 1, cx, cy + 6);
                }
            }
        }

        private void DrawEntryCard(Graphics g, int slot, ClipEntry entry, Rectangle bounds,
            Color ink, Color muted, Color border, Color accent)
        {
            bool hovered = slot == _hoveredCard;
            bool pressed = slot == _pressedCard;
            bool copied = object.ReferenceEquals(entry, _copiedEntry);
            Color fill = copied ? Color.FromArgb(235, 245, 233) :
                (pressed ? Color.FromArgb(247, 235, 225) :
                (hovered ? Color.FromArgb(255, 247, 240) : Color.FromArgb(255, 253, 249)));
            Color line = copied ? Color.FromArgb(155, 190, 148) :
                (pressed ? Color.FromArgb(207, 128, 92) :
                (hovered ? Color.FromArgb(228, 171, 143) : border));

            using (GraphicsPath path = RoundedRectangle(bounds, 10))
            using (SolidBrush brush = new SolidBrush(fill))
            using (Pen pen = new Pen(line))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            Rectangle badge = new Rectangle(bounds.X + 11, bounds.Y + 10, 24, 24);
            using (SolidBrush badgeBrush = new SolidBrush(copied ? Color.FromArgb(210, 232, 205) : Color.FromArgb(244, 235, 227)))
                g.FillEllipse(badgeBrush, badge);
            using (Font badgeFont = ScaledFont("Segoe UI", 7.8f, FontStyle.Bold))
            using (SolidBrush badgeTextBrush = new SolidBrush(copied ? Color.FromArgb(75, 123, 71) : accent))
            {
                string badgeText = copied ? "✓" : (slot + 1).ToString();
                SizeF size = g.MeasureString(badgeText, badgeFont);
                g.DrawString(badgeText, badgeFont, badgeTextBrush,
                    badge.X + (badge.Width - size.Width) / 2f,
                    badge.Y + (badge.Height - size.Height) / 2f - 1);
            }

            Rectangle textArea = new Rectangle(bounds.X + 45, bounds.Y + 8, 207, 20);
            using (Font bodyFont = ScaledFont("Microsoft YaHei UI", 8.6f, FontStyle.Regular))
            using (SolidBrush bodyBrush = new SolidBrush(ink))
            using (StringFormat format = new StringFormat())
            {
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(CompactPreview(entry.Text), bodyFont, bodyBrush, textArea, format);
            }

            string note = copied ? "已复制" : RelativeTime(entry.CreatedAt);
            using (Font noteFont = ScaledFont("Microsoft YaHei UI", 7.2f, FontStyle.Regular))
            using (SolidBrush noteBrush = new SolidBrush(copied ? Color.FromArgb(75, 123, 71) : muted))
                g.DrawString(note, noteFont, noteBrush, bounds.X + 46, bounds.Y + 33);

            if (hovered)
            {
                using (Font hintFont = ScaledFont("Microsoft YaHei UI", 6.8f, FontStyle.Regular))
                using (SolidBrush hintBrush = new SolidBrush(Color.FromArgb(155, muted)))
                    g.DrawString("右键更多", hintFont, hintBrush, bounds.Right - 57, bounds.Y + 34);
            }
        }

        private void DrawEmptyCard(Graphics g, Rectangle bounds, Color border, Color muted)
        {
            using (GraphicsPath path = RoundedRectangle(bounds, 10))
            using (Pen pen = new Pen(Color.FromArgb(180, border)))
            {
                pen.DashStyle = DashStyle.Dash;
                g.DrawPath(pen, path);
            }
            using (Font font = ScaledFont("Microsoft YaHei UI", 7.8f, FontStyle.Regular))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(135, muted)))
                g.DrawString("复制文字后会出现在这里", font, brush, bounds.X + 44, bounds.Y + 19);
            using (Pen circlePen = new Pen(Color.FromArgb(165, border), 1.1f))
                g.DrawEllipse(circlePen, bounds.X + 11, bounds.Y + 16, 23, 23);
        }

        private void DrawFooter(Graphics g, Color muted, Color border, Color accent)
        {
            using (Pen divider = new Pen(border)) g.DrawLine(divider, 14, 319, 286, 319);

            _previousBounds = new Rectangle(14, 328, 24, 25);
            _nextBounds = new Rectangle(112, 328, 24, 25);
            _clearBounds = new Rectangle(248, 328, 38, 25);
            DrawSmallButton(g, _previousBounds, "‹", CanGoPrevious());
            DrawSmallButton(g, _nextBounds, "›", CanGoNext());
            DrawSmallButton(g, _clearBounds, "清空", _entries.Count > 0);

            string range = _entries.Count == 0 ? "0 / 0" :
                (_pageOffset + 1) + "-" + Math.Min(_pageOffset + VisibleCards, _entries.Count) + " / " + _entries.Count;
            using (Font rangeFont = ScaledFont("Segoe UI", 7.5f, FontStyle.Regular))
            using (SolidBrush rangeBrush = new SolidBrush(muted))
            using (StringFormat center = new StringFormat())
            {
                center.Alignment = StringAlignment.Center;
                center.LineAlignment = StringAlignment.Center;
                g.DrawString(range, rangeFont, rangeBrush, new Rectangle(40, 328, 70, 25), center);
            }

            using (Font hintFont = ScaledFont("Microsoft YaHei UI", 7.1f, FontStyle.Regular))
            using (SolidBrush hintBrush = new SolidBrush(muted))
                g.DrawString("鼠标悬停 · 点击复制", hintFont, hintBrush, 145, 334);
            using (SolidBrush dot = new SolidBrush(accent)) g.FillEllipse(dot, 137, 339, 3, 3);
        }

        private void DrawSmallButton(Graphics g, Rectangle bounds, string text, bool enabled)
        {
            bool hovered = enabled && IsMouseOver(bounds);
            using (GraphicsPath path = RoundedRectangle(bounds, 7))
            using (SolidBrush brush = new SolidBrush(hovered ? Color.FromArgb(239, 232, 225) : Color.FromArgb(241, 237, 231)))
                g.FillPath(brush, path);
            Color textColor = enabled ? Color.FromArgb(104, 96, 87) : Color.FromArgb(185, 180, 172);
            using (Font font = ScaledFont("Microsoft YaHei UI", text.Length == 1 ? 10f : 7.4f, FontStyle.Regular))
            using (SolidBrush brush = new SolidBrush(textColor))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                g.DrawString(text, font, brush, bounds, format);
            }
        }

        private bool IsMouseOver(Rectangle logicalBounds)
        {
            if (!Visible || !_expanded) return false;
            Point client = PointToClient(Cursor.Position);
            return logicalBounds.Contains(LogicalPoint(client));
        }

        private Font ScaledFont(string family, float pointSize, FontStyle style)
        {
            return new Font(family, pointSize / Math.Max(1f, _scale), style);
        }

        private static void DrawRoundedRectangle(Graphics g, Pen pen, Rectangle bounds, int radius)
        {
            using (GraphicsPath path = RoundedRectangle(bounds, radius)) g.DrawPath(pen, path);
        }

        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private ContextMenuStrip BuildTrayMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = new Font("Microsoft YaHei UI", 9f);
            menu.Items.Add("展开剪贴板", null, delegate { ExpandOnCursorScreen(true); });
            menu.Items.Add("最小化到托盘", null, delegate { MinimizeToTray(); });
            menu.Items.Add(new ToolStripSeparator());

            _pinMenuItem = new ToolStripMenuItem("锁定展开");
            _pinMenuItem.Click += delegate { TogglePinned(); };
            menu.Items.Add(_pinMenuItem);

            _performanceMenuItem = new ToolStripMenuItem("极简性能模式");
            _performanceMenuItem.Click += delegate { TogglePerformanceMode(); };
            menu.Items.Add(_performanceMenuItem);

            _translucentMenuItem = new ToolStripMenuItem("半透明");
            _translucentMenuItem.Click += delegate { ToggleTranslucency(); };
            menu.Items.Add(_translucentMenuItem);

            _topmostMenuItem = new ToolStripMenuItem("始终置顶");
            _topmostMenuItem.Click += delegate { ToggleTopMost(); };
            menu.Items.Add(_topmostMenuItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("清空记录", null, delegate { ClearHistory(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出 PicoPaste", null, delegate { ExitApplication(); });
            menu.Opening += delegate { RefreshMenuChecks(); };
            menu.Closed += delegate { BeginAutoHide(); };
            return menu;
        }

        private ContextMenuStrip BuildEntryMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = new Font("Microsoft YaHei UI", 9f);
            menu.Items.Add("复制", null, delegate { CopyContextEntry(); });
            menu.Items.Add("移到最前", null, delegate { MoveContextEntryToFront(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("删除这条", null, delegate { DeleteContextEntry(); });
            menu.Closed += delegate { BeginAutoHide(); };
            return menu;
        }

        private Icon CreateTrayIcon()
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(201, 105, 76)))
                    g.FillEllipse(brush, 1, 1, 30, 30);
                using (Pen pen = new Pen(Color.White, 2f))
                {
                    DrawRoundedRectangle(g, pen, new Rectangle(8, 7, 12, 15), 3);
                    DrawRoundedRectangle(g, pen, new Rectangle(12, 11, 12, 15), 3);
                }
                IntPtr handle = bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        private void HandleMouseMove(object sender, MouseEventArgs e)
        {
            CancelAutoHide();
            if (_dragging)
            {
                Point cursor = Cursor.Position;
                Location = new Point(cursor.X - _dragOffset.X, cursor.Y - _dragOffset.Y);
                _screen = Screen.FromPoint(cursor);
                return;
            }

            if (!_expanded)
            {
                ExpandOnCursorScreen(false);
                return;
            }

            Point logical = LogicalPoint(e.Location);
            int newHover = FindVisibleSlot(logical);
            if (newHover != _hoveredCard)
            {
                _hoveredCard = newHover;
                Invalidate();
            }

            bool clickable = newHover >= 0 || HitControl(logical) != null;
            Cursor = _headerDragBounds.Contains(logical) ? Cursors.SizeAll : (clickable ? Cursors.Hand : Cursors.Default);
        }

        private void HandleMouseDown(object sender, MouseEventArgs e)
        {
            Point logical = LogicalPoint(e.Location);
            if (e.Button == MouseButtons.Right)
            {
                int slot = FindVisibleSlot(logical);
                if (slot >= 0) ShowEntryMenu(_pageOffset + slot, e.Location);
                return;
            }

            if (e.Button != MouseButtons.Left) return;
            _pressedControl = HitControl(logical);
            _pressedCard = FindVisibleSlot(logical);

            if (_pressedControl == null && _pressedCard < 0 && _headerDragBounds.Contains(logical))
            {
                _animationTimer.Stop();
                _dragging = true;
                _dragOffset = e.Location;
                _dockEdge = DockEdge.Floating;
                _expanded = true;
                Capture = true;
                UpdateOpacity();
            }
            Invalidate();
        }

        private void HandleMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            if (_dragging)
            {
                _dragging = false;
                Capture = false;
                FinishDrag();
                return;
            }

            Point logical = LogicalPoint(e.Location);
            string releasedControl = HitControl(logical);
            string pressedControl = _pressedControl;
            int pressedCard = _pressedCard;
            _pressedControl = null;
            _pressedCard = -1;

            if (pressedControl != null && pressedControl == releasedControl)
                RunControlAction(pressedControl);
            else if (pressedCard >= 0 && pressedCard == FindVisibleSlot(logical))
                CopyEntryAt(_pageOffset + pressedCard, 0);
            Invalidate();
        }

        private void HandleMouseWheel(object sender, MouseEventArgs e)
        {
            if (e.Delta < 0) NextPage();
            else if (e.Delta > 0) PreviousPage();
        }

        private void HandleKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                if (_dockEdge == DockEdge.Floating) MinimizeToTray();
                else Collapse();
                e.Handled = true;
                return;
            }
            if (e.KeyCode == Keys.PageDown || e.KeyCode == Keys.Down)
            {
                NextPage();
                e.Handled = true;
                return;
            }
            if (e.KeyCode == Keys.PageUp || e.KeyCode == Keys.Up)
            {
                PreviousPage();
                e.Handled = true;
                return;
            }

        }

        private string HitControl(Point logical)
        {
            if (_pinBounds.Contains(logical)) return "pin";
            if (_minimizeBounds.Contains(logical)) return "minimize";
            if (_closeBounds.Contains(logical)) return "close";
            if (_previousBounds.Contains(logical) && CanGoPrevious()) return "previous";
            if (_nextBounds.Contains(logical) && CanGoNext()) return "next";
            if (_clearBounds.Contains(logical) && _entries.Count > 0) return "clear";
            return null;
        }

        private void RunControlAction(string action)
        {
            if (action == "pin") TogglePinned();
            else if (action == "minimize") MinimizeToTray();
            else if (action == "close") ExitApplication();
            else if (action == "previous") PreviousPage();
            else if (action == "next") NextPage();
            else if (action == "clear") ClearHistory();
        }

        private int FindVisibleSlot(Point logical)
        {
            for (int slot = 0; slot < VisibleCards; slot++)
            {
                if (_pageOffset + slot < _entries.Count && _cardBounds[slot].Contains(logical))
                    return slot;
            }
            return -1;
        }

        private void ShowEntryMenu(int index, Point clientLocation)
        {
            if (_entryMenu == null || index < 0 || index >= _entries.Count) return;
            _contextEntry = _entries[index];
            _entryMenu.Show(this, clientLocation);
        }

        private void CopyContextEntry()
        {
            int index = _entries.IndexOf(_contextEntry);
            if (index >= 0) CopyEntryAt(index, 0);
        }

        private void MoveContextEntryToFront()
        {
            int index = _entries.IndexOf(_contextEntry);
            if (index <= 0) return;
            ClipEntry entry = _entries[index];
            _entries.RemoveAt(index);
            _entries.Insert(0, entry);
            _pageOffset = 0;
            ScheduleHistorySave();
            SetStatus("已移到最前", Color.FromArgb(88, 122, 80));
            Invalidate();
        }

        private void DeleteContextEntry()
        {
            int index = _entries.IndexOf(_contextEntry);
            if (index < 0) return;
            if (object.ReferenceEquals(_copiedEntry, _contextEntry)) _copiedEntry = null;
            _entries.RemoveAt(index);
            ClampPageOffset();
            ScheduleHistorySave();
            SetStatus("已删除", Color.FromArgb(126, 119, 108));
            Invalidate();
        }

        private void BeginAutoHide()
        {
            bool menuOpen = (_trayMenu != null && _trayMenu.Visible) || (_entryMenu != null && _entryMenu.Visible);
            if (!_expanded || _dragging || menuOpen || _minimizedToTray ||
                _dockEdge == DockEdge.Floating || _preferences.Pinned) return;
            _autoHideTimer.Stop();
            _autoHideTimer.Start();
        }

        private void CancelAutoHide()
        {
            _autoHideTimer.Stop();
        }

        private void TryAutoHide()
        {
            bool menuOpen = (_trayMenu != null && _trayMenu.Visible) || (_entryMenu != null && _entryMenu.Visible);
            if (!_expanded || _dragging || menuOpen || _minimizedToTray ||
                _dockEdge == DockEdge.Floating || _preferences.Pinned) return;
            Rectangle safeArea = Bounds;
            safeArea.Inflate(7, 7);
            if (!safeArea.Contains(Cursor.Position)) Collapse();
        }

        private void ToggleFromHotkey()
        {
            if (_minimizedToTray || !Visible || !_expanded) ExpandOnCursorScreen(true);
            else if (_dockEdge == DockEdge.Floating) MinimizeToTray();
            else Collapse();
        }

        private void ExpandOnCursorScreen(bool activate)
        {
            CancelAutoHide();
            Screen target = Screen.FromPoint(Cursor.Position);
            bool changedScreen = _screen == null || _screen.DeviceName != target.DeviceName;
            if (_dockEdge != DockEdge.Floating) _screen = target;
            if (_dockEdge != DockEdge.Floating && changedScreen)
                _dockEdge = ParseDockEdge(_preferences.DockSide);

            if (!Visible)
            {
                Show();
                _minimizedToTray = false;
            }

            if (_trayIcon != null) _trayIcon.Text = "PicoPaste · 移到屏幕边缘呼出";

            if (_dockEdge == DockEdge.Floating)
            {
                _expanded = true;
                UpdateOpacity();
                if (activate) { Activate(); BringToFront(); }
                Invalidate();
                return;
            }

            if (changedScreen) PlaceAtSavedPosition(false);
            else SetVerticalPositionFromRatio();

            _expanded = true;
            AnimateTo(ExpandedLeft());
            UpdateOpacity();
            if (activate) { Activate(); BringToFront(); }
            Invalidate();
        }

        private void Collapse()
        {
            CancelAutoHide();
            if (_dockEdge == DockEdge.Floating)
            {
                MinimizeToTray();
                return;
            }
            if (!_expanded && !_animationTimer.Enabled) return;
            _expanded = false;
            _hoveredCard = -1;
            Cursor = Cursors.Default;
            AnimateTo(CollapsedLeft());
            UpdateOpacity();
            Invalidate();
        }

        private void MinimizeToTray()
        {
            CancelAutoHide();
            _animationTimer.Stop();
            _minimizedToTray = true;
            _expanded = false;
            Hide();
            if (_trayIcon != null) _trayIcon.Text = "PicoPaste · 已最小化 · Shift+P 呼出";
        }

        private void FinishDrag()
        {
            _screen = Screen.FromPoint(Cursor.Position);
            Rectangle area = _screen.WorkingArea;
            int leftDistance = Math.Abs(Left - area.Left);
            int rightDistance = Math.Abs((Left + Width) - area.Right);
            int threshold = ScalePixels(SnapDistance);

            if (leftDistance <= threshold || rightDistance <= threshold)
            {
                _dockEdge = leftDistance <= rightDistance ? DockEdge.Left : DockEdge.Right;
                _preferences.DockSide = _dockEdge.ToString();
                SaveTopRatio();
                LocalStore.SavePreferences(_preferences);
                _expanded = true;
                AnimateTo(ExpandedLeft());
                SetStatus(_dockEdge == DockEdge.Left ? "已吸附左侧" : "已吸附右侧", Color.FromArgb(88, 122, 80));
            }
            else
            {
                _dockEdge = DockEdge.Floating;
                KeepInsideWorkingArea();
                SetStatus("自由窗口 · 拖到边缘可吸附", Color.FromArgb(126, 119, 108));
            }
            UpdateOpacity();
            Invalidate();
        }

        private void PlaceAtSavedPosition(bool expanded)
        {
            if (_screen == null) _screen = Screen.PrimaryScreen;
            if (_dockEdge == DockEdge.Floating) _dockEdge = ParseDockEdge(_preferences.DockSide);
            SetVerticalPositionFromRatio();
            Left = expanded ? ExpandedLeft() : CollapsedLeft();
            _expanded = expanded;
        }

        private void SetVerticalPositionFromRatio()
        {
            if (_screen == null) _screen = Screen.PrimaryScreen;
            Rectangle area = _screen.WorkingArea;
            int available = Math.Max(0, area.Height - Height - ScalePixels(16));
            int top = area.Top + ScalePixels(8) + (int)Math.Round(available * _preferences.TopRatio);
            Top = Math.Max(area.Top + ScalePixels(4), Math.Min(top, area.Bottom - Height - ScalePixels(4)));
        }

        private int ExpandedLeft()
        {
            Rectangle area = (_screen ?? Screen.PrimaryScreen).WorkingArea;
            return _dockEdge == DockEdge.Left ? area.Left : area.Right - Width;
        }

        private int CollapsedLeft()
        {
            Rectangle area = (_screen ?? Screen.PrimaryScreen).WorkingArea;
            int handle = ScalePixels(VisibleHandle);
            return _dockEdge == DockEdge.Left ? area.Left - Width + handle : area.Right - handle;
        }

        private void SaveTopRatio()
        {
            if (_screen == null) return;
            Rectangle area = _screen.WorkingArea;
            int available = Math.Max(1, area.Height - Height - ScalePixels(16));
            _preferences.TopRatio = Math.Max(0, Math.Min(1,
                (double)(Top - area.Top - ScalePixels(8)) / available));
        }

        private void KeepInsideWorkingArea()
        {
            if (_screen == null) _screen = Screen.FromControl(this);
            Rectangle area = _screen.WorkingArea;
            int margin = ScalePixels(6);
            Left = Math.Max(area.Left + margin, Math.Min(Left, area.Right - Width - margin));
            Top = Math.Max(area.Top + margin, Math.Min(Top, area.Bottom - Height - margin));
        }

        private void ApplyDpiScale()
        {
            if (IsHandleCreated)
            {
                try
                {
                    uint value = GetDpiForWindow(Handle);
                    if (value >= 96 && value <= 768) _dpi = value;
                }
                catch (EntryPointNotFoundException) { _dpi = 96; }
            }
            _scale = _dpi / 96f;
            ClientSize = new Size(ScalePixels(WindowWidth), ScalePixels(WindowHeight));
            UpdateWindowRegion();
            Invalidate();
        }

        private void UpdateWindowRegion()
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            int radius = Math.Max(8, ScalePixels(12));
            Rectangle bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            using (GraphicsPath path = RoundedRectangle(bounds, radius))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        private int ScalePixels(int value) { return (int)Math.Round(value * _scale); }

        private Point LogicalPoint(Point value)
        {
            return new Point((int)Math.Round(value.X / _scale), (int)Math.Round(value.Y / _scale));
        }

        private void AnimateTo(int destinationLeft)
        {
            if (Left == destinationLeft)
            {
                _animationTimer.Stop();
                UpdateOpacity();
                return;
            }
            _animationFrom = Left;
            _animationTo = destinationLeft;
            _animationDuration = _preferences.PerformanceMode ? PerformanceAnimationDuration : SmoothAnimationDuration;
            _animationStarted = DateTime.UtcNow;
            _animationTimer.Start();
        }

        private void AnimationTimerTick(object sender, EventArgs e)
        {
            double t = (DateTime.UtcNow - _animationStarted).TotalMilliseconds / _animationDuration;
            if (t >= 1.0)
            {
                Left = _animationTo;
                _animationTimer.Stop();
                UpdateOpacity();
                return;
            }
            double eased = 1.0 - Math.Pow(1.0 - t, 3.0);
            Left = _animationFrom + (int)Math.Round((_animationTo - _animationFrom) * eased);
        }

        private void UpdateOpacity()
        {
            if (_previewMode) return;
            if (!_preferences.Translucent) Opacity = 1.0;
            else if (!_expanded) Opacity = 0.86;
            else if (_dockEdge == DockEdge.Floating) Opacity = 0.97;
            else Opacity = 0.95;
        }

        private void CaptureClipboard(int attempt)
        {
            try
            {
                if (!Clipboard.ContainsText(TextDataFormat.UnicodeText)) return;
                string text = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (string.IsNullOrWhiteSpace(text)) return;

                if (_suppressClipboardText != null && _suppressClipboardText == text)
                {
                    _suppressClipboardText = null;
                    return;
                }
                if (text.Length > MaxTextLength)
                {
                    SetStatus("内容过大，未加入历史", Color.FromArgb(176, 92, 68));
                    return;
                }
                if (_entries.Count > 0 && _entries[0].Text == text) return;

                _entries.RemoveAll(delegate(ClipEntry item) { return item.Text == text; });
                _entries.Insert(0, new ClipEntry { Text = text, CreatedAt = DateTime.Now });
                TrimHistoryToLimits();
                _pageOffset = 0;
                ScheduleHistorySave();
                Invalidate();
            }
            catch (ExternalException)
            {
                if (attempt < 3) RetryClipboard(delegate { CaptureClipboard(attempt + 1); });
                else SetStatus("剪贴板正忙，请稍后再试", Color.FromArgb(176, 92, 68));
            }
        }

        private void CopyEntryAt(int index, int attempt)
        {
            if (index < 0 || index >= _entries.Count) return;
            ClipEntry entry = _entries[index];
            try
            {
                _suppressClipboardText = entry.Text;
                Clipboard.SetText(entry.Text, TextDataFormat.UnicodeText);
                _copiedEntry = entry;
                SetStatus("已复制", Color.FromArgb(75, 123, 71));
            }
            catch (ExternalException)
            {
                _suppressClipboardText = null;
                if (attempt < 3) RetryClipboard(delegate { CopyEntryAt(index, attempt + 1); });
                else SetStatus("复制失败，请重试", Color.FromArgb(176, 92, 68));
            }
        }

        private void RetryClipboard(MethodInvoker action)
        {
            Timer retry = new Timer();
            retry.Interval = 80;
            retry.Tick += delegate { retry.Stop(); retry.Dispose(); action(); };
            retry.Start();
        }

        private void SetStatus(string message, Color color)
        {
            _statusText = message;
            _statusColor = color;
            _feedbackTimer.Stop();
            _feedbackTimer.Start();
            Invalidate();
        }

        private void ClearHistory()
        {
            _saveTimer.Stop();
            _historyDirty = false;
            _entries.Clear();
            _pageOffset = 0;
            _copiedEntry = null;
            _contextEntry = null;
            _suppressClipboardText = null;
            LocalStore.ClearHistory();
            SetStatus("记录已清空", Color.FromArgb(126, 119, 108));
        }

        private bool TrimHistoryToLimits()
        {
            bool changed = _entries.RemoveAll(delegate(ClipEntry entry)
            {
                return entry == null || string.IsNullOrEmpty(entry.Text) || entry.Text.Length > MaxTextLength;
            }) > 0;

            if (_entries.Count > MaxHistory)
            {
                _entries.RemoveRange(MaxHistory, _entries.Count - MaxHistory);
                changed = true;
            }

            long characters = 0;
            int keep = 0;
            while (keep < _entries.Count)
            {
                int length = _entries[keep].Text.Length;
                if (keep > 0 && characters + length > MaxHistoryCharacters) break;
                characters += length;
                keep++;
            }
            if (keep < _entries.Count)
            {
                _entries.RemoveRange(keep, _entries.Count - keep);
                changed = true;
            }
            return changed;
        }

        private void ScheduleHistorySave()
        {
            if (_previewMode) return;
            _historyDirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void FlushHistorySave()
        {
            if (!_historyDirty || _previewMode) return;
            LocalStore.SaveHistory(_entries);
            _historyDirty = false;
        }

        private bool CanGoPrevious() { return _pageOffset > 0; }
        private bool CanGoNext() { return _pageOffset + VisibleCards < _entries.Count; }

        private void PreviousPage()
        {
            if (!CanGoPrevious()) return;
            _pageOffset = Math.Max(0, _pageOffset - VisibleCards);
            _hoveredCard = -1;
            Invalidate();
        }

        private void NextPage()
        {
            if (!CanGoNext()) return;
            _pageOffset = Math.Min(MaxPageOffset(), _pageOffset + VisibleCards);
            _hoveredCard = -1;
            Invalidate();
        }

        private int MaxPageOffset()
        {
            if (_entries.Count <= VisibleCards) return 0;
            return ((_entries.Count - 1) / VisibleCards) * VisibleCards;
        }

        private void ClampPageOffset()
        {
            _pageOffset = Math.Max(0, Math.Min(_pageOffset, MaxPageOffset()));
        }

        private void TogglePinned()
        {
            _preferences.Pinned = !_preferences.Pinned;
            if (_preferences.Pinned && !_expanded) ExpandOnCursorScreen(false);
            if (_preferences.Pinned) CancelAutoHide();
            else BeginAutoHide();
            LocalStore.SavePreferences(_preferences);
            RefreshMenuChecks();
            SetStatus(_preferences.Pinned ? "已锁定展开" : "已恢复自动收起", Color.FromArgb(126, 119, 108));
        }

        private void ToggleTranslucency()
        {
            _preferences.Translucent = !_preferences.Translucent;
            LocalStore.SavePreferences(_preferences);
            RefreshMenuChecks();
            UpdateOpacity();
        }

        private void TogglePerformanceMode()
        {
            _preferences.PerformanceMode = !_preferences.PerformanceMode;
            LocalStore.SavePreferences(_preferences);
            RefreshMenuChecks();
            UpdateOpacity();
            SetStatus(_preferences.PerformanceMode ? "低功耗动效已开启" : "平滑动画已开启",
                Color.FromArgb(126, 119, 108));
        }

        private void ToggleTopMost()
        {
            _preferences.AlwaysOnTop = !_preferences.AlwaysOnTop;
            TopMost = _preferences.AlwaysOnTop;
            LocalStore.SavePreferences(_preferences);
            RefreshMenuChecks();
        }

        private void RefreshMenuChecks()
        {
            if (_pinMenuItem != null) _pinMenuItem.Checked = _preferences.Pinned;
            if (_performanceMenuItem != null) _performanceMenuItem.Checked = _preferences.PerformanceMode;
            if (_translucentMenuItem != null) _translucentMenuItem.Checked = _preferences.Translucent;
            if (_topmostMenuItem != null) _topmostMenuItem.Checked = _preferences.AlwaysOnTop;
        }

        private static string CompactPreview(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string result = text.Replace("\r\n", " · ").Replace("\n", " · ").Replace("\r", " · ").Replace("\t", " ");
            while (result.Contains("  ")) result = result.Replace("  ", " ");
            return result.Trim();
        }

        private static string RelativeTime(DateTime value)
        {
            TimeSpan age = DateTime.Now - value;
            if (age.TotalSeconds < 60) return "刚刚";
            if (age.TotalMinutes < 60) return ((int)age.TotalMinutes) + " 分钟前";
            if (age.TotalHours < 24) return ((int)age.TotalHours) + " 小时前";
            return value.ToString("M月d日 HH:mm");
        }

        private static DockEdge ParseDockEdge(string value)
        {
            return value == "Left" ? DockEdge.Left : DockEdge.Right;
        }

        private void DisplaySettingsChanged(object sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke((MethodInvoker)delegate { DisplaySettingsChanged(sender, e); });
                return;
            }
            _screen = Screen.FromPoint(Cursor.Position);
            if (_dockEdge == DockEdge.Floating) _dockEdge = ParseDockEdge(_preferences.DockSide);
            PlaceAtSavedPosition(_expanded);
        }

        private void ExitApplication()
        {
            _exiting = true;
            FlushHistorySave();
            LocalStore.SavePreferences(_preferences);
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                MinimizeToTray();
                return;
            }

            _exiting = true;
            _saveTimer.Stop();
            FlushHistorySave();
            if (_showEventRegistration != null) _showEventRegistration.Unregister(null);
            if (_trayIcon != null) _trayIcon.Visible = false;
            SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_trayIcon != null) _trayIcon.Dispose();
                if (_trayMenu != null) _trayMenu.Dispose();
                if (_entryMenu != null) _entryMenu.Dispose();
                if (_autoHideTimer != null) _autoHideTimer.Dispose();
                if (_animationTimer != null) _animationTimer.Dispose();
                if (_feedbackTimer != null) _feedbackTimer.Dispose();
                if (_saveTimer != null) _saveTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
