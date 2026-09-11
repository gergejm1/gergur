# Gergur

Personal WebView2 browser (C#/.NET 10 WinForms). Build `dotnet build src\Gergur`,
test `dotnet test`, run `src\Gergur\bin\Release\net10.0-windows\Gergur.exe` after
`dotnet build src\Gergur -c Release`. There is a `publish\` beside it only once you
have run `dotnet publish`, which nothing here needs. Always close Gergur before
building Release (the running exe locks the output), and let its engine processes
exit before relaunching or new engine flags no-op.

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
- The phone drop (`DropServer`, port 24003) is the only listener beyond loopback, so
  treat everything it reads as hostile. Its pairing key is read from the raw query,
  not the parsed one: the iOS share sheet appends the shared item to the end of the
  url as it is, so a shared link brings its own `&` and its own parameters, and a
  last-wins parse lets a link decide who is paired. Same reason `SharedValue` reads
  to the end of the query rather than splitting it.
- `/share?k=&text=` is the share-sheet endpoint and `/setup?k=` explains how to build
  the Shortcut. `/share` is a GET that changes state, which is deliberate: an iOS
  Shortcut is one action for a GET, there is no ambient authority to forge, and the
  key is the only credential.
- The drop moves files nothing refers to into `drop/orphans` when it opens, rather
  than deleting them: this judgement was wrong four separate times in review, each
  time costing a photo. It stands down entirely unless the index parsed and every
  entry survived, and before deciding anything is unreferenced it reads every file
  sitting beside `items.json` (the staging file, and the copies kept when an index
  could not be read or could not be fully used, which are numbered when a name is
  already taken). Those are found by enumeration, never by a list of names: a written
  out list is how a numbered copy stopped protecting anything. Quarantined files and
  spent index copies are pruned after 60 days, dated from when they were set aside.
  The drop window says how many things are set aside and opens the folder on click.
- `items.json.recovered*` is a session's work written beside an index it was not
  allowed to touch. Nothing reads it back into the list yet; it is preserved, counted
  and pointed at, and merging it is the obvious next thing here.
- A YouTube ad can only be taken away **before the player commits to the break**.
  Pruning the payload as `ytInitialPlayerResponse` and `ytInitialData` are assigned
  works, and was measured over repeated loads where YouTube had actually scheduled an
  ad. Pruning a *fetched* youtubei response instead hangs the player: the video never
  starts at all. That held for a fetch wrapper, a global `JSON.parse` hook and a
  `Response.prototype.json` hook alike, and whether they removed every ad key, only ad
  renderers, or a single key. Hiding player furniture does the same: `#player-ads`
  under `display: none` cost the video every run (three of three), and started every
  run without it. So anything added to `adblock.js` has to be tried against a real ad,
  in the browser, several times. Reading it cannot tell you, and neither can a single
  run: YouTube only schedules an ad on some loads, so a load it offered nothing on
  looks exactly like a load the blocker handled. Read whether an ad was scheduled out
  of the page's own script text, which nothing here rewrites, and only compare loads
  that were served one. `adblock.test.js` covers the pruning and drives the skip timer
  against a stub page; what has never run in a browser is the skip itself, because on
  every verified load the ad was gone before anything rendered. Green loads are
  evidence for the pruning and say nothing about the skip.
- `home.html` asks for its icon at `favicon.png?v=<token>`, where the token is the first
  eight hex of that icon's sha256, and `HomePageIconTests` fails when the two disagree.
  The engine caches favicons per profile keyed on the icon url, and that cache sits
  beside the profile rather than the build, so recolouring `favicon.png` in place changed
  nothing anyone could see: the logo on the page went crimson, the tab strip went on
  drawing the blue icon, and every check stayed green. Nothing renders `favicon.png`
  except the strip, which is why it was the only place the old colour survived. What the
  tab strip *draws* is the one thing nothing automated looks at (`ThemeContrastTests`
  checks its colours, not its pixels), so anything that shows up only there needs a
  guard like that one. Another bundled page with its own icon would need its own.
  Confirmed in the browser on 2026-09-11, against the profile that was already holding
  the blue icon: a new tab drew the old one before the change and the crimson one after
  it. The cache key is the whole url, and
  `HomePage.Url` is a `file://` path into the build output, so what the token retires is
  an entry under `file:///.../Assets/favicon.png`.
- The screenshots in the README were never captured from a committed build. Their title
  bars separate the page from the app name with a long dash, and `MainForm.UpdateChrome`
  has written `{Title} - Gergur` with a hyphen since `cc80de6`, which is the same commit
  that added the images, so they were mocked or edited rather than taken. They are also
  the pre-crimson artwork. Recapturing them is not a quick job and it is not free:
  `RestoreStartupAsync` reopens every window and tab from last time, so a relaunched
  window comes back carrying whatever the user had, and photographing that would commit
  their tab titles. `home.png` wants exactly one tab and `browsing.png` exactly two, so
  everything else has to be closed first, and `AppSession` saves the survivors on exit,
  which is how closing them costs the session for real. The caption "Browsing with a
  parked tab" is already untrue of the shipped picture, whose status bar reads "0/2 tabs
  asleep", so leave the second tab alone long enough to park before taking the new one.
  Nothing here captures chrome either: `Tab.CaptureScreenshotAsync` goes through the
  engine's own `CapturePreviewAsync` and gets the page alone, so the shot itself is a
  Win32 `PrintWindow` call from outside the app.
- YouTube stops starting playback through the WARP exit once it has seen enough
  traffic from it, and it looks exactly like a broken ad blocker: permanent buffering,
  no error. Turn the VPN off before suspecting anything else.

Agent actions are visualized: /click and /type animate a blue cursor to the
target, ripple, and flash the element, so the user can watch the agent work.
Both endpoints return after the animation and action complete (~1s).
