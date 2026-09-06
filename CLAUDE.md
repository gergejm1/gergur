# Gergur

Personal WebView2 browser (C#/.NET 10 WinForms). Build `dotnet build src\Gergur`,
test `dotnet test`, run the published exe in `src\Gergur\bin\Release\net10.0-windows\publish\`.
Always close Gergur before `dotnet publish` (the running exe locks the output),
and let its engine processes exit before relaunching or new engine flags no-op.

## Driving the browser (agent API)

When Gergur is running it serves a token-protected API on `http://127.0.0.1:24002`.
The token is in `%LOCALAPPDATA%\Gergur\agent-token.txt` (regenerated every launch).
Send it as the `X-Gergur-Token` header. PowerShell:

```powershell
$t = Get-Content "$env:LOCALAPPDATA\Gergur\agent-token.txt"
$H = @{ 'X-Gergur-Token' = $t }
Invoke-RestMethod "http://127.0.0.1:24002/tabs" -Headers $H
```

| Endpoint | Body / query | Does |
|---|---|---|
| GET /tabs | | list tabs: index, window, url, title, state, active, errors |
| POST /open | {"url": "..."} | open tab (activates), returns index |
| POST /activate | {"index": n} | switch to tab |
| POST /close | {"index": n} | close tab |
| POST /navigate | {"url": "...", "index": n?} | navigate (default: active tab) |
| GET /page?index=n | | {url, title, text} - rendered innerText |
| GET /html?index=n | | outer HTML |
| GET /screenshot?index=n | | PNG bytes (activates the tab first) |
| POST /eval | {"js": "...", "index": n?} | run JS, returns {"result": ...} |
| POST /click | {"selector": "...", "index": n?} | querySelector + click |
| POST /type | {"selector": "...", "text": "...", "index": n?} | fill input (React-safe) |

Tabs can be dragged out into their own windows, so `index` is a flat position
across every window in window order: tearing a tab off renumbers what follows it.
Re-read /tabs rather than reusing an index across such a change. `window` says
which window a tab is in, and `active` means active within that window, so several
tabs can be active at once. `index` omitted means the active tab of the focused
window. Reading/evaluating a parked (asleep) tab wakes it. Screenshots activate the target tab, so prefer /page for background
reads to avoid disturbing what the user is looking at. The user's browsing is
personal: read what the task requires, nothing more.

## Notes

- Settings: `%LOCALAPPDATA%\Gergur\settings.json`. Engine flags (VPN, process
  policy) apply only on a fresh engine start.
- Debug trace: set `GERGUR_DEBUG=1` before launch, log at `%LOCALAPPDATA%\Gergur\debug.log`.
- `errors` on /tabs is exactly what the status bar's "⚠ N issues" counts: JS errors,
  unhandled rejections, `console.error` calls, and failed subresource loads on that
  tab's current page. It clears on every navigation, so it always describes the page
  you are looking at, not the session. Requests the blocklist killed are excluded:
  they are the blocker working, not a page defect. A cross-origin script served
  without CORS headers reports opaquely (no message, file, or line) and is labelled
  as such rather than shown as a bare "Script error".
- The user never wants em dashes anywhere: code, UI text, docs, commits.

Agent actions are visualized: /click and /type animate a blue cursor to the
target, ripple, and flash the element, so the user can watch the agent work.
Both endpoints return after the animation and action complete (~1s).
