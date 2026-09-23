# Exercises the agent API against a running Gergur, in a window of its own, and closes
# everything it opened. Run it against a fresh Release build after changing the agent
# server: the unit tests never reach a real engine, and a live run is the only check
# that has caught a request-thread bug in these endpoints. It changes no setting's value,
# though one patch writes the same values back to the settings file.
# Never prints the url or title of any tab it did not open itself: the user's browsing
# is personal, and this only needs ids and window numbers from theirs.
#
# Windows PowerShell 5.1 hands a JSON array back from Invoke-RestMethod as one object,
# so Count on it is 1 whatever the length. Every list here is unrolled through
# ForEach-Object before it is counted; that quirk already produced one wrong number.

$ErrorActionPreference = 'Stop'
$t = Get-Content "$env:LOCALAPPDATA\Gergur\agent-token.txt"
$H = @{ 'X-Gergur-Token' = $t }
$base = 'http://127.0.0.1:24002'
$script:pass = 0
$script:fail = 0
$script:failures = @()
# Every tab this script opens, by id. Cleanup closes exactly these and nothing else: a
# window index taken at the start can name somebody else's window by the end, since
# closing a window renumbers every one after it.
$script:created = @()
function Made($answer) { if ($answer.id) { $script:created += $answer.id }; $answer }

