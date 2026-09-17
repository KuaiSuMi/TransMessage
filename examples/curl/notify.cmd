@echo off
curl.exe --fail-with-body --max-time 35 -X POST "http://127.0.0.1:18765/v1/notify" -H "Authorization: Bearer %TM_TOKEN%" -H "Content-Type: application/json" --data-binary "{\"title\":\"Done\",\"message\":\"Completed\",\"level\":\"success\"}"
