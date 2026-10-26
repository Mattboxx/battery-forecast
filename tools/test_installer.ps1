# For a disposable GitHub-hosted runner only. Never run over the user's installation.
param([Parameter(Mandatory=$true)][string]$Installer, [ValidateSet('x64','arm64')][string]$Arch='x64',
      [string]$Python='forecast/.venv/Scripts/python.exe')   # basta la libreria standard
$ErrorActionPreference='Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP) { throw 'Installer smoke test requires a disposable CI runner' }
$smokeRoot = Join-Path $env:RUNNER_TEMP 'BatteryForecast-installed-smoke'
$installerPath = (Resolve-Path -LiteralPath $Installer).Path
$installArgs = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-', '/ACCEPTCOMPONENTTERMS', '/TASKS=', "/DIR=`"$smokeRoot`"",
                 "/LOG=`"$(Join-Path $env:RUNNER_TEMP "inno-$Arch.log")`"")

function Step($text) { Write-Host "[$((Get-Date).ToString('HH:mm:ss'))] $text" }

# Waits for the process itself only (Start-Process -Wait also waits for every descendant) and never forever:
# on timeout it lists what is still running, so a hang leaves a readable log instead of a stuck job.
function Run($file, [string[]]$arguments, [int]$seconds = 300) {
    Step "$([IO.Path]::GetFileName($file)) $($arguments -join ' ')"
    $p = Start-Process -FilePath $file -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $null = $p.Handle       # keeps the handle open, otherwise ExitCode can come back empty
    if (-not $p.WaitForExit($seconds * 1000)) {
        Get-CimInstance Win32_Process | Where-Object { $_.CreationDate -gt (Get-Date).AddMinutes(-15) } |
            Format-Table ProcessId, ParentProcessId, Name, CommandLine -AutoSize | Out-String -Width 400 | Write-Host
        $p.Kill($true)
        throw "Timed out after $seconds s: $file"
    }
    Step "exit $($p.ExitCode)"
    return $p.ExitCode
}

try {
    Step 'silent installation without component-term acceptance must be rejected'
    $rejectedArgs = $installArgs | Where-Object { $_ -ne '/ACCEPTCOMPONENTTERMS' }
    if ((Run $installerPath $rejectedArgs) -eq 0) { throw 'Installer silently accepted missing component-term consent' }
    if (Test-Path -LiteralPath (Join-Path $smokeRoot 'BatteryForecast.exe')) { throw 'Rejected setup wrote an application' }
    if ((Run $installerPath $installArgs) -ne 0) { throw 'Installer failed' }
    Step 'smoke test of the installed package'
    & $Python tools/smoke_package.py $smokeRoot --arch $Arch --report "artifacts/installed-smoke-$Arch.json"
    if ($LASTEXITCODE) { throw 'Installed package smoke test failed' }
    if ((Run (Join-Path $smokeRoot 'BatteryForecast.exe') @('--quit') 60) -ne 0) { throw 'Installed app did not close cleanly' }
    # Reinstall over the same directory and ensure notices are still present and intact.
    if ((Run $installerPath $installArgs) -ne 0) { throw 'In-place reinstall failed' }
    Step 'legal files after reinstall'
    & $Python tools/licenses.py (Join-Path $smokeRoot 'licenses') --verify
    if ($LASTEXITCODE) { throw 'Updated installation lost legal files' }
}
finally {
    $exe = Join-Path $smokeRoot 'BatteryForecast.exe'
    if (Test-Path -LiteralPath $exe) { Run $exe @('--quit') 60 | Out-Null }
    $uninstaller = Join-Path $smokeRoot 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller) {
        if ((Run $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')) -ne 0) { throw 'Uninstall failed' }
    }
    $log = Join-Path $env:RUNNER_TEMP "inno-$Arch.log"
    if (Test-Path -LiteralPath $log) { Step 'Inno Setup log (tail)'; Get-Content -LiteralPath $log -Tail 40 | Write-Host }
}
