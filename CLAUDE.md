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

Every endpoint that takes a tab takes `id` or `index`, and `id` wins when both are
sent. Prefer the id: see "Naming a tab" below.

| Endpoint | Body / query | Does |
|---|---|---|
| GET /tabs | | list tabs: id, index, window, url, title, state, active, errors |
| POST /open | {"url": "...", "background": true?, "window": n?} | open tab, returns {id, index} |
| POST /activate | {"id": "t3"} or {"index": n} | switch to tab |
| POST /close | {"id": "t3"} or {"index": n} | close tab |
| POST /navigate | {"url": "...", "wait": true?, "timeout": s?} | navigate; wait returns {ok, loaded} |
| GET /page | | {url, title, text} - rendered innerText |
| GET /html | | outer HTML |
| GET /screenshot | ?activate=1, ?chrome=1 | PNG bytes |
| GET /console | | {url, errors} - the same list the status bar counts |
| POST /eval | {"js": "...", "await": false?, "timeout": s?} | run JS, returns {"ok": true, "result": ...} |
| POST /click | {"selector": "..."} | querySelector + click; 503 if the page is not loaded |
| POST /type | {"selector": "...", "text": "..."} | fill input (React-safe); 503 if the page is not loaded |
| GET /settings | | {settings, restartRequired, vpnInForce, tunnelRunning}; credentials read as "(hidden)" |
| POST /settings | {"BlocklistEnabled": false, ...} | change settings, returns {applied, restartNeededFor, unknown, persisted} |
| POST /window | {"url": "..."?, "focus": true?} | open another window, returns {window, id} |
| POST /mcp | JSON-RPC 2.0 | the same surface as MCP tools; see below |

### Naming a tab

`id` is handed out once per tab and stays with it wherever it goes, including into
another window. `index` is a position, and a tab opening, closing or being torn off
renumbers every position after it, so an agent holding an index acts on whatever slid
into that slot: the wrong page navigated away from, typed into, or closed. Read the id
out of /tabs and use that. The index still works, and still means a flat position across
every window in window order.

### Working while somebody else is using the browser

Nothing here takes the screen unless it is asked to:

- `POST /window` opens a second window, placed directly behind whatever has focus
  unless `focus` is set, and always with a tab in it. Not taking the keyboard is not the
  same as not covering the screen: a new window starts at the top of the stack, so it is
  put behind explicitly. Measured on 2026-09-22 with a terminal in front: focus kept, new
  window behind. That is the one to reach for: a window of your own is where to work
  without interrupting anyone. It answers with a `window` index.
- `POST /open` takes that `window` index, so tabs land in your window rather than the
  one the user is reading. With `background: true` it does not switch to the new tab.
- `GET /screenshot` reads whatever the tab last rendered. `activate=1` switches the tab's
  window to it first, which changes what that window shows, and is not the default. It
  never raises the window over other apps; `POST /activate` is the one that does.
- `GET /page` disturbs nothing and is the better read anyway.

Activating a tab and focusing a window are the two things the person at the keyboard
feels, so both are opt in.

A window shows one tab at a time, so `chrome=1` for a tab that is not the one on screen
is refused rather than answered with a different page inside the right frame. Pass
`activate=1` with it, or leave the tab out and photograph what is on screen.

A tab that has never been on screen answers 503 rather than a blank png, and so does one
whose page has not finished loading when the wait runs out. `activate=1` skips the first
of those deliberately: it rebuilds the view, and the capture then waits for the page
rather than sleeping a fixed moment and photographing whatever is there.

The wait reports what it saw, which sounds obvious and was not: it used to ask afterwards
whether the tab still had a load outstanding, and the staleness cutoff on that answer was
the same fifteen seconds the wait itself takes, so a page that never loaded read as
"settled" the moment the wait gave up and the refusal turned back into a blank png.

There is still a cutoff, at two minutes, on how long a tab counts as loading. A navigation
that starts and never completes, which is what a link that turns into a download looks
like, would otherwise make every later read wait for a page that is not coming. So a tab
left loading for longer than that reads as settled and will be photographed as it stands,
which for a slow-loris url means the previous page under the new url. Two minutes is long
enough that nothing ordinary reaches it; it is written down here because it is a real hole
rather than a tidy one.

### Settings over the API

`GET /settings` reports every setting. Credentials read as `"(hidden)"`: the answer goes
into a transcript, and `DropKey` is the pairing key for the only listener in this browser
that reaches past loopback.

