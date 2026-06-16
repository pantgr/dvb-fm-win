using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace DvbFmWin;

/// <summary>
/// TEST (option B) — FAITHFUL port of SDRangel FreqTracker `tick()` AFC:
///   • FLL freq estimate = `hub.AfcFreqHz` (= freqlockcomplex: a1=10/rate EMA of the discriminator)
///   • 2nd EMA every 50ms (gated with POWER squelch): avgΔf = α·getFreq + (1−α)·avg
///   • correction every 500ms (10 ticks) with deadband (rate/1000) + decay-cooldown anti-windup
///   • PURE digital NCO (hub.NcoTargetHz) — NO hardware retune
/// Does NOT touch the live FmEngine/LiveRadio AFC. We observe convergence/behavior.
/// Source: freqtrackersink.cpp::tick() + freqlockcomplex.cpp (read verbatim).
/// ⚠️ Difference from SDRangel: the RawHub NCO slews at 200Hz/s (instead of instant) — the log shows it.
/// Mode: afctest [freqMHz] [seconds]   (self-terminating, releases the stick)
/// </summary>
internal static class AfcTest
{
    private const long Fs4 = 256_500, SampleRate = 1_026_000;
    // 🔴 The FLL (hub.AfcFreqHz) lives at 342k, BUT the tick() deadband/decay are tied to the
    // tracker's SINK rate (SDRangel DECIMATES). We pick a low sink (~48k) → deadband
    // ~48Hz so it FOLLOWS the small wobble of the transmitter's carrier, NOT 342Hz
    // (which would ignore the small cycles). This is the "do it right, not like a fool" way.
    private const double TrackRate = 48_000.0;

    public static int Run(double freqMHz, int seconds, ILogger log)
    {
        long station = (long)Math.Round(freqMHz * 1e6);
        if (RtlSdr.rtlsdr_get_device_count() == 0) { log.LogError("no RTL device"); return 1; }
        if (RtlSdr.rtlsdr_open(out IntPtr dev, 0) != 0 || dev == IntPtr.Zero) { log.LogError("open fail (busy; is the UI closed?)"); return 1; }

        var hub = new RawHub();
        RtlSdr.rtlsdr_set_sample_rate(dev, (uint)SampleRate);
        RtlSdr.rtlsdr_set_center_freq(dev, (uint)(station + Fs4));
        RtlSdr.rtlsdr_set_agc_mode(dev, 0);
        var tg = TunerGain.Init(dev);
        int idx = Math.Min(11, tg.Count - 1); tg.Set(idx);
        RtlSdr.rtlsdr_reset_buffer(dev);
        log.LogInformation("afctest @ {F:F1}MHz · {T} · gain {Db:F0}dB · {S}s · SDRangel tick() pure-NCO · deadband={Trim:F0}Hz (follows the carrier drift) · NCO slew 200Hz/s", freqMHz, tg.Type, tg.Db(idx), seconds, TrackRate / 1000.0);

        bool running = true;
        byte[] iqTmp = new byte[262144];
        RtlSdr.ReadAsyncCallback cb = (buf, len, ctx) =>
        {
            int n = (int)len; if (n > iqTmp.Length) n = iqTmp.Length;
            Marshal.Copy(buf, iqTmp, 0, n); hub.FeedIq(iqTmp, n);
        };
        var pump = new Thread(() => RtlSdr.rtlsdr_read_async(dev, cb, IntPtr.Zero, 0, 0))
        { Name = "AfcTestPump", IsBackground = true, Priority = ThreadPriority.Highest };
        pump.Start();

        // ---- SDRangel tick() controller (faithful, freqtrackersink.cpp:351-393) ----
        const double alpha = 0.1;                  // m_alphaEMA
        const float squelchDb = -45f;              // power squelch (magsq>level → here RfDb)
        double avgDeltaFreq = 0, ncoOffset = 0, lastCorrAbs = 0;
        int tickCount = 0, corrections = 0;
        double decayAmount = Math.Max(1.0, TrackRate / (200.0 * alpha)); // sinkRate/(200·α) = 2400
        double trim = TrackRate / 1000.0;                                // deadband = 48Hz (low → follows the wobble)

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastTick = 0, lastLog = 0;
        while (running && sw.ElapsedMilliseconds < seconds * 1000L)
        {
            long now = sw.ElapsedMilliseconds;
            if (now - lastTick < 50) { Thread.Sleep(5); continue; }
            lastTick = now;
            bool squelchOpen = hub.RfDb > squelchDb;

            if (squelchOpen)
                avgDeltaFreq = alpha * hub.AfcFreqHz + (1.0 - alpha) * avgDeltaFreq;

            if (tickCount < 9) tickCount++;
            else
            {
                tickCount = 0;
                if (squelchOpen)
                {
                    if (lastCorrAbs < decayAmount)
                    {
                        lastCorrAbs = Math.Abs(avgDeltaFreq);
                        if (lastCorrAbs > trim)
                        {
                            ncoOffset = Math.Clamp(ncoOffset + avgDeltaFreq, -10000.0, 10000.0);
                            hub.NcoTargetHz = (float)ncoOffset;  // PURE NCO — no hardware retune
                            corrections++;
                            log.LogInformation("CORR #{N}: avgΔf={Avg:F0}Hz → ncoTarget={Nco:F0}Hz | rawAfc={Raw:F0} lastCorrAbs={LC:F0}", corrections, avgDeltaFreq, ncoOffset, hub.AfcFreqHz, lastCorrAbs);
                        }
                    }
                    else lastCorrAbs -= decayAmount;  // decay-cooldown (anti-windup)
                }
            }

            if (now - lastLog >= 1000)
            {
                lastLog = now;
                log.LogDebug("[{S}] rawAfc={Raw:F0}Hz avgΔf={Avg:F0}Hz ncoTarget={NcoT:F0} ncoActual={NcoA:F0}(slew) rf={Rf:F1} corr={C}",
                    squelchOpen ? "OPEN" : "SQL ", hub.AfcFreqHz, avgDeltaFreq, ncoOffset, hub.NcoTargetHz, hub.RfDb, corrections);
            }
        }

        running = false;
        RtlSdr.rtlsdr_cancel_async(dev);
        pump.Join(1500);
        log.LogInformation("afctest done: {C} corrections · final ncoTarget={Nco:F0}Hz · residual rawAfc={Raw:F0}Hz", corrections, ncoOffset, hub.AfcFreqHz);
        // NO rtlsdr_close (hayguen crash); the process exit releases the stick.
        return 0;
    }
}
