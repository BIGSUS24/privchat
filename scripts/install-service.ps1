#Requires -RunAsAdministrator
param(
    [int]$Port = 8787,
    [switch]$OpenFirewall
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$serviceExe = Join-Path $root "publish\PrivChat\service\PrivChat.Service.exe"

# Always republish. Reusing an existing publish folder installs whatever was built
# last time, which silently ships stale code.
& (Join-Path $PSScriptRoot "publish.ps1")
if (-not (Test-Path $serviceExe)) {
    throw "Publish did not produce $serviceExe"
}

$binPath = '"{0}" --service --port {1}' -f $serviceExe, $Port

if (Get-Service -Name PrivChatLocal -ErrorAction SilentlyContinue) {
    Stop-Service -Name PrivChatLocal -Force -ErrorAction SilentlyContinue
    sc.exe delete PrivChatLocal | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE" }

    # Deletion is asynchronous; wait for the name to be free before recreating it.
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Service -Name PrivChatLocal -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    if (Get-Service -Name PrivChatLocal -ErrorAction SilentlyContinue) {
        throw "PrivChatLocal is still present after delete. Close services.msc and retry."
    }
}

# New-Service quotes the binary path correctly; sc.exe create needs it hand-escaped.
New-Service -Name PrivChatLocal `
    -BinaryPathName $binPath `
    -DisplayName "PrivChat Local Messaging" `
    -Description "Private LAN messaging relay with capture-protected local overlay." `
    -StartupType Manual | Out-Host

if ($OpenFirewall) {
    $ruleName = "PrivChat Local Messaging"
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Profile Private | Out-Host
    # LAN room discovery beacons arrive as UDP broadcasts on this port.
    New-NetFirewallRule -DisplayName "$ruleName Discovery" -Direction Inbound -Action Allow -Protocol UDP -LocalPort 50508 -Profile Private | Out-Host
}

Start-Service -Name PrivChatLocal

$status = (Get-Service -Name PrivChatLocal).Status
if ($status -ne "Running") {
    throw "PrivChatLocal did not start (status: $status). See %ProgramData%\PrivChat\service-error.log"
}

Write-Host "Service installed and running (manual start only). Use scripts\start-privchat.bat and scripts\stop-privchat.bat to control it. Port: $Port"
