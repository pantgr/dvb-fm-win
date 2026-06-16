using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;

namespace DvbFmWin;

/// <summary>Read-only snapshot of the metrics — UI rendering with real FmEngine or fake data.</summary>
internal interface IFmData
{
    double FreqMHz { get; } string Ps { get; } bool Stereo { get; } float Blend { get; }
    float SignalDb { get; } float BlkPerSec { get; } bool Locked { get; } float AfcHz { get; }
    string RtpTitle { get; } string RtpArtist { get; } string Rt { get; } string Ct { get; }
    int GainIndex { get; } int GainCount { get; } float GainDb { get; } float RdsLock { get; } float AfcResidualHz { get; }
    int VolDb { get; }
}

internal sealed class FakeFmData : IFmData
{
    public double FreqMHz => 99.2; public string Ps => "ASTRA FM";
    public bool Stereo => true; public float Blend => 0.84f;
    public float SignalDb => -13.8f; public float BlkPerSec => 46f;
    public bool Locked => true; public float AfcHz => -23f;
    public string RtpTitle => "THE ISLAND"; public string RtpArtist => "DESPINA VANDI";
    public string Rt => "HOT 40 ME TON THEMI GEORGANTA"; public string Ct => "13/06  12:14";
    public int GainIndex => 10; public int GainCount => 19; public float GainDb => 38f; public float RdsLock => 1.5f; public float AfcResidualHz => -23f;
    public int VolDb => 4;
}

internal sealed class MainForm : Form
{
    [DllImport("kernel32")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32")] private static extern bool ShowWindow(IntPtr h, int cmd);

    private readonly FmEngine _engine;
    private readonly ILogger<MainForm> _log;
    private double _freq;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 120 };
    private readonly Panel _canvas;
    private TextBox? _freqBox;
    private Panel? _memPanel;
    private Button? _scanBtn;
    private volatile bool _scanRunning;
    private readonly List<double> _memories = new();
    private static string StationFile => Path.Combine(AppContext.BaseDirectory, "last_station.txt");
    private static string MemoryFile => Path.Combine(AppContext.BaseDirectory, "memories.txt");
    private static string GainFile => Path.Combine(AppContext.BaseDirectory, "last_gain.txt");
    private static string VolFile => Path.Combine(AppContext.BaseDirectory, "last_vol.txt");

    public MainForm(FmEngine engine, ILogger<MainForm> log)
    {
        _engine = engine;
        _log = log;                    // the FmEngine logs on its own (ILogger<FmEngine>); here the UI events
        Text = "DvbFM";
        try { Icon = MakeAppIcon(); } catch { /* ignore */ }
        ClientSize = new Size(880, 596);   // compact vintage layout (TUNE inside the RDS box, controls below)
        BackColor = Palette.Bg;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        KeyPreview = true;

        ShowWindow(GetConsoleWindow(), 0);

        _canvas = new DoubleBufferedPanel { Dock = DockStyle.Fill, BackColor = Palette.Bg };
        _canvas.Paint += (s, e) => Render(e.Graphics, _engine);
        _canvas.MouseWheel += (s, e) => Tune(e.Delta > 0 ? +0.1 : -0.1); // mouse wheel = tuning
        Controls.Add(_canvas);

        // ── real buttons (definitely clickable) ──
        // uniform centered row: ◄ ► ▼ ▲  −dB +dB  VOL− VOL+ (all 56w, step 66, center 440)
        MkBtn("◄", 181, 56, () => Tune(-0.1));
        MkBtn("►", 247, 56, () => Tune(+0.1));
        MkBtn("▼", 313, 56, () => Tune(-1.0));
        MkBtn("▲", 379, 56, () => Tune(+1.0));
        MkBtn("− dB", 445, 56, GainDown);
        MkBtn("+ dB", 511, 56, GainUp);
        MkBtn("VOL−", 577, 56, VolDown);
        MkBtn("VOL+", 643, 56, VolUp);

        // ── bottom strip: manual freq + memories + scan ──
        BuildBottomStrip();
        LoadMemories();
        RebuildMemButtons();

        // 🔴 The shortcuts go in ProcessCmdKey (not KeyDown) — the arrow keys get eaten by focus-navigation.
        Shown += (_, _) => { if (!_engine.Start(_freq, LoadGain())) { Text = "DvbFM — device ERROR"; return; } _engine.SetVolDb(LoadVol()); _timer.Start(); };
        FormClosing += (_, _) => { _timer.Stop(); _engine.Stop(); SaveStation(_freq); };
        _timer.Tick += (_, _) => { _canvas.Invalidate(); LogUiValues(); };
    }

