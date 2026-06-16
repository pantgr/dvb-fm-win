using DvbFmWin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

// ============================================================================
// DvbFmWin — CORE (composition root): full DI (Generic Host) + Serilog with
// configurable LOG LEVEL. Every layer gets ILogger<T> from DI; every step is
// logged with a level (Verbose/Debug/Information/Warning/Error) → Console
// (bridge) AND logs/<mode>-<ts>.log (shared, so reads don't break into _001).
//
//   DvbFmWin.exe [--log <level>] [--logfile] <mode> [args...]
//   --log verbose|debug|information|warning|error   (default: information)
//   --logfile   also write logs/<mode>-<ts>.log  (OFF by default — no garbage files)
//
// Modes (same as before): phase1 | rawhub | fmaudio | stereompx | rdstest |
//   uishot | makeicon | scan | ftest | gainfunc | regsweep | gainprobe | ui | live
// 🔴 DSP/gain UNTOUCHED — only construction + logging change.
// ============================================================================

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* console without UTF-8 */ }

// ---- args: optional --log <level> + optional --logfile, then the mode + the rest ----
var argList = new List<string>(args);
var level = LogEventLevel.Information; // default: quiet console, no debug spam
int li = argList.FindIndex(a => a is "--log" or "-l");
if (li >= 0 && li + 1 < argList.Count && Enum.TryParse<LogEventLevel>(argList[li + 1], true, out var parsed))
{
    level = parsed;
    argList.RemoveRange(li, 2);
}
// 🔴 File logging is OPT-IN (--logfile) — OFF by default so the app never piles up log files.
// Turn it on only when we want to debug something.
bool wantFile = argList.Remove("--logfile") || argList.Remove("-f");
string[] a = argList.ToArray();
string mode = a.Length >= 1 ? a[0] : "phase1";

const string tmpl = "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";
var levelSwitch = new LoggingLevelSwitch(level);
var logCfg = new LoggerConfiguration()
    .MinimumLevel.ControlledBy(levelSwitch)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: tmpl);