`POST /settings` refuses a setting outright when changing it would widen what this API can
reach *and* outlive the agent that changed it. Everything on that list is persisted, in
force at the next launch long after the session is gone, and shows up nowhere a person
would look:

- `DropKey`, `DropEnabled`, `DropPort`, `AgentServerEnabled`, `AgentServerPort` - who can
  reach this browser from off the machine, and with what credential.
- `ExtraBrowserArguments` - arbitrary engine flags, which is the rest of its security.
- `DisableSiteIsolation` - the renderer sandbox boundary.
- `VpnEnabled`, `VpnProfile`, `VpnBypassHosts`, `VpnLocalPort` - whether traffic goes
  through the tunnel, which one, what skips it, and the port the engine's `--proxy-server`
  flag points at, which is every request the browser makes. `"VpnBypassHosts": "*"`
  sends everything around the tunnel while `GET /settings` still reports the vpn as on
  and the status bar still shows it.
- `SearchUrlTemplate` - where every search the person types goes. Validating it was tried
  first and was not enough: a template can be perfectly well formed and still point at
  somebody else's server.

Those are changed from the settings window, where somebody is looking at what they typed.
The refusal names the setting rather than dropping it quietly. It refuses in both
directions on purpose: an agent cannot turn the phone drop or the vpn *off* either, even
though that narrows the reach, because a coarse rule you can state in one sentence is
easier to keep right than a list of exceptions. Turning the vpn off to test the YouTube
buffering symptom below is a settings-window job.

Everything else is range checked as well as type checked, and a single refusal rejects the
whole patch rather than applying half of it. What can take effect without a restart does,
through the same path the settings dialog uses, so `applied` means applied;
`restartNeededFor` names the engine flags that did not. That includes the page settings on
tabs already open (`MainForm.ApplyLiveSettings` pushes them into every live view): the
colour scheme, tracking prevention and the two autofill switches at once, `PageAdCleanup`
from the next page each tab loads, since it is a script that runs as a document is
created. A suspended tab takes them when it wakes, since calls into a sleeping view can
wake it. Those used to reach only views built afterwards while being reported as applied.

`persisted: false` comes back, with the reason, when Gergur could not read `settings.json`
at startup. That run is on defaults and leaves the file alone for good: saving those
defaults would replace the pairing key and the vpn profile with them. The settings window
and the menu toggles are held to the same rule and say so in a box; the vpn toggle does
not restart for a change it could not save, because the restart would read the old value
back, and the phone drop does not start, because its pairing key would not survive.

A tunnel that will not come up at startup is recorded for that run only
(`Settings.VpnDownThisRun`), never in `VpnEnabled`. It used to switch `VpnEnabled` off "for
this session", and the next save of anything wrote that to disk, an agent's `/settings`
patch of an unrelated setting included: the vpn was then off at the next start, and an
agent had turned it off without naming it. A new engine points at the tunnel when
`StartWithProxy` (chosen, and up) says so, and what the running engine was given is recorded
once in `BrowserEnvironment.ProxyInForce`: the menu, the status bar, whether a profile can
be switched live, what the menu's Off does (`MainForm.TurnVpnOff`, run in all four states
with a save that works and one that fails), and `vpnInForce` on `GET /settings` all read that, because `VpnEnabled` moves
under a running engine whenever a change is waiting for a restart. So `VpnEnabled` in
`settings` is the choice, `vpnInForce` says the engine points at the tunnel, and
`tunnelRunning` says the tunnel's process is running (a start that times out is now
taken down, so a failed start no longer reads as running). Traffic goes through the vpn only when both are
true; in force with no tunnel, requests fail rather than go around it.

`UrlHeuristics.Search` falls back to the default template rather than throwing, because a
hand-edited `settings.json` never passes through the API: `string.Format` throws on a
template naming an argument it was not given, the address bar calls it straight from the
Enter key with no try/catch, and there is no unhandled-exception handler in the app. A bad
template in the file used to turn every search into a crash dialog that survived restarts.

### When a read refuses

