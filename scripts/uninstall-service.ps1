#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"

if (Get-Service -Name PrivChatLocal -ErrorAction SilentlyContinue) {
    Stop-Service -Name PrivChatLocal -Force -ErrorAction SilentlyContinue
    sc.exe delete PrivChatLocal | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE" }
}

Get-NetFirewallRule -DisplayName "PrivChat Local Messaging" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Write-Host "PrivChatLocal removed."
