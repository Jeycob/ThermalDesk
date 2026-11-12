# ThermalDesk

A lightweight Windows app for temperatures, fans, RGB and small LCD dashboards.
Native UI. No browser, cloud account or LibHWInfo process.

## Install and start

1. Run **ThermalDesk-0.3.21-Setup-x64.exe**, or extract the portable ZIP.
2. Approve the administrator prompt; some CPU sensors need it.
3. Open **Sensors** and check that the correct CPU/GPU and readings appear.
4. Configure outputs in **Displays** and **Cooling + RGB**.
5. Choose **Start live control**. Preview uses sample data, not hardware control.

The .NET runtime is included. The installer is not digitally signed; use a trusted
build. Before upgrading, choose **Stop**, then **Exit** from the tray icon. Setup
will not terminate a cooling app. Your saved profile is kept.

## Your last profile

Valid changes save automatically. In Live mode, curve, color and widget changes
apply without Stop / Start. Changing controller ownership or wiring still needs Stop.
**Save now** is an optional immediate save.

- **Settings → Resume my saved profile when ThermalDesk opens** activates saved
  devices next time you open the app. It does not add Windows startup.
- Older profiles already armed for Live at sign-in inherit resume behavior.
  New, unarmed profiles stay in Preview.
- **Start at sign-in** separately configures disabled, Preview, or elevated Live
  startup. Installation does not enable it for a new user.
- Hold **Shift**, or run `ThermalDesk.exe --preview --show-window`, to bypass
  automatic control. Damaged/recovered profiles cannot automatically start hardware.
- **Minimize after startup** hides the window. **Minimize to tray when closed**
  makes X / Alt+F4 hide it without stopping devices. Tray **Exit** actually quits.

Profiles: `%LOCALAPPDATA%\ThermalDesk\settings.json`.
Use **Import / export** to transfer one. Import shows a summary and backs up
the current profile before replacement. Startup choices remain local to this PC.

## Fans and RGB

Identify each port before enabling control. Choose **Silent**, **Custom** or **Full**
for fan curves. The BIOS-controlled pump is not changed by a USB fan profile.
Duty percentage is a request, not measured RPM or power.

For RGB, enable the global master and configure each port. **Turn off** explicitly
keeps a selected port dark; disabling the global master releases lighting control.
LED count must match connected lighting; it is not a fan-speed setting.
Hover over controls for explanations.

Do not run TT RGB Plus, OpenRGB, Fan Control or SignalRGB against the same USB
controller. Conflicts block control; ThermalDesk does not kill apps.

**Keep independent BIOS/firmware thermal protection.** Experimental USB control
cannot guarantee safety after a crash, frozen process or disconnected cable.
Missing sensors do not trigger an invented full-speed fallback. Genuine critical
temperatures still use the emergency policy. Verify physical operation.

## LCD widgets

Open **Displays → Edit widgets**. Choose a horizontal, vertical or round layout,
then edit size, font, color, source and gauge/graph style. Both displays have
independent settings and rotation.

- Sources include temperatures, clocks, VRAM, load, fans and power.
- **Observed last 24h energy** accumulates sampled power, not watts multiplied by
  24. Whole-PC power is approximate, not a wall meter. Coverage shows the measured
  part; missing/offline time is excluded. History persists through normal restarts.
- **AI log** shows the last reported generation/prompt tokens/s, context and age
  when provided. OLD means stale, not current streaming speed. LM Studio backend
  logs are preferred. Choose **RawTail** for raw latest lines.
- Game FPS/latency needs a suitable log or per-process PresentMon CSV. This is
  not an injected live overlay; PC rendering latency is not network ping.

## Hardware and performance

Intel/AMD CPU and Intel/AMD/NVIDIA GPU telemetry works where the hardware,
driver and permissions expose sensors. Missing readings stay unavailable.
**Sensors** shows the device and source. Shared RAM is not VRAM.
Monitoring-only mode works without Thermaltake USB devices.

USB control is limited to these implemented Thermaltake devices:

| Device | USB ID |
| --- | --- |
| 3.9-inch LCD panel | `264A:233D` |
| TOUGHLIQUID AX LCD | `264A:2328` |
| Five-port fan controller | `264A:232B` |

Unknown firmware/descriptors are rejected. This is not universal motherboard,
RAM RGB, pump or fan-controller support. Check physical compatibility first.

For low overhead, leave optional process monitoring off unless needed. Longer
process/log intervals reduce work. Hidden UI refresh slows down automatically;
control sampling is unchanged. Technical thread/handle counters are opt-in.
Log reads and histories are bounded; there are no full-log background scans.

## Troubleshooting

- **CPU values missing:** check administrator access and supported PawnIO/LHM
  hardware access. Do not disable Windows security protections to force a reading.
- **LCD stays on its logo:** check the correct device, enabled output and Live
  mode. Stop competing software. Verify the USB data cable, not just power.
- **Changes seem unapplied:** read the status line. Compatible valid edits apply
  automatically; incomplete or unsafe settings show an explanation.
- **Automatic control blocked:** resolve the device/conflict in Overview and
  **Log + diagnostics**, then start Live. Recovery opens safely in Preview.
- **Need help:** export diagnostics and review before sharing. Names and local
  paths may be included; log-widget contents are excluded from telemetry exports.

## Build and tests

Use .NET SDK 10 on Windows:

```powershell
dotnet run --project tests/ThermalDesk.Tests -c Release
dotnet publish src/ThermalDesk.App -c Release -r win-x64 --self-contained true -o dist/ThermalDesk-win-x64
.\dist\ThermalDesk-win-x64\ThermalDesk.exe --widget-check C:\Temp\ThermalDesk-checks
.\package.ps1
```

Core and widget checks use fixtures/mock devices, not physical USB control.
See [VALIDATION.txt](VALIDATION.txt) for test evidence and limitations.
GPL-3.0-or-later; see [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
