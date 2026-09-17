// Node is only needed by your own JavaScript program, never by TransMessage.
const response = await fetch('http://127.0.0.1:18765/v1/notify', {
  method: 'POST',
  headers: { Authorization: `Bearer ${process.env.TM_TOKEN}`, 'Content-Type': 'application/json' },
  body: JSON.stringify({ title: '任务完成', message: '已完成', level: 'success', source: 'javascript' }),
  signal: AbortSignal.timeout(35000),
});
if (!response.ok) throw new Error(`Notification failed: HTTP ${response.status}`);
console.log(await response.json());
