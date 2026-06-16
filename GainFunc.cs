using System.Runtime.InteropServices;

namespace DvbFmWin;

/// <summary>
/// FUNCTIONAL gain test — measures the metric Pantelis reads (stereo% + RDS blk/s),
/// NOT raw IQ power ([[feedback_functional_test_over_synthetic_proxy]]). Runs the full
/// chain (RawHub → FmDemod stereo + RdsDecoder + AFC) without audio out, locks, and
/// sweeps through the 5 gain steps ~7s each. Shows whether gain changes the FUNCTIONAL
/// result (overload → RDS drops, stereo drops). Headless. Mode: DvbFmWin.exe gainfunc [freqMHz]
/// </summary>
internal static class GainFunc
{
    private const long Fs4 = 256_500, SampleRate = 1_026_000;
    private static long _trim;
    private static long Clamp50k(long v) => Math.Clamp(v, -50_000, 50_000);

    public static int Run(double freqMHz, Action<string> log)
    {
        long station = (long)Math.Round(freqMHz * 1e6);
        if (RtlSdr.rtlsdr_get_device_count() == 0) { log("FAIL: no RTL device"); return 1; }
        if (RtlSdr.rtlsdr_open(out IntPtr dev, 0) != 0 || dev == IntPtr.Zero) { log("FAIL: open"); return 1; }

        var hub = new RawHub();
        var demod = new FmDemod();
        var rds = new RdsDecoder();
        RtlSdr.rtlsdr_set_sample_rate(dev, (uint)SampleRate);
        RtlSdr.rtlsdr_set_center_freq(dev, (uint)(station + Fs4));
        RtlSdr.rtlsdr_set_agc_mode(dev, 0);
        var tg = TunerGain.Init(dev);                // multi-tuner abstraction (validation)
        int step = Math.Max(1, tg.Count / 9);
        tg.Set(tg.DefaultLevel);
        float gainDbVal = tg.Db(tg.DefaultLevel);
        RtlSdr.rtlsdr_reset_buffer(dev);
        _trim = 0;
        log($"gainfunc @ {freqMHz:F1}MHz · {tg.Type} · {tg.Count} gain steps · start {tg.Db(tg.DefaultLevel):F0}dB");

        bool running = true;

        byte[] iqTmp = new byte[262144];
        RtlSdr.ReadAsyncCallback cb = (buf, len, ctx) =>
        {
            int n = (int)len; if (n > iqTmp.Length) n = iqTmp.Length;
            Marshal.Copy(buf, iqTmp, 0, n); hub.FeedIq(iqTmp, n);
        };
        var pump = new Thread(() => RtlSdr.rtlsdr_read_async(dev, cb, IntPtr.Zero, 0, 0))
        { Name = "FmPump", IsBackground = true, Priority = ThreadPriority.Highest };
        pump.Start();

        // processing thread: demod (for stereo detect) + AFC (acquire→LOCK→NCO), without audio out
        var reader = hub.Attach();
        var proc = new Thread(() =>
        {
            var mpxBuf = new float[16384];
            double afcAvg = 0; int afcTicks = 0; bool locked = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastAfc = 0, lastStep = 0;
            while (running)
            {
                int n = reader.Read(mpxBuf);
                if (n == 0) { Thread.Sleep(5); continue; }
                demod.SignalDb = hub.RfDb - gainDbVal;
                for (int k = 0; k < n; k++) demod.ProcessMpx(mpxBuf[k], out _, out _);
                long now = sw.ElapsedMilliseconds;
                if (now - lastAfc < 50) continue;
                lastAfc = now;
                bool settled = now - lastStep > 2000;
                if (settled && hub.RfDb > -45f) { afcAvg = 0.02 * hub.AfcFreqHz + 0.98 * afcAvg; afcTicks++; }
                double est = afcTicks == 0 ? 0 : afcAvg / (1.0 - Math.Pow(0.98, afcTicks));
                double mag = Math.Abs(est);
                if (!locked)
                {
                    if (settled && afcTicks >= 20 && now - lastStep >= 3000)
                    {
                        if (mag > 300) { lastStep = now; _trim = Clamp50k(_trim + (long)est); RtlSdr.rtlsdr_set_center_freq(dev, (uint)(station + Fs4 + _trim)); afcAvg = 0; afcTicks = 0; }
                        else locked = true;
                    }
                }
                else
                {
                    hub.NcoTargetHz = Math.Clamp(hub.NcoTargetHz + (float)(0.01 * est), -10000f, 10000f);
                    if (mag > 2000) { _trim = Clamp50k(_trim + (long)hub.NcoTargetHz + (long)est); hub.NcoTargetHz = 0; locked = false; afcAvg = 0; afcTicks = 0; lastStep = now; RtlSdr.rtlsdr_set_center_freq(dev, (uint)(station + Fs4 + _trim)); }
                }
            }
        }) { Name = "FmProc", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        proc.Start();

        var rdsReader = hub.Attach();
        var rdsThread = new Thread(() =>
        {
            var rbuf = new float[16384];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastTick = 0;
            while (running)
            {
                int n = rdsReader.Read(rbuf);
                if (n == 0) { Thread.Sleep(5); continue; }
                rds.ClockMs = sw.ElapsedMilliseconds;
                for (int k = 0; k < n; k++) rds.Process(rbuf[k]);
                long now = sw.ElapsedMilliseconds;
                if (now - lastTick >= 50) { lastTick = now; rds.PsTick(); }
            }
        }) { Name = "FmRds", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        rdsThread.Start();

        // warm-up: AFC lock + RDS sync (~9s) at the starting step
        Dwell(9000);
        log($"  warm-up: signal={hub.RfDb:F1}dB stereo={(demod.StereoDetected ? $"{demod.Blend * 100:F0}%" : "MONO")} RDS={rds.SigBlocksSec:F0}blk/s PS='{rds.PsMsg}'");

        // sweep through the tuner steps (manual mode already active), measure functional
        for (int lv = 0; lv < tg.Count; lv += step)
        {
            tg.Set(lv);
            gainDbVal = tg.Db(lv);
            Dwell(6000); // settle blend/RDS at the new gain
            log($"  gain {lv,2}/{tg.Count - 1} ({tg.Db(lv),4:F0}dB)  →  signal={hub.RfDb,6:F1}dB  {(demod.StereoDetected ? $"STEREO {demod.Blend * 100:F0}%" : "MONO"),-11}  RDS={rds.SigBlocksSec,4:F0}blk/s  PS='{rds.PsMsg}'  drops={reader.Drops}");
        }

        running = false;
        RtlSdr.rtlsdr_cancel_async(dev);
        pump.Join(1500); proc.Join(1500); rdsThread.Join(1500);
        // NO rtlsdr_close here — hayguen crashes (0xC0000005); the process exit frees the USB.
        log("== If stereo%/RDS CHANGE per step → the NEW IF gain works functionally. ==");
        return 0;
    }

    // Measurement window (not event-wait): I need N sec of data for blk/s to stabilize.
    private static void Dwell(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) Thread.Sleep(50);
    }
}
