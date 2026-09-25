// Behaviour tests for bridge.js, the channel and helpers behind the browser's own pages
// (new tab, History, Downloads, Bookmarks). Run: node src/Gergur/Assets/bridge.test.js
//
// The date helpers run under three time zones, one with no daylight saving and two that
// change clocks on different weekends, because "which day is this visit" is exactly the
// sort of thing that is right on the machine it was written on and wrong everywhere else.
"use strict";

const assert = require("node:assert");
const fs = require("node:fs");
const path = require("node:path");
const bridge = require(path.join(__dirname, "bridge.js"));

// The real timers, kept before anything below can stand in for them: the runner needs one
// that actually fires to notice a test whose promise never settles.
const realSetTimeout = setTimeout;
const realClearTimeout = clearTimeout;

const tests = [];
function test(name, body) { tests.push({ name, body }); }

const ZONES = ["Europe/Berlin", "America/Los_Angeles", "Asia/Kolkata"];
function inZones(body) {
    const before = process.env.TZ;
    try {
        for (const zone of ZONES) {
            process.env.TZ = zone;
            try { body(zone); }
            catch (e) { e.message = "[" + zone + "] " + e.message; throw e; }
        }
    } finally {
        if (before === undefined) delete process.env.TZ;
        else process.env.TZ = before;
    }
}

// Timers the test moves by hand, so a five second timeout takes no time at all.
function fakeTimers() {
    let now = 0, next = 1;
    const due = new Map();
    return {
        setTimeout(fn, ms) { const id = next++; due.set(id, { at: now + ms, fn }); return id; },
        clearTimeout(id) { due.delete(id); },
        advance(ms) {
            now += ms;
            for (const [id, t] of [...due].sort((a, b) => a[1].at - b[1].at)) {
                if (t.at <= now) { due.delete(id); t.fn(); }
            }
        },
        get pending() { return due.size; },
    };
}

function settled(promise) {
    return promise.then(v => ({ ok: true, value: v }), e => ({ ok: false, error: e }));
}

const tick = () => new Promise(resolve => setImmediate(resolve));

// ---------------------------------------------------------------- days and times

test("a visit is labelled by the local day it happened on", () => inZones(() => {
    const now = new Date(2026, 8, 24, 15, 0);                        // Thursday
    assert.strictEqual(bridge.dayLabel(new Date(2026, 8, 24, 0, 1), now), "Today");
    assert.strictEqual(bridge.dayLabel(new Date(2026, 8, 23, 23, 59), now), "Yesterday");
    assert.strictEqual(bridge.dayLabel(new Date(2026, 8, 22, 12, 0), now), "Tuesday");
    assert.strictEqual(bridge.dayLabel(new Date(2026, 8, 18, 12, 0), now), "Friday");
    assert.strictEqual(bridge.dayLabel(new Date(2026, 8, 17, 12, 0), now), "Thursday, 17 September",
        "a week ago is a date, or it would read as today's weekday");
    assert.strictEqual(bridge.dayLabel(new Date(2025, 11, 31, 12, 0), now), "Wednesday, 31 December 2025");
}));

test("the browser's UTC timestamps are shown in local time", () => {
    // 23:30 UTC is already tomorrow in Berlin and still this afternoon in Los Angeles.
    const visited = "2026-09-23T23:30:00.0000000Z";                  // the host's "O" format
    const cases = { "Europe/Berlin": ["Today", "01:30"], "America/Los_Angeles": ["Yesterday", "16:30"] };
    const before = process.env.TZ;
    try {
        for (const zone of Object.keys(cases)) {
            process.env.TZ = zone;
            const now = new Date(2026, 8, 24, 12, 0);
            assert.strictEqual(bridge.dayLabel(visited, now), cases[zone][0], zone);
            assert.strictEqual(bridge.timeLabel(visited), cases[zone][1], zone);
        }
    } finally {
        if (before === undefined) delete process.env.TZ; else process.env.TZ = before;
    }
});

