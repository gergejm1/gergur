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
| POST /open | {"url": "...", "background": true?, "window": n?, "muted": true?} | open tab, returns {id, index} |
| POST /activate | {"id": "t3"} or {"index": n} | switch to tab |
| POST /close | {"id": "t3"} or {"index": n} | close tab |
| POST /navigate | {"url": "...", "wait": true?, "timeout": s?} | navigate; wait returns {ok, loaded} |
| GET /page | | {url, title, text} - rendered innerText |
| GET /html | | outer HTML |
| GET /screenshot | ?activate=1, ?chrome=1 | PNG bytes |
| GET /console | | {url, errors} - the same list the status bar counts |
| POST /eval | {"js": "...", "await": false?, "timeout": s?} | run JS, returns {"ok": true, "result": ...}; 409 where a password is filled in (see "Saved passwords") |
| POST /click | {"selector": "..."} | a real (trusted) mouse click; reports covered, fileChooser, opened tabs, dialogs. See "Acting like a person" |
| POST /type | {"selector": "...", "text": "...", "clear": false?, "submit": true?} | real typing (Input.insertText); answers matches without echoing the text |
| POST /key | {"key": "Enter" or "Ctrl+A", "selector": "..."?, "times": n?} | press a key or chord |
| POST /hover | {"selector": "..."} | move the mouse over it and leave it there |
| POST /scroll | {"selector"?, "dx"?, "dy"?, "to": "top"/"bottom"?} | wheel, scroll into view, or to an end; answers {x, y, height, viewport, atTop, atBottom} |
| POST /select | {"selector": "...", "value" or "label": "..."} | choose a <select> option; lists the options when none fits |
| POST /wait | {"selector" and/or "text", "gone": true?, "timeout": s?} | {met, waitedSeconds}; met:false at the timeout, not an error |
| POST /upload | {"selector": "...", "files": ["C:/abs/path", ...]} | file input (hidden ones too) or a button that opens the chooser |
| GET or POST /dialogs | {"accept": false?, "text": "..."?} | recent page dialogs, and how the next confirm/prompt are answered |
| GET /downloads | ?wait=1&timeout=s | the caller's own downloads: file, path, state, bytes |
| POST /ask | {"question": "..."} or {"id": "ask-1"}, "timeout": s? | ask the person; a bar in their window. See "Asking the person" |
| POST /login | {"submit": false?, "account": "..."?} | sign in with a saved password, choosing the account and filling two-step password pages and sign-in forms in a frame; on Google's account chooser, clicks the person's Google account for the site: {filled, user, submitted, via, stillAsking, url, picked, frame}. See "Saved passwords" |
| GET /settings | | {settings, restartRequired, vpnInForce, tunnelRunning}; credentials read as "(hidden)" |
| POST /settings | {"BlocklistEnabled": false, ...} | change settings, returns {applied, restartNeededFor, unknown, persisted} |
| POST /window | {"url": "..."?, "focus": true?, "muted": true?} | open another window, returns {window, id} |
| POST /task | {"task": "Researching flights to Tokyo"} | say what you are doing; shown to the person (see "Saying what you are doing") |

`muted` opens the tab silent from its first frame: the mute is set on the tab before its
view exists, and every view the tab builds afterwards starts muted. Use it for anything that
plays sound (testing a video, a playlist): without it the only way to test YouTube was to
play it at whoever was sitting at the computer.
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

- `POST /window` opens a second window, always with a tab in it, and unless `focus` is
  set the person never sees it at all: it is cloaked by the window manager before its
  first frame (`DWMWA_CLOAK`, what Windows does to a window on another virtual desktop)
  and its taskbar button is taken away (`ITaskbarList`), so it is not on screen, not on
  the taskbar and not in Alt+Tab (`MainForm.HiddenForAgent`, `WindowStacking`). Its page
  still draws, captures and takes trusted clicks: measured on WebView2, a cloaked page
  stays "visible" to itself. It is also placed behind whatever has focus, as before, for
  the case below where cloaking fails. That is the one to reach for: a window of your own
  is where to work without interrupting anyone. It answers with a `window` index.
  - Only the person shows it: Show on the agents panel or page. `/activate` never shows a
    hidden window (it switches the tab and leaves the window hidden). Hide, X, Alt+F4 and
    the taskbar's Close hide it again (`SendBackToAgent`); the person taking the window
    over (`ClaimForPerson`) shows it for good.
  - A hidden window is silent: the person asked never to hear a video an agent plays
    there, even one it needs to play, until they show it. The engine is told
    `Tab.EngineMuted(IsMuted, SilencedByWindow)`, so the page still plays and its media
    still runs, only nothing reaches the speakers. `IsMuted` stays the tab's own choice
    (`muted: true`, Ctrl+M) and is what the strip shows; shown, a tab plays as it was
    chosen to. `MainForm.HiddenForAgent` is the one place the window's hiddenness is
    written, and its setter tells every tab (`Tab.RefreshSound`); a view built in a
    hidden window starts silent before anything is navigated, and a tab moved between
    windows follows its new one (`TransferTo`). Tabs set aside for a download follow it
    too (`TabManager.RefreshSound`). Measured 2026-11-03 with a Windows audio peak meter
    on the engine's sessions, a local page playing a tone at gain 0.003 after a trusted
    click, in a hidden agent window: the build before this change opened an active session
    at peak 0.003; this one opened none while the page's audio clock ran for 19 s. Not
    measured: the sound coming back when the person shows the window (that would play at
    them); it rests on `ATabFollowsItsWindowAndKeepsItsOwnChoice`.
  - If the system will not cloak it (the attribute is undocumented), it opens as it used
    to, behind and on the taskbar, and does not count as hidden: a window left on screen
    with no taskbar button would be one nobody can find.
  - When the person closes the last window they can see and only hidden agent windows are
    left, those close too and the app exits (`MainForm.QuitsWithThisWindow`); otherwise
    a browser would run on where nobody can find it. Work in progress in them ends. The
    session file keeps the person's tabs (`AppSession.SessionOf` writes nothing when only
    agent windows are left).
  - An agent's window has a blue band across it (`AgentBanner`: AGENT, the agent's name,
    what it says it is doing, Hide), and every tab in it, like every tab an agent opened
    in one of the person's windows (`Tab.AgentName`), wears an AGENT pill on the strip.
    The pill on a tab in the person's window goes once they navigate it from the address bar.
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
- `RelaxedSites` - sites where requests are not blocked and ads are not taken out. An agent
  adding one would weaken the blocker at every later launch with nothing to show for it.
- `BuildSongLibrary` - whether Gergur downloads song previews and sends artist names to Deezer,
  for hours, in the background.