Every endpoint that touches a page answers 503 rather than guessing when the page is not
there: /page, /html, /eval, /click, /type and /screenshot. /open, /window and /navigate
answer 503 "could not start" when the engine failed to build the tab's view, which is a
different thing from a url it refused and has a different fix. /open and /window take
the tab or window they made away again before answering (`TabManager.OpenOrDiscardAsync`),
so a failure leaves nothing for the person to find: no tab, no "could not start" in their
status bar about a tab that is gone, not on their Ctrl+Shift+T stack, and for a tab opened
in front, the tab they were reading comes back rather than whichever sat next to it.
/navigate keeps its tab and says in `retryInSeconds` how long until it can be tried again,
because a tab that failed waits five seconds before the next attempt, and five minutes
after three failures in a row. Nothing retries by itself: the next read or navigate after
that is the retry. A build that is slow rather than failed is not reported as either,
including a retry after an earlier failure that is still under way: a `wait: true`
navigate gives up on it within its own timeout, answers `loaded: false`, and the
navigation happens when the view arrives. Two navigations asked for during one slow build
end on the second.

A 500, or an MCP tool that "failed inside the browser", is written to
`%LOCALAPPDATA%\Gergur\debug.log` whether or not `GERGUR_DEBUG` is set. That is where the
cause is: the answer itself only says where to look.

Refusing matters most for /click and /type, which used to answer `{"ok": false}` for a
page that never loaded, and that is indistinguishable from "nothing matched that
selector": an agent reads it as a bad selector and retries against a page that was never
there.

### Waiting for things

- `POST /eval` awaits a promise, so `fetch(...).then(r => r.json())` comes back with
  its value rather than `{}`. Only an expression can be awaited; a script of several
  statements runs as it always did and answers with its completion value. Which of the
  two it is gets decided by parsing the source without running it, so a well formed
  expression cannot run twice. `await=0` forces the old immediate read. The answer is
  `{"ok": true, "result": ...}` or `{"ok": false, "error": "..."}`, either way, and that
  includes a script that threw: the plain engine call reports a thrown error and the
  value null identically, which is why this one does not use it.
- Reading a tab wakes it, and that means both kinds of asleep. A suspended tab is
  resumed; a discarded one has its view rebuilt, and either way the read waits for
  whatever page is on its way, whoever started it. Waiting only when *this* call had to
  build the view was not enough: activating a tab, or clicking it in the strip, issues the
  navigation and returns, so the next read saw a live view with nothing in it and answered
  200 with the real url, the real title and an empty page. The wait is 15 seconds, under
  the call's own budget rather than added to it, so waking a tab does not push a request
  past its own deadline. A screenshot spends one budget across the wait, the paint and the
  capture, and a `wait: true` navigate starts its clock before the view is built. Both
  used to hold two full waits: a wake of 15 seconds and then the endpoint's own 15 made a
  screenshot of a wedged page take 30, past the 20 an MCP tool call gets, so the honest
  refusal arrived as "the tool call was abandoned".
- Reading a tab counts as using it, so it will not be frozen underneath the request, and
  an agent polling one more often than the suspend timer keeps that tab's renderer alive.
  That is deliberate, and it is a real memory cost in a browser whose whole point is to
  be frugal: prefer reading a tab when you need it to polling it.
- `POST /navigate` with `wait: true` comes back when *that* page has loaded and says
  whether it actually did, rather than leaving you to sleep and hope. A sleep that is too
  short reads as a broken page: one run here reported a video player wedged when it had
  simply not started yet. It is bound to the navigation it asked for, not to the next one
  the tab happens to finish, because navigating a tab that is still loading aborts the
  old load and that abort arrives first.
- Over `/mcp` both waits are capped to the tool call's own budget, so a slow page answers
  `{"ok": true, "loaded": false}` rather than the tool reporting itself abandoned.
- `GET /screenshot?chrome=1` photographs the whole window, tab strip and toolbar and
  status bar included, using `PrintWindow` from outside the app. The engine's own
  capture returns the page alone, and two of the faults reported in this browser lived in
  the chrome where nothing automated could see them.

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

`window` says which window a tab is in, and `active` means active within that window,
so several tabs can be active at once. With neither `id` nor `index`, an endpoint acts
on the active tab of the window the person last brought to the front, which the app
records as it happens (`AppSession.LastActiveWindow`). Asking a form whether it has focus
from a request thread always says no, so "the focused window" used to mean window 0
whatever the person was using. Reading or evaluating a parked (asleep) tab
wakes it. The user's browsing is personal: read what the task requires, nothing more.

## Notes

