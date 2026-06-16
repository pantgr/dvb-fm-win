namespace DvbFmWin;

/// <summary>
/// Tuner gain abstraction — SIMPLE standard rtl-sdr API for ALL tuners (stock rtl-sdr-blog driver).
///  • FC0012/FC0013 → native 5 LNA steps {-9.9,-4,7.1,17.9,19.2} dB (reg 0x13; fc0012_gains[]).
///  • R820T → 29 steps 0-49dB etc.
/// `rtlsdr_get_tuner_gains` (tenths of dB) + `rtlsdr_set_tuner_gain_mode(1)` manual + `rtlsdr_set_tuner_gain`.
/// 🔴 NO more old-dab 19-step IF gain / i2c register hacks (non-native → overload/latch, broke for 2 days).
/// </summary>
internal sealed class TunerGain
{
    public RtlSdr.TunerType Type { get; }
    public int Count { get; }
    private readonly int[] _tenthDb;   // gain in tenths of dB per step (driver table)
    private readonly IntPtr _dev;

    private TunerGain(IntPtr dev, RtlSdr.TunerType type, int[] tenthDb)
    {
        _dev = dev; Type = type;
        _tenthDb = tenthDb.Length > 0 ? tenthDb : new[] { 0 };
        Count = _tenthDb.Length;
    }

    public static TunerGain Init(IntPtr dev)
    {
        var type = (RtlSdr.TunerType)RtlSdr.rtlsdr_get_tuner_type(dev);
        RtlSdr.rtlsdr_set_tuner_gain_mode(dev, 1);          // manual tuner gain
        var tg = new TunerGain(dev, type, RtlSdr.TunerGains(dev));
        // 🔴 min-first kick = ONLY FC0012/FC0013 (gain bug, FC0012.md: ignores the first set). On the R820T
        // (& the rest) the kick is a pointless glitch (every set passes through minimum) → off.
        tg.Kick = type is RtlSdr.TunerType.FC0012 or RtlSdr.TunerType.FC0013;
        if (tg.Kick && tg.Count > 0) { RtlSdr.rtlsdr_set_tuner_gain(dev, tg._tenthDb[0]); }
        return tg;
    }

    /// <summary>Good default: moderate gain (not max = overload on a strong station).</summary>
    public int DefaultLevel => Count <= 6 ? Count / 2 : (int)(Count * 0.6);

    public float Db(int level) => _tenthDb[Math.Clamp(level, 0, Count - 1)] / 10f;

    /// <summary>min-first kick on/off — true ONLY for FC0012/FC0013 (gain bug); set in Init per tuner.</summary>
    public bool Kick { get; set; } = false;

    public int Set(int level)
    {
        level = Math.Clamp(level, 0, Count - 1);
        // FC0012 gain bug (rtl_433 PR #2417): min-first kick — set min FIRST, then target.
        // Kick=false → simple set like Android (0x0d index → one write).
        if (Kick) RtlSdr.rtlsdr_set_tuner_gain(_dev, _tenthDb[0]);
        return RtlSdr.rtlsdr_set_tuner_gain(_dev, _tenthDb[level]);
    }
}