Those are changed from the settings page (`gergur://settings`), where somebody is looking at what they typed.
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
- History, Downloads, Bookmarks, Settings and Agents are pages in a tab (`Assets/history.html`,
  `downloads.html`, `bookmarks.html`, `settings.html`, and the new tab page `home.html`), shown in the
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
  `/type` on those pages (403), the settings page included. Through them it could read the whole history and bookmark
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
- So the skip no longer seeks at all (2026-11-02). An ad that has the player
  (`ad-interrupting`, and a length an ad could be) is muted and played at 16x, and the
  element's own speed is given back the moment the class goes. The seek was also what the
  user reported as "playlists skip like four songs": where YouTube stitches the ad into
  the same stream as the song, the element holds both, its length is a song's, and its end
  is the song's end, so seeking there ended the song, the playlist moved on, and the next
  break did the same. A wrong guess about speed costs a moment of song played fast and is
  undone by itself; a wrong seek cost the song. `adblock.test.js` pins both ("a song with
  an ad stitched into it is never ended").
- The "Ad blockers are not allowed on YouTube" box is the server's
  `bkaEnforcementMessageViewModel` (and `enforcementMessageViewModel`) under
  `auxiliaryUi.messageRenderers` of a flagged player response. Both are pruned with the ad
  keys at the same safe moment, and if the box renders anyway (music.youtube.com loads its
  responses by fetch, which nothing may touch) the tick presses its close button and plays
  the video it paused. Only if the box is still shown two ticks (about 600 ms) later does
  it remove the dialog and the backdrops marked `opened`, once per box: a paper dialog
  pulled out from under its overlay manager leaves that manager thinking it is open, and
  the site's next dialog can then come up behind a backdrop nobody can close. A closed
  paper dialog stays in the page hidden, so "shown" is read from `opened` and
  `display`, not from the box being found. Both of these were
  written from the report and unit tested against stub pages; the 16x run-out has still
  not met a real stitched break (no ad ran in the live test below).
- Playlist songs being "skipped" had this cause on 2026-11-02; two others were measured on
  2026-11-04, see "A playlist skipping songs" under Now playing. This one is YouTube refusing
  to play, not the ad code, measured
  2026-11-02 on the signed-in profile with the VPN off (`yt-playlist-test.ps1` in a muted
  agent window, then `getPlayerResponse()` on the tab): every song's
  `playabilityStatus.status` was `ERROR` with `errorScreen.enforcementMessageViewModel`
  ("Ad blockers violate YouTube's Terms of Service", primary button "Allow YouTube Ads"),
  although `streamingData` was present. The video never loads (`readyState` 0, no
  `currentSrc`), the mix treats it as unplayable and moves on about every 6 s, and no ad is
  ever shown. Songs after the first come by fetch, which nothing here touches, and they
  were `ERROR` too, so this is the server's verdict on the session, not something in the
  page. The message renders inline in the player, not in a paper dialog. Pressing or
  removing it was not tried, and is expected to change nothing, because the player has
  no media to play. Nothing on the page
  side can undo a server `ERROR`. What might lift it is the browser looking like it shows
  ads on YouTube (ad requests let through, the ads muted and run out at 16x by the tick
  that already exists), which needs a per-site exemption from the blocklist and the
  pruning that does not exist yet; untested, and how long the flag takes to clear is not
  known.
- `adblock.js` is read again when its file changes (`PageCleanup.Read`, by stamp and
  length), so trying a change no longer costs a restart: edit the copy under
  `bin\Release\net10.0-windows\Assets\` and open a fresh tab or window (an agent window,
  `muted: true`). Pages already open keep the script they were given. A file that cannot
  be read keeps the last good script.
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
- The menu button and the downloads button drop panels drawn by hand (`FlyoutPanel`,
  `MainMenuFlyout`, `DownloadsFlyout`), not ContextMenuStrips, so the menu can hold tiles
  for history, downloads and bookmarks, a zoom stepper that stays open between steps, and
  switches that show their state. The vpn, phone drop, bookmarks and "More" lists still
  open to the side as the ToolStripMenuItems `BuildVpnMenu` and friends have always built,
  so what they do is unchanged; `FlyoutTests.EveryCommandTheOldMenuHadIsStillOnTheNewOne`
  holds every call the old list made. The menu takes the keyboard (arrows, Tab, Enter, Esc;
  Right, Enter or Space on a side-list line opens it on its first usable line). Once a side
  list is open the menu does not drive it: WinForms' own menu filter (`ToolStripManager`)
  sends keystrokes to the open list, which moves, skips greyed lines, picks, and closes on
  Left or Esc by itself. A handler in the panel for those keys was written, never reached
  in a real message loop, and taken out. The downloads panel never does: it opens by itself when a
  download starts in the window the person is using (not an agent's, not a minimised or
  background one, not over a fullscreen film), goes again after six seconds unless the
  pointer is on it, and is `WS_EX_NOACTIVATE`, so it cannot take keystrokes from someone
  typing. A window that is never active is never deactivated either, so it closes on a
  click outside it by watching the mouse buttons globally (`GetAsyncKeyState`; the page is
  another process's window, so this thread's own key state never sees that click). Its rows
  open the file on click, with Show in folder and Cancel beside them, and its last line
  opens the downloads page; Ctrl+J still goes straight to the page. A panel also goes when
  its button moves on screen (the window moved, resized or snapped), and keeps a press
  across the relayouts a running download makes, so Cancel can be clicked mid-download.
  Lengths go through `S()` and fonts are made in pixels per DPI (`MakeFonts`), with the
  handle created on the button's monitor before anything is measured. What was checked:
  the panels rendered to images off screen from the real source; hit testing, layout and
  focus order unit tested; and the real panels driven as real windows on a hidden desktop
  (a scratch exe compiling the real files, the same method as the title bar work): placement
  under the button, the click that spans two relayouts, the menu going when its window
  moves, and the side-list keyboard (that run is what showed the filter above). All at
  96 dpi only; a second monitor at another scale has not been tried. The downloads panel's
  foreground-window check cannot be run on a hidden desktop (the foreground window is on
  the real one) and is only exercised in the browser.
- More panels hang from the toolbar, all `FlyoutPanel`s like the menu:
  - A panel opens on its button's own monitor. A panel's window is created at the button
    before it is measured, and `MenuPlacement` places it within that monitor's working
    area. Before, a panel could open on the neighbouring monitor (three side by side here,
    one at X=-1920). A side list (`ToolStripDropDown`) that still belongs to its item ignores the
    point it is shown at and came up at 0,0; `OpenSubmenu` sets `OwnerItem = null` first.
  - The menu's History tile opens `HistoryFlyout`: the last eight places, deduplicated,
    with "Show all history" (Ctrl+H) through to the page. Its Downloads tile opens the
    downloads panel rather than the page.
  - Agents (`AgentsFlyout`, the robot button beside the menu, shown while the agent API is
    on): every agent since start, active (green dot, 30 s after its last request) or not,
    what it says it is doing, what it did last in a few words, and up to three of its live
    tabs, any of which is brought forward with its window on click (out of hiding), and a
    last row, "Open the agents page".
  - The agents page (`gergur://agents`, `Assets/agents.html`, `AgentsPage` and
    `IAgentsHost` behind it): the panel at full size. Each agent with its task, its
    timeline (the last twelve actions with times), and its open tabs, each with a live
    picture and Show, Hide (only in an agent's own window) and DevTools. Every tab says
    where it is: hidden, agent window, or "Your window". Its ops (`agents.*`) act only on
    tabs some agent has worked in and that are still open (`InternalPages.AgentTabOf`). The
    pictures are small JPEGs (`Tab.CaptureThumbnailAsync`, 560 px wide) pushed as `data:`
    urls in a "snapshot" push, which is why this page's CSP alone allows `img-src data:`.
    A picture never wakes a tab or counts as using it, and they are rationed: two at most
    under way, each tab at most every eight seconds, only cards on screen, one capture per
    tab at a time on the browser side. The list itself is read every four seconds and on a
    push after each agent action. The agent API cannot read or script it, like every other
    own page. Verified: unit tests against a fake host, and the page in headless Edge with
    `?demo=1`. MCP clients are named by `clientInfo.name`
    from initialize, carried as the `Mcp-Session-Id` header the server now issues; REST
    callers may send `X-Gergur-Agent: name`, otherwise they are "Script". `AgentActivity`
    records the route, the tab and the url's site, never typed text, scripts or page
    contents (a source check holds that).
  - Phone drop (`PhoneFlyout`, the phone button): off, it offers Turn on (the menu's
    `TogglePhoneBridge`, which asks first). On: Send page, Send file, Pair phone; a
    message box where Enter sends (a file or a picture on the clipboard is sent as it is
    by Ctrl+V); files dropped on the panel or on the button are sent; the last four items
    open (a link in a new tab), copy, or show in folder. A program from the phone is never
    run, as in the drop window. Files are copied into the drop, never moved
    (`AddFileFromPath` moves its source), off the UI thread, 1 GB each at most. The
    button gets a dot for things from the phone since the panel was last opened in any
    window (`AppSession.DropSeenUtc`) and is dim while the drop is off.
  - Verified: renders of every panel, unit tests, and the hidden-desktop harness (46
    checks: placement on all three monitors, the phone panel's keyboard: letters and Space
    go into its box, Enter sends and lists it, Tab out and typing back, Esc). That run
    found the Recent list only refreshing through the window's listener; the panel now
    relays itself out after a send. Not run in the real browser yet as of writing, and
    dragging a file from Explorer onto the button has not been tried at all.
- Per-site relax (`Settings.RelaxedSites`, the menu's "Relax blocking on {site}"):
  requests made from a page on a relaxed site are not blocked (`RequestBlocker.Relaxed`,
  judged by the request's own host for a document and by the page otherwise), and
  `adblock.js` skips pruning, cosmetic css and its forced reloads there, but its ad tick
  still mutes, runs at 16x and skips. Made for YouTube's server-side flag (see the
  playlist note above); whether relaxing YouTube lifts it, and how fast, is not known.
  On 2026-11-03 the person reported their playlists stopping with the ad-blocker message
  and moving on every song, with youtube.com already relaxed; while looking (a muted hidden
  agent window on the real profile, about a dozen loads and a Mix advanced three songs) no
  load was refused, and the person then said their playlists worked again. Nothing was
  changed: the two ads let through in that window, run at 16x, may have counted, or a tab
  opened before the relax still had the old script. Not established. Seen in the same
  run and not yet looked into: both loads that served a pre-roll ran it out muted at 16x
  in under a second, then the film never started (`unstarted-mode`, `readyState` 0, no
  player response, for 45 s). Later the same day it came back: the person's playlist tab
  (a Mix, loaded 24 minutes before) had every song `ERROR` with
  `bkaEnforcementMessageViewModel` and moved on every couple of seconds, a reload of one
  song played, and a fresh hidden window was refused too, with the vpn off. The flagged
  verdict covers the session, and the songs a playlist fetches in the page are where it
  shows. What answers it is below.
- uBlock Origin Lite runs inside the engine (`Settings.UBlockLite`, on by default,
  `Blocking/UBlockLite.cs`), for the YouTube refusal above. uBlock's current YouTube rules
  (its "Quick fixes" list) do not fight the flag, they avoid it: they rewrite the player's
  request so YouTube serves it as a kind of playback that carries no ads, ask again in
  another such form when a video comes back refused, rename the ad fields in the answers
  rather than delete them (deleting hung the player here), and quiet YouTube's tamper
  checks. They change every few days and are built into each uBO Lite release, so Gergur
  takes each release rather than copying them.
  - WebView2 loads it as a browser extension (`AreBrowserExtensionsEnabled`, then
    `CoreWebView2Profile.AddBrowserExtensionAsync` on the unpacked folder). It lives at
    `extensions\ubol\current` in the data folder, always that path, so the engine keeps its
    id and settings; the engine also keeps it registered in the profile between runs. It is
    loaded before the first page of a run (`EnsureLoadedOnceAsync`, in `CreateWebViewAsync`,
    bounded at 10 s).
  - Kept current from github.com/uBlockOrigin/uBOL-home: the latest release's tag is read
    from GitHub's API a minute after start (3 s when there is none yet) and every six hours,
    and the download address is built from the tag (`DownloadUrl`), never taken from the
    answer. The archive is unpacked only inside its folder (bounded in entries and size), and
    only a manifest that is uBO Lite's at that version counts; a folder is used only once its
    `.gergur-complete` marker is written. The first download goes into `current` and is
    loaded at once (engine calls on the UI thread, `AppSession.StartUBlockLite`), and tabs
    already open switch over from their next page. Later ones wait in `pending` and are
    swapped in at the next start before the engine starts (`PromotePending`), since a loaded
    extension's files are in use. The status line says when one arrived. Releases are not
    signed: this is the same trust as installing it from that page by hand, over HTTPS. Its
    requests go direct, not through the vpn, like the blocklist download.
  - While it is loaded, `adblock.js` does nothing at all on YouTube (`UBOL_ACTIVE`, written
    in by `PageCleanup.Compose`): no hiding, no pruning, no 16x tick. Two hands on the same
    player fight, and the 16x run-out of ads is a likely reason YouTube flagged this browser.
  - The setting needs a restart and is refused over the API (`DialogOnly`): an extension in
    every page of every session is the person's to turn off.
  - uBO Lite has no toolbar here: its popup and dashboard are not reachable, so it runs with
    its defaults.
  - Its network rules (`declarativeNetRequest`) block nothing in WebView2, measured: with
    Gergur's own blocklist and page cleanup off, a page's requests to EasyList and
    EasyPrivacy staples (adsbygoogle.js, analytics.js, gpt.js, ad_status.js) all went
    through, and the same with Gergur's request interception taken out entirely. Its page
    scripts do run. Gergur's blocklist goes on blocking hosts as before.

  Verified on 2026-11-03 in a test instance (`scratchpad/livetest/ubol-live.ps1`,
  `ubol-update.ps1`, Gergur's own blocking off so what is blocked is uBO Lite's, signed out,
  hidden muted agent windows, no window on screen): downloaded about 5 s after a fresh start
  and loaded while running; seven videos and three playlist songs played with no ad and no
  refusal; the player's response held ad placements on every one of those 11 with it off and
  on none with it on; an older installed copy loaded, the latest downloaded into pending while
  it ran, and swapped in and loaded at the next start. Not verified: the person's flagged
  session with it (it needs their profile), and that the engine reads the swapped files
  rather than a copy of its own (it reports no version).
- Settings are a page now (`gergur://settings`, `SettingsPage` behind it, the menu's
  Settings): every setting described from the attributes on `Settings`, in sections with
  the engine last, each change saved as it is made (`ISettingsHost.Commit`: save, then
  `ApplyLiveSettings`), refusals said under the field (ranges including ports 1024 to
  65535, ports that clash, the search template, a vpn profile that does not exist), and a
  banner naming what waits for a restart, measured against `AppSession.StartedWith`, with
  Restart now. The modal `SettingsForm` is gone. This page is where the API's refused
  settings (`SettingsPatch.DialogOnly`) are changed. The phone drop switch goes through
  `TogglePhoneBridge`, and the pairing key never reaches the page: New key replaces it
  (put back if it cannot be saved) and Show pairing link opens the browser's own window.
  Verified: SettingsPageTests against a fake host, and the page driven in headless Edge
  in demo mode (`settings.html?demo=1`, bridge.js answers the settings ops from sample
  data). Not yet opened inside the browser itself as of writing.
- YouTube stops starting playback through the WARP exit once it has seen enough
  traffic from it, and it looks exactly like a broken ad blocker: permanent buffering,
  no error. Turn the VPN off before suspecting anything else.
- Gmail is given no service worker (`SiteWorkarounds`). Its worker, once it controls a page
  here, stops answering: every request the page makes waits out the engine's one minute
  worker start limit and fails ("Not connected", blank account picture, a page that sits
  in `interactive` and never loads). Measured 2026-11-02 on the real profile: the same
  fetches from any other tab took 100 to 350 ms; with the registration removed the inbox
  loaded complete in about six seconds; Gmail then registered a fresh worker on that load
  and the next load hung again. So wiping the Service Worker folder only cures it for a
  few days, which is what happened twice. x.com, Notion, Outlook and YouTube workers in the
  same profile are fine. Why Gmail's cannot start was not found (not the size of the store,
  not storage, not the network). `mail.google.com` pages get a script that unregisters
  workers and deletes `navigator.serviceWorker`, and the first run clears Gmail's
  registration once, and no other site's (`Storage.clearDataForOrigin` for
  `https://mail.google.com`, `service_workers` only; `service-workers-reset` marker in the
  data folder). Not yet confirmed in the real browser with the shipped build as of writing:
  what was measured live is Gmail loading with its registration removed by hand. A symptom that
  looks like this on another site: `navigator.serviceWorker.controller` is set and every
  fetch from that page hangs while the same fetch from another tab is instant.
