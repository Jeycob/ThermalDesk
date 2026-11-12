# Windows installer

`ThermalDesk-<version>-Setup-x64.exe` installs the self-contained app under `Program Files\ThermalDesk` and adds a Start menu shortcut. A desktop shortcut is optional. Upgrades use the same directory, so new versions do not create another installed copy.

Before updating, choose **Stop**, then **Exit** in ThermalDesk's tray menu. Setup refuses to replace a running controller. It does not kill processes, change fan duty, start live control, install drivers or reset USB devices.

Your profile in `%LOCALAPPDATA%\ThermalDesk` is kept during upgrades and uninstall. Existing app-created sign-in entries are redirected to the installed executable while preserving their enabled state and arguments. The task XML is backed up under `%LOCALAPPDATA%\ThermalDesk\installer-backups`; a failed migration restores the original task. Custom or other users' startup entries are left unchanged, with a warning to re-register startup from the app. No startup entry is created if there was none.

Manual launches from Start open in Preview and request administrator permission as usual. The post-install launch checkbox is off by default. Old portable folders are not blindly deleted by setup: they may contain running binaries, custom files or the only rollback version.

Uninstall checks startup entries before asking for confirmation. After confirmation it removes only entries pointing to this installed copy. If cleanup fails, uninstall stops before removing application files, so an unresolved startup task cannot be left pointing at a deleted app. Your saved profile remains available for reinstall.

## Build

1. Publish the self-contained app and run `package.ps1` to include the license/docs.
2. Install or extract the official signed [Inno Setup compiler](https://jrsoftware.org/isdl.php). Inno's own installer supports `/PORTABLE=1 /CURRENTUSER /NOICONS /DIR=<tools-folder>` for a workspace-only compiler.
3. Run:

```powershell
.\installer\Test-StartupMigration.ps1
.\installer\Build-Installer.ps1 -Compiler 'C:\tools\Inno Setup\ISCC.exe'
```

The build script checks the compiler's Authenticode signature and requires the published app version to match the source project version. The output is in `artifacts`. ThermalDesk itself is not currently code-signed; Windows may display an unknown-publisher warning. Do not disable Windows security protections.

## Isolated extraction smoke test

```powershell
.\installer\Build-Installer.ps1 -Compiler 'C:\tools\Inno Setup\ISCC.exe' -SandboxTest
& '.\artifacts\ThermalDesk-<version>-SandboxTest-x64.exe' /CURRENTUSER /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR="C:\test\thermaldesk"
```

The **SandboxTest build**, not a switch on the release installer, disables registration, shortcuts, uninstall registration, startup migration and application launch. It only extracts to the selected test directory and still runs the process safety check. It is a test artifact, not a distribution package. The test does not replace a real installed-upgrade/uninstall acceptance test on a spare machine.
