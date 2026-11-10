using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using BetterRawAccel.Core;

namespace BetterRawAccel
{
    /// <summary>
    /// Lightweight, non-intrusive in-game HUD overlay.
    /// Supports Fixed (WS_EX_TRANSPARENT click-through) and Movable (interactive drag) modes.
    /// Completely external and 100% safe with Riot Vanguard, Easy Anti-Cheat, and BattlEye.
    /// </summary>
    public sealed class OverlayForm : Form
    {
        const int WS_EX_TOPMOST = 0x00000008;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_LAYERED = 0x00080000;
        const int WS_EX_NOACTIVATE = 0x08000000;

        const int GWL_EXSTYLE = -20;

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        readonly Timer fadeTimer = new Timer();
        DateTime showUntil = DateTime.MinValue;
        float targetOpacity = 0.94f;
        float currentOpacity = 0.0f;

        public string ProfileName { get; private set; } = "default";
        public string DeviceName { get; private set; } = "All Mice";
        public string ModeName { get; private set; } = "Natural";
        public string SensValue { get; private set; } = "1.00×";
        public string Cm360Value { get; private set; } = "~52 cm";
        public string PositionMode { get; private set; } = "top-right";
        public int CustomX { get; private set; } = -1;
        public int CustomY { get; private set; } = -1;
        public bool AutoFade { get; set; } = false;
        public bool IsActive { get; private set; } = false;
        public bool IsMovable { get; private set; } = false;

        bool isDragging = false;
        Point dragStartPoint;
        Rectangle lockBtnRect;

        public event Action<Point> PositionSaved;
        public event Action<bool> MovableStateChanged;

        public OverlayForm(AppSettings app)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(380, 100);
            BackColor = Color.FromArgb(14, 15, 20);

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            UpdateStyles();

            AutoFade = app.OverlayAutoFade;
            PositionMode = app.OverlayPosition ?? "top-right";
            CustomX = app.OverlayCustomX;
            CustomY = app.OverlayCustomY;
            IsMovable = app.OverlayMovable;

            fadeTimer.Interval = 25;
            fadeTimer.Tick += OnFadeTick;

            Reposition(PositionMode, CustomX, CustomY);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST;
                if (!IsMovable) cp.ExStyle |= WS_EX_TRANSPARENT; // Click-through when fixed
                cp.ExStyle |= WS_EX_LAYERED;
                cp.ExStyle |= WS_EX_NOACTIVATE;  // Never steals focus from Valorant / games
                cp.ExStyle |= WS_EX_TOOLWINDOW;  // Hidden from Alt-Tab
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        public void SetMovable(bool movable)
        {
            IsMovable = movable;
            if (IsHandleCreated)
            {
                int style = GetWindowLong(Handle, GWL_EXSTYLE);
                if (movable)
                {
                    style &= ~WS_EX_TRANSPARENT; // Intercept mouse for dragging
                    Cursor = Cursors.SizeAll;
                }
                else
                {
                    style |= WS_EX_TRANSPARENT; // Click-through for games
                    Cursor = Cursors.Default;
                }
                SetWindowLong(Handle, GWL_EXSTYLE, style);
            }

            if (movable && !Visible) SetVisibility(true);

            Invalidate();
            Update();
            MovableStateChanged?.Invoke(movable);
        }

        public void Reposition(string pos, int customX = -1, int customY = -1)
        {
            PositionMode = pos;
            CustomX = customX;
            CustomY = customY;

            var screen = Screen.PrimaryScreen.WorkingArea;
            int margin = 24;

            if (pos == "custom" && customX >= 0 && customY >= 0)
            {
                Location = new Point(Math.Min(screen.Right - Width, Math.Max(0, customX)),
                                     Math.Min(screen.Bottom - Height, Math.Max(0, customY)));
                return;
            }

            int x = screen.Right - Width - margin;
            int y = screen.Top + margin;

            if (pos == "top-left")
            {
                x = screen.Left + margin;
                y = screen.Top + margin;
            }
            else if (pos == "top-center")
            {
                x = screen.Left + (screen.Width - Width) / 2;
                y = screen.Top + 18;
            }
            else if (pos == "bottom-right")
            {
                x = screen.Right - Width - margin;
                y = screen.Bottom - Height - margin;
            }

            Location = new Point(x, y);
        }

