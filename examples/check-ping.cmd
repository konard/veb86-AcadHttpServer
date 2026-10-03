@echo off
rem Checks the AutoCAD HTTP server with curl (built into Windows 10/11).
rem Run HTTPSTART in AutoCAD first.
curl -i --max-time 5 http://127.0.0.1:5000/ping
echo.
if errorlevel 1 echo Server is not reachable (expected after HTTPSTOP or before HTTPSTART).
