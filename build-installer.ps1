<#  Builds the self-contained Windows x64 installer.
    Needs: Windows 11/10, .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0), NSIS (script installs it via winget if missing).
    Run:   powershell -ExecutionPolicy Bypass -File build-installer.ps1
    Output: dist\RetroLabel-Setup-<version>-x64.exe                                                         #>
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot; Set-Location $root
$version = '2.0.0'
Write-Host '== 1/4 Self-tests (PDF render, EPL2, queue, phone security)'
dotnet run --project tests/LabelPrinter.SelfTest -c Release
if ($LASTEXITCODE -ne 0) { throw 'Self-tests failed; not building an installer.' }
Write-Host '== 2/4 Publish self-contained win-x64'
$pub = Join-Path $root 'dist\publish'
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
dotnet publish src/LabelPrinter.Desktop/LabelPrinter.Desktop.csproj -c Release -r win-x64 --self-contained true -o $pub
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
if (-not (Test-Path "$pub\pdfium.dll")) { throw 'pdfium.dll missing from publish output.' }
Write-Host '== 3/4 Locate NSIS'
$nsis = @("$env:ProgramFiles\NSIS\makensis.exe", "${env:ProgramFiles(x86)}\NSIS\makensis.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $nsis) { winget install --id NSIS.NSIS -e --accept-source-agreements --accept-package-agreements; $nsis = @("$env:ProgramFiles\NSIS\makensis.exe", "${env:ProgramFiles(x86)}\NSIS\makensis.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1 }
if (-not $nsis) { throw 'NSIS not found. Install it from https://nsis.sourceforge.io and re-run.' }
Write-Host '== 4/4 Build installer'
& $nsis "/DVERSION=$version" "/DSRCDIR=$pub" "installer\RetroLabel.nsi"
if ($LASTEXITCODE -ne 0) { throw 'makensis failed.' }
New-Item -ItemType Directory -Force dist | Out-Null
Move-Item -Force "installer\RetroLabel-Setup-$version-x64.exe" dist\
Write-Host "Done: dist\RetroLabel-Setup-$version-x64.exe"
