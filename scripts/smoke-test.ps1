<#
    Runnable check for the pairing gate. Starts the relay on a spare port, then asserts
    that a wrong pair code is refused and the real one is accepted.
    Usage: pwsh -File scripts\smoke-test.ps1
#>
param([int]$Port = 8799)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "PrivChat.Service\bin\Debug\net8.0\PrivChat.Service.exe"

if (-not (Test-Path $exe)) {
    dotnet build (Join-Path $root "PrivChat.sln") | Out-Host
}

function Test-Pairing([string]$code) {
    $socket = New-Object System.Net.WebSockets.ClientWebSocket
    $cts = New-Object System.Threading.CancellationTokenSource 5000
    try {
        $uri = [Uri]"ws://127.0.0.1:$Port/ws?role=phone&code=$code"
        $socket.ConnectAsync($uri, $cts.Token).GetAwaiter().GetResult()
        return $true
    }
    catch {
        return $false
    }
    finally {
        $socket.Dispose()
        $cts.Dispose()
    }
}

$relay = Start-Process -FilePath $exe -ArgumentList "--port", $Port, "--no-overlay" -PassThru -WindowStyle Hidden
try {
    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 300
        $up = try { (Invoke-WebRequest "http://127.0.0.1:$Port/healthz" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 204 } catch { $false }
    } while (-not $up -and (Get-Date) -lt $deadline)
    if (-not $up) { throw "Relay did not come up on port $Port" }

    $realCode = (Invoke-RestMethod "http://127.0.0.1:$Port/api/local-info" -TimeoutSec 5).pairCode
    $wrongCode = if ($realCode -eq "000000") { "111111" } else { "000000" }

    if (Test-Pairing $wrongCode) { throw "FAIL: wrong pair code $wrongCode was accepted" }
    Write-Host "PASS: wrong pair code refused"

    if (-not (Test-Pairing $realCode)) { throw "FAIL: correct pair code $realCode was refused" }
    Write-Host "PASS: correct pair code accepted"
}
finally {
    Stop-Process -Id $relay.Id -Force -ErrorAction SilentlyContinue
}

Write-Host "Pairing gate OK."