- The tab strip is the title bar. The system one is removed by `WM_NCCALCSIZE` in
  `MainForm.WndProc` (the client area starts at the window's top edge; a maximised window
  is pushed back on screen by the frame thickness, `WindowFrame.ClientTop`), and the strip
  draws minimise, maximise and close itself. Dragging, double clicking and the top
  resize edge are answered through `WM_NCHITTEST`: the strip returns `HTTRANSPARENT` for
  bare strip and the top few pixels so the window decides (`MainForm.FrameHit`).
  Windows 10 here, so there is no snap layout flyout on the maximise button. Taking the
  title bar out of the client area breaks WinForms' memory of the window's size, and this
  was only found by running a real window (a throwaway form on a hidden desktop, whose
  geometry was measured; nothing in the code reads wrong): WinForms keeps a restore size as a
  client size and converts it back by adding the frame a window with a title bar would have,
  so every maximise and restore, and every fullscreen page and back, made the window one
  title bar taller (31px at 96 dpi, adding up). `MainForm.WndProc` handles
  `WM_WINDOWPOSCHANGED` and puts the window back where the system restored it
  (`WindowFrame.IsComingBack`, which is tracked rather than read off the form, because a
  form told to restore already says it has), and `SetPageFullScreen` gives back the bounds
  it took the window from. The window buttons do not `ClaimForPerson`, as the system ones
  never did: a claimed agent window's tabs are saved as the person's session, and closing
  one that was claimed would write the agent's tabs over theirs.
- Page fullscreen (a video) takes the whole window, chrome included, and was a trap: Esc
  only worked if the page had the keyboard, and a page leaving fullscreen while another tab
  was in front was ignored, so the window stayed without its strip and toolbar. Script run
  through the engine counts as the person's click for a fullscreen request, so one
  agent `/click` on a player's fullscreen button turned an agent's own window borderless and
  maximised across the whole monitor (reproduced 2026-11-02). Now: `SyncPageFullScreen`
  works it out from the tab in front on every change; Esc and F11 leave it from anywhere
  (`ExitPageFullScreen`); an agent's window never goes fullscreen, and neither does a page an
  agent ran script in during the last ten seconds (`Tab.NoteAgentAction`). Those reasons are
  judged once, when the page asks, and the answer is kept on the tab (`FullScreenVerdict`,
  `FullScreenPolicy.Judge`) until the page leaves fullscreen: judged again on every redraw,
  a refusal lapsed with the ten seconds and the window took the screen late, the same bug
  delayed. A person's genuine fullscreen is refused if an agent used that tab in the ten
  seconds before; the page then sits "fullscreen" inside the window's own area and Esc and
  asking again later work as usual. Leaving fullscreen from a maximised window was measured
  to return to exactly the same geometry, so it does not go through Normal first. A refusal
  in the person's own window says so in the status line. Tested: the policy as a pure
  function over whole histories; the MainForm wiring around it (and the restore hook) has
  only source checks, and the window behaviour was measured on the throwaway form, not on
  MainForm itself.

Agent actions are visualized: /click, /type, /key (with a selector), /hover, /select and
/upload animate a blue cursor to the target, ripple, and flash the element, so the user can
watch the agent work. Each returns after the animation and the action (~1s).

### Acting like a person

/click, /type, /key, /hover and /scroll send real input through the DevTools protocol
(`Input.dispatchMouseEvent`, `Input.insertText`, `Input.dispatchKeyEvent`; `Tabs/TabInput.cs`,
`Tabs/TrustedInput.cs`), so the page sees `isTrusted: true` events. Sites that ignore
script clicks (a "Submit" that checks isTrusted, a file chooser, a popup) now work.

- The element is found, scrolled to the middle of the view and measured in the page
  (`AgentServer.LocateJs`), then measured again after the cursor's glide in case it moved.
