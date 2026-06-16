using System.Runtime.InteropServices;

namespace DvbFmWin;

/// <summary>
/// Diagnostic: writes live IQ to a file via read_async (EXACTLY the live delivery of
/// FmEngine: same sample rate / center freq / IF gain). Self-terminating after N sec.
/// We then run it offline (fmaudio) → if the pilot IS PRESENT in the capture but NOT live,
/// the realtime processing is to blame; if it's not even in the capture, the IQ data (device/USB) is to blame.
/// Mode: capture [freqMHz] [seconds] [outfile]
/// </summary>
internal static class Capture
{
    private const long Fs4 = 256_500, SampleRate = 1_026_000;

    public static int Run(double freqMHz, int seconds, string outFile, Action<string> log)
    {
        long station = (long)Math.Round(freqMHz * 1e6);
        if (RtlSdr.rtlsdr_get_device_count() == 0) { log("FAIL: no RTL device"); return 1; }
        if (RtlSdr.rtlsdr_open(out IntPtr dev, 0) != 0 || dev == IntPtr.Zero) { log("FAIL: open (busy; is the UI closed?)"); return 1; }

        RtlSdr.rtlsdr_set_sample_rate(dev, (uint)SampleRate);
        RtlSdr.rtlsdr_set_center_freq(dev, (uint)(station + Fs4));
        RtlSdr.rtlsdr_set_agc_mode(dev, 0);
        var tg = TunerGain.Init(dev);
        int idx = Math.Min(11, tg.Count - 1);   // same as live (idx 11 = 42dB)
        tg.Set(idx);
        RtlSdr.rtlsdr_reset_buffer(dev);
        log($"capture @ {freqMHz:F1}MHz → tuner {(station + Fs4) / 1e6:F4}MHz · {tg.Type} · gain idx {idx} ({tg.Db(idx):F0}dB) · {seconds}s → {outFile}");

        long targetBytes = SampleRate * 2L * seconds;
        long written = 0;
        var fs = File.Create(outFile);
        bool running = true;

        RtlSdr.ReadAsyncCallback cb = (buf, len, ctx) =>
        {
            if (!running) return;
            int n = (int)len;
            var tmp = new byte[n];
            Marshal.Copy(buf, tmp, 0, n);
            fs.Write(tmp, 0, n);
            written += n;
            if (written >= targetBytes) { running = false; RtlSdr.rtlsdr_cancel_async(dev); }
        };
        var pump = new Thread(() => RtlSdr.rtlsdr_read_async(dev, cb, IntPtr.Zero, 0, 0))
        { Name = "CapPump", IsBackground = true, Priority = ThreadPriority.Highest };
        pump.Start();
        pump.Join(seconds * 1000 + 5000);
        running = false;
        try { RtlSdr.rtlsdr_cancel_async(dev); } catch { /* ignore */ }
        fs.Flush(); fs.Close();
        log($"capture done: {written} bytes ({written / 2.0 / SampleRate:F2}s) → {outFile}");
        // NO rtlsdr_close (hayguen crash); the process exit releases the USB.
        return 0;
    }
}
