using System.Text;
using Microsoft.Extensions.Logging;

namespace DvbFmWin;

/// <summary>
/// RDS decoder — EXACT port of RdsDecoder.kt (dvb_android v13, the one solved after 43+ builds).
/// 🔴🔴 piece of art — EVERY constant/syndrome/sequence is measured. Zero "improvement".
/// Chain (redsea/SAA6588): MPX@171k → mix 57k=fs/3 (3-phase LUT) → [1,2,3,2,1]÷9 → 57k →
///  53-tap LPF ±2.4k → Costas (gear-shift) + FFC FFT 32768 → I-arm ÷3 → 19k → biphase matched
///  → 16 BitPaths (phase+biphase ambiguity) → differential → 26-bit blocks/syndromes → FEC
///  chip-emu (367 syndromes, ERDA/B/C/D) → flywheel 32 / bit-slip ±1 → groups 0A/2A/2B/3A/RT+/4A.
/// Validate offline (gen_test_mpx.py + redsea) BEFORE a real signal.
/// </summary>
internal sealed class RdsDecoder
{
    private const int POLY = 0x5B9;
    // 🔴 build-67: correct syndromes for the shift-register computation (NOT the textbook
    // 0x3D8…). validated: synthetic → 79/80 groups, BER=0. With the old ones: 0 blocks EVER.
    private const int SYN_A = 0x17F, SYN_B = 0x00E, SYN_C = 0x12F, SYN_C2 = 0x078, SYN_D = 0x297;

    private static readonly float[] MIX_C = { 1f, -0.5f, -0.5f };
    private static readonly float[] MIX_S = { 0f, -0.8660254f, 0.8660254f };

    private const double BB_RATE = 57_000.0;
    private const float COSTAS_A_ACQ = 0.008f, COSTAS_A_TRK = 0.002f;
    private const float LOCK_UP = 1.25f, LOCK_DOWN = 1.08f; // FC0012 lq ceiling ~1.3
    private const float COSTAS_B = 1e-6f;
    private static readonly float FREQ_MAX = (float)(2.0 * Math.PI * 450.0 / BB_RATE);
    private const int FFC_DEC = 8, FFC_N = 32768;
    private const float FFC_GATE = 10f;
    private static readonly float TWO_PI = (float)(2.0 * Math.PI);
    private static readonly float PI_F = (float)Math.PI;

    private static readonly Dictionary<int, int> SYNPOS = new()
    { { SYN_A, 0 }, { SYN_B, 1 }, { SYN_C, 2 }, { SYN_C2, 2 }, { SYN_D, 3 } };
    private static readonly int[] NEXTPOS = { 1, 2, 3, 0 };
    private const int FLY_MAX = 32;

    private readonly ILogger<RdsDecoder>? _log;
    public RdsDecoder(ILogger<RdsDecoder>? log = null) => _log = log;

    // ---- outputs (volatile-ish: read from the UI thread) ----
    public volatile string Ps = "";
    public volatile string Rt = "";
    public volatile string PsMsg = "";   // current PS step (follows the scroll)
    public volatile string Ct = "";
    public volatile string RtpTitle = "";
    public volatile string RtpArtist = "";

    // ---- PS text layer (redsea sequential_length) ----
    private readonly string?[] _psBuf = new string?[4];
    private int _psSeqLen, _psPrevAddr = -1;
    private string _psPend = "";
    private bool _psPendOk, _psPendClean;
    private long _psPendMs, _psMsgMs;
    private int _psChainCls;
    private BitPath? _textOwner; // b119: ONE feeder of the text

    // ---- RT (cumulative, AN243 Table 4) ----
    private readonly string?[] _rtBuf = new string?[16];
    private int _rtFlag = -1;

    // ---- RT+ (ODA AID 0x4BD7) ----
    private int _rtpAppGt = -1, _rtpToggle = -1;

    // ---- mixer + [1,2,3,2,1] decimator ----
    private int _mixPhase, _triCnt;
    private float _d1I, _d2I, _d3I, _d4I, _d1Q, _d2Q, _d3Q, _d4Q;