- Covered: when something else is on top at its centre (`elementFromPoint`), the click is
  NOT sent at that point, which would click the cover (a cookie banner's "Accept", say).
  The element's own `click()` is called instead and the answer names the cover
  (`covered: "div#consent"`, `trusted: false`). A label for the element counts as the element.
- A click that fails before the button went down falls back to `click()`. One that fails
  after (`MayHaveLanded`) is never retried: a second click on "Pay" is worse than none.
  Each protocol call has a 5 second budget (`TabInput.CallBudget`), and a whole action
  (click with its chooser watch, upload, a run of keys) 8 seconds between them
  (`TabInput.ActionBudget`). A press that failed after the button went down sends the
  release, so the button is not left held. Typing that timed out is treated the same way
  as a click that may have landed: not set again.
- Clicks and uploads in one tab take turns (`TabInput.TakeTurnAsync`): interception is
  switched on and off per action, and overlapping ones would switch it off under each other.
- The own-page check is made before the engine is touched and again directly before the
  message that acts, with nothing awaited between: the chooser watch awaits, and the tab
  may have reached gergur://settings meanwhile.
- A file chooser a click opens is intercepted and closed unseen, and the answer says to
  use /upload. A native dialog from a hidden window would land on the person's screen.
- /upload with a file input sets its files (`DOM.setFileInputFiles`), hidden inputs
  included. With anything else it clicks it and answers the chooser that opens. Paths must
  be absolute, local and existing, at most 100 (`TrustedInput.CheckFiles`); nothing else is
  restricted, as the person asked.
- Page dialogs (alert, confirm, prompt, "leave this page?") in an agent's window are
  answered by the tab itself (`AreDefaultScriptDialogsEnabled = false`, set before the first
  navigation) and kept for /dialogs, the last 10. Confirm and prompt follow the tab's policy
  (OK by default; prompt gets `text` or its default); alert and leave-page are always OK.
  In the person's windows dialogs show as ever. A tab moved into the person's window, or an
  agent window that becomes theirs, gets the engine's dialogs back at its next navigation
  (`Tab.RefreshDialogMode`); until then a dialog is asked of the person in a plain box.
- /click and /key report tabs the page opened (`opened`, by Opener) and dialogs answered
  (`dialogs`, by `Tab.DialogsSince`).
- /downloads lists only downloads put down to the caller (`DownloadItem.AgentKey`, set by
  `AgentServer.DownloadOwner`): started in its own window, or in a tab it acted in (click, type, key,
  hover, scroll, select, upload, eval, navigate; by id, index or none) within the last 30
  seconds (`AgentServer.Acting`, `Tab.NoteDrivenBy`). Reads never count. The person's
  are never listed, a download in a tab an agent once touched included.
- The person's log (agents panel and page) says "Typed in t3", "Pressed a key", "Uploaded
  files": never the text, keys or file names.

Verified live on 2026-11-03 (`scratchpad/livetest/sprint2.ps1`, 55 checks, a hidden agent
window on a local test page): trusted click/input/keydown/mouseover seen by the page,
uploads through a visible input, a hidden one and a chooser button, no dialog window ever
created, confirm cancelled and accepted, prompt answered, popup reported, covered element
not clicked through, wheel/top/bottom/into-view scrolling, waits met and timed out, the
agent's own download listed, focus never moved and the window stayed cloaked. Not verified
live: the person-side dialog fallback (`AskPersonInstead`, `PromptBox`), input in a
background tab of an agent window, and a tab zoomed away from 100%: coordinates come from
`getBoundingClientRect` and go to `Input.dispatchMouseEvent` unscaled, which is right if the
protocol takes CSS pixels at that zoom and wrong if not. Agent windows open at 100%.

### Asking the person

`gergur_ask_person` (POST /ask) is for what only the person can do: a code from their
phone, a captcha, a login, a choice. The question (one printable line, at most 300
characters, `AgentAsks.Clean`) appears in a bar across every one of the person's windows,
never an agent's, in the agents' blue: who asks, the question, a reply box, Done, Can't,
"Show its window" when the agent has a tab open, and how many more are waiting. A new
question flashes the window's taskbar button until it comes to the front; nothing is
brought forward. The call waits up to its timeout (18 s over MCP) and answers
`{id, answered: false}`; calling again with the id keeps waiting. The answer is
`{answered: true, done, reply}`, handed over once. At most 10 wait at a time; the same
question asked again while it waits is the same question; everything is forgotten after an
hour. An id is only answered to the agent that asked. Verified live: asked, answered in the
bar through UI automation, the reply reached the agent, the bar went away, focus did not move.

### Saved passwords

The person asked for a vault: offered on every login, kept only if they say yes, and usable by
every agent without the agent ever seeing a password. The engine already is one, so Gergur
does not keep passwords of its own:

- Saving is the engine's (`SavePasswords`, on by default): after a sign-in in the person's
  window it shows "Save your password?" with Save and Not now, and keeps what they save
  encrypted in the profile. Agent windows never offer to save
  (`BrowserEnvironment.SavesPasswords`, `IsPasswordAutosaveEnabled` off there, set when the
  view is built, when settings change and in `Tab.RefreshDialogMode` when a tab moves or the
  window is claimed): what an agent types is not the person choosing to keep it.
- Filling is the engine's too, and reaches agent windows, which share the profile: a saved
  login fills in as its sign-in page loads, the most recently used account when there are
  several.
- `gergur_login` (POST /login) looks at the first password field shown in the document and
  sends its form: a real click on its submit button, or Enter in the field. It answers the
  username before it, how it sent it (`via`), whether the same field is still asking
  afterwards (`stillAsking`: a wrong password, a captcha, or a page that signs in without
  leaving) and where the page went, never the password (`LoginStateJs` reads only its
  length). Where it stops is `AgentServer.LoginStop`: no field (or one only in a shadow
  root, or a frame inside a frame, which it cannot click into, said as such), an empty field (no saved login), more
  than one password field shown (sign-up, change password), or a page an agent scripted.
  `submit: false` only looks, and is the one answer given on a scripted page.
- A scripted page (`Tab.AgentScriptedThisDocument`): /eval marks the tab as it starts and
  as it ends, and counts itself running in between; a new document (`ContentLoading`) is
  marked as it begins; a Back or Forward in a tab an agent has scripted counts as scripted
  until a page loads afresh, since the back-forward cache brings back the old document.
  Marks come from one counter, not the clock. /login looks again directly before the click
  and before Enter, since such a script could have pointed the form at another site.
- /eval is refused (409) on a page holding a password (`PasswordStateJs`), and looked at
  again after the script: if one is there by then (the page moved on, or drew a form the
  engine filled), the script's answer is kept back (409, the script did run). A field
  counts as a password when it is one, or was one and a "show password" switch made it
  text (a typed field, not a checkbox or button, whose autocomplete says current-password or
  new-password or whose name or id contains password,
  passwd or pwd or being just pass, or /login's mark), in the document, open shadow roots and frames the page
  can reach. A page that throws counts as one.
- /screenshot refuses (409) a page with a password shown as plain text; dots are fine.

Measured on WebView2 156 with a scratch profile and a local login page (a throwaway exe in
the session scratchpad, not in the repo): the save prompt after a sign-in, Save storing it,
the fields filled on the next load before any input, a view with saving off still filled
and never prompting, two accounts filling the most recent. The page scripts run in the
test suite against a stand-in page under node (`LoginScripts.harness.js`, from
`SavedLoginsTests`). Not airtight: a script can keep
what it reads somewhere (a global, storage) and hand it over in a later call once the field
is gone, or wait for the engine to fill a form, read it and empty the field before it
returns, so the look afterwards finds nothing; a page scripted through another tab the agent scripted (a window.open handle) is
not caught; a "show password" field not named or marked as one is not recognised, and it
would then show in a screenshot. Saved passwords cannot be listed or removed one by one
from Gergur; WebView2 has no API for it, only clearing them all.

#### Choosing the account, and two-step sign-ins

`gergur_login` takes `account` (a username, 1 to 200 characters, no control characters,
`AgentServer.CleanAccount`). What it does is decided from the page alone
(`AgentServer.WantedAccount`, `PlanPick`):

- One account wanted and already filled in: used as it is, no keyboard.
- A password page with no username field (two-step, or a bare password form): the account
  is taken from `account`, else from the page itself (`pageUser`: a hidden or plain input
  before the field marked as a username or email, by autocomplete, type or a name like
  `email` or `identifier`). Gergur adds a username field of its own before the password
  field (`AddPickFieldJs`), which makes the engine fill the most recent saved account
  passively; if that is the one wanted, done, with no keyboard. The field is always removed
  before anything is sent.
- Otherwise (another account wanted than the one filled, or the passive fill was another
  one): the account is chosen from the engine's own list, the way a person does it
  (`Tabs/AccountPick.cs`).

The engine shows its list only to a page with the real Win32 keyboard focus (focus emulation
over the protocol does not do it), and all Gergur windows share one UI thread. So a pick
borrows this app's keyboard for one to two seconds. The person agreed to that while they are
in Gergur; it is never taken from another app. The guards, each measured:

- Only in an agent's own window (`OpenedByAgent`), never the person's (409), and one pick at
  a time app wide (`AccountPick.Turn`, 503 when busy).
- A hidden window is moved wholly left of every screen first (`AccountPick.Parked`) and put
  back after. The list is a top-level window of the engine's browser process, owned by the
  hidden window and so cloaked with it (inherited), and drawn beside the field.
- If giving the page the focus brings Gergur in front of another app (`TookTheFront`;
  Windows allows it after a while without input, and to a process the front app started),
  the pick stops before any key (503 `PickInterrupted`). Measured before this check existed:
  the person's typing in another app went into the test page.
- No key goes in unless the list is there (`ListShown`: a shown window of the engine's
  browser process whose root owner is the pick's window, beside it, so a popup of the
  person's own window is never taken for it; `NoList` otherwise, and the answer is
  `AccountNotSaved` with nothing sent). The page's `submit` is held off while keys go in
  (`GuardJs`), not its keydown: holding keydown off held the list's choice off too. Without
  the list, Enter in the field sent the form.
- Typing the account narrows the list to it; ArrowDown, then Enter as a `rawKeyDown` with no
  text (`ListEnter`). The usual Enter carries "\r", goes as a keyDown, and the list passed it
  to the page. The list sometimes ignores an Enter that comes soon after it narrows (still up
  1.5 s later in 3 picks of 15), so Enter waits 400 ms and is pressed again while the list is
  still up and nothing was chosen (`EnterAttempts`), never with the list gone.
- The account is read back from the page while the focus is held (`PickStateJs`, the
  username and only the password's length). A pick that did not come out as the account
  asked answers `AccountNotSaved` and empties the password field.
- The whole pick is waited for at most `PickBudget` (15 s, 503 `PickTimedOut`), so a window
  closing under it cannot hold the turn for good. Whatever ends it (refused, failed, thrown),
  one `finally` empties the password unless the wanted account came out, and takes Gergur's
  added field away, each best effort and logged.
- After: given back first, with nothing awaited before it, so a page script that throws
  cannot skip it. This thread's active window and focus are given back, and the foreground only when
  the pick's own window (or something it owns) holds it (`GiveFrontBack`). The person moving
  to another app mid-pick is theirs: in the live test they went from Gergur to a console
  during a pick, and putting the front back would have pulled them out of it.
- The hidden window activating for it never becomes the window requests go to
  (`MainForm.OnActivated`).

Verified live on 2026-11-03 (`scratchpad/livetest/pick-live.ps1`, 15 checks, a test instance
on a seeded scratch profile with two made-up accounts, local pages): a second account chosen
from the list and signed in with its own password, the account already filled used without
the list, the two-step password page filled for the account it carries, a bare password page
filled passively with the most recent and with `account` for the other, a never-saved account
refused with nothing sent, refused in the person's window, no engine window ever drawn on a
screen, the test instance never holding the foreground, and the window back in place and
hidden. 15 of 15 in the last six runs, the last three after the review fixes, with the list traced as owned by the pick's window. Not verified live: a pick borrowing the keyboard of
a person who is in Gergur at that moment (the test instance is a separate process, so the
person was always in another app); a list with many accounts; a site's own script-driven
username field.

#### Sign-in forms in a frame

A page whose sign-in form sits in a frame (a sign-in provider's, say) is signed in to by
`gergur_login` as well (`FrameLoginAsync` in `AgentServer`). Only when the page itself has
no password field: the frames the tab knows of (`Tab.FrameCreated`, direct children only, at
most 20 read) are each asked with `FrameLoginStateJs`, and only one may hold a password field
(two: 409 `SeveralFramesAsk`). A frame inside a frame is not reached.

- The engine never fills a saved password into another site's frame by itself (measured: the
  same saved login fills when the provider's page is opened on its own). It does once an
  account is chosen from its list, so the frame's sign-in is the page's account pick
  (`AccountPick.RunAsync` with the frame's own script runner, `inDocument`), held to the same
  rules: an agent's own window only, one at a time, keyboard for about a second, nothing kept
  unless the account wanted came out, the password emptied otherwise. With no `account` it
  types nothing and takes the first entry the browser lists, which is not always the most
  recently used (measured: it was the other one). `submit: false` on an unfilled frame says
  so (`FrameFillsOnChoice`) and touches nothing.
- Where to click is found in the page, since the DevTools frame tree of the page leaves out
  another site's frame (measured): the one frame whose `src` is on the frame's own origin,
  and only if its content box is the frame's own view size within 2 px (`FrameBoxJs`,
  `SameFrame`), and not drawn scaled, turned or skewed by itself or anything around it (its
  drawn and laid-out sizes agree within a pixel), since a point in the frame would then be
  another point on the page and a click could reach another control of the provider's
  form; otherwise `FrameNotPlaced`. The point is the box's corner plus the element's
  centre in the frame, and must lie inside both (`PointInFrame`).
- Right before each click, on the UI thread with nothing awaited between (`LastLookAsync`):
  the page's own hit test at that point must be the frame (covered: `FrameNotReachable`),
  the page not scripted by an agent, the address unchanged and the frame still there
  (`FrameMovedOn`). Sent by a click on its button, or with none, a click in the password
  field, Escape (for the list) and Enter. Keys go wherever the keyboard is, so before each
  of the two (`KeyboardStillThere`, on the UI thread, one block for both) the page's focused
  element must be a frame on that site (`FrameFocusedJs`), the frame's the field clicked
  (`FocusedJs`; not `document.hasFocus()`, which an agent's hidden window never has), the
  page unscripted and unchanged. The answer adds `frame` (the frame's origin).

Verified on 2026-11-03 in a test instance with two made-up saved accounts for a local
provider in a frame of another local site (`scratchpad/livetest/frame-live.ps1`, 11 checks,
hidden agent window): only looking sends nothing; a named account chosen, signed in with its
own password; an account not saved refused with nothing sent; none named takes the first
listed, in a frame far down the page; Enter with no button; two frames refused; a covered
frame not clicked; a scripted page refused; a page with no form said so; no window of the
instance on screen at any point. Run again, 11 of 11, after the focus check before Enter and
the scaled-frame refusal were added. Neither refusal has been seen happen live (no test page
moves the focus or scales its frame); they rest on the unit tests. Not verified: a real provider's frame, and a frame whose
`src` redirected to another site before the form (it is then not found, and refused).

#### A test instance

`GERGUR_DATA_DIR` moves the whole data folder (settings, profile, logs) and gives the
instance its own single-instance mutex and pipe (`Settings.InstanceSuffix`, a hash of the
folder), so a second Gergur runs beside the person's own without touching their profile,
vault or settings. Use it for anything that needs saved logins: seed the scratch profile,
never the real vault. Launch it through WMI (`Win32_Process.Create`), not as a child of the
terminal: a child of the front app may take the foreground. With `GERGUR_TEST_GOOGLE_ORIGIN`
set as well (a loopback or `*.localhost` origin only, `GoogleSignIn.OriginFrom`), a local
stand-in takes Google's sign-in's place, so the Google account path runs without Google.

#### Sign in with Google

The person signs in to most sites through Google, and all that takes is clicking the right
Google account, so that is remembered per site and clicked for agents (`App/GoogleSignIn.cs`,
`Data/GoogleAccountStore.cs`):

- Learned from the person's own sign-ins, and from an account an agent was told to use
  (below). A script on Google's sign-in pages
  (`GoogleSignIn.ChoiceScript`, top page of `https://accounts.google.com` only) posts the
  address of an account entry that is clicked (or chosen with Enter or Space); the tab takes
  it only from that origin (`e.Source`), and the window keeps it only when it is one of the
  person's windows and no agent acted in that tab, or in the tab that opened it (a sign-in
  popup), within 30 s, nor ran script in the page shown at all (`GoogleSignIn.IsThePersonsChoice`,
  `AgentScriptedThisDocument`): a script can post the choice or click an entry long after it
  ran. An agent's trusted click looks exactly like the person's to the page, so who was there
  decides, never the event (`GoogleSignIn.ChoiceFrom` checks only where the message came from).
- The site is read from the sign-in's own address (`GoogleSignIn.SiteOf`): `redirect_uri`,
  `origin`, `app_domain`, followed through `continue` and `followup`, an older popup's
  `storagerelay://`. Google's own services are not a site. One account per site, the latest.
- An account an agent named in `account` and signed in with is remembered for the site too,
  so the person can say "sign in with name@gmail.com" once (`RememberNamedAsync`): only once
  the sign-in is past Google (`stillOnGoogle` false, the site known), marked with the agent's
  name (`GoogleAccount.NamedBy`, `GoogleAccountStore.CleanName`), said in the agents log
  ("Remembered which Google account to use on example.com") and in the answer
  (`rememberedNow`). The settings show "Named to X, an agent". The same account named again
  changes nothing; the person picking it themselves makes it theirs (NamedBy cleared), and
  their pick of another replaces it. Latest wins both ways: an agent named another account
  replaces the person's own pick for that site, as asked for ("sign in with name@gmail.com
  and remember it"), and the settings and the agents log say which agent did.
- Kept in `google-accounts.json` in the data folder, never in the settings, so it never
  reaches `GET /settings`. The settings page lists it (Google accounts, the last section)
  with Forget and Forget all (`settings.forgetGoogleAccount(s)`). A file that cannot be
  read is never written over, as for bookmarks.
- `gergur_login` on Google's sign-in page clicks the account named in `account`, else the
  one remembered for the site, one trusted click on the entry Gergur marked
  (`GoogleSignIn.MarkJs`, which answers only whether it is listed and how many are, never
  the addresses). Nothing is clicked when no account is remembered (`NoGoogleAccountRemembered`),
  the site cannot be told, the account is not listed, the page was scripted by an agent, or
  it moved on between measuring the entry and the click (`GoogleMovedOn`).
  It never clicks Google's consent screen or anything after the account: `stillOnGoogle`
  and a note say to ask the person, and called on a consent page (`GoogleSignIn.IsConsentPage`,
  "consent" in the path) it clicks nothing at all, an account chip there included. `submit:
  false` only says which account it would click.
- FedCM, in an agent's window only (`Tabs/FedCmWatch.cs`). The "Sign in with Google" button
  and One Tap use FedCM since August 2025: the chooser is drawn by the browser, not a page
  (see `DisableFedCm`). In a tab of an agent's window the DevTools `FedCm` domain is enabled
  (`Tab.RefreshFedCmAsync`, on each view and each move between windows; stopped when the tab
  moves to the person's window, and never started there), and the chooser it reports is
  answered with `FedCm.selectAccount`. `gergur_login` with a chooser up chooses the account
  named, else the one remembered for the page's own site (`GoogleSignIn.SiteOfPage`), only
  when it is Google's (its config URL on Google's origin), listed, and already used with the
  site (`loginState` "SignIn"): a first sign-in ("SignUp") shares the person's details with
  the site and is theirs to agree to (`FedCmFirstSignIn`). Not on a page an agent scripted,
  and only in the chooser read, still up, over the same page, still this tab's watch: all
  looked at on the UI thread with nothing awaited before `selectAccount` (`FedCmLook`). Nothing
  else in the chooser is ever pressed. The chooser takes the call only when it is the sign-in
  meant (`FedCmWatch.Applies`: it offers the account named or remembered, or it is the quiet
  return); otherwise the page is signed in to as usual, since One Tap can sit over a password
  form, and only when the page has no password field is the chooser's answer given (no
  account known for the site, the named one not offered). Remembered as named once the
  chooser closed after the choice: the browser had Google's token for the site by then;
  whether the site then accepted it is not known. The browser's own quiet return (`AutoReauthn`: one account
  used here before, signed back in by itself, as for the person) is waited for and said
  (`via: "browser"`), never answered or remembered; with a site that allows it, it can
  sign in with the account used last even when an agent named another, and Gergur cannot
  stop that: then the answer is `ok: false` with `FedCmQuietReturnOther`. The person's own FedCM choices are not learned: their windows are not watched.
- A page of any site can send the person to a Google sign-in naming another site in its
  `redirect_uri`, and if they pick an account there it is remembered for that site. Google
  refuses redirects a site has not registered, so such a sign-in goes nowhere, and an agent
  can name any account anyway; worth knowing, not worth more.

Google's real pages were never tried: the entries are recognised by `data-identifier` (the
account chooser) and `data-email`, as far as is known. If Google names them otherwise,
nothing is remembered and agents are told the account is not listed; nothing is clicked by
guess. Verified on 2026-11-03 against a stand-in (`scratchpad/livetest/google-live.ps1`, 15
checks, test instance, made-up accounts): the person's own choice remembered for the site,
an agent's click in the person's window not remembered, nor a click an agent's script
scheduled for after the 30 s, the remembered account clicked in a
hidden agent window and the site signed in with it, a named account clicked instead (then
remembered, as below), nothing clicked with no account remembered, an account not listed or a chooser
not naming its site, the consent screen left alone (its Continue never pressed, nothing
clicked when called on it), the window
hidden throughout. The scripts run in the test suite against a stand-in page
(`GoogleScripts.harness.js`); the settings section was looked at in headless Edge in demo mode.
Agent-named accounts, verified the same day in the same test (now 17 checks): a named account
remembered for the site with the agent's name, named again changing nothing, the person's own
pick in their window replacing it. FedCM, verified the same day against a stand-in identity
provider (`scratchpad/livetest/fedcm-live.ps1`, 14 checks, test instance): the chooser seen in
a hidden agent window, a chooser over a password form leaving the call to the form (and taken
when its account was named), nothing chosen with no account remembered, for an account new to the
site, or one not offered, a named account chosen and the site signed in with it, remembered
as named by the agent, only that one account ever asserted, the remembered one chosen next
time unnamed, nothing on a scripted page, and no window of the test instance on screen at any
point (every visible, uncloaked, unminimised window of it and its engine processes polled
every 50 ms). Not seen live: the quiet return's answer (the browser finished it within
milliseconds, before a call could arrive), nor any of it against Google.

### Agent watching

When an agent reads or acts on a tab in one of the person's own windows (asked to look at
what they have open, say), a soft edge in the agents' blue lights around that window's page
with a pill at the top: "Agent watching · Claude Code", or "Agent watching another tab" when
the tab is not the one shown (`UI/AgentWatchGlow.cs`). It stays 4 s after the agent's last
touch and fades in about 0.3 s. Agents' own windows have their band instead and never show
it, nor does a hidden or minimised window. Every endpoint that names a tab goes through
`AgentServer.TargetEntry`, which is the one place it is raised (`MainForm.AgentLookedIn`,
posted to the UI thread, never waited on).

