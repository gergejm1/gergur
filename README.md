# Gergur

A personal, memory-frugal browser for Windows 11. A tiny WinForms shell over
WebView2 (the Edge engine already installed with Windows - nothing bundled),
with an aggressive tab-lifecycle policy that mainstream browsers won't ship:

![Gergur home page](docs/home.png)

## Benchmark

Same six tabs (Wikipedia, GitHub, Hacker News, Stack Overflow, The Verge,
example.com), clean profiles for all three browsers, measured as the sum of
private bytes across each browser's entire process tree:

| | Chrome | Edge | Firefox | Gergur |
|---|---|---|---|---|
| All six tabs freshly loaded | 2,835 MB | 1,039 MB | 1,925 MB | 988 MB |
| A few minutes later | 1,875 MB | 1,031 MB | 1,891 MB | **374 MB** |

Chrome, Edge, and Firefox all keep every renderer alive indefinitely. Gergur
converges to a single live renderer: background tabs first freeze, then park to
zero-process snapshots that reload on click. Firefox (Gecko engine) settles the
heaviest of the mainstream three; Gergur uses roughly 5x less than all of them.

## How

- **Active** → the one visible tab, fully alive.
- **Hidden** → background tab, still live.
- **Suspended** (after 3 min idle) → page frozen via `TrySuspendAsync`, renderer
  memory trimmed. Tabs playing audio are exempt (they get low-memory mode instead).
- **Discarded** (after 15 min idle) → the WebView is destroyed entirely; the tab
  keeps only its URL/title/favicon and holds **zero** engine processes until clicked.
  The selected tab is never discarded, so its exact state always survives.

When the window is minimized or the PC is locked for a minute, everything sleeps,
active tab included; restoring the window wakes it instantly. The engine also runs
with the spare-renderer process disabled and memory pressure simulated on hidden
views (`DisableSpareRenderer` / `InactiveMemoryPressure` toggles in settings).

Sleeping tabs show a ☾ in the tab strip. The status bar shows live engine memory,
renderer count, tabs asleep, and blocked-request count. New tabs open the Gërgur
home page (`src/Gergur/Assets/`, generated from the logo, along with the G app icon). Session restore brings
background tabs back as discarded snapshots, so startup cost is one renderer no
matter how many tabs you had open.

Blocking is two layers: the engine's Strict tracking prevention, plus a
hosts-format blocklist (StevenBlack, auto-downloaded on first run) enforced via
`WebResourceRequested` with allocation-free suffix matching on the request hot path.

![Browsing with a parked tab](docs/browsing.png)

## Default browser

```
.\scripts\register-browser.ps1            # register the current build
.\scripts\register-browser.ps1 -Install   # copy to %LOCALAPPDATA%\Programs\Gergur first
.\scripts\register-browser.ps1 -Remove    # undo
```

Registers Gergur under `HKCU\Software\Clients\StartMenuInternet` with `http`, `https`,
`.htm` and `.html` associations, then opens Settings. Windows never lets an app make
itself the default, so picking Gergur under "Web browser" is a step only you can do.
No admin rights; nothing changes for other users.

`-Install` exists because a registered path has to keep working: pointing Windows at a
`bin\Release` folder is fine while developing, but that folder moving breaks every link
on the machine. Register the build while iterating, install when you are done.

This is also what makes desktop-app sign-in work. "Sign in with Google" from a desktop
app shells out to the default browser and then redirects to a loopback address like
`http://127.0.0.1:5000/callback`, which is why `http` is registered alongside `https`,
and why localhost stays in `VpnBypassHosts` so the callback is not tunnelled.

Windows opens links by running `Gergur.exe <url>` whether or not Gergur is up. When it
is, that launch forwards the url over a named pipe to the running instance and exits;
the url opens as a new tab in the focused window. A url on the command line opens *on
top of* the restored session, never instead of it.

## Windows

Drag a tab off the strip and let go: it becomes its own window. Drop it on another
window's tab strip instead and it lands there, at the slot the accent bar shows.
Merging a window's last tab away closes that window.

