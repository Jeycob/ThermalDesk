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

Both LCDs are disabled in a fresh profile. **Start live control** now rejects an
empty selection instead of silently running sensors with no display. The lower
status bar shows **Limited user** or **Administrator**. On this PC, use
**Restart as admin** to read CPU sensors through the official PawnIO driver.
This stops the current session, requests Windows elevation and reopens Preview;
confirm live control again afterward. It does not install a driver or change BIOS.

## Device support

| USB ID | Implementation | Validation status |
|---|---|---|
| `264A:233D` | 3.9-inch 480×128 LCD, MI_00 command + MI_01 frame channels | Protocol/unit tested; physical-device test pending |
| `264A:2328` | TOUGHLIQUID AX 480×480 LCD | 60-second physical dashboard test passed on firmware 1.0.5.4; other firmware unverified |
| `264A:232B` | Five-port USB fan controller + direct RGB | Port 1 curve test and all five RPM readbacks passed; RGB colors/unattended operation unverified |

Unknown IDs and unexpected report lengths are rejected, not guessed. Multiple physical controllers require explicit selection. HKC LCD firmware is not supported. Physical testing is necessary; a successful build is not a safety certification.

### Current target-PC acceptance status

Read-only feedback worked on all five fan ports (about 425–470 RPM at 30%).
The AX firmware replied as 1.0.5.4. A five-second test did not receive physical
confirmation. The repeated 60-second test with continuous heartbeat and an initial
status poll **passed**: the operator saw the dashboard and its updating clock.
This does not establish which of those changes resolved the earlier ambiguity.
After an approved installation of official signed PawnIO 2.2.0, elevated sensor
checks read CPU temperature successfully. GPU core, hotspot and memory temperature
are separate sources; hotspot is not used as the GPU-core safety temperature.
Zero-valued sentinel temperatures are rejected, not shown as real readings.
An attended port-1 curve test passed: 30% / 429 RPM → 78% / 1577 RPM → 98% / 1974 RPM.
Original 30% duty was restored and confirmed; TT was restarted. Other ports were
not speed-controlled. ThermalDesk has not replaced unattended cooling.
The round dashboard labels have been enlarged following operator feedback; their
new physical size has not yet been confirmed on the target LCD.

This is **not** a universal replacement for every TT product or feature: no PSU control, TOUGHRAM RGB, firmware update, Razer/cloud integration, video-file decoder or vendor mini-games. Motherboard fan headers and the AIO pump remain under their existing BIOS control unless physically connected to and explicitly managed through the supported USB controller.

## Features

- CPU/GPU temperature and load, memory usage, and app CPU/RAM/thread/handle counters.
- Two independent LCDs: telemetry dashboard, image/GIF, or primary-screen mirroring; rotation, brightness, overlays, 1–10 FPS.
- Native per-LCD widget editor: drag/resize, numeric bounds, font picker, size, bold, custom text, colors, alignment and sensor bindings.
- Image/GIF fitting preserves aspect ratio. GIF frames are sampled at the configured refresh rate; no video decoder runs in the background.
- Per-port Preserve / Fixed / Curve cooling, draggable curve points, fan/pump roles, minimum speeds, CPU/GPU sensor selection and optional read-only RPM feedback.
- Per-port Static, Breathing, Blink, ColorCycle, RainbowWave and Temperature RGB effects. Color, brightness, LED count and period are editable. Animations are capped at 5 FPS; thermal decisions run first.
- JSON profiles, bounded in-memory log, diagnostics export, tray operation and opt-in preview or elevated saved-live sign-in startup.
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

### Configure fans and lighting

1. Open **Cooling + RGB → Edit…** on the correct physical port.
2. Leave **Preserve** to keep speed commands off. For a verified fan, select **Fixed** or **Curve**, its temperature source and minimum duty. Drag curve points or edit the numeric table. Apply and save the profile.
3. For lighting, enable that port's RGB, choose its physical LED count, effect, brightness and period. Blank per-port color inherits the main window color. A 1-second minimum period prevents rapid strobing; blink remains opt-in.
4. Enable the appropriate main **Cooling** / **RGB** checkbox. Restart and confirm the live session to apply. Editing a curve never silently commands a fan.
5. **Monitor controller RPM** polls all five ports without speed writes; it still opens and queries the USB controller. Do not run it alongside TT.

Duty **%** is the requested controller output, not watts, RPM or a confirmed
electrical PWM/DC selector. The known protocol does not expose safe PWM/DC mode
switching. No such switch is guessed. The BIOS-controlled pump remains unchanged.
RGB patterns have automated packet tests, not yet a physical color/LED-count check.

### Customize the LCDs

