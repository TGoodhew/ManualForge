@echo off
powershell -NoProfile -ExecutionPolicy Bypass -Command "iwr https://raw.githubusercontent.com/TGoodhew/ManualForge/FreshInstallTest/tools/fresh-install-test.ps1 -OutFile $env:TEMP\fresh-install-test.ps1 -UseBasicParsing; & $env:TEMP\fresh-install-test.ps1"
pause
