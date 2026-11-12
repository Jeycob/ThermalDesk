$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Startup-Migration.ps1')
$script:assertions = 0
function Assert([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message};$script:assertions++}
function Assert-Throws([scriptblock]$Action,[string]$Message){$failed=$false;try{& $Action | Out-Null}catch{$failed=$true};Assert $failed $Message}
$old = 'C:\Users\test\ThermalDesk-0.1.0\release-0.3.12\ThermalDesk.exe'
$next = 'C:\Program Files\ThermalDesk\ThermalDesk.exe'
$xml = @'
<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task" version="1.2"><Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers><Principals><Principal id="Author"><UserId>S-1-5-21-123</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><Enabled>false</Enabled></Settings><Actions Context="Author"><Exec><Command>C:\Users\test\ThermalDesk-0.1.0\release-0.3.12\ThermalDesk.exe</Command><Arguments>--startup --minimized</Arguments><WorkingDirectory>C:\Users\test\ThermalDesk-0.1.0\release-0.3.12</WorkingDirectory></Exec></Actions></Task>
'@
$result = Convert-ThermalDeskStartupTask $xml $next
[xml]$parsed = $result
Assert ($parsed.Task.Actions.Exec.Command -eq $next) 'Path did not change'
Assert ($parsed.Task.Actions.Exec.Arguments -eq '--startup --minimized') 'Live arguments changed'
Assert ($parsed.Task.Settings.Enabled -eq 'false') 'Disabled task was enabled'
Assert ($parsed.Task.Principals.Principal.RunLevel -eq 'HighestAvailable') 'Elevation changed'
Assert ($parsed.Task.Actions.Exec.WorkingDirectory -eq 'C:\Program Files\ThermalDesk') 'Working directory stale'
Assert ($parsed.Task.Triggers.LogonTrigger.Enabled -eq 'true') 'Trigger changed'
Assert ($xml.Contains($old)) 'Input mutated'
Assert ((Convert-ThermalDeskStartupTask $result $next) -eq $result) 'Migration is not idempotent'
Assert ((Get-ThermalDeskRunPath ('"'+$old+'" --minimized')) -eq $old) 'Preview Run not parsed'
Assert ((Get-ThermalDeskRunPath ('"'+$old+'" --startup --preview --minimized')) -eq $old) 'Explicit Preview Run not parsed'
Assert ((Get-ThermalDeskRunPath ('"'+$old+'"')) -eq $old) 'Plain Run not parsed'
Assert-Throws {Convert-ThermalDeskStartupTask $xml 'relative.exe'} 'Relative target accepted'
Assert-Throws {Convert-ThermalDeskStartupTask ($xml.Replace('ThermalDesk.exe','Other.exe')) $next} 'Other app task accepted'
Assert-Throws {Convert-ThermalDeskStartupTask ($xml.Replace('</Actions>','<Exec><Command>other.exe</Command></Exec></Actions>')) $next} 'Multiple actions accepted'
Assert-Throws {Convert-ThermalDeskStartupTask ($xml.Replace('<WorkingDirectory>C:\Users\test\ThermalDesk-0.1.0\release-0.3.12</WorkingDirectory>','<WorkingDirectory>C:\Other</WorkingDirectory>')) $next} 'Custom working dir overwritten'
Assert-Throws {Get-ThermalDeskRunPath ('"'+$old+'" --live-once')} 'Unknown Run args accepted'
Assert-Throws {Get-ThermalDeskRunPath ('"'+$old+'" --startup --live-once --minimized')} 'Unknown mixed Run args accepted'
Assert-Throws {Get-ThermalDeskRunPath 'powershell.exe -command test'} 'Foreign Run accepted'
Assert-Throws {Convert-ThermalDeskStartupTask ($xml.Replace('LogonTrigger','RegistrationTrigger')) $next} 'Registration-trigger execution risk accepted'
Assert-Throws {Convert-ThermalDeskStartupTask ($xml.Replace('InteractiveToken','Password')) $next} 'Password task accepted'
# Check real installed cmdlet metadata without querying or mutating any tasks.
# This catches API drift/typos that pure XML tests cannot catch.
$register = Get-Command Register-ScheduledTask -ErrorAction Stop
foreach($parameter in 'TaskPath','TaskName','Xml','Force') {
    Assert ($register.Parameters.ContainsKey($parameter)) ('Register-ScheduledTask missing '+$parameter)
}
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Startup-Migration.ps1'),[ref]$tokens,[ref]$parseErrors)
Assert ($parseErrors.Count -eq 0) 'Migration syntax errors'
$calls = $ast.FindAll({param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -in 'Get-ScheduledTask','Export-ScheduledTask','Register-ScheduledTask','Unregister-ScheduledTask'},$true)
foreach($call in $calls){
    $definition = Get-Command $call.GetCommandName() -ErrorAction Stop
    foreach($element in $call.CommandElements){
        if($element -is [Management.Automation.Language.CommandParameterAst]){
            Assert ($definition.Parameters.ContainsKey($element.ParameterName)) ($call.GetCommandName()+' invalid parameter '+$element.ParameterName)
        }
    }
}
$installerText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ThermalDesk.iss'))
Assert ($installerText -match '(?m)^Filename:.*Parameters: "--preview --show-window"; Description: "Open ThermalDesk in Preview') 'Postinstall Preview launch must explicitly bypass saved Live resume'
[pscustomobject]@{Assertions=$assertions;Passed=$true;LiveStartupModified=$false} | ConvertTo-Json
