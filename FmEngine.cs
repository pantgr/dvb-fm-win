using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace DvbFmWin;

/// <summary>
/// Headless FM engine — same chain as the console LiveRadio (read_async → RawHub → FmDemod
/// stereo + RdsDecoder, AFC acquire→LOCK→NCO) without console blocking. Exposes metrics (volatile
/// reads) + GainUp/Down + Retune for the WinForms UI. Start() does not block.
/// DI: ILogger&lt;FmEngine&gt; (+ ILoggerFactory → typed loggers for hub/demod/rds). Leveled logging.
/// </summary>
internal sealed class FmEngine : IFmData
{
    private const long Fs4 = 256_500, SampleRate = 1_026_000;
    // 🔴 FM gain = native 5 LNA steps (stock driver, reg 0x13: -9.9..19.2 dB) via TunerGain.

    private readonly ILogger<FmEngine> _log;
    private readonly RawHub _hub;
    private readonly FmDemod _demod;
    private readonly RdsDecoder _rds;
    private IntPtr _dev;
    private Thread? _pump, _audio, _rdsThread, _gainThread;
    private WasapiOut? _wo;
    private BufferedWaveProvider? _bwp;
    private RtlSdr.ReadAsyncCallback? _cb;
    private volatile bool _running;
    private volatile bool _scanning;   // true during band-scan → audio/rds drain, the scanner controls the tuning
    private long _station, _trim;
    private volatile int _gainIdx;             // current gain step (FC0012 = 0..4, native 5 LNA steps)
    private volatile float _gainDbVal;         // current gain dB (status/blend diagnostic)
    private bool _fcTuner;                      // FC0012/FC0013
    private volatile float _retuneReq = -1;    // UI → audio thread (>0 = retune requested)
    // gain on ITS OWN thread (not the UI thread = freeze, not the audio thread). _gainPending = requested idx.
    private readonly object _gainLock = new();
    private readonly AutoResetEvent _gainSignal = new(false);
    private int _gainPending = -1;
    private volatile bool _afcBlank;           // gain change → blank the AFC (the DC kicks)
    private TunerGain? _tunerGain;             // tuner gain (native driver table)
    private const float AudioGainBase = 3150f; // b118 eye-tube base· VolDb scales it
    private volatile int _volDb;               // audio volume in dB above/below base (UI, 2dB step)

    public FmEngine(ILogger<FmEngine> log, ILoggerFactory loggerFactory)
    {
        _log = log;
        _hub = new RawHub(loggerFactory.CreateLogger<RawHub>());
        _demod = new FmDemod(loggerFactory.CreateLogger<FmDemod>());
        _rds = new RdsDecoder(loggerFactory.CreateLogger<RdsDecoder>());
    }

    // ---- metrics (thread-safe reads) ----
    public float SignalDb => _hub.RfDb;
    public float AfcHz => _hub.AfcFreqHz;
    public bool Stereo => _demod.StereoDetected;
    public float Blend => _demod.Blend;
    public int GainIndex => _gainIdx;
    public int GainCount => _tunerGain?.Count ?? 5;
    public float GainDb => _tunerGain?.Db(_gainIdx) ?? 0f;
    public string Ps => _rds.PsMsg;
    public string Rt => _rds.Rt;
    public string Ct => _rds.Ct;
    public string RtpTitle => _rds.RtpTitle;
    public string RtpArtist => _rds.RtpArtist;
    public float RdsLock => _rds.SigLock;
    public float BlkPerSec => _rds.SigBlocksSec;
    public int VolDb => _volDb;
    public bool Locked { get; private set; }
    public float AfcResidualHz { get; private set; } // smoothed est (not raw jitter) — like android
    public double FreqMHz { get; private set; }

