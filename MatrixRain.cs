// MatrixRain.cs  v3
// Same rain engine and render loop as v1 (the look you liked), plus:
//   * FIX: each monitor gets its own BufferedGraphicsContext, so all screens draw
//   * settings dialog now controls the start delay and the lock-on-wake option
//   * settings dialog writes both the live keys and the policy keys, so the
//     configuration survives a reboot without touching gpedit
// Build with build.bat (uses the C# compiler shipped with Windows).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MatrixRain
{
    // ------------------------------------------------------------------
    // Diagnostic log -> %TEMP%\MatrixRain.log
    // ------------------------------------------------------------------
    public static class Log
    {
        static string path;
        static object gate = new object();

        public static void Init()
        {
            try
            {
                path = Path.Combine(Path.GetTempPath(), "MatrixRain.log");
                if (File.Exists(path) && new FileInfo(path).Length > 200000) File.Delete(path);
            }
            catch { path = null; }
        }

        public static void W(string msg)
        {
            if (path == null) return;
            try
            {
                lock (gate)
                    File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n");
            }
            catch { }
        }

        public static void Ex(string where, Exception ex)
        {
            W("EXCEPTION in " + where + ": " + ex.GetType().Name + ": " + ex.Message);
            W("  stack: " + ex.StackTrace);
        }
    }

    // ------------------------------------------------------------------
    // Settings
    //   look       -> HKCU\Software\MatrixRain
    //   behaviour  -> HKCU\Control Panel\Desktop           (live values)
    //              -> HKCU\Software\Policies\...\Desktop   (survives reboot)
    // ------------------------------------------------------------------
    public class Settings
    {
        const string LookKey = "Software\\MatrixRain";
        const string LiveKey = "Control Panel\\Desktop";
        const string PolicyKey = "Software\\Policies\\Microsoft\\Windows\\Control Panel\\Desktop";

        // appearance
        public Color RainColor = Color.FromArgb(255, 0x05, 0xB7, 0xFA);
        public int Speed = 40;
        public int Density = 70;
        public int FontSize = 20;
        public int Trail = 22;
        public bool HeadGlow = false;

        // behaviour
        public int TimeoutMinutes = 5;
        public bool SecureLock = true;

        static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        static int ReadInt(RegistryKey k, string name, int fallback)
        {
            try
            {
                object o = k.GetValue(name);
                if (o == null) return fallback;
                return Convert.ToInt32(o);
            }
            catch { return fallback; }
        }

        static int ReadStrInt(RegistryKey k, string name, int fallback)
        {
            try
            {
                object o = k.GetValue(name);
                if (o == null) return fallback;
                int v;
                if (int.TryParse(o.ToString().Trim(), out v)) return v;
            }
            catch { }
            return fallback;
        }

        public static Settings Load()
        {
            Settings s = new Settings();

            try
            {
                RegistryKey k = Registry.CurrentUser.OpenSubKey(LookKey);
                if (k != null)
                {
                    s.RainColor = Color.FromArgb(ReadInt(k, "Color", s.RainColor.ToArgb()));
                    if (s.RainColor.A == 0) s.RainColor = Color.FromArgb(255, s.RainColor);
                    s.Speed = Clamp(ReadInt(k, "Speed", s.Speed), 1, 100);
                    s.Density = Clamp(ReadInt(k, "Density", s.Density), 1, 100);
                    s.FontSize = Clamp(ReadInt(k, "FontSize", s.FontSize), 8, 48);
                    s.Trail = Clamp(ReadInt(k, "Trail", s.Trail), 4, 60);
                    s.HeadGlow = ReadInt(k, "HeadGlow", s.HeadGlow ? 1 : 0) != 0;
                    k.Close();
                }
            }
            catch { }

            // read behaviour from the policy key first (it is what actually wins),
            // then fall back to the live key
            int seconds = -1;
            int secure = -1;
            try
            {
                RegistryKey p = Registry.CurrentUser.OpenSubKey(PolicyKey);
                if (p != null)
                {
                    seconds = ReadStrInt(p, "ScreenSaveTimeOut", -1);
                    secure = ReadStrInt(p, "ScreenSaverIsSecure", -1);
                    p.Close();
                }
            }
            catch { }
            try
            {
                RegistryKey l = Registry.CurrentUser.OpenSubKey(LiveKey);
                if (l != null)
                {
                    if (seconds < 0) seconds = ReadStrInt(l, "ScreenSaveTimeOut", -1);
                    if (secure < 0) secure = ReadStrInt(l, "ScreenSaverIsSecure", -1);
                    l.Close();
                }
            }
            catch { }

            if (seconds > 0) s.TimeoutMinutes = Clamp((int)Math.Round(seconds / 60.0), 1, 120);
            if (secure >= 0) s.SecureLock = (secure != 0);

            return s;
        }

        public void SaveLook()
        {
            try
            {
                RegistryKey k = Registry.CurrentUser.CreateSubKey(LookKey);
                k.SetValue("Color", RainColor.ToArgb(), RegistryValueKind.DWord);
                k.SetValue("Speed", Speed, RegistryValueKind.DWord);
                k.SetValue("Density", Density, RegistryValueKind.DWord);
                k.SetValue("FontSize", FontSize, RegistryValueKind.DWord);
                k.SetValue("Trail", Trail, RegistryValueKind.DWord);
                k.SetValue("HeadGlow", HeadGlow ? 1 : 0, RegistryValueKind.DWord);
                k.Close();
            }
            catch { }
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SystemParametersInfo(uint action, uint param, IntPtr pv, uint winIni);

        const uint SPI_SETSCREENSAVETIMEOUT = 0x000F;
        const uint SPI_SETSCREENSAVEACTIVE = 0x0011;
        const uint SPI_SETSCREENSAVESECURE = 0x0077;
        const uint SPIF_UPDATEINIFILE = 0x01;
        const uint SPIF_SENDCHANGE = 0x02;

        // Writes the screensaver wiring to BOTH the live key and the policy key.
        // The policy key is the one that survives a reboot.
        public string ApplyToWindows(string scrPath)
        {
            int seconds = TimeoutMinutes * 60;
            string report = "";

            try
            {
                RegistryKey l = Registry.CurrentUser.CreateSubKey(LiveKey);
                l.SetValue("ScreenSaveActive", "1", RegistryValueKind.String);
                l.SetValue("ScreenSaveTimeOut", seconds.ToString(), RegistryValueKind.String);
                l.SetValue("SCRNSAVE.EXE", scrPath, RegistryValueKind.String);
                l.SetValue("ScreenSaverIsSecure", SecureLock ? "1" : "0", RegistryValueKind.String);
                l.Close();
            }
            catch (Exception ex) { report += "live key: " + ex.Message + "\r\n"; }

            // Best effort only. Windows locks HKCU\Software\Policies to
            // administrators by design, so an unelevated run cannot write here.
            // That is fine: with the Personalization policies set to
            // "Not configured", the live key above is what Windows obeys and it
            // persists across reboots on its own. If the policy key IS writable
            // (elevated run), mirroring the values there just adds belt and braces.
            try
            {
                RegistryKey p = Registry.CurrentUser.OpenSubKey(PolicyKey, true);
                if (p != null)
                {
                    p.SetValue("ScreenSaveActive", "1", RegistryValueKind.String);
                    p.SetValue("ScreenSaveTimeOut", seconds.ToString(), RegistryValueKind.String);
                    p.SetValue("SCRNSAVE.EXE", scrPath, RegistryValueKind.String);
                    p.SetValue("ScreenSaverIsSecure", SecureLock ? "1" : "0", RegistryValueKind.String);
                    p.Close();
                }
            }
            catch { /* not writable without elevation - not an error */ }

            // Warn only if a policy value exists that will override what we just set.
            try
            {
                RegistryKey p = Registry.CurrentUser.OpenSubKey(PolicyKey);
                if (p != null)
                {
                    object t = p.GetValue("ScreenSaveTimeOut");
                    object x = p.GetValue("SCRNSAVE.EXE");
                    if (t != null && t.ToString().Trim() != seconds.ToString())
                        report += "A Group Policy is forcing the start delay to "
                                + t + " seconds, which overrides this setting.\r\n";
                    if (x != null && !string.Equals(x.ToString().Trim(), scrPath, StringComparison.OrdinalIgnoreCase))
                        report += "A Group Policy is forcing the screensaver to " + x + ".\r\n";
                    p.Close();
                }
            }
            catch { }

            try
            {
                SystemParametersInfo(SPI_SETSCREENSAVEACTIVE, 1, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
                SystemParametersInfo(SPI_SETSCREENSAVETIMEOUT, (uint)seconds, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
                SystemParametersInfo(SPI_SETSCREENSAVESECURE, (uint)(SecureLock ? 1 : 0), IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            }
            catch (Exception ex) { report += "apply: " + ex.Message + "\r\n"; }

            return report;
        }
    }

    // ------------------------------------------------------------------
    // Rain simulation + renderer  (unchanged from v1)
    // ------------------------------------------------------------------
    public class RainEngine : IDisposable
    {
        const int Levels = 20;

        Settings cfg;
        int width, height;
        Font font;
        int cellW, cellH, cols, rows;
        float[] head;
        float[] spd;
        int[] trail;
        float[] delay;
        bool[] alive;
        char[][] glyphs;
        Random rnd = new Random(Environment.TickCount);
        Dictionary<int, Bitmap> cache = new Dictionary<int, Bitmap>();
        static char[] charset;

        public RainEngine(Settings c, int w, int h)
        {
            cfg = c;
            width = Math.Max(1, w);
            height = Math.Max(1, h);
            Init();
        }

        static char[] BuildCharset()
        {
            List<char> l = new List<char>();
            for (int i = 0xFF66; i <= 0xFF9D; i++) l.Add((char)i);
            for (char c = '0'; c <= '9'; c++) l.Add(c);
            l.Add(':'); l.Add('.'); l.Add('='); l.Add('*');
            l.Add('+'); l.Add('-'); l.Add('<'); l.Add('>');
            return l.ToArray();
        }

        static Font PickFont(int size)
        {
            string[] names = { "MS Gothic", "MS PGothic", "Yu Gothic", "Meiryo", "Consolas", "Courier New" };
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    FontFamily ff = new FontFamily(names[i]);
                    return new Font(ff, size, FontStyle.Bold, GraphicsUnit.Pixel);
                }
                catch { }
            }
            return new Font(FontFamily.GenericMonospace, size, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        void Init()
        {
            if (charset == null) charset = BuildCharset();
            font = PickFont(cfg.FontSize);

            using (Bitmap tmp = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(tmp))
            {
                SizeF sz = g.MeasureString("\uFF71", font, PointF.Empty, StringFormat.GenericTypographic);
                cellW = (int)Math.Ceiling(sz.Width);
                if (cellW < 3) cellW = 3;
            }
            cellH = font.Height;
            if (cellH < 5) cellH = 5;

            cols = Math.Max(1, width / cellW);
            rows = Math.Max(1, height / cellH) + 1;

            head = new float[cols];
            spd = new float[cols];
            trail = new int[cols];
            delay = new float[cols];
            alive = new bool[cols];
            glyphs = new char[cols][];

            for (int c = 0; c < cols; c++)
            {
                glyphs[c] = new char[rows];
                for (int r = 0; r < rows; r++) glyphs[c][r] = charset[rnd.Next(charset.Length)];
                Spawn(c);
                head[c] = -rnd.Next(0, rows * 2);
            }
        }

        void Spawn(int c)
        {
            alive[c] = true;
            head[c] = -rnd.Next(0, (rows / 2) + 1);
            float baseSpeed = 3f + (cfg.Speed / 100f) * 45f;
            spd[c] = baseSpeed * (0.6f + (float)rnd.NextDouble() * 0.8f);
            trail[c] = Math.Max(3, (int)(cfg.Trail * (0.6 + rnd.NextDouble() * 0.8)));
            delay[c] = 0f;
        }

        static Color Fade(Color c, float t)
        {
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return Color.FromArgb(255, (int)(c.R * t), (int)(c.G * t), (int)(c.B * t));
        }

        static Color Blend(Color a, Color b, float t)
        {
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return Color.FromArgb(255,
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        Bitmap GetGlyph(char ch, int level, bool isHead)
        {
            int key = (((int)ch) << 8) | (isHead ? 255 : level);
            Bitmap bmp;
            if (cache.TryGetValue(key, out bmp)) return bmp;

            Color col = isHead
                ? Blend(cfg.RainColor, Color.White, 0.8f)
                : Fade(cfg.RainColor, (float)level / (Levels - 1));

            bmp = new Bitmap(cellW, cellH, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (SolidBrush b = new SolidBrush(col))
                    g.DrawString(ch.ToString(), font, b, new PointF(0, 0), StringFormat.GenericTypographic);
            }
            cache[key] = bmp;
            return bmp;
        }

        public void Update(float dt)
        {
            for (int c = 0; c < cols; c++)
            {
                if (!alive[c])
                {
                    delay[c] -= dt;
                    if (delay[c] <= 0f) Spawn(c);
                    continue;
                }

                head[c] += spd[c] * dt;

                if (head[c] - trail[c] > rows)
                {
                    alive[c] = false;
                    float d = (100f - cfg.Density) / 100f * 3f;
                    delay[c] = d + (float)rnd.NextDouble() * (0.3f + d);
                }
            }

            int mutations = Math.Max(1, cols / 3);
            for (int i = 0; i < mutations; i++)
                glyphs[rnd.Next(cols)][rnd.Next(rows)] = charset[rnd.Next(charset.Length)];
        }

        public void Draw(Graphics g)
        {
            g.Clear(Color.Black);
            for (int c = 0; c < cols; c++)
            {
                if (!alive[c]) continue;
                int h = (int)head[c];
                int t = trail[c];
                for (int i = 0; i < t; i++)
                {
                    int r = h - i;
                    if (r < 0) break;
                    if (r >= rows) continue;
                    float b = 1f - (float)i / t;
                    int level = (int)(b * (Levels - 1));
                    if (level < 0) level = 0;
                    if (level > Levels - 1) level = Levels - 1;
                    bool isHead = (i == 0 && cfg.HeadGlow);
                    g.DrawImageUnscaled(GetGlyph(glyphs[c][r], level, isHead), c * cellW, r * cellH);
                }
            }
        }

        public void Dispose()
        {
            foreach (KeyValuePair<int, Bitmap> kv in cache) kv.Value.Dispose();
            cache.Clear();
            if (font != null) font.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // Fullscreen / preview window
    // ------------------------------------------------------------------
    public class RainForm : Form
    {
        [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int newLong);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        const int GWL_STYLE = -16;
        const int WS_CHILD = 0x40000000;

        Settings cfg;
        RainEngine engine;
        BufferedGraphics buffer;
        BufferedGraphicsContext ctx;   // FIX: one context per form, not the shared singleton
        Graphics target;
        bool previewMode;
        Point lastMouse;
        bool mouseInit;
        Stopwatch grace = Stopwatch.StartNew();

        public RainForm(Rectangle bounds, Settings c, bool preview, IntPtr parent)
        {
            cfg = c;
            previewMode = preview;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            BackColor = Color.Black;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.UserPaint, true);

            if (preview && parent != IntPtr.Zero)
            {
                SetWindowLong(this.Handle, GWL_STYLE, GetWindowLong(this.Handle, GWL_STYLE) | WS_CHILD);
                SetParent(this.Handle, parent);
                RECT rc;
                GetClientRect(parent, out rc);
                Location = new Point(0, 0);
                Size = new Size(Math.Max(1, rc.Right - rc.Left), Math.Max(1, rc.Bottom - rc.Top));
            }
            else
            {
                // WinForms defaults StartPosition to "let Windows choose", which
                // silently discards Bounds and cascades every form onto the
                // primary monitor. Manual is what makes multi-monitor work.
                StartPosition = FormStartPosition.Manual;
                DesktopBounds = bounds;
                wanted = bounds;
                TopMost = true;
            }
        }

        public int Index;
        Rectangle wanted = Rectangle.Empty;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // belt and braces: re-assert position after the window exists
            if (!previewMode && wanted != Rectangle.Empty && DesktopBounds != wanted)
            {
                Log.W("form[" + Index + "] was moved to " + DesktopBounds + ", forcing back to " + wanted);
                DesktopBounds = wanted;
            }
            Log.W("form[" + Index + "] shown at " + DesktopBounds);
        }

        void EnsureBuffer()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0) { Log.W("form[" + Index + "] client size empty, skipping"); return; }
            if (buffer != null) return;

            target = this.CreateGraphics();
            ctx = new BufferedGraphicsContext();          // <-- the multi-monitor fix
            ctx.MaximumBuffer = new Size(w + 1, h + 1);
            buffer = ctx.Allocate(target, new Rectangle(0, 0, w, h));
            buffer.Graphics.CompositingMode = CompositingMode.SourceCopy;
            buffer.Graphics.SmoothingMode = SmoothingMode.None;
            buffer.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            buffer.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            engine = new RainEngine(cfg, w, h);
            Log.W("form[" + Index + "] buffer built " + w + "x" + h + " at " + Bounds);
        }

        int frames;

        public void Render(float dt)
        {
            EnsureBuffer();
            if (buffer == null || engine == null) return;
            engine.Update(dt);
            engine.Draw(buffer.Graphics);
            buffer.Render();
            frames++;
            if (frames == 1) Log.W("form[" + Index + "] first frame");
            if (frames == 200) Log.W("form[" + Index + "] 200 frames ok");
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (buffer != null) buffer.Render(e.Graphics);
        }

        void Quit()
        {
            if (previewMode) return;
            if (grace.ElapsedMilliseconds < 700) return;
            Application.Exit();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (previewMode) return;
            if (!mouseInit) { lastMouse = e.Location; mouseInit = true; return; }
            if (Math.Abs(e.X - lastMouse.X) > 10 || Math.Abs(e.Y - lastMouse.Y) > 10) Quit();
        }

        protected override void OnMouseDown(MouseEventArgs e) { Quit(); }
        protected override void OnKeyDown(KeyEventArgs e) { Quit(); }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (buffer != null) { buffer.Dispose(); buffer = null; }
            if (ctx != null) { ctx.Dispose(); ctx = null; }
            if (target != null) { target.Dispose(); target = null; }
            if (engine != null) { engine.Dispose(); engine = null; }
            base.OnFormClosed(e);
        }
    }

    // ------------------------------------------------------------------
    // Frame loop (unchanged from v1)
    // ------------------------------------------------------------------
    static class Loop
    {
        [StructLayout(LayoutKind.Sequential)]
        struct NativeMessage
        {
            public IntPtr hWnd;
            public uint msg;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public Point p;
        }

        [DllImport("user32.dll")]
        static extern bool PeekMessage(out NativeMessage msg, IntPtr hWnd, uint min, uint max, uint remove);

        static bool IsIdle()
        {
            NativeMessage m;
            return !PeekMessage(out m, IntPtr.Zero, 0, 0, 0);
        }

        static List<RainForm> forms;
        static Stopwatch sw = Stopwatch.StartNew();
        static double last;

        public static void Run(List<RainForm> f)
        {
            forms = f;
            Log.W("loop: running with " + f.Count + " form(s)");
            for (int i = 1; i < f.Count; i++)
            {
                try { f[i].Show(); }
                catch (Exception ex) { Log.Ex("Show[" + i + "]", ex); }
            }
            Application.Idle += OnIdle;
            last = sw.Elapsed.TotalSeconds;
            Application.Run(f[0]);
        }

        static void OnIdle(object s, EventArgs e)
        {
            while (IsIdle())
            {
                double now = sw.Elapsed.TotalSeconds;
                float dt = (float)(now - last);
                if (dt < 1.0 / 120.0) { Thread.Sleep(1); continue; }
                last = now;
                if (dt > 0.05f) dt = 0.05f;
                for (int i = 0; i < forms.Count; i++)
                {
                    try { forms[i].Render(dt); }
                    catch (Exception ex) { Log.Ex("Render[" + i + "]", ex); }
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Settings dialog (/c)
    // ------------------------------------------------------------------
    public class SettingsForm : Form
    {
        Settings cfg;
        Panel swatch;
        TextBox txtHex;
        TrackBar tbSpeed, tbDensity, tbTrail;
        NumericUpDown numFont, numMinutes;
        CheckBox chkHead, chkLock;
        Label lblPath;

        public SettingsForm(Settings c)
        {
            cfg = c;
            Text = "Matrix Rain Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(380, 460);

            int y = 14;

            Label hdr1 = new Label();
            hdr1.Text = "Appearance";
            hdr1.Font = new Font(Font, FontStyle.Bold);
            hdr1.Location = new Point(14, y);
            hdr1.AutoSize = true;
            Controls.Add(hdr1);
            y += 24;

            Label l1 = new Label();
            l1.Text = "Rain color (hex):";
            l1.Location = new Point(14, y + 4);
            l1.AutoSize = true;
            Controls.Add(l1);

            txtHex = new TextBox();
            txtHex.Location = new Point(140, y);
            txtHex.Width = 90;
            txtHex.Text = ToHex(cfg.RainColor);
            txtHex.TextChanged += new EventHandler(OnHexChanged);
            Controls.Add(txtHex);

            swatch = new Panel();
            swatch.Location = new Point(238, y);
            swatch.Size = new Size(40, 22);
            swatch.BackColor = cfg.RainColor;
            swatch.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(swatch);

            Button btnPick = new Button();
            btnPick.Text = "Pick...";
            btnPick.Location = new Point(288, y - 1);
            btnPick.Size = new Size(70, 24);
            btnPick.Click += new EventHandler(OnPick);
            Controls.Add(btnPick);

            y += 38;
            tbSpeed = AddSlider("Speed:", y, 1, 100, cfg.Speed);
            y += 50;
            tbDensity = AddSlider("Density:", y, 1, 100, cfg.Density);
            y += 50;
            tbTrail = AddSlider("Trail length:", y, 4, 60, cfg.Trail);
            y += 50;

            Label l5 = new Label();
            l5.Text = "Font size (px):";
            l5.Location = new Point(14, y + 4);
            l5.AutoSize = true;
            Controls.Add(l5);

            numFont = new NumericUpDown();
            numFont.Location = new Point(140, y);
            numFont.Width = 60;
            numFont.Minimum = 8;
            numFont.Maximum = 48;
            numFont.Value = cfg.FontSize;
            Controls.Add(numFont);

            chkHead = new CheckBox();
            chkHead.Text = "Bright leading glyph";
            chkHead.Location = new Point(216, y + 2);
            chkHead.AutoSize = true;
            chkHead.Checked = cfg.HeadGlow;
            Controls.Add(chkHead);

            y += 40;

            Label hdr2 = new Label();
            hdr2.Text = "Behaviour";
            hdr2.Font = new Font(Font, FontStyle.Bold);
            hdr2.Location = new Point(14, y);
            hdr2.AutoSize = true;
            Controls.Add(hdr2);
            y += 24;

            Label l6 = new Label();
            l6.Text = "Start after (minutes):";
            l6.Location = new Point(14, y + 4);
            l6.AutoSize = true;
            Controls.Add(l6);

            numMinutes = new NumericUpDown();
            numMinutes.Location = new Point(140, y);
            numMinutes.Width = 60;
            numMinutes.Minimum = 1;
            numMinutes.Maximum = 120;
            numMinutes.Value = Math.Min(120, Math.Max(1, cfg.TimeoutMinutes));
            Controls.Add(numMinutes);

            chkLock = new CheckBox();
            chkLock.Text = "Lock screen on wake";
            chkLock.Location = new Point(216, y + 2);
            chkLock.AutoSize = true;
            chkLock.Checked = cfg.SecureLock;
            Controls.Add(chkLock);

            y += 34;

            lblPath = new Label();
            lblPath.Text = "Installed as: " + Application.ExecutablePath;
            lblPath.Location = new Point(14, y);
            lblPath.Size = new Size(350, 30);
            lblPath.ForeColor = SystemColors.GrayText;
            Controls.Add(lblPath);

            y += 40;

            Button test = new Button();
            test.Text = "Test";
            test.Location = new Point(14, y);
            test.Size = new Size(78, 26);
            test.Click += new EventHandler(OnTest);
            Controls.Add(test);

            Button ok = new Button();
            ok.Text = "OK";
            ok.Location = new Point(196, y);
            ok.Size = new Size(78, 26);
            ok.Click += new EventHandler(OnOk);
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.Location = new Point(282, y);
            cancel.Size = new Size(78, 26);
            cancel.Click += new EventHandler(OnCancel);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        TrackBar AddSlider(string label, int y, int min, int max, int val)
        {
            Label l = new Label();
            l.Text = label;
            l.Location = new Point(14, y + 4);
            l.AutoSize = true;
            Controls.Add(l);

            TrackBar tb = new TrackBar();
            tb.Location = new Point(136, y);
            tb.Width = 226;
            tb.Minimum = min;
            tb.Maximum = max;
            tb.TickFrequency = Math.Max(1, (max - min) / 10);
            tb.Value = Math.Min(max, Math.Max(min, val));
            Controls.Add(tb);
            return tb;
        }

        static string ToHex(Color c)
        {
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        static bool TryParseHex(string s, out Color c)
        {
            c = Color.Black;
            if (s == null) return false;
            s = s.Trim();
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.Length != 6) return false;
            int v;
            if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return false;
            c = Color.FromArgb(255, (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
            return true;
        }

        void OnHexChanged(object sender, EventArgs e)
        {
            Color c;
            if (TryParseHex(txtHex.Text, out c)) swatch.BackColor = c;
        }

        void OnPick(object sender, EventArgs e)
        {
            ColorDialog cd = new ColorDialog();
            cd.FullOpen = true;
            cd.Color = swatch.BackColor;
            if (cd.ShowDialog() == DialogResult.OK)
            {
                swatch.BackColor = cd.Color;
                txtHex.Text = ToHex(cd.Color);
            }
        }

        void Harvest()
        {
            Color c;
            cfg.RainColor = TryParseHex(txtHex.Text, out c) ? c : swatch.BackColor;
            cfg.Speed = tbSpeed.Value;
            cfg.Density = tbDensity.Value;
            cfg.Trail = tbTrail.Value;
            cfg.FontSize = (int)numFont.Value;
            cfg.HeadGlow = chkHead.Checked;
            cfg.TimeoutMinutes = (int)numMinutes.Value;
            cfg.SecureLock = chkLock.Checked;
        }

        void OnTest(object sender, EventArgs e)
        {
            Harvest();
            cfg.SaveLook();
            try
            {
                Process.Start(Application.ExecutablePath, "/s");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not start the preview: " + ex.Message);
            }
        }

        void OnOk(object sender, EventArgs e)
        {
            Harvest();
            cfg.SaveLook();

            // point Windows at whichever copy is installed in System32 if it exists,
            // otherwise at wherever this executable lives
            string scr = "C:\\Windows\\System32\\MatrixRain.scr";
            try
            {
                if (!System.IO.File.Exists(scr)) scr = Application.ExecutablePath;
            }
            catch { scr = Application.ExecutablePath; }

            string problems = cfg.ApplyToWindows(scr);
            if (problems.Length > 0)
                MessageBox.Show("Settings saved, but something is overriding them:\r\n\r\n" + problems
                                + "\r\nOpen gpedit.msc and set the Personalization policies to "
                                + "\"Not configured\", then run: gpupdate /force",
                                "Matrix Rain", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
        }

        void OnCancel(object sender, EventArgs e) { Close(); }
    }

    // ------------------------------------------------------------------
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Log.Init();
            Log.W("==== start ==== args: " + string.Join(" ", args));

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // No arguments means the user launched it directly: show settings,
            // which is the standard screensaver convention.
            string mode = args.Length > 0 ? "/s" : "/c";
            IntPtr preview = IntPtr.Zero;

            if (args.Length > 0)
            {
                string a = args[0].Trim().ToLowerInvariant();
                string handle = null;
                int ci = a.IndexOf(':');
                if (ci >= 0) { handle = a.Substring(ci + 1); a = a.Substring(0, ci); }
                else if (args.Length > 1) handle = args[1];

                if (a.StartsWith("/c")) mode = "/c";
                else if (a.StartsWith("/p"))
                {
                    mode = "/p";
                    long h;
                    if (handle != null && long.TryParse(handle.Trim(), out h)) preview = new IntPtr(h);
                }
                else mode = "/s";
            }

            Settings cfg = Settings.Load();

            if (mode == "/c")
            {
                Application.Run(new SettingsForm(cfg));
                return;
            }

            List<RainForm> forms = new List<RainForm>();

            if (mode == "/p")
            {
                if (preview == IntPtr.Zero) return;
                forms.Add(new RainForm(Rectangle.Empty, cfg, true, preview));
                Loop.Run(forms);
                return;
            }

            Cursor.Hide();
            Screen[] screens = Screen.AllScreens;
            Log.W("screens reported by Windows: " + screens.Length);
            Log.W("virtual desktop: " + SystemInformation.VirtualScreen);
            for (int i = 0; i < screens.Length; i++)
            {
                Log.W("  screen[" + i + "] bounds=" + screens[i].Bounds
                      + " primary=" + screens[i].Primary + " device=" + screens[i].DeviceName);
                RainForm rf = new RainForm(screens[i].Bounds, cfg, false, IntPtr.Zero);
                rf.Index = i;
                forms.Add(rf);
            }
            if (forms.Count == 0) { Log.W("no screens!"); return; }
            Loop.Run(forms);
            Log.W("==== exit ====");
        }
    }
}
