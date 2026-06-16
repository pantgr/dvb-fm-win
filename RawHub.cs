using Microsoft.Extensions.Logging;

namespace DvbFmWin;

/// <summary>
/// MASTER RAW HUB — faithful port of RawHub.kt (dvb_android). ONE owner of the
/// signal, many independent Readers on the SAME raw MPX@171k.
///   IQ u8 → rotate ×jⁿ → 49-tap Kaiser ÷3 → 342k → NCO derotate → discriminator
///   → 23-tap MPX FIR ÷2 → 171k → ring.
/// 🔴 EXACT Kotlin→C# translation (PORT_UNDERSTANDING.md) — zero "improvements".
/// </summary>
internal sealed class RawHub
{
    public const int MpxRate = 171_000;
    public const int InRate = 1_026_000;
    private const int DecimIq = 3;
    public const int DiscRate = InRate / DecimIq; // 342000
    private const int RingBits = 19;
    private const int RingSize = 1 << RingBits;     // 524288 ≈ 3.06s MPX
    private const long RingMask = RingSize - 1;

    private readonly float[] _ring = new float[RingSize];
    private readonly ILogger<RawHub>? _log;
    public RawHub(ILogger<RawHub>? log = null) => _log = log;

    // ---- AFC measure (SDRangel FLL port; irrelevant for front-end validation, faithful) ----
    private readonly double _afcA1 = 10.0 / DiscRate;
    private double _afcFHat;
    private int _afcTick;
    public volatile float AfcFreqHz;

    private readonly double _rfA = 10.0 / DiscRate;
    private double _rfEma;
    public volatile float RfDb = -99f;
    public volatile float ClipPct;            // % raw IQ samples at 0/255 (ADC saturation)
    private long _clipCnt, _clipTot;

    // ---- fine-AFC NCO @342k (at start = 0 → no-op) ----
    public volatile float NcoTargetHz;
    private double _ncoHz;
    private double _ncoPhC = 1.0, _ncoPhS = 0.0;
    private double _ncoStC = 1.0, _ncoStS = 0.0;
    private int _ncoRenorm;

    private long _wr; // total samples written (monotonic, Volatile publish)

    // ---- front-end state (pump thread only) ----
    private int _rotPhase;
    private float _prevI, _prevQ;

    // 49-tap Kaiser polyphase ÷3 (anti-spur) — EXACTLY from RawHub.kt
    private static readonly float[] IqTaps =
    {
        0.000879340f, 0.000040680f, -0.001602391f, -0.002927665f, -0.002420761f,
        0.000543347f, 0.004650067f, 0.006907212f, 0.004527723f, -0.002580178f,
        -0.010623046f, -0.013510123f, -0.006902286f, 0.007683960f, 0.021921108f,
        0.024610745f, 0.009103343f, -0.020128903f, -0.046578038f, -0.048484629f,
        -0.010663579f, 0.065027136f, 0.157134137f, 0.232564457f, 0.261656690f,
        0.232564457f, 0.157134137f, 0.065027136f, -0.010663579f, -0.048484629f,
        -0.046578038f, -0.020128903f, 0.009103343f, 0.024610745f, 0.021921108f,
        0.007683960f, -0.006902286f, -0.013510123f, -0.010623046f, -0.002580178f,
        0.004527723f, 0.006907212f, 0.004650067f, 0.000543347f, -0.002420761f,
        -0.002927665f, -0.001602391f, 0.000040680f, 0.000879340f
    };
    private readonly float[] _iqRingI = new float[IqTaps.Length];
    private readonly float[] _iqRingQ = new float[IqTaps.Length];
    private int _iqIdx, _iqCount;

    // 23-tap MPX FIR (fc=85.5k @342k, Hamming) — computed as in RawHub.kt
    private static readonly float[] MpxTaps = BuildMpxTaps();
    private readonly float[] _mpxRing = new float[MpxTaps.Length];
    private int _mpxIdx, _mpxToggle;