The tab does not reload. Its live WebView is reparented rather than recreated, so
scroll position, script state and half-typed forms survive the move. Every window
shares one engine environment, one blocklist, one history and bookmark store, and
one tunnel, so a second window costs a shell, not a browser: the memory policy
still sees a single process group. Session restore brings every window back.

## Browser-only VPN

`scripts/setup-warp.ps1` provisions a free Cloudflare WARP account (via wgcf)
and a userspace WireGuard tunnel (wireproxy) exposing a local SOCKS5 proxy.
The engine launches with `--proxy-server` plus a host-resolver rule so all
traffic AND DNS route through the tunnel - browser only, nothing system-wide.
Toggle from the menu; a badge shows in the status bar. Verified via
cloudflare.com/cdn-cgi/trace (`warp=on`, Cloudflare exit IP).

### Choosing an exit country

WARP always exits at the Cloudflare datacenter nearest you, so it hides your IP
but cannot make you look like you are somewhere else. Any other WireGuard
provider drops into the same tunnel: put its `.conf` in
`%LOCALAPPDATA%\Gergur\vpn\profiles\` (or use "Add profile from .conf file" in
the VPN menu) and it appears in the menu, named after the file. Free providers
that hand out WireGuard configs with a choice of country include Windscribe
(10 GB/month) and Proton VPN's free tier; a WireGuard server on a free-tier
cloud VM works the same way.

Switching between profiles restarts only wireproxy, so it takes about a second
and the browser keeps running. Turning the VPN on or off restarts the browser,
because the proxy is a browser-process flag.

## Build & run

```
dotnet run --project src\Gergur          # dev
dotnet publish src\Gergur -c Release     # optimized build
dotnet test                              # unit tests (policy engine, blocklist, URL heuristics)
```

## Shortcuts

| Keys | Action |
|---|---|
| Ctrl+T / Ctrl+W | new / close tab |
| Ctrl+Shift+T | reopen closed tab |
| Ctrl+Tab / Ctrl+Shift+Tab | next / previous tab |
| Ctrl+1..8, Ctrl+9 | tab N, last tab |
| Ctrl+L or Alt+D | focus address bar |
| Ctrl+R / F5, Alt+←/→ | reload, back/forward |
| Ctrl+D | bookmark toggle |
| Ctrl+H / Ctrl+J | history / downloads |
| Ctrl+F, F3 | find in page, find next |
| Ctrl+P | print (Save as PDF lives in that dialog) |
| Ctrl+M | mute tab |
| Ctrl+plus / Ctrl+minus / Ctrl+0 | zoom in / out / reset |
| F12 | DevTools |

## Data & settings

Everything lives in `%LOCALAPPDATA%\Gergur`: `settings.json` (suspend/discard
timers, search engine, browser flags), `blocklist.txt`, `bookmarks.json`,
`history.jsonl`, `session.json`, and the WebView2 profile. The ≡ menu has
"Sleep background tabs now", History, the browser task manager, and a memory-CSV
dump for A/B-testing flags. Set `GERGUR_DEBUG=1` for a trace log.

Downloads (Ctrl+J) lists every download across every window, with progress drawn behind
the row, plus open, show-in-folder, cancel and clear-finished. The engine's own download
flyout is suppressed, since two competing lists is worse than either alone. Files still
land wherever the engine would have put them.

Settings (≡ menu) edits everything in `settings.json` without a text editor. It works on
a copy, so Cancel really cancels, and it names any engine-level change that needs a
restart rather than quietly doing nothing.

History (Ctrl+H) opens a searchable window over `history.jsonl`: type to filter on
title or address, Enter or double-click to open in a new tab, Delete to forget the
selected pages, "Clear all" to wipe the log. Consecutive visits to the same URL
collapse into one row, so a reload loop does not bury the rest.

Flags in settings (`ProcessPerSite`, `DisableSiteIsolation`, `V8ScavengerMaxMb`)
are applied at engine startup; changing them requires a full app restart.
`DisableSiteIsolation` trades process isolation for fewer processes - off by default.