test("a day with a clock change still counts as one day", () => {
    const before = process.env.TZ;
    try {
        // Europe falls back on 25 October 2026, the US on 1 November: 25 hour days.
        process.env.TZ = "Europe/Berlin";
        let now = new Date(2026, 9, 26, 0, 30);
        assert.strictEqual(bridge.dayLabel(new Date(2026, 9, 25, 23, 30), now), "Yesterday");
        assert.strictEqual(bridge.dayLabel(new Date(2026, 9, 24, 0, 30), now), "Saturday");
        process.env.TZ = "America/Los_Angeles";
        now = new Date(2026, 10, 2, 0, 15);
        assert.strictEqual(bridge.dayLabel(new Date(2026, 10, 1, 0, 5), now), "Yesterday");
        // And springing forward: the 23 hour day in March.
        now = new Date(2026, 2, 9, 23, 50);
        assert.strictEqual(bridge.dayLabel(new Date(2026, 2, 8, 0, 10), now), "Yesterday");
        assert.strictEqual(bridge.dayLabel(new Date(2026, 2, 9, 0, 10), now), "Today");
    } finally {
        if (before === undefined) delete process.env.TZ; else process.env.TZ = before;
    }
});

test("a date that cannot be read is said to be unknown, not shown as today", () => {
    const now = new Date(2026, 8, 24, 12, 0);
    assert.strictEqual(bridge.dayLabel("not a date", now), "Unknown date");
    assert.strictEqual(bridge.dayLabel(undefined, now), "Unknown date");
    assert.strictEqual(bridge.dayLabel("", now), "Unknown date");
    assert.strictEqual(bridge.timeLabel("nope"), "");
    assert.strictEqual(bridge.dayKey(null), "");
});

test("a date in the future is shown as a date, not as today", () => inZones(() => {
    const now = new Date(2026, 8, 24, 12, 0);
    assert.strictEqual(bridge.dayLabel(new Date(2026, 8, 25, 9, 0), now), "Friday, 25 September");
}));

test("times are local, 24 hour and padded", () => inZones(() => {
    assert.strictEqual(bridge.timeLabel(new Date(2026, 0, 5, 7, 3)), "07:03");
    assert.strictEqual(bridge.timeLabel(new Date(2026, 0, 5, 23, 59)), "23:59");
    assert.strictEqual(bridge.timeLabel(new Date(2026, 0, 5, 0, 0)), "00:00");
}));

test("the short form says when a download started without taking a line", () => inZones(() => {
    const now = new Date(2026, 8, 24, 15, 0);
    assert.strictEqual(bridge.shortWhen(new Date(2026, 8, 24, 9, 5), now), "09:05");
    assert.strictEqual(bridge.shortWhen(new Date(2026, 8, 23, 9, 5), now), "Yesterday");
    assert.strictEqual(bridge.shortWhen(new Date(2026, 8, 21, 9, 5), now), "Monday");
    assert.strictEqual(bridge.shortWhen(new Date(2026, 8, 2, 9, 5), now), "2 Sep");
    assert.strictEqual(bridge.shortWhen(new Date(2025, 8, 2, 9, 5), now), "2 Sep 2025");
    assert.strictEqual(bridge.shortWhen("garbage", now), "");
}));

test("history is grouped by day in the order it came, newest first", () => inZones(() => {
    const now = new Date(2026, 8, 24, 15, 0);
    const at = (d, h) => new Date(2026, 8, d, h, 0).toISOString();
    const items = [
        { id: 1, visitedUtc: at(24, 14) },
        { id: 2, visitedUtc: at(24, 1) },
        { id: 3, visitedUtc: at(23, 22) },
        { id: 4, visitedUtc: "broken" },
        { id: 5, visitedUtc: at(20, 10) },
    ];
    const groups = bridge.groupByDay(items, i => i.visitedUtc, now);
    assert.deepStrictEqual(groups.map(g => g.label), ["Today", "Yesterday", "Unknown date", "Sunday"]);
    assert.deepStrictEqual(groups.map(g => g.items.map(i => i.id)), [[1, 2], [3], [4], [5]]);
    assert.deepStrictEqual(groups.map(g => g.key), ["2026-09-24", "2026-09-23", "unknown", "2026-09-20"]);
}));

test("a day that turns up again later joins its own group rather than starting a new one", () => {
    const now = new Date(2026, 8, 24, 15, 0);
    const items = [new Date(2026, 8, 24, 9), new Date(2026, 8, 23, 9), new Date(2026, 8, 24, 8)];
    const groups = bridge.groupByDay(items, i => i, now);
    assert.deepStrictEqual(groups.map(g => [g.label, g.items.length]), [["Today", 2], ["Yesterday", 1]]);
});

