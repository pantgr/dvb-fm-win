namespace DvbFmWin;

/// <summary>
/// Functional A/B (Pantelis request: try the gain paths, see which gives RDS/stereo).
/// Runs the FULL FM engine on a station, 3 configs × all steps, measures RDS blk/s + lock,
/// stereo%/blend, clip%. Headless. Mode: abtest [freqMHz].
///   • KICK+AGCoff   = the current one (min-first kick, baseband max)
///   • NOKICK+AGCoff = like Android (plain set)
///   • NOKICK+AGCon  = plain set + RTL2832 AGC (clean ADC, no saturation)
/// </summary>
internal static class FmAbTest
{
    /// <summary>Hold test: hold one step (AGC OFF, no-kick) for secs, log RDS+stereo every 5s.</summary>
    public static int Hold(FmEngine engine, double freqMHz, int gainIdx, int secs, Action<string> log)
    {
        if (!engine.Start(freqMHz, gainIdx, playAudio: false)) { log("FAIL: engine start"); return 1; }
        engine.GainKick = false;
        engine.SetAgc(0);
        engine.SetGainIdx(gainIdx);
        log($"HOLD @ {freqMHz:F1}MHz · step {gainIdx + 1} ({engine.GainDb:F1}dB) · AGC OFF · {secs}s");
        for (int t = 0; t < secs; t += 5)
        {
            Thread.Sleep(5000);
            string st = engine.Stereo ? $"STEREO {engine.Blend * 100,3:F0}%" : "MONO      ";
            log($"  t={t + 5,3}s │ rf={engine.SignalDb,6:F1} clip={engine.ClipPct,5:F2}% │ {st} │ RDS {engine.BlkPerSec,5:F1}blk/s lock={engine.RdsLock,4:F2} │ PS='{engine.Ps}'");
        }
        engine.Stop();
        log("HOLD done.");
        return 0;
    }

    public static int Run(FmEngine engine, double freqMHz, Action<string> log)
    {
        if (!engine.Start(freqMHz, 0, playAudio: false)) { log("FAIL: engine start"); return 1; }
        int n = engine.GainCount;
        log($"FM A/B @ {freqMHz:F1}MHz — 3 gain configs × {n} steps · functional (RDS/stereo/clip)");
        Thread.Sleep(4000); // initial settle

        var configs = new (string name, bool kick, int agc)[]
        {
            ("KICK+AGCoff  ", true, 0),
            ("NOKICK+AGCoff", false, 0),
            ("NOKICK+AGCon ", false, 1),
        };

        foreach (var (name, kick, agc) in configs)
        {
            engine.GainKick = kick;
            engine.SetAgc(agc);
            Thread.Sleep(2000);
            log($"── {name} ──");
            for (int i = 0; i < n; i++)
            {
                engine.SetGainIdx(i);
                Thread.Sleep(6000); // settle + RDS block-rate
                string st = engine.Stereo ? $"STEREO {engine.Blend * 100,3:F0}%" : "MONO      ";
                log($"  {name} st{i + 1} {engine.GainDb,5:F1}dB │ rf={engine.SignalDb,6:F1} clip={engine.ClipPct,5:F2}% │ {st} │ RDS {engine.BlkPerSec,5:F1}blk/s lock={engine.RdsLock,4:F2}");
            }
        }
        engine.Stop();
        log("A/B done.");
        return 0;
    }
}
