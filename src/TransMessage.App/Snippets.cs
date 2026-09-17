namespace TransMessage.App;

internal static class Snippets
{
    public static string For(string kind, string exe, int port)
    {
        var url = $"http://127.0.0.1:{port}/v1/notify";
        return kind switch
        {
            "CMD" => $"\"{exe}\" send \"任务完成\"\r\n\"{exe}\" run -- cmd /c echo hello",
            "Python" => $"import json, os, urllib.request\nbody = json.dumps({{'title': '训练完成', 'message': '完成', 'level': 'success'}}).encode()\nreq = urllib.request.Request('{url}', data=body, headers={{'Authorization': 'Bearer ' + os.environ['TM_TOKEN'], 'Content-Type': 'application/json'}})\nwith urllib.request.urlopen(req, timeout=35) as response:\n    print(response.read().decode())",
            "JavaScript" => $"const response = await fetch('{url}', {{\n  method: 'POST',\n  headers: {{ 'Authorization': `Bearer ${{process.env.TM_TOKEN}}`, 'Content-Type': 'application/json' }},\n  body: JSON.stringify({{title:'任务完成', message:'已完成', level:'success'}}),\n  signal: AbortSignal.timeout(35000)\n}});\nif (!response.ok) throw new Error(`通知失败 ${{response.status}}`);\nconsole.log(await response.json());",
            "HTTP / curl" => $"curl.exe -X POST \"{url}\" -H \"Authorization: Bearer %TM_TOKEN%\" -H \"Content-Type: application/json\" --data-binary \"{{\\\"title\\\":\\\"Done\\\",\\\"message\\\":\\\"Complete\\\"}}\"",
            "Codex" => $"& '{exe.Replace("'", "''")}' codex install\n# 重启已有 Codex 会话；此后每轮完成自动通知。",
            _ => $"& '{exe.Replace("'", "''")}' send '任务完成'\n\n$body = @{{ title='训练完成'; message='loss=0.032'; level='success' }} | ConvertTo-Json\nInvoke-RestMethod '{url}' -Method Post -Headers @{{ Authorization=\"Bearer $env:TM_TOKEN\" }} -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))"
        };
    }
}
