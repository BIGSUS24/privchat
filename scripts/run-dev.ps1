param([int]$Port = 8787)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

dotnet build (Join-Path $root "PrivChat.sln")
dotnet run --project (Join-Path $root "PrivChat.Service\PrivChat.Service.csproj") -- --port $Port
