# Run separately for upstream UniPad, patched UniPad, then DS4Windows. Never simultaneously.
# Example: .\build\performance\Measure-Process.ps1 -TargetProcessId 1234 -Label UniPad-before -Scenario idle-tray
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][int]$TargetProcessId,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Label,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Scenario,
    [ValidateRange(5, 3600)][int]$Seconds = 60,
    [ValidateRange(0, 300)][int]$WarmupSeconds = 15,
    [ValidateRange(1, 2000)][int]$PollHz = 1000,
    [ValidateRange(0, 8)][int]$Controllers = 1,
    [string]$Notes = '',
    [string]$OutputDirectory = 'artifacts\windows-cpu'
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'This benchmark requires Windows.' }
$process = Get-Process -Id $TargetProcessId
$executable = $process.Path
$hash = if ($executable) { (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash } else { '' }
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$logical = [int](Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors
$metadata = [ordered]@{
    Label = $Label; Scenario = $Scenario; TargetProcessId = $TargetProcessId
    Executable = $executable; SHA256 = $hash; CpuModel = $cpu.Name; LogicalProcessors = $logical
    OS = [Environment]::OSVersion.VersionString; PowerPlan = ((powercfg /getactivescheme) -join ' ')
    PollHzRequested = $PollHz; Controllers = $Controllers; Notes = $Notes
    Warning = 'CPU measurement only. Requested Hz is metadata, not measured IO rate or latency. DPC/ISR costs are not attributed to this process.'
}
Write-Host "Warm up $WarmupSeconds seconds. Preserve the same devices, profile, connection, UI state and input workload."
Start-Sleep -Seconds $WarmupSeconds
$process.Refresh()
$previousCpuMs = $process.TotalProcessorTime.TotalMilliseconds
$watch = [Diagnostics.Stopwatch]::StartNew()
$previousMs = $watch.Elapsed.TotalMilliseconds
$rows = [Collections.Generic.List[object]]::new()
while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
    Start-Sleep -Milliseconds 1000
    $process.Refresh()
    if ($process.HasExited) { throw 'Target exited; incomplete run, no valid comparison.' }
    $nowMs = $watch.Elapsed.TotalMilliseconds
    $nowCpuMs = $process.TotalProcessorTime.TotalMilliseconds
    $elapsedMs = $nowMs - $previousMs
    $corePercent = 100.0 * ($nowCpuMs - $previousCpuMs) / $elapsedMs
    $rows.Add([pscustomobject]@{
        TimeSeconds = $nowMs / 1000.0; SampleMilliseconds = $elapsedMs
        CpuMilliseconds = $nowCpuMs - $previousCpuMs
        CpuCorePercent = $corePercent; CpuMachinePercent = $corePercent / $logical
        WorkingSetMiB = $process.WorkingSet64 / 1MB; PrivateMiB = $process.PrivateMemorySize64 / 1MB
        Threads = $process.Threads.Count; Handles = $process.HandleCount
    })
    $previousMs = $nowMs; $previousCpuMs = $nowCpuMs
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$prefix = Join-Path $OutputDirectory "$Label-$Scenario-$(Get-Date -Format yyyyMMdd-HHmmss)"
$rows | Export-Csv -LiteralPath "$prefix.csv" -NoTypeInformation -Encoding UTF8
$totalMs = ($rows | Measure-Object SampleMilliseconds -Sum).Sum
$totalCpuMs = ($rows | Measure-Object CpuMilliseconds -Sum).Sum
$ordered = @($rows.CpuCorePercent | Sort-Object)
$metadata['MeanCorePercent'] = 100.0 * $totalCpuMs / $totalMs
$metadata['MeanMachinePercent'] = $metadata.MeanCorePercent / $logical
$metadata['P95SampleCorePercent'] = $ordered[[Math]::Floor(($ordered.Count - 1) * 0.95)]
$metadata['MeasuredSeconds'] = $totalMs / 1000.0
$metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$prefix.json" -Encoding UTF8
$metadata | ConvertTo-Json -Depth 5 | Write-Host
Write-Host "Saved $prefix.csv and .json"
