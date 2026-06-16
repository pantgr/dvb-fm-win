namespace DvbFmWin;

/// <summary>
/// Phase 3a RDS validation — C#-only, zero Python dependency. Generates a synthetic STANDARD
/// RDS MPX (group 0A, PS="TESTFM01", CRC POLY 0x5B9 + EN50067 offsets) and runs it
/// through the RdsDecoder. The generator (standard offsets) is INDEPENDENT of the decoder
/// (shift-register syndromes) → if "TESTFM01" comes out, the port is a correct translation.
/// (The RdsDecoder.kt is already redsea/live-validated; here we check that the TRANSLATION is faithful.)
/// </summary>
internal static class RdsHarness
{
    private const int FS = 171_000;
    private const int SPB = 144; // 171000/1187.5 exactly
    private const int POLY = 0x5B9;
    private static readonly int[] OFF = { 0x0FC, 0x198, 0x168, 0x1B4 }; // A,B,C,D — EN50067

    private static int Crc10(int info)
    {
        int reg = info << 10;
        for (int i = 25; i >= 10; i--)
            if ((reg & (1 << i)) != 0) reg ^= POLY << (i - 10);
        return reg & 0x3FF;
    }

    private static int Block(int info, int offIdx) => (info << 10) | (Crc10(info) ^ OFF[offIdx]);

    private static float[] GenSynthMpx(string ps, int pi)
    {
        // 4 groups 0A (segments 0-3), PI constant, AF filler, PS chars
        var groups = new int[4][];
        for (int seg = 0; seg < 4; seg++)
        {
            int b2 = seg;        // type 0A, flags 0, segment in the 2 LSB
            int b3 = 0xE0E0;     // AF filler
            int b4 = (ps[seg * 2] << 8) | ps[seg * 2 + 1];
            groups[seg] = new[] { Block(pi, 0), Block(b2, 1), Block(b3, 2), Block(b4, 3) };
        }
        // bitstream: 20× repeat (~7s)
        var bits = new List<int>();
        for (int r = 0; r < 20; r++)
            foreach (var g in groups)
                foreach (int b in g)
                    for (int k = 25; k >= 0; k--) bits.Add((b >> k) & 1);
        // differential encode (decoder: data = raw ^ prev_raw)
        int prev = 0;
        var raw = new int[bits.Count];
        for (int i = 0; i < bits.Count; i++) { prev ^= bits[i]; raw[i] = prev; }

        int nTotal = raw.Length * SPB;
        var mpx = new float[nTotal];
        var rng = new Random(1);
        for (int idx = 0; idx < nTotal; idx++)
        {
            int bitIdx = idx / SPB;
            int phase = idx % SPB;
            int sym = raw[bitIdx] * 2 - 1;
            float biphase = sym * (phase < SPB / 2 ? 1f : -1f);
            // 57k=fs/3 (period 3), pilot 19k=fs/9, mono 1kHz — modulo for accuracy
            double carrier = Math.Cos(2 * Math.PI * (idx % 3) / 3.0);
            double pilot = 0.09 * Math.Sin(2 * Math.PI * (idx % 9) / 9.0);
            double mono = 0.18 * Math.Sin(2 * Math.PI * (idx % 171) / 171.0);
            mpx[idx] = (float)(mono + pilot + 0.03 * biphase * carrier + 0.005 * (rng.NextDouble() * 2 - 1));
        }
        return mpx;
    }

    public static int RunSelfTest(Action<string> log)
    {
        const string ps = "TESTFM01";
        const int pi = 0x1234;
        log($"Phase 3a RDS self-test (C#-only): synthetic STANDARD RDS group 0A, PS='{ps}', PI=0x{pi:X4}");
        float[] mpx = GenSynthMpx(ps, pi);
        log($"synthetic MPX: {mpx.Length} samples @171k ({mpx.Length / (double)FS:F1}s)");

        var rds = new RdsDecoder { Logger = log };
        for (int i = 0; i < mpx.Length; i++)
        {
            rds.ClockMs = (long)(i / 171.0); // synthetic ms @171k
            rds.Process(mpx[i]);
            if (i % 8550 == 0) rds.PsTick(); // ~50ms
        }
        rds.PsTick();

        log($"RDS result: PsMsg='{rds.PsMsg}'  Ps='{rds.Ps}'  RT='{rds.Rt}'  CT='{rds.Ct}'");
        bool pass = rds.PsMsg == ps || rds.Ps == ps;
        log("=== Phase 3a RDS " + (pass ? $"PASS: PS='{ps}' ✅ (port = faithful translation)" : "WARNING — PS≠TESTFM01, see decode") + " ===");
        return pass ? 0 : 2;
    }
}