    // 53-tap LPF @57k (fc=4200 transition midpoint 2.4k→6k)
    private static readonly float[] BbTaps = BuildBbTaps();
    private readonly float[] _bbRingI = new float[BbTaps.Length];
    private readonly float[] _bbRingQ = new float[BbTaps.Length];
    private int _bbIdx;

    // Costas
    private float _w, _freqInt, _costasA = COSTAS_A_ACQ;

    // FFC
    private int _fdN;
    private readonly float[] _fcBufI = new float[FFC_N];
    private readonly float[] _fcBufQ = new float[FFC_N];
    private int _fcIdx;
    private readonly float[] _fftRe = new float[FFC_N];
    private readonly float[] _fftIm = new float[FFC_N];
    private static readonly float[] Hann = BuildHann();
    private bool _ffcApplied;

    // lock quality
    private float _emaI2, _emaQ2;

    // ÷3 → 19k
    private float _dAcc;
    private int _dCount;

    // bit clock @19k (carrier-slaved)
    private long _tick;
    private double _slotNco;
    private long _lastSlot;
    private readonly float[] _ring = new float[32];
    private int _ringIdx;

    // matched filter (biphase ≈ half a sine cycle over 16 samples)
    private static readonly float[] MfW = BuildMfW();

    // FEC chip-emu (367 syndromes, ERDA/B(≤2)/C(≤5)) — SAA6588 Table 14
    private static readonly Dictionary<int, (long pat, int cls)> FecTable = BuildFecTable();

    private readonly BitPath[] _paths = BuildPaths();

    // diagnostics
    private int _dbgGoodBlocks, _dbgBadBlocks, _dbgGroups, _dbgFec, _dbgSlips, _dbgAHits;
    private float _emaPeak;
    private long _dbgBits;

    public volatile float SigLock;
    public volatile float SigBlocksSec;
    private int _sigLastGood;

    private static float[] BuildBbTaps()
    {
        var t = new float[53];
        double fc = 4200.0 / BB_RATE;
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

    private static float[] BuildHann()
    {
        var h = new float[FFC_N];
        for (int i = 0; i < FFC_N; i++) h[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (FFC_N - 1)));
        return h;
    }

    private static float[] BuildMfW()
    {
        var w = new float[16];
        for (int i = 0; i < 16; i++) w[i] = (float)Math.Sin(Math.PI * (i + 0.5) / 8.0);
        return w;
    }

    private static BitPath[] BuildPaths()
    {
        var p = new BitPath[16];
        for (int i = 0; i < 16; i++) p[i] = new BitPath();
        return p;
    }

    // (26,16) linear: error pattern = lookup of syn(error). Corrects ONLY 1-5 bit bursts.
    private static Dictionary<int, (long, int)> BuildFecTable()
    {
        var t = new Dictionary<int, (long, int)>();
        for (int L = 1; L <= 5; L++)
        {
            var pats = new List<long>();
            if (L == 1) pats.Add(1L);
            else
                for (int inner = 0; inner < (1 << (L - 2)); inner++)
                    pats.Add((1L << (L - 1)) | 1L | ((long)inner << 1));
            int cls = L <= 2 ? 1 : 2;
            foreach (long p in pats)
                for (int pos = 0; pos <= 26 - L; pos++)
                {
                    int s = Syndrome(p << pos);
                    if (!t.ContainsKey(s)) t[s] = (p << pos, cls);
                }
        }
        return t;
    }

    private static int Syndrome(long vector)
    {
        int reg = 0;
        for (int k = 25; k >= 0; k--)
        {
            reg = (reg << 1) | (int)((vector >> k) & 1L);
            if ((reg & 0x400) != 0) reg ^= POLY;
        }
        for (int k = 10; k >= 1; k--)
        {
            reg <<= 1;
            if ((reg & 0x400) != 0) reg ^= POLY;
        }
        return reg & 0x3FF;
    }

    /// <summary>b95 NWSY (SAA6588): restart sync for zap — only decoder/text, the Costas holds.</summary>
    public void ResetSync()
    {
        Ps = ""; Rt = "";
        Array.Clear(_psBuf); _psSeqLen = 0; _psPrevAddr = -1;
        PsMsg = ""; _psPend = ""; _psPendOk = false; _psPendMs = 0; _psMsgMs = 0;
        Array.Clear(_rtBuf); _rtFlag = -1;
        Ct = ""; _rtpAppGt = -1; _rtpToggle = -1; RtpTitle = ""; RtpArtist = "";
        foreach (var p in _paths) p.Reset();
        _textOwner = null;
        _ffcApplied = false; _fcIdx = 0; _fdN = 0;
    }

