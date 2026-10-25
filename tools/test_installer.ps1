# For a disposable GitHub-hosted runner only. Never run over the user's installation.
param([Parameter(Mandatory=$true)][string]$Installer, [ValidateSet('x64','arm64')][string]$Arch='x64')
$ErrorActionPreference='Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP) { throw 'Installer smoke test requires a disposable CI runner' }
$smokeRoot = Join-Path $env:RUNNER_TEMP 'BatteryForecast-installed-smoke'
$installerPath = (Resolve-Path -LiteralPath $Installer).Path
$installArgs = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-', '/TASKS=', "/DIR=`"$smokeRoot`"")
$proc = Start-Process -FilePath $installerPath -ArgumentList $installArgs -WindowStyle Hidden -PassThru -Wait
if ($proc.ExitCode -ne 0) { throw "Installer failed: $($proc.ExitCode)" }
try {
    & forecast/.venv/Scripts/python.exe tools/smoke_package.py $smokeRoot --arch $Arch --report "artifacts/installed-smoke-$Arch.json"
    if ($LASTEXITCODE) { throw 'Installed package smoke test failed' }
    & (Join-Path $smokeRoot 'BatteryForecast.exe') --quit
    if ($LASTEXITCODE) { throw 'Installed app did not close cleanly' }
    # Reinstall over the same directory and ensure notices are still present and intact.
    $proc = Start-Process -FilePath $installerPath -ArgumentList $installArgs -WindowStyle Hidden -PassThru -Wait
    if ($proc.ExitCode -ne 0) { throw 'In-place reinstall failed' }
    & forecast/.venv/Scripts/python.exe tools/licenses.py (Join-Path $smokeRoot 'licenses') --verify
    if ($LASTEXITCODE) { throw 'Updated installation lost legal files' }
}
finally {
    & (Join-Path $smokeRoot 'BatteryForecast.exe') --quit
    $uninstaller = Join-Path $smokeRoot 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller) {
        $proc = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -WindowStyle Hidden -PassThru -Wait
        if ($proc.ExitCode -ne 0) { throw 'Uninstall failed' }
    }
}
