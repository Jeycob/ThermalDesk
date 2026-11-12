# ThermalDesk

Experimental native Windows replacement for the **specific Thermaltake devices in this PC**. No Electron, browser server, cloud account, or LibHWInfo process. English UI and configuration.

## Start

Extract the portable ZIP and run `ThermalDesk.exe`. It includes its .NET 10 runtime. No installer is required.

The app starts in **PREVIEW**: simulated readings, no sensor polling, and no USB commands. Closing TT RGB Plus is not necessary for preview.

For actual hardware:

1. Close TT RGB Plus and other fan/RGB controller applications yourself.
2. In **Displays**, enable the desired LCD. Start with Dashboard, 1 FPS.
3. Click **Start live control** and confirm. Check both the real screen and the log.
4. Test RGB separately: select the correct ports and LED counts under **Cooling + RGB**, enable RGB, then confirm live access. Later valid curve/color/widget edits apply automatically without reopening the controller.
5. Only consider fan control after verifying physical wiring and independent thermal protection. Do not test experimental cooling unattended.

No existing TT installation, startup task, driver, or profile is automatically changed. **Stop** releases devices. To return to TT RGB Plus, stop/exit ThermalDesk first, then start TT RGB Plus.

### Changing settings without Stop/Apply

Valid edits to curves, Silent/Custom/Full, RGB effects and LCD widgets save and
apply automatically after a 450 ms pause. They do not reconnect the controller.
Read-only RPM/duty feedback never triggers another settings update. Temperature
hysteresis and downward speed smoothing still apply, so a quieter target can
take several seconds to reach; the table shows requested, target and reported duty.

Changing enabled devices, wiring/port roles or connection settings still requires
an explicit stop and new confirmation. A latched USB fault is not silently retried.
On initial cooling startup the app waits up to 20 seconds for valid temperatures
**before opening the fan controller**. Changing the pending profile during that
wait uses the latest settings. Timeout/cancellation sends no fan-speed commands.

### Damaged or unreadable profile

The app opens **Preview only** with a visible recovery warning. Automatic live
startup, `--live-once` and start-minimized are bypassed for that launch. No device
or fan control is enabled by recovery.

Normal saves keep the last valid profile as `settings.json.previous`. A malformed
profile is copied to a timestamped `.invalid-…json` file before replacement; the
original is not silently discarded. Recovery can use the valid previous profile's
editable settings, but leaves hardware disabled until you review and enable it.
Profiles above 1 MiB are not duplicated or implicitly overwritten. Unreadable
profiles remain in place. These are recovery aids, not a replacement for backups.

Both LCDs are disabled in a fresh profile. **Start live control** now rejects an
empty selection instead of silently running sensors with no display. The lower
status bar shows **Limited user** or **Administrator**. On this PC, use
**Restart as admin** to read CPU sensors through the official PawnIO driver.
This stops the current session, requests Windows elevation and reopens Preview;
confirm live control again afterward. It does not install a driver or change BIOS.

## Device support

| USB ID | Implementation | Validation status |
|---|---|---|
| `264A:233D` | 3.9-inch 480×128 LCD, MI_00 command + MI_01 frame channels | Protocol/unit tested; currently absent from target-PC USB inventory |
| `264A:2328` | TOUGHLIQUID AX 480×480 LCD | 60-second physical dashboard test passed on firmware 1.0.5.4; other firmware unverified |
| `264A:232B` | Five-port USB fan controller + direct RGB | 40-second mixed fan/RGB test and 90-second five-fan profile test passed; physical colors/long-term operation still unverified |

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
The operator confirmed that the enlarged round CPU dashboard works well.

Version 0.3.1 exposed a real mixed-control bug: RGB/SET acknowledgements accumulated
in the telemetry queue, but the reader skipped only four reports before failing.
Automatic reconnect then repeatedly requested safe 100% duty, producing fan pulses
and RGB resets. Version 0.3.2 matches the entire telemetry header with a bounded
1200 ms / 128-report budget. A real 40-second test skipped 160 unrelated reports
without a failure; duty remained 70%, approximately 1440 RPM. A separate 90-second
test exercised all five TT custom curves, Full and four in-place updates with one
controller connection. These short tests are not unattended-use certification.

