param(
    [ValidateSet('Apply','Remove','Plan','PlanRemove')][string]$Mode = 'Plan',
    [string]$AppPath
)
$ErrorActionPreference = 'Stop'

function Convert-ThermalDeskStartupTask {
    param([string]$TaskXml,[string]$NewExecutable)
    if(-not [IO.Path]::IsPathRooted($NewExecutable) -or [IO.Path]::GetFileName($NewExecutable) -ine 'ThermalDesk.exe') { throw 'Invalid installed executable path.' }
    [xml]$document = $TaskXml
    $ns = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $ns.AddNamespace('t','http://schemas.microsoft.com/windows/2004/02/mit/task')
    $actions = @($document.SelectNodes('/t:Task/t:Actions/*',$ns))
    if($actions.Count -ne 1 -or $actions[0].LocalName -ne 'Exec') { throw 'Existing startup task has unexpected actions; leave it unchanged.' }
    $triggers = @($document.SelectNodes('/t:Task/t:Triggers/*',$ns))
    if($triggers.Count -ne 1 -or $triggers[0].LocalName -ne 'LogonTrigger'){throw 'Only the app-created logon trigger can be migrated. Leave custom triggers unchanged.'}
    $logon = $document.SelectSingleNode('/t:Task/t:Principals/t:Principal/t:LogonType',$ns)
    if($null -eq $logon -or $logon.InnerText -ne 'InteractiveToken'){throw 'The startup task is not an interactive sign-in task; leave it unchanged.'}
    $command = $actions[0].SelectSingleNode('t:Command',$ns)
    if($null -eq $command -or [IO.Path]::GetFileName($command.InnerText.Trim('"')) -ine 'ThermalDesk.exe') { throw 'Existing startup task does not launch ThermalDesk; leave it unchanged.' }
    $oldExecutable = $command.InnerText.Trim('"')
    $directory = $actions[0].SelectSingleNode('t:WorkingDirectory',$ns)
    if($null -ne $directory -and $directory.InnerText -and $directory.InnerText -ine [IO.Path]::GetDirectoryName($oldExecutable)) { throw 'Startup task uses a custom working directory; leave it unchanged.' }
    $command.InnerText = $NewExecutable
    if($null -ne $directory){ $directory.InnerText = [IO.Path]::GetDirectoryName($NewExecutable) }
    return $document.OuterXml
}

function Get-ThermalDeskRunPath {
    param([string]$Value)
    # Only known application-created HKCU Run entries are migrated.
    $match = [regex]::Match($Value,'^"(?<path>[^"\r\n]+\\ThermalDesk\.exe)"(?<args>\s+--minimized|\s+--startup\s+--preview\s+--minimized)?$',[Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if(-not $match.Success){throw 'Existing Run entry is not a known ThermalDesk entry; leave it unchanged.'}
    return $match.Groups['path'].Value
}

function Invoke-ThermalDeskStartupMigration {
    param([string]$Operation,[string]$Executable)
    $Executable = [IO.Path]::GetFullPath($Executable)
    if([IO.Path]::GetFileName($Executable) -ine 'ThermalDesk.exe'){throw 'Expected ThermalDesk.exe.'}
    if($Operation -eq 'Apply' -and -not(Test-Path -LiteralPath $Executable -PathType Leaf)){throw 'Installed executable is missing.'}
    $task = Get-ScheduledTask -TaskPath '\' -TaskName 'ThermalDesk-AtSignIn' -ErrorAction SilentlyContinue
    if($task){
        $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        try { $ownerSid = ([Security.Principal.NTAccount]::new($task.Principal.UserId)).Translate([Security.Principal.SecurityIdentifier]).Value }
        catch { $ownerSid = $task.Principal.UserId }
        if($ownerSid -ne $currentSid){throw 'The startup task belongs to a different user. Re-register Start at sign-in from ThermalDesk as that user.'}
        $xml = Export-ScheduledTask -TaskPath '\' -TaskName 'ThermalDesk-AtSignIn'
        $updated = Convert-ThermalDeskStartupTask -TaskXml $xml -NewExecutable $Executable
        if($Operation -eq 'Apply'){
            $backupDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ThermalDesk\installer-backups'
            New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
            $backupFile = Join-Path $backupDirectory ('startup-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')+'.xml')
            [IO.File]::WriteAllText($backupFile,$xml,[Text.UTF8Encoding]::new($false))
            try {
                Register-ScheduledTask -TaskPath '\' -TaskName 'ThermalDesk-AtSignIn' -Xml $updated -Force | Out-Null
                $verified = Get-ScheduledTask -TaskPath '\' -TaskName 'ThermalDesk-AtSignIn'
                if($verified.Actions.Count -ne 1 -or $verified.Actions[0].Execute -ine $Executable){throw 'Startup path verification failed.'}
            } catch {
                $migrationError = $_.Exception.Message
                try { Register-ScheduledTask -TaskPath '\' -TaskName 'ThermalDesk-AtSignIn' -Xml $xml -Force | Out-Null }
                catch { throw ('Startup migration and rollback failed. Keep the old portable copy and re-register startup in the app. Backup: '+$backupFile) }
                throw ('Startup migration failed; the original task was restored. '+$migrationError)
            }
            Write-Host 'Updated existing sign-in task path; its triggers, enabled state and live arguments were preserved.'
        } elseif($Operation -eq 'Remove') {
            if($task.Actions.Count -eq 1 -and $task.Actions[0].Execute.Trim('"') -ieq $Executable){
                Unregister-ScheduledTask -TaskPath '\' -TaskName 'ThermalDesk-AtSignIn' -Confirm:$false
                Write-Host 'Removed the sign-in task for this installed copy.'
            }
        } else { Write-Host 'Existing sign-in task can be migrated without enabling or running it.' }
    }
    $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $entry = Get-ItemProperty -LiteralPath $run -Name ThermalDesk -ErrorAction SilentlyContinue
    if($entry){
        $previous = Get-ThermalDeskRunPath -Value ([string]$entry.ThermalDesk)
        if($Operation -eq 'Apply'){
            Set-ItemProperty -LiteralPath $run -Name ThermalDesk -Value ('"'+$Executable+'" --startup --preview --minimized')
            Write-Host 'Updated existing Preview sign-in entry path.'
        } elseif($Operation -eq 'Remove' -and $previous -ieq $Executable){
            Remove-ItemProperty -LiteralPath $run -Name ThermalDesk
            Write-Host 'Removed the sign-in entry for this installed copy.'
        } else { Write-Host 'Existing Preview sign-in entry was left unchanged.' }
    }
    if(-not $task -and -not $entry){Write-Host 'No existing startup entry. Startup remains disabled.'}
}

if($MyInvocation.InvocationName -ne '.'){
    try { Invoke-ThermalDeskStartupMigration -Operation $Mode -Executable $AppPath; exit 0 }
    catch { Write-Error -Message $_.Exception.Message -ErrorAction Continue; exit 1 }
}
