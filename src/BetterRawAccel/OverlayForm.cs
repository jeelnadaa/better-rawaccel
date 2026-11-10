using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using BetterRawAccel.Core;

namespace BetterRawAccel
{
    /// <summary>
    /// Precision engineering in-game HUD overlay matching the desktop drafting aesthetic.
    /// Supports Fixed (WS_EX_TRANSPARENT click-through) and Movable (interactive drag) modes.
    /// Renders dynamically in all 14 application themes (Paper, Graphite, Blueprint, Obsidian, etc.)
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
        float targetOpacity = 0.96f;
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

        public string ActiveThemeName { get; private set; } = "paper";
        OverlayTheme currentTheme;

        bool isDragging = false;
        Point dragStartPoint;
        Rectangle lockBtnRect = Rectangle.Empty;

        public event Action<Point> PositionSaved;
        public event Action<bool> MovableStateChanged;

        public OverlayForm(AppSettings app)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(440, 108);
            BackColor = Color.FromArgb(20, 20, 20);

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            UpdateStyles();

            AutoFade = app.OverlayAutoFade;
            PositionMode = app.OverlayPosition ?? "top-right";
            CustomX = app.OverlayCustomX;
            CustomY = app.OverlayCustomY;
            IsMovable = app.OverlayMovable;

            SetTheme(app.Theme ?? "paper");

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

