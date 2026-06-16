using Microsoft.Extensions.Logging;

namespace DvbFmWin;

/// <summary>
/// FM AUDIO service — STEREO (faithful port of FmDemod.kt v24). MPX@171k → pilot PLL 19k
/// → sin(2φ) L−R demod → 57-tap 15k FIR ÷4 → blend (RSSI) → STEREO_GAIN 2.0 →
/// de-emphasis 50µs per channel → 42750 Hz stereo PCM16.
/// 🔴 piece of art — EXACT translation (PORT_UNDERSTANDING). Constants EXACTLY.
/// </summary>
internal sealed class FmDemod
{
    public const int MidRate = 171_000;
    private const int Decim2 = 4;
    public const int OutRate = MidRate / Decim2; // 42750
    // b118 eye-tube calibration = base· the user scales it live via UI volume (FmEngine.SetVolDb)
    private float _audioGain = 3150f;
    public float AudioGain { get => _audioGain; set => _audioGain = Math.Clamp(value, 0f, 40000f); }
    // 🔴 v24 sim finding: sd=mpx·sin(2φ) recovers 0.5×sub → ×2.0 EXACTLY (1.17 = ceiling 11.6dB)
    private const float StereoGain = 2.0f;
    // pilot gate hysteresis (chip practice)
    private const float PilotOn = 0.06f, PilotOff = 0.04f;
    // blend RSSI ramp (antenna-referred dBFS): b=0 below SIG_MONO, b=1 above SIG_FULL.
    // 🔴 Recalibrated for the NEW IF gain (0-70dB): antenna-referred = RfDb − gainDb ~ −50 for a
    // strong station (PC/FC0012 small antenna). The old −25/−40 were for the fake 5-step LNA gain.
    private const float SigFull = -54f, SigMono = -72f;
    // 🔬 Pilot-referenced ultrasonic-noise blend (US5027402 «blend-on-noise» spirit· gain AND program
    // INDEPENDENT). SNR = pilot² / ultrasonic-noise:
    //   • noise = ultrasonic >60k (HPF) — NO transmission there (above RDS 57k) → program-free.
    //   • reference = pilot 19k (_pilotLevel) — transmitted steadily → program-free.
    //   → ratio cancels the gain· both program-free → it doesn't «breathe» with the music.
    // ⚠️ NOT the quadrature of the pilot (tested & FAILED): it leaks A·sin(θ) with the PLL phase jitter
    //    → a strong pilot gave a falsely low SNR (measured: 92.0 RDS-46 ceiling produced stereo 0%).
    // Si47xx (AN332): SNR full-stereo 27 / mono 14 dB. Roll back: tgt formula → RSSI (one line).
    private const float SnrFull = 20f, SnrMono = 6f;  // SNR dB ramp (blend 0..1) — calibrate live
    private const float NoiseEmaA = 0.0005f;           // slow EMA of the power (τ≈11.7ms) → stable SNR
    private static readonly float BlendDownA = 1f / (0.003f * OutRate); // toward mono τ≈3ms
    private static readonly float BlendUpA = 1f / (0.150f * OutRate);   // toward stereo τ≈150ms
    // 2nd-order pilot PLL
    private static readonly float PllFreq = (float)(2.0 * Math.PI * 19000.0 / MidRate);
    private const float PllAlpha = 0.002f, PllBeta = 1e-6f;
    private static readonly float PllIntMax = (float)(2.0 * Math.PI * 50.0 / MidRate);
    private const float TwoPi = (float)(2.0 * Math.PI);
    private static readonly float DeemphA = (float)((1.0 / OutRate) / (50e-6 + 1.0 / OutRate));

    /// <summary>RSSI driver of the blend (written by the caller: rfDb − gainDb). Default −20 = full stereo.</summary>
    public float SignalDb = -20f;
    public bool StereoDetected { get; private set; }
    public float PilotLevel { get; private set; }
    public float Blend { get; private set; }
    // 🔬 diagnostics INDEPENDENT of the PLL: 19k pilot magnitude (free NCO) + MPX rms.
    // pilot19k high but PilotLevel~0 → the PLL is not locking· pilot19k~0 → there is no 19k in the MPX.
    public float Pilot19kMag { get; private set; }
    public float MpxRms { get; private set; }
    // 🔬 MRC noise detector — gain-independent blend driver. SnrDb = 10·log10(total/ultrasonic).
    public float SnrDb { get; private set; }
    public float NoiseTgt { get; private set; } // the blend target produced by the detector (before the dynamics)

    private readonly ILogger<FmDemod>? _log;
    public FmDemod(ILogger<FmDemod>? log = null) => _log = log;

    // 57-tap audio FIR (fc=15kHz @171k, Hamming)
    private static readonly float[] Taps = BuildTaps();
    private readonly float[] _delayM = new float[Taps.Length];
    private readonly float[] _delayS = new float[Taps.Length];
    private int _dIdx, _phase;