// ---------------------------------------------------------------- sizes and progress

test("sizes read the way the browser's own status text writes them", () => {
    const MB = 1048576;
    assert.strictEqual(bridge.formatBytes(0), "0 B");
    assert.strictEqual(bridge.formatBytes(1023), "1023 B");
    assert.strictEqual(bridge.formatBytes(1024), "1 KB");
    assert.strictEqual(bridge.formatBytes(1536), "1.5 KB");
    assert.strictEqual(bridge.formatBytes(Math.round(12.4 * MB)), "12.4 MB");
    assert.strictEqual(bridge.formatBytes(80 * MB), "80 MB");
    assert.strictEqual(bridge.formatBytes(1.5 * 1024 * MB), "1.5 GB");
    assert.strictEqual(bridge.formatBytes(3 * 1024 * 1024 * MB), "3072 GB", "GB is the largest unit, as in DownloadItem.Size");
    // DownloadItem.Size formats "0.#", which rounds 1023.96 KB up to 1024 KB rather than
    // moving to MB. Saying something different from the status line beside it is worse.
    assert.strictEqual(bridge.formatBytes(1048535), "1024 KB");
});

test("a size that is not a size is shown as nothing rather than NaN", () => {
    assert.strictEqual(bridge.formatBytes(-1), "");
    assert.strictEqual(bridge.formatBytes(NaN), "");
    assert.strictEqual(bridge.formatBytes(undefined), "");
    assert.strictEqual(bridge.formatBytes(Infinity), "");
});

test("progress is a fraction, and unknown when the size is", () => {
    assert.strictEqual(bridge.progressOf(25, 100), 0.25);
    assert.strictEqual(bridge.progressOf(0, 0), null, "a total of 0 means the browser does not know it");
    assert.strictEqual(bridge.progressOf(10, -1), null);
    assert.strictEqual(bridge.progressOf(150, 100), 1, "a server that sent more than it said is still done, not 150%");
    assert.strictEqual(bridge.progressOf(-5, 100), 0);
});

// ---------------------------------------------------------------- addresses and titles

test("the host shown is the site, without www.", () => {
    assert.strictEqual(bridge.hostOf("https://www.example.com/a?b#c"), "example.com");
    assert.strictEqual(bridge.hostOf("http://Example.COM:8080/"), "example.com");
    assert.strictEqual(bridge.hostOf("https://wwwexample.com/"), "wwwexample.com");
    assert.strictEqual(bridge.hostOf("https://news.www.example.com/"), "news.www.example.com");
    assert.strictEqual(bridge.hostOf("not a url"), "");
    assert.strictEqual(bridge.hostOf(""), "");
    assert.strictEqual(bridge.hostOf(null), "");
});

test("a lookalike host is shown in the form that cannot pass for another site", () => {
    // Cyrillic "а" in place of the Latin one: shown as punycode, never as "apple.com".
    assert.strictEqual(bridge.hostOf("https://\u0430pple.com/"), "xn--pple-43d.com");
});

test("addresses without a host still say what they are", () => {
    assert.strictEqual(bridge.siteOf("file:///C:/Users/me/notes.html"), "Local file");
    assert.strictEqual(bridge.siteOf("data:text/html,hi"), "data");
    assert.strictEqual(bridge.siteOf("about:blank"), "about");
    assert.strictEqual(bridge.siteOf("https://github.com/x"), "github.com");
    assert.strictEqual(bridge.siteOf("%%%"), "");
});

test("a page with no title is shown by its address", () => {
    assert.strictEqual(bridge.displayTitle("  ", "https://a.example/"), "https://a.example/");
    assert.strictEqual(bridge.displayTitle(null, "https://a.example/"), "https://a.example/");
    assert.strictEqual(bridge.displayTitle(" Hello ", "https://a.example/"), "Hello");
});

test("an enormous title or address is cut rather than laid out whole", () => {
    const huge = "data:text/plain," + "x".repeat(100000);
    const shown = bridge.displayTitle("", huge);
    assert.ok(shown.length <= 400, "shown " + shown.length + " characters");
    assert.ok(shown.endsWith("\u2026"));
});

