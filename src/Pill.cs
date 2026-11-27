// The lock-status pill, shared by Keyboard Lock and Touchpad Lock (same file in both projects,
// only the namespace differs).
//
// A black pill in the style of a Dynamic Island. While locked it rests as a small round icon, so it
// doesn't distract; pointing at it opens it to show the full text. It opens by itself for a moment
// right after locking. Drag it anywhere; the place is remembered. A per-pixel-alpha layered window,
// so the edges are smooth. It never takes focus.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KeyboardLock
{
    delegate void IconPainter(Graphics g, RectangleF bounds, Color color, bool locked);

    sealed class PillForm : Form
    {
        const int AppearMs = 160, HideMs = 220, OpenMs = 460, CloseMs = 260, FadeMs = 300, SlideMs = 260;
        const int PeekMs = 2200, UnlockedHoldMs = 1300, LeaveGraceMs = 250;

        readonly string avoidTitle;
        readonly IconPainter paintIcon;
        readonly Color lockedColor, unlockedColor;
        readonly float scale, iconSize;
        readonly Font titleFont, hintFont;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Timer watch = new Timer { Interval = 50 };
        readonly Timer hold = new Timer { Interval = UnlockedHoldMs };
        readonly Tween open = new Tween(), alpha = new Tween(), content = new Tween(), drop = new Tween();

        string title = "", hint = "";
        float titleWidth, hintWidth;
        bool lockedMode, hiding, hovered, pressed, dragging, stepQueued;
        long peekUntil, lastInside, dragEndedAt = -10000;
        Point anchor, anchorAtDown, mouseAtDown;   // anchor = centre of the round icon's top edge
        bool customPosition;

        public event EventHandler UnlockRequested, HotKeyPressed, PositionChanged;

        public PillForm(string windowTitle, string avoidWindowTitle, IconPainter painter, float iconSizeDip,
            Color locked, Color unlocked)
        {
            Text = windowTitle;
            avoidTitle = avoidWindowTitle;
            paintIcon = painter;
            lockedColor = locked;
            unlockedColor = unlocked;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Cursor = Cursors.Hand;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            iconSize = S(iconSizeDip);
            titleFont = new Font("Segoe UI Semibold", 14f * scale, GraphicsUnit.Pixel);
            hintFont = new Font("Segoe UI", 12.5f * scale, GraphicsUnit.Pixel);
            anchor = DefaultAnchor();
            watch.Tick += (s, e) => Watch();
            hold.Tick += (s, e) => Collapse();
            PillNative.DisablePowerThrottling();
        }

        float S(float v) { return v * scale; }
        int H { get { return (int)Math.Round(S(40)); } }
        long Now { get { return clock.ElapsedMilliseconds; } }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x80 | 0x08000000 | 0x08;   // layered, tool window, no activate, topmost
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && HotKeyPressed != null) HotKeyPressed(this, EventArgs.Empty);   // WM_HOTKEY
            base.WndProc(ref m);
        }

        // ---- Position -------------------------------------------------------------------------

        // The saved place, or null for the default (top centre of the main screen).
        public Point? Position
        {
            get { return customPosition ? anchor : (Point?)null; }
            set
            {
                customPosition = value.HasValue && OnSomeScreen(value.Value);
                anchor = customPosition ? value.Value : DefaultAnchor();
                if (Visible) Render();
            }
        }

        public void ResetPosition()
        {
            Position = null;
            if (PositionChanged != null) PositionChanged(this, EventArgs.Empty);
        }

        Point DefaultAnchor()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            return new Point(area.Left + area.Width / 2, area.Top + (int)S(10));
        }

        static bool OnSomeScreen(Point p)
        {
            foreach (Screen s in Screen.AllScreens)
                if (s.WorkingArea.Contains(p)) return true;
            return false;
        }

        Point ClampAnchor(Point p)
        {
            Rectangle area = Screen.FromPoint(p).WorkingArea;
            int h = H;
            return new Point(Math.Max(area.Left + h / 2, Math.Min(area.Right - h / 2, p.X)),
                             Math.Max(area.Top, Math.Min(area.Bottom - h, p.Y)));
        }

        // Where the pill is drawn: centred on the anchor, kept inside the screen when it opens.
        Rectangle PillRect(float width)
        {
            int w = (int)Math.Ceiling(width), h = H;
            Rectangle area = Screen.FromPoint(anchor).WorkingArea;
            int x = anchor.X - w / 2, y = anchor.Y + (int)Math.Round(drop.Value);
            x = Math.Max(area.Left, Math.Min(area.Right - w, x));
            y = Math.Max(area.Top, Math.Min(area.Bottom - h, y));
            return new Rectangle(x, y, w, h);
        }

        // Moves down out of the way when it would sit on top of the other app's pill.
        float DropTarget()
        {
            if (avoidTitle == null) return 0;
            IntPtr other = PillNative.FindWindow(null, avoidTitle);
            PillNative.RECT r;
            if (other == IntPtr.Zero || !PillNative.IsWindowVisible(other) || !PillNative.GetWindowRect(other, out r)) return 0;
            int h = H, gap = (int)S(8);
            int left = anchor.X - h / 2 - gap, right = anchor.X + h / 2 + gap;
            bool overlaps = r.Left < right && r.Right > left && r.Top < anchor.Y + h + gap && r.Bottom > anchor.Y - gap;
            return overlaps ? r.Bottom + gap - anchor.Y : 0;
        }

        // ---- Showing and hiding ---------------------------------------------------------------

        public bool IsShowingLocked { get { return lockedMode && Visible && !hiding; } }

        public void KeepOnTop()
        {
            if (Visible) PillNative.KeepOnTop(Handle);
        }

        public void ShowLocked(string newTitle, string newHint)
        {
            hold.Stop();
            if (IsShowingLocked && title == newTitle && hint == newHint) return;
            bool wasLocked = IsShowingLocked;
            lockedMode = true;
            if (!wasLocked) peekUntil = Now + PeekMs;   // open for a moment, so the change is noticed
            SetContent(newTitle, newHint);
            Appear();
        }

        // Only while the pill is on screen: turn into the green "unlocked" text, then go away.
        public void ShowUnlocked(string newTitle)
        {
            if (!Visible || hiding) return;
            lockedMode = false;
            SetContent(newTitle, "");
            Retarget();
            hold.Stop();
            hold.Start();
        }

        public void Collapse()
        {
            hold.Stop();
            lockedMode = false;
            if (!Visible || hiding) return;
            hiding = true;
            long now = Now;
            alpha.Set(0, HideMs, Ease.Linear, now);
            open.Set(0, HideMs, Ease.InCubic, now);
            StartFrames();
        }

        void Appear()
        {
            long now = Now;
            if (!Visible)
            {
                open.Jump(0);
                alpha.Jump(0);
                drop.Jump(DropTarget());
                anchor = ClampAnchor(anchor);
                hovered = false;
                Bounds = PillRect(H);
                Render();
                Show();
                PillNative.KeepOnTop(Handle);
                watch.Start();
            }
            hiding = false;
            alpha.Set(1, AppearMs, Ease.Linear, now);
            Retarget();
            StartFrames();
        }

        void SetContent(string newTitle, string newHint)
        {
            title = newTitle;
            hint = newHint;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                titleWidth = g.MeasureString(title, titleFont, PointF.Empty, StringFormat.GenericTypographic).Width;
                hintWidth = hint.Length == 0 ? 0 : g.MeasureString(hint, hintFont, PointF.Empty, StringFormat.GenericTypographic).Width;
            }
            content.Jump(0);
            content.Set(1, FadeMs, Ease.OutCubic, Now);
            StartFrames();
        }

        // Open while pointed at, dragged, just locked, or showing "unlocked"; otherwise just the icon.
        void Retarget()
        {
            if (hiding) return;
            float want = hovered || dragging || Now < peekUntil || !lockedMode ? 1f : 0f;
            if (open.To == want) return;
            if (want > 0) open.Set(1, OpenMs, Ease.OutBack, Now);
            else open.Set(0, CloseMs, Ease.InOutCubic, Now);
            StartFrames();
        }

        // Runs while the pill is visible: hover, the end of the first look, and the other pill.
        void Watch()
        {
            if (!Visible) { watch.Stop(); return; }
            long now = Now;
            Rectangle r = PillRect(CurrentWidth());
            r.Inflate(2, 2);
            if (r.Contains(Cursor.Position)) { hovered = true; lastInside = now; }
            else if (now - lastInside > LeaveGraceMs) hovered = false;

            if (!dragging)
            {
                float target = DropTarget();
                if (Math.Abs(target - drop.To) > 0.5f)
                {
                    drop.Set(target, SlideMs, Ease.OutCubic, now);
                    StartFrames();
                }
            }
            Retarget();
        }

        // ---- Mouse ----------------------------------------------------------------------------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            hovered = true;
            lastInside = Now;
            if (pressed)
            {
                Point p = Cursor.Position;
                int dx = p.X - mouseAtDown.X, dy = p.Y - mouseAtDown.Y;
                Size slop = SystemInformation.DragSize;
                if (!dragging && (Math.Abs(dx) > slop.Width || Math.Abs(dy) > slop.Height))
                {
                    dragging = true;
                    Cursor = Cursors.SizeAll;
                }
                if (dragging)
                {
                    anchor = ClampAnchor(new Point(anchorAtDown.X + dx, anchorAtDown.Y + dy));
                    Render();
                }
            }
            Retarget();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            pressed = true;
            mouseAtDown = Cursor.Position;
            anchorAtDown = anchor;
            Capture = true;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            pressed = false;
            Capture = false;
            if (!dragging) return;
            dragging = false;
            dragEndedAt = Now;
            Cursor = Cursors.Hand;
            customPosition = true;
            if (PositionChanged != null) PositionChanged(this, EventArgs.Empty);
            Retarget();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            bool justDragged = Now - dragEndedAt < SystemInformation.DoubleClickTime;
            if (e.Button == MouseButtons.Left && lockedMode && !justDragged && UnlockRequested != null)
                UnlockRequested(this, EventArgs.Empty);
        }

        // ---- Drawing --------------------------------------------------------------------------

        float FullWidth()
        {
            return TextLeft() + titleWidth + (hintWidth > 0 ? S(12) + hintWidth : 0) + S(20);
        }

        float TextLeft() { return S(15) + iconSize + S(9); }

        float CurrentWidth()
        {
            return Math.Max(H, H + (FullWidth() - H) * open.Value);
        }

        // One frame per screen refresh: draw, wait for the compositor, queue the next frame.
        void StartFrames()
        {
            if (stepQueued || IsDisposed || !IsHandleCreated) return;
            stepQueued = true;
            BeginInvoke((Action)Step);
        }

        void Step()
        {
            stepQueued = false;
            if (IsDisposed || !Visible) return;
            long now = Now;
            bool running = open.Update(now) | alpha.Update(now) | content.Update(now) | drop.Update(now);
            Render();
            if (hiding && !running)
            {
                hiding = false;
                watch.Stop();
                Hide();
                return;
            }
            if (!running) return;
            PillNative.DwmFlush();
            StartFrames();
        }

        void Render()
        {
            float width = CurrentWidth();
            Rectangle bounds = PillRect(width);
            int h = H;
            using (var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                using (GraphicsPath pill = PillPath(new RectangleF(0, 0, width, h)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.FillPath(Brushes.Black, pill);
                    g.SetClip(pill);   // the text is revealed as the pill opens

                    float o = Math.Max(0f, Math.Min(1f, open.Value));
                    Color color = lockedMode ? lockedColor : unlockedColor;
                    int a = (int)(Clamp01(content.Value) * 255);
                    float roundX = (h - iconSize) / 2f;
                    float iconX = roundX + (S(15) - roundX) * o;
                    if (a > 0)
                        paintIcon(g, new RectangleF(iconX, (h - iconSize) / 2f, iconSize, iconSize), Color.FromArgb(a, color), lockedMode);

                    int ta = (int)(a * Clamp01((o - 0.45f) / 0.55f));
                    if (ta > 0)
                    {
                        float x = TextLeft() - (S(15) - iconX);
                        using (var brush = new SolidBrush(Color.FromArgb(ta, Color.White)))
                            g.DrawString(title, titleFont, brush, x, TextTop(titleFont), StringFormat.GenericTypographic);
                        if (hint.Length > 0)
                            using (var brush = new SolidBrush(Color.FromArgb(ta, 150, 150, 158)))
                                g.DrawString(hint, hintFont, brush, x + titleWidth + S(12), TextTop(hintFont),
                                    StringFormat.GenericTypographic);
                    }
                }
                PillNative.SetLayeredBitmap(Handle, bmp, bounds.X, bounds.Y, (byte)(Clamp01(alpha.Value) * 255));
            }
        }

        static float Clamp01(float v) { return Math.Max(0f, Math.Min(1f, v)); }

        float TextTop(Font font)
        {
            return (H - font.Size * 1.33f) / 2f;   // Segoe UI line height is about 1.33 em
        }

        static GraphicsPath PillPath(RectangleF r)
        {
            float d = r.Height;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                watch.Dispose();
                hold.Dispose();
                titleFont.Dispose();
                hintFont.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---- Animation ------------------------------------------------------------------------

        static class Ease
        {
            public static float Linear(float t) { return t; }
            public static float OutCubic(float t) { float u = 1 - t; return 1 - u * u * u; }
            public static float InCubic(float t) { return t * t * t; }
            public static float InOutCubic(float t) { return t < 0.5f ? 4 * t * t * t : 1 - (float)Math.Pow(-2 * t + 2, 3) / 2; }

            // Slight overshoot, so the pill springs open.
            public static float OutBack(float t)
            {
                const float c1 = 1.2f, c3 = c1 + 1f;
                float u = t - 1f;
                return 1f + c3 * u * u * u + c1 * u * u;
            }
        }

        sealed class Tween
        {
            public float Value, To;
            float from;
            long start;
            int duration;
            Func<float, float> ease = Ease.Linear;
            bool running;

            public void Jump(float v)
            {
                Value = from = To = v;
                running = false;
            }

            public void Set(float to, int ms, Func<float, float> easing, long now)
            {
                from = Value;
                To = to;
                start = now;
                duration = Math.Max(1, ms);
                ease = easing;
                running = true;
            }

            // Returns true while still moving.
            public bool Update(long now)
            {
                if (!running) return false;
                float t = Math.Min(1f, (now - start) / (float)duration);
                Value = from + (To - from) * ease(t);
                if (t < 1f) return true;
                Value = To;
                running = false;
                return false;
            }
        }
    }

    static class PillNative
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("dwmapi.dll")]
        public static extern int DwmFlush();
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        public static void KeepOnTop(IntPtr hwnd)
        {
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // no size, move or activate
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POWER_THROTTLING { public uint Version, ControlMask, StateMask; }
        [DllImport("kernel32.dll")]
        static extern bool SetProcessInformation(IntPtr process, int infoClass, ref POWER_THROTTLING info, int size);
        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        // Otherwise Windows slows down the animation timing of a background app.
        public static void DisablePowerThrottling()
        {
            try
            {
                var p = new POWER_THROTTLING { Version = 1, ControlMask = 1 | 4 };
                SetProcessInformation(GetCurrentProcess(), 4, ref p, Marshal.SizeOf(p));
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll")]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr obj);

        // Shows a 32-bit ARGB bitmap as the content of a WS_EX_LAYERED window, with per-pixel alpha.
        public static void SetLayeredBitmap(IntPtr hwnd, Bitmap bmp, int x, int y, byte opacity)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr memory = CreateCompatibleDC(screen);
            IntPtr hbitmap = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = SelectObject(memory, hbitmap);
            try
            {
                var dst = new POINT { x = x, y = y };
                var src = new POINT();
                var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
                var blend = new BLENDFUNCTION { SourceConstantAlpha = opacity, AlphaFormat = 1 };
                UpdateLayeredWindow(hwnd, screen, ref dst, ref size, memory, ref src, 0, ref blend, 2);
            }
            finally
            {
                SelectObject(memory, old);
                DeleteObject(hbitmap);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
}