    public bool Start(double freqMHz, int startGain = -1, bool playAudio = true)
    {
        FreqMHz = freqMHz;
        _station = (long)Math.Round(freqMHz * 1e6);
        if (RtlSdr.rtlsdr_get_device_count() == 0) { _log.LogError("FAIL: no RTL device"); return false; }
        if (RtlSdr.rtlsdr_open(out _dev, 0) != 0 || _dev == IntPtr.Zero) { _log.LogError("FAIL: rtlsdr_open"); return false; }

        RtlSdr.rtlsdr_set_sample_rate(_dev, (uint)SampleRate);
        RtlSdr.rtlsdr_set_center_freq(_dev, (uint)(_station + Fs4));
        RtlSdr.rtlsdr_set_agc_mode(_dev, 0);          // RTL2832 digital AGC OFF
        _tunerGain = TunerGain.Init(_dev);            // manual mode + native gain table (FC0012 = 5 steps)
        _fcTuner = _tunerGain.Type is RtlSdr.TunerType.FC0012 or RtlSdr.TunerType.FC0013;
        _gainIdx = (startGain >= 0 && startGain < _tunerGain.Count) ? startGain : _tunerGain.DefaultLevel;
        _tunerGain.Set(_gainIdx);
        _gainDbVal = _tunerGain.Db(_gainIdx);
        _log.LogInformation("Start @ {Freq:F1}MHz · tuner={Tuner} · {Count} gain steps · start idx={Idx} ({Db:F0}dB)",
            freqMHz, _tunerGain.Type, _tunerGain.Count, _gainIdx, _tunerGain.Db(_gainIdx));
        // 🔵 R820T IF bandwidth filter = 300kHz default: +~2.8dB SNR (measured 99.9 control, 2 sweeps)
        // + adjacent-channel rejection (helps weak stations). FC0012 set_bw = no-op (harmless). Env override
        // (DVBFM_TUNER_BW: 0 = auto/wide for experiments).
        int bwHz = 300_000;
        if (int.TryParse(Environment.GetEnvironmentVariable("DVBFM_TUNER_BW"), out var bwEnv)) bwHz = bwEnv;
        if (bwHz > 0)
        {
            int brc = RtlSdr.rtlsdr_set_tuner_bandwidth(_dev, (uint)bwHz);
            _log.LogInformation("tuner IF bandwidth = {Bw} Hz (rc={Rc})", bwHz, brc);
        }
        RtlSdr.rtlsdr_reset_buffer(_dev);

        _bwp = new BufferedWaveProvider(new WaveFormat(FmDemod.OutRate, 16, 2))
        { BufferDuration = TimeSpan.FromSeconds(3), DiscardOnBufferOverflow = true };
        if (playAudio)
        {
            try { _wo = new WasapiOut(AudioClientShareMode.Shared, 200); _wo.Init(_bwp); }
            catch (Exception ex) { _log.LogWarning(ex, "audio init failed → headless (RawHub/RDS/stereo run normally)"); _wo = null; }
        }

        _running = true;

        byte[] iqTmp = new byte[262144];
        _cb = (buf, len, ctx) =>
        {
            int n = (int)len; if (n > iqTmp.Length) n = iqTmp.Length;
            Marshal.Copy(buf, iqTmp, 0, n); _hub.FeedIq(iqTmp, n);
        };
        _pump = new Thread(() => RtlSdr.rtlsdr_read_async(_dev, _cb, IntPtr.Zero, 0, 0))
        { Name = "FmPump", IsBackground = true, Priority = ThreadPriority.Highest };
        _pump.Start();

        var reader = _hub.Attach();
        _audio = new Thread(() => AudioLoop(reader)) { Name = "FmAudio", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _audio.Start();

        var rdsReader = _hub.Attach();
        _rdsThread = new Thread(() => RdsLoop(rdsReader)) { Name = "FmRds", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _rdsThread.Start();

        _gainThread = new Thread(GainLoop) { Name = "FmGain", IsBackground = true };
        _gainThread.Start();

        for (int i = 0; i < 200 && _bwp.BufferedDuration.TotalMilliseconds < 500; i++) Thread.Sleep(10);
        _wo?.Play();
        _log.LogInformation("engine running · prebuffer={Ms:F0}ms · threads: pump/audio/rds/gain",
            _bwp.BufferedDuration.TotalMilliseconds);
        return true;
    }

    private void AudioLoop(RawHub.Reader reader)
    {
        var mpxBuf = new float[16384];
        var pcm = new byte[16384 * 4];
        double afcAvg = 0; int afcTicks = 0; bool locked = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastAfc = 0, lastStep = 0, lastStatus = 0;
        bool stereoPrev = false;
        _log.LogDebug("AudioLoop: start");
        while (_running)
        {
            if (_scanning) { reader.Read(mpxBuf); Thread.Sleep(5); continue; } // band-scan: drain, the scanner tunes
            // ---- retune (all tuner access on THIS thread → no race) ----
            if (_retuneReq > 0)
            {
                double f = _retuneReq; _retuneReq = -1;
                FreqMHz = f; _station = (long)Math.Round(f * 1e6); _trim = 0;
                afcAvg = 0; afcTicks = 0; locked = false; Locked = false; lastStep = sw.ElapsedMilliseconds;
                _hub.NcoTargetHz = 0;
                RtlSdr.rtlsdr_set_center_freq(_dev, (uint)(_station + Fs4));
                _hub.ResetFrontEnd(); _rds.ResetSync();
                _log.LogInformation("RETUNE → {Freq:F1}MHz · ResetFrontEnd+ResetSync · Nco=0 trim=0", f);
            }
            int n = reader.Read(mpxBuf);
            if (n == 0) { Thread.Sleep(5); continue; }
            _demod.SignalDb = _hub.RfDb - _gainDbVal; // antenna-referred (diagnostic· the blend = noise-driven)
            int pi = 0;
            for (int k = 0; k < n; k++)
                if (_demod.ProcessMpx(mpxBuf[k], out short l, out short rr))
                {
                    pcm[pi++] = (byte)(l & 0xFF); pcm[pi++] = (byte)((l >> 8) & 0xFF);
                    pcm[pi++] = (byte)(rr & 0xFF); pcm[pi++] = (byte)((rr >> 8) & 0xFF);
                }
            if (pi > 0) _bwp!.AddSamples(pcm, 0, pi);

            long now = sw.ElapsedMilliseconds;
            if (now - lastAfc < 50) continue;
            lastAfc = now;
            if (_afcBlank) { _afcBlank = false; afcAvg = 0; afcTicks = 0; lastStep = now; } // gain change → settle blanking (android cycleGain)
            bool settled = now - lastStep > 1500;   // 2026-06-15: 2000→1500 (faster lock· retest stability)
            // 🔴 POWER squelch gate (SDRangel/SDR practice· verified LiveRadio): the AFC locks on
            // CARRIER PRESENCE, NOT on the 19k pilot — the AFC has nothing to do with stereo (a mono station
            // = carrier without pilot· it must lock there too). Pilot gate = the nonsense of the android port.
            // 🔴🔴 OVERLOAD gate: ONLY on real clip near 0 dBFS. 🔴 MEASURED 2026-06-15 with
            // stock rtl_sdr capture @92.0 (strong local): rf ~−8 dBFS steady, clip% 0.2-0.8% on ALL
            // gain steps = CLEAN signal, NOT overload. The old −8 dBFS gate fired at a normal
            // level → froze the AFC → trim stuck ~31kHz → detune → dead RDS. Real overload =
            // near 0 dBFS (rms high + clip). Threshold −2 dBFS.
            bool overload = _hub.RfDb >= -2f;                       // genuine near-clip only (−8 dBFS = normal)
            bool afcOk = settled && _hub.RfDb > -45f && !overload;
            if (afcOk) { afcAvg = 0.02 * _hub.AfcFreqHz + 0.98 * afcAvg; afcTicks++; }
            double est = afcTicks == 0 ? 0 : afcAvg / (1.0 - Math.Pow(0.98, afcTicks));
            AfcResidualHz = (float)est; // smoothed residual for the UI (not raw afcFreqHz jitter)
            double mag = Math.Abs(est);
            if (!locked)
            {
                if (settled && afcTicks >= 15 && now - lastStep >= 2000)   // 2026-06-15: ticks 20→15, gap 3000→2000 (faster lock)
                {
                    if (mag > 300)
                    {
                        lastStep = now; _trim = Clamp50k(_trim + (long)est);
                        RtlSdr.rtlsdr_set_center_freq(_dev, (uint)(_station + Fs4 + _trim));
                        afcAvg = 0; afcTicks = 0;
                        _log.LogInformation("AFC acquire step: est={Est}Hz → trim={Trim}Hz", (int)est, _trim);
                    }
                    else { locked = true; Locked = true; _log.LogInformation("AFC LOCKED ✓ trim={Trim}Hz residual={Res}Hz", _trim, (int)est); }
                }
            }
            else
            {
                // 🔴🔴 on clip DON'T let the nco accumulate (est = old garbage value → nco drifts
                // 665Hz → detune → abrupt stereo drop on gain-down). Reset to 0· the coarse trim holds the
                // station. When it comes out of overload, clean nco → smooth re-lock without a drop.
                if (overload) _hub.NcoTargetHz = 0f;
                else
                {
                    _hub.NcoTargetHz = Math.Clamp(_hub.NcoTargetHz + (float)(0.01 * est), -10000f, 10000f);
                    if (mag > 2000)
                    {
                        _trim = Clamp50k(_trim + (long)_hub.NcoTargetHz + (long)est);
                        _hub.NcoTargetHz = 0; locked = false; Locked = false; afcAvg = 0; afcTicks = 0; lastStep = now;
                        RtlSdr.rtlsdr_set_center_freq(_dev, (uint)(_station + Fs4 + _trim));
                        _log.LogInformation("AFC re-acquire (jump >2kHz): trim={Trim}Hz", _trim);
                    }
                }
            }

            if (_demod.StereoDetected != stereoPrev)
            {
                stereoPrev = _demod.StereoDetected;
                _log.LogInformation("STEREO {State} · pilot={Pilot:F3} blend={Blend:F2} sig={Sig:F1}dB",
                    stereoPrev ? "ON" : "OFF", _demod.PilotLevel, _demod.Blend, _demod.SignalDb);
            }
            if (now - lastStatus >= 2000)
            {
                lastStatus = now;
                _log.LogDebug("[{St}] {Freq:F1}MHz rf={Rf:F1} sig={Sig:F1} gain={Gain:F0}dB {Stereo} snr={Snr:F1}dB noiseTgt={NTgt:F2} pilotPLL={Pilot:F4} pilot19k={P19:F4} mpxrms={Mpx:F3} | AFC{Gate} est={Est} raw={Raw:F0} nco={Nco} trim={Trim} ticks={Ticks} | RDS blk/s={Blk:F0} lock={Lk:F1} PS='{Ps}' | drops={Drops}",
                    locked ? "LOCK" : "ACQ", FreqMHz, _hub.RfDb, _demod.SignalDb, _gainDbVal,
                    _demod.StereoDetected ? $"STEREO {_demod.Blend * 100:F0}%" : "MONO",
                    _demod.SnrDb, _demod.NoiseTgt, _demod.PilotLevel, _demod.Pilot19kMag, _demod.MpxRms,
                    _hub.RfDb >= -2f ? "⛔OVL" : "", (int)est, _hub.AfcFreqHz, (int)_hub.NcoTargetHz, _trim, afcTicks,
                    _rds.SigBlocksSec, _rds.SigLock, _rds.PsMsg, reader.Drops);
            }
        }
        _log.LogDebug("AudioLoop: exit");
    }

    private void RdsLoop(RawHub.Reader reader)
    {
        var rbuf = new float[16384];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastTick = 0;
        string lastPs = "", lastRt = "";
        while (_running)
        {
            if (_scanning) { reader.Read(rbuf); Thread.Sleep(5); continue; } // band-scan: drain (the band is being swept)
            int n = reader.Read(rbuf);
            if (n == 0) { Thread.Sleep(5); continue; }
            _rds.ClockMs = sw.ElapsedMilliseconds;
            for (int k = 0; k < n; k++) _rds.Process(rbuf[k]);
            long now = sw.ElapsedMilliseconds;
            if (now - lastTick >= 50) { lastTick = now; _rds.PsTick(); }
            if (_rds.PsMsg != lastPs && _rds.PsMsg.Trim().Length > 0) { lastPs = _rds.PsMsg; _log.LogInformation("RDS PS = «{Ps}»", _rds.PsMsg); }
            if (_rds.Rt != lastRt && _rds.Rt.Trim().Length > 0) { lastRt = _rds.Rt; _log.LogInformation("RDS RT = «{Rt}»", _rds.Rt); }
        }
    }

    /// <summary>Change station without restart (clamp 87.5–108).</summary>
    public void Retune(double freqMHz) => _retuneReq = (float)Math.Clamp(freqMHz, 87.5, 108.0);

    /// <summary>
    /// Band scan FROM WITHIN the live engine: reuse the open device/hub with THE CURRENT UI gain (Pantelis).
    /// Freezes audio/rds (drain), the scanner controls the tuning, then returns to the station. BLOCKS ~110s
    /// → call it on a background thread. Returns the frequencies that were found.
    /// </summary>
    public List<double> ScanBand(Action<string>? progress)
    {
        if (!_running || _tunerGain == null) return new List<double>();
        long savedStation = _station; int savedGain = _gainIdx;
        _scanning = true;
        _log.LogInformation("ScanBand start (UI gain idx={Idx})", savedGain);
        try
        {
            Thread.Sleep(200);                  // let audio/rds/gain loops enter drain (GainLoop freezes on _scanning)
            _tunerGain.Set(_gainIdx);           // THE CURRENT UI gain — not hardcoded
            var found = Scanner.SweepAndConfirm(_dev, _hub, _tunerGain.Db(_gainIdx), progress ?? (_ => { }));
            return found.Select(s => s.f).ToList();
        }
        finally
        {
            _tunerGain.Set(savedGain); _gainDbVal = _tunerGain.Db(savedGain);
            Retune(savedStation / 1e6);         // audio thread will reset AFC/lock when it unfreezes
            _scanning = false;
            _log.LogInformation("ScanBand done");
        }
    }

    public void GainUp() => RequestGain(+1);
    public void GainDown() => RequestGain(-1);

    // ---- audio volume (software, DSP scale· INDEPENDENT of the tuner RF gain) ----
    public void VolUp() => SetVolDb(_volDb + 2);
    public void VolDown() => SetVolDb(_volDb - 2);
    /// <summary>Audio volume in dB around the base (clamp −20..+20)· applied live to FmDemod.AudioGain.</summary>
    public void SetVolDb(int db)
    {
        _volDb = Math.Clamp(db, -20, 20);
        _demod.AudioGain = AudioGainBase * (float)Math.Pow(10.0, _volDb / 20.0);
        _log.LogInformation("AUDIO VOL = {Db:+0;-0}dB → AudioGain={Gain:F0}", _volDb, _demod.AudioGain);
    }

    // ---- functional A/B test hooks ----
    public void SetGainIdx(int idx)
    {
        lock (_gainLock) { _gainIdx = Math.Clamp(idx, 0, (_tunerGain?.Count ?? 5) - 1); _gainPending = _gainIdx; }
        _gainSignal.Set();
    }
    public void SetAgc(int on) { if (_dev != IntPtr.Zero) RtlSdr.rtlsdr_set_agc_mode(_dev, on); }
    public bool GainKick { set { if (_tunerGain != null) _tunerGain.Kick = value; } }
    public float ClipPct => _hub.ClipPct;

    private void RequestGain(int dir)
    {
        lock (_gainLock)
        {
            _gainIdx = Math.Clamp(_gainIdx + dir, 0, (_tunerGain?.Count ?? 5) - 1);
            _gainPending = _gainIdx;
        }
        _gainSignal.Set(); // wakes the gain thread (instant, no UI freeze)
    }

    // gain on ITS OWN thread — the tuner gain access (rtlsdr_set_tuner_gain) here → no race with the retune
    // (set_center_freq) of the audio thread. Native 5 LNA steps (FC0012) or driver table (R820T etc).
    private void GainLoop()
    {
        while (_running)
        {
            _gainSignal.WaitOne(250);
            if (!_running) break;
            if (_scanning) continue;
            int idx;
            lock (_gainLock) { idx = _gainPending; _gainPending = -1; }
            if (idx < 0 || _tunerGain == null) continue;
            float rfBefore = _hub.RfDb;
            int rc = _tunerGain.Set(idx);
            _gainDbVal = _tunerGain.Db(idx);
            _afcBlank = true; // gain change kicks the discriminator DC → blank AFC
            _log.LogInformation("GAINSET step {Idx}/{Count} = {Db:F0}dB setRc={Rc} rfBefore={RfB:F1} (AFC blanked)",
                idx + 1, _tunerGain.Count, _tunerGain.Db(idx), rc, rfBefore);
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _log.LogInformation("engine STOP: last {Freq:F1}MHz {St} rf={Rf:F1} {Stereo} RDS blk/s={Blk:F0} PS='{Ps}'",
            FreqMHz, Locked ? "LOCK" : "acq", _hub.RfDb,
            _demod.StereoDetected ? $"STEREO {_demod.Blend * 100:F0}%" : "MONO", _rds.SigBlocksSec, _rds.PsMsg);
        _running = false;
        _gainSignal.Set(); // unblock the gain thread so it can exit
        RtlSdr.rtlsdr_cancel_async(_dev);
        _pump?.Join(1500); _audio?.Join(1500); _rdsThread?.Join(1500); _gainThread?.Join(1000);
        try { _wo?.Stop(); _wo?.Dispose(); } catch { /* ignore */ }
        // 🔴 NO rtlsdr_close — the hayguen driver crashes (0xC0000005)· the process exit frees the USB.
        _dev = IntPtr.Zero;
    }

    private static long Clamp50k(long v) => Math.Clamp(v, -50_000, 50_000);
}