test("cutting text never splits a character in two", () => {
    const s = "ab\u{1F600}cd";                                          // the emoji is two code units
    const cut = bridge.clip(s, 4);
    assert.ok(!/[\ud800-\udbff]\u2026$/.test(cut), "left half a surrogate pair: " + JSON.stringify(cut));
    assert.strictEqual(bridge.clip("short", 10), "short");
});

test("the avatar letter comes from the title, then the site", () => {
    assert.strictEqual(bridge.letterOf("MDN Web Docs", "https://developer.mozilla.org/"), "M", "the title the person reads, not the host");
    assert.strictEqual(bridge.letterOf("", "https://www.github.com/"), "G", "the site when there is no title");
    assert.strictEqual(bridge.letterOf("\u00e9lan vital", "file:///C:/x.html"), "\u00c9");
    assert.strictEqual(bridge.letterOf("  ...", "about:blank"), "#");
    assert.strictEqual(bridge.letterOf("", "https://123.example/"), "1");
    assert.strictEqual(bridge.letterOf("stra\u00dfe", "file:///x"), "S", "one letter, even where upper case makes two");
});

test("one site always gets one colour, and sites differ", () => {
    const a = bridge.hueOf("github.com");
    assert.strictEqual(bridge.hueOf("github.com"), a);
    assert.ok(a >= 0 && a < 360 && Number.isInteger(a));
    const hues = new Set(["github.com", "wikipedia.org", "youtube.com", "bbc.co.uk", "example.com"].map(bridge.hueOf));
    assert.ok(hues.size >= 4, "five sites landed on " + hues.size + " hues");
    assert.ok(Number.isInteger(bridge.hueOf("")));
});

test("filtering needs every word, in the title or the address, in any case", () => {
    const b = { title: "Rust Book", url: "https://doc.rust-lang.org/book/" };
    assert.ok(bridge.matches(b, ""));
    assert.ok(bridge.matches(b, "  "));
    assert.ok(bridge.matches(b, "rust"));
    assert.ok(bridge.matches(b, "BOOK doc.rust"));
    assert.ok(!bridge.matches(b, "rust python"));
    assert.ok(bridge.matches({ title: null, url: "https://x.example/" }, "x.example"));
});

test("Ctrl, Cmd and the middle button open in a new tab; a plain click does not", () => {
    assert.strictEqual(bridge.wantsNewTab({ button: 0 }), false);
    assert.strictEqual(bridge.wantsNewTab({ button: 0, shiftKey: true }), false);
    assert.strictEqual(bridge.wantsNewTab({ button: 0, ctrlKey: true }), true);
    assert.strictEqual(bridge.wantsNewTab({ button: 0, metaKey: true }), true);
    assert.strictEqual(bridge.wantsNewTab({ button: 1 }), true);
});

test("a file's badge is its short extension", () => {
    assert.strictEqual(bridge.extensionOf("Quarterly report.pdf"), "PDF");
    assert.strictEqual(bridge.extensionOf("archive.tar.gz"), "GZ");
    assert.strictEqual(bridge.extensionOf(".bashrc"), "");
    assert.strictEqual(bridge.extensionOf("README"), "");
    assert.strictEqual(bridge.extensionOf("weird.extension"), "");
    assert.strictEqual(bridge.extensionOf("C:\\dir.v2\\file"), "");
    assert.strictEqual(bridge.extensionOf(null), "");
});

test("the browser's refusals read as sentences", () => {
    assert.strictEqual(bridge.sentence("that address cannot be opened from here"), "That address cannot be opened from here.");
    assert.strictEqual(bridge.sentence("Already one."), "Already one.");
    assert.strictEqual(bridge.sentence(""), "");
    assert.strictEqual(bridge.sentence(undefined), "");
});

test("demo mode is ?demo=1 and nothing else", () => {
    assert.strictEqual(bridge.demoRequested("?demo=1"), true);
    assert.strictEqual(bridge.demoRequested("?x=2&demo=1"), true);
    assert.strictEqual(bridge.demoRequested("?demo=0"), false);
    assert.strictEqual(bridge.demoRequested("?demo=10"), false);
    assert.strictEqual(bridge.demoRequested(""), false);
});

// ---------------------------------------------------------------- the channel