    /// <summary>Called per MPX sample @171k. (phi is ignored — our own tracking.)</summary>
    public void Process(float mpx)
    {
        // mix ×e^{-j2πn/3}: the 57k comes down to DC
        float bi = mpx * MIX_C[_mixPhase];
        float bq = mpx * MIX_S[_mixPhase];
        if (++_mixPhase == 3) _mixPhase = 0;

        // [1,2,3,2,1]/9 + ÷3 → 57k
        float yi = 0f, yq = 0f;
        bool due = ++_triCnt == 3;
        if (due)
        {
            _triCnt = 0;
            yi = (bi + 2f * _d1I + 3f * _d2I + 2f * _d3I + _d4I) * (1f / 9f);
            yq = (bq + 2f * _d1Q + 3f * _d2Q + 2f * _d3Q + _d4Q) * (1f / 9f);
        }
        _d4I = _d3I; _d3I = _d2I; _d2I = _d1I; _d1I = bi;
        _d4Q = _d3Q; _d3Q = _d2Q; _d2Q = _d1Q; _d1Q = bq;
        if (!due) return;

        // 53-tap LPF @57k — here the L−R splatter dies BEFORE the Costas
        _bbRingI[_bbIdx] = yi; _bbRingQ[_bbIdx] = yq;
        _bbIdx = (_bbIdx + 1) % BbTaps.Length;
        float fI = 0f, fQ = 0f;
        int k = _bbIdx;
        foreach (float c in BbTaps)
        {
            k = k == 0 ? BbTaps.Length - 1 : k - 1;
            fI += c * _bbRingI[k];
            fQ += c * _bbRingQ[k];
        }

        // Costas derotate
        float cw = MathF.Cos(_w), sw = MathF.Sin(_w);
        float di = fI * cw + fQ * sw;
        float dq = fQ * cw - fI * sw;
        _emaI2 += 1e-4f * (di * di - _emaI2);
        _emaQ2 += 1e-4f * (dq * dq - _emaQ2);
        float lq = _emaI2 / (_emaQ2 + 1e-12f);
        float e = (di >= 0f ? 1f : -1f) * dq;
        if (lq >= 1.1f) _freqInt = Math.Clamp(_freqInt + COSTAS_B * e, -FREQ_MAX, FREQ_MAX);
        _w += _freqInt + _costasA * e;
        if (_w > PI_F) _w -= TWO_PI; else if (_w < -PI_F) _w += TWO_PI;
        if (_costasA == COSTAS_A_ACQ) { if (lq > LOCK_UP) _costasA = COSTAS_A_TRK; }
        else if (lq < LOCK_DOWN) _costasA = COSTAS_A_ACQ;

        // FFC: every 8th, z² @7125 → FFT 32768
        if (++_fdN == FFC_DEC)
        {
            _fdN = 0;
            _fcBufI[_fcIdx] = fI * fI - fQ * fQ;
            _fcBufQ[_fcIdx] = 2f * fI * fQ;
            if (++_fcIdx == FFC_N)
            {
                _fcIdx = 0;
                if (lq < 1.2f) FfcFromFft();
            }
        }

        // ÷3 → 19k
        _dAcc += di;
        if (++_dCount < 3) return;
        _dCount = 0;
        OnTick19k(_dAcc * (1f / 3f));
        _dAcc = 0f;
    }

