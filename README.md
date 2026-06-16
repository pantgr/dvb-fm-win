# DvbFmWin — native Windows SDR FM radio (RTL2832U via WinUSB)

A native Windows .NET 9 app that talks **directly to an RTL2832U USB stick through the WinUSB
driver (Zadig)** — raw I/Q → **software demodulation**. It decodes broadcast **FM with RDS/RT+
and stereo**, with a vintage-radio WinForms UI.

It is the Windows sibling of [dvb-fm-android](https://github.com/pantgr/dvb-fm-android) — the
exact same validated DSP chain, brought native to Windows with a clean DI + Serilog architecture.

## Features

- **Direct USB SDR** via `rtlsdr.dll` P/Invoke (`rtlsdr_read_async` → in-memory ring buffer).
- **FM demodulation**: pilot-PLL 19 kHz stereo, L−R demod, RSSI blend, 50 µs de-emphasis.
- **RDS / RT+**: full block sync + FEC, PS / RadioText / clock-time / RT+ tags.
- **AFC**: acquire → LOCK → digital-NCO carrier following.
- **Vintage UI**: analog VU meter, amber displays, tuning (keyboard / mouse-wheel / buttons),
  gain and **audio volume** controls, band scan, station memories.
- **NAudio** WASAPI output. Full **Dependency Injection** (Generic Host) + **Serilog** logging.

## Hardware

Any RTL2832U stick switched to the **WinUSB** driver with [Zadig](https://zadig.akeo.ie/).
Best results with an **R820T/R820T2** tuner (full 0–49 dB gain control). The FC0012 tuner also
works but has limited (and quirky) gain control.

> The same stick can be either **WinUSB (SDR, this app)** *or* the Realtek BDA driver (hardware
> DVB-T demod) — one driver at a time. Switch in Device Manager.

## Run

A ready-to-run, self-contained build (no .NET install required) lives in [`dist/`](dist/):

```
dist\DvbFmWin.exe ui 92.0
```

Modes: `ui [freq]` (the WinForms radio), `live [freq]` (console), plus offline/test harnesses.

## Build from source

Requires the .NET 9 SDK.

```
dotnet build DvbFmWin.csproj -c Release
```

The native `rtlsdr.dll` (+ `pthreadVC2.dll`, `msvcr100.dll`) ships in [`native/`](native/) and is
copied next to the executable automatically.

## License

GPL-2.0 — this is a port of GPL'd rtl-sdr / dvb-fm-android code. See [LICENSE](LICENSE).
