# ThermalDesk

Experimental native Windows replacement for the **specific Thermaltake devices in this PC**. No Electron, browser server, cloud account, or LibHWInfo process. English UI and configuration.

## Start

Extract the portable ZIP and run `ThermalDesk.exe`. It includes its .NET 10 runtime. No installer is required.

The app starts in **PREVIEW**: simulated readings, no sensor polling, and no USB commands. Closing TT RGB Plus is not necessary for preview.

For actual hardware:

1. Close TT RGB Plus and other fan/RGB controller applications yourself.
2. In **Displays**, enable the desired LCD. Start with Dashboard, 1 FPS.
3. Click **Start live control** and confirm. Check both the real screen and the log.
4. Test RGB separately: select the correct ports and LED counts under **Cooling + RGB**, enable RGB, restart the live session.
5. Only consider fan control after verifying physical wiring and independent thermal protection. Do not test experimental cooling unattended.

No existing TT installation, startup task, driver, or profile is automatically changed. **Stop** releases devices. To return to TT RGB Plus, stop/exit ThermalDesk first, then start TT RGB Plus.

## Device support

| USB ID | Implementation | Validation status |
|---|---|---|
| `264A:233D` | 3.9-inch 480×128 LCD, MI_00 command + MI_01 frame channels | Protocol/unit tested; physical-device test pending |
| `264A:2328` | TOUGHLIQUID AX 480×480 LCD | 60-second physical dashboard test passed on firmware 1.0.5.4; other firmware unverified |
| `264A:232B` | Five-port USB fan controller + direct RGB | Protocol/unit tested; wiring, report descriptors, RPM and real colors must be verified |

Unknown IDs and unexpected report lengths are rejected, not guessed. Multiple physical controllers require explicit selection. HKC LCD firmware is not supported. Physical testing is necessary; a successful build is not a safety certification.

### Current target-PC acceptance status

Read-only feedback worked on all five fan ports (about 425–470 RPM at 30%).
The AX firmware replied as 1.0.5.4. A five-second test did not receive physical
confirmation. The repeated 60-second test with continuous heartbeat and an initial
status poll **passed**: the operator saw the dashboard and its updating clock.
This does not establish which of those changes resolved the earlier ambiguity.
CPU temperature is unavailable without the missing supported sensor-driver setup.
Zero-valued sentinel temperatures are rejected, not shown as real readings.
TT RGB Plus was restored after the check. ThermalDesk has not replaced live cooling.
The round dashboard labels have been enlarged following operator feedback; their
new physical size has not yet been confirmed on the target LCD.

This is **not** a universal replacement for every TT product or feature: no PSU control, TOUGHRAM RGB, firmware update, Razer/cloud integration, video-file decoder or vendor mini-games. Motherboard fan headers and the AIO pump remain under their existing BIOS control unless physically connected to and explicitly managed through the supported USB controller.

## Features

- CPU/GPU temperature and load, memory usage, and app CPU/RAM/thread/handle counters.
- Two independent LCDs: telemetry dashboard, image/GIF, or primary-screen mirroring; rotation, brightness, overlays, 1–10 FPS.
- Image/GIF fitting preserves aspect ratio. GIF frames are sampled at the configured refresh rate; no video decoder runs in the background.
- Per-port Preserve / Fixed / Curve cooling, fan/pump roles, minimum speeds, sensor selection, RPM feedback.
- Per-port static RGB with a selectable color and LED count. A bounded keepalive maintains direct lighting.
- JSON profiles, bounded in-memory log, diagnostics export, tray operation, optional preview-only sign-in startup.
- Latest-snapshot telemetry and separate device loops: a stalled display does not block fan control. No thread per sensor, fan, or LED.

## Cooling rules — read before enabling

`Preserve` means **no fan-speed commands**, not "restore BIOS". The USB controller has no verified BIOS hand-back command.

Managed channels:

- Start at 100% before evaluating temperatures.
- Missing/invalid CPU temperature, stale data over 5 seconds, or selected missing curve sensor → request 100%.
- CPU or GPU at/above 85 °C → request 100% without ramp delay.
- Fan minimum at least 30%; configured pump minimum at least 60%. These are conservative software limits, not manufacturer-certified safe speeds.
- Cooling increases are immediate. Decreases are limited to 3 percentage points/second and use 2 °C downward hysteresis.
- Repeated zero-RPM readings or a detected controller error → close/retry, requesting 100% on managed ports if USB still works.
- Normal stop/exit requests 100% on managed ports. It does not restore the earlier speed or a firmware curve.

**A killed/frozen process, lost USB connection, driver fault, power loss or pre-login interval cannot be made safe by a process-exit handler.** No hardware watchdog has been verified. Keep BIOS/firmware cooling and thermal protection independent of this experimental software. Do not assume an exit-time request succeeded after hardware failure. Do not lower pump speed based only on its name or temperature readings.

Some CPU sensors require administrator rights and a supported LibreHardwareMonitor/PawnIO installation. ThermalDesk does not install drivers, turn off Defender, disable driver protections or fall back to an unsafe unverified driver. If CPU temperature is unavailable, fix sensor access before using curves; the app requests safe duty meanwhile.

## Profiles and privacy

Settings: `%LOCALAPPDATA%\ThermalDesk\settings.json`. Save/import changes are applied on the next session start. A saved profile **never auto-starts live control**.

**Start at sign-in** toggles one current-user startup entry. It opens the app in the tray in preview mode, does not run before login and is not a background cooling service.

No network port is opened. Diagnostics are saved only when requested and can contain local HID paths and logs. Screen mirroring can expose desktop content on the physical LCD; it is opt-in. Existing credentials are neither read nor embedded.

## Build and test

Requires the .NET **10** SDK on Windows x64. Dependencies are pinned; NuGet lock files are included. A portable build bundles the runtime.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Portable
.\dist\ThermalDesk-win-x64\ThermalDesk.exe
```

Use `-Dotnet C:\path\to\dotnet.exe` for a private SDK. Do not run the build as administrator unless your build environment requires it.

Read-only device enumeration (no HID commands or sensor initialization):

```powershell
.\ThermalDesk.exe --diagnose > thermaldesk-devices.json
```

Explicit bounded acceptance checks (use full output paths):

```powershell
# No USB access; can run alongside TT. Exit 2 if CPU temperature is unavailable.
.\ThermalDesk.exe --sensor-check C:\path\sensor-report.json

# Close TT and other controller applications first. No fan-speed or RGB writes.
.\ThermalDesk.exe --fan-status-check C:\path\fan-report.json
.\ThermalDesk.exe --aio-firmware-check C:\path\firmware-report.json

# Operator must observe the physical LCD during this 60-second test.
# Changes LCD brightness/image only; keep fan settings untouched and restore TT afterward.
.\ThermalDesk.exe --aio-display-check C:\path\display-report.json
```

An error-free USB transfer does not prove the physical image is visible. Operator
confirmation is mandatory; do not bypass an unsupported firmware/size gate.

Render the simulated UI without opening devices:

```powershell
.\ThermalDesk.exe --render-preview preview.png
```

Tests cover packet boundaries/reassembly, hardware response validation, cooling decisions, stale/invalid sensors, pump floor, hysteresis, configuration validation and profile persistence. They do not exercise a physical controller. See `VALIDATION.txt` for the actual build/test/hardware status of this release.

## Troubleshooting

- **Device busy/conflict:** close other controller applications; do not run them alongside ThermalDesk.
- **Unexpected report size / unsupported AX firmware:** stop; export diagnostics. Do not change a VID/PID to bypass the gate.
- **No CPU temperature:** run with appropriate privileges and check the official LHM/PawnIO requirements. Do not disable security protections.
- **Wrong colors:** stop RGB, verify physical LED count and controller model before changing packet formats.
- **Zero RPM:** stop experimental control, inspect cabling and restore the existing proven cooling arrangement.
- **High load:** use Dashboard/static image, 1 FPS, 2-second sensor polling; avoid screen mirroring and large animated media. Inspect the app's measured counters rather than thread count alone.

Independent project; not affiliated with Thermaltake. GPL-3.0-or-later. Protocol provenance and dependency licenses: `THIRD-PARTY-NOTICES.txt`.