    private void OnTick19k(float x)
    {
        _ring[_ringIdx] = x;
        _ringIdx = (_ringIdx + 1) & 31;
        _tick++;

        // trailing biphase matched filter
        float soft = 0f;
        for (int j = 0; j < 16; j++) soft += MfW[j] * _ring[(_ringIdx + 16 + j) & 31];
        _emaPeak += 0.001f * (Math.Abs(soft) - _emaPeak);

        // NCO: bitrate = (57000+foff)/48 from the carrier
        double foffHz = _freqInt * BB_RATE / (2.0 * Math.PI);
        _slotNco += (1187.5 + foffHz / 48.0) * 16.0 / 19000.0;
        long slot = (long)_slotNco;
        int bit = soft > 0f ? 1 : 0;
        while (_lastSlot < slot)
        {
            _lastSlot++;
            _paths[(int)(_lastSlot & 15L)].Push(bit, this);
        }

        if (++_dbgBits % (6000L * 16L) == 0L)
        {
            double hz = _freqInt * BB_RATE / (2.0 * Math.PI);
            int best = 0;
            for (int p = 1; p < 16; p++) if (_paths[p].Good > _paths[best].Good) best = p;
            var bp = _paths[best];
            _textOwner = bp; // b119: re-election (identity, not score)
            float lockq = _emaI2 / (_emaQ2 + 1e-12f);
            SigLock = lockq;
            int dg = bp.Good - _sigLastGood;
            SigBlocksSec = dg is >= 0 and <= 400 ? dg / 5.05f : 0f;
            _sigLastGood = bp.Good;
            string gear = (_costasA == COSTAS_A_TRK ? "T" : "A") + (_ffcApplied ? "+F" : "");
            Logger?.Invoke($"bits={_dbgBits / 16} foff={hz:F1}Hz lock={lockq:F2} gear={gear} peak={_emaPeak:F4} " +
                $"best={best} sync={bp.Synced} g={bp.Good} gr={bp.Groups} fec={_dbgFec} slip={_dbgSlips} " +
                $"ps='{PsMsg}' rt='{(Rt.Length > 24 ? Rt[..24] : Rt)}'");
        }
    }

    /// <summary>Coherent FFT (Hann) on z² → peak ±450 Hz; applied ONLY with a 10× gate.</summary>
    private void FfcFromFft()
    {
        for (int i = 0; i < FFC_N; i++) { _fftRe[i] = _fcBufI[i] * Hann[i]; _fftIm[i] = _fcBufQ[i] * Hann[i]; }
        Fft(_fftRe, _fftIm);
        double frate = BB_RATE / FFC_DEC;
        int kMax = (int)(900.0 * FFC_N / frate);
        var mags = new float[2 * kMax];
        int bk = 0; float bm = -1f;
        for (int kk = 1; kk <= kMax; kk++)
        {
            float m1 = _fftRe[kk] * _fftRe[kk] + _fftIm[kk] * _fftIm[kk];
            mags[kk - 1] = m1;
            if (m1 > bm) { bm = m1; bk = kk; }
            int k2 = FFC_N - kk;
            float m2 = _fftRe[k2] * _fftRe[k2] + _fftIm[k2] * _fftIm[k2];
            mags[kMax + kk - 1] = m2;
            if (m2 > bm) { bm = m2; bk = -kk; }
        }
        if (bk == 0) return;
        Array.Sort(mags);
        float median = mags[mags.Length / 2];
        if (bm < FFC_GATE * median) return;
        double f2 = bk * frate / FFC_N;
        double wOff = Math.PI * f2 / BB_RATE;
        _freqInt = Math.Clamp((float)wOff, -FREQ_MAX, FREQ_MAX);
        _ffcApplied = true;
    }

    private static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        int j = 0;
        for (int i = 1; i < n; i++)
        {
            int bit = n >> 1;
            while ((j & bit) != 0) { j ^= bit; bit >>= 1; }
            j |= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        int len = 2;
        while (len <= n)
        {
            double ang = -2.0 * Math.PI / len;
            float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
            for (int b = 0; b < n; b += len)
            {
                float curR = 1f, curI = 0f;
                int half = len / 2;
                for (int kk = 0; kk < half; kk++)
                {
                    int a = b + kk, bb = a + half;
                    float vR = re[bb] * curR - im[bb] * curI;
                    float vI = re[bb] * curI + im[bb] * curR;
                    re[bb] = re[a] - vR; im[bb] = im[a] - vI;
                    re[a] += vR; im[a] += vI;
                    float nR = curR * wr - curI * wi;
                    curI = curR * wi + curI * wr;
                    curR = nR;
                }
            }
            len <<= 1;
        }
    }