        public void UpdateInfo(string profile, string device, string mode, string sens, string cm360)
        {
            ProfileName = string.IsNullOrWhiteSpace(profile) ? "default" : profile;
            DeviceName = string.IsNullOrWhiteSpace(device) ? "All Mice" : device;
            ModeName = string.IsNullOrWhiteSpace(mode) ? "Linear" : mode;
            SensValue = sens ?? "1.00×";
            Cm360Value = cm360 ?? "~52 cm";

            Invalidate();
            Update();

            if (AutoFade && IsActive && !IsMovable)
            {
                TriggerToast(3.5);
            }
        }

        public void TriggerToast(double seconds = 3.5)
        {
            showUntil = DateTime.UtcNow.AddSeconds(seconds);
            currentOpacity = targetOpacity;
            Opacity = currentOpacity;
            if (!Visible) Show();
            fadeTimer.Start();
        }

        public void SetVisibility(bool show)
        {
            IsActive = show;
            if (show)
            {
                currentOpacity = targetOpacity;
                Opacity = currentOpacity;
                if (!Visible) Show();
                if (AutoFade && !IsMovable) TriggerToast(3.5);
                else fadeTimer.Stop();
            }
            else
            {
                fadeTimer.Stop();
                Hide();
            }
        }

        public void Toggle()
        {
            SetVisibility(!IsActive);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!IsMovable) return;

            if (e.Button == MouseButtons.Left)
            {
                if (lockBtnRect.Contains(e.Location))
                {
                    SetMovable(false);
                    return;
                }
                isDragging = true;
                dragStartPoint = e.Location;
            }
            else if (e.Button == MouseButtons.Right)
            {
                SetMovable(false);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!IsMovable) return;

            if (isDragging && e.Button == MouseButtons.Left)
            {
                var screen = Screen.FromControl(this).Bounds;
                int newX = Location.X + e.X - dragStartPoint.X;
                int newY = Location.Y + e.Y - dragStartPoint.Y;

                newX = Math.Max(0, Math.Min(screen.Right - Width, newX));
                newY = Math.Max(0, Math.Min(screen.Bottom - Height, newY));

                Location = new Point(newX, newY);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (isDragging)
            {
                isDragging = false;
                CustomX = Location.X;
                CustomY = Location.Y;
                PositionMode = "custom";
                PositionSaved?.Invoke(Location);
            }
        }

