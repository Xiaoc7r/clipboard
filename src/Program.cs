using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace PicoPaste
{
    internal static class Program
    {
        private const string MutexName = "Local\\PicoPaste.SingleInstance";
        private const string ShowEventName = "Local\\PicoPaste.ShowWindow";

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 2 && string.Equals(args[0], "--preview", StringComparison.OrdinalIgnoreCase))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
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

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
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

    internal static class HistoryStore
    {
        private static readonly string AppDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PicoPaste");
        private static readonly string HistoryFile = Path.Combine(AppDirectory, "history.json");

        public static List<ClipEntry> Load()
        {
            try
            {
                if (!File.Exists(HistoryFile)) return new List<ClipEntry>();
                using (FileStream stream = File.OpenRead(HistoryFile))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(List<ClipEntry>));
                    List<ClipEntry> entries = serializer.ReadObject(stream) as List<ClipEntry>;
                    return entries ?? new List<ClipEntry>();
                }
            }
            catch { return new List<ClipEntry>(); }
        }

        public static void Save(List<ClipEntry> entries)
        {
            try
            {
                Directory.CreateDirectory(AppDirectory);
                string temp = HistoryFile + ".tmp";
                using (FileStream stream = File.Create(temp))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(List<ClipEntry>));
                    serializer.WriteObject(stream, entries);
                }
                if (File.Exists(HistoryFile)) File.Delete(HistoryFile);
                File.Move(temp, HistoryFile);
            }
            catch { }
        }

        public static void Clear()
        {
            try { if (File.Exists(HistoryFile)) File.Delete(HistoryFile); }
            catch { }
        }
    }

    internal sealed class ClipboardDockForm : Form
    {
        private const int WindowWidth = 336;
        private const int WindowHeight = 462;
        private const int VisibleHandle = 12;
        private const int MaxEntries = 4;
        private const int WmClipboardUpdate = 0x031D;
        private const int WmHotkey = 0x0312;
        private const int WmDpiChanged = 0x02E0;
        private const int HotkeyId = 0x5043;
        private const uint ModAlt = 0x0001;
        private const uint ModNoRepeat = 0x4000;

        private readonly List<ClipEntry> _entries;
        private readonly Rectangle[] _cardBounds = new Rectangle[MaxEntries];
        private readonly Timer _hoverTimer;
        private readonly Timer _animationTimer;
        private readonly Timer _feedbackTimer;
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _trayMenu;
        private readonly EventWaitHandle _showEvent;
        private RegisteredWaitHandle _showEventRegistration;
        private Rectangle _clearBounds;
        private int _hoveredCard = -1;
        private int _pressedCard = -1;
        private int _copiedCard = -1;
        private int _outsideTicks;
        private int _animationFrom;
        private int _animationTo;
        private DateTime _animationStarted;
        private bool _expanded;
        private bool _exiting;
        private bool _clipboardListenerAttached;
        private bool _hotkeyAttached;
        private Screen _screen;
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
            _entries = HistoryStore.Load();
            if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            if (previewMode)
            {
                _entries.Clear();
                _entries.Add(new ClipEntry { Text = "Claude 风格：温暖、克制，保留足够的呼吸感。", CreatedAt = DateTime.Now });
                _entries.Add(new ClipEntry { Text = "点击任意卡片，即可重新复制这段文字。", CreatedAt = DateTime.Now.AddMinutes(-3) });
                _entries.Add(new ClipEntry { Text = "窗口贴在屏幕右侧，鼠标靠近时自然滑出。", CreatedAt = DateTime.Now.AddMinutes(-18) });
                _entries.Add(new ClipEntry { Text = "Alt + V 可以在当前显示器快速呼出。", CreatedAt = DateTime.Now.AddHours(-2) });
            }

            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(WindowWidth, WindowHeight);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(248, 246, 241);
            DoubleBuffered = true;
            KeyPreview = true;
            Text = "PicoPaste";

            _hoverTimer = new Timer();
            _hoverTimer.Interval = 80;
            _hoverTimer.Tick += HoverTimerTick;

            _animationTimer = new Timer();
            _animationTimer.Interval = 15;
            _animationTimer.Tick += AnimationTimerTick;

            _feedbackTimer = new Timer();
            _feedbackTimer.Interval = 950;
            _feedbackTimer.Tick += delegate
            {
                _feedbackTimer.Stop();
                _copiedCard = -1;
                Invalidate();
            };

            if (!previewMode)
            {
                _trayMenu = BuildTrayMenu();
                _trayIcon = new NotifyIcon();
                _trayIcon.Icon = CreateTrayIcon();
                _trayIcon.Text = "PicoPaste · Alt+V 呼出";
                _trayIcon.ContextMenuStrip = _trayMenu;
                _trayIcon.Visible = true;
                _trayIcon.DoubleClick += delegate { ExpandOnCursorScreen(); };
            }

            MouseMove += HandleMouseMove;
            MouseDown += HandleMouseDown;
            MouseUp += HandleMouseUp;
            MouseLeave += delegate { _hoveredCard = -1; Invalidate(); };
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Collapse(); };
            SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        internal void RenderPreview(string path)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            _expanded = true;
            CreateControl();
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
                bitmap.Save(path, ImageFormat.Png);
            }
        }

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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpiScale();
            _clipboardListenerAttached = AddClipboardFormatListener(Handle);
            _hotkeyAttached = RegisterHotKey(Handle, HotkeyId, ModAlt | ModNoRepeat, (uint)Keys.V);
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
            PlaceWindow(false);
            _hoverTimer.Start();
            if (_showEvent != null)
            {
                _showEventRegistration = ThreadPool.RegisterWaitForSingleObject(
                    _showEvent,
                    delegate { if (!_exiting && IsHandleCreated) BeginInvoke((MethodInvoker)ExpandOnCursorScreen); },
                    null, Timeout.Infinite, false);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmDpiChanged)
            {
                base.WndProc(ref m);
                _dpi = (uint)(m.WParam.ToInt32() & 0xFFFF);
                ApplyDpiScale();
                return;
            }
            if (m.Msg == WmClipboardUpdate) BeginInvoke((MethodInvoker)delegate { CaptureClipboard(0); });
            else if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId) ExpandOnCursorScreen();
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
            Color border = Color.FromArgb(229, 223, 213);
            Color accent = Color.FromArgb(205, 112, 82);

            using (SolidBrush background = new SolidBrush(surface)) g.FillRectangle(background, 0, 0, WindowWidth, WindowHeight);
            using (Pen outer = new Pen(border)) g.DrawLine(outer, 0, 0, 0, WindowHeight);
            using (SolidBrush accentBrush = new SolidBrush(accent)) g.FillRectangle(accentBrush, WindowWidth - VisibleHandle, 0, VisibleHandle, WindowHeight);

            using (Pen arrowPen = new Pen(Color.FromArgb(250, 239, 233), 1.8f))
            {
                arrowPen.StartCap = LineCap.Round;
                arrowPen.EndCap = LineCap.Round;
                int cx = WindowWidth - 6;
                int cy = WindowHeight / 2;
                g.DrawLine(arrowPen, cx + (_expanded ? 2 : -2), cy - 5, cx + (_expanded ? -2 : 2), cy);
                g.DrawLine(arrowPen, cx + (_expanded ? -2 : 2), cy, cx + (_expanded ? 2 : -2), cy + 5);
            }

            using (SolidBrush logoBrush = new SolidBrush(Color.FromArgb(240, 225, 215))) g.FillEllipse(logoBrush, 20, 17, 38, 38);
            using (Pen logoPen = new Pen(accent, 1.7f))
            {
                DrawRoundedRectangle(g, logoPen, new Rectangle(30, 25, 15, 17), 3);
                DrawRoundedRectangle(g, logoPen, new Rectangle(35, 30, 15, 17), 3);
            }

            using (Font titleFont = ScaledFont("Microsoft YaHei UI", 12.5f, FontStyle.Bold))
            using (Font metaFont = ScaledFont("Microsoft YaHei UI", 8.5f, FontStyle.Regular))
            using (SolidBrush inkBrush = new SolidBrush(ink))
            using (SolidBrush mutedBrush = new SolidBrush(muted))
            {
                g.DrawString("PicoPaste", titleFont, inkBrush, 68, 16);
                string subtitle = _entries.Count == 0 ? "等待复制内容" : "最近 " + _entries.Count + " 条文本";
                g.DrawString(subtitle, metaFont, mutedBrush, 69, 39);
            }

            _clearBounds = new Rectangle(255, 22, 54, 29);
            DrawPillButton(g, _clearBounds, "清空");
            int cardY = 74;
            for (int i = 0; i < MaxEntries; i++)
            {
                _cardBounds[i] = new Rectangle(18, cardY + i * 79, 291, 68);
                if (i < _entries.Count) DrawEntryCard(g, i, _cardBounds[i], ink, muted, border, accent);
                else DrawEmptyCard(g, _cardBounds[i], border, muted);
            }

            using (Pen divider = new Pen(border)) g.DrawLine(divider, 18, 397, 309, 397);
            using (Font footerFont = ScaledFont("Microsoft YaHei UI", 8.5f, FontStyle.Regular))
            using (SolidBrush mutedBrush = new SolidBrush(muted))
            {
                g.DrawString("Alt + V  呼出", footerFont, mutedBrush, 19, 414);
                g.DrawString("离开后自动收起", footerFont, mutedBrush, 135, 414);
            }
            using (SolidBrush dot = new SolidBrush(accent)) g.FillEllipse(dot, 121, 420, 4, 4);
        }

        private void DrawEntryCard(Graphics g, int index, Rectangle bounds, Color ink, Color muted, Color border, Color accent)
        {
            bool hovered = index == _hoveredCard;
            bool copied = index == _copiedCard;
            Color fill = copied ? Color.FromArgb(235, 245, 233) : (hovered ? Color.FromArgb(255, 247, 240) : Color.FromArgb(255, 253, 249));
            Color line = copied ? Color.FromArgb(162, 195, 155) : (hovered ? Color.FromArgb(230, 178, 151) : border);
            using (GraphicsPath path = RoundedRectangle(bounds, 12))
            using (SolidBrush brush = new SolidBrush(fill))
            using (Pen pen = new Pen(line)) { g.FillPath(brush, path); g.DrawPath(pen, path); }

            Rectangle badge = new Rectangle(bounds.X + 13, bounds.Y + 12, 27, 27);
            using (SolidBrush badgeBrush = new SolidBrush(copied ? Color.FromArgb(210, 232, 205) : Color.FromArgb(244, 235, 227))) g.FillEllipse(badgeBrush, badge);
            using (Font indexFont = ScaledFont("Segoe UI", 8.5f, FontStyle.Bold))
            using (SolidBrush accentBrush = new SolidBrush(copied ? Color.FromArgb(75, 123, 71) : accent))
            {
                string badgeText = copied ? "✓" : (index + 1).ToString();
                SizeF size = g.MeasureString(badgeText, indexFont);
                g.DrawString(badgeText, indexFont, accentBrush, badge.X + (badge.Width - size.Width) / 2f, badge.Y + (badge.Height - size.Height) / 2f - 1);
            }

            Rectangle textArea = new Rectangle(bounds.X + 50, bounds.Y + 10, 217, 39);
            using (Font bodyFont = ScaledFont("Microsoft YaHei UI", 9.2f, FontStyle.Regular))
            using (SolidBrush bodyBrush = new SolidBrush(ink))
            using (StringFormat bodyFormat = new StringFormat())
            {
                bodyFormat.Trimming = StringTrimming.EllipsisCharacter;
                bodyFormat.FormatFlags = StringFormatFlags.LineLimit;
                g.DrawString(CompactPreview(_entries[index].Text), bodyFont, bodyBrush, textArea, bodyFormat);
            }
            string note = copied ? "已复制到剪贴板" : RelativeTime(_entries[index].CreatedAt);
            using (Font noteFont = ScaledFont("Microsoft YaHei UI", 7.8f, FontStyle.Regular))
            using (SolidBrush noteBrush = new SolidBrush(copied ? Color.FromArgb(75, 123, 71) : muted)) g.DrawString(note, noteFont, noteBrush, bounds.X + 51, bounds.Bottom - 19);
        }

        private void DrawEmptyCard(Graphics g, Rectangle bounds, Color border, Color muted)
        {
            using (GraphicsPath path = RoundedRectangle(bounds, 12))
            using (Pen pen = new Pen(Color.FromArgb(190, border))) { pen.DashStyle = DashStyle.Dash; g.DrawPath(pen, path); }
            using (Font font = ScaledFont("Microsoft YaHei UI", 8.5f, FontStyle.Regular))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(145, muted))) g.DrawString("复制一段文字后会出现在这里", font, brush, bounds.X + 49, bounds.Y + 24);
            using (Pen circlePen = new Pen(Color.FromArgb(170, border), 1.2f)) g.DrawEllipse(circlePen, bounds.X + 14, bounds.Y + 20, 24, 24);
        }

        private void DrawPillButton(Graphics g, Rectangle bounds, string text)
        {
            using (GraphicsPath path = RoundedRectangle(bounds, 14))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(241, 237, 231))) g.FillPath(brush, path);
            using (Font font = ScaledFont("Microsoft YaHei UI", 8.5f, FontStyle.Regular))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(104, 96, 87)))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                g.DrawString(text, font, brush, bounds, format);
            }
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
            menu.Items.Add("展开剪贴板", null, delegate { ExpandOnCursorScreen(); });
            menu.Items.Add("收起到右侧", null, delegate { Collapse(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("清空记录", null, delegate { ClearHistory(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出 PicoPaste", null, delegate { ExitApplication(); });
            return menu;
        }

        private Icon CreateTrayIcon()
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(205, 112, 82))) g.FillEllipse(brush, 1, 1, 30, 30);
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
            if (!_expanded) { ExpandOnCursorScreen(); return; }
            Point logical = LogicalPoint(e.Location);
            int newHover = -1;
            for (int i = 0; i < _entries.Count; i++) if (_cardBounds[i].Contains(logical)) newHover = i;
            if (newHover != _hoveredCard) { _hoveredCard = newHover; Invalidate(); }
            Cursor = newHover >= 0 || _clearBounds.Contains(logical) ? Cursors.Hand : Cursors.Default;
            _outsideTicks = 0;
        }

        private void HandleMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Point logical = LogicalPoint(e.Location);
            _pressedCard = -1;
            for (int i = 0; i < _entries.Count; i++) if (_cardBounds[i].Contains(logical)) _pressedCard = i;
        }

        private void HandleMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Point logical = LogicalPoint(e.Location);
            int pressed = _pressedCard;
            _pressedCard = -1;
            if (pressed >= 0 && pressed < _entries.Count && _cardBounds[pressed].Contains(logical)) CopyEntry(pressed, 0);
            else if (_clearBounds.Contains(logical)) ClearHistory();
        }

        private void HoverTimerTick(object sender, EventArgs e)
        {
            if (_animationTimer.Enabled || (_trayMenu != null && _trayMenu.Visible)) return;
            if (!_expanded)
            {
                if (Bounds.Contains(Cursor.Position)) ExpandOnCursorScreen();
                return;
            }
            Rectangle safeArea = Bounds;
            safeArea.Inflate(8, 8);
            if (safeArea.Contains(Cursor.Position)) _outsideTicks = 0;
            else if (++_outsideTicks >= 7) Collapse();
        }

        private void ExpandOnCursorScreen()
        {
            Screen target = Screen.FromPoint(Cursor.Position);
            bool changedScreen = _screen == null || _screen.DeviceName != target.DeviceName;
            _screen = target;
            PlaceWindow(!changedScreen);
            _expanded = true;
            _outsideTicks = 0;
            AnimateTo(_screen.WorkingArea.Right - Width);
            Invalidate();
        }

        private void Collapse()
        {
            if (!_expanded && !_animationTimer.Enabled) return;
            if (_screen == null) _screen = Screen.FromControl(this);
            _expanded = false;
            _hoveredCard = -1;
            AnimateTo(_screen.WorkingArea.Right - ScalePixels(VisibleHandle));
            Invalidate();
        }

        private void PlaceWindow(bool keepCurrentLeft)
        {
            if (_screen == null) _screen = Screen.PrimaryScreen;
            Rectangle area = _screen.WorkingArea;
            int top = area.Top + Math.Max(10, (area.Height - Height) / 2);
            if (top + Height > area.Bottom - 10) top = Math.Max(area.Top, area.Bottom - Height - 10);
            int left = keepCurrentLeft ? Left : area.Right - ScalePixels(VisibleHandle);
            Bounds = new Rectangle(left, top, Width, Height);
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
            Invalidate();
        }

        private int ScalePixels(int value) { return (int)Math.Round(value * _scale); }

        private Point LogicalPoint(Point value)
        {
            return new Point((int)Math.Round(value.X / _scale), (int)Math.Round(value.Y / _scale));
        }

        private void AnimateTo(int destinationLeft)
        {
            _animationFrom = Left;
            _animationTo = destinationLeft;
            _animationStarted = DateTime.UtcNow;
            _animationTimer.Start();
        }

        private void AnimationTimerTick(object sender, EventArgs e)
        {
            double t = (DateTime.UtcNow - _animationStarted).TotalMilliseconds / 190.0;
            if (t >= 1.0) { Left = _animationTo; _animationTimer.Stop(); return; }
            double eased = 1.0 - Math.Pow(1.0 - t, 3.0);
            Left = _animationFrom + (int)Math.Round((_animationTo - _animationFrom) * eased);
        }

        private void CaptureClipboard(int attempt)
        {
            try
            {
                if (!Clipboard.ContainsText(TextDataFormat.UnicodeText)) return;
                string text = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (string.IsNullOrWhiteSpace(text)) return;
                if (_entries.Count > 0 && _entries[0].Text == text) return;
                _entries.RemoveAll(delegate(ClipEntry item) { return item.Text == text; });
                _entries.Insert(0, new ClipEntry { Text = text, CreatedAt = DateTime.Now });
                if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
                HistoryStore.Save(_entries);
                Invalidate();
            }
            catch (ExternalException) { if (attempt < 3) RetryClipboard(delegate { CaptureClipboard(attempt + 1); }); }
        }

        private void CopyEntry(int index, int attempt)
        {
            if (index < 0 || index >= _entries.Count) return;
            try
            {
                Clipboard.SetText(_entries[index].Text, TextDataFormat.UnicodeText);
                _copiedCard = index;
                _feedbackTimer.Stop();
                _feedbackTimer.Start();
                Invalidate();
            }
            catch (ExternalException) { if (attempt < 3) RetryClipboard(delegate { CopyEntry(index, attempt + 1); }); }
        }

        private void RetryClipboard(MethodInvoker action)
        {
            Timer retry = new Timer();
            retry.Interval = 80;
            retry.Tick += delegate { retry.Stop(); retry.Dispose(); action(); };
            retry.Start();
        }

        private void ClearHistory()
        {
            _entries.Clear();
            _copiedCard = -1;
            HistoryStore.Clear();
            Invalidate();
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
            if (age.TotalSeconds < 60) return "刚刚复制";
            if (age.TotalMinutes < 60) return ((int)age.TotalMinutes) + " 分钟前";
            if (age.TotalHours < 24) return ((int)age.TotalHours) + " 小时前";
            return value.ToString("M月d日 HH:mm");
        }

        private void DisplaySettingsChanged(object sender, EventArgs e)
        {
            if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { DisplaySettingsChanged(sender, e); }); return; }
            _screen = Screen.FromPoint(Cursor.Position);
            PlaceWindow(false);
            if (_expanded) Left = _screen.WorkingArea.Right - Width;
        }

        private void ExitApplication() { _exiting = true; Close(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Collapse(); return; }
            _exiting = true;
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
                if (_hoverTimer != null) _hoverTimer.Dispose();
                if (_animationTimer != null) _animationTimer.Dispose();
                if (_feedbackTimer != null) _feedbackTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