    // ===================== BitPath (SAA6588 methods) =====================
    private sealed class BitPath
    {
        private int _prevBit;
        private long _bitReg;
        private int _bitsSinceBlock;
        public bool Synced;
        private int _expectedBlock;
        private int _fly;
        private readonly int[] _blocks = new int[4];
        private readonly bool[] _okFlags = new bool[4];
        private readonly int[] _okCls = { 3, 3, 3, 3 };
        public int Good;
        public int Groups;
        private long _nbits;
        private int _usPos = -1;
        private long _usBit = -1000;
        private int _usInfo;

        public void Reset()
        {
            _prevBit = 0; _bitReg = 0; _bitsSinceBlock = 0; Synced = false; _expectedBlock = 0; _fly = 0;
            Array.Clear(_okFlags); for (int i = 0; i < 4; i++) _okCls[i] = 3;
            Good = 0; Groups = 0; _nbits = 0; _usPos = -1; _usBit = -1000; _usInfo = 0;
        }

        private void Accept(int inf, int cls, bool early, RdsDecoder d)
        {
            Good++; d._dbgGoodBlocks++;
            if (_fly > 0) _fly--;
            _blocks[_expectedBlock] = inf;
            _okFlags[_expectedBlock] = true;
            _okCls[_expectedBlock] = cls;
            // PS needs only B+D; dispatch ONLY from textOwner (b119 root-cause fix)
            if (_expectedBlock == 3 && _okFlags[1])
            {
                Groups++; d._dbgGroups++;
                if (d._textOwner == null) d._textOwner = this;
                if (ReferenceEquals(d._textOwner, this)) d.ParseGroup(_blocks, _okFlags, _okCls);
            }
            _expectedBlock = NEXTPOS[_expectedBlock];
            _bitsSinceBlock = early ? 1 : 0;
        }

        public void Push(int rawBit, RdsDecoder d)
        {
            int data = rawBit ^ _prevBit;
            _prevBit = rawBit;
            _bitReg = (_bitReg << 1) | (long)data;
            _nbits++;
            long v = _bitReg & 0x3FFFFFFL;
            if (!Synced)
            {
                if (SYNPOS.TryGetValue(Syndrome(v), out int pos))
                {
                    int info = (int)((v >> 10) & 0xFFFF);
                    if (_usPos >= 0 && _nbits - _usBit == 26L && NEXTPOS[_usPos] == pos)
                    {
                        d._dbgAHits++;
                        Synced = true; _fly = 0;
                        Array.Clear(_okFlags); for (int i = 0; i < 4; i++) _okCls[i] = 3;
                        _blocks[_usPos] = _usInfo; _okFlags[_usPos] = true; _okCls[_usPos] = 0;
                        _blocks[pos] = info; _okFlags[pos] = true; _okCls[pos] = 0;
                        _expectedBlock = NEXTPOS[pos];
                        _bitsSinceBlock = 0;
                    }
                    else { _usPos = pos; _usBit = _nbits; _usInfo = info; }
                }
                return;
            }
            if (++_bitsSinceBlock < 26) return;
            int syn = Syndrome(v);
            int expSyn = _expectedBlock switch { 0 => SYN_A, 1 => SYN_B, 3 => SYN_D, _ => SYN_C };
            bool ok = _expectedBlock == 2 ? (syn == SYN_C || syn == SYN_C2) : syn == expSyn;
            int inf = ok ? (int)((v >> 10) & 0xFFFF) : -1;
            int cls = 0;
            if (inf < 0 && _bitsSinceBlock == 26)
            {
                if (!FecTable.TryGetValue(syn ^ expSyn, out var ee) && _expectedBlock == 2)
                    FecTable.TryGetValue(syn ^ SYN_C2, out ee);
                else FecTable.TryGetValue(syn ^ expSyn, out ee);
                if (ee.Item1 != 0)
                {
                    inf = (int)(((v ^ ee.Item1) >> 10) & 0xFFFF);
                    cls = ee.Item2;
                    d._dbgFec++;
                }
            }
            if (inf >= 0)
            {
                if (_bitsSinceBlock == 27) d._dbgSlips++;
                Accept(inf, cls, false, d);
                return;
            }
            if (_bitsSinceBlock == 26)
            {
                long v2 = (_bitReg >> 1) & 0x3FFFFFFL;
                int s2 = Syndrome(v2);
                bool ok2 = _expectedBlock == 2 ? (s2 == SYN_C || s2 == SYN_C2) : s2 == expSyn;
                if (ok2) { d._dbgSlips++; Accept((int)((v2 >> 10) & 0xFFFF), 0, true, d); return; }
                return;
            }
            d._dbgBadBlocks++;
            _okFlags[_expectedBlock] = false;
            _okCls[_expectedBlock] = 3;
            _expectedBlock = NEXTPOS[_expectedBlock];
            _fly++;
            if (_fly >= FLY_MAX) { Synced = false; _usPos = -1; _usBit = -1000; }
            _bitsSinceBlock = 1;
        }
    }

