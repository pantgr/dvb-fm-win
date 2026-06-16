using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace DvbFmWin;

/// <summary>
/// Phase 2b LIVE — realtime FM mono radio: read_async pump → RawHub → FmDemod →
/// NAudio (WaveOut) + AFC carrier lock controller (acquire → LOCK → NCO following,
/// port of FmEngine.kt with RfDb gate instead of pilot). 🔴 Run AS pantg (audio session 0 = silent).
/// </summary>
internal sealed class LiveRadio
{
    private const long Fs4 = 256_500;        // fs/4 offset (DC dodge)
    private const long SampleRate = 1_026_000;

    public static int Run(double freqMHz, Action<string> log)
    {
        long station = (long)Math.Round(freqMHz * 1e6);

        if (RtlSdr.rtlsdr_get_device_count() == 0) { log("FAIL: no RTL device"); return 1; }
        int rc = RtlSdr.rtlsdr_open(out IntPtr dev, 0);
        if (rc != 0 || dev == IntPtr.Zero) { log($"FAIL: open rc={rc}"); return 1; }

        var hub = new RawHub();
        var demod = new FmDemod();
        var rds = new RdsDecoder(); // Phase 3b: independent RDS service on the same raw MPX

        RtlSdr.rtlsdr_set_sample_rate(dev, (uint)SampleRate);
        RtlSdr.rtlsdr_set_center_freq(dev, (uint)(station + Fs4));
        RtlSdr.rtlsdr_set_agc_mode(dev, 0); // RTL digital AGC OFF
        // 🔴 multi-tuner gain: FC0012=IF gain (reg 0x12+manual), R820T etc.=driver table. See Tuner.cs.
        var tg = TunerGain.Init(dev);
        int gainIdx = tg.DefaultLevel;
        tg.Set(gainIdx);
        float gainDbVal = tg.Db(gainIdx);                   // for the RSSI blend driver
        RtlSdr.rtlsdr_reset_buffer(dev);
        log($"live: station {freqMHz:F1}MHz → tuner {(station + Fs4) / 1e6:F4}MHz · {tg.Type} · gain {tg.Db(gainIdx):F0}dB (step {gainIdx + 1}/{tg.Count}, +/-)");

        // ---- NAudio out ----
        // WasapiOut in shared mode does the resampling AUTOMATICALLY (ResamplerDmoStream,
        // NAudio 2.1+ — markheath.net/post/wasapi-sample-rate-conversion). We feed the
        // 42750 mono directly; default render endpoint (not legacy WaveOut → BadDeviceId).
        var bwp = new BufferedWaveProvider(new WaveFormat(FmDemod.OutRate, 16, 2))
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true
        };
        WasapiOut wo;
        try
        {
            wo = new WasapiOut(AudioClientShareMode.Shared, 200);
            wo.Init(bwp);
            log($"audio init OK: WASAPI shared (auto-resample {FmDemod.OutRate}Hz mono → mixer)");
        }
        catch (Exception ex)
        {
            log($"FAIL audio init: {ex.GetType().Name}: {ex.Message}");
            RtlSdr.rtlsdr_close(dev);
            return 1;
        }

        bool running = true;

        // ---- pump: read_async (BLOCKS) on its own thread; cb → feedIq ----
        byte[] iqTmp = new byte[262144];
        RtlSdr.ReadAsyncCallback cb = (buf, len, ctx) =>
        {
            int n = (int)len;
            if (n > iqTmp.Length) n = iqTmp.Length;
            Marshal.Copy(buf, iqTmp, 0, n);
            hub.FeedIq(iqTmp, n);
        };
        var pump = new Thread(() => RtlSdr.rtlsdr_read_async(dev, cb, IntPtr.Zero, 0, 0))
        { Name = "FmPump", IsBackground = true, Priority = ThreadPriority.Highest };
        pump.Start();

