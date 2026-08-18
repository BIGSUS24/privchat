param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "publish\PrivChat"
$serviceOut = Join-Path $out "service"
$overlayOut = Join-Path $out "overlay"

New-Item -ItemType Directory -Force -Path $serviceOut | Out-Null
New-Item -ItemType Directory -Force -Path $overlayOut | Out-Null

dotnet publish (Join-Path $root "PrivChat.Service\PrivChat.Service.csproj") -c $Configuration -r $Runtime --self-contained false -o $serviceOut
dotnet publish (Join-Path $root "PrivChat.Overlay\PrivChat.Overlay.csproj") -c $Configuration -r $Runtime --self-contained false -o $overlayOut

Copy-Item -Path (Join-Path $overlayOut "*") -Destination $serviceOut -Recurse -Force

Write-Host "Published to $serviceOut"