    // ===================== group parsing =====================
    private void ParseGroup(int[] blocks, bool[] okf, int[] okCls)
    {
        int b2 = blocks[1];
        int gtype = (b2 >> 12) & 0xF;
        bool versionB = (b2 & 0x800) != 0;

        if (gtype == 0)
        {
            if (okCls[1] > 1 || okCls[3] > 1) return; // PS: B+D ≤ERDB
            int addr = b2 & 0x3;
            _psBuf[addr] = "" + RdsChar((blocks[3] >> 8) & 0xFF) + RdsChar(blocks[3] & 0xFF);
            if (addr == 0) _psSeqLen = 1;
            else if (addr == _psPrevAddr + 1 && _psSeqLen == addr) _psSeqLen = addr + 1;
            else _psSeqLen = 0; // strict: broken sequence = dead
            _psPrevAddr = addr;
            int cls = Math.Max(okCls[1], okCls[3]);
            _psChainCls = addr == 0 ? cls : Math.Max(_psChainCls, cls);
            if (_psSeqLen == 4)
            {
                string word = ($"{_psBuf[0]}{_psBuf[1]}{_psBuf[2]}{_psBuf[3]}").Trim();
                if (word.Length > 0) PsPublish(word, _psChainCls == 0);
            }
        }
        else if (gtype == 2 && !versionB)
        {
            if (!okf[2] || okCls[1] > 1 || okCls[2] > 1 || okCls[3] > 1) return; // 2A: C+D ≤ERDB
            int addr = b2 & 0xF;
            int flag = (b2 >> 4) & 1;
            if (flag != _rtFlag) { _rtFlag = flag; Array.Clear(_rtBuf); }
            _rtBuf[addr] = "" + RdsChar((blocks[2] >> 8) & 0xFF) + RdsChar(blocks[2] & 0xFF) +
                RdsChar((blocks[3] >> 8) & 0xFF) + RdsChar(blocks[3] & 0xFF);
            RtTryPublish();
        }
        else if (gtype == 2 && versionB)
        {
            if (okCls[1] > 1 || okCls[3] > 1) return;
            int addr = b2 & 0xF;
            int flag = (b2 >> 4) & 1;
            if (flag != _rtFlag) { _rtFlag = flag; Array.Clear(_rtBuf); }
            _rtBuf[addr] = "" + RdsChar((blocks[3] >> 8) & 0xFF) + RdsChar(blocks[3] & 0xFF);
            RtTryPublish();
        }
        else if (gtype == 3 && !versionB)
        {
            if (okCls[1] > 1 || okCls[3] > 1) return;
            int agc = b2 & 0x1F;
            if (blocks[3] == 0x4BD7 && (agc & 1) == 0) _rtpAppGt = agc >> 1;
        }
        else if (_rtpAppGt > 0 && gtype == _rtpAppGt && !versionB)
        {
            if (!okf[2] || okCls[1] > 1 || okCls[2] > 1 || okCls[3] > 1) return;
            int toggle = (b2 >> 4) & 1;
            int running = (b2 >> 3) & 1;
            if (toggle != _rtpToggle) { _rtpToggle = toggle; RtpTitle = ""; RtpArtist = ""; }
            if (running == 0) { RtpTitle = ""; RtpArtist = ""; return; }
            int c = blocks[2], dd = blocks[3];
            ApplyRtpTag(((b2 & 0x7) << 3) | ((c >> 13) & 0x7), (c >> 7) & 0x3F, (c >> 1) & 0x3F);
            ApplyRtpTag(((c & 0x1) << 5) | ((dd >> 11) & 0x1F), (dd >> 5) & 0x3F, dd & 0x1F);
        }
        else if (gtype == 4 && !versionB)
        {
            // CT: AN243 — ONLY from a group with 0 corrections (ERDA everywhere)
            if (!okf[2] || okCls[1] > 0 || okCls[2] > 0 || okCls[3] > 0) return;
            int mjd = ((blocks[1] & 0x3) << 15) | ((blocks[2] >> 1) & 0x7FFF);
            int hourUtc = ((blocks[2] & 0x1) << 4) | ((blocks[3] >> 12) & 0xF);
            int minute = (blocks[3] >> 6) & 0x3F;
            int offHalf = blocks[3] & 0x1F;
            if ((blocks[3] & 0x20) != 0) offHalf = -offHalf;
            if (mjd >= 15079 && hourUtc < 24 && minute < 60)
            {
                int totMin = hourUtc * 60 + minute + offHalf * 30;
                int dayAdj = 0;
                if (totMin < 0) { totMin += 1440; dayAdj = -1; }
                if (totMin >= 1440) { totMin -= 1440; dayAdj = 1; }
                int mjdL = mjd + dayAdj;
                int yp = (int)((mjdL - 15078.2) / 365.25);
                int mp = (int)((mjdL - 14956.1 - (int)(yp * 365.25)) / 30.6001);
                int day = mjdL - 14956 - (int)(yp * 365.25) - (int)(mp * 30.6001);
                int kk = (mp == 14 || mp == 15) ? 1 : 0;
                int year = 1900 + yp + kk;
                int month = mp - 1 - kk * 12;
                Ct = $"{totMin / 60:D2}:{totMin % 60:D2} {day}/{month}/{year}";
            }
        }
    }

