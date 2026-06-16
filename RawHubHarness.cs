namespace DvbFmWin;

/// <summary>
/// Phase 2a harness: reads an IQ u8 file → RawHub.FeedIq in chunks → collects the raw
/// MPX via Reader → writes mpx_cs.f32 (little-endian float32). Validation against
/// validate_rawhub.py (cross-corr + aligned diff + spectral pilot/RDS).
/// </summary>
internal static class RawHubHarness
{
    public static int Run(string iqFile, string mpxOut, Action<string> log)
    {
        if (!File.Exists(iqFile)) { log($"FAIL: does not exist {iqFile}"); return 1; }

        var hub = new RawHub();
        var reader = hub.Attach();
        var chunk = new byte[65536];
        var mpxBuf = new float[65536];
        long iqBytes = 0, mpxSamples = 0;

        using (var fin = File.OpenRead(iqFile))
        using (var fout = new BinaryWriter(File.Create(mpxOut)))
        {
            int r;
            while ((r = fin.Read(chunk, 0, chunk.Length)) > 0)
            {
                hub.FeedIq(chunk, r);
                iqBytes += r;
                int n;
                while ((n = reader.Read(mpxBuf)) > 0)
                {
                    for (int k = 0; k < n; k++) fout.Write(mpxBuf[k]);
                    mpxSamples += n;
                }
            }
            // drain whatever is left
            int n2;
            while ((n2 = reader.Read(mpxBuf)) > 0)
            {
                for (int k = 0; k < n2; k++) fout.Write(mpxBuf[k]);
                mpxSamples += n2;
            }
        }

        long expectIq = iqBytes / 2;            // I/Q pairs
        long expectMpx = expectIq / 6;          // ÷3 (decim) ÷2 (mpx) = ÷6
        log($"RawHub: IQ {iqBytes} bytes = {expectIq} samples -> MPX {mpxSamples} @171k (expected ~{expectMpx}), drops={reader.Drops}");
        log($"  AfcFreqHz={hub.AfcFreqHz:F1} Hz  RfDb={hub.RfDb:F1} dBFS  -> {mpxOut}");
        return 0;
    }
}
