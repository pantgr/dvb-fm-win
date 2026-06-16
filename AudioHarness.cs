namespace DvbFmWin;

/// <summary>
/// Phase 2b/c offline harnesses → STEREO WAV (42750 Hz, 16-bit, 2ch).
///   RunIq:  IQ u8 → RawHub → FmDemod stereo  (real capture, Pantelis listens)
///   RunMpx: MPX f32 → FmDemod stereo         (synthetic, validate_stereo.py separation)
/// signalDb = RSSI driver of the blend (−20 = full stereo for the offline tests).
/// </summary>
internal static class AudioHarness
{
    public static int RunIq(string iqFile, string wavOut, float signalDb, Action<string> log)
    {
        if (!File.Exists(iqFile)) { log($"FAIL: does not exist {iqFile}"); return 1; }
        var hub = new RawHub();
        var reader = hub.Attach();
        var demod = new FmDemod { SignalDb = signalDb };
        var chunk = new byte[65536];
        var mpxBuf = new float[65536];
        var pcm = new List<short>(4_000_000);

        using (var fin = File.OpenRead(iqFile))
        {
            int r;
            while ((r = fin.Read(chunk, 0, chunk.Length)) > 0)
            {
                hub.FeedIq(chunk, r);
                int n;
                while ((n = reader.Read(mpxBuf)) > 0)
                    for (int k = 0; k < n; k++)
                        if (demod.ProcessMpx(mpxBuf[k], out short l, out short rr)) { pcm.Add(l); pcm.Add(rr); }
            }
            int n2;
            while ((n2 = reader.Read(mpxBuf)) > 0)
                for (int k = 0; k < n2; k++)
                    if (demod.ProcessMpx(mpxBuf[k], out short l, out short rr)) { pcm.Add(l); pcm.Add(rr); }
        }

        WriteWavStereo(wavOut, pcm, FmDemod.OutRate);
        log($"FM stereo: {pcm.Count / 2} frames @ {FmDemod.OutRate}Hz ({pcm.Count / 2.0 / FmDemod.OutRate:F2}s)  " +
            $"pilotPLL={demod.PilotLevel:F4} pilot19k={demod.Pilot19kMag:F4} mpxrms={demod.MpxRms:F3} stereo={demod.StereoDetected} blend={demod.Blend:F2}  -> {wavOut}");
        return 0;
    }

    public static int RunMpx(string mpxFile, string wavOut, float signalDb, Action<string> log)
    {
        if (!File.Exists(mpxFile)) { log($"FAIL: does not exist {mpxFile}"); return 1; }
        var demod = new FmDemod { SignalDb = signalDb };
        byte[] bytes = File.ReadAllBytes(mpxFile);
        int n = bytes.Length / 4;
        var pcm = new List<short>(n / 2);
        for (int i = 0; i < n; i++)
        {
            float mpx = BitConverter.ToSingle(bytes, i * 4);
            if (demod.ProcessMpx(mpx, out short l, out short rr)) { pcm.Add(l); pcm.Add(rr); }
        }
        WriteWavStereo(wavOut, pcm, FmDemod.OutRate);
        log($"stereo from MPX: {n} samples → {pcm.Count / 2} frames  " +
            $"pilot={demod.PilotLevel:F3} stereo={demod.StereoDetected} blend={demod.Blend:F2}  -> {wavOut}");
        return 0;
    }

    private static void WriteWavStereo(string path, List<short> pcm, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        int dataBytes = pcm.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);   // PCM, stereo
        w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16); // blockAlign 4, 16-bit
        w.Write("data"u8); w.Write(dataBytes);
        foreach (short s in pcm) w.Write(s);
    }
}
