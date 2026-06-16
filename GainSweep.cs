namespace DvbFmWin;

/// <summary>
/// "3 scales" diagnostic (Pantelis request: try all 3, log):
///   • #2 gain index 0x0d vs value → set_tuner_gain_index(i) VERSUS set_tuner_gain(g[i]), side-by-side.
///   • #1 IQ→rf scaling → DC level (|mean − 127.5|): if large, the −127 vs −127.5 & the mean|IQ-127| metric
///         are dominated by DC, not signal.
///   • saturation → clip% (samples at 0/255). If ~constant & high across all → ADC saturated, gain is choked.
/// Self-terminating, AC-RMS = DC-removed signal power. Mode: gsweep [freqMHz].
/// </summary>
internal static class GainSweep
{
    private const long Fs4 = 256_500, SampleRate = 1_026_000;

    public static int Run(double freqMHz, Action<string> log)
    {
        if (RtlSdr.rtlsdr_get_device_count() == 0) { log("FAIL: no device"); return 1; }
        if (RtlSdr.rtlsdr_open(out IntPtr dev, 0) != 0 || dev == IntPtr.Zero) { log("FAIL: open"); return 1; }

        RtlSdr.rtlsdr_set_sample_rate(dev, (uint)SampleRate);
        RtlSdr.rtlsdr_set_center_freq(dev, (uint)((long)Math.Round(freqMHz * 1e6) + Fs4));
        int agcRc = RtlSdr.rtlsdr_set_agc_mode(dev, 0);
        var type = (RtlSdr.TunerType)RtlSdr.rtlsdr_get_tuner_type(dev);
        int modeRc = RtlSdr.rtlsdr_set_tuner_gain_mode(dev, 1);
        int[] g = RtlSdr.TunerGains(dev);
        log($"gsweep @ {freqMHz:F1}MHz · {type} · {g.Length} steps · agcRc={agcRc} modeRc={modeRc}");
        log("  columns: VALUE=set_tuner_gain(value) | INDEX=set_tuner_gain_index(0x0d) · RMS=AC signal · clip%=0/255 · DC=|mean−127.5|");

        var buf = new byte[262144];
        RtlSdr.rtlsdr_set_agc_mode(dev, 0);
        RtlSdr.rtlsdr_set_tuner_gain(dev, g[0]);   // LNA min

        // step 1: can I read demod reg 0x18 AT ALL? (try/catch — to see the exact exception)
        try
        {
            int cur = RtlSdr.rtlsdr_demod_read_reg(dev, 0, 0x18, 1);
            log($"  demod_read_reg(0,0x18) = 0x{cur & 0xFF:X2} ({cur}) — read OK");
        }
        catch (Exception ex) { log($"  demod_read_reg EXCEPTION: {ex.GetType().Name} — {ex.Message}"); RtlSdr.rtlsdr_close(dev); return 1; }

        // step 2: reg 0x18 sweep (read-modify-write style: write the value, measure clip/rms)
        log("  ADC-gain (reg 0x18, DAGC-off) sweep, AGC OFF, LNA=min:");
        foreach (int adc in new[] { 0x00, 0x08, 0x10, 0x18, 0x20, 0x30, 0x40, 0x60, 0x7f })
        {
            int rc;
            try { rc = RtlSdr.rtlsdr_demod_write_reg(dev, 0, 0x18, (ushort)adc, 1); }
            catch (Exception ex) { log($"    0x18=0x{adc:X2} WRITE EXC: {ex.GetType().Name}"); break; }
            double rms = Measure(dev, buf, () => { }, out double clip, out _);
            log($"    reg0x18=0x{adc:X2} ({adc,3}) rc={rc,3}  rms={rms,6:F2}  clip={clip,5:F2}%");
        }
        RtlSdr.rtlsdr_close(dev);
        return 0;
    }

    private static double Measure(IntPtr dev, byte[] buf, Action setGain, out double clipPct, out double dcLevel)
    {
        setGain();
        Thread.Sleep(60);
        RtlSdr.rtlsdr_reset_buffer(dev);
        for (int f = 0; f < 5; f++) RtlSdr.rtlsdr_read_sync(dev, buf, buf.Length, out _);  // flush stale
        RtlSdr.rtlsdr_read_sync(dev, buf, buf.Length, out int n);
        clipPct = 0; dcLevel = 0;
        if (n <= 0) return -1;
        int pairs = Math.Min(100_000, n / 2);
        double sumI = 0, sumQ = 0; int clip = 0;
        for (int k = 0; k < pairs; k++)
        {
            int bi = buf[2 * k] & 0xFF, bq = buf[2 * k + 1] & 0xFF;
            sumI += bi; sumQ += bq;
            if (bi == 0 || bi == 255 || bq == 0 || bq == 255) clip++;
        }
        double mI = sumI / pairs, mQ = sumQ / pairs;
        dcLevel = Math.Sqrt((mI - 127.5) * (mI - 127.5) + (mQ - 127.5) * (mQ - 127.5));
        clipPct = 100.0 * clip / pairs;
        double acc = 0;
        for (int k = 0; k < pairs; k++)
        {
            double di = (buf[2 * k] & 0xFF) - mI, dq = (buf[2 * k + 1] & 0xFF) - mQ;
            acc += di * di + dq * dq;
        }
        return Math.Sqrt(acc / pairs);   // AC RMS amplitude
    }
}