1. Open **Displays → Edit widgets…** under the correct device.
2. Select a widget on the canvas or in the list. Drag to move; drag the bottom-right handle to resize. Arrow keys move 1 pixel; Shift moves 10.
3. Change **Source**, **Text**, **ValueFormat**, geometry, font and colors in the properties panel. **Font…** opens the font picker. Transparent background leaves the canvas/image visible.
4. Text such as `CPU {value} °C` inserts the selected sensor. Numeric formats: `0`, `0.0`. Clock: `HH:mm:ss`. Date: `yyyy-MM-dd`. No expressions or code are evaluated. Missing sensors show `—`.
5. Sources: CPU/GPU core, GPU hotspot/memory temperature, CPU/GPU load, RAM usage, clock/date and port 1–5 fan RPM/duty. Fan data requires an active controller session; preview does not poll hardware.
6. **Add / Duplicate / Delete** manage widgets; **Back / Front** change overlap order. **Canvas color…** changes the background. **Export PNG…** saves a full-resolution frame.
7. **Apply layout → Save profile**. Click **Preview** to restart simulated rendering with the new design. Live changes require stopping and explicitly confirming a new live session; editing never silently restarts fans or writes USB.

Layouts are independent for the 480×128 panel and 480×480 AX LCD. Coordinates are
before rotation; orientation changes reframe the layout. The circle guide shows
the round LCD crop. Limits: 40 widgets/display, fonts 6–120 pixels. Long text clips
to its box. Cancel leaves input settings unchanged; Defaults resets only that LCD.

### Saved settings

Settings: `%LOCALAPPDATA%\ThermalDesk\settings.json`. Save/import changes apply on the next session start. Manual launches always start in preview. Importing a profile clears its automatic-live flag.

**Start at sign-in** offers:

- **Preview:** current-user Run entry; minimized preview, no hardware access.
- **Saved LIVE profile:** explicit confirmation, an armed saved profile and an elevated interactive scheduled task named `ThermalDesk-AtSignIn`. Run the app as administrator once to register it. No password is stored. Only the saved enabled devices are opened.
- **Remove:** deletes only ThermalDesk's own entry/task.

Hold **Shift** during launch to bypass automatic live control. A running TT/OpenRGB/FanControl/SignalRGB blocks live startup; it is not killed. No existing vendor startup setting is changed. Test devices first, then disable vendor startup yourself if you choose to switch. This runs **after Windows sign-in**, not before login or as a cooling service. BIOS/firmware protection must remain independent.

For an explicitly approved one-time launch, `ThermalDesk.exe --live-once` opens
the saved enabled devices without registering startup. This is real hardware
access, not a test flag. It still validates the profile, administrator access for
cooling, device descriptors, conflicts and sensor fail-safe policy. Shift bypasses it.

The automatic-live launch policy has automated tests; an actual sign-in and physical
hardware handover still require operator acceptance. It is not enabled by default.

An attended diagnostic launch may add `--status-output C:\path\live-status.json`.
This opt-in local file contains sensor readings, controller feedback and recent
status messages every five seconds. No server is opened; omit the switch for
ordinary use. A successful status report is not physical LCD/color confirmation.

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

# Explicit attended port-1-only curve test; TT closed, known wiring required.
.\ThermalDesk.exe --fan-curve-acceptance C:\path\fan-curve-report.json
```

An error-free USB transfer does not prove the physical image is visible. Operator
confirmation is mandatory; do not bypass an unsupported firmware/size gate.

Render the simulated UI without opening devices:

```powershell
.\ThermalDesk.exe --render-preview preview.png
.\ThermalDesk.exe --widget-check C:\path\widget-test-output
```

Tests cover protocol boundaries, cooling safety, sensors, profile persistence, widget formatting/bounds, independent layouts, UI editing actions and repeated rendering. Automated tests do not exercise hardware. See `VALIDATION.txt` for actual acceptance status.

## Troubleshooting

- **Device busy/conflict:** close other controller applications; do not run them alongside ThermalDesk.
- **Unexpected report size / unsupported AX firmware:** stop; export diagnostics. Do not change a VID/PID to bypass the gate.
- **No CPU temperature:** run with appropriate privileges and check the official LHM/PawnIO requirements. Do not disable security protections.
- **Wrong colors:** stop RGB, verify physical LED count and controller model before changing packet formats.
- **Zero RPM:** stop experimental control, inspect cabling and restore the existing proven cooling arrangement.
- **High load:** use Dashboard/static image, 1 FPS, 2-second sensor polling; avoid screen mirroring and large animated media. Inspect the app's measured counters rather than thread count alone.

Independent project; not affiliated with Thermaltake. GPL-3.0-or-later. Protocol provenance and dependency licenses: `THIRD-PARTY-NOTICES.txt`.
