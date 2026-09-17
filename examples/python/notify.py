"""Set TM_TOKEN in your environment. No third-party Python packages required."""
import json
import os
import urllib.request

body = json.dumps({"title": "训练完成", "message": "loss=0.032", "level": "success", "source": "python"}).encode("utf-8")
request = urllib.request.Request(
    "http://127.0.0.1:18765/v1/notify", data=body,
    headers={"Authorization": "Bearer " + os.environ["TM_TOKEN"], "Content-Type": "application/json"},
)
with urllib.request.urlopen(request, timeout=35) as response:
    print(response.read().decode("utf-8"))