The second chassis LCD is remembered in the TT profile but does not currently
enumerate as a present USB device. No guessed device ID or firmware command is
used to work around a missing connection. Its USB data/power connection needs
checking before a physical test is possible.

This is **not** a universal replacement for every TT product or feature: no PSU control, TOUGHRAM RGB, firmware update, Razer/cloud integration, video-file decoder or vendor mini-games. Motherboard fan headers and the AIO pump remain under their existing BIOS control unless physically connected to and explicitly managed through the supported USB controller.

## Features

- CPU/GPU temperature and load, memory usage, and app CPU/RAM/thread/handle counters.
- **Sensors** selects one GPU for all GPU metrics; automatic prefers NVIDIA, then AMD, then Intel. You can pin its identifier. A missing pinned GPU stays unavailable instead of silently substituting integrated graphics. The page shows the exact sampled device and temperature source.
- CPU display prefers package/Tctl-Tdie. Its highest actual temperature sensor is retained separately for critical-heat protection and as an optional `CpuMaximumTemperature` LCD widget. Thermal distance/TjMax limits are not treated as measured temperatures. RAM usage is physical memory, not virtual-memory load.
- Two independent LCDs: telemetry dashboard, image/GIF, or primary-screen mirroring; rotation, brightness, overlays, 1–10 FPS.
- Native per-LCD widget editor: drag/resize, numeric bounds, font picker, size, bold, custom text, colors, alignment and sensor bindings. Editable radial gauges include range, thickness, track color and arc angles. **Undo/Redo** (Ctrl+Z / Ctrl+Y outside the property text editor) retains up to 50 states for this dialog only; Cancel discards all its changes. A mouse drag is one undo step, including when another window interrupts it.
- Image/GIF fitting preserves aspect ratio. GIF frames are sampled at the configured refresh rate; no video decoder runs in the background.
- Per-port Preserve / Fixed / Curve cooling, Silent / Custom / Full presets, named locations, draggable curve points, TT nine-point curve import, fan/pump roles, minimum speeds, CPU/GPU sensor selection and optional read-only RPM feedback.
- The cooling table separates **request → final target %**, **controller-reported %** and **RPM**. A lower target takes effect automatically; the command decreases smoothly rather than restarting the fans. Percentages are not electrical watts. Feedback is cleared after Stop/fault so old RPM cannot look live.
- Per-port Static, Breathing, Blink, ColorCycle, RainbowWave, PaletteWave and Temperature RGB effects. PaletteWave blends 2–8 comma-separated hex colors around the LED ring (for example `FF0000, AA00FF`). Wave direction, color, brightness, LED count and period are editable. Existing profiles keep their original effects. Animations are capped at 5 FPS; thermal decisions run first.
- The port editor shows a live preview of its planned RGB frame. It is not a claim of physical LED feedback; close the editor to accept and automatically apply the change.
- Hidden LCD previews do not render while you are editing cooling or viewing another tab. Physical LCD output has its own loop and is unaffected by the selected tab.
- Editor-only fields (color/effect/palette/name) also trigger automatic application, even when the visible grid values did not change. No repeated Stop/Apply is needed. Wiring/device enablement still requires explicit Stop/rearming.
- JSON profiles, bounded in-memory log, diagnostics export, tray operation and opt-in preview or elevated saved-live sign-in startup.
- Latest-snapshot telemetry and separate device loops: a stalled display does not block fan control. No thread per sensor, fan, or LED.

## Cooling rules — read before enabling

`Preserve` means **no fan-speed commands**, not "restore BIOS". The USB controller has no verified BIOS hand-back command.

Managed channels:

- Wait briefly for initial sensor data, then use the validated curve. Missing/invalid initial data aborts before taking control; it does not command full speed.
- Missing/invalid CPU temperature, stale data over 5 seconds, or a missing selected curve sensor → release software control and request the captured original duty back. Restore the proven vendor controller; a static restored duty is not a dynamic firmware curve.
- CPU package/maximum actual sensor or selected GPU core at/above 85 °C → request 100% without ramp delay. GPU hotspot/VRAM and thermal-distance limits are not silently treated as GPU core temperature.
- Fan minimum at least 30%; configured pump minimum at least 60%. These are conservative software limits, not manufacturer-certified safe speeds.
- Cooling increases are immediate. Decreases are limited to 3 percentage points/second and use 2 °C downward hysteresis.
- Repeated zero-RPM readings or a detected controller error → request the captured original duty if USB still works, close and latch the fault. No forced 100% fallback or automatic reconnect loop. Resolve the cause and restore vendor control before retrying.
- Normal stop/exit requests each managed port's captured original duty. It does not restore a firmware curve. Only a genuine critical-temperature condition retains emergency cooling; Full and high-temperature curve requests remain explicit operations.
- Failed restoration is reported in the controller log, not silently treated as success and not replaced with an automatic full-speed command.