It is a window of its own over the page, owned by the person's window (the page is the
engine's window, which nothing of the form can draw over, and taking pixels from it would
reflow the page): layered with per-pixel alpha (`UpdateLayeredWindow`), transparent to the
mouse (`WS_EX_TRANSPARENT`, `HTTRANSPARENT`), never activated (`WS_EX_NOACTIVATE`,
`ShowWithoutActivation`, `MA_NOACTIVATE`), off the taskbar and Alt+Tab
(`WS_EX_TOOLWINDOW`). It follows the window as it moves and resizes and goes when it is
minimised.

Verified on 2026-11-03: the real glow driven as a real window on a hidden desktop (26
checks: never activated, focus kept, clicks on its edge and middle reach the page under it,
owned, follows a move and a resize, held then faded by its own timer, gone on minimise, the
pixels of the edge and the pill), and end to end in a test instance (a read of a tab in a
person-style window brought it up, the foreground never moved, it was gone after the hold,
an agent window showed nothing). That end-to-end run put its test window and glow on the
person's screen for about 10 s, without focus: `SetWindowPlacement` pulls a window placed
off every monitor back onto one. Not done again in that form. Not yet seen by the person
in their own Gergur.

`initialize`'s `instructions` also tell agents that Sign in with Google is a click on the
site's Google button followed by `gergur_login`, with `account` remembered for the site.