        void OnFadeTick(object sender, EventArgs e)
        {
            if ((!AutoFade || IsMovable) && IsActive)
            {
                Opacity = targetOpacity;
                fadeTimer.Stop();
                return;
            }

            if (DateTime.UtcNow < showUntil)
            {
                Opacity = targetOpacity;
                return;
            }

            currentOpacity -= 0.05f;
            if (currentOpacity <= 0)
            {
                currentOpacity = 0;
                Opacity = 0;
                fadeTimer.Stop();
                Hide();
            }
            else
            {
                Opacity = currentOpacity;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);

            // Translucent rounded card background
            using (var path = RoundedRect(bounds, 10))
            {
                using (var brush = new SolidBrush(Color.FromArgb(240, 15, 17, 23)))
                {
                    g.FillPath(brush, path);
                }

                // Border: Cyan glowing accent when movable, subtle border when fixed
                Color borderColor = IsMovable
                    ? Color.FromArgb(230, 27, 150, 255)
                    : Color.FromArgb(70, 255, 255, 255);
                float borderWidth = IsMovable ? 2.0f : 1.25f;

                using (var pen = new Pen(borderColor, borderWidth))
                {
                    if (IsMovable) pen.DashStyle = DashStyle.Dash;
                    g.DrawPath(pen, path);
                }
            }

            // Status indicator dot (red = live, cyan = movable)
            int dotY = 16;
            Color dotColor = IsMovable ? Color.FromArgb(27, 150, 255) : Color.FromArgb(229, 72, 77);
            using (var dotBrush = new SolidBrush(dotColor))
            {
                g.FillEllipse(dotBrush, 14, dotY, 7, 7);
            }

            // Brand: accel. HUD or accel. [MOVABLE]
            using (var brandFont = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (var brandBrush = new SolidBrush(Color.FromArgb(220, 222, 230)))
            {
                string brand = IsMovable ? "accel. [MOVABLE]" : "accel.";
                g.DrawString(brand, brandFont, brandBrush, 26, dotY - 4);
            }

            // Active Profile Pill Tag
            using (var profFont = new Font("Segoe UI", 10.5f, FontStyle.Bold))
            using (var profBrush = new SolidBrush(Color.FromArgb(255, 255, 255)))
            {
                string profText = "PROFILE: " + ProfileName;
                int profX = IsMovable ? 145 : 85;
                g.DrawString(profText, profFont, profBrush, profX, dotY - 5);
            }

            // Lock / Fix Button when in Movable Mode
            if (IsMovable)
            {
                lockBtnRect = new Rectangle(Width - 84, 8, 72, 22);
                using (var btnPath = RoundedRect(lockBtnRect, 4))
                using (var btnBrush = new SolidBrush(Color.FromArgb(240, 27, 99, 181)))
                using (var btnTextFont = new Font("Segoe UI", 8.5f, FontStyle.Bold))
                using (var btnTextBrush = new SolidBrush(Color.White))
                {
                    g.FillPath(btnBrush, btnPath);
                    g.DrawString("🔒 Lock", btnTextFont, btnTextBrush, Width - 75, 11);
                }
            }
            else
            {
                lockBtnRect = Rectangle.Empty;
            }

            // Horizontal hairline separator
            using (var linePen = new Pen(Color.FromArgb(35, 255, 255, 255), 1f))
            {
                g.DrawLine(linePen, 14, 38, Width - 14, 38);
            }

            // Mouse Device Line
            using (var devFont = new Font("Segoe UI", 9f, FontStyle.Regular))
            using (var devBrush = new SolidBrush(Color.FromArgb(170, 175, 190)))
            {
                string devText = "Mouse: " + Truncate(DeviceName, 38);
                g.DrawString(devText, devFont, devBrush, 14, 44);
            }

            // Mode, Sens & cm/360 stats
            using (var statsFont = new Font("Segoe UI", 8.5f, FontStyle.Regular))
            using (var statsBrush = new SolidBrush(Color.FromArgb(130, 136, 150)))
            using (var accentBrush = new SolidBrush(Color.FromArgb(77, 163, 255)))
            {
                string modeText = ModeName + " (" + SensValue + ")";
                g.DrawString(modeText, statsFont, statsBrush, 14, 68);

                string cmText = "·  Valorant: " + Cm360Value;
                var modeSize = g.MeasureString(modeText, statsFont);
                g.DrawString(cmText, statsFont, accentBrush, 14 + modeSize.Width + 4, 68);
            }

            // Hotkey / Hint in bottom right
            using (var hintFont = new Font("Segoe UI", 8f, FontStyle.Regular))
            using (var hintBrush = new SolidBrush(Color.FromArgb(110, 115, 130)))
            {
                string hint = IsMovable
                    ? "[Drag anywhere · Click 🔒 or Ctrl+Alt+M to Lock]"
                    : "[Ctrl+Alt+O] Toggle · [Ctrl+Alt+M] Move";
                var hintSize = g.MeasureString(hint, hintFont);
                g.DrawString(hint, hintFont, hintBrush, Width - hintSize.Width - 14, 70);
            }
        }

        static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int diameter = radius * 2;
            var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= maxLen ? s : s.Substring(0, maxLen - 3) + "…";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                fadeTimer.Stop();
                fadeTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
