# ThermalDesk

A lightweight Windows app for temperatures, fans, RGB and small LCD dashboards.
Native UI. No browser, cloud account or LibHWInfo process.

## Install and start

1. Run a published **ThermalDesk Setup-x64.exe**, or extract the portable ZIP.
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
**Profile… → Save now** is an optional immediate save. Session state and profile
save status appear on separate lines. **Applied to devices · NOT SAVED** means
the current Live changes took effect, but were not stored for the next launch.
Correct the reported file/disk problem and use **Save now** to retry.

- **Settings → Resume my saved profile when ThermalDesk opens** activates saved
  devices next time you open the app. It does not add Windows startup.
- Older profiles already armed for Live at sign-in inherit resume behavior.
  New, unarmed profiles stay in Preview.
- After your first confirmed **Start live control**, the app remembers that
  profile for the next launch. An explicit resume opt-out stays off. A minimized
  launch can resume when you explicitly enabled it. Imports never authorize Live.
- **Settings → Windows sign-in…** separately configures disabled, Preview, or
  elevated Live startup. Installation does not enable it for a new user. An
  existing task owned by another Windows user is left unchanged.
- Hold **Shift**, or run `ThermalDesk.exe --preview --show-window`, to bypass
  automatic control. Damaged/recovered profiles cannot automatically start hardware.
- **Minimize after startup** hides the window. **Minimize to tray when closed**
  makes X / Alt+F4 hide it without stopping devices. Tray **Exit** actually quits.

Profiles: `%LOCALAPPDATA%\ThermalDesk\settings.json`.
Use **Profile… → Import profile… / Export current profile…** to transfer one.
Import shows a summary and backs up the current profile before replacement.
Startup choices remain local to this PC.

## Fans and RGB

Identify each port before enabling control. Choose **Silent**, **Custom** or **Full**
for fan curves. The BIOS-controlled pump is not changed by a USB fan profile.
Duty percentage is a request, not measured RPM or power.

The **Cooling + RGB** table opens as a summary of each port's mode, lighting,
RPM, current request/curve target and controller-reported duty. Choose **Edit…**
for the port's **Fan control** and **RGB lighting** tabs. **Advanced port columns**
also exposes wiring, source, minimum/fixed duty, LED count and the raw curve.
The curve editor shows the configured curve, effective minimum and current
temperature/target; that preview is not a new command or measured fan speed.

Each active fan command is checked against controller-reported duty with a
±2 percentage-point tolerance. A mismatch gets a 1.5-second confirmation grace
and at most two retries, spaced by the same grace period. Persistent mismatch
latches a controller fault; changing curve targets does not restart that grace
window. Reported duty is controller feedback, not independent electrical or RPM
verification.

For RGB, enable the global master and configure each port. **OFF** explicitly
keeps a selected port dark; disabling the global master releases lighting control.
LED count must match connected lighting; it is not a fan-speed setting.
Hover over controls for explanations.

**Cooling + RGB → RGB schedule…** adds timed lighting. Try **Night preset**:
08:00 restores port brightness, 22:00 makes the managed ports dark. Times use the
PC's local clock. Choose every day, weekdays or weekend. Each entry stays active
until the next one, even across midnight or a restart. Brightness is a percentage
of the port's configured brightness; an optional color selects static lighting.
Global RGB OFF and explicit port OFF take priority. The schedule never changes
fan speeds, pump settings or unselected RGB ports.

Do not run TT RGB Plus, OpenRGB, Fan Control or SignalRGB against the same USB
controller. Conflicts block control; ThermalDesk does not kill apps.

**Keep independent BIOS/firmware thermal protection.** Experimental USB control
cannot guarantee safety after a crash, frozen process or disconnected cable.
Missing sensors do not trigger an invented full-speed fallback. Genuine critical
temperatures still use the emergency policy. Verify physical operation.

**Stop and faults request the captured pre-control duties back**, except while a
valid critical temperature requires retaining emergency duty. This is not a
handover to BIOS control or a hardware watchdog; restore and verify vendor
control when prompted. Only ports with an attempted speed write are restored.