### Saying what you are doing

The person sees what each agent says it is doing, beside its name: on the agents panel,
the agents page, and the band across its window. Over MCP every tool but
`gergur_set_task`, `gergur_list_tabs` and `gergur_read_settings` is refused until the
session has called `gergur_set_task` with a few words ("Uploading the 2.4 build to App
Store Connect"); `initialize` says so in its `instructions`. Call it again when the task
changes. Over plain HTTP send `POST /task` or an `X-Gergur-Task` header on any request;
nothing is refused without it. It is kept as one printable line of at most 100 characters
(`AgentActivity.CleanTask`), in memory only.

This is a nudge, not a boundary: an agent can skip it by using the HTTP API, and agents
that share a key share a task (every MCP client that sent no session id is "mcp:unnamed",
and API callers with the same `X-Gergur-Agent` name are one agent).

### Now playing

While a tab in any of the person's windows plays sound, a note shows on the toolbar nearest
the address bar (lit while heard, its tooltip "Now playing: ..." or "Paused: ..."), and a
click drops a panel with the song, previous, play or pause, next, a volume slider with a mute
button, and below them Go to tab and Hide tab (`UI/NowPlayingFlyout.cs`). It drives that tab from whichever tab or window is in front.
Which tab (`App/NowPlaying.cs`, checked once a second by each window): the one making sound,
of two the one that began last; once nothing is heard, the last one chosen stays chosen for
30 minutes while it is on the site it played on, so a paused song can be played again from
the panel; a hidden tab (below) stays chosen for as long as it is open. Agents' windows are
never asked and never show the note.

Hide tab takes the tab off the strip and leaves its controls: the header then says "Now
playing, tab hidden", and the row below reads Show tab (back on the strip, in front) and Close
tab. `TabManager.TuckAwayAsync` moves it to a list beside the strip's, so it is not in the strip,
Ctrl+Tab or agents' tab lists, and its window closing closes it. `AllTabs` is what session
saving, settings and the note go through: after a restart it is an ordinary tab again. Hiding
the only tab, or closing the last one left on the strip, leaves a new tab rather than closing
the window under the music. With nothing making sound the control goes to a hidden tab once a
paused one on the strip has been quiet for 5 minutes (not 30), so a song paused from the panel
can be played again from it and the hidden one is soon back in reach; with two hidden, closing
one brings up the other at once, and showing one does once it has been quiet that long. Hide and Close act only on the tab the panel describes: a
tab that begins to play takes over the control a moment before the panel can say so. Closing
the window ends a hidden tab like its other tabs, without asking.

- The buttons are the site's own. A site hands Windows its play, pause, next and previous
  through the Media Session API (`setActionHandler`), which a page cannot be asked for
  afterwards, so `MediaControl.HookScript` notes each one at document creation and the
  panel presses that one. Without a handler it plays and pauses the page's `<audio>` and
  `<video>` directly, and on YouTube presses the player's own Next and Previous: YouTube
  hands "next" over on a schedule of its own, and a press a few seconds into a playlist
  found none (measured). The song's name is the one the site gives Windows
  (`mediaSession.metadata`), else the tab's title.
- The scripts run in the page's own world, where the site's handlers live, so the page can
  lie about what it plays; that costs a wrong title. Its texts are cut to one clean line
  (`MediaControl.Clean`), nothing it says is opened or run, and a page that claims a next
  without having media is not believed. A player in a frame from another site is found by
  asking the page's frames (`Tab.ReadMediaAsync`). Reading never wakes a parked tab and
  never asks one of the browser's own pages.
- Media keys need nothing from Gergur: WebView2 already registers each page's media session
  with Windows, measured on 2026-11-04 (Windows listed the test page's song as
  `msedgewebview2.exe` with next available, its play and pause reached the page's own
  handler, and a real play/pause key reached it with another app in front). Windows names it
  `msedgewebview2.exe` in its media overlay, not Gergur.
- A read that does not come back in time (a busy page) leaves the panel on the page's last
  good answer for that tab, for 5 s (`MainForm.KeptState`), rather than greying its buttons
  for a second and losing a click. A next or previous the page cannot take yet is asked
  again, every 0.5 s, four times: in the first moment of a song YouTube has neither its
  handler nor its Next button (measured: hidden, no link, the film at 0 s).
- YouTube drops a next pressed while it is still changing songs, through its own handler
  too, so it does to a media key what it does to the panel: two presses 0.3 s apart moved
  the playlist on one song, measured twice. Not worked around.
- The engine reports a tab as playing only when there is signal (digital silence is not
  heard), and a muted tab still counts, so the live test plays a quiet tone in a tab opened
  muted.
- A tab not in front that is heard keeps its page up, out of sight (`Tab.StaysUpBehind`):
  while it plays, for 60 s after it stopped or a panel button was pressed, and always when
  hidden with Hide tab. Its view stays visible to the engine, moved beside the page area where
  its parent clips it, so nothing shows through when no page covers it, and it re-docks when it
  comes forward. It is hidden first and then put up, because hiding is what takes the keyboard
  off a page, and it is off the Tab key's round (`TabStop`). The cost is a background video being drawn as if it were in front, while heard.
  A page up behind cannot be suspended (`TrySuspendAsync` refuses a visible view).

#### The volume

WebView2 has no volume per tab, only mute, so the slider sets the page's own: on YouTube sites
the player's (`#movie_player`: `setVolume`, `unMute`, the same number YouTube's own slider and
its memory of it use, so it holds for the next song), elsewhere every media element in the page
(`MediaControl.VolumeScript`). The panel shows what the page reads back (`MediaState.Volume`),
except for 1.5 s after a change (`LevelHeld`), so the thumb does not jump under a hand. A click
sets it, a drag follows (a drag past the panel's edge keeps going rather than dismissing it,
`OverOwnedPopup`), the wheel moves it 5%. Values go through `MainForm.SetMediaVolumeAsync`, one at
a time with the latest winning, so a drag does not queue up calls on a busy page. A level above
nothing also takes off the tab's mute, since a slider moved on a muted tab that stays silent reads
as broken. The button beside it is the tab's own mute (`Tab.IsMuted`, the same as Ctrl+M).

