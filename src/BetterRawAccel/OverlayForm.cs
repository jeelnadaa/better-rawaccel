using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using BetterRawAccel.Core;

namespace BetterRawAccel
{
    /// <summary>
    /// Lightweight, non-intrusive in-game HUD overlay.
    /// Uses WS_EX_TRANSPARENT, WS_EX_NOACTIVATE, and WS_EX_LAYERED.
    /// Completely external and 100% safe with Riot Vanguard, Easy Anti-Cheat, and BattlEye.
    /// </summary>
    public sealed class OverlayForm : Form
    {
        const int WS_EX_TOPMOST = 0x00000008;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_LAYERED = 0x00080000;
        const int WS_EX_NOACTIVATE = 0x08000000;

        readonly Timer fadeTimer = new Timer();
        DateTime showUntil = DateTime.MinValue;
        float targetOpacity = 0.92f;
        float currentOpacity = 0.0f;

        public string ProfileName { get; private set; } = "default";
        public string DeviceName { get; private set; } = "All Mice";
        public string ModeName { get; private set; } = "Natural";
        public string SensValue { get; private set; } = "1.00×";
        public string Cm360Value { get; private set; } = "~52 cm";
        public string PositionMode { get; private set; } = "top-right";
        public bool AutoFade { get; set; } = false;
        public bool IsActive { get; private set; } = false;

        public OverlayForm(AppSettings app)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(360, 96);
            BackColor = Color.FromArgb(14, 15, 20);

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            UpdateStyles();

            AutoFade = app.OverlayAutoFade;
            PositionMode = app.OverlayPosition ?? "top-right";

            fadeTimer.Interval = 25;
            fadeTimer.Tick += OnFadeTick;

            Reposition(PositionMode);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST;
                cp.ExStyle |= WS_EX_TRANSPARENT; // Mouse clicks pass right through to game
                cp.ExStyle |= WS_EX_LAYERED;
                cp.ExStyle |= WS_EX_NOACTIVATE;  // Never steals focus from Valorant / games
                cp.ExStyle |= WS_EX_TOOLWINDOW;  // Hidden from Alt-Tab
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        public void Reposition(string pos)
        {
            PositionMode = pos;
            var screen = Screen.PrimaryScreen.WorkingArea;
            int margin = 24;
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

            if (AutoFade && IsActive)
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
                if (AutoFade) TriggerToast(3.5);
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

        void OnFadeTick(object sender, EventArgs e)
        {
            if (!AutoFade && IsActive)
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
                using (var brush = new SolidBrush(Color.FromArgb(235, 15, 17, 23)))
                {
                    g.FillPath(brush, path);
                }

                // High-contrast technical border
                using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255), 1.25f))
                {
                    g.DrawPath(pen, path);
                }
            }

            // Status indicator dot
            int dotY = 16;
            using (var dotBrush = new SolidBrush(Color.FromArgb(229, 72, 77)))
            {
                g.FillEllipse(dotBrush, 14, dotY, 7, 7);
            }

            // Brand: accel. HUD
            using (var brandFont = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (var brandBrush = new SolidBrush(Color.FromArgb(220, 222, 230)))
            {
                g.DrawString("accel.", brandFont, brandBrush, 26, dotY - 4);
            }

            // Active Profile Pill Tag
            using (var profFont = new Font("Segoe UI", 10.5f, FontStyle.Bold))
            using (var profBrush = new SolidBrush(Color.FromArgb(255, 255, 255)))
            {
                string profText = "PROFILE: " + ProfileName;
                g.DrawString(profText, profFont, profBrush, 85, dotY - 5);
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
                string devText = "Mouse: " + Truncate(DeviceName, 36);
                g.DrawString(devText, devFont, devBrush, 14, 44);
            }

            // Mode, Sens & cm/360 stats
            using (var statsFont = new Font("Segoe UI", 8.5f, FontStyle.Regular))
            using (var statsBrush = new SolidBrush(Color.FromArgb(130, 136, 150)))
            using (var accentBrush = new SolidBrush(Color.FromArgb(77, 163, 255)))
            {
                string modeText = ModeName + " (" + SensValue + ")";
                g.DrawString(modeText, statsFont, statsBrush, 14, 66);

                string cmText = "·  Valorant: " + Cm360Value;
                var modeSize = g.MeasureString(modeText, statsFont);
                g.DrawString(cmText, statsFont, accentBrush, 14 + modeSize.Width + 4, 66);
            }

            // Hotkey toggle hint
            using (var hintFont = new Font("Segoe UI", 8f, FontStyle.Regular))
            using (var hintBrush = new SolidBrush(Color.FromArgb(90, 95, 110)))
            {
                g.DrawString("[Ctrl+Alt+O]", hintFont, hintBrush, Width - 80, 68);
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
                fadeTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