        // ---- audio + AFC + status on its own thread ----
        var reader = hub.Attach();
        var audio = new Thread(() =>
        {
            var mpxBuf = new float[16384];
            var pcm = new byte[16384 * 4]; // stereo 16-bit
            double afcAvg = 0; int afcTicks = 0; bool locked = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastAfc = 0, lastStep = 0, lastLog = 0;
            while (running)
            {
                int n = reader.Read(mpxBuf);
                if (n == 0) { Thread.Sleep(5); continue; }
                demod.SignalDb = hub.RfDb - gainDbVal; // RSSI antenna-referred (blend driver)
                int pi = 0;
                for (int k = 0; k < n; k++)
                    if (demod.ProcessMpx(mpxBuf[k], out short l, out short rr))
                    {
                        pcm[pi++] = (byte)(l & 0xFF); pcm[pi++] = (byte)((l >> 8) & 0xFF);
                        pcm[pi++] = (byte)(rr & 0xFF); pcm[pi++] = (byte)((rr >> 8) & 0xFF);
                    }
                if (pi > 0) bwp.AddSamples(pcm, 0, pi);

                long now = sw.ElapsedMilliseconds;
                if (now - lastAfc < 50) continue;
                lastAfc = now;
                bool settled = now - lastStep > 2000; // settle blanking after retune
                if (settled && hub.RfDb > -45f)        // RfDb gate (mono: instead of pilot)
                {
                    afcAvg = 0.02 * hub.AfcFreqHz + 0.98 * afcAvg;
                    afcTicks++;
                }
                double est = afcTicks == 0 ? 0 : afcAvg / (1.0 - Math.Pow(0.98, afcTicks));
                double mag = Math.Abs(est);
                if (!locked)
                {
                    if (settled && afcTicks >= 20 && now - lastStep >= 3000)
                    {
                        if (mag > 300)
                        {
                            lastStep = now;
                            long trim = (long)est;
                            RtlSdr.rtlsdr_set_center_freq(dev, (uint)(station + Fs4 + Clamp50k(AccumTrim(ref _trim, trim))));
                            log($"AFC: est {(int)est}Hz → trim {_trim}Hz (acquire)");
                            afcAvg = 0; afcTicks = 0;
                        }
                        else { locked = true; log($"AFC: LOCKED ✓ trim {_trim}Hz (residual {(int)est}Hz)"); }
                    }
                }
                else
                {
                    hub.NcoTargetHz = Math.Clamp(hub.NcoTargetHz + (float)(0.01 * est), -10000f, 10000f);
                    if (mag > 2000) // carrier jumped → coarse again
                    {
                        _trim = Clamp50k(_trim + (long)hub.NcoTargetHz + (long)est);
                        hub.NcoTargetHz = 0; locked = false; afcAvg = 0; afcTicks = 0; lastStep = now;
                        RtlSdr.rtlsdr_set_center_freq(dev, (uint)(station + Fs4 + _trim));
                        log($"AFC: re-acquire → trim {_trim}Hz");
                    }
                }
                if (now - lastLog >= 2000)
                {
                    lastLog = now;
                    log($"  {(locked ? "LOCK" : "ACQ ")} gain={gainIdx + 1}/{tg.Count} ({tg.Db(gainIdx):F0}dB) · signal={hub.RfDb:F1}dB · {(demod.StereoDetected ? $"●STEREO {demod.Blend * 100:F0}%" : "MONO")} · RDS={rds.SigBlocksSec:F0}blk/s '{rds.PsMsg}' · drops={reader.Drops} · trim={_trim} nco={(int)hub.NcoTargetHz}");
                }
            }
        })
        { Name = "FmAudio", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        audio.Start();

        // ---- RDS service on its own thread: 2nd INDEPENDENT reader on the same raw MPX ----
        // isolation: stereo & RDS read the same raw MPX separately; one being slow/broken
        // does not touch the other — structurally (dvb_android b73/b90: "stereo no longer breaks RDS again").
        var rdsReader = hub.Attach();
        var rdsThread = new Thread(() =>
        {
            var rbuf = new float[16384];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastTick = 0, lastLog = 0;
            string lastPs = "", lastRt = "";
            while (running)
            {
                int n = rdsReader.Read(rbuf);
                if (n == 0) { Thread.Sleep(5); continue; }
                rds.ClockMs = sw.ElapsedMilliseconds;        // live wall-clock (PS dwell)
                for (int k = 0; k < n; k++) rds.Process(rbuf[k]);
                long now = sw.ElapsedMilliseconds;
                if (now - lastTick >= 50) { lastTick = now; rds.PsTick(); }
                // PS/RT change → log immediately (so you see station name/text as soon as it locks)
                if (rds.PsMsg != lastPs && rds.PsMsg.Trim().Length > 0)
                { lastPs = rds.PsMsg; log($"  📻 RDS PS = «{rds.PsMsg}»"); }
                if (rds.Rt != lastRt && rds.Rt.Trim().Length > 0)
                { lastRt = rds.Rt; log($"  📻 RDS RT = «{rds.Rt}»"); }
                if (now - lastLog >= 5000)
                {
                    lastLog = now;
                    log($"  RDS: PS='{rds.PsMsg}' lock={rds.SigLock:F1} blk/s={rds.SigBlocksSec:F0} drops={rdsReader.Drops}");
                }
            }
        })
        { Name = "FmRds", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        rdsThread.Start();

        // prebuffer ~500ms BEFORE Play → no underrun/jitter (the feed is bursty
        // ~8 read_async callbacks/s vs steady WASAPI consumption; we fill up first).
        for (int i = 0; i < 200 && bwp.BufferedDuration.TotalMilliseconds < 500; i++) Thread.Sleep(10);
        wo.Play();
        log($"▶ playing (prebuffer {bwp.BufferedDuration.TotalMilliseconds:F0}ms). [+]/[-] = IF gain· [q]/Enter = stop. IF step {gainIdx + 1}/{tg.Count} (LNA = RSSI auto AGC)");
        while (running)
        {
            ConsoleKeyInfo key;
            try { key = Console.ReadKey(true); }
            catch { Thread.Sleep(100); continue; } // no console (redirected) → don't crash
            if (key.Key is ConsoleKey.Enter or ConsoleKey.Q) break;
            int prevIdx = gainIdx;
            if (key.KeyChar is '+' or '=') gainIdx = Math.Min(tg.Count - 1, gainIdx + 1);
            else if (key.KeyChar is '-' or '_') gainIdx = Math.Max(0, gainIdx - 1);
            else continue;
            if (gainIdx == prevIdx) continue;               // at the limit → no spam
            float ifRfBefore = hub.RfDb;
            tg.Set(gainIdx);
            gainDbVal = tg.Db(gainIdx);
            log($"  🎚 IF gain step {gainIdx + 1}/{tg.Count} = {tg.Db(gainIdx):F0}dB · rfBEFORE={ifRfBefore:F1}dB → signal {hub.RfDb:F1}dB · {(demod.StereoDetected ? $"STEREO {demod.Blend * 100:F0}%" : "MONO")} · RDS {rds.SigBlocksSec:F0}blk/s");
        }

        running = false;
        RtlSdr.rtlsdr_cancel_async(dev);
        pump.Join(1500);
        audio.Join(1500);
        rdsThread.Join(1500);
        try { wo.Stop(); wo.Dispose(); } catch { /* ignore */ }
        // 🔴 NO rtlsdr_close — hayguen crashes (0xC0000005); process exit releases the USB.
        log("stop. done.");
        return 0;
    }

    private static long _trim;
    private static long AccumTrim(ref long t, long add) { t = Clamp50k(t + add); return t; }
    private static long Clamp50k(long v) => Math.Clamp(v, -50_000, 50_000);
}