- Tabs leave their strip sideways as well as up and down. Height alone was the old test,
  and with monitors side by side the natural way to move a tab to the other screen is
  sideways, level with the strip, so it counted as a reorder: pulling off the left edge
  worked out as "move to the front". Being over another window's strip counts as leaving
  too, even inside the margin, because two maximised windows' strips meet at the monitor
  boundary. A torn off window lands on the monitor it was dropped on (`TabDrag.TearOffBounds`);
  it used to be clamped at zero, which is only the edge of the primary monitor, so a screen
  to its left or above sent it back. The user's machine has a monitor at X=-1920.
- A tab whose page the engine could not start says so ("could not start", a status
  message), and at launch the first one brings up "Gergur failed to start". The build
  itself never throws: it is awaited from a tab click, an async void handler, in an app
  with no unhandled exception handler, and throwing there took the whole browser down.
- Live verification, 2026-09-22, against the Release build of that afternoon, before the
  last two review rounds: 48 endpoint checks passing, a `chrome=1` capture looked at by
  eye, which does include the rendered page area, and an agent window measured behind a
  terminal. The drag, sideways onto another monitor, was confirmed by hand by the user
  the same day; nothing automated drives it.
- Live verification again that evening, against a Release build of 8f5abbc, the user's
  real profile, a window of the script's own: `scripts/verify-agent-api.ps1` passed 51 of
  51 on its second run, and both captures were looked at by eye. The first run, about a
  minute after launch, lost one plain `/screenshot` of the script's own loaded page to
  "no rendered page to photograph when the wait ran out" (that message covers a capture
  that timed out or threw and a wake that ran out; which one was not established). It
  did not happen again in three tries on the same build, and the cause is not known. The
  same run showed that an `edge://` url is accepted and fails at once (`loaded: false`),
  so the script's check for it was wrong to demand 503; it now checks that the answer
  comes back at once.
- What changed between the afternoon build and 8f5abbc, and what else checks each part:
  - Unit tests, which exercise the decision with no engine behind it:
    `TabManager.OpenOrDiscardAsync` (a failed tab taken away quietly, the previous tab
    back), `Tab.CouldNotStart`, `AgentServer.NavigateAnswer` and `PageCouldNotStart`,
    `AppSession.PickWindow`, the held navigation for a slow build, settings held while
    a tab sleeps and a suspended tab staying suspended when switched away from, the
    settings save refusal (`Settings.Save`, `DropServer.EnsureKey`), and a tunnel that
    failed this run never being saved as turned off (`Settings.VpnDownThisRun`), and what
    the vpn menu's Off does in its four states, with the save working and failing
    (`MainForm.TurnVpnOff`). Turning the vpn on is picking a profile, which is not covered.
  - Source checks only (`TheWindowInUseIsWhatEveryCrossThreadCallerAsks`), which prove the
    call is there and nothing about what it does at runtime: that `ActiveEntry` and
    `OpenExternalUrl` go through `WindowInUse`, that `OnActivated` records the window, and
    that `ApplyLiveSettings` pushes into open tabs, that a woken tab is handed the settings
    held for it, that startup marks a failed tunnel with `VpnDownThisRun` rather than
    `VpnEnabled`, that `GET /settings` reports `vpnInForce` and `tunnelRunning` from one
    snapshot, that a failed profile pick puts back the previous choice rather than
    forcing the vpn off, that the vpn menu, status bar and live switch read
    `ProxyInForce`, and that the real pairing key path saves through `Settings.Save`.
  - Nothing at all: whether `ApplySettingsLiveAsync` actually changes an open page (it
    needs an engine), `/settings` answering `persisted: false`, the not-saved boxes in the
    settings window and the menu toggles, the vpn menu being disabled while Gergur starts, `VpnTunnel.StartAsync` taking a failed
    start down without touching a newer pick's tunnel (it needs a wireproxy; a review
    ran it in a copy against a fake one, four cases including two overlapping picks), and `/window` closing an empty window.
  - `scripts/verify-agent-api.ps1` exercises only the happy paths of /open and /window
    among these, since every call it makes names a tab and it never changes a page
    setting. It was run against 8f5abbc that evening, as above.
  Never exercised live at all: a view build that fails (and so the could-not-start
  messages and the taking away of a failed /open or /window), a tab discarded after
  fifteen minutes, the sleep timers applying live, an agent window placed while another
  Gergur window has focus rather than a terminal, and tearing a tab off onto a monitor
  with a different scale, since every monitor on this machine is at 96 dpi.