**A killed/frozen process, lost USB connection, driver fault, power loss or pre-login interval cannot be made safe by a process-exit handler.** No hardware watchdog has been verified. Keep BIOS/firmware cooling and thermal protection independent of this experimental software. Do not assume an exit-time request succeeded after hardware failure. Do not lower pump speed based only on its name or temperature readings.

Some CPU sensors require administrator rights and a supported LibreHardwareMonitor/PawnIO installation. ThermalDesk does not install drivers, turn off Defender, disable driver protections or fall back to an unsafe unverified driver. If CPU temperature is unavailable, fix sensor access before taking over curves; keep the vendor controller active meanwhile.

## Profiles and privacy

### Configure fans and lighting

1. Open **Cooling + RGB → Edit…** on the correct physical port.
2. Leave **Preserve** to keep speed commands off. For a verified fan, choose **Silent**, **Custom** or **Full**. The main window's quick buttons affect already-managed fans only, not pumps or unknown ports. Silent is a temperature curve, not fan-off; Custom restores your retained custom curve; Full requests 100%. **Import TT curve…** accepts nine percentages corresponding to 20,30,…,100 °C, including small dips.
3. For lighting, enable that port's RGB, choose its physical LED count, effect, brightness and period. Blank per-port color inherits the main window color. A 1-second minimum period prevents rapid strobing; blink remains opt-in.
4. Enable the appropriate main **Cooling** / **RGB** checkbox and start a session once. After that, valid curves/colors/widgets save and apply automatically, about 450 ms after editing; a fan decision reaches the next control tick (normally within 2 seconds). No Stop/Apply sequence is needed. Invalid partial edits stay pending. Changing the controller, managed ports, wiring, polling or LCD brightness still requires Stop first; it never silently restarts all fans.
5. **Monitor controller RPM** polls all five ports without speed writes; it still opens and queries the USB controller. Do not run it alongside TT.

Duty **%** is the requested controller output, not watts, RPM or a confirmed
electrical PWM/DC selector. The known protocol does not expose safe PWM/DC mode
switching. No such switch is guessed. The BIOS-controlled pump remains unchanged.
RGB has packet tests and a mixed-control timing test, not yet a physical color/LED-count confirmation.

### Customize the LCDs

1. Open **Displays → Edit widgets…** under the correct device.
2. Select a widget on the canvas or in the list. Drag to move; drag the bottom-right handle to resize. Arrow keys move 1 pixel; Shift moves 10.
3. Change **Source**, **Text**, **ValueFormat**, geometry, font and colors in the properties panel. **Font…** opens the font picker. Transparent background leaves the canvas/image visible.
   Set **Kind = RadialGauge** for a numeric progress ring. The **Radial gauge** category controls its range, thickness, track color, start/sweep angles and optional center text. Missing data leaves only the track, never a fabricated reading.
4. Text such as `CPU {value} °C` inserts the selected sensor. Numeric formats: `0`, `0.0`. Clock: `HH:mm:ss`. Date: `yyyy-MM-dd`. No expressions or code are evaluated. Missing sensors show `—`.
5. Sources: CPU/GPU core, GPU hotspot/memory temperature, CPU/GPU load, RAM usage, clock/date and port 1–5 fan RPM/duty. Fan data requires an active controller session; preview does not poll hardware.
6. **Add / Duplicate / Delete** manage widgets; **Back / Front** change overlap order. **Canvas color…** changes the background. **Export PNG…** saves a full-resolution frame.
7. Close the widget editor with **Apply layout**. The main window automatically saves and updates the running display without restarting fans. In Preview, only simulated rendering updates. Editing inside an open modal designer remains a preview until you accept the layout.

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
