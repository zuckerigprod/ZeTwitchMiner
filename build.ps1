# Сборка релиза: нативный exe, портативный zip и установщик
# Запуск: pwsh ./build.ps1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

[xml]$proj = Get-Content ZeTwitchMiner.csproj
$version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

# NativeAOT ищет линкер MSVC через vswhere
$installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if (Test-Path $installer) { $env:PATH = "$installer;$env:PATH" }

Remove-Item publish, dist -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish -c Release -o publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Remove-Item publish\*.pdb -ErrorAction SilentlyContinue

New-Item dist -ItemType Directory | Out-Null

# Портативная версия: файл portable рядом с exe переключает хранение данных в папку data
$portable = "dist\ZeTwitchMiner-$version-portable"
New-Item $portable -ItemType Directory | Out-Null
Copy-Item publish\* $portable
New-Item "$portable\portable" -ItemType File | Out-Null
Compress-Archive "$portable\*" "dist\ZeTwitchMiner-$version-portable.zip"
Remove-Item $portable -Recurse -Force

$iscc = "$env:ProgramFiles\Inno Setup 7\ISCC.exe"
if (-not (Test-Path $iscc)) { $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
if (Test-Path $iscc) {
    & $iscc /Qp "/DAppVersion=$version" installer\ZeTwitchMiner.iss
    if ($LASTEXITCODE -ne 0) { throw "installer failed" }
} else {
    Write-Warning "Inno Setup не найден, установщик не собран"
}

Get-ChildItem dist | Format-Table Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
