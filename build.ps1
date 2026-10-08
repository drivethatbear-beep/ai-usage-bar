# Builds AI Usage Bar and its installer: tests → Release build → dist\AIUsageBar-Setup.exe
# Usage: pwsh ./build.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

[xml]$proj = Get-Content src/AIUsageBar/AIUsageBar.csproj
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

Write-Host "== Tests"
dotnet test --nologo
if ($LASTEXITCODE -ne 0) { throw "tests failed" }

Write-Host "== Release build $version"
dotnet build src/AIUsageBar -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    Write-Host "Inno Setup 6 is not installed. Install it with:  winget install JRSoftware.InnoSetup" -ForegroundColor Yellow
    exit 1
}

Write-Host "== Installer ($iscc)"
& $iscc "/DAppVersion=$version" installer/AIUsageBar.iss
if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
Get-Item dist/AIUsageBar-Setup.exe | Select-Object FullName, Length