string? logPath = null;
if (wantFile)
{
    string logDir = Path.Combine(AppContext.BaseDirectory, "logs"); // next to the exe, only when asked
    Directory.CreateDirectory(logDir);
    string tag = mode is "rawhub" or "fmaudio" or "stereompx" or "rdstest" or "live" or "ui" or "uishot"
        or "gainprobe" or "regsweep" or "gainfunc" or "gsweep" or "abtest" or "ftest" or "scan" or "makeicon" or "capture" or "afctest" ? mode : "phase1";
    logPath = Path.Combine(logDir, $"{tag}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    logCfg = logCfg.WriteTo.File(logPath, outputTemplate: tmpl, shared: true);
}
Serilog.Log.Logger = logCfg.CreateLogger();

// ---- DI container (Generic Host) + Serilog ----
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddSerilog(Serilog.Log.Logger, dispose: false);
builder.Services.AddSingleton(levelSwitch);

// pipeline + engine — transient (every run fresh); ILogger<T> is injected automatically
builder.Services.AddTransient<RawHub>();
builder.Services.AddTransient<FmDemod>();
builder.Services.AddTransient<RdsDecoder>();
builder.Services.AddTransient<FmEngine>();
builder.Services.AddTransient<MainForm>();

using var host = builder.Build();
var sp = host.Services;
var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("DvbFmWin");

// Serilog-backed adapter for the modes that haven't migrated to ILogger<T> yet
// (offline harnesses); writes at Information so it shows up at the default level.
void LogLine(string s) => log.LogInformation("{Line}", s);

log.LogInformation("=== DvbFmWin start · mode={Mode} · logLevel={Level} · log={LogPath} ===", mode, level, logPath ?? "(console only)");

try
{
    switch (mode)
    {
        case "rawhub":
        {
            string iq = a.Length >= 2 ? a[1] : @"C:\Claude\dvb_fm_win\iq_test.bin";
            string outp = a.Length >= 3 ? a[2] : @"C:\Claude\dvb_fm_win\mpx_cs.f32";
            log.LogInformation("Phase 2a RawHub harness · IQ={Iq} → MPX={Out}", iq, outp);
            return RawHubHarness.Run(iq, outp, LogLine);
        }
        case "fmaudio":
        {
            string iq = a.Length >= 2 ? a[1] : @"C:\Claude\dvb_fm_win\iq_test.bin";
            string wav = a.Length >= 3 ? a[2] : @"C:\Claude\dvb_fm_win\fm_stereo.wav";
            log.LogInformation("Phase 2c FM stereo harness · IQ={Iq} → WAV={Wav}", iq, wav);
            return AudioHarness.RunIq(iq, wav, -20f, LogLine);
        }
        case "stereompx":
        {
            string mpx = a.Length >= 2 ? a[1] : @"C:\Claude\dvb_fm_win\synthetic_mpx.f32";
            string wav = a.Length >= 3 ? a[2] : @"C:\Claude\dvb_fm_win\stereo_synth.wav";
            log.LogInformation("Phase 2c stereo from synthetic MPX · MPX={Mpx} → WAV={Wav}", mpx, wav);
            return AudioHarness.RunMpx(mpx, wav, -20f, LogLine);
        }
        case "rdstest":
            log.LogInformation("Phase 3a RDS decoder self-test");
            return RdsHarness.RunSelfTest(LogLine);
        case "uishot":
        {
            string png = a.Length >= 2 ? a[1] : @"C:\Claude\dvb_fm_win\ui_preview.png";
            MainForm.SavePreview(png);
            log.LogInformation("UI preview saved: {Png}", png);
            return 0;
        }
        case "makeicon":
        {
            string ico = a.Length >= 2 ? a[1] : @"C:\Claude\dvb_fm_win\app.ico";
            MainForm.SaveIcoFile(ico);
            log.LogInformation("app.ico saved: {Ico}", ico);
            return 0;
        }
        case "scan":
        {
            int gi = a.Length >= 2 && int.TryParse(a[1], out int g) ? g : -1;
            return Scanner.Run(LogLine, gi);
        }
        case "gainfunc":
            return GainFunc.Run(ParseFreq(a, 99.2), LogLine);
        case "gsweep":
            return GainSweep.Run(ParseFreq(a, 92.0), LogLine);
        case "abtest":
            if (a.Length >= 3 && int.TryParse(a[2], out int holdIdx))
                return FmAbTest.Hold(sp.GetRequiredService<FmEngine>(), ParseFreq(a, 92.0), holdIdx, 30, LogLine);
            return FmAbTest.Run(sp.GetRequiredService<FmEngine>(), ParseFreq(a, 92.0), LogLine);
        case "ui":
        {
            double fu = ParseFreq(a, 99.2);
            log.LogInformation("UI dashboard @ {Freq:F1}MHz", fu);
            int rc = 0;
            var uiThread = new Thread(() =>
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                var form = sp.GetRequiredService<MainForm>();
                form.SetFrequency(fu);
                System.Windows.Forms.Application.Run(form);
            });
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
            uiThread.Join();
            return rc;
        }
        case "live":
        {
            double f = ParseFreq(a, 93.0);
            log.LogInformation("Phase 2b LIVE — FM @ {Freq:F1}MHz", f);
            return LiveRadio.Run(f, LogLine);
        }
        case "capture":
        {
            double f = ParseFreq(a, 93.0);
            int secs = a.Length >= 3 && int.TryParse(a[2], out int s) ? s : 6;
            string outf = a.Length >= 4 ? a[3] : @"C:\Claude\dvb_fm_win\live_cap.bin";
            return Capture.Run(f, secs, outf, LogLine);
        }
        case "afctest":
        {
            double f = ParseFreq(a, 93.0);
            int secs = a.Length >= 3 && int.TryParse(a[2], out int s) ? s : 30;
            var afcLog = sp.GetRequiredService<ILoggerFactory>().CreateLogger("AfcTest");
            return AfcTest.Run(f, secs, afcLog);
        }
        default:
            return Phase1.Run(LogLine);
    }
}
finally
{
    Serilog.Log.CloseAndFlush();
}

static double ParseFreq(string[] a, double dflt)
{
    if (a.Length >= 2 && double.TryParse(a[1], System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var f)) return f;
    return dflt;
}
