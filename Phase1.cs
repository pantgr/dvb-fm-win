namespace DvbFmWin;

/// <summary>
/// Phase 1 — USB comms proof (blue FC0012 @ WinUSB). Diagnostic: device enum,
/// open, tuner type, gains, sample-rate/center-freq set, IQ rms (read_sync),
/// testmode counter (USB pipe lossless). Self-terminating. (Moved from
/// Program.cs into the DI core — exactly the same logic.)
/// </summary>
internal static class Phase1
{
    public static int Run(Action<string> log)
    {
        log("=== DvbFmWin Phase 1 — USB comms proof (FC0012 @ WinUSB) ===");

        uint count = RtlSdr.rtlsdr_get_device_count();
        log($"rtlsdr_get_device_count = {count}");
        if (count == 0)
        {
            log("FAIL: no RTL device. (WinUSB driver on the blue one? stick connected? busy?)");
            return 1;
        }
        for (uint i = 0; i < count; i++)
            log($"  device[{i}] = {RtlSdr.DeviceName(i)}");

        int rc = RtlSdr.rtlsdr_open(out IntPtr dev, 0);
        log($"rtlsdr_open(0) -> rc={rc}, handle={(dev == IntPtr.Zero ? "NULL" : "ok")}");
        if (rc != 0 || dev == IntPtr.Zero)
        {
            log("FAIL: open. (likely: wrong driver (BDA instead of WinUSB), or stick busy)");
            return 1;
        }

        try
        {
            var tuner = (RtlSdr.TunerType)RtlSdr.rtlsdr_get_tuner_type(dev);
            log($"tuner_type = {tuner}  (expecting FC0012)");

            int[] gains = RtlSdr.TunerGains(dev);
            log($"tuner_gains (tenths dB) = [{string.Join(", ", gains)}]  (FC0012 expected: -99,-40,71,179,192)");

            rc = RtlSdr.rtlsdr_set_sample_rate(dev, 1_026_000);
            log($"set_sample_rate(1026000) -> rc={rc}, actual = {RtlSdr.rtlsdr_get_sample_rate(dev)}");

            uint freq = 93_000_000u + 256_500u; // 93.0 MHz + fs/4 offset (DC dodge, RTL2832.md)
            rc = RtlSdr.rtlsdr_set_center_freq(dev, freq);
            log($"set_center_freq({freq}) -> rc={rc}, actual = {RtlSdr.rtlsdr_get_center_freq(dev)}");

            RtlSdr.rtlsdr_set_agc_mode(dev, 0); // RTL2832 digital AGC OFF (b70: RDS killer)
            int target = gains.Length > 0 ? gains[^1] : 192;
            int min = gains.Length > 0 ? gains[0] : -99;
            RtlSdr.SetGainManualWithKick(dev, target, min); // 🔴 min-first kick (FC0012 bug)
            log($"gain manual+kick: target {target / 10.0:F1}dB (kick {min / 10.0:F1}dB), actual = {RtlSdr.rtlsdr_get_tuner_gain(dev) / 10.0:F1}dB");

            var buf = new byte[16384]; // 32 × 512 (URB multiple)

            RtlSdr.rtlsdr_reset_buffer(dev);
            rc = RtlSdr.rtlsdr_read_sync(dev, buf, buf.Length, out int n);
            double sum = 0; int pairs = n / 2;
            for (int k = 0; k + 1 < n; k += 2)
            {
                double iI = (buf[k] - 127) / 128.0;
                double qQ = (buf[k + 1] - 127) / 128.0;
                sum += iI * iI + qQ * qQ;
            }
            double rms = pairs > 0 ? Math.Sqrt(sum / pairs) : 0;
            bool rfOk = rc == 0 && rms > 0.001;
            log($"[RF ] read_sync rc={rc} n={n}  IQ rms={rms:F4}  -> {(rfOk ? "OK (samples flowing)" : "FAIL (flat/zero)")}");

            RtlSdr.rtlsdr_set_testmode(dev, 1);
            RtlSdr.rtlsdr_reset_buffer(dev);
            rc = RtlSdr.rtlsdr_read_sync(dev, buf, buf.Length, out n);
            int errs = 0;
            byte exp = n > 0 ? buf[0] : (byte)0;
            for (int k = 0; k < n; k++) { if (buf[k] != exp) errs++; exp = (byte)(exp + 1); }
            bool usbOk = rc == 0 && n > 0 && errs == 0;
            log($"[USB] testmode counter rc={rc} n={n} first={(n > 0 ? buf[0] : -1)} mismatches={errs}  -> {(usbOk ? "OK (pipe lossless)" : $"{errs} errors")}");
            RtlSdr.rtlsdr_set_testmode(dev, 0);

            log(rfOk && usbOk
                ? "=== PHASE 1 PASS: USB communication OK ✅ ==="
                : "=== PHASE 1 WARNING: something did not pass — see above ===");
        }
        finally
        {
            RtlSdr.rtlsdr_close(dev);
            log("rtlsdr_close. done.");
        }
        return 0;
    }
}
