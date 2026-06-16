using System.Runtime.InteropServices;

namespace DvbFmWin;

/// <summary>
/// P/Invoke wrapper for rtlsdr.dll (librtlsdr, WinUSB backend).
/// 37 exports binary-verified (dumpbin); calling convention = cdecl.
/// See RTLSDR_API.md + FC0012.md (gain bug) + RTL2832.md.
/// </summary>
internal static class RtlSdr
{
    private const string DLL = "rtlsdr";
    private const CallingConvention CC = CallingConvention.Cdecl;

    public enum TunerType { Unknown = 0, E4000 = 1, FC0012 = 2, FC0013 = 3, FC2580 = 4, R820T = 5, R828D = 6 }

    [DllImport(DLL, CallingConvention = CC)] public static extern uint rtlsdr_get_device_count();
    [DllImport(DLL, CallingConvention = CC)] public static extern IntPtr rtlsdr_get_device_name(uint index);
    [DllImport(DLL, CallingConvention = CC)] public static extern int rtlsdr_open(out IntPtr dev, uint index);
    [DllImport(DLL, CallingConvention = CC)] public static extern int rtlsdr_close(IntPtr dev);

    [DllImport(DLL, CallingConvention = CC)] public static extern int rtlsdr_set_center_freq(IntPtr dev, uint freq);
    [DllImport(DLL, CallingConvention = CC)] public static extern uint rtlsdr_get_center_freq(IntPtr dev);
    [DllImport(DLL, CallingConvention = CC)] public static extern int rtlsdr_set_sample_rate(IntPtr dev, uint rate);
    [DllImport(DLL, CallingConvention = CC)] public static extern uint rtlsdr_get_sample_rate(IntPtr dev);

    [DllImport(DLL, CallingConvention = CC)] public static extern int rtlsdr_get_tuner_type(IntPtr dev);
    [DllImport(DLL, CallingConvention = CC)] public static extern int rtlsdr_get_tuner_gains(IntPtr dev, int[]? gains);
    [DllImport(DLL, CallingConvention = CC)] public static extern int rtlsdr_set_tuner_gain_mode(IntPtr dev, int manual);
    [DllImport(DLL, CallingConvention = CC)] public static extern int rtlsdr_set_tuner_gain(IntPtr dev, int gain);
    [DllImport(DLL, CallingConvention = CC)] public static extern int rtlsdr_set_tuner_bandwidth(IntPtr dev, uint bw); // R820T: programmable IF LPF (0=auto)
    [DllImport(DLL, CallingConvention = CC)] public static extern int rtlsdr_get_tuner_gain(IntPtr dev);
    [DllImport(DLL, CallingConvention = CC)] public static extern int rtlsdr_set_tuner_gain_index(IntPtr dev, uint index); // rtl-sdr-blog: command 0x0d (like Android)
    [DllImport(DLL, CallingConvention = CC)] public static extern int rtlsdr_set_tuner_if_gain(IntPtr dev, int stage, int gain); // IF gain (FC0012 reg 0x12) — the hidden saturation knob
    // RTL2832 demod register access (rtl-sdr-blog DLL): page 0 reg 0x18 = ADC gain when DAGC OFF (0=min,0x7f=max)
    [DllImport(DLL, CallingConvention = CC)] public static extern int rtlsdr_demod_write_reg(IntPtr dev, byte page, ushort addr, ushort val, byte len);
    [DllImport(DLL, CallingConvention = CC)] public static extern int rtlsdr_demod_read_reg(IntPtr dev, byte page, ushort addr, byte len);

    // 🔴 FC0012 GAIN = native 5 LNA steps (stock rtl-sdr-blog v1.3.6: fc0012_gains[]={-99,-40,71,179,192}
    //    = -9.9/-4/7.1/17.9/19.2 dB, reg 0x13). Set via standard API: set_tuner_gain_mode(1) + set_tuner_gain.
    //    NOT the old-dab 19-step IF gain (reg 0x12, non-native → overload/latch). "auto" = separate RTL2832
    //    demod AGC (set_agc_mode), not the tuner (FC0012 tuner-AGC = no-op).

    [DllImport(DLL, CallingConvention = CC)] public static extern int rtlsdr_set_agc_mode(IntPtr dev, int on);
    [DllImport(DLL, CallingConvention = CC)] public static extern int rtlsdr_set_testmode(IntPtr dev, int on);
    [DllImport(DLL, CallingConvention = CC)] public static extern int rtlsdr_reset_buffer(IntPtr dev);
    [DllImport(DLL, CallingConvention = CC)] public static extern int rtlsdr_read_sync(IntPtr dev, byte[] buf, int len, out int n_read);

    // async streaming — read_async BLOCKS until cancel_async → its own thread.
    // 🔴 Keep a field reference to the delegate (GC pin) while async is running.
    [UnmanagedFunctionPointer(CC)]
    public delegate void ReadAsyncCallback(IntPtr buf, uint len, IntPtr ctx);
    [DllImport(DLL, CallingConvention = CC)] public static extern int rtlsdr_read_async(IntPtr dev, ReadAsyncCallback cb, IntPtr ctx, uint bufNum, uint bufLen);
    [DllImport(DLL, CallingConvention = CC)] public static extern int rtlsdr_cancel_async(IntPtr dev);

    // ---- managed helpers ----

    public static string? DeviceName(uint i)
    {
        var p = rtlsdr_get_device_name(i);
        return p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);
    }

    /// <summary>2-pass: NULL → count, then fill.</summary>
    public static int[] TunerGains(IntPtr dev)
    {
        int n = rtlsdr_get_tuner_gains(dev, null);
        if (n <= 0) return [];
        var g = new int[n];
        rtlsdr_get_tuner_gains(dev, g);
        return g;
    }

    /// <summary>
    /// 🔴 FC0012 gain bug fix (FC0012.md): min-first kick. The FC0012 ignores the
    /// gain set unless it first passes through a low state → set minTenths, then target.
    /// </summary>
    public static int SetGainManualWithKick(IntPtr dev, int tenths, int minTenths)
    {
        rtlsdr_set_tuner_gain_mode(dev, 1); // manual
        rtlsdr_set_tuner_gain(dev, minTenths); // kick
        return rtlsdr_set_tuner_gain(dev, tenths);    // target (rc: 0=ok)
    }
}