Cooling sensor sampling and control deadlines are start-to-start. Due control,
sample freshness and critical temperatures are checked between HID operations,
including individual feedback reads, so a complete feedback/RGB sweep does not
add an extra polling interval. An in-progress USB transaction is not preempted.
Before the first speed write, current temperatures and cancellation are checked
again after opening the controller and capturing the port's baseline.

Automatic fan curves ignore brief rises for 4 seconds, then increase the requested
duty by at most 3 percentage points/second. A peak that ends during the hold does
not accelerate the fans. This applies to CPU, GPU and Maximum sources. Startup
uses the current curve directly. Fixed / Full commands and pump increases remain
immediate; valid critical CPU/core/GPU heat at 85 °C bypasses the filter. Existing
minimum duties, stale-sensor checks and gradual reductions remain active.

## LCD widgets

Open **Displays → Edit widgets**. Choose a horizontal, vertical or round layout,
then edit size, font, color, source and gauge/graph style. Both displays have
independent settings and rotation.

### Rotate widgets / pages

1. Select a widget and choose **Copy to next page** in the designer.
2. Change the copy's source or appearance. It uses the same position on another page.
3. Apply the layout. Under **Displays**, choose **Page seconds** (default 5).

**Page 0** is always visible. Pages 1–16 rotate as groups, so several widgets can
appear together. Assign pages directly in **Content → Page (0 = always)**.
Use the designer's **Preview page** selector to edit a specific page, show only
always-visible widgets, or intentionally overlay **All pages**. Clicking or
dragging the canvas only selects widgets visible on that preview page. Selecting
a widget from another page in the list switches the preview to its page.
Disabling rotation in the display settings shows all widgets on the physical LCD.
This changes LCD rendering only; it does not reconnect the cooling controller.

Clocks and page rotation advance using render time while sensor values retain
their original sample timestamp. A sensor dashboard dims and shows
**SENSOR DATA OLD** when readings are stale; missing initial data says
**Waiting for sensor data**. The display accounts for monitoring intervals up to
10 seconds, so a normal 10-second interval does not trigger a false five-second
warning. This display threshold does not change cooling's fixed five-second
stale-data guard. Clock/image-only pages continue without a sensor warning.

### Online image / webcam

In **Edit widgets → Add widget**, select **Online image / webcam**. Under
**Online image**, enter a direct HTTP(S) image URL and choose:

- **Snapshot:** JPEG/PNG, polled at your chosen interval. Start with 5–30 seconds.
- **Mjpeg:** a multipart MJPEG stream. Decode 1–5 FPS (default 2); set the LCD's
  separate **Frames / sec** value accordingly. Intermediate frames are discarded.

**Contain** shows the full image; **Cover** fills the widget by cropping. Rounded
corners are configurable. The last frame is marked when stale or disconnected.
Preview never makes network requests; fetching starts only in Live. Use Dashboard
with a custom layout, or enable the custom overlay over Image/Screen content.

This is not a web browser: webpages, YouTube, RTSP and HLS are not supported.
Redirects, cookies, proxy authentication and passwords embedded in URLs are not
supported. Use a direct trusted camera endpoint. HTTPS certificates are verified;
HTTP is unencrypted. URLs are stored in your local profile, so protect exports
that contain private query tokens. A source receives normal HTTP requests; photos
are not uploaded elsewhere and no network server is opened.

Limits: four distinct sources across both LCDs; JPEG/PNG up to 4 MiB and 4 MP;
one retained, downscaled frame per source. MJPEG reads are limited to about
2 MiB/s per source with reconnect backoff. No FFmpeg process or browser runtime.

- Sources include temperatures, clocks, VRAM, load, fans and power.
- **Observed last 24h energy** accumulates sampled power, not watts multiplied by
  24. Whole-PC power is approximate, not a wall meter. Coverage shows the measured
  part; missing/offline time is excluded. History persists through normal restarts.
