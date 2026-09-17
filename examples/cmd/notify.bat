@echo off
rem Run from the extracted release directory, or supply the full executable path.
TransMessage.exe send --title "Task finished" --message "Completed" --level success
exit /b %errorlevel%
