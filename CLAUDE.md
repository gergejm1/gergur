# Gergur

Personal WebView2 browser (C#/.NET 10 WinForms). Build `dotnet build src\Gergur`,
test `dotnet test`, run the published exe in `src\Gergur\bin\Release\net10.0-windows\publish\`.
Always close Gergur before `dotnet publish` (the running exe locks the output),
and let its engine processes exit before relaunching or new engine flags no-op.

## Driving the browser (agent API)

When Gergur is running it serves a token-protected API on `http://127.0.0.1:24002`.
The token is in `%LOCALAPPDATA%\Gergur\agent-token.txt`. It is a stable per-install
secret, not rotated per launch, because MCP client config carries it in a static
header. Only a token of the exact shape Gergur mints (48 hex characters) in a file
owned by the current user is ever adopted, and the file is ACL'd to that user.

Those checks stop a **different account** on the machine, and nothing more. Any
process running as you can read that file, by design, since you have to read it
yourself to configure a client; it can equally create the file first with a 48-hex
value of its own choosing and have that adopted. Rotating the token per launch used
to bound a stolen one to a single browser session, and persisting it gives that up
with nothing in its place. Against code already running as you, treat this API as
fully exposed.

**Never commit a `.mcp.json` or any config containing the token:** it grants
arbitrary JavaScript in every logged-in session this browser holds.
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
| POST /mcp | JSON-RPC 2.0 | the same surface as MCP tools; see below |

`/mcp` speaks Model Context Protocol over JSON-RPC (`initialize`, `ping`, `tools/list`,
`tools/call`), so any Claude Code session can drive the browser as native tools rather
than through a subagent. Register it once at user scope:

```
claude mcp add --transport http gergur http://127.0.0.1:24002/mcp \
  --header "X-Gergur-Token: <token>" --scope user
```

Each tool maps onto an endpoint above, so there is one implementation of every action.
An `index` that is supplied but unusable is an error, never a silent fall back to the
tab the user is looking at.

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
