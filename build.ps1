# Builds the self-contained app and the installer.
#   publish\SerPlayer.exe              the app (runs from anywhere)
#   dist\SerPlayer-Setup-<ver>.exe     the installer (same binary; the "Setup" name switches it to the install wizard)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet publish -c Release -o publish
if ($LASTEXITCODE) { throw "publish failed" }
$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
New-Item -ItemType Directory -Force dist | Out-Null
Get-ChildItem dist -Filter 'SerPlayer-Setup-*.exe' | Remove-Item
Copy-Item publish\SerPlayer.exe "dist\SerPlayer-Setup-$version.exe"
Get-ChildItem dist | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
