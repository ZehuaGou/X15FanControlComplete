# X15 Fan Control

A safety-first, x86 Windows fan controller for the **COLORFUL X15 AT 23 / Clevo NP50SNE**. Features temperature filtering, asymmetric ramping, acoustic stable zones, write verification with external-override detection, adaptive power strategies with a game fast-track, Control Center takeover with lease-based recovery, an independent watchdog, and calibration tools.

> ⚠ **Hardware compatibility warning**
> - This software has been developed and tested only on **COLORFUL X15 AT 23 / Clevo NP50SNE**.
> - Other Clevo models may use different EC registers and are **not** guaranteed to work.
> - Incorrect EC writes could cause fan malfunction or system instability.
> - **Always complete ReadOnly and Simulation validation before switching to Active mode.**
> - **Never run two fan control applications simultaneously.**
> - You assume all hardware risk.

## Features

- **Three run modes**: ReadOnly (sensor read only), Simulation (engine runs, no EC writes), Active (real control)
- **Write verification**: every EC duty write is read back at 50 ms and 1000 ms; a confirmed external override (three consecutive mismatches) restores Auto and drops to ReadOnly
- **Adaptive power strategies**: four tiers (Quiet / Daily / Code / Heavy) selected automatically from load, or pinned via fixed strategies
- **Game fast-track**: GPU ≥ 70% or CPU ≥ 80% sustained load pierces the tier hold and upshifts within ~3 s, so games get full PL1/CPU performance immediately
- **Fail-safe supervision**: an independent watchdog process restores the EC to Auto if the main program exits, hangs (30 s heartbeat timeout), or is killed
- **Control Center takeover**: the app briefly stops CLEVO Control Center's services/FnKey so its power limits can be written; a lease file plus a second watchdog restore them on exit or crash
- **Safety floors**: three-stage emergency fan ramps by temperature, CPU RPM safety guard, sensor-invalid fallback, and rate-limited up/down ramping
- **Logging**: CSV telemetry (~1 Hz), async application log with throttled UI updates, and verification logs

## Project status

- ✅ Solution compiles on Windows with .NET Framework 4.8 (Release x86 + x64 helper)
- ✅ ReadOnly / Simulation / Active verified on target hardware (fan control, watchdog, sleep/resume, tray exit)
- ✅ Adaptive tiers calibrated against a 23-hour real-usage trace on the target machine; game fast-track verified in place
- ⏳ GPU acoustic calibration sweep — **not yet completed**
- ✅ Desktop experience: minimize to taskbar, close to tray, auto-start, async logging

## Build

Requirements:

- Windows 10 or 11
- .NET SDK 8.0 or later (install from https://dotnet.microsoft.com/download)
- .NET Framework 4.8 targeting pack

Primary build command:

```bat
scripts\build-release-dotnet.bat
```

Or manually:

```bat
dotnet build .\X15FanControl.sln -c Release -p:Platform=x86
```

The combined runnable folder is `dist\`. Run the regression suite with:

```bat
tests\X15FanCore.Tests\bin\x86\Release\net48\X15FanCore.Tests.exe
```

### Required third-party binary

This repository does **not** include `vendor\ClevoEcInfo.dll`. Before running:

1. Locate `ClevoEcInfo.dll` in your existing Clevo fan-control installation directory.
2. Copy it to `vendor\ClevoEcInfo.dll`.
3. Re-run the build script or manually copy it to `dist\`.

See `vendor\README.md` for the required SHA-256 and technical details.

## Safe first run

1. Build and run `dist\X15FanControl.exe` as administrator.
2. Stay in **ReadOnly** for several minutes. Confirm CPU/GPU temperatures, duty percentages, and RPM values are plausible.
3. Switch to **Simulation**. Confirm the controller target moves gradually and emergency thresholds are correct.
4. Only then switch to **Active**. Keep the Restore Auto button visible and start at idle.
5. Test sleep/resume and application exit. Confirm the original Auto controller resumes.
6. Run the calibration sweep only after normal Active operation is stable.

See [docs/SAFE_TEST_PLAN.md](docs/SAFE_TEST_PLAN.md) for the detailed test sequence.

## Usage

### Three modes

| Mode | Description |
|------|-------------|
| **ReadOnly** | Only reads sensors, no EC writes. Default startup mode. |
| **Simulation** | Control engine runs, targets are displayed, but no EC writes. |
| **Active** | Actual EC fan control with watchdog, heartbeat, and fail-safe Auto restoration. |

### Window behavior

- **Minimize button** → minimizes to Windows taskbar (not tray)
- **Close ×** → hides to system tray, control loop continues running
- **Tray double-click / "打开主窗口"** → restores window
- **Tray "退出"** → full cleanup and exit (restores Auto and Control Center, stops watchdogs, disposes EC)

### Auto-start

Tray menu "开机自动启动" creates a scheduled task (`schtasks /SC ONLOGON /RL HIGHEST`) to launch the program at login with `--autostart --minimized`. The saved Active preference is restored automatically once EC, telemetry, and the Control Center lease all pass their gates.

### Power strategies

The strategy dropdown contains **Auto** plus four fixed strategies. In Auto mode the tier moves automatically; the fixed modes pin one tier and never drift.

| Strategy | PL1/PL2 (W) | Window (s) | CPU Max Performance | Notes |
|----------|-------------|------------|---------------------|-------|
| Auto | Adaptive | 28 | Adaptive | Automatic tier selection |
| Quiet | 25 / 35 | 28 | 75% | Lowest fan curve, thermal-safe gate |
| Daily | 30 / 45 | 28 | 85% | Default neutral tier |
| Code | 38 / 55 | 28 | 95% | Interactive/compile workloads |
| Heavy | 55 / 69 | 28 | 100% | Full performance |

### Adaptive tier logic

The four tiers (Quiet / Daily / Code / Heavy) are selected from 8-second load evidence windows; downshifts use 30-second averages plus 15-second peak gates, so upshifts are fast and downshifts are deliberately slow to avoid flapping.

| Transition | Gate |
|------------|------|
| Quiet → Daily | CPU ≥ 12% or GPU ≥ 10% |
| Daily → Code | CPU ≥ 25% or GPU ≥ 20% |
| Code → Heavy | CPU ≥ 50% or GPU ≥ 40% |
| Heavy → Code | avg CPU ≤ 25% / GPU ≤ 20%, peak < 60% / 50%, 45 s dwell |
| Code → Daily | avg CPU ≤ 15% / GPU ≤ 12%, peak < 40% / 30%, 60 s dwell |
| Daily → Quiet | avg CPU ≤ 8% / GPU ≤ 15%, peak < 30% / 30%, 90 s dwell, plus thermal gate (CPU < 85 °C, GPU < 75 °C, rise ≤ 0.5 °C/s) |

**Game fast-track**: sustained GPU ≥ 70% or CPU ≥ 80% (game-level load) pierces the 20 s minimum tier hold and compresses the upshift dwell to 3 seconds, so a game reaches Heavy (55 W / 100% CPU) within seconds instead of ~45 s. Downshift dwells are never compressed.

> The thresholds were calibrated against a 23-hour usage trace on the target machine (84,139 one-second samples). Notably, the previous 70% CPU heavy gate was unreachable in practice (only 0.05% of real samples exceeded it) and the old quiet gate (GPU ≤ 8%) could never fire because desktop compositing holds the GPU near 10%. See `src/X15FanCore/Control/AdaptivePowerTier.cs` for the data notes.

### Profiles

Each strategy has a matching fan profile; the active profile follows the strategy tier automatically (fixed strategies pin their profile):

- **自动** — Auto entry point; fan behavior follows the current tier
- **安静** — Quiet: gentle curve, no stable zone, long down-hold (20 s)
- **日常** — Daily: acoustic stable zone 45–50% (hold 48%)
- **代码** — Code: stable zone 50–55% (hold 53%), slow ramp
- **重负载** — Heavy: aggressive curve, earlier cooling, CPU/GPU cross-fan coupling

All values are editable in the Profiles & curves tab. Fan curves, temperature thresholds, and safety logic are not modified by profile changes.

### Write verification & external override detection

Every EC duty write in Active mode is verified asynchronously: a 50 ms readback confirms the write took effect, and a 1000 ms readback confirms it stuck. Three consecutive mismatched readbacks against the last written duty confirm an **external override** (another controller fighting the EC register); the program then restores Auto, drops to ReadOnly, and logs the event. Readback values and override state are shown on the dashboard and recorded in the CSV log.

### Control Center takeover

In Active mode the program briefly stops `CCDCHUService` / `XTU3SERVICE` and the FnKey process, then writes CPU power limits through the installed CLEVO Control Center SDK (with readback confirmation and rollback). The takeover is recorded in a lease file; on exit, sleep, or crash a dedicated lease watchdog restores the original service and FnKey state.

## Data locations

The application writes to:

```text
%LOCALAPPDATA%\X15FanControl\
```

This includes `config.json`, heartbeat state, CSV logs, application logs, watchdog logs, and the Control Center lease file. CSV logs are retained for 7 days by default.

## Architecture

- **X15FanControl** — WinForms GUI (x86): dashboard, profiles, calibration, tray, control loop
- **X15FanCore** — Core library: EC wrapper, fan curves, temperature filter, control engine, adaptive power tiers, heartbeat, lease, telemetry client
- **X15FanWatchdog** — Independent process that restores fans to Auto (and Control Center, in lease-only mode) if the main program exits or stops beating
- **X15GpuTelemetry** — x64 helper that runs nvidia-smi and outputs JSON telemetry via stdout (process-tree cleanup via `taskkill /T`)
- **X15XtuBridge** — Short-lived bridge for Control Center / Intel XTU power writes, with range validation and readback verification

## License

The source code, scripts, and documentation in this repository are licensed under the MIT License. See `LICENSE` for details.

**Not covered by MIT license:**
- `ClevoEcInfo.dll` — third-party binary, not distributed with this repository
- NTPort driver — third-party component
- NVIDIA nvidia-smi — third-party tool
