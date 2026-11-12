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
        public string Cm360Value { get; private set; } = "—";
        /// <summary>Game the cm/360 cell is for, or empty when no game sensitivity has been set.</summary>
        public string GameLabel { get; private set; } = "";
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

            Relayout();
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
            BackColor = currentTheme.Sheet;

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

            Relayout();
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

        public void UpdateInfo(string profile, string device, string mode, string sens, string cm360, string gameLabel = null)
        {
            GameLabel = gameLabel ?? "";
            ProfileName = string.IsNullOrWhiteSpace(profile) ? "default" : profile;
            DeviceName = string.IsNullOrWhiteSpace(device) ? "All Mice" : device;
            ModeName = string.IsNullOrWhiteSpace(mode) ? "Linear" : mode;
            SensValue = sens ?? "1.00×";
            Cm360Value = string.IsNullOrWhiteSpace(cm360) ? "—" : cm360;

            Relayout();
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

        // ------------------------------------------------------------ layout and drawing
        //
        // Everything is laid out in 96-dpi units and multiplied by the display scale, and every cell is as wide as
        // its text (capped, with an ellipsis), so nothing can run into anything else at any scaling or text length.

        static readonly string UiFamily = PickFamily("Segoe UI Variable Text", "Segoe UI", "Arial");
        static readonly string MonoFamily = PickFamily("Cascadia Mono", "Consolas", "Courier New");

        static string PickFamily(params string[] names)
        {
            try
            {
                using (var fc = new InstalledFontCollection())
                {
                    var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var f in fc.Families) have.Add(f.Name);
                    foreach (var n in names) if (have.Contains(n)) return n;
                }
            }
            catch { }
            return FontFamily.GenericSansSerif.Name;
        }

        /// <summary>Text in the title block's four cells: label above, value below.</summary>
        string[] CellLabels => new[] { "PROFILE", string.IsNullOrEmpty(GameLabel) ? "CM/360" : GameLabel.ToUpperInvariant() + " CM/360", "CURVE", "DEVICE" };
        string[] CellValues => new[] { ProfileName, Cm360Value, ModeName + " · " + SensValue, DeviceName };
        static readonly bool[] CellMono = { false, true, false, false };

        string FooterText => IsMovable
            ? "Drag to place · right-click or Ctrl+Alt+M to lock"
            : "Ctrl+Alt+O hide · Ctrl+Alt+P next profile · Ctrl+Alt+M move";

        float scale = 1f;

        void Relayout()
        {
            try
            {
                using (var g = CreateGraphics())
                {
                    scale = g.DpiX / 96f;
                    var size = LayoutContent(g, false);
                    if (Size != size) Size = size;
                }
                Reposition(PositionMode, CustomX, CustomY);
                Invalidate();
            }
            catch (Exception e)
            {
                Log.Error("overlay layout", e);
            }
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            Relayout();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            LayoutContent(g, true);
        }

        int Px(float v) => (int)Math.Round(v * scale);

        /// <summary>Draws the overlay as it would appear, whether or not it is on screen.</summary>
        public string RenderPngBase64()
        {
            Relayout();
            using (var bmp = new Bitmap(Width, Height))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    LayoutContent(g, true);
                }
                using (var ms = new System.IO.MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
        }

        /// <summary>Measures (draw = false) or draws (draw = true) the overlay. Returns the size it needs.</summary>
        Size LayoutContent(Graphics g, bool draw)
        {
            var t = currentTheme ?? Themes["paper"];
            const TextFormatFlags One = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            const TextFormatFlags Ell = One | TextFormatFlags.EndEllipsis;

            using (var fBrand = new Font(UiFamily, 15f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var fTag = new Font(UiFamily, 9.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var fLabel = new Font(UiFamily, 9.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var fValue = new Font(UiFamily, 14f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var fMono = new Font(MonoFamily, 14f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var fFoot = new Font(UiFamily, 10f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                Size M(string s, Font f) => TextRenderer.MeasureText(g, s, f, new Size(int.MaxValue, int.MaxValue), One);

                int pad = Px(12), gap = Px(14), maxCell = Px(170), minCell = Px(56);

                // Header: curve mark, wordmark, state tag, and a LOCK button while movable.
                int markW = Px(20);
                var brand = M("accel", fBrand);
                var dot = M(".", fBrand);
                string tag = IsMovable ? "MOVABLE" : "LIVE HUD";
                var tagSz = M(tag, fTag);
                var lockSz = M("LOCK", fTag);
                int lockW = IsMovable ? lockSz.Width + Px(18) : 0;
                int headerH = Math.Max(brand.Height, Px(22));
                int headerW = markW + Px(6) + brand.Width + dot.Width + Px(12) + tagSz.Width + (IsMovable ? Px(16) + lockW : 0);

                // Cells.
                var labels = CellLabels;
                var values = CellValues;
                var widths = new int[4];
                int labelH = M("Ag", fLabel).Height, valueH = Math.Max(M("Ag", fValue).Height, M("Ag", fMono).Height);
                for (int i = 0; i < 4; i++)
                {
                    int w = Math.Max(M(labels[i], fLabel).Width, M(values[i] ?? "", CellMono[i] ? fMono : fValue).Width);
                    widths[i] = Math.Max(minCell, Math.Min(maxCell, w));
                }
                int cellsW = 0;
                for (int i = 0; i < 4; i++) cellsW += widths[i];
                cellsW += gap * 6;   // a gap on each side of the three dividers

                var foot = M(FooterText, fFoot);

                int innerW = Math.Max(cellsW, Math.Max(headerW, foot.Width));
                int W = innerW + pad * 2;
                int yRule1 = pad + headerH + Px(7);
                int yLabel = yRule1 + Px(8);
                int yValue = yLabel + labelH + Px(3);
                int yRule2 = yValue + valueH + Px(8);
                int yFoot = yRule2 + Px(6);
                int H = yFoot + foot.Height + Px(9);

                if (!draw) return new Size(W, H);

                // Sheet and frame. Square corners: this is a drawing title block, and rounded corners would leave
                // the window's own colour showing at the corners on light themes.
                using (var sheet = new SolidBrush(t.Sheet)) g.FillRectangle(sheet, 0, 0, W, H);
                using (var frame = new Pen(IsMovable ? t.Pen : t.Border, Math.Max(1f, (IsMovable ? 2f : 1.5f) * scale)))
                {
                    if (IsMovable) frame.DashStyle = DashStyle.Dash;
                    float inset = frame.Width / 2f;
                    g.DrawRectangle(frame, inset, inset, W - frame.Width, H - frame.Width);
                }

                // Header.
                int hy = pad;
                using (var curve = new Pen(t.Ink, 2.2f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                using (var path = new GraphicsPath())
                {
                    float x0 = pad, yb = hy + headerH * 0.78f, yt = hy + headerH * 0.22f;
                    path.AddLine(x0, yb, x0 + markW * 0.3f, yb);
                    path.AddBezier(x0 + markW * 0.3f, yb, x0 + markW * 0.55f, yb, x0 + markW * 0.7f, yt + (yb - yt) * 0.3f, x0 + markW, yt);
                    g.DrawPath(curve, path);
                }
                int bx = pad + markW + Px(6);
                int by = hy + (headerH - brand.Height) / 2;
                TextRenderer.DrawText(g, "accel", fBrand, new Point(bx, by), t.Ink, One);
                TextRenderer.DrawText(g, ".", fBrand, new Point(bx + brand.Width, by), t.Pen, One);
                int tx = bx + brand.Width + dot.Width + Px(12);
                float ledD = Px(6);
                using (var led = new SolidBrush(t.Pen)) g.FillEllipse(led, tx, hy + (headerH - ledD) / 2f, ledD, ledD);
                TextRenderer.DrawText(g, tag, fTag, new Point(tx + Px(11), hy + (headerH - tagSz.Height) / 2), IsMovable ? t.Pen : t.Ink3, One);

                if (IsMovable)
                {
                    lockBtnRect = new Rectangle(W - pad - lockW, hy + (headerH - Px(20)) / 2, lockW, Px(20));
                    using (var b = new SolidBrush(t.Ink)) g.FillRectangle(b, lockBtnRect);
                    TextRenderer.DrawText(g, "LOCK", fTag, lockBtnRect, t.Sheet, One | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                else lockBtnRect = Rectangle.Empty;

                using (var rule = new Pen(t.Rule, Math.Max(1f, scale)))
                {
                    g.DrawLine(rule, pad, yRule1, W - pad, yRule1);
                    g.DrawLine(rule, pad, yRule2, W - pad, yRule2);

                    // Cells, with hairline dividers between them.
                    int x = pad;
                    for (int i = 0; i < 4; i++)
                    {
                        TextRenderer.DrawText(g, labels[i], fLabel, new Rectangle(x, yLabel, widths[i], labelH), t.Ink3, Ell);
                        var color = i == 1 ? t.Pen : i == 3 ? t.Ink2 : t.Ink;
                        TextRenderer.DrawText(g, values[i] ?? "", CellMono[i] ? fMono : fValue, new Rectangle(x, yValue, widths[i], valueH), color, Ell);
                        x += widths[i];
                        if (i < 3)
                        {
                            x += gap;
                            g.DrawLine(rule, x, yRule1 + Px(5), x, yRule2 - Px(5));
                            x += gap;
                        }
                    }
                }

                TextRenderer.DrawText(g, FooterText, fFoot, new Point(pad, yFoot), IsMovable ? t.Pen : t.Ink3, One);
                return new Size(W, H);
            }
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
