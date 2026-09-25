// The one channel between Gergur's own pages (the new tab page, History, Downloads,
// Bookmarks) and the browser, and the small helpers those pages share.
//
// A request goes out as chrome.webview.postMessage({gergur: 1, id, op, args}) and comes
// back as {id, ok: true, result} or {id, ok: false, error}. Pushes arrive the same way
// with an event name and no id. With ?demo=1 in the page url chrome.webview is never
// touched: a built-in backend answers every op from invented sample data, so the pages
// can be looked at straight from the source folder, where the browser refuses them.
//
// These pages can read browsing history and every title and address in it was written
// by some website, so nothing here builds markup from data: DOM nodes and textContent
// only. bridge.test.js searches the pages' source for the known ways round that, markup
// sinks and links set from script included. A search of the text is a floor, not a proof,
// and it reads comments too, so this one names none of them.
//
// Under node only the pure parts are exported, for bridge.test.js; the browser wiring at
// the bottom never runs there.
(function () {
    "use strict";

    var TIMEOUT_MS = 5000;

    var WEEKDAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    var MONTHS = ["January", "February", "March", "April", "May", "June", "July",
        "August", "September", "October", "November", "December"];

    // ---------------------------------------------------------------- dates

    function toDate(value) {
        if (value === null || value === undefined || value === "") return null;
        var date = value instanceof Date ? value : new Date(value);
        return isNaN(date.getTime()) ? null : date;
    }

    function pad2(n) { return (n < 10 ? "0" : "") + n; }

    function startOfDay(date) {
        return new Date(date.getFullYear(), date.getMonth(), date.getDate());
    }

    // Calendar days, not 24 hour spans: a day that changes the clock is 23 or 25 hours
    // long, and rounding is what keeps that from moving an evening into the wrong day.
    function daysBefore(date, now) {
        return Math.round((startOfDay(now) - startOfDay(date)) / 86400000);
    }

    /** Local calendar day as YYYY-MM-DD, or "" for something that is not a date. */
    function dayKey(value) {
        var d = toDate(value);
        if (!d) return "";
        return d.getFullYear() + "-" + pad2(d.getMonth() + 1) + "-" + pad2(d.getDate());
    }

    function longDate(d, now) {
        var text = WEEKDAYS[d.getDay()] + ", " + d.getDate() + " " + MONTHS[d.getMonth()];
        return d.getFullYear() === now.getFullYear() ? text : text + " " + d.getFullYear();
    }

    /** The heading a day's history sits under: Today, Yesterday, a weekday, then a date. */
    function dayLabel(value, now) {
        var d = toDate(value);
        if (!d) return "Unknown date";
        now = toDate(now) || new Date();
        var days = daysBefore(d, now);
        if (days === 0) return "Today";
        if (days === 1) return "Yesterday";
        if (days > 1 && days < 7) return WEEKDAYS[d.getDay()];
        return longDate(d, now);
    }

    /** Local time of day, 24 hour. */
    function timeLabel(value) {
        var d = toDate(value);
        return d ? pad2(d.getHours()) + ":" + pad2(d.getMinutes()) : "";
    }

    /** A compact "when" for one line: the time today, otherwise the day. */
    function shortWhen(value, now) {
        var d = toDate(value);
        if (!d) return "";
        now = toDate(now) || new Date();
        var days = daysBefore(d, now);
        if (days === 0) return timeLabel(d);
        if (days === 1) return "Yesterday";
        if (days > 1 && days < 7) return WEEKDAYS[d.getDay()];
        var text = d.getDate() + " " + MONTHS[d.getMonth()].slice(0, 3);
        return d.getFullYear() === now.getFullYear() ? text : text + " " + d.getFullYear();
    }

    /**
     * Items grouped under their local day, in the order the days first appear, each group
     * keeping the items' own order. Items without a readable date share one group.
     */
    function groupByDay(items, dateOf, now) {
        var groups = [];
        var byKey = new Map();
        (items || []).forEach(function (item) {
            var value = dateOf(item);
            var key = dayKey(value) || "unknown";
            var group = byKey.get(key);
            if (!group) {
                group = { key: key, label: dayLabel(value, now), items: [] };
                byKey.set(key, group);
                groups.push(group);
            }
            group.items.push(item);
        });
        return groups;
    }

    // ---------------------------------------------------------------- text

    /** Sizes the way the browser's own status text writes them (DownloadItem.Size). */
    function formatBytes(bytes) {
        var n = Number(bytes);
        if (!isFinite(n) || n < 0) return "";
        n = Math.floor(n);
        var units = [["GB", 1073741824], ["MB", 1048576], ["KB", 1024]];
        for (var i = 0; i < units.length; i++) {
            if (n >= units[i][1]) return String(Math.round(n / units[i][1] * 10) / 10) + " " + units[i][0];
        }
        return n + " B";
    }

    /** Fraction done, 0 to 1, or null when the size is not known. */
    function progressOf(received, total) {
        var r = Number(received), t = Number(total);
        if (!(t > 0) || !isFinite(r)) return null;
        return Math.min(1, Math.max(0, r / t));
    }

    function parseUrl(url) {
        try { return new URL(String(url)); } catch (e) { return null; }
    }

    /** The host to show for an address, without a leading www., or "" when it has none. */
    function hostOf(url) {
        var parsed = parseUrl(url);
        if (!parsed || !parsed.hostname) return "";
        return parsed.hostname.replace(/^www\./, "");
    }

    /** What to show where a host goes, for addresses that have none as well. */
    function siteOf(url) {
        var host = hostOf(url);
        if (host) return host;
        var parsed = parseUrl(url);
        if (!parsed) return "";
        if (parsed.protocol === "file:") return "Local file";
        return parsed.protocol.replace(/:$/, "");
    }

    /** Text cut to at most max characters, never through the middle of a surrogate pair. */
    function clip(text, max) {
        var s = String(text === null || text === undefined ? "" : text);
        if (s.length <= max) return s;
        var end = max - 1;
        var code = s.charCodeAt(end - 1);
        if (code >= 0xd800 && code <= 0xdbff) end--;
        return s.slice(0, end) + "\u2026";
    }

    /** The title, or the address when a page never had one. */
    function displayTitle(title, url) {
        var t = String(title === null || title === undefined ? "" : title).trim();
        return clip(t || String(url === null || url === undefined ? "" : url), 400);
    }

    var LETTER = /[\p{L}\p{N}]/u;

    /** The letter on an avatar: the title's, which is what the person reads beside it, else
     *  the site's, else "#". Site first gave "MDN Web Docs" a D and Wikipedia an E. */
    function letterOf(title, url) {
        var sources = [String(title === null || title === undefined ? "" : title), hostOf(url)];
        for (var i = 0; i < sources.length; i++) {
            var m = LETTER.exec(sources[i]);
            if (m) return Array.from(m[0].toUpperCase())[0];
        }
        return "#";
    }

    /** A stable hue for a site, so one host always gets the same colour. FNV-1a. */
    function hueOf(text) {
        var s = String(text === null || text === undefined ? "" : text);
        var h = 0x811c9dc5;
        for (var i = 0; i < s.length; i++) {
            h ^= s.charCodeAt(i);
            h = Math.imul(h, 0x01000193);
        }
        return (h >>> 0) % 360;
    }

    /** Whether an item's title or address holds every word of the query, ignoring case. */
    function matches(item, query) {
        var words = String(query || "").toLowerCase().split(/\s+/).filter(Boolean);
        if (!words.length) return true;
        var hay = (String(item && item.title || "") + " " + String(item && item.url || "")).toLowerCase();
        return words.every(function (w) { return hay.indexOf(w) !== -1; });
    }

    /** Ctrl+click, Cmd+click and middle-click open in a new tab. */
    function wantsNewTab(e) {
        return !!(e && (e.ctrlKey || e.metaKey || e.button === 1));
    }

    /** "PDF" for "report.pdf"; "" when there is no short extension to show. */
    function extensionOf(fileName) {
        var name = String(fileName || "").split(/[\\/]/).pop();
        var dot = name.lastIndexOf(".");
        if (dot <= 0) return "";
        var ext = name.slice(dot + 1);
        return /^[A-Za-z0-9]{1,4}$/.test(ext) ? ext.toUpperCase() : "";
    }

    /** The browser's refusals are phrases ("there is no x"); shown, they want to be sentences. */
    function sentence(text) {
        var s = String(text === null || text === undefined ? "" : text).trim();
        if (!s) return "";
        s = s.charAt(0).toUpperCase() + s.slice(1);
        return /[.!?]$/.test(s) ? s : s + ".";
    }

    function demoRequested(search) {
        try { return new URLSearchParams(String(search || "")).get("demo") === "1"; }
        catch (e) { return false; }
    }

    // ---------------------------------------------------------------- the channel

    function failure(message, code) {
        var error = new Error(message);
        error.code = code;
        return error;
    }

    /**
     * Requests matched to their replies by id, pushes handed to whoever listens. post
     * sends one request and may throw; receive takes whatever arrives. A request nobody
     * answers rejects after timeoutMs rather than leaving a page waiting on nothing.
     */
    function createClient(post, timers, timeoutMs) {
        var pending = new Map();
        var listeners = new Map();
        var nextId = 1;
        var seconds = Math.round(timeoutMs / 1000);

        function call(op, args) {
            return new Promise(function (resolve, reject) {
                var id = nextId++;
                var entry = { resolve: resolve, reject: reject };
                entry.timer = timers.setTimeout(function () {
                    pending.delete(id);
                    reject(failure("Gergur did not answer within " + seconds + " seconds.", "timeout"));
                }, timeoutMs);
                pending.set(id, entry);
                try {
                    post({ gergur: 1, id: id, op: op, args: args || {} });
                } catch (e) {
                    timers.clearTimeout(entry.timer);
                    pending.delete(id);
                    reject(e instanceof Error ? e : failure(String(e), "send"));
                }
            });
        }

        function receive(data) {
            if (typeof data === "string") {
                try { data = JSON.parse(data); } catch (e) { return; }
            }
            if (!data || typeof data !== "object") return;
            if (typeof data.event === "string" && data.id === undefined) {
                var list = listeners.get(data.event);
                if (list) list.slice().forEach(function (fn) { fn(data); });
                return;
            }
            var entry = pending.get(data.id);
            if (!entry) return;
            pending.delete(data.id);
            timers.clearTimeout(entry.timer);
            if (data.ok === true) entry.resolve(data.result);
            else entry.reject(failure(sentence(data.error) || "Gergur could not do that.", "refused"));
        }

        function on(event, fn) {
            if (!listeners.has(event)) listeners.set(event, []);
            listeners.get(event).push(fn);
            return function off() {
                var list = listeners.get(event) || [];
                var at = list.indexOf(fn);
                if (at !== -1) list.splice(at, 1);
            };
        }

        // pendingCount is for the tests: a request that timed out or was answered must not linger.
        return { call: call, receive: receive, on: on, pendingCount: function () { return pending.size; } };
    }

    // ---------------------------------------------------------------- demo backend

    /**
     * Answers every op the way the browser does, from invented sample data, including its
     * refusals, so a page previewed with ?demo=1 meets the same shapes it will in a tab.
     * options: now() in ms, push(event) for changes, onOpen(url, newTab), and timers
     * ({setInterval, clearInterval}) to move the running download; without timers it moves
     * only when tick() is called.
     */
    function createDemo(options) {
        options = options || {};
        var push = options.push || function () {};
        var onOpen = options.onOpen || function () {};
        var timers = options.timers || null;
        var started = options.now ? options.now() : Date.now();
        var base = new Date(started);
        var midnight = startOfDay(base).getTime();

        function iso(ms) { return new Date(ms).toISOString(); }
        // Spread across the part of today that has happened, so "today" stays today at
        // any hour the preview is opened.
        function today(fraction) { return iso(midnight + (started - midnight) * fraction); }
        function daysAgo(days, hour, minute) {
            return iso(new Date(base.getFullYear(), base.getMonth(), base.getDate() - days, hour, minute).getTime());
        }

        var history = [
            { url: "https://developer.mozilla.org/en-US/docs/Web/API/Element/replaceChildren", title: "Element: replaceChildren() method - Web APIs | MDN", visitedUtc: today(0.97) },
            { url: "https://news.ycombinator.com/", title: "Hacker News", visitedUtc: today(0.9) },
            { url: "https://attacker.example/titles-are-text", title: "<img src=x onerror=alert(1)> Titles are shown as text, never as markup", visitedUtc: today(0.72) },
            { url: "https://example.org/reading-list?utm_source=demo&page=2", title: "", visitedUtc: today(0.55) },
            { url: "https://www.bbc.co.uk/weather", title: "Weather for the week ahead", visitedUtc: today(0.3) },
            { url: "https://news.ycombinator.com/", title: "Hacker News", visitedUtc: daysAgo(1, 21, 14) },
            { url: "https://doc.rust-lang.org/book/ch04-00-understanding-ownership.html", title: "Understanding Ownership: how borrowing, references and lifetimes fit together in a long chapter title", visitedUtc: daysAgo(1, 18, 2) },
            { url: "https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/communicate-btwn-web-native", title: "Interop of native-side and web-side code - Microsoft Edge Developer documentation", visitedUtc: daysAgo(1, 9, 41) },
            { url: "https://www.youtube.com/playlist?list=WL", title: "Watch later - YouTube", visitedUtc: daysAgo(3, 22, 5) },
            { url: "https://en.wikipedia.org/wiki/Crimson", title: "Crimson - Wikipedia", visitedUtc: daysAgo(4, 13, 30) },
            { url: "file:///C:/Users/demo/Documents/notes.html", title: "notes.html", visitedUtc: daysAgo(5, 8, 12) },
            { url: "https://github.com/example/sample-project/releases", title: "Releases - example/sample-project", visitedUtc: daysAgo(12, 16, 45) },
            { url: "https://www.seriouseats.com/sourdough-starter", title: "Sourdough starter, day four", visitedUtc: daysAgo(40, 10, 0) },
        ];

        var bookmarks = [
            { url: "https://github.com/", title: "GitHub", addedUtc: daysAgo(90, 12, 0) },
            { url: "https://developer.mozilla.org/", title: "MDN Web Docs", addedUtc: daysAgo(80, 12, 0) },
            { url: "https://en.wikipedia.org/", title: "Wikipedia", addedUtc: daysAgo(70, 12, 0) },
            { url: "https://news.ycombinator.com/", title: "Hacker News", addedUtc: daysAgo(60, 12, 0) },
            { url: "https://www.youtube.com/", title: "YouTube", addedUtc: daysAgo(50, 12, 0) },
            { url: "https://www.bbc.co.uk/weather", title: "BBC Weather", addedUtc: daysAgo(40, 12, 0) },
        ];

        var MB = 1048576;
        var downloads = [
            { id: 4, fileName: "sample-dataset-2026.zip", url: "https://files.example.com/datasets/sample-dataset-2026.zip", state: "inProgress", received: Math.round(12.4 * MB), total: 80 * MB, startedUtc: today(0.99), status: "" },
            { id: 3, fileName: "Quarterly report.pdf", url: "https://intranet.example.com/reports/q3.pdf", state: "completed", received: Math.round(2.3 * MB), total: Math.round(2.3 * MB), startedUtc: today(0.6), status: "" },
            { id: 2, fileName: "node-v22.19.0-x64.msi", url: "https://nodejs.org/dist/v22.19.0/node-v22.19.0-x64.msi", state: "interrupted", received: 8 * MB, total: 29 * MB, startedUtc: daysAgo(1, 17, 20), status: "Failed: network" },
            { id: 1, fileName: "harbour-at-dusk.jpg", url: "https://images.example.net/photos/harbour-at-dusk.jpg", state: "completed", received: Math.round(3.1 * MB), total: Math.round(3.1 * MB), startedUtc: daysAgo(3, 11, 5), status: "" },
        ];
        downloads.forEach(describe);

        var ticker = null;

        function describe(d) {
            if (d.state === "completed") d.status = formatBytes(d.total > 0 ? d.total : d.received);
            else if (d.state === "inProgress") d.status = d.total > 0
                ? formatBytes(d.received) + " of " + formatBytes(d.total)
                : formatBytes(d.received);
        }

        function copy(value) { return JSON.parse(JSON.stringify(value)); }

        function refusal(message) {
            var e = new Error(message);
            e.refusal = true;
            return e;
        }

        function text(args, name) {
            if (typeof args[name] !== "string") throw refusal(name + " is required");
            return args[name];
        }

        function download(args) {
            var found = downloads.filter(function (d) { return d.id === args.id; })[0];
            if (!found) throw refusal("that download is no longer in the list");
            return found;
        }

        function changed(event) {
            push(event === "downloads" ? { event: "downloads", items: copy(downloads) } : { event: event });
        }

        function tick() {
            var moving = false;
            downloads.forEach(function (d) {
                if (d.state !== "inProgress") return;
                d.received = d.total > 0 ? Math.min(d.total, d.received + Math.round(d.total / 110)) : d.received + 262144;
                if (d.total > 0 && d.received >= d.total) d.state = "completed";
                else moving = true;
                describe(d);
            });
            changed("downloads");
            if (!moving) stop();
            return moving;
        }

        function stop() {
            if (ticker !== null && timers) timers.clearInterval(ticker);
            ticker = null;
        }

        function keepMoving() {
            if (ticker !== null || !timers) return;
            if (downloads.some(function (d) { return d.state === "inProgress"; }))
                ticker = timers.setInterval(tick, 600);
        }

        function run(op, args) {
            switch (op) {
                case "open": {
                    var url = text(args, "url");
                    var parsed = parseUrl(url);
                    if (!parsed || ["http:", "https:", "file:"].indexOf(parsed.protocol) === -1)
                        throw refusal("that address cannot be opened from here");
                    onOpen(url, args.newTab === true);
                    return {};
                }
                case "history.list": {
                    var max = Math.min(5000, Math.max(1, typeof args.max === "number" ? Math.floor(args.max) : 500));
                    var q = typeof args.q === "string" ? args.q.trim() : "";
                    return history
                        .filter(function (h) { return matches(h, q); })
                        .sort(function (a, b) { return a.visitedUtc < b.visitedUtc ? 1 : a.visitedUtc > b.visitedUtc ? -1 : 0; })
                        .slice(0, max);
                }
                case "history.remove": {
                    var urls = Array.isArray(args.urls) ? args.urls.filter(function (u) { return typeof u === "string"; }) : [];
                    var before = history.length;
                    history = history.filter(function (h) { return urls.indexOf(h.url) === -1; });
                    return { removed: before - history.length };
                }
                case "history.clear":
                    history = [];
                    return {};
                case "bookmarks.list":
                    return bookmarks;
                case "bookmarks.remove": {
                    var gone = text(args, "url");
                    bookmarks = bookmarks.filter(function (b) { return b.url !== gone; });
                    changed("bookmarks");
                    return {};
                }
                case "bookmarks.rename": {
                    var target = text(args, "url");
                    var title = text(args, "title").trim();
                    var found = bookmarks.filter(function (b) { return b.url === target; })[0];
                    if (!found || !title) throw refusal("that bookmark could not be renamed");
                    found.title = title;
                    changed("bookmarks");
                    return {};
                }
                case "bookmarks.add": {
                    var addUrl = text(args, "url");
                    var addTitle = text(args, "title").trim();
                    if (!addTitle || bookmarks.some(function (b) { return b.url === addUrl; }))
                        throw refusal("that bookmark could not be added");
                    var at = typeof args.index === "number" ? Math.min(bookmarks.length, Math.max(0, Math.floor(args.index))) : bookmarks.length;
                    bookmarks.splice(at, 0, { url: addUrl, title: addTitle, addedUtc: iso(Date.now()) });
                    changed("bookmarks");
                    return {};
                }
                case "downloads.list":
                    keepMoving();
                    return downloads;
                case "downloads.open":
                case "downloads.showInFolder":
                    download(args);
                    return {};
                case "downloads.cancel": {
                    var d = download(args);
                    if (d.state === "inProgress") {
                        d.state = "interrupted";
                        d.status = "Stopped";
                        changed("downloads");
                    }
                    return {};
                }
                case "downloads.clearFinished":
                    downloads = downloads.filter(function (d) { return d.state === "inProgress"; });
                    changed("downloads");
                    return {};
                default:
                    throw refusal("there is no " + op);
            }
        }

        /** One request in, its reply out, serialised as the browser would. */
        function handle(message) {
            var id = message && message.id;
            var args = message && message.args && typeof message.args === "object" ? message.args : {};
            try {
                return copy({ id: id, ok: true, result: run(message && message.op, args) });
            } catch (e) {
                if (e && e.refusal) return { id: id, ok: false, error: e.message };
                throw e;
            }
        }

        return { handle: handle, tick: tick, stop: stop };
    }

    var pure = {
        TIMEOUT_MS: TIMEOUT_MS,
        dayKey: dayKey,
        dayLabel: dayLabel,
        timeLabel: timeLabel,
        shortWhen: shortWhen,
        groupByDay: groupByDay,
        formatBytes: formatBytes,
        progressOf: progressOf,
        hostOf: hostOf,
        siteOf: siteOf,
        clip: clip,
        displayTitle: displayTitle,
        letterOf: letterOf,
        hueOf: hueOf,
        matches: matches,
        wantsNewTab: wantsNewTab,
        extensionOf: extensionOf,
        sentence: sentence,
        demoRequested: demoRequested,
        createClient: createClient,
        createDemo: createDemo,
    };

    if (typeof module === "object" && module && module.exports) {
        module.exports = pure;
        return;
    }

    // ---------------------------------------------------------------- browser wiring

    var isDemo = demoRequested(window.location.search);
    var timers = {
        setTimeout: function (fn, ms) { return window.setTimeout(fn, ms); },
        clearTimeout: function (t) { window.clearTimeout(t); },
        setInterval: function (fn, ms) { return window.setInterval(fn, ms); },
        clearInterval: function (t) { window.clearInterval(t); },
    };
    var client;

    if (isDemo) {
        // A little latency, so a page is seen handling an answer that is not instant.
        var LATENCY = 90;
        var demo = createDemo({
            now: Date.now,
            timers: timers,
            push: function (event) { window.setTimeout(function () { client.receive(event); }, LATENCY + 30); },
            onOpen: function (url, newTab) {
                toast("Demo: would open " + clip(url, 80) + (newTab ? " in a new tab" : " here"));
            },
        });
        client = createClient(function (message) {
            var reply = demo.handle(message);
            window.setTimeout(function () { client.receive(reply); }, LATENCY);
        }, timers, TIMEOUT_MS);
    } else {
        var webview = window.chrome && window.chrome.webview;
        client = createClient(function (message) {
            if (!webview) throw failure("This page only works inside Gergur.", "no-host");
            webview.postMessage(message);
        }, timers, TIMEOUT_MS);
        if (webview) webview.addEventListener("message", function (e) { client.receive(e.data); });
    }

    // ---------------------------------------------------------------- DOM helpers

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined && text !== null) node.textContent = text;
        return node;
    }

    function clear(node) {
        while (node.firstChild) node.removeChild(node.firstChild);
    }

    var ICONS = {
        close: "M4 4l8 8M12 4l-8 8",
    };

    function icon(name) {
        var ns = "http://www.w3.org/2000/svg";
        var svg = document.createElementNS(ns, "svg");
        svg.setAttribute("viewBox", "0 0 16 16");
        svg.setAttribute("aria-hidden", "true");
        var path = document.createElementNS(ns, "path");
        path.setAttribute("d", ICONS[name]);
        path.setAttribute("fill", "none");
        path.setAttribute("stroke", "currentColor");
        path.setAttribute("stroke-width", "1.6");
        path.setAttribute("stroke-linecap", "round");
        svg.appendChild(path);
        return svg;
    }

    function avatar(title, url, large) {
        var node = el("span", large ? "avatar avatar-lg" : "avatar", letterOf(title, url));
        node.style.setProperty("--hue", String(hueOf(hostOf(url) || title || url)));
        node.setAttribute("aria-hidden", "true");
        return node;
    }

    function describeError(err) {
        if (err && err.code === "no-host")
            return "This page only works inside Gergur. To preview it with sample data, add ?demo=1 to the address.";
        return (err && err.message) || "Something went wrong.";
    }

    function openUrl(url, newTab) {
        return client.call("open", { url: url, newTab: !!newTab }).catch(function (err) {
            toast("Could not open that. " + describeError(err));
        });
    }

    /**
     * Makes a node open url when activated: click here, Ctrl+click or middle-click in a
     * new tab, Enter from the keyboard. Never an href, so every open goes through the
     * browser, which decides what may be opened and where.
     */
    function linkTo(node, url) {
        node.tabIndex = 0;
        node.setAttribute("role", "link");
        node.title = clip(url, 2048);
        node.addEventListener("click", function (e) {
            if (e.button !== 0) return;
            e.preventDefault();
            openUrl(url, wantsNewTab(e));
        });
        node.addEventListener("auxclick", function (e) {
            if (e.button !== 1) return;
            e.preventDefault();
            openUrl(url, true);
        });
        // Middle button down would otherwise start autoscroll instead of opening.
        node.addEventListener("mousedown", function (e) { if (e.button === 1) e.preventDefault(); });
        node.addEventListener("keydown", function (e) {
            if (e.key !== "Enter") return;
            e.preventDefault();
            openUrl(url, e.ctrlKey || e.metaKey);
        });
        return node;
    }

    function notice(container, title, detail, action, onAction) {
        clear(container);
        var box = el("div", "notice");
        box.appendChild(el("p", "notice-title", title));
        if (detail) box.appendChild(el("p", "notice-detail", detail));
        if (action) {
            var button = el("button", "btn", action);
            button.type = "button";
            button.addEventListener("click", onAction);
            box.appendChild(button);
        }
        container.appendChild(box);
        return box;
    }

    function showError(container, title, err, retry) {
        var box = notice(container, title, describeError(err), retry ? "Try again" : null, retry);
        box.setAttribute("role", "alert");
        return box;
    }

    function showLoading(container) {
        var box = notice(container, "Loading\u2026");
        box.classList.add("is-loading");
        return box;
    }

    var toastHost = null;

    /** A short message at the bottom; with an action it offers one button (Undo). */
    function toast(message, opts) {
        opts = opts || {};
        if (!toastHost) {
            toastHost = el("div", "toasts");
            toastHost.setAttribute("role", "status");
            toastHost.setAttribute("aria-live", "polite");
            document.body.appendChild(toastHost);
        }
        while (toastHost.children.length >= 3) toastHost.removeChild(toastHost.firstChild);
        var box = el("div", "toast");
        box.appendChild(el("span", "toast-text", message));
        var done = false;
        var timer = null;
        function dismiss() {
            if (done) return;
            done = true;
            window.clearTimeout(timer);
            if (box.parentNode) box.parentNode.removeChild(box);
            if (opts.onDismiss) opts.onDismiss();
        }
        if (opts.action) {
            var button = el("button", "btn btn-sm", opts.action);
            button.type = "button";
            button.addEventListener("click", function () {
                dismiss();
                opts.onAction();
            });
            box.appendChild(button);
        }
        toastHost.appendChild(box);
        timer = window.setTimeout(dismiss, opts.duration || 4500);
        return { dismiss: dismiss };
    }

    function debounce(fn, ms) {
        var timer = null;
        function later() {
            window.clearTimeout(timer);
            timer = window.setTimeout(function () { timer = null; fn(); }, ms);
        }
        later.now = function () {
            window.clearTimeout(timer);
            timer = null;
            fn();
        };
        return later;
    }

    /**
     * Which row and which control in it had focus, so a list rebuilt under the keyboard
     * puts focus back rather than dropping it on the page. Rows carry data-key, controls
     * data-part. A row that went away hands focus to the one now in its place.
     */
    function captureFocus(root) {
        var active = document.activeElement;
        if (!active || !root.contains(active)) return null;
        var row = active.closest("[data-key]");
        if (!row) return null;
        var rows = Array.prototype.slice.call(root.querySelectorAll("[data-key]"));
        return { key: row.dataset.key, part: active.dataset.part || "", index: rows.indexOf(row) };
    }

    function restoreFocus(root, saved) {
        if (!saved) return false;
        var rows = Array.prototype.slice.call(root.querySelectorAll("[data-key]"));
        if (!rows.length) return false;
        var target = rows[saved.index] && rows[saved.index].dataset.key === saved.key ? rows[saved.index] : null;
        if (!target) target = rows.filter(function (r) { return r.dataset.key === saved.key; })[0];
        if (!target) target = rows[Math.min(Math.max(saved.index, 0), rows.length - 1)];
        var parts = Array.prototype.slice.call(target.querySelectorAll("[data-part]"));
        if (target.dataset.part !== undefined) parts.unshift(target);
        var control = parts.filter(function (p) { return p.dataset.part === saved.part; })[0] || parts[0];
        if (!control) return false;
        control.focus();
        return true;
    }

    function markDemo() {
        if (!isDemo || document.querySelector(".demo-note")) return;
        var note = el("div", "demo-note");
        note.appendChild(el("span", null, "Demo mode: sample data only"));
        document.body.insertBefore(note, document.body.firstChild);
    }
    if (document.body) markDemo();
    else document.addEventListener("DOMContentLoaded", markDemo);

    var api = Object.assign({}, pure, {
        isDemo: isDemo,
        call: client.call,
        on: client.on,
        open: openUrl,
        describeError: describeError,
        dom: {
            el: el,
            clear: clear,
            icon: icon,
            avatar: avatar,
            linkTo: linkTo,
            notice: notice,
            showError: showError,
            showLoading: showLoading,
            toast: toast,
            debounce: debounce,
            captureFocus: captureFocus,
            restoreFocus: restoreFocus,
        },
    });
    delete api.createClient;
    delete api.createDemo;
    window.gergur = api;
})();