- An agent's own window (`POST /window`, `MainForm.OpenedByAgent`) is never part of the
  person's session, and when only agent windows are left the session file is not written
  at all (`AppSession.SessionOf`). On 2026-09-24 the person closed their own window, an
  agent's window was then the last one, the agent closed its own last tab, the app
  exited, and the exit saved "no windows" over the person's tabs. They were restored
  from a backup two days old; anything newer was lost. A window stops being the agent's
  the moment the person uses it (`ClaimForPerson`): typing in its address bar, clicking
  its tab strip, any keyboard shortcut in it, a link from another app landing in it, or a
  tab dragged in from their own window. Otherwise dragging all their tabs into an agent
  window emptied their own, which saved as nothing while the tabs lived on unsaved.
- History, Downloads and Bookmarks are pages in a tab (`Assets/history.html`,
  `downloads.html`, `bookmarks.html`, and the new tab page `home.html`), shown in the
  address bar as `gergur://history` and so on, which can also be typed, and saved in the
  session by those names rather than a path into the install. They were separate
  windows. They reach the browser only by posting web messages, and any page can post
  one, a site included, so `InternalPages` treats a message as a request only when it
  comes from one of those files in this install's Assets folder, by exact path, and each
  page may ask only for what it shows: the new tab page can list bookmarks and nothing
  else, a site can do nothing. Replies and pushes go only to the document the tab has
  actually committed to (`Tab.ShownPage`), never by the address a navigation is heading
  to, and never into a sleeping tab; a page reloads its list when it is shown again.
  Pages must build their DOM with `textContent`, never `innerHTML`, since titles and urls
  come from the sites themselves. `OwnPagesHardeningTests` checks that every op a page
  calls is one the host answers: the bookmark Undo once called an op only the demo mode had.
- The agent API refuses `/page`, `/html`, `/screenshot`, `/console`, `/eval`, `/click` and
  `/type` on those pages (403). Through them it could read the whole history and bookmark
  list, launch any downloaded file, or clear history without the page's own confirm,
  which is the kind of reach the `/settings` refusals exist to stop. Opening them is fine.
  Checked twice: on entry against both the address a navigation is heading to and the
  page actually committed (`AgentServer.OwnPageRefusal`), since a navigation that never
  commits leaves the old page on screen under a new address; and again on the UI thread
  right before each place agent code or a capture actually runs, with nothing awaited
  in between (`Tab.RefuseOwnPage`: ReadScriptAsync, CaptureScreenshotAsync, the
  `chrome=1` and `/console` steps, and in `/eval` before its probe, before the statement
  run and before the awaiting run, outside every catch), since the wait for the page
  before that is long enough for the tab to arrive at one of these pages. That second
  check also counts a navigation the engine has announced and not finished
  (`NoteNavigationUnderWay`): the engine cannot commit a navigation before the UI thread
  has handled its NavigationStarting, but it can before the UI thread has handled the
  SourceChanged after it, and a Back click to one of these pages never moves `Url`. That
  ordering is reasoned from the WebView2 contract (the host may cancel in
  NavigationStarting, so the engine waits for it) and has not been exercised against an
  engine. Back is covered only because these pages are `file://`, which Chromium's
  back-forward cache never restores without a NavigationStarting; moved to an https
  virtual host they would need looking at again. The decision is `Tab.RefusalFor`, unit
  tested with every address; the order of the checks is held by source checks. Two consequences, both
  deliberate: a tab still leaving one of these pages (where every `/window` and url-less
  `/open` starts) is refused until the new page commits, answered 503 "still leaving"
  rather than 403, since it is "not yet" and not "never"; and a `chrome=1` capture of any
  tab always shows the bookmarks bar, whose titles are therefore readable to an agent
  with the token. `/tabs` leaves out `errors` for these pages, as `/console` refuses them,
  and reports their `url` as `gergur://history` and the like, the new tab page as
  `gergur://newtab`: the file url is a path into the install with the account name in it.
- A link with `target=_blank` that only starts a download used to leave an empty tab
  behind, in front of the page it came from. That tab now leaves the strip at once and
  the opener comes back (`TabManager.SetAsideAsync`), but its view is kept, out of sight,
  until every download it started has ended: whether closing it would cancel a download
  was not worth finding out with somebody's file. The exception is its window closing,
  which disposes it with everything else. A new window that shows a page first, or that
  its opener wrote into (a report, a print view), is not treated as empty.
