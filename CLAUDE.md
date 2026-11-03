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
| POST /login | {"submit": false?, "account": "..."?} | sign in with a saved password, choosing the account and filling two-step password pages: {filled, user, submitted, via, stillAsking, url, picked}. See "Saved passwords" |
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
- Playlist songs being "skipped" is YouTube refusing to play, not the ad code, measured
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
  length). Where it stops is `AgentServer.LoginStop`: no field (or one only in a frame or
  shadow root it cannot click into, said as such), an empty field (no saved login), more
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

#### A test instance

`GERGUR_DATA_DIR` moves the whole data folder (settings, profile, logs) and gives the
instance its own single-instance mutex and pipe (`Settings.InstanceSuffix`, a hash of the
folder), so a second Gergur runs beside the person's own without touching their profile,
vault or settings. Use it for anything that needs saved logins: seed the scratch profile,
never the real vault. Launch it through WMI (`Win32_Process.Create`), not as a child of the
terminal: a child of the front app may take the foreground.

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