    /// <summary>DI: the frequency is set after resolve, before Application.Run (before Shown).</summary>
    public void SetFrequency(double freqMHz) => _freq = LoadStation(freqMHz);

    // 🔬 Log ALL of the UI values (IFmData) as the render reads them — to see which stay EMPTY.
    private long _lastUiLog;
    private void LogUiValues()
    {
        long now = Environment.TickCount64;
        if (now - _lastUiLog < 1000) return;
        _lastUiLog = now;
        IFmData e = _engine;
        _log.LogDebug("UIVAL freq={Freq:F1} PS='{Ps}' stereo={Stereo} blend={Blend:F2} sig={Sig:F1} blk/s={Blk:F0} locked={Locked} afcHz={Afc:F0} afcRes={AfcRes:F0} rtpTitle='{RtpT}' rtpArtist='{RtpA}' rt='{Rt}' ct='{Ct}' gainIdx={Gi} gainCount={Gc} gainDb={Gdb:F0} rdsLock={RdsLk:F1}",
            e.FreqMHz, e.Ps, e.Stereo, e.Blend, e.SignalDb, e.BlkPerSec, e.Locked, e.AfcHz, e.AfcResidualHz,
            e.RtpTitle, e.RtpArtist, e.Rt, e.Ct, e.GainIndex, e.GainCount, e.GainDb, e.RdsLock);
    }

    protected override void OnMouseWheel(MouseEventArgs e) => Tune(e.Delta > 0 ? +0.1 : -0.1);

    // 🔴 ProcessCmdKey catches the keys BEFORE focus-navigation (which eats the arrow keys in KeyDown).
    // return true = handled. All shortcuts here: arrow keys = tuning, +/− = gain.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_freqBox?.Focused == true) return base.ProcessCmdKey(ref msg, keyData); // let the manual field type
        switch (keyData)
        {
            case Keys.Left: Tune(-0.1); return true;     // fine down
            case Keys.Right: Tune(+0.1); return true;    // fine up
            case Keys.Down: Tune(-1.0); return true;     // coarse −1 MHz
            case Keys.Up: Tune(+1.0); return true;       // coarse +1 MHz
            case Keys.Oemplus: case Keys.Add: GainUp(); return true;
            case Keys.OemMinus: case Keys.Subtract: GainDown(); return true;
            case Keys.PageUp: VolUp(); return true;
            case Keys.PageDown: VolDown(); return true;
            case Keys.F12: SelfShot(); return true;   // self-screenshot → app_shot.png (I read it myself)
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void Tune(double d) => SetFreq(_freq + d);

    // Absolute frequency → dial (manual entry, memories, arrow keys all go through here).
    private void SetFreq(double abs)
    {
        _freq = Math.Clamp(Math.Round(abs * 10) / 10.0, 87.5, 108.0);
        _log.LogInformation("UI tune → {Freq:F1}MHz", _freq);
        _engine.Retune(_freq);
        SaveStation(_freq);
    }

    // gain from UI → the press is logged (did it arrive?) → engine flag → audio thread (rc+readback)
    private void GainUp() { _log.LogInformation("UI: +dB pressed"); _engine.GainUp(); SaveGain(_engine.GainIndex); }
    private void GainDown() { _log.LogInformation("UI: −dB pressed"); _engine.GainDown(); SaveGain(_engine.GainIndex); }

    // audio volume (software DSP scale) → engine + persist
    private void VolUp() { _log.LogInformation("UI: VOL+ pressed"); _engine.VolUp(); SaveVol(_engine.VolDb); }
    private void VolDown() { _log.LogInformation("UI: VOL− pressed"); _engine.VolDown(); SaveVol(_engine.VolDb); }

    private void MkBtn(string text, int x, int w, Action onClick)
    {
        var b = new VintageButton
        {
            Text = text, Left = x, Top = 432, Width = w, Height = 38,
            ForeColor = Palette.Amber, Font = new Font("Segoe UI", 12f, FontStyle.Bold), TabStop = false
        };
        b.Click += (_, _) => onClick();
        b.MouseWheel += (s, e) => Tune(e.Delta > 0 ? +0.1 : -0.1);
        Controls.Add(b); b.BringToFront();
    }

    // ── bottom strip: manual freq + ★ memory + scan, and the row of memory buttons ──
    private void BuildBottomStrip()
    {
        // centered row of controls around x=440 (like the top buttons)
        _freqBox = new TextBox
        {
            Left = 248, Top = 484, Width = 86, Height = 30, BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(20, 19, 14), ForeColor = Palette.Amber,
            Font = new Font("Consolas", 13f, FontStyle.Bold), TextAlign = HorizontalAlignment.Center
        };
        _freqBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { GoManual(); e.SuppressKeyPress = true; } };
        Controls.Add(_freqBox); _freqBox.BringToFront();

        MkSmallBtn("▶ MHz", 344, 76, 484, GoManual);        // type freq → dial
        MkSmallBtn("★ memory", 430, 96, 484, AddMemory);     // manual box → manual entry, otherwise current
        _scanBtn = MkSmallBtn("⟳ SCAN", 536, 96, 484, StartScan); // scan → fills memories

        // memories: centered block (Left 66, Width 748 → 11/row) inside the deck; bg = Palette.Bg (blend)
        _memPanel = new FlowLayoutPanel { Left = 66, Top = 522, Width = 748, Height = 56, BackColor = Palette.Bg, AutoScroll = false, WrapContents = true, FlowDirection = FlowDirection.LeftToRight };
        Controls.Add(_memPanel); _memPanel.BringToFront();
    }

