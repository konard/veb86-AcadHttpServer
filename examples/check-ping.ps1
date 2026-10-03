# Checks the AutoCAD HTTP server (run HTTPSTART in AutoCAD first).
#   powershell -ExecutionPolicy Bypass -File check-ping.ps1
param([string]$Url = "http://127.0.0.1:5000/ping")

try {
    $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
    Write-Host "HTTP $($response.StatusCode)"
    Write-Host $response.Content
    $json = $response.Content | ConvertFrom-Json
    if ($json.status -eq "ok" -and $json.application -eq "AutoCAD" -and $json.version -eq "2021") {
        Write-Host "OK: server inside AutoCAD 2021 is responding" -ForegroundColor Green
        exit 0
    }
    Write-Host "Unexpected response" -ForegroundColor Red
    exit 2
}
catch {
    Write-Host "Server is not reachable at ${Url}: $($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host "(expected after HTTPSTOP or before HTTPSTART)"
    exit 1
}