    private static float[] BuildMpxTaps()
    {
        var t = new float[23];
        double fc = 85_500.0 / DiscRate;
        int m = t.Length - 1;
        double sum = 0;
        for (int i = 0; i < t.Length; i++)
        {
            double x = i - m / 2.0;
            double sinc = x == 0.0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
            double w = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / m);
            t[i] = (float)(sinc * w);
            sum += t[i];
        }
        for (int i = 0; i < t.Length; i++) t[i] = (float)(t[i] / sum);
        return t;
    }

    /// <summary>Called by the pump thread with raw I/Q bytes (u8 interleaved).</summary>
    public void FeedIq(byte[] buf, int len)
    {
        int p = 0;
        while (p + 1 < len)
        {
            int bi = buf[p] & 0xFF, bq = buf[p + 1] & 0xFF;
            if (bi == 0 || bi == 255 || bq == 0 || bq == 255) _clipCnt++;   // ADC saturation count
            _clipTot++;
            float i = (bi - 127) / 128f;                     // ⚠️ 127 (RawHub.kt), not 127.5
            float q = (bq - 127) / 128f;
            p += 2;

            // rotate ×jⁿ (station from −fs/4 to center)
            float ri, rq;
            switch (_rotPhase)
            {
                case 0: ri = i; rq = q; break;
                case 1: ri = -q; rq = i; break;
                case 2: ri = -i; rq = -q; break;
                default: ri = q; rq = -i; break;
            }
            _rotPhase = (_rotPhase + 1) & 3;

            _iqRingI[_iqIdx] = ri;
            _iqRingQ[_iqIdx] = rq;
            _iqIdx = (_iqIdx + 1) % IqTaps.Length;
            if (++_iqCount < DecimIq) continue;
            _iqCount = 0;

            // 49-tap FIR at the output instants (polyphase ÷3)
            float ci = 0f, cq = 0f;
            int ik = _iqIdx;
            foreach (float c in IqTaps)
            {
                ik = ik == 0 ? IqTaps.Length - 1 : ik - 1;
                ci += c * _iqRingI[ik];
                cq += c * _iqRingQ[ik];
            }

            // NCO derotate: y = x·e^{−jφ}
            float yi = (float)(ci * _ncoPhC + cq * _ncoPhS);
            float yq = (float)(cq * _ncoPhC - ci * _ncoPhS);
            double nc = _ncoPhC * _ncoStC - _ncoPhS * _ncoStS;
            double ns = _ncoPhC * _ncoStS + _ncoPhS * _ncoStC;
            _ncoPhC = nc;
            _ncoPhS = ns;
            if (++_ncoRenorm >= 8192)
            {
                _ncoRenorm = 0;
                double mm = 1.0 / Math.Sqrt(nc * nc + ns * ns);
                _ncoPhC *= mm;
                _ncoPhS *= mm;
            }

            // quadrature discriminator @342k — full Carson band
            float re = yi * _prevI + yq * _prevQ;
            float im = yq * _prevI - yi * _prevQ;
            _prevI = yi;
            _prevQ = yq;
            float mpx342 = MathF.Atan2(im, re);

            // AFC FLL + RF level
            _afcFHat += _afcA1 * (mpx342 - _afcFHat);
            _rfEma += _rfA * ((yi * yi + yq * yq) - _rfEma);
            if (++_afcTick >= DiscRate / 20) // 50ms tick
            {
                _afcTick = 0;
                AfcFreqHz = (float)(_afcFHat * DiscRate / (2.0 * Math.PI));
                RfDb = (float)(10.0 * Math.Log10(_rfEma + 1e-12));
                if (_clipTot > 0) { ClipPct = 100f * _clipCnt / _clipTot; _clipCnt = 0; _clipTot = 0; }
                double d = Math.Clamp(NcoTargetHz - _ncoHz, -10.0, 10.0); // slew 200Hz/s
                if (d != 0.0)
                {
                    _ncoHz += d;
                    double w2 = 2.0 * Math.PI * _ncoHz / DiscRate;
                    _ncoStC = Math.Cos(w2);
                    _ncoStS = Math.Sin(w2);
                }
            }

            // MPX FIR + ÷2 → 171k raw MPX
            _mpxRing[_mpxIdx] = mpx342;
            _mpxIdx = (_mpxIdx + 1) % MpxTaps.Length;
            if (++_mpxToggle < 2) continue;
            _mpxToggle = 0;
            float mpx = 0f;
            int mk = _mpxIdx;
            foreach (float c in MpxTaps)
            {
                mk = mk == 0 ? MpxTaps.Length - 1 : mk - 1;
                mpx += c * _mpxRing[mk];
            }
            long w = _wr;
            _ring[w & RingMask] = mpx;
            Volatile.Write(ref _wr, w + 1); // publish AFTER the write
        }
    }

    public void ResetFrontEnd()
    {
        _rotPhase = 0;
        Array.Clear(_iqRingI); Array.Clear(_iqRingQ); _iqIdx = 0; _iqCount = 0;
        _prevI = 0f; _prevQ = 0f;
        Array.Clear(_mpxRing); _mpxIdx = 0; _mpxToggle = 0;
        _afcFHat = 0; _afcTick = 0; AfcFreqHz = 0f;
        _rfEma = 0; RfDb = -99f;
        NcoTargetHz = 0f; _ncoHz = 0; _ncoPhC = 1; _ncoPhS = 0; _ncoStC = 1; _ncoStS = 0; _ncoRenorm = 0;
    }

    public Reader Attach() => new(this);

    /// <summary>Independent read pointer into the shared raw MPX. One per service.</summary>
    public sealed class Reader
    {
        private readonly RawHub _h;
        private long _rd;
        public long Drops { get; private set; }

        internal Reader(RawHub h) { _h = h; _rd = Volatile.Read(ref h._wr); }

        /// <summary>Fills outBuf with up to outBuf.Length samples. 0 = nothing new.</summary>
        public int Read(float[] outBuf)
        {
            long w = Volatile.Read(ref _h._wr);
            long available = w - _rd;
            if (available <= 0) return 0;
            if (available > RingSize - 4096)
            {
                long skipTo = w - RingSize / 2;
                long skipped = skipTo - _rd;
                Drops += skipped;
                _rd = skipTo;
                available = w - _rd;
                _h._log?.LogWarning("RawHub ring overrun: skipped {Skipped} samples (totalDrops={Drops})", skipped, Drops);
            }
            int n = (int)Math.Min(available, outBuf.Length);
            for (int k = 0; k < n; k++)
                outBuf[k] = _h._ring[(_rd + k) & RingMask];
            _rd += n;
            return n;
        }
    }
}