The panel never takes the keyboard, so the slider has no keys and no accessible role; Ctrl+M mutes.
It cannot reach sound made only through Web Audio (an `AudioContext` with no media element), and
a site may set its own volume back. Tab mute still works on those. A hidden tab is moved the same
way, which is the point of the slider.

Verified on 2026-11-04 in a test instance on a hidden desktop (`scratchpad/volumelive`), on a real
signed-out Mix: a click at the middle sets the player to 50, a press and drag to 75 and 25, past
the left end to 0 with the panel staying up, the wheel up 5, the next song keeps the level, the
panel follows a level the page sets itself, the mute button, a level taking the tab's mute off, a
level set while the tab is hidden, and a plain `<audio>` page. The panels were photographed and looked at.
The hide-tab and now-playing harnesses were run again with the panel 38 px taller: all of the now-playing
checks but its own stale height, and all of the hide-tab checks but the pause described below.
The video element itself reads less than the player's level, since YouTube scales it for
loudness: 0.259 with the player at 50 and 0.130 at 25, on that song. Not verified: a real mouse
(window messages only), a screen at another scale, and the person's own use.

Testing this needs the tab unmuted, which is why `--mute-audio` will not do: with it the engine
reports no sound, so the note never shows. `ExtraBrowserArguments: "--disable-audio-output"` in the
test instance works: the note shows, the page's clock runs, and no audio session opens at all.
Measured with a Windows peak meter on that instance's engine processes through the whole run, which
would have ended the run on any peak: 0 sessions, peak 0.

Pressing pause sometimes does nothing, and that is not this slider. In `scratchpad/hidelive` the
press reaches the site's handler (the log says "the page did pause") and the video goes on playing:
seen in 2 of 2 runs of this build and 1 of 2 of the commit before it, at a different step each time.
Not reproduced in isolation: 24 of 24 in front on both builds, and 10 of 10 behind another tab and 10
of 10 hidden on this one. Cause not known.

#### A playlist skipping songs

Two causes measured on 2026-11-04, and neither is the 2026-11-02 one (nothing refused to play):

- A song already heard starts at its saved watch position. YouTube hands that position over
  with the song's data and the player seeks to it in its own `loadedmetadata`. At the very end
  the song "ends" within about 0.5 s and the playlist moves on: a stay of 5 to 13 s with the
  time reading 0, on the signed-in profile (one song at 154 of 155 s, another at 123 of
  124). Songs never played, and the first song of a direct navigation, start at 0, and signed
  out it does not happen. Not every fully watched song does it. `MediaControl.SongStartScript`
  sends a new song in a playlist (`list=`) back to 0 when it starts near its end (its last 30
  s or last tenth), or part way through anything YouTube files under Music; a film or talk
  resumes, and a link naming a time (`t`, `start`), the person's own seek, the song a reload
  of the page comes back to, and an ad are left alone. It is a capture
  listener that waits for the player's own handler and decides once per song, by the time the
  song plays. Not
  established: which field carries the position, and whether uBO Lite's rewrite of the player
  request plays a part (`UBlockLite` is dialog-only, so it cannot be turned off for a test on
  the real profile). `s.youtube.com`, which records the position, is not blocked.
- A page told it is hidden: YouTube's player takes no new playlist from the page when a song
  changes (Next hidden, handlers dropped, the Next link pointing at its own suggestion rather
  than the queue), and when that song ends it follows the suggestion, off the list. Signed
  out, five song endings each: tab in front 5 of 5 stayed on the queue, tab behind left the
  playlist at the second ending, and with the page kept up behind 5 of 5 stayed. Pressing the
  hidden Next left the playlist too, which is why this is not fixed in the page.
  `p.nextVideo()` from a hand-built list URL also left it.

The earlier history "index jumps" were no evidence of either: history does not record every
playlist step.

Verified on 2026-11-04 in a test instance started on a hidden Windows desktop (its real
window, toolbar and panel, clicked with window messages, nothing on the person's screen;
`scratchpad/medialive`): no note while nothing plays; the note with the tab's title once it
plays, still there with another tab in front; the panel at its laid-out size under it;
pause, play, next and previous reaching the site's own handlers; "Paused" with the song's
own name; Go to tab bringing the tab forward; a plain `<audio>` page paused and played with
next doing nothing; a player in a frame from another site paused and played; a real
YouTube Mix paused, played, and moved to the next song both through its handler and, with
the handler taken away (a song well under way), through its Next button; the note gone once
the tab left for a page with nothing playing. On a cold start YouTube reports playing before
any sound, and the note follows about 3.6 s later, when the engine hears it. The page
scripts also run under node against a stand-in page (`MediaScriptTests`): handlers noted and
pressed, a refused action never noted, a throwing handler falling through to the page's
media, YouTube's buttons on YouTube only. Not yet used by the person in their own Gergur.

Hide tab and the page kept up, verified on 2026-11-04 the same way (`scratchpad/hidelive`, a
real signed-out Mix, tab muted): Hide tab takes it off the strip, another tab comes forward
with none of the hidden page showing (photographed), the panel and note stay; the playlist
moves on by itself while hidden; pause, play and next from the panel; two songs later Show tab
finds it on the queue, in front, nothing reloaded, at full size; Close tab; the only tab and the
last strip tab leave a new tab with the window and note staying; saved with the session. On
the strip behind another tab it is up while heard, still up 30 s after a pause, hidden once the
grace is over, and up again when the panel presses play. `SongStartScript`'s built text on the
signed-in profile, in a hidden agent window, one step through the queue's link: one song went
from 158 of 158 to 0 and played on (35 s watched); by hand beforehand, another went from
154 of 155 to 0, played to its end, and moved to the right next song, against 5 s and a skip
without it. That real run was before the script learned its playlist, reload and ad rules; the
final text ran in the test instance on a real Mix behind another tab, with a stand-in moving the
next song to 286 of 287 s the way the player does: back to 0, still that song 24 s on, in the
playlist. Those rules themselves ran under node only (`TuckedAwayTabTests`, each one broken on
purpose and caught). Not verified: signed in with a page up behind, keyboard focus after leaving a
playing tab (hidden first by design, not measured) and the Tab key skipping it (`TabStop`, by
WinForms' rule), moving the last strip tab to another window with a hidden tab behind, and the
person's own use.

### What song is this?

A microphone on the toolbar, beside the phone button, drops a panel like the agents and
phone ones (`UI/SongFlyout.cs`, `MainForm.ToggleSongPanel`). Opening it starts listening: it
says "Listening..." and which site, then the song and artist, or that it does not know it,
with "Listen again" below. It listens to whatever plays in the person's windows, on any
site (TikTok, YouTube, anything with `<audio>` or `<video>`): of the tabs making sound, the
one that began last (`MainForm.PlayingTab`), never an agent's window. Nothing playing says
so at once. It names the song under talking too ("a song in the background of a video").
Identifying stays on the machine: no audio or question leaves it. The library it searches has
two sources, the songs the person plays on YouTube and YouTube Music (`Settings.LearnSongs`,
below) and a catalog it fills by itself in the background (`Settings.BuildSongLibrary`, below), so
it knows songs it has never heard here. A YouTube page that is itself a song answers at once from
its own name, without listening (`SongPage.Judge`, `SongListener.Ask.Named`); YouTube Music's
likewise. Anything else is listened to.

#### The song catalog

`Music/SongCatalog.cs` fills the library from the free 30 s previews of Deezer's and Apple's
public APIs, with no account: the same files those sites play on their own pages. The preview is
decoded in memory (`Music/PreviewAudio.cs`, NAudio over Windows' decoders, resampled to 11025 Hz
mono), fingerprinted, and thrown away: only the landmarks are kept, under `deezer:{id}` and
`apple:{id}`. A song gets both where Apple has it (`AppleMatch`, same `NameKey`), because the two
often preview different parts of a song, and sometimes different recordings: Deezer's gave nothing
on two of the 12 test songs and Apple's on another.

- Order: the artists the person has played first (`SongLibrary.LearnedArtists`, their top 50 and
  related artists), then every genre's chart, then the artists found so far (top 15 and 10
  related), at most 5000 waiting. A song every 4 s (`BetweenSongs`), first 2 minutes after start,
  charts again every 7 days, 5 minutes between looks once there is nothing to do, and backing off
  from 1 minute doubling to 60 on any failure (`Backoff`). It stops at 10,000 previews
  (`SongLibrary.MaxCatalog`), so about 5,000 songs, since most have two; they do not count against
  the 500 learned ones and never push them out. Progress is `songs\catalog.json`.
- Something counts as done only once every song it led to has been taken (`FeedingDone`). The songs
  waiting are in memory only, since a preview's address expires, so a restart reads what led to them
  again, a few requests, and passes over the songs it already has. An artist goes to the back of
  the list until then, and a played artist or chart that fails is left for an hour, so no one of them
  holds up the rest. A song lost to the network goes round again, three tries at most in a run, since one that
  never answers would otherwise keep the list from ever finishing. Fingerprints are written every 10
  songs and as the app closes (`AppSession.StopSongCatalog`, which waits at most 2 s).
