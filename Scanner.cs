using System.Runtime.InteropServices;
using System.Text;

namespace DvbFmWin;

/// <summary>
/// FM band auto-scan 87.5-108.0 (car-radio chip method):
///   SWEEP   → chip-method valid-channel: AFCRL (|afc|&lt;MaxTuneErr) + RSSI(gate) + (SNR or strong pilot).
///   CONFIRM → AFC pull-in walk: localization (snap onto the carrier, leak→neighbor merge) +
///             carrier-lock reject (afc converges &lt;6k = real; no-lock = blip).
/// SweepAndConfirm = reusable core: headless (Run, opens device) + live UI (FmEngine.ScanBand,
/// reuses the already-open device/hub with the CURRENT UI gain).
/// </summary>
internal static class Scanner
{
    private const long Fs4 = 256_500, SampleRate = 1_026_000;
    private const float PilotThr = 0.09f;     // strong pilot = definitely a stereo station
    private const float SnrThr = 8f;          // dB FM-quieting (ultrasonic noise ratio)
    private const float MaxTuneErr = 20_000f; // FM_MAX_TUNE_ERROR (Si47xx default 20kHz)

    /// <summary>Headless: opens device, scans, writes stations.txt. Mode: scan [gainIdx] (default idx 8).</summary>
    public static int Run(Action<string> log, int gainIdx = -1)
    {
        if (RtlSdr.rtlsdr_get_device_count() == 0) { log("FAIL: no RTL device"); return 1; }
        if (RtlSdr.rtlsdr_open(out IntPtr dev, 0) != 0 || dev == IntPtr.Zero) { log("FAIL: open"); return 1; }
        RtlSdr.rtlsdr_set_sample_rate(dev, (uint)SampleRate);
        RtlSdr.rtlsdr_set_agc_mode(dev, 0);
        var tg = TunerGain.Init(dev);
        int scanGain = Math.Clamp(gainIdx >= 0 ? gainIdx : 11, 0, tg.Count - 1); tg.Set(scanGain);
        // default idx 11 = the live sweet-spot (R820T, ~Pantelis' antenna): catches weak ones (88.6) without
        // overload. (The old idx 8 = FC0012-era/too low on the R820T → missed weak ones.)
        var hub = new RawHub();
        RtlSdr.rtlsdr_set_center_freq(dev, (uint)(87_500_000L + Fs4));
        RtlSdr.rtlsdr_reset_buffer(dev);

        byte[] iqTmp = new byte[262144];
        RtlSdr.ReadAsyncCallback cb = (buf, len, ctx) =>
        { int n = (int)len; if (n > iqTmp.Length) n = iqTmp.Length; Marshal.Copy(buf, iqTmp, 0, n); hub.FeedIq(iqTmp, n); };
        var pump = new Thread(() => RtlSdr.rtlsdr_read_async(dev, cb, IntPtr.Zero, 0, 0))
        { Name = "ScanPump", IsBackground = true, Priority = ThreadPriority.Highest };
        pump.Start();

        log($"=== FM scan 87.5-108.0 · {tg.Type} @ {tg.Db(scanGain):F0}dB (idx {scanGain}) · chip-method AFCRL+RSSI+SNR ===");
        var found = SweepAndConfirm(dev, hub, tg.Db(scanGain), log);

        RtlSdr.rtlsdr_cancel_async(dev);
        pump.Join(1500);
        // NO close (hayguen crash)

        var sb = new StringBuilder();
        foreach (var s in found) sb.AppendLine(s.f.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "stations.txt"), sb.ToString()); log($"→ stations.txt ({found.Count})"); }
        catch (Exception e) { log($"save fail: {e.Message}"); }
        return 0;
    }

    /// <summary>
    /// Core scan (reusable). PRECONDITIONS: the pump is already feeding the hub; the gain is already set (gainDb = its dB).
    /// Sweeps 87.5-108 → candidates (chip-method) → confirm (AFC walk + carrier-lock). Returns confirmed.
    /// </summary>
    public static List<(double f, float rf, float pilot, bool stereo)> SweepAndConfirm(IntPtr dev, RawHub hub, float gainDb, Action<string> log)
    {
        // proc thread: SNR (total MPX vs ultrasonic 60-85k HPF) + pilot demod, continuously
        bool procRun = true;
        var reader = hub.Attach();
        var demod = new FmDemod { SignalDb = -20f };
        float[] totEma = { 0f }, ultEma = { 0f };
        var proc = new Thread(() =>
        {
            var b = new float[16384];
            float x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            while (procRun)
            {
                int n = reader.Read(b);
                if (n == 0) { Thread.Sleep(2); continue; }
                for (int k = 0; k < n; k++)
                {
                    float x = b[k];
                    totEma[0] += 0.0005f * (x * x - totEma[0]);
                    float y = 0.1305f * x - 0.261f * x1 + 0.1305f * x2 - 0.752f * y1 - 0.273f * y2; // HPF 60k
                    x2 = x1; x1 = x; y2 = y1; y1 = y;
                    ultEma[0] += 0.0005f * (y * y - ultEma[0]);
                    demod.ProcessMpx(x, out _, out _);
                }
            }
        }) { Name = "ScanProc", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        proc.Start();

        // ---- SWEEP ----
        var sweep = new List<(double f, float p, float rf, float snr, float afc)>();
        Dwell(600);
        for (double f = 87.5; f <= 108.05; f += 0.1)
        {
            double fr = Math.Round(f, 1);
            RtlSdr.rtlsdr_set_center_freq(dev, (uint)((long)Math.Round(fr * 1e6) + Fs4));
            Dwell(350);
            float snr = 10f * MathF.Log10((totEma[0] + 1e-12f) / (ultEma[0] + 1e-12f));
            sweep.Add((fr, demod.PilotLevel, hub.RfDb, snr, hub.AfcFreqHz));
        }
        var rfs = sweep.Select(x => x.rf).OrderBy(x => x).ToArray();
        float floor = rfs[rfs.Length / 4];
        float rfThr = floor + 4f;
        log($"noise floor ≈ {floor:F1}dB · RfDb gate {rfThr:F1}dB");

        // chip-method valid-channel: carrier on-channel (AFCRL) AND RSSI AND (SNR or pilot)
        var cand = new List<(double f, float p, float rf, float snr, float afc)>();
        for (int i = 0; i < sweep.Count; i++)
        {
            var s = sweep[i];
            bool valid = MathF.Abs(s.afc) < MaxTuneErr && s.rf > rfThr && (s.snr > SnrThr || s.p > PilotThr);
            if (!valid) continue;
            var rec = (s.f, s.p, s.rf, s.snr, s.afc);
            if (cand.Count > 0 && s.f - cand[^1].f < 0.25) { if (s.rf > cand[^1].rf) cand[^1] = rec; }
            else cand.Add(rec);
        }

        // ---- CONFIRM: AFC pull-in walk → localization + carrier-lock reject ----
        log($"--- confirm: AFC walk → localization + merge on {cand.Count} candidates ---");
        var creader = hub.Attach();
        var cbuf = new float[16384];
        var confirmed = new List<(double f, float rf, float pilot, bool stereo)>();
        foreach (var c in cand)
        {
            long baseHz = (long)Math.Round(c.f * 1e6);
            long trim = 0;
            RtlSdr.rtlsdr_set_center_freq(dev, (uint)(baseHz + Fs4));
            while (creader.Read(cbuf) > 0) { }            // drain stale
            var cdemod = new FmDemod { SignalDb = -20f };
            var sw = System.Diagnostics.Stopwatch.StartNew(); long lastWalk = 0;
            while (sw.ElapsedMilliseconds < 1800)         // AFC walk: pull center onto the carrier (±120k)
            {
                int n = creader.Read(cbuf); if (n == 0) { Thread.Sleep(2); continue; }
                cdemod.SignalDb = hub.RfDb - gainDb;
                for (int k = 0; k < n; k++) cdemod.ProcessMpx(cbuf[k], out _, out _);
                long now = sw.ElapsedMilliseconds;
                if (now - lastWalk >= 300 && now > 300)
                {
                    lastWalk = now; long off = (long)hub.AfcFreqHz;
                    if (Math.Abs(off) > 2500) { trim = Math.Clamp(trim + off, -120_000, 120_000); RtlSdr.rtlsdr_set_center_freq(dev, (uint)(baseHz + Fs4 + trim)); }
                }
            }
            double exactF = Math.Round((c.f + trim / 1e6) * 10) / 10.0;
            bool stereo = cdemod.StereoDetected;
            float afc = hub.AfcFreqHz;
            // the AFC LOCKED onto a carrier (converges <6k) = REAL; no-lock (e.g. blip) = out.
            // UNIVERSAL: no RDS (slow), no stereo (optional) — EVERYONE has a carrier.
            bool locked = MathF.Abs(afc) < 6000f;
            log($"  {c.f,5:F1}→{exactF,5:F1}  pilot={cdemod.PilotLevel:F3}  afc={afc,6:F0}  trim={trim,7}  {(stereo ? "STEREO" : "mono")}  {(locked ? "✓ lock" : "✗ no-lock")}");
            if (locked) AddOrMerge(confirmed, exactF, c.rf, cdemod.PilotLevel, stereo);
        }
        procRun = false; proc.Join(1500);
        log($"=== CONFIRMED {confirmed.Count} stations (out of {cand.Count} candidates) ===");
        return confirmed;
    }

    // dedup-merge: same freq (<0.15) → keep the strongest RfDb (a leak that walked merges with the neighbor)
    private static void AddOrMerge(List<(double f, float rf, float pilot, bool stereo)> list, double f, float rf, float pilot, bool stereo)
    {
        int di = list.FindIndex(x => Math.Abs(x.f - f) < 0.15);
        if (di >= 0) { if (rf > list[di].rf) list[di] = (f, rf, pilot, stereo); }
        else list.Add((f, rf, pilot, stereo));
    }

    private static void Dwell(int ms) { var sw = System.Diagnostics.Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) Thread.Sleep(20); }
}