    private void PsPublish(string word, bool clean)
    {
        Ps = word;
        if (word == _psPend) _psPendOk = true;
        else if (word != PsMsg) { _psPend = word; _psPendOk = false; _psPendClean = clean; _psPendMs = ClockMs; }
        else _psPend = "";
    }

    /// <summary>ClockMs: the caller sets it (live: wall-clock; offline harness: synthetic time).</summary>
    public long ClockMs;

    /// <summary>Called periodically (Rds svc loop): confirm-or-timeout + dwell 2s (b115/116).</summary>
    public void PsTick()
    {
        if (_psPend.Length == 0 || _psPend == PsMsg) return;
        int tmo = _psPendClean ? 1200 : 3500;
        bool ready = _psPendOk || ClockMs - _psPendMs > tmo;
        if (ready && ClockMs - _psMsgMs >= 2000)
        {
            PsMsg = _psPend; _psMsgMs = ClockMs; _psPend = ""; _psPendOk = false;
        }
    }

    // b101: RT immediate publish (not 2× confirm); cumulative + A/B wipe
    private void RtTryPublish()
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < 16)
        {
            string? seg = _rtBuf[i];
            if (seg == null) return; // hole before the end
            sb.Append(seg);
            if (seg.Contains('\r')) break;
            i++;
        }
        string s = sb.ToString();
        int cr = s.IndexOf('\r');
        string t = (cr >= 0 ? s[..cr] : s).TrimEnd();
        if (t.Length == 0 || t == Rt) return;
        Rt = t;
    }

    private void ApplyRtpTag(int ctype, int start, int len)
    {
        string r = Rt;
        if (r.Length == 0 || start >= r.Length) return;
        string v = r.Substring(start, Math.Min(start + len + 1, r.Length) - start).Trim();
        if (v.Length == 0) return;
        if (ctype == 1) RtpTitle = v;
        else if (ctype == 4) RtpArtist = v;
    }

    private static char RdsChar(int c) => c switch
    {
        0x0D => '\r',
        >= 0x20 and <= 0x7E => (char)c,
        _ => ' '
    };

    /// <summary>Optional logger for the statistics line (DvbTvRds).</summary>
    public Action<string>? Logger;
}