function Check($name, $condition, $detail) {
    if ($condition) { $script:pass++; Write-Host ("  ok   " + $name) }
    else {
        $script:fail++
        $script:failures += $name
        Write-Host ("  FAIL " + $name + "   " + $detail) -ForegroundColor Red
    }
}
function List($value) { @($value | ForEach-Object { $_ }) }
function Tabs() { List (Invoke-RestMethod ($base + '/tabs') -Headers $H) }
function Post($path, $body, $timeoutSec = 60) {
    Invoke-RestMethod -Method Post -Uri ($base + $path) -Headers $H -TimeoutSec $timeoutSec `
        -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 5 -Compress)
}
function Get_($path) { Invoke-RestMethod -Uri ($base + $path) -Headers $H -TimeoutSec 60 }
function Status($block) {
    try { $r = & $block; return @{ code = 200; body = $r } }
    catch {
        $code = 0
        if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
        $body = $null
        try { $body = $_.ErrorDetails.Message | ConvertFrom-Json } catch {}
        return @{ code = $code; body = $body }
    }
}
function Show($o) { if ($null -eq $o) { 'null' } else { $o | ConvertTo-Json -Compress -Depth 4 } }

$start = Tabs
$startCount = $start.Count
$userTabIds = $start | ForEach-Object { $_.id }

# Everything that opens something runs inside the try, so a request that fails part way
# still reaches the cleanup. With ErrorActionPreference Stop, any 4xx or 5xx from a bare
# call used to end the script right there, leaving its window and tabs in the browser,
# which is exactly when a run is failing and somebody is looking at it.
try {
    Write-Host "`n--- ids"
    Check 'every tab has an id' ((List ($start | Where-Object { -not $_.id })).Count -eq 0) ''
    Check 'no two tabs share one' ((List ($start.id | Sort-Object -Unique)).Count -eq $startCount) ''

    Write-Host "`n--- a window of my own, opened behind the user's"
    $w = Made (Post '/window' @{ })
    Check 'a window comes back with a tab in it' ($null -ne $w.id -and $null -ne $w.window) (Show $w)
    $mine = $w.id
    $myWindow = $w.window

    $second = Made (Post '/open' @{ url = 'about:blank'; window = $myWindow; background = $true })
    $where = (Tabs) | Where-Object { $_.id -eq $second.id }
    Check '/open lands in the window it was asked for' ($where.window -eq $myWindow) ("asked $myWindow, got " + $where.window)
    Post '/close' @{ id = $second.id } | Out-Null

    Write-Host "`n--- navigate and wait"
    $nav = Post '/navigate' @{ id = $mine; url = 'https://example.com'; wait = $true; timeout = 30 }
    Check 'the page loaded and says so' ($nav.ok -eq $true -and $nav.loaded -eq $true) (Show $nav)
    $page = Get_ ('/page?id=' + $mine)
    Check '/page reads the page that loaded' ($page.url -like 'https://example.com*' -and $page.text.Length -gt 20) ("url " + $page.url + ", " + $page.text.Length + " chars")

    Write-Host "`n--- navigate and wait, interrupting a load in flight"
    # Not Start-Job: that spawns a new process, which takes over a second, so the "first"
    # request arrived after the "second" and the test inverted its own order. HttpClient's
    # PostAsync sends straight away without waiting for the answer.
    Add-Type -AssemblyName System.Net.Http
    $client = New-Object System.Net.Http.HttpClient
    $client.Timeout = [TimeSpan]::FromSeconds(60)
    $client.DefaultRequestHeaders.Add('X-Gergur-Token', $t)
    function PostNow($path, $json) {
        $content = New-Object System.Net.Http.StringContent($json, [Text.Encoding]::UTF8, 'application/json')
        return $client.PostAsync($base + $path, $content)
    }
    function Answer($task) { $task.Result.Content.ReadAsStringAsync().Result | ConvertFrom-Json }
    # A first load that cannot finish quickly: a non-routable address hangs or fails, so it
    # is certainly still in flight when the second replaces it. A real site from a warm cache
    # can finish inside the gap, and then "it loaded" is the truth and the check is wrong.
    $firstTask  = PostNow '/navigate' ('{"id":"' + $mine + '","url":"http://10.255.255.1/?first","wait":true,"timeout":30}')
    Start-Sleep -Milliseconds 300
    $secondTask = PostNow '/navigate' ('{"id":"' + $mine + '","url":"https://example.com/?second","wait":true,"timeout":30}')
    $first = Answer $firstTask
    $interrupt = Answer $secondTask
    $landed = (Get_ ('/page?id=' + $mine)).url
    Check 'the interrupting navigation reports its own load' ($interrupt.loaded -eq $true) (Show $interrupt)
    Check 'the replaced one does not claim to have loaded' ($first.loaded -ne $true) (Show $first)
    Check 'and the tab is on the page that won' ($landed -like '*example.com/?second*') $landed

    Write-Host "`n--- navigate refuses what it cannot do"
    $bad = Status { Post '/navigate' @{ id = $mine; url = 'edge://nonsense-that-is-not-real'; wait = $true; timeout = 10 } }
    # 503, and only 503. loaded:false is the slow-page answer, which is what this is not.
    Check 'a url the engine will not take is not a slow page' ($bad.code -eq 503) ("" + $bad.code + " " + (Show $bad.body))
    Post '/navigate' @{ id = $mine; url = 'https://example.com'; wait = $true; timeout = 30 } | Out-Null

    Write-Host "`n--- eval"
    $e = Post '/eval' @{ id = $mine; js = 'document.title' }
    Check 'an expression' ($e.ok -eq $true -and $e.result.Length -gt 0) (Show $e)
    $e = Post '/eval' @{ id = $mine; js = 'new Promise(function (r) { setTimeout(function () { r(42); }, 300); })' }
    Check 'a promise is awaited' ($e.ok -eq $true -and $e.result -eq 42) (Show $e)
    $e = Post '/eval' @{ id = $mine; js = 'var a = 1; a + 1' }
    Check 'statements keep their completion value' ($e.ok -eq $true -and $e.result -eq 2) (Show $e)
    $e = Post '/eval' @{ id = $mine; js = 'document.title // which page' }
    Check 'a trailing comment does not break it' ($e.ok -eq $true) (Show $e)
    $e = Post '/eval' @{ id = $mine; js = 'Promise.reject(new Error("no"))' }
    Check 'a rejection is an error, not a value' ($e.ok -eq $false -and $e.error -like '*no*') (Show $e)
    $e = Post '/eval' @{ id = $mine; js = 'var x = null; x.y.z' }
    Check 'a statement script that throws is not ok' ($e.ok -eq $false) (Show $e)
    $e = Post '/eval' @{ id = $mine; js = 'nonsense syntax here ((' }
    Check 'a syntax error is an error' ($e.ok -eq $false) (Show $e)
    $e = Post '/eval' @{ id = $mine; js = 'window.__ggProbe = 0; window.__ggProbe++; 5' }
    $count = (Post '/eval' @{ id = $mine; js = 'window.__ggProbe' }).result
    Check 'a side effect runs exactly once' ($count -eq 1) ("ran " + $count + " times")

    $clock = [Diagnostics.Stopwatch]::StartNew()
    $e = Post '/eval' @{ id = $mine; js = 'new Promise(function () { })'; timeout = 3 }
    $clock.Stop()
    Check 'a promise that never settles gives up' ($e.ok -eq $false) (Show $e)
    Check '  at about the timeout it was given' ($clock.Elapsed.TotalSeconds -ge 2.5 -and $clock.Elapsed.TotalSeconds -lt 10) ("took {0:N1}s" -f $clock.Elapsed.TotalSeconds)

    Write-Host "`n--- console"
    Post '/eval' @{ id = $mine; js = '(function () { setTimeout(function () { null.x; }, 0); return 1; })()' } | Out-Null
    Start-Sleep -Milliseconds 800
    $c = Get_ ('/console?id=' + $mine)
    Check 'page errors are readable' ((List $c.errors).Count -ge 1) (Show $c)

    Write-Host "`n--- click and type"
    $k = Post '/click' @{ id = $mine; selector = '#there-is-no-such-element' }
    Check 'a selector that matches nothing is ok:false, not an error' ($k.ok -eq $false) (Show $k)
    $k = Post '/click' @{ id = $mine; selector = 'h1' }
    Check 'a selector that matches clicks' ($k.ok -eq $true) (Show $k)

    Write-Host "`n--- screenshots"
    $shot = Invoke-WebRequest -Uri ($base + '/screenshot?id=' + $mine) -Headers $H -UseBasicParsing
    Check 'a page screenshot is a png' ($shot.Headers['Content-Type'] -like 'image/png*' -and $shot.RawContentLength -gt 1000) ("" + $shot.RawContentLength + " bytes")
    $pagePng = "$env:TEMP\gergur-page.png"
    [IO.File]::WriteAllBytes($pagePng, $shot.Content)

    $activeMine = ((Tabs) | Where-Object { $_.window -eq $myWindow -and $_.active }).id
    $chrome = Status { Invoke-WebRequest -Uri ($base + '/screenshot?id=' + $activeMine + '&chrome=1') -Headers $H -UseBasicParsing }
    $chromePng = "$env:TEMP\gergur-chrome.png"
    if ($chrome.code -eq 200) {
        [IO.File]::WriteAllBytes($chromePng, $chrome.body.Content)
        Check 'a window screenshot is a png' ($chrome.body.RawContentLength -gt 1000) ("" + $chrome.body.RawContentLength + " bytes")
    } else {
        Check 'a window screenshot is a png' $false ("" + $chrome.code + " " + (Show $chrome.body))
    }

    # A tab that has rendered but is not the one its window shows. The user's own tabs will
    # not do: after a restart they come back asleep, and that is refused earlier and for a
    # different reason (no frame at all), which is right but is not this check.
    $bgTab = Made (Post '/open' @{ url = 'about:blank'; window = $myWindow; background = $true })
    Post '/activate' @{ id = $bgTab.id } | Out-Null
    Start-Sleep -Milliseconds 400
    Post '/activate' @{ id = $mine } | Out-Null
    Start-Sleep -Milliseconds 400
    $wrong = Status { Invoke-WebRequest -Uri ($base + '/screenshot?id=' + $bgTab.id + '&chrome=1') -Headers $H -UseBasicParsing }
    Check 'chrome=1 for a tab its window is not showing is refused' ($wrong.code -eq 400) ("" + $wrong.code)
    # A tab of the script's own that has never been on screen, so has painted nothing. Not one
    # of the user's: asking for theirs photographed their browsing, and woke it if it slept.
    # 503 and only 503: a blank png is bigger than any size threshold, so a size check
    # passed exactly the answer this exists to catch.
    $unseen = Made (Post '/open' @{ url = 'about:blank'; window = $myWindow; background = $true })
    $r = Status { Invoke-WebRequest -Uri ($base + '/screenshot?id=' + $unseen.id) -Headers $H -UseBasicParsing }
    Check 'a tab with no frame is refused rather than photographed blank' ($r.code -eq 503) ("" + $r.code)
    Post '/close' @{ id = $unseen.id } | Out-Null

    Write-Host "`n--- settings"
    $s = Get_ '/settings'
    Check 'the pairing key is not given away' ($s.settings.DropKey -eq '(hidden)' -or $s.settings.DropKey -eq '') 'DropKey came back readable'
    foreach ($name in 'DropKey','DropEnabled','AgentServerPort','ExtraBrowserArguments','VpnEnabled','VpnProfile','VpnBypassHosts','VpnLocalPort','SearchUrlTemplate','DisableSiteIsolation') {
        $value = $s.settings.$name
        if ($value -is [bool]) { $value = -not $value } elseif ($value -is [int] -or $value -is [long]) { $value = 1234 } else { $value = 'x' }
        $r = Status { Post '/settings' @{ $name = $value } }
        Check ("$name is refused") ($r.code -eq 400 -and $r.body.error -like "*$name*") ("" + $r.code + " " + (Show $r.body))
    }
    $r = Status { Post '/settings' @{ DiscardAfterMinutes = $s.settings.SuspendAfterMinutes } }
    Check 'discarding before sleeping is refused' ($r.code -eq 400) ("" + $r.code)
    $after = Get_ '/settings'
    Check 'none of those moved anything' (($after.settings | ConvertTo-Json -Compress) -eq ($s.settings | ConvertTo-Json -Compress)) 'a refused patch still changed something'

    $ok = Post '/settings' @{ BlocklistEnabled = $s.settings.BlocklistEnabled; NoSuchSetting = 1 }
    Check 'a good patch applies' ($ok.ok -eq $true -and $ok.persisted -eq $true) (Show $ok)
    Check 'an unknown name is reported' ((List $ok.unknown) -contains 'NoSuchSetting') (Show $ok)

    Write-Host "`n--- ids that name nothing"
    foreach ($bad in 'tnope', '') {
        $r = Status { Post '/eval' @{ id = $bad; js = '1' } }
        Check ("id '" + $bad + "' is an error, not the user's tab") ($r.code -eq 404) ("" + $r.code)
    }
    $r = Status { Post '/close' @{ index = 9999 } }
    Check 'an index past the end closes nothing' ($r.code -eq 404) ("" + $r.code)

    Write-Host "`n--- a page that blocks its own main thread"
    # On its own site, so only this throwaway tab's renderer is involved, then closed.
    $wedge = Made (Post '/open' @{ url = 'https://example.net'; window = $myWindow; background = $true })
    Post '/navigate' @{ id = $wedge.id; url = 'https://example.net'; wait = $true; timeout = 30 } | Out-Null
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $r = Status { Post '/eval' @{ id = $wedge.id; js = '(function () { var t = Date.now(); while (Date.now() - t < 60000) {} return 1; })()'; timeout = 4 } 30 }
    $clock.Stop()
    Check 'a script that blocks the page does not hold the request' ($clock.Elapsed.TotalSeconds -lt 20) ("took {0:N1}s" -f $clock.Elapsed.TotalSeconds)
    Check '  and says it gave up' ($r.body.ok -eq $false) ("" + $r.code + " " + (Show $r.body))
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $r = Status { Post '/eval' @{ id = $wedge.id; js = 'var a = 1; a'; timeout = 4 } 30 }
    $clock.Stop()
    Check 'the next read of that tab does not hang either' ($clock.Elapsed.TotalSeconds -lt 20) ("took {0:N1}s" -f $clock.Elapsed.TotalSeconds)
    Post '/close' @{ id = $wedge.id } | Out-Null

} finally {
    Write-Host "`n--- cleaning up"
    $open = (Tabs) | ForEach-Object { $_.id }
    foreach ($id in $script:created) {
        if ($open -contains $id) { try { Post '/close' @{ id = $id } | Out-Null } catch {} }
    }
    Start-Sleep -Milliseconds 800
}
$end = Tabs
# Each side joined in its own brackets. Unbracketed, -join and -eq share a precedence and
# this read as ((A -join ",") -eq B) -join ",", the string "False", which is truthy:
# the one check guarding the user's tabs could not fail.
$endIds = ((List $end) | ForEach-Object { $_.id } | Sort-Object) -join ','
$startIds = ($userTabIds | Sort-Object) -join ','
Check "the user's tabs are exactly as they were" ($endIds -eq $startIds) ("started with " + $startCount + ", now " + $end.Count)

Write-Host ""
Write-Host ("passed: " + $script:pass + "   failed: " + $script:fail)
if ($script:fail -gt 0) { Write-Host ("failed: " + ($script:failures -join '; ')) }
Write-Host ("page png:   " + $pagePng)
Write-Host ("window png: " + $chromePng)