- Backing off means a refusal: 429 or a server error, or 403 from an API (Apple's way of saying too
  often), or an error object from Deezer other than its 800, which is only "no data". A preview's 403
  is an address that has expired and a preview over 4 MB is passed over. One request may take a minute
  with its body.
- Requests go direct, not through the vpn, like the blocklist download, with their own client: no
  redirects followed, 1 MB of text, 4 MB of preview, and a preview only from https on the default
  port of `*.dzcdn.net` or `*.itunes.apple.com` (`IsPreviewAddress`), since the address comes from
  an answer. What leaves the machine: the names of artists the person has played, sent to Deezer
  and Apple as searches. That is why it is a setting on the settings page and refused over the API.
- `PutCatalog` adds without touching `_version`, so a held search index stays: it is built by a
  question and let go three minutes later, and the catalog adds a song every 4 s.
- A song under two ids and sources is one song: `Identify` takes the runner-up as the best match
  that is not `SongCatalog.SameSong` (title and first artist, with brackets, guests and versions
  taken out, so a live cut and the studio one count as one; `AppleMatch` pairs them by the same rule).
  Without that, Deezer's and Apple's copies of one song count as rivals and only 22% of clips
  came out, against 34%. That 34% was measured on title alone; the artist was added after review,
  and the pairs always share it.
- A question keeps listening (`SongListener.KeepListening`, 2 minutes): a preview is 30 s of the
  song, so a clip can only match while the song is on that part. First clip at 10 s, then every 5 s
  over the last 10 s. A match of 20 or more stands (`SureAtOnce`); a weaker one must come up for
  the same song again in that question, not necessarily the next clip, since talk over a song comes
  and goes. With nothing found it says how many songs it checked, or that the library is still
  filling or is off (`SongAnswer.Of`, `growing`).

Measured on 2026-11-04 with a throwaway program over the real fingerprint code: 12 well known songs
downloaded whole, clips of 10 s every 5 s, every clip asked against 400 decoys plus the previews.
Deezer's alone found 34% of clips, Apple's 33%, wrong 0. So a song is found when the listening
reaches its previewed part: mean 11 to 33 s where it was found, and from 5% to 68% of starting
points, depending on the song, never within the 2 minutes because the part was already past. With
1118 previews (and 1313 decoy files, one sixth held out): no wrong song in about 580 clips of real
songs or 879 held out clips. Every "sure and wrong" was the same recording under another Deezer id,
looked up by hand (a remaster, a remix, a reissue), which is what same-title grouping answers.
Noise, held out songs: median 4, p90 11. The right song: median 32, p90 215. Searching 19 ms per
clip; building the index 204 ms for 2.57M landmarks (19 MB); about 2300 landmarks (18 KB) a preview.
Not measured: the full 10,000 size. Extrapolated from those numbers: about 180 MB on disk and about
as much in memory while held, and the build around a second. A larger library gives chance more to
work with, and that has not been measured either; the 3x and confirm rules are what stand against it.
In the last run the per-song percentages are depressed by the program grouping by file name, not by
name: use the 400 decoy figures for how often a song is found.

Deezer's developer FAQ says commercial use needs an agreement. Whether its terms allow keeping
fingerprints was not confirmed, and Apple's terms were not read. This is one person's own browser
and nothing but landmarks is kept, which is why it was built this way; publishing it to many users
would need that looked at first.

Verified live the same day, in the same test instance, signed out, nothing audible: after the
2 minutes the catalog filled by itself against the real Deezer and Apple sites (the first songs
135 s after asking, both sources), real MP3 and AAC previews decoded in the app at 17 to 18 KB each,
and only `.fp`, `songs.json` and `catalog.json` on disk. A song it had fetched, played from a local file on a
page of no known site from its start: named in 18 s in each of two runs (scores 25 and 32, next best 2 and 1, 20 songs in
the library), through the real button and panel; nothing learned from that page. A made-up tune: "Could
not find this song" after 121 s (score 1, of 60 songs). A YouTube song page: named from itself in
1 s. All photographed. Not verified: a real start part way through a song, talk over one, a library
past about 70 songs inside the app (the numbers above are from the throwaway program), the 10,000
limit being reached, or the person's own use.

#### Songs it learns as they play

`Settings.LearnSongs`, on by default, Privacy. It only learns from YouTube and YouTube Music. A song
heard only on TikTok is never learned, though the catalog may know it.

- Fingerprints are Shazam's published method (Wang, 2003; `Music/AudioFingerprint.cs`):
  spectrogram peaks at 11025 Hz, chosen per band so a voice cannot take them all, paired
  into 24 bit hashes, and matched by how many of a clip's hashes agree on one offset in time.
  A match needs 10 agreeing and three times the runner-up (`SongLibrary.Matches`). Measured
  offline on real captures (four songs, a TED talk laid over a second playback): right 24 of
  24 from no talk down to the song 6 dB under the talk, 19 to 21 of 24 at 10 dB under; a
  wrong song never scored over 3, with 2000 decoy songs in the library too; a 10 s question
  takes about 11 ms.
- Listening happens in the page (`Music/SongPage.cs`): `captureStream` on the playing media,
  an `AudioContext` at 11025 Hz, a processor whose output goes nowhere (gain 0), so nothing
  heard changes, and a muted tab is heard all the same. It skips digital silence and
  YouTube's ads (`ad-showing`), keeps at most 20 s, follows a playlist's next track
  (`addtrack`), and stops by itself 15 s after the browser last took from it. The browser
  takes what it heard every 2.5 s (`Music/SongListener.cs`, ticked by each window's
  once-a-second timer, minimised or not, never an agent's window).
- Learned: a YouTube video YouTube files under Music (the player's own
  `microformat...category`), or anything on YouTube Music, not live, at most ten minutes.
  Named from the video title and channel ("Artist - Song (Official Video)" by "ArtistVEVO"
  is Song by Artist, `SongNames`), or from what YouTube Music hands Windows. Kept after 30 s
  heard, then every 60 s, up to six minutes or the whole song, keyed by video id so the same
  video on either site is one song; a longer hearing replaces a shorter one, and a song heard
  in full is not listened to again. A talk, a stream, a mix or any other site is never learned.
  Unless a question is open, a tab is not even read unless its own address (not what the page
  says) is on youtube.com, www., m. or music.youtube.com, and a page claiming another of those
  hosts than its address is never learned from (`SongPage.SongSiteOf`).
- Kept in `songs` in the data folder: `songs.json` (names, how much was heard) and one
  `{id}.fp` per song (its landmarks, about 37 KB a minute heard). Never audio. At most 500 songs,
  the one learned longest ago going first. A `songs.json` that cannot be read is never
  written over (nothing is learned that run). Each keep writes a new file and removes the old
  one, never writing over it: Windows refuses to replace a file a question is reading
  (measured in a test, sharing flags or not). Files the list does not name are removed at
  start (only `{number}.fp` and its `.tmp`, and only when the list was read). The search index
  is one array of every landmark, built when a question needs it and let go three minutes
  after the last one (`SongListener.Tick` calls `ReleaseIdle`); full of four minute songs it
  is about 75 MB while held. No file work happens under the lock the UI thread reads the list
  with. Forgetting is deleting that folder; there is no button for it yet.
- Asked while the tab's audio is already being heard (the song being learned), the answer
  can come at once; otherwise it listens for 10 s, and on as above. Audio heard on the tab's
  previous page, or by a listener since replaced, never answers for the page now (measured
  live: it did, before `_recentUrl` and the listener's name were checked).
- The page's script runs in the page's own world, so a page can feed it what it likes; that
  costs a wrong song learned under that page's own video id, nothing more.
- One question at a time, app wide: two windows asking about the same tab share it, but a
  second window asking about another tab while the first listens replaces it, and the first
  says "Could not hear that tab" (`SongListener.IdentifyAsync`). Both pick the tab by the same
  rule, so it needs two tabs playing and two windows asking within the same 2 minutes; accepted.
- Not done: a player in a frame from another site is not listened to (the page itself only),
  and a site whose media is cross-origin without CORS gives silence to `captureStream`.

The button was verified on 2026-11-04 in a test instance on a hidden Windows desktop
(`scratchpad/songlive`, 12 checks, tab muted, the button found and clicked by window
messages): on the toolbar row; with nothing playing it says so and the panel hangs from the
button; Despacito played on YouTube was learned after 33 s and the button named it (score
178); the same tab on a local page playing a made-up tune, asked with Listen again, answered
"not a song Gergur knows" (score 2) and nothing was learned from it. All three panels were
photographed and looked at. The now-playing harness passed again with its panel back as it
was.

Before the button, the same feature as a row of the now-playing panel was verified the same
day (14 checks; run again after the review fixes): Despacito played on YouTube was learned
after 33 s under its own name ("Despacito ft. Daddy Yankee" by Luis Fonsi), with only
`songs.json` and one fingerprint file written; asked while it played it was named at once
(score 234); a local page playing a second capture of it with a TED talk at the same
loudness was named in 12.6 s (score 63, runner-up 0); the talk alone was "none" (score 0);
nothing else was learned. Unit tests (`SongRecognizerTests`) cover names,
which pages are songs, reading the page's answer, the library on disk, matching made-up songs
under noise, and the listener learning and answering with stand-in tabs; `SongScriptTests`
runs the page script under node. Not yet used by the person in their own Gergur, and not
tried with a library of hundreds of real songs. (That paragraph and the button's above are from
before the catalog: their answers now also wait for up to 2 minutes, see "The song catalog".)