test("a request goes out in the agreed shape, each with its own id", () => {
    const sent = [];
    const client = bridge.createClient(m => sent.push(m), fakeTimers(), 5000);
    client.call("history.list", { max: 3 });
    client.call("history.clear");
    assert.deepStrictEqual(sent[0], { gergur: 1, id: sent[0].id, op: "history.list", args: { max: 3 } });
    assert.deepStrictEqual(sent[1].args, {}, "no args still sends an object");
    assert.ok(Number.isInteger(sent[0].id) && sent[0].id !== sent[1].id);
});

test("an answer resolves the request it names, not another", async () => {
    const sent = [];
    const client = bridge.createClient(m => sent.push(m), fakeTimers(), 5000);
    const first = settled(client.call("a"));
    const second = settled(client.call("b"));
    client.receive({ id: sent[1].id, ok: true, result: "for b" });
    client.receive({ id: sent[0].id, ok: true, result: ["for a"] });
    assert.deepStrictEqual(await first, { ok: true, value: ["for a"] });
    assert.deepStrictEqual(await second, { ok: true, value: "for b" });
});

test("a refusal rejects with the browser's reason, as a sentence", async () => {
    const sent = [];
    const client = bridge.createClient(m => sent.push(m), fakeTimers(), 5000);
    const answer = settled(client.call("open", { url: "javascript:alert(1)" }));
    client.receive({ id: sent[0].id, ok: false, error: "that address cannot be opened from here" });
    const outcome = await answer;
    assert.strictEqual(outcome.ok, false);
    assert.strictEqual(outcome.error.message, "That address cannot be opened from here.");
    assert.strictEqual(outcome.error.code, "refused");
});

test("a refusal with no reason still says something", async () => {
    const sent = [];
    const client = bridge.createClient(m => sent.push(m), fakeTimers(), 5000);
    const answer = settled(client.call("x"));
    client.receive({ id: sent[0].id, ok: false });
    assert.strictEqual((await answer).error.message, "Gergur could not do that.");
});

test("a request nobody answers fails after five seconds, and a late answer is ignored", async () => {
    const timers = fakeTimers();
    const sent = [];
    const client = bridge.createClient(m => sent.push(m), timers, bridge.TIMEOUT_MS);
    const answer = settled(client.call("history.list"));
    timers.advance(4999);
    await tick();
    let finished = false;
    answer.then(() => { finished = true; });
    await tick();
    assert.strictEqual(finished, false, "gave up before five seconds");
    timers.advance(1);
    const outcome = await answer;
    assert.strictEqual(outcome.ok, false);
    assert.strictEqual(outcome.error.code, "timeout");
    assert.match(outcome.error.message, /did not answer within 5 seconds/);
    assert.strictEqual(client.pendingCount(), 0, "a timed-out request is still waiting for its answer");
    client.receive({ id: sent[0].id, ok: true, result: [] });   // too late: must not throw
});

test("an answered request leaves no timer behind", async () => {
    const timers = fakeTimers();
    const sent = [];
    const client = bridge.createClient(m => sent.push(m), timers, 5000);
    const answer = client.call("x");
    assert.strictEqual(timers.pending, 1);
    client.receive({ id: sent[0].id, ok: true, result: 1 });
    await answer;
    assert.strictEqual(timers.pending, 0);
    assert.strictEqual(client.pendingCount(), 0, "an answered request is still waiting");
});

test("a page outside the browser fails at once, and says why", async () => {
    const timers = fakeTimers();
    const client = bridge.createClient(() => {
        const e = new Error("This page only works inside Gergur.");
        e.code = "no-host";
        throw e;
    }, timers, 5000);
    const outcome = await settled(client.call("bookmarks.list"));
    assert.strictEqual(outcome.error.code, "no-host");
    assert.strictEqual(timers.pending, 0, "a request that never left still held a timer");
});

test("pushes reach their listeners, and a listener can leave", () => {
    const client = bridge.createClient(() => {}, fakeTimers(), 5000);
    const seen = [];
    const off = client.on("downloads", m => seen.push(m.items.length));
    client.on("bookmarks", () => seen.push("bookmarks"));
    client.receive({ event: "downloads", items: [1, 2] });
    client.receive({ event: "bookmarks" });
    off();
    client.receive({ event: "downloads", items: [1] });
    assert.deepStrictEqual(seen, [2, "bookmarks"]);
});

