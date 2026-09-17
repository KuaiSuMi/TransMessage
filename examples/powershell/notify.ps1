$body = @{ title = '任务完成'; message = '处理已完成'; level = 'success'; source = 'powershell' } | ConvertTo-Json
Invoke-RestMethod 'http://127.0.0.1:18765/v1/notify' -Method Post -Headers @{ Authorization = "Bearer $env:TM_TOKEN" } -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
