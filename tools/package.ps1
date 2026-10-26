# Pacchetto completo e offline di Battery Forecast (x64 o arm64, sulla stessa architettura della macchina:
# PyInstaller impacchetta il Python con cui gira, quindi il motore arm64 si costruisce su Windows ARM).
#   powershell -NoProfile -File tools\package.ps1 [-Version 0.9.0] [-Arch x64|arm64] [-SkipEngine] [-SkipInstaller]
#
# 1. app .NET autonoma (nessun runtime richiesto all'utente)   -> artifacts\BatteryForecast
# 2. motore PyInstaller (Python, PyTorch CPU, Timestral)        -> artifacts\BatteryForecast\engine
# 3. pesi Timestral-28M verificati con gli hash di MODEL_INFO   -> artifacts\BatteryForecast\engine\model
# 4. licenze di tutto ciò che si distribuisce                   -> artifacts\BatteryForecast\licenses
# 5. installer Inno Setup + SHA-256                              -> artifacts\BatteryForecast-Setup-<v>-<arch>.exe
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.]+)?$')][string]$Version = "0.9.0",
    [ValidateSet('x64', 'arm64')][string]$Arch = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }),
    [switch]$SkipEngine,
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts'
$dist = Join-Path $out 'BatteryForecast'
$python = Join-Path $root 'forecast\.venv\Scripts\python.exe'
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' } else { $env:DOTNET_ROOT = Split-Path $dotnet }
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
          "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

# Verify the absolute deletion target stays inside this checkout's artifacts directory.
$expectedDist = [IO.Path]::GetFullPath((Join-Path $root 'artifacts\BatteryForecast'))
if ([IO.Path]::GetFullPath($dist) -ne $expectedDist) { throw 'Unexpected package directory' }
if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null

Step "app .NET (Release, self-contained win-$Arch)"
$numeric = ($Version -split '-')[0]
& $dotnet publish (Join-Path $root 'windows\src\BatteryForecast.App\BatteryForecast.App.csproj') -c Release -r "win-$Arch" --self-contained true `
    -p:PublishReadyToRun=true -p:Version=$numeric -p:InformationalVersion=$Version -o $dist --nologo
if ($LASTEXITCODE) { throw "dotnet publish non riuscito" }

Step "motore"
$engine = Join-Path $root 'forecast\dist\engine'
if (-not $SkipEngine -or -not (Test-Path $engine)) {
    & $python -m PyInstaller --noconfirm --log-level WARN --distpath (Join-Path $root 'forecast\dist') `
        --workpath (Join-Path $root 'forecast\build') (Join-Path $root 'forecast\packaging\forecast.spec')
    if ($LASTEXITCODE) { throw "PyInstaller non riuscito" }
}
Copy-Item -Recurse $engine (Join-Path $dist 'engine')

Step "pesi Timestral-28M"
$modelSrc = Join-Path $root 'forecast\models\Timestral-28M'
$modelDst = Join-Path $dist 'engine\model'
New-Item -ItemType Directory -Force $modelDst | Out-Null
$info = Get-Content (Join-Path $modelSrc 'MODEL_INFO.json') -Raw | ConvertFrom-Json
foreach ($f in 'config.json', 'model.safetensors', 'LICENSE', 'MODEL_INFO.json') {
    Copy-Item (Join-Path $modelSrc $f) $modelDst
    if ($f -ne 'MODEL_INFO.json') {
        $hash = (Get-FileHash (Join-Path $modelDst $f) -Algorithm SHA256).Hash.ToLower()
        if ($hash -ne $info.files.$f) { throw "hash diverso per $f" }
    }
}
Write-Host "$($info.model_id) @ $($info.revision) verificato"

$docOut = Join-Path $dist 'docs'
New-Item -ItemType Directory -Force $docOut | Out-Null
foreach ($doc in 'MODEL_CARD.md', 'LICENSE_AUDIT.md', 'UPSTREAM.md') {
    Copy-Item -LiteralPath (Join-Path $root "docs\$doc") -Destination $docOut
}

Step "licenze"
$legalArgs = @((Join-Path $root 'tools\licenses.py'), (Join-Path $dist 'licenses'), (Join-Path $dist 'engine\_internal'),
    '--publish', $dist, '--toc', (Join-Path $root 'forecast\build\forecast\Analysis-00.toc'))
if ($iscc) { $legalArgs += @('--inno-license', (Join-Path (Split-Path $iscc) 'license.txt')) }
& $python @legalArgs
if ($LASTEXITCODE) { throw "raccolta licenze non riuscita" }

# Limite di 260 caratteri: la cartella d'installazione predefinita è circa
# C:\Users\<nome>\AppData\Local\Programs\Battery Forecast\ (45 caratteri + nome utente). Si lascia margine.
$longest = Get-ChildItem $dist -Recurse -File | ForEach-Object { $_.FullName.Substring($dist.Length + 1) } |
    Sort-Object Length -Descending | Select-Object -First 1
Write-Host "percorso relativo più lungo: $($longest.Length) caratteri ($longest)"
if ($longest.Length -gt 140) { throw "percorso troppo lungo nel pacchetto: $longest" }

$size = (Get-ChildItem $dist -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("cartella: {0:N0} MB" -f $size)

if (-not $SkipInstaller) {
    Step "installer"
    if (-not $iscc) { throw "Inno Setup 6 non trovato" }
    & $iscc /Q "/DAppVersion=$Version" "/DDist=$dist" "/DArch=$Arch" (Join-Path $root 'windows\packaging\BatteryForecast.iss')
    if ($LASTEXITCODE) { throw "Inno Setup non riuscito" }
    $setup = Join-Path $out "BatteryForecast-Setup-$Version-$Arch.exe"
    $sha = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
    "$sha  $(Split-Path -Leaf $setup)" | Set-Content -Encoding ascii "$setup.sha256"
    Write-Host ("{0} ({1:N0} MB)`nSHA-256 {2}" -f $setup, ((Get-Item $setup).Length / 1MB), $sha)
}