    private Button MkSmallBtn(string text, int x, int w, int top, Action onClick)
    {
        var b = new VintageButton
        {
            Text = text, Left = x, Top = top, Width = w, Height = 30,
            ForeColor = Palette.Amber, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), TabStop = false
        };
        b.Click += (_, _) => onClick();
        Controls.Add(b); b.BringToFront();
        return b;
    }

    // type «92.0» → dial (manual; the person enters their favorite directly)
    private void GoManual()
    {
        if (_freqBox == null) return;
        if (double.TryParse(_freqBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f is >= 87.5 and <= 108.0)
            SetFreq(f);
        _freqBox.Text = "";
        ActiveControl = null;   // returns focus to the form → arrow keys/wheel work
    }

    // ★memory: if there is a frequency typed in the manual box → add THAT one (manual
    // entry); otherwise add the current station.
    private void AddMemory()
    {
        double f = _freq;
        if (_freqBox != null && double.TryParse(_freqBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var typed) && typed is >= 87.5 and <= 108.0)
        { f = Math.Round(typed * 10) / 10.0; _freqBox.Text = ""; ActiveControl = null; }
        if (_memories.Any(m => Math.Abs(m - f) < 0.05)) return; // already exists
        _memories.Add(f); _memories.Sort(); SaveMemories(); RebuildMemButtons();
    }

    // SCAN → scans with THE CURRENT UI gain (background thread, so the UI doesn't freeze) → fills memories
    private void StartScan()
    {
        if (_scanRunning) return;
        _scanRunning = true;
        if (_scanBtn != null) { _scanBtn.Text = "···"; _scanBtn.Enabled = false; }
        _log.LogInformation("SCAN start (UI gain)");
        var th = new Thread(() =>
        {
            List<double> found;
            try { found = _engine.ScanBand(m => _log.LogInformation("{Msg}", m)); }
            catch (Exception ex) { _log.LogWarning("SCAN err: {Err}", ex.Message); found = new List<double>(); }
            try
            {
                BeginInvoke(new Action(() =>
                {
                    int added = 0;
                    foreach (var f in found)
                        if (!_memories.Any(m => Math.Abs(m - f) < 0.05)) { _memories.Add(f); added++; }
                    _memories.Sort(); SaveMemories(); RebuildMemButtons();
                    if (_scanBtn != null) { _scanBtn.Text = "⟳ SCAN"; _scanBtn.Enabled = true; }
                    _log.LogInformation("SCAN: {Count} stations, +{Added} new memories", found.Count, added);
                    _scanRunning = false;
                }));
            }
            catch { _scanRunning = false; }
        })
        { IsBackground = true, Name = "UiScan" };
        th.Start();
    }

    private void LoadMemories()
    {
        _memories.Clear();
        try
        {
            if (File.Exists(MemoryFile))
                foreach (var line in File.ReadAllLines(MemoryFile))
                    if (double.TryParse(line.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f is >= 87.5 and <= 108.0 && !_memories.Contains(f))
                        _memories.Add(f);
            _memories.Sort();
        }
        catch { /* ignore */ }
    }
    private void SaveMemories() { try { File.WriteAllLines(MemoryFile, _memories.Select(f => f.ToString("F1", CultureInfo.InvariantCulture))); } catch { /* ignore */ } }

    // memories = buttons with the frequency; left=tune, right=fix-to-current / delete
    private void RebuildMemButtons()
    {
        if (_memPanel == null) return;
        foreach (Control c in _memPanel.Controls) c.Dispose();
        _memPanel.Controls.Clear();
        foreach (var f in _memories)
        {
            double mem = f;
            var b = new VintageButton
            {
                Text = mem.ToString("F1", CultureInfo.InvariantCulture), Width = 62, Height = 26, Margin = new Padding(2),
                ForeColor = Palette.Amber, Font = new Font("Consolas", 10f, FontStyle.Bold), TabStop = false
            };
            b.Click += (_, _) => SetFreq(mem);
            b.MouseUp += (_, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var menu = new ContextMenuStrip();
                menu.Items.Add($"Fix → {_freq:F1}", null, (_, _) => { _memories.Remove(mem); if (!_memories.Any(m => Math.Abs(m - _freq) < 0.05)) _memories.Add(_freq); _memories.Sort(); SaveMemories(); RebuildMemButtons(); });
                menu.Items.Add("Delete", null, (_, _) => { _memories.Remove(mem); SaveMemories(); RebuildMemButtons(); });
                menu.Show(b, e.Location);
            };
            _memPanel.Controls.Add(b);
        }
    }

    private static double LoadStation(double fallback)
    {
        try { if (File.Exists(StationFile) && double.TryParse(File.ReadAllText(StationFile).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f >= 87.5 && f <= 108) return f; }
        catch { /* ignore */ }
        return fallback;
    }
    private static void SaveStation(double f) { try { File.WriteAllText(StationFile, f.ToString("F1", CultureInfo.InvariantCulture)); } catch { /* ignore */ } }

    // last gain (persist on exit; -1 = does not exist → engine sets DefaultLevel)
    private static int LoadGain()
    {
        try { if (File.Exists(GainFile) && int.TryParse(File.ReadAllText(GainFile).Trim(), out var g) && g >= 0 && g < 64) return g; }
        catch { /* ignore */ }
        return -1;
    }
    private static void SaveGain(int idx) { try { File.WriteAllText(GainFile, idx.ToString()); } catch { /* ignore */ } }

    // last audio volume (dB, persist; 0 = base)
    private static int LoadVol()
    {
        try { if (File.Exists(VolFile) && int.TryParse(File.ReadAllText(VolFile).Trim(), out var v) && v is >= -20 and <= 20) return v; }
        catch { /* ignore */ }
        return 0;
    }
    private static void SaveVol(int db) { try { File.WriteAllText(VolFile, db.ToString()); } catch { /* ignore */ } }

    // app icon: amber "FM" on dark with ring — runtime (no .ico file)
    private static Icon MakeAppIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Palette.Bg);
            using (var ring = new Pen(Palette.Amber, 2f)) g.DrawEllipse(ring, 2, 2, 27, 27);
            using var f = new Font("Segoe UI", 10f, FontStyle.Bold);
            const string s = "FM"; var sz = g.MeasureString(s, f);
            using var b = new SolidBrush(Palette.Amber);
            g.DrawString(s, f, b, (32 - sz.Width) / 2f, (32 - sz.Height) / 2f);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    // Writes app.ico (256×256 PNG-in-ICO) — exe icon (Explorer/taskbar). Mode: makeicon [path].
    public static void SaveIcoFile(string path)
    {
        const int sz = 256;
        using var bmp = new Bitmap(sz, sz);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Palette.Bg);
            using (var ring = new Pen(Palette.Amber, sz * 0.055f)) g.DrawEllipse(ring, sz * 0.10f, sz * 0.10f, sz * 0.80f, sz * 0.80f);
            using var f = new Font("Segoe UI", sz * 0.30f, FontStyle.Bold, GraphicsUnit.Pixel);
            const string s = "FM"; var ssz = g.MeasureString(s, f);
            using var b = new SolidBrush(Palette.Amber);
            g.DrawString(s, f, b, (sz - ssz.Width) / 2f, (sz - ssz.Height) / 2f);
        }
        using var png = new MemoryStream();
        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        byte[] pb = png.ToArray();
        using var fs = new FileStream(path, FileMode.Create);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)1);   // ICONDIR: reserved, type=icon, count=1
        w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); // 256×256, colors, reserved
        w.Write((short)1); w.Write((short)32);                     // planes, bpp
        w.Write(pb.Length); w.Write(22);                           // bytes, offset (6+16)
        w.Write(pb);
    }

    // F12: self-photo of the live window (CopyFromScreen) → app_shot.png. The app runs in your
    // session so it sees itself (the SYSTEM agent does NOT). This way I don't work blind on the UI.
    private void SelfShot()
    {
        try
        {
            var r = Bounds;
            using var bmp = new Bitmap(r.Width, r.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            string p = Path.Combine(AppContext.BaseDirectory, "app_shot.png");
            bmp.Save(p, System.Drawing.Imaging.ImageFormat.Png);
            _log.LogInformation("📷 screenshot → {Path}", p);
        }
        catch (Exception ex) { _log.LogWarning("shot err: {Err}", ex.Message); }
    }

    public static void SavePreview(string path)
    {
        using var bmp = new Bitmap(880, 648);
        using (var g = Graphics.FromImage(bmp)) { Render(g, new FakeFmData()); DrawBottomStripPreview(g); }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    // Preview-only rendering of the bottom strip (same coordinates as the real controls) — for layout check.
    private static void DrawBottomStripPreview(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var fNav = new Font("Segoe UI", 12f, FontStyle.Bold);
        using var fBtn = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        using var fTb = new Font("Consolas", 13f, FontStyle.Bold);
        using var fMem = new Font("Consolas", 10f, FontStyle.Bold);
        // nav + gain — uniformly 56w, centered (y432)
        VintageBtn(g, 247, 432, 56, 38, "◄", fNav); VintageBtn(g, 313, 432, 56, 38, "►", fNav);
        VintageBtn(g, 379, 432, 56, 38, "▼", fNav); VintageBtn(g, 445, 432, 56, 38, "▲", fNav);
        VintageBtn(g, 511, 432, 56, 38, "− dB", fBtn); VintageBtn(g, 577, 432, 56, 38, "+ dB", fBtn);
        // manual + memory + scan — centered (y484)
        VintageBtn(g, 248, 484, 86, 30, "92.0", fTb, true);
        VintageBtn(g, 344, 484, 76, 30, "▶ MHz", fBtn);
        VintageBtn(g, 430, 484, 96, 30, "★ memory", fBtn);
        VintageBtn(g, 536, 484, 96, 30, "⟳ SCAN", fBtn);
        // memory presets — centered block (start x66, 11/row), y522
        double[] sample = { 88.6, 90.8, 91.5, 92.0, 92.6, 93.0, 93.6, 94.4, 94.7, 96.0, 96.3, 96.6, 96.9, 97.9, 99.2, 99.9 };
        int mx = 66, my = 522;
        foreach (var s in sample)
        {
            if (mx + 62 > 814) { mx = 66; my += 30; }
            VintageBtn(g, mx, my, 62, 26, s.ToString("F1", CultureInfo.InvariantCulture), fMem);
            mx += 66;
        }
    }

    // Vintage bakelite push-button: warm gradient face + top brass highlight + brass bezel + amber label.
    private static void VintageBtn(Graphics g, int x, int y, int w, int h, string t, Font f, bool inset = false)
    {
        var r = new Rectangle(x, y, w, h);
        Color top = inset ? Color.FromArgb(24, 21, 15) : Color.FromArgb(66, 58, 42);
        Color bot = inset ? Color.FromArgb(14, 12, 9) : Color.FromArgb(30, 26, 18);
        using (var grad = new LinearGradientBrush(r, top, bot, 90f)) using (var p = Rounded(r, 6)) g.FillPath(grad, p);
        if (!inset) using (var hl = new Pen(Color.FromArgb(120, 156, 124, 62))) g.DrawLine(hl, x + 6, y + 1, x + w - 6, y + 1);
        using (var pen = new Pen(Color.FromArgb(122, 98, 46))) using (var p = Rounded(r, 6)) g.DrawPath(pen, p);
        var sz = g.MeasureString(t, f);
        using var tb = new SolidBrush(Palette.Amber);
        g.DrawString(t, f, tb, x + (w - sz.Width) / 2, y + (h - sz.Height) / 2);
    }

    private static float SigNorm(float db) => Math.Clamp((db + 40f) / 40f, 0, 1);

    // ============================================================ rendering — boxed & symmetric
    private static void Render(Graphics g, IFmData e)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Palette.Bg);

        // ── vintage faceplate: warm vertical gradient + double brushed-brass frame + brand ──
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, 880, 600), Color.FromArgb(22, 17, 12), Color.FromArgb(7, 7, 9), 90f))
            g.FillRectangle(bg, 0, 0, 880, 600);
        using (var fr1 = new Pen(Color.FromArgb(132, 104, 48), 2)) g.DrawRectangle(fr1, 6, 6, 867, 587);
        using (var fr2 = new Pen(Color.FromArgb(58, 46, 22), 1)) g.DrawRectangle(fr2, 9, 9, 861, 581);

        Vu.Draw(g, 24, 24, 300, 224, SigNorm(e.SignalDb), $"{e.SignalDb:F1} dB", "SIGNAL");

        Box(g, 344, 24, 188, 104, "MHz");
        using (var fF = new Font("Consolas", 38f, FontStyle.Bold)) Centered(g, $"{e.FreqMHz:F1}", fF, Palette.Amber, 344, 188, 40);
        Box(g, 548, 24, 308, 104, "STATION");
        string ps = string.IsNullOrWhiteSpace(e.Ps) ? "····" : e.Ps.Trim();
        using (var fP = new Font("Consolas", 28f, FontStyle.Bold)) Centered(g, ps, fP, Palette.Amber, 548, 308, 50);

        Box(g, 344, 140, 512, 108, "");
        using (var fS = new Font("Segoe UI", 13f, FontStyle.Bold))
        {
            string st = e.Stereo ? $"●  STEREO  {e.Blend * 100:F0}%" : "○  MONO";
            using var sb = new SolidBrush(e.Stereo ? Palette.Red : Color.Gray);
            g.DrawString(st, fS, sb, 364, 150);
        }
        using (var fI = new Font("Consolas", 10.5f))
        {
            int y = 180;
            g.DrawString("RF", fI, Palette.DimB, 364, y);
            Bars(g, 396, y + 1, SigNorm(e.SignalDb), 8);
            g.DrawString($"{e.SignalDb,6:F1} dB", fI, Palette.DimB, 528, y);
            g.DrawString("RDS", fI, Palette.DimB, 364, y + 22);
            Bars(g, 396, y + 23, Math.Clamp(e.BlkPerSec / 46f, 0, 1), 8);
            g.DrawString($"{e.BlkPerSec,4:F0} blk/s", fI, Palette.DimB, 528, y + 22);
            string lk = e.Locked ? "✓ LOCK" : "· acq ";
            // 🔴 fixed-width fields (Consolas = monospace) → the AFC, whose width changes each tick,
            // no longer pushes the gain/vol left-right (the «shifting» bug, Pantelis 2026-06-16).
            g.DrawString($"{lk}  lk {e.RdsLock,4:F1}  AFC {e.AfcResidualHz,5:+0;-0}Hz  gain {e.GainIndex + 1,2}/{e.GainCount} · {e.GainDb,2:F0}dB  vol {e.VolDb,3:+0;-0}dB",
                fI, e.Locked ? Palette.GreenB : Palette.DimB, 364, y + 46);
        }
        TuneMeter(g, 745, 196, 92, e.AfcResidualHz, e.Locked);   // 🔵 TUNE inside the RDS display box (right side)

        Box(g, 24, 262, 832, 88, "RT+   title · artist");
        string title = e.RtpTitle.Trim(), artist = e.RtpArtist.Trim();
        using (var fT = new Font("Segoe UI Semibold", 21f, FontStyle.Bold))
            if (title.Length > 0) CenterAll(g, title, fT, Brushes.WhiteSmoke, 282);
        using (var fA = new Font("Segoe UI", 14f))
            if (artist.Length > 0) CenterAll(g, artist, fA, new SolidBrush(Palette.Amber), 318);

        Box(g, 24, 362, 596, 56, "RADIOTEXT");
        using (var fR = new Font("Consolas", 12f))
        {
            string rt = e.Rt.Trim();
            if (rt.Length > 0) { var sz = g.MeasureString(rt, fR); g.DrawString(rt, fR, Palette.DimB, 24 + (596 - sz.Width) / 2, 384); }
        }
        Box(g, 632, 362, 224, 56, "CT  clock");
        using (var fC = new Font("Consolas", 13f, FontStyle.Bold))
        {
            string ct = e.Ct.Trim(); if (ct.Length == 0) ct = "--:--";
            var sz = g.MeasureString(ct, fC); g.DrawString(ct, fC, new SolidBrush(Palette.Amber), 632 + (224 - sz.Width) / 2, 382);
        }

        // 🔵 control deck: vintage brass frame around the buttons (nav/gain/manual/memories). Interior
        // = Palette.Bg so the owner-drawn buttons (which do Clear(Palette.Bg)) blend in,
        // without flat squares → grouped, not scattered.
        var deck = new Rectangle(20, 424, 840, 156);
        using (var db = new SolidBrush(Palette.Bg)) using (var dp = Rounded(deck, 12)) g.FillPath(db, dp);
        using (var dpen = new Pen(Color.FromArgb(92, 74, 34))) using (var dp = Rounded(deck, 12)) g.DrawPath(dpen, dp);
        using (var dbez = new Pen(Color.FromArgb(70, 122, 96, 44))) using (var dp = Rounded(new Rectangle(22, 426, 836, 146), 10)) g.DrawPath(dbez, dp);
        using (var fD = new Font("Consolas", 7.5f, FontStyle.Bold))
            g.DrawString("TUNING · PRESETS", fD, new SolidBrush(Color.FromArgb(96, 76, 36)), 32, 429);
    }

    private static void Centered(Graphics g, string s, Font f, Color c, int px, int pw, int y)
    { var sz = g.MeasureString(s, f); using var b = new SolidBrush(c); g.DrawString(s, f, b, px + (pw - sz.Width) / 2, y); }
    private static void CenterAll(Graphics g, string s, Font f, Brush b, int y)
    { var sz = g.MeasureString(s, f); g.DrawString(s, f, b, (880 - sz.Width) / 2, y); }

    private static void Box(Graphics g, int x, int y, int w, int h, string label)
    {
        var r = new Rectangle(x, y, w, h);
        using (var grad = new LinearGradientBrush(r, Color.FromArgb(26, 24, 18), Color.FromArgb(16, 15, 12), 90f))
        using (var p = Rounded(r, 12)) g.FillPath(grad, p);
        using (var pen = new Pen(Color.FromArgb(70, 56, 26))) using (var p = Rounded(r, 12)) g.DrawPath(pen, p);
        using (var bez = new Pen(Color.FromArgb(90, 122, 96, 44))) using (var p3 = Rounded(new Rectangle(x + 2, y + 2, w - 4, h - 4), 10)) g.DrawPath(bez, p3); // vintage gold inner bezel
        if (label.Length > 0)
            using (var fL = new Font("Consolas", 7.5f, FontStyle.Bold))
                g.DrawString(label, fL, new SolidBrush(Color.FromArgb(96, 76, 36)), x + 12, y + 6);
    }

    private static void Bars(Graphics g, int x, int y, float v, int n)
    {
        int lit = (int)Math.Round(v * n);
        for (int i = 0; i < n; i++)
        {
            Color c = i < lit ? (i >= n - 2 ? Color.FromArgb(255, 90, 60) : Palette.Amber) : Color.FromArgb(46, 38, 20);
            using var b = new SolidBrush(c); g.FillRectangle(b, x + i * 15, y, 10, 20);
        }
    }

    private static void TuneMeter(Graphics g, int cx, int y, int halfW, float afcHz, bool locked)
    {
        using var fC = new Font("Segoe UI", 8f, FontStyle.Bold);
        var cs = g.MeasureString("TUNE", fC);
        g.DrawString("TUNE", fC, Palette.DimB, cx - cs.Width / 2, y - 34);   // higher up → doesn't overlap the needle
        using (var pen = new Pen(Color.FromArgb(82, 64, 30), 2)) g.DrawLine(pen, cx - halfW, y, cx + halfW, y);
        for (int i = -2; i <= 2; i++)
        {
            int tx = cx + i * halfW / 2;
            using var pt = new Pen(i == 0 ? Color.FromArgb(110, 210, 120) : Color.FromArgb(74, 58, 32), i == 0 ? 2f : 1f);
            g.DrawLine(pt, tx, y - 5, tx, y + 5);
        }
        float pos = Math.Clamp(afcHz / 3000f, -1, 1);
        float px = cx + pos * halfW;
        bool centered = locked && Math.Abs(pos) < 0.1f;
        Color pc = centered ? Color.FromArgb(110, 230, 130) : (Math.Abs(pos) > 0.6f ? Color.FromArgb(230, 90, 60) : Palette.Amber);
        using var pb = new SolidBrush(pc);
        g.FillPolygon(pb, new[] { new PointF(px, y - 8), new PointF(px - 6, y - 17), new PointF(px + 6, y - 17) });
    }

    private static GraphicsPath Rounded(Rectangle r, int rad)
    {
        var p = new GraphicsPath(); int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    private static class Vu
    {
        public static void Draw(Graphics g, int x, int y, int w, int h, float v, string dbText, string caption)
        {
            var face = new Rectangle(x, y, w, h);
            using (var grad = new LinearGradientBrush(face, Color.FromArgb(247, 240, 214), Color.FromArgb(218, 204, 165), 90f))
            using (var p = Rounded(face, 14)) g.FillPath(grad, p);
            using (var shine = new LinearGradientBrush(new Rectangle(x, y, w, h / 2), Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
            using (var p = Rounded(new Rectangle(x + 4, y + 4, w - 8, h / 2), 12)) g.FillPath(shine, p);
            using (var bez = new Pen(Color.FromArgb(28, 24, 16), 3)) using (var p = Rounded(face, 14)) g.DrawPath(bez, p);

            float cx = x + w / 2f, cy = y + h * 0.86f, r = h * 0.66f;
            const float aMin = 150f, aMax = 30f;
            var arc = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
            using (var pArc = new Pen(Color.FromArgb(70, 58, 40), 2)) g.DrawArc(pArc, arc, 210, 120);
            using (var pRed = new Pen(Color.FromArgb(192, 42, 30), 4)) g.DrawArc(pRed, arc, 300, 30);

            using var fT = new Font("Consolas", 8f, FontStyle.Bold);
            (float fr, string lbl)[] marks = { (0, "-40"), (.25f, "-30"), (.5f, "-20"), (.75f, "-10"), (1f, "0") };
            foreach (var (fr, lbl) in marks)
            {
                float a = (aMin + (aMax - aMin) * fr) * (float)Math.PI / 180f;
                float c1 = (float)Math.Cos(a), s1 = (float)Math.Sin(a);
                using var pT = new Pen(fr >= .75f ? Color.FromArgb(170, 40, 30) : Color.FromArgb(60, 50, 34), 2);
                g.DrawLine(pT, cx + c1 * (r - 10), cy - s1 * (r - 10), cx + c1 * r, cy - s1 * r);
                var sz = g.MeasureString(lbl, fT);
                g.DrawString(lbl, fT, Brushes.DimGray, cx + c1 * (r - 24) - sz.Width / 2, cy - s1 * (r - 24) - sz.Height / 2);
            }

            using var fC = new Font("Segoe UI", 8.5f, FontStyle.Italic | FontStyle.Bold);
            var cs = g.MeasureString(caption, fC);
            g.DrawString(caption, fC, Brushes.DimGray, cx - cs.Width / 2, cy - r * 0.42f);

            float av = (aMin + (aMax - aMin) * v) * (float)Math.PI / 180f;
            float nx = cx + (float)Math.Cos(av) * (r - 3), ny = cy - (float)Math.Sin(av) * (r - 3);
            using (var sh = new Pen(Color.FromArgb(50, 0, 0, 0), 4)) g.DrawLine(sh, cx + 1.5f, cy + 1.5f, nx + 1.5f, ny + 1.5f);
            using (var pN = new Pen(Color.FromArgb(170, 30, 20), 2.4f)) g.DrawLine(pN, cx, cy, nx, ny);
            using (var hub = new SolidBrush(Color.FromArgb(45, 38, 26))) g.FillEllipse(hub, cx - 6, cy - 6, 12, 12);

            using var fDb = new Font("Consolas", 12f, FontStyle.Bold);
            var ds = g.MeasureString(dbText, fDb);
            g.DrawString(dbText, fDb, new SolidBrush(Color.FromArgb(120, 30, 20)), cx - ds.Width / 2, y + h - 24);
        }
    }

    private static class Palette
    {
        public static readonly Color Bg = Color.FromArgb(9, 10, 12);
        public static readonly Color Amber = Color.FromArgb(255, 182, 16);
        public static readonly Brush DimB = new SolidBrush(Color.FromArgb(152, 116, 40));
        public static readonly Color Dim = Color.FromArgb(152, 116, 40);
        public static readonly Color Red = Color.FromArgb(255, 70, 70);
        public static readonly Brush GreenB = new SolidBrush(Color.FromArgb(110, 210, 120));
    }

    private sealed class DoubleBufferedPanel : Panel { public DoubleBufferedPanel() => DoubleBuffered = true; }

    // Vintage bakelite push-button (owner-drawn) — same look as the preview; keeps the Button's click wiring.
    private sealed class VintageButton : Button
    {
        public VintageButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Palette.Bg);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            int lift = ClientRectangle.Contains(PointToClient(MousePosition)) ? 12 : 0;   // hover glow
            using (var grad = new LinearGradientBrush(new Rectangle(0, 0, Width, Height), Color.FromArgb(66 + lift, 58 + lift, 42 + lift), Color.FromArgb(30 + lift, 26 + lift, 18 + lift), 90f))
            using (var p = Rounded(r, 6)) g.FillPath(grad, p);
            using (var hl = new Pen(Color.FromArgb(120, 156, 124, 62))) g.DrawLine(hl, 6, 1, Width - 6, 1);
            using (var pen = new Pen(Color.FromArgb(122, 98, 46))) using (var p = Rounded(r, 6)) g.DrawPath(pen, p);
            var sz = g.MeasureString(Text, Font);
            using var tb = new SolidBrush(Palette.Amber);
            g.DrawString(Text, Font, tb, (Width - sz.Width) / 2, (Height - sz.Height) / 2);
        }
    }
}