test("an answer sent as a JSON string is read the same way", async () => {
    const sent = [];
    const client = bridge.createClient(m => sent.push(m), fakeTimers(), 5000);
    const answer = client.call("x");
    client.receive(JSON.stringify({ id: sent[0].id, ok: true, result: { removed: 2 } }));
    assert.deepStrictEqual(await answer, { removed: 2 });
});

test("anything else arriving on the channel is ignored", () => {
    const client = bridge.createClient(() => {}, fakeTimers(), 5000);
    for (const junk of [null, 42, "GERGUR_ERR:x", "{", [], { id: 999, ok: true }, { event: 5 }])
        client.receive(junk);
});

// ---------------------------------------------------------------- demo backend

function demo(extra) {
    const pushes = [];
    const opened = [];
    const backend = bridge.createDemo(Object.assign({
        now: () => new Date(2026, 8, 24, 15, 0).getTime(),
        push: e => pushes.push(e),
        onOpen: (url, newTab) => opened.push([url, newTab]),
    }, extra));
    let id = 0;
    const ask = (op, args) => backend.handle({ gergur: 1, id: ++id, op, args: args || {} });
    return { backend, ask, pushes, opened };
}

test("demo: a dozen history entries across today, yesterday, this week and before", () => {
    const { ask } = demo();
    const reply = ask("history.list", {});
    assert.strictEqual(reply.ok, true);
    const items = reply.result;
    assert.ok(items.length >= 12, items.length + " entries");
    const labels = bridge.groupByDay(items, i => i.visitedUtc, new Date(2026, 8, 24, 15, 0)).map(g => g.label);
    assert.strictEqual(labels[0], "Today");
    assert.ok(labels.includes("Yesterday"));
    assert.ok(labels.some(l => ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"].includes(l)));
    assert.ok(labels.some(l => l.includes(",")), "nothing older than a week");
    const times = items.map(i => Date.parse(i.visitedUtc));
    assert.deepStrictEqual(times, [...times].sort((a, b) => b - a), "not newest first");
    for (const i of items) assert.deepStrictEqual(Object.keys(i).sort(), ["title", "url", "visitedUtc"]);
});

test("demo: history search and max behave like the browser's", () => {
    const { ask } = demo();
    assert.ok(ask("history.list", { q: "wikipedia" }).result.every(i => /wikipedia/i.test(i.title + i.url)));
    assert.strictEqual(ask("history.list", { q: "WIKIPEDIA" }).result.length, 1);
    assert.strictEqual(ask("history.list", { max: 3 }).result.length, 3);
    assert.strictEqual(ask("history.list", { q: "zzz-nothing" }).result.length, 0);
});

test("demo: removing and clearing history", () => {
    const { ask } = demo();
    const hn = "https://news.ycombinator.com/";
    const visits = ask("history.list", {}).result.filter(i => i.url === hn).length;
    assert.ok(visits >= 2, "the sample should visit one address twice");
    assert.deepStrictEqual(ask("history.remove", { urls: [hn] }).result, { removed: visits });
    assert.ok(!ask("history.list", {}).result.some(i => i.url === hn));
    assert.deepStrictEqual(ask("history.clear", {}).result, {});
    assert.deepStrictEqual(ask("history.list", {}).result, []);
});

test("demo: six bookmarks, renamed and removed with a push each time", () => {
    const { ask, pushes } = demo();
    const list = ask("bookmarks.list").result;
    assert.strictEqual(list.length, 6);
    assert.ok(ask("bookmarks.rename", { url: list[0].url, title: "  Code  " }).ok);
    assert.strictEqual(ask("bookmarks.list").result[0].title, "Code");
    assert.strictEqual(ask("bookmarks.rename", { url: list[0].url, title: "   " }).ok, false, "a blank name must be refused, as the browser does");
    assert.ok(ask("bookmarks.remove", { url: list[1].url }).ok);
    assert.strictEqual(ask("bookmarks.list").result.length, 5);
    assert.deepStrictEqual(pushes, [{ event: "bookmarks" }, { event: "bookmarks" }]);
});

test("demo: a deleted bookmark comes back where it was", () => {
    const { ask } = demo();
    const list = ask("bookmarks.list").result;
    ask("bookmarks.remove", { url: list[2].url });
    assert.ok(ask("bookmarks.add", { url: list[2].url, title: list[2].title, index: 2 }).ok);
    assert.deepStrictEqual(ask("bookmarks.list").result.map(b => b.url), list.map(b => b.url));
    assert.strictEqual(ask("bookmarks.add", { url: list[0].url, title: "dup" }).ok, false);
});

test("demo: four downloads, one in each state and one of them running", () => {
    const { ask } = demo();
    const items = ask("downloads.list").result;
    assert.strictEqual(items.length, 4);
    const states = items.map(i => i.state);
    for (const s of ["inProgress", "completed", "interrupted"]) assert.ok(states.includes(s), "no " + s);
    for (const i of items) {
        assert.deepStrictEqual(Object.keys(i).sort(),
            ["fileName", "id", "received", "startedUtc", "state", "status", "total", "url"]);
        assert.ok(i.status.length > 0, "empty status for " + i.fileName);
    }
});

test("demo: the running download moves, pushes, and finishes", () => {
    const { backend, ask, pushes } = demo();
    const start = ask("downloads.list").result.find(i => i.state === "inProgress");
    assert.ok(backend.tick());
    const moved = pushes[0].items.find(i => i.id === start.id);
    assert.strictEqual(pushes[0].event, "downloads");
    assert.ok(moved.received > start.received);
    assert.match(moved.status, / of 80 MB$/);
    let guard = 0;
    while (backend.tick() && guard++ < 1000) { /* run it to the end */ }
    const done = pushes[pushes.length - 1].items.find(i => i.id === start.id);
    assert.strictEqual(done.state, "completed");
    assert.strictEqual(done.received, done.total);
    assert.strictEqual(done.status, "80 MB");
});

test("demo: cancel, clear finished, and a download that is gone", () => {
    const { ask } = demo();
    const running = ask("downloads.list").result.find(i => i.state === "inProgress");
    assert.ok(ask("downloads.cancel", { id: running.id }).ok);
    assert.strictEqual(ask("downloads.list").result.find(i => i.id === running.id).state, "interrupted");
    assert.ok(ask("downloads.clearFinished").ok);
    assert.deepStrictEqual(ask("downloads.list").result, []);
    const gone = ask("downloads.open", { id: running.id });
    assert.deepStrictEqual(gone, { id: gone.id, ok: false, error: "that download is no longer in the list" });
});

test("demo: open refuses what the browser refuses", () => {
    const { ask, opened } = demo();
    assert.ok(ask("open", { url: "https://example.com/", newTab: true }).ok);
    assert.ok(ask("open", { url: "file:///C:/x.html" }).ok);
    assert.strictEqual(ask("open", { url: "javascript:alert(1)" }).ok, false);
    assert.strictEqual(ask("open", { url: "data:text/html,x" }).ok, false);
    assert.strictEqual(ask("open", {}).error, "url is required");
    assert.deepStrictEqual(opened, [["https://example.com/", true], ["file:///C:/x.html", false]]);
});

test("demo: an unknown op is refused, and every reply names its request", () => {
    const { backend } = demo();
    assert.deepStrictEqual(backend.handle({ gergur: 1, id: 77, op: "history.nuke", args: {} }),
        { id: 77, ok: false, error: "there is no history.nuke" });
    assert.strictEqual(backend.handle({ gergur: 1, id: 78, op: "bookmarks.list" }).id, 78);
});

test("demo: answers are copies, so a page cannot change the sample by editing them", () => {
    const { ask } = demo();
    ask("bookmarks.list").result[0].title = "changed";
    assert.notStrictEqual(ask("bookmarks.list").result[0].title, "changed");
});

test("demo: the running download is moved by a timer and stops when it is done", () => {
    const intervals = [];
    const { ask } = demo({ timers: {
        setInterval(fn, ms) { intervals.push({ fn, ms, live: true }); return intervals.length - 1; },
        clearInterval(i) { intervals[i].live = false; },
    } });
    ask("downloads.list");
    ask("downloads.list");
    assert.strictEqual(intervals.length, 1, "asking twice started two timers");
    let guard = 0;
    while (intervals[0].live && guard++ < 1000) intervals[0].fn();
    assert.strictEqual(intervals[0].live, false, "the timer outlived the download");
});

// ---------------------------------------------------------------- the pages themselves

const PAGES = ["home.html", "history.html", "downloads.html", "bookmarks.html"];
const read = name => fs.readFileSync(path.join(__dirname, name), "utf8");

test("no page builds markup out of data", () => {
    // Titles and addresses are written by whatever site was visited. Every one of these
    // parses a string as HTML, and there is no use for any of them here.
    for (const name of PAGES.concat(["bridge.js"])) {
        const source = read(name);
        for (const sink of [/\.innerHTML\b/, /\.outerHTML\b/, /insertAdjacentHTML/, /document\.write/,
            /\bsrcdoc\b/, /\beval\s*\(/, /new\s+Function\s*\(/, /createContextualFragment/, /DOMParser/,
            // The ways round the dotted form: newer HTML sinks, and the property by bracket.
            /\bsetHTMLUnsafe\b|\bparseHTMLUnsafe\b/, /\[[^\]\n]*["'`](?:inner|outer)/]) {
            assert.ok(!sink.test(source), name + " uses " + sink);
        }
    }
});

test("no page reaches outside the Assets folder", () => {
    for (const name of PAGES) {
        const source = read(name);
        assert.ok(!/(?:href|src)\s*=\s*["']?\s*(?:[a-z]+:)?\/\//i.test(source), name + " loads something by absolute url");
        assert.ok(!/@import|\burl\s*\(/i.test(source), name + " pulls in a resource from CSS");
        assert.ok(/http-equiv="Content-Security-Policy" content="default-src 'none';/.test(source), name + " has no content security policy");
    }
    assert.ok(!/@import|\burl\s*\(/i.test(read("pages.css")), "pages.css pulls in a resource");
});

test("every page asks for the icon exactly as the new tab page does", () => {
    const icon = '<link rel="icon" href="favicon.png?v=235fd424">';
    for (const name of PAGES) {
        const source = read(name);
        assert.strictEqual(source.split('rel="icon"').length - 1, 1, name + " has more or fewer than one icon link");
        assert.ok(source.includes(icon), name + " does not carry the icon's current token");
    }
    assert.ok(read("home.html").includes('src="logo.png"'), "the new tab page lost its logo");
});

test("no page opens anything with an href or by setting location", () => {
    // Opening goes through the browser (gergur.open) so its own rules apply to it.
    for (const name of PAGES.concat(["bridge.js"])) {
        const source = read(name);
        assert.ok(!/<a\b[^>]*\bhref\s*=/i.test(source), name + " has an <a href>");
        // Nor an href set from script, by property or by attribute.
        assert.ok(!/\.href\s*=[^=]/.test(source), name + " sets an href");
        assert.ok(!/setAttribute\(\s*["']href/.test(source), name + " sets an href attribute");
        assert.ok(!/location\s*(?:\.href\s*)?=[^=]|location\.(?:assign|replace)\s*\(|window\.open\s*\(/.test(source),
            name + " navigates by itself");
    }
});

test("no dashes the user does not want, anywhere in the pages", () => {
    for (const name of PAGES.concat(["bridge.js", "bridge.test.js", "pages.css"])) {
        const source = read(name);
        assert.ok(!/[\u2013\u2014]/.test(source), name + " contains an en or em dash");
    }
});

// ---------------------------------------------------------------- run

(async () => {
    // Failed unless the run reaches its end. A test whose promise never settles lets node
    // drain its event loop and exit, and it exited 0, which the checks gate read as a pass:
    // breaking the channel's resolve or its timeout ran half the tests and printed nothing.
    process.exitCode = 1;
    let failures = 0;
    console.log("bridge");
    for (const t of tests) {
        let timer;
        try {
            // And each test races a real timer, so a hung one is named rather than silent.
            await Promise.race([
                Promise.resolve().then(t.body),
                new Promise((_, reject) => { timer = realSetTimeout(() => reject(new Error("never settled within 2 s")), 2000); }),
            ]);
            console.log("  ok   " + t.name);
        } catch (e) {
            failures++;
            console.log("  FAIL " + t.name + "\n       " + e.message);
        } finally {
            realClearTimeout(timer);
        }
    }
    console.log(failures === 0 ? "bridge: OK (" + tests.length + " tests)" : "bridge: " + failures + " failed");
    process.exit(failures === 0 ? 0 : 1);
})();