- The bookmarks bar is drawn by hand (`BookmarksBar`), not a page, so it costs a window
  handle rather than a renderer. Ctrl+Shift+B toggles it, Ctrl+Shift+O opens the
  bookmarks page. The status bar shows only what changes: engine memory and renderer
  count are the tooltip of its right-hand items (`ShowItemToolTips`, off by default on a
  StatusStrip, which is why an earlier error tooltip never showed either).
- A `bookmarks.json` that is there but cannot be read or parsed at startup (or parses into
  entries with no url or title, such as `[null]`, which the bar would throw on) loads as an
  empty list, and every change that run is refused (`BookmarkStore.UnreadableException`),
  the same rule as `settings.json`. It used to be saved over by the next bookmark, and
  this sprint gave it four more writers. The first refusal in a run is a box, later ones
  the status bar; the bookmarks page and the new tab page say it could not be read rather
  than "No bookmarks yet". The bar is simply empty. A file that is not there yet, or is
  empty, is not unreadable: there is nothing in it to lose.
- Settings: `%LOCALAPPDATA%\Gergur\settings.json`, or `GET`/`POST /settings` while it
  runs. Engine flags (VPN, process policy) apply only on a fresh engine start, and
  `restartNeededFor` in the response names the ones that have not taken effect yet
  rather than leaving you to find out.
- `Settings.Save()` writes to one fixed path, so nothing a test can reach may call it.
  `SettingsPatch.Apply` changes memory and the `/settings` endpoint decides to persist.
  It did not always: a run of SettingsPatchTests wrote its fixtures over the real file
  and took out the vpn profile, the blocklist and the phone drop pairing key, and that
  key was not recoverable from anything in the build. The tests now read that file
  before and after and fail if it moved.
- Debug trace: set `GERGUR_DEBUG=1` before launch, log at `%LOCALAPPDATA%\Gergur\debug.log`.
  Failures are written there without it: agent API 500s, a view that could not start
  (without its url, which is browsing and only logged with tracing on), a window that
  could not be put behind the one in use, a settings file that could not be read. It
  starts over past 4 MB and keeps the one before as `debug.log.old`, and keeps writing
  past the cap while something holds the file open rather than going silent. The test
  assembly sends it to `%TEMP%\gergur-tests\debug.log` before any test runs (`TestLog`),
  because tests fail builds on purpose and each one used to land in the real log looking
  exactly like a real failure; `DebugLogTests.TestsNeverWriteTheRealLog` fails if that
  stops.
- `errors` on /tabs is exactly what the status bar's "⚠ N issues" counts: JS errors,
  unhandled rejections, `console.error` calls, and failed subresource loads on that
  tab's current page. It clears on every navigation, so it always describes the page
  you are looking at, not the session. Requests the blocklist killed are excluded:
  they are the blocker working, not a page defect. A cross-origin script served
  without CORS headers reports opaquely (no message, file, or line) and is labelled
  as such rather than shown as a bare "Script error".
- `PrintWindow` is how the chrome gets photographed, and it answers TRUE for a minimised
  window while drawing nothing, so that case is refused rather than served as a black
  png. Whether it captures the WebView2 page area at all under PW_RENDERFULLCONTENT is
  the classic blind spot with hardware composited content: verify a `chrome=1` capture
  by looking at the image, not by checking that bytes came back.
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
  evidence for the pruning and say nothing about the skip. On 2026-09-24, with page ad
  cleanup switched off so an ad did render, an emulation of the skip path (mute, press
  skip, seek the ad to its end) cleared the ad in about a second and then the film never
  started, three of three: the seek to the end wedges the player on these loads.
- The ~12 second wait before a YouTube video starts is YouTube's server, not this
  browser, measured 2026-09-24. The session is flagged as blocking ads (every page carries
  `bkaEnforcementMessageViewModel`), and on loads where an ad was scheduled the player's
  first SABR `videoplayback` response carries no media and a backoff of about 12000 ms
  (the NEXT_REQUEST_POLICY part), which the server enforces: asked early, it answers with
  the time still remaining. Loads with no ad start in under a second. What ships is
  already the fastest option measured: about 16 s to the film, against about 21 s for
  letting the ad play, and never for skipping it. What might lift the flag is upstream
  of the page and untested: the WARP exit, or the blocklist killing YouTube's
  `ad_status.js` probe, which would likely trade the wait for an ad.
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