        public void SetTheme(string themeName)
        {
            if (string.IsNullOrWhiteSpace(themeName)) themeName = "paper";
            ActiveThemeName = themeName.ToLowerInvariant();
            if (!Themes.TryGetValue(ActiveThemeName, out currentTheme))
                currentTheme = Themes["paper"];

            Invalidate();
            Update();
        }

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
                if (!lockBtnRect.IsEmpty && lockBtnRect.Contains(e.Location))
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
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var theme = currentTheme ?? Themes["paper"];
            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);

            // 1. Engineering Drafting Sheet Background
            int bgAlpha = theme.IsDark ? 245 : 248;
            using (var path = RoundedRect(bounds, 6))
            {
                using (var brush = new SolidBrush(Color.FromArgb(bgAlpha, theme.Sheet)))
                {
                    g.FillPath(brush, path);
                }

                // Outer border: crisp high-contrast technical border, dashed highlight when movable
                Color borderColor = IsMovable ? theme.Pen : theme.Border;
                float borderWidth = IsMovable ? 2.0f : 1.25f;

                using (var pen = new Pen(borderColor, borderWidth))
                {
                    if (IsMovable) pen.DashStyle = DashStyle.Dash;
                    g.DrawPath(pen, path);
                }
            }

            // Typography setup
            using (var fontBrand = CreateFont(10.5f, FontStyle.Bold))
            using (var fontTitle = CreateFont(11f, FontStyle.Bold))
            using (var fontLabel = CreateFont(7.5f, FontStyle.Bold))
            using (var fontSub = CreateFont(9f, FontStyle.Regular))
            using (var fontFoot = CreateFont(7.2f, FontStyle.Regular))
            using (var fontFootBold = CreateFont(7.5f, FontStyle.Bold))
            using (var inkBrush = new SolidBrush(theme.Ink))
            using (var ink2Brush = new SolidBrush(theme.Ink2))
            using (var ink3Brush = new SolidBrush(theme.Ink3))
            using (var penBrush = new SolidBrush(theme.Pen))
            using (var rulePen = new Pen(theme.Rule, 1f))
            {
                // 2. Header Row: Official Option 4 Brand Logo (accel.) & Status
                // Vector curve icon
                using (var curvePen = new Pen(theme.Ink, 2.2f))
                {
                    curvePen.StartCap = LineCap.Round;
                    curvePen.EndCap = LineCap.Round;
                    using (var curvePath = new GraphicsPath())
                    {
                        curvePath.AddLine(14, 18, 20, 18);
                        curvePath.AddBezier(20, 18, 24, 18, 26, 13, 29, 8);
                        g.DrawPath(curvePen, curvePath);
                    }
                }
                g.FillEllipse(inkBrush, 28f, 6.5f, 3.2f, 3.2f);
                g.FillEllipse(penBrush, 32.5f, 6.5f, 2.4f, 2.4f);

                // Wordmark: accel.
                g.DrawString("accel", fontBrand, inkBrush, 38, 5);
                var wordSize = g.MeasureString("accel", fontBrand);
                g.DrawString(".", fontBrand, penBrush, 38 + wordSize.Width - 5, 5);

                // LED Status Pulse
                float ledX = 38 + wordSize.Width + 8;
                g.FillEllipse(penBrush, ledX, 11, 5, 5);

                // Mode / Status Badge
                if (IsMovable)
                {
                    string movLabel = "MOVABLE HUD";
                    var movSize = g.MeasureString(movLabel, fontLabel);
                    var movBadgeRect = new Rectangle((int)ledX + 12, 7, (int)movSize.Width + 10, 16);
                    using (var badgeBrush = new SolidBrush(Color.FromArgb(theme.IsDark ? 70 : 40, theme.Pen)))
                    using (var badgePath = RoundedRect(movBadgeRect, 3))
                    {
                        g.FillPath(badgeBrush, badgePath);
                    }
                    g.DrawString(movLabel, fontLabel, penBrush, ledX + 17, 8);

                    // Technical Lock Button (top right, no emoji)
                    lockBtnRect = new Rectangle(Width - 76, 5, 62, 20);
                    using (var lockPath = RoundedRect(lockBtnRect, 3))
                    using (var lockBg = new SolidBrush(theme.Pen))
                    using (var lockPen = new Pen(theme.Border, 1f))
                    {
                        g.FillPath(lockBg, lockPath);
                        g.DrawPath(lockPen, lockPath);
                    }
                    using (var lockTextBrush = new SolidBrush(theme.IsDark ? Color.Black : Color.White))
                    {
                        var btnSize = g.MeasureString("LOCK", fontFootBold);
                        float bx = lockBtnRect.X + (lockBtnRect.Width - btnSize.Width) / 2f;
                        float by = lockBtnRect.Y + (lockBtnRect.Height - btnSize.Height) / 2f;
                        g.DrawString("LOCK", fontFootBold, lockTextBrush, bx, by);
                    }
                }
                else
                {
                    lockBtnRect = Rectangle.Empty;
                    g.DrawString("ACTIVE · KERNEL HUD", fontLabel, ink3Brush, ledX + 12, 9);
                }

                // 3. Hairline divider under Header
                g.DrawLine(rulePen, 10, 28, Width - 10, 28);

                // 4. Precision Title Block Cells (Key-Value Grid)
                // Cell 1: PROFILE
                int col1X = 14;
                g.DrawString("PROFILE", fontLabel, ink2Brush, col1X, 32);
                g.DrawString(Truncate(ProfileName, 14), fontTitle, inkBrush, col1X, 45);

                // Divider 1
                g.DrawLine(rulePen, 136, 30, 136, 74);

                // Cell 2: VALORANT CM/360
                int col2X = 144;
                g.DrawString("VALORANT", fontLabel, ink2Brush, col2X, 32);
                g.DrawString(Cm360Value, fontTitle, penBrush, col2X, 45);

                // Divider 2
                g.DrawLine(rulePen, 246, 30, 246, 74);

                // Cell 3: MODE & SENSITIVITY
                int col3X = 254;
                g.DrawString("MODE · MULT", fontLabel, ink2Brush, col3X, 32);
                string modeSens = ModeName + " · " + SensValue;
                g.DrawString(Truncate(modeSens, 14), fontSub, inkBrush, col3X, 46);

                // Divider 3
                g.DrawLine(rulePen, 344, 30, 344, 74);

                // Cell 4: DEVICE
                int col4X = 352;
                g.DrawString("DEVICE", fontLabel, ink2Brush, col4X, 32);
                g.DrawString(Truncate(DeviceName, 12), fontSub, ink2Brush, col4X, 46);

                // 5. Hairline divider above Footer
                g.DrawLine(rulePen, 10, 75, Width - 10, 75);

                // 6. Footer Annotation / Hotkeys
                if (IsMovable)
                {
                    string hint = "Drag anywhere to position · Click [LOCK] or press Ctrl+Alt+M to lock";
                    g.DrawString(hint, fontFootBold, penBrush, 14, 83);
                }
                else
                {
                    string driverTag = "accel. driver: connected";
                    g.DrawString(driverTag, fontFoot, ink3Brush, 14, 84);

                    string hotkeys = "Ctrl+Alt+O Toggle · Ctrl+Alt+P Switch · Ctrl+Alt+M Move";
                    var hkSize = g.MeasureString(hotkeys, fontFoot);
                    g.DrawString(hotkeys, fontFoot, ink3Brush, Width - hkSize.Width - 14, 84);
                }
            }
        }

        static Font CreateFont(float size, FontStyle style)
        {
            try { return new Font("Satoshi", size, style); }
            catch { }
            try { return new Font("Segoe UI", size, style); }
            catch { return new Font(FontFamily.GenericSansSerif, size, style); }
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
            return s.Length <= maxLen ? s : s.Substring(0, maxLen - 1) + "…";
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

        public sealed class OverlayTheme
        {
            public Color Sheet { get; set; }
            public Color Ink { get; set; }
            public Color Ink2 { get; set; }
            public Color Ink3 { get; set; }
            public Color Pen { get; set; }
            public Color Rule { get; set; }
            public Color Border { get; set; }
            public bool IsDark { get; set; }
        }

        public static readonly Dictionary<string, OverlayTheme> Themes = new Dictionary<string, OverlayTheme>(StringComparer.OrdinalIgnoreCase)
        {
            ["paper"] = new OverlayTheme
            {
                Sheet = Color.FromArgb(242, 239, 231),
                Ink = Color.FromArgb(22, 21, 15),
                Ink2 = Color.FromArgb(75, 73, 64),
                Ink3 = Color.FromArgb(111, 108, 97),
                Pen = Color.FromArgb(181, 49, 27),
                Rule = Color.FromArgb(32, 22, 21, 15),
                Border = Color.FromArgb(22, 21, 15),
                IsDark = false
            },
            ["ledger"] = new OverlayTheme
            {
                Sheet = Color.FromArgb(237, 241, 230),
                Ink = Color.FromArgb(15, 31, 21),
                Ink2 = Color.FromArgb(59, 76, 64),
                Ink3 = Color.FromArgb(88, 106, 92),
                Pen = Color.FromArgb(31, 79, 176),
                Rule = Color.FromArgb(35, 15, 31, 21),
                Border = Color.FromArgb(15, 31, 21),
                IsDark = false
            },
            ["parchment"] = new OverlayTheme
            {
                Sheet = Color.FromArgb(250, 243, 230),
                Ink = Color.FromArgb(31, 23, 16),
                Ink2 = Color.FromArgb(84, 67, 53),
                Ink3 = Color.FromArgb(125, 107, 90),
                Pen = Color.FromArgb(156, 56, 24),
                Rule = Color.FromArgb(32, 31, 23, 16),
                Border = Color.FromArgb(31, 23, 16),
                IsDark = false
            },
            ["millimeter"] = new OverlayTheme
            {
                Sheet = Color.FromArgb(250, 248, 242),
                Ink = Color.FromArgb(26, 31, 38),
                Ink2 = Color.FromArgb(70, 78, 89),
                Ink3 = Color.FromArgb(112, 120, 131),
                Pen = Color.FromArgb(211, 47, 47),
                Rule = Color.FromArgb(40, 230, 81, 0),
                Border = Color.FromArgb(26, 31, 38),
                IsDark = false
            },
            ["technical"] = new OverlayTheme
            {
                Sheet = Color.FromArgb(246, 248, 250),
                Ink = Color.FromArgb(13, 17, 23),
                Ink2 = Color.FromArgb(48, 54, 61),
                Ink3 = Color.FromArgb(87, 96, 106),
                Pen = Color.FromArgb(9, 105, 218),
                Rule = Color.FromArgb(35, 9, 105, 218),
                Border = Color.FromArgb(13, 17, 23),
                IsDark = false
            },
            ["chalk"] = new OverlayTheme
            {
                Sheet = Color.FromArgb(236, 239, 241),
                Ink = Color.FromArgb(27, 36, 40),
                Ink2 = Color.FromArgb(67, 79, 84),
                Ink3 = Color.FromArgb(104, 119, 125),
                Pen = Color.FromArgb(0, 121, 107),
                Rule = Color.FromArgb(32, 27, 36, 40),
                Border = Color.FromArgb(27, 36, 40),
                IsDark = false
            },
            ["blueprint"] = new OverlayTheme
            {
                Sheet = Color.FromArgb(18, 55, 105),
                Ink = Color.FromArgb(238, 243, 249),
                Ink2 = Color.FromArgb(195, 209, 226),
                Ink3 = Color.FromArgb(154, 174, 199),
                Pen = Color.FromArgb(245, 196, 81),
                Rule = Color.FromArgb(45, 221, 231, 243),
                Border = Color.FromArgb(238, 243, 249),
                IsDark = true
            },
            ["graphite"] = new OverlayTheme
            {
                Sheet = Color.FromArgb(28, 28, 26),
                Ink = Color.FromArgb(236, 232, 222),
                Ink2 = Color.FromArgb(184, 179, 166),
                Ink3 = Color.FromArgb(144, 140, 128),
                Pen = Color.FromArgb(255, 107, 66),
                Rule = Color.FromArgb(38, 236, 232, 222),
                Border = Color.FromArgb(236, 232, 222),
                IsDark = true
            },
            ["phosphor"] = new OverlayTheme
            {
                Sheet = Color.FromArgb(18, 16, 11),
                Ink = Color.FromArgb(242, 178, 76),
                Ink2 = Color.FromArgb(204, 150, 67),
                Ink3 = Color.FromArgb(166, 124, 60),
                Pen = Color.FromArgb(255, 240, 200),
                Rule = Color.FromArgb(42, 242, 178, 76),
                Border = Color.FromArgb(242, 178, 76),
                IsDark = true
            },
            ["obsidian"] = new OverlayTheme
            {
                Sheet = Color.FromArgb(12, 13, 14),
                Ink = Color.FromArgb(242, 244, 247),
                Ink2 = Color.FromArgb(168, 173, 183),
                Ink3 = Color.FromArgb(113, 119, 132),
                Pen = Color.FromArgb(255, 59, 48),
                Rule = Color.FromArgb(36, 242, 244, 247),
                Border = Color.FromArgb(242, 244, 247),
                IsDark = true
            },
            ["midnight"] = new OverlayTheme
            {
                Sheet = Color.FromArgb(14, 22, 38),
                Ink = Color.FromArgb(226, 232, 240),
                Ink2 = Color.FromArgb(148, 163, 184),
                Ink3 = Color.FromArgb(100, 116, 139),
                Pen = Color.FromArgb(0, 229, 255),
                Rule = Color.FromArgb(45, 56, 189, 248),
                Border = Color.FromArgb(226, 232, 240),
                IsDark = true
            },
            ["radar"] = new OverlayTheme
            {
                Sheet = Color.FromArgb(7, 21, 11),
                Ink = Color.FromArgb(51, 255, 102),
                Ink2 = Color.FromArgb(36, 191, 75),
                Ink3 = Color.FromArgb(24, 135, 52),
                Pen = Color.FromArgb(118, 255, 3),
                Rule = Color.FromArgb(48, 51, 255, 102),
                Border = Color.FromArgb(51, 255, 102),
                IsDark = true
            },
            ["cyberpunk"] = new OverlayTheme
            {
                Sheet = Color.FromArgb(23, 18, 36),
                Ink = Color.FromArgb(248, 249, 250),
                Ink2 = Color.FromArgb(184, 181, 199),
                Ink3 = Color.FromArgb(130, 126, 150),
                Pen = Color.FromArgb(255, 42, 133),
                Rule = Color.FromArgb(50, 168, 85, 247),
                Border = Color.FromArgb(255, 42, 133),
                IsDark = true
            },
            ["solarized"] = new OverlayTheme
            {
                Sheet = Color.FromArgb(0, 43, 54),
                Ink = Color.FromArgb(147, 161, 161),
                Ink2 = Color.FromArgb(101, 123, 131),
                Ink3 = Color.FromArgb(88, 110, 117),
                Pen = Color.FromArgb(181, 137, 0),
                Rule = Color.FromArgb(42, 131, 148, 150),
                Border = Color.FromArgb(147, 161, 161),
                IsDark = true
            }
        };
    }
}