    // pilot PLL state
    private float _phi, _freqInt, _pilotLevel;
    private float _blend;
    private float _deemphL, _deemphR;
    // 🔬 free-running 19k probe (NOT PLL) — shows whether the pilot EXISTS in the MPX
    private float _probePhi, _probeI, _probeQ, _mpxSq;
    private int _probeN;
    // 🔬 ultrasonic noise detector state (HPF 60k biquad + EMA power· program-free) — normalized with pilot
    private float _ultEma, _hx1, _hx2, _hy1, _hy2;

    private static float[] BuildTaps()
    {
        var t = new float[57];
        double fc = 15_000.0 / MidRate;
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

    /// <summary>Raw MPX @171k → optionally one stereo PCM16 pair (every 4th).</summary>
    public bool ProcessMpx(float mpx, out short left, out short right)
    {
        left = 0; right = 0;

        // ---- pilot PLL 19k ----
        float s1 = MathF.Sin(_phi), c1 = MathF.Cos(_phi);
        float err = mpx * c1;
        _freqInt = Math.Clamp(_freqInt + PllBeta * err, -PllIntMax, PllIntMax);
        _phi += PllFreq + _freqInt + PllAlpha * err;
        if (_phi > TwoPi) _phi -= TwoPi;
        _pilotLevel += 0.0005f * (mpx * s1 * 2f - _pilotLevel);
        if (StereoDetected) { if (_pilotLevel < PilotOff) StereoDetected = false; }
        else { if (_pilotLevel > PilotOn) StereoDetected = true; }
        PilotLevel = _pilotLevel;

        // 🔬 diagnostic: independent free NCO @19k (whether the pilot exists, independent of the PLL) + MPX rms
        _probeI += mpx * MathF.Cos(_probePhi);
        _probeQ += mpx * MathF.Sin(_probePhi);
        _probePhi += PllFreq; if (_probePhi > TwoPi) _probePhi -= TwoPi;
        _mpxSq += mpx * mpx;
        if (++_probeN >= 1710) // ~10ms @171k
        {
            Pilot19kMag = MathF.Sqrt(_probeI * _probeI + _probeQ * _probeQ) / _probeN;
            MpxRms = MathF.Sqrt(_mpxSq / _probeN);
            _probeI = 0f; _probeQ = 0f; _mpxSq = 0f; _probeN = 0;
        }

        // 🔬 pilot-referenced ultrasonic noise (gain & program independent). HPF 60k (Scanner coeffs)
        // isolates noise >60k (above RDS 57k = program-free)· reference = pilot (program-free).
        // SNR = pilot²/ultrasonic → gain-indep (ratio) + program-indep (neither depends on music).
        float hy = 0.1305f * mpx - 0.261f * _hx1 + 0.1305f * _hx2 - 0.752f * _hy1 - 0.273f * _hy2; // HPF 60k
        _hx2 = _hx1; _hx1 = mpx; _hy2 = _hy1; _hy1 = hy;
        _ultEma += NoiseEmaA * (hy * hy - _ultEma);
        SnrDb = 10f * MathF.Log10((_pilotLevel * _pilotLevel + 1e-12f) / (_ultEma + 1e-12f));

        // L−R demod per sample: sd = mpx·sin(2φ) (=2·s1·c1)
        _delayM[_dIdx] = mpx;
        _delayS[_dIdx] = mpx * 2f * s1 * c1;
        _dIdx = (_dIdx + 1) % Taps.Length;
        if (++_phase != Decim2) return false;
        _phase = 0;

        float mm = 0f, sb = 0f;
        int k = _dIdx;
        foreach (float c in Taps)
        {
            k = k == 0 ? Taps.Length - 1 : k - 1;
            mm += c * _delayM[k];
            sb += c * _delayS[k];
        }

        // blend (TDA1591 SNC): ramp × pilot gate, asymmetric dynamics.
        // VALIDATED RSSI blend (antenna-referred SignalDb = rf−gain). 2026-06-15: roll back from the
        // experimental SNR-driven — SnrMono=6 was untuned → SnrDb~4-5 on the R820T → blend 0/mono
        // even though the reception was perfect (RDS 46 blk/s, AFC lock). RSSI = the proven path.
        float tgt = StereoDetected ? Math.Clamp((SignalDb - SigMono) / (SigFull - SigMono), 0f, 1f) : 0f;
        // experimental SNR-driven (v2, needs live calibration): Math.Clamp((SnrDb - SnrMono) / (SnrFull - SnrMono), 0f, 1f)
        NoiseTgt = tgt;
        _blend += (tgt < _blend ? BlendDownA : BlendUpA) * (tgt - _blend);
        Blend = _blend;
        float s = _blend * StereoGain * sb;
        float l = mm + s, r = mm - s;
        _deemphL += DeemphA * (l - _deemphL);
        _deemphR += DeemphA * (r - _deemphR);
        left = (short)Math.Clamp((int)(_deemphL * AudioGain), -32767, 32767);
        right = (short)Math.Clamp((int)(_deemphR * AudioGain), -32767, 32767);
        return true;
    }
}