- **AI log** shows the last reported generation/prompt tokens/s, context and age
  when provided. OLD means stale, not current streaming speed. LM Studio backend
  logs are preferred. Choose **RawTail** for raw latest lines.
- **Strata** native logs are also recognized: decode tokens/s, prefill tokens/s
  and reused/new prompt tokens. Wide AI widgets show **CACHE %**. These are the
  last reported inference values, not instantaneous GPU speed. Automatic
  discovery checks known local folders only; no recursive disk scan.
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
Unchanged LCD content reuses its JPEG; hidden rotation pages are not rendered.
Disabled LCD workers wake once per second rather than four times. RGB schedules
are resolved once per minute or profile change, not for every LED animation frame.
Network clients are created lazily; unused online widgets add no network handles.
If GPU process counters temporarily fail, they reconnect with a bounded retry
delay without requiring a full Live restart. Failed samples do not reuse old
process readings.

Cooling sensor reads use a dedicated sampler with start-to-start intervals. Slow
reads do not add a full polling delay. The five-second stale-data guard still
latches a cooling fault; port status includes the shared cause. The last fault's
reason, sample age and sampler timing are kept in
`%LOCALAPPDATA%\ThermalDesk\last-controller-fault.json` (one overwritten file).
The report also records the active sampling phase and hardware identifier, with
monotonic elapsed times. The last completed read duration is distinct from an
in-progress read: a short previous read does not rule out a later driver stall.
Diagnostic exports and optional acceptance status include the same sampler
snapshot. A stale-data fault still requires Stop followed by Start live control;
wait for Stop to finish before retrying.

When Windows suspends, ThermalDesk requests a stop and gives hardware cleanup a
short grace period. Unfinished operations stay owned by the original session;
new Live or Preview sessions are blocked until they finish. After waking, control
remains paused: verify the devices and choose **Start live control** explicitly.
Cleanup failures remain visible. A completed restore request is not confirmed
fan-duty readback or a hand-back to BIOS control.

## Troubleshooting

- **CPU values missing:** check administrator access and supported PawnIO/LHM
  hardware access. Do not disable Windows security protections to force a reading.
- **LCD stays on its logo:** check the correct device, enabled output and Live
  mode. Stop competing software. Verify the USB data cable, not just power.
- **Changes seem unapplied:** read the status line. Compatible valid edits apply
  automatically; incomplete or unsafe settings show an explanation.
- **NOT SAVED:** check the separate profile status. Changes may already be active
  in Live; fix the file/disk problem and choose **Profile… → Save now**.
- **Automatic control blocked:** resolve the device/conflict in Overview and
  **Log + diagnostics**, then start Live. Recovery opens safely in Preview.
- **Paused after Windows sleep:** wait for hardware cleanup to finish, verify
  cooling, then start Live. Repeated USB initialization timeouts require checking
  the affected device; increasing the telemetry freshness limit will not fix them.
- **Need help:** export diagnostics and review before sharing. Names and local
  paths may be included; log-widget contents are excluded from telemetry exports.

## Build and tests

Use Windows and the .NET SDK version pinned in [global.json](global.json)
(10.0.404):

```powershell
.\build.ps1 -Portable -OfflineChecks
.\package.ps1
```

The build runs Core checks and restores application dependencies in locked mode
for `win-x64`. **-OfflineChecks** also runs UI/widget/controller fixtures, sampler
regressions, preview rendering and startup migration checks. Reports and PNGs go
to `artifacts/checks`. The Windows CI workflow runs the same checks and retains
their outputs. Packaging refuses a binary whose version differs from the project.

Core and offline application checks use fixtures/mock devices, not physical USB
control.
Failed core checks print `FAIL` and exit with code 1 instead of leaving an
unhandled .NET exception / Windows error dialog. `--verify-error-reporting` is an
intentional failing-runner check, not the normal test command.
See [VALIDATION.txt](VALIDATION.txt) for test evidence and limitations.
GPL-3.0-or-later; see [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
