// Behaviour tests for the ad-payload pruning in adblock.js, run by .claude/checks.sh.
//
// These exist because the pruning is the whole of the YouTube blocking: the ads come
// from the same hosts as the video, so nothing at the network layer can touch them,
// and the only defence is that the player never sees the ad payload. A test that
// asserted the script "contains a fetch hook" would not have noticed that the old
// version pruned four keys at the top level and walked no further.
"use strict";

const assert = require("node:assert");
const path = require("node:path");
const { prune, isAdObject, pressable, interrupting, GIVE_UP_ON_SKIP_AFTER } =
    require(path.join(__dirname, "adblock.js"));

let failures = 0;
function test(name, body) {
    try {
        body();
        console.log("  ok   " + name);
    } catch (e) {
        failures++;
        console.log("  FAIL " + name + "\n       " + e.message);
    }
}

console.log("adblock: pruning");

// ---------------------------------------------------------------- the watch page

test("a preroll break is removed from the player response", () => {
    const response = {
        streamingData: { formats: [{ itag: 18 }] },
        adPlacements: [{ adPlacementRenderer: { config: {} } }],
        playerAds: [{ playerLegacyDesktopWatchAdsRenderer: {} }],
        adSlots: [{ adSlotRenderer: {} }],
        adBreakHeartbeatParams: "abc",
    };

    prune(response);

    assert.ok(!("adPlacements" in response), "adPlacements survived");
    assert.ok(!("playerAds" in response), "playerAds survived");
    assert.ok(!("adSlots" in response), "adSlots survived");
    assert.ok(!("adBreakHeartbeatParams" in response), "adBreakHeartbeatParams survived");
    assert.deepStrictEqual(response.streamingData.formats, [{ itag: 18 }], "the video was damaged");
});

test("server-stitched ad config is removed", () => {
    // The case host blocking cannot touch at all: the ad is spliced into the same
    // media stream as the video, and the only defence is the player never asking.
    const response = { playerConfig: { daiConfig: { enableDai: true }, audioConfig: {} } };

    prune(response);

    assert.ok(!("daiConfig" in response.playerConfig));
    assert.ok("audioConfig" in response.playerConfig, "unrelated player config was removed");
});

test("an instream video ad is removed from a break's list", () => {
    // The shape a real preroll arrives in: the placement is deleted by key, but the
    // same renderer also turns up on its own in a layout list, so it has to be an ad
    // by identity too rather than only by where it happens to sit.
    const layout = {
        playerBytes: [
            { instreamVideoAdRenderer: { adVideoId: "x" } },
            { adActionInterstitialRenderer: {} },
            { playerOverlayRenderer: { keep: true } },
        ],
    };

    prune(layout);

    assert.deepStrictEqual(layout.playerBytes, [{ playerOverlayRenderer: { keep: true } }]);
});

test("the names of ad renderers are not mistaken for ad renderers", () => {
    // Every watch page carries this list: strings naming the message types the server
    // may send. It is where the renderer list above came from, and pruning it would
    // both do nothing useful and break the page's own preload.
    const response = {
        responseContext: {
            webResponseContextExtensionData: {
                webResponseContextPreloadData: {
                    preloadMessageNames: [
                        "miniplayerRenderer", "adPlacementRenderer", "adSlotRenderer",
                        "instreamVideoAdRenderer", "buttonRenderer",
                    ],
                },
            },
        },
    };
    const names = response.responseContext.webResponseContextExtensionData
        .webResponseContextPreloadData.preloadMessageNames;

    prune(response);

    assert.strictEqual(names.length, 5, "a list of strings was pruned as if it held ads");
});

// ---------------------------------------------------------------- nested payloads

test("an ad in the up-next list is removed, not emptied", () => {
    // This is the shape the old top-level prune walked straight past.
    const next = {
        contents: {
            twoColumnWatchNextResults: {
                secondaryResults: {
                    secondaryResults: {
                        results: [
                            { compactVideoRenderer: { videoId: "a" } },
                            { adSlotRenderer: { adSlotMetadata: {} } },
                            { compactVideoRenderer: { videoId: "b" } },
                        ],
                    },
                },
            },
        },
    };

    prune(next);

    const results = next.contents.twoColumnWatchNextResults.secondaryResults.secondaryResults.results;
    assert.strictEqual(results.length, 2, "the ad was left in the list");
    assert.deepStrictEqual(results.map(r => r.compactVideoRenderer.videoId), ["a", "b"]);
});

test("feed ads are removed from a rich grid", () => {
    const browse = {
        contents: {
            twoColumnBrowseResultsRenderer: {
                tabs: [{
                    tabRenderer: {
                        content: {
                            richGridRenderer: {
                                // trackingParams on every entry, because that is what a
                                // captured ytInitialData looks like. Without it this
                                // fixture passes against an implementation that still
                                // leaves the hole: one bookkeeping string on the wrapper
                                // was enough to make it look like it still held content.
                                contents: [
                                    { richItemRenderer: { content: { videoRenderer: { videoId: "a" } }, trackingParams: "CBUQ3DAYASIT" } },
                                    { richItemRenderer: { content: { adSlotRenderer: {} }, trackingParams: "CBUQ3DAYAiIT" } },
                                    { richSectionRenderer: { content: { inFeedAdLayoutRenderer: {} }, trackingParams: "CBYQ3DAYAyIT" } },
                                    { richItemRenderer: { content: { videoRenderer: { videoId: "b" } }, trackingParams: "CBUQ3DAYBCIT" } },
                                ],
                            },
                        },
                    },
                }],
            },
        },
    };

    prune(browse);

    const grid = browse.contents.twoColumnBrowseResultsRenderer.tabs[0]
        .tabRenderer.content.richGridRenderer.contents;
    // The two real videos survive and the ad wrappers are gone from the list, rather
    // than left behind as {content:{}}. This is the shape that matters most: the ad is
    // two levels down inside an ordinary wrapper, so removing only the ad leaves an
    // entry the feed still lays out and no cosmetic rule can recognise, which is the
    // hole in the grid. The old assertion counted the survivors and never noticed the
    // list was still four long.
    assert.strictEqual(grid.length, 2, "an emptied ad wrapper was left in the grid");
    assert.deepStrictEqual(
        grid.map(x => x.richItemRenderer.content.videoRenderer.videoId), ["a", "b"]);
    const json = JSON.stringify(browse);
    assert.ok(json.indexOf("adSlotRenderer") < 0, "an ad renderer survived");
    assert.ok(json.indexOf("inFeedAdLayoutRenderer") < 0, "an in-feed ad survived");
});

test("a grid ad carrying its own coordinates still goes", () => {
    // Captured from a real home feed. These wrappers came back with rowIndex, colIndex
    // and onFocusEffect beside the emptied content, and every one of those counted as
    // something worth keeping, so three holes stayed in the grid on a page the unit
    // tests all passed for.
    const grid = {
        contents: [
            {
                richItemRenderer: {
                    content: { adSlotRenderer: {} },
                    trackingParams: "CBUQ3DAYASIT",
                    onFocusEffect: { effect: {} },
                    rowIndex: 0,
                    colIndex: 2,
                },
            },
            {
                richItemRenderer: {
                    content: { videoRenderer: { videoId: "a" } },
                    trackingParams: "CBUQ3DAYAiIT",
                    rowIndex: 0,
                    colIndex: 3,
                },
            },
        ],
    };

    prune(grid);

    assert.strictEqual(grid.contents.length, 1, "an emptied grid cell was left behind");
    assert.strictEqual(grid.contents[0].richItemRenderer.content.videoRenderer.videoId, "a");
});

test("a shelf whose every entry was an ad goes with them", () => {
    // The other road to the same hole: the ads are list entries, so the first pass
    // splices them and the shelf is left as an empty list inside a wrapper that still
    // renders. Nothing was deleted by key here, so only the splice can mark it.
    const feed = {
        contents: [
            {
                richSectionRenderer: {
                    content: { richShelfRenderer: { contents: [{ adSlotRenderer: {} }, { adSlotRenderer: {} }] } },
                    trackingParams: "CBYQ3DAYASIT",
                },
            },
            { richItemRenderer: { content: { videoRenderer: { videoId: "a" } }, trackingParams: "CBUQ3DAYAiIT" } },
        ],
    };

    prune(feed);

    assert.strictEqual(feed.contents.length, 1, "an emptied shelf was left in the feed");
    assert.strictEqual(feed.contents[0].richItemRenderer.content.videoRenderer.videoId, "a");
});

test("an empty object we did not empty is left alone", () => {
    // The counterpart to the rich grid case. Removing a hollow list entry is only safe
    // because it is limited to ones an ad was taken out of; an entry that arrived empty
    // is not ours to touch, and neither is one that still holds a value.
    const payload = {
        // The last of these holds nothing but bookkeeping, which does not render, but
        // we did not empty it so it is not ours to remove.
        untouched: [{}, { marker: {} }, { keep: false }, { nested: { deeper: 0 } }, { trackingParams: "CBUQ" }],
        withAnAd: [
            { wrapper: { content: { adSlotRenderer: {} } }, trackingParams: "CBUQ" },
            { wrapper: { content: { videoRenderer: { videoId: "a" } } } },
        ],
    };

    prune(payload);

    assert.strictEqual(payload.untouched.length, 5, "an empty object we never touched was removed");
    assert.strictEqual(payload.withAnAd.length, 1, "the gutted wrapper stayed");
    assert.strictEqual(payload.withAnAd[0].wrapper.content.videoRenderer.videoId, "a");
});

test("a skip button is only pressed when pressing it would do something", () => {
    // YouTube keeps the button in the DOM through the unskippable countdown with its
    // slot hidden, so presence alone had us click an inert button every 300ms and
    // return, never reaching the mute and seek, and the ad played out in full.
    const box = (w, h) => ({ getBoundingClientRect: () => ({ width: w, height: h }) });
    assert.ok(pressable({ disabled: false, offsetParent: {}, ...box(80, 30) }));
    assert.ok(!pressable({ disabled: false, offsetParent: null, ...box(80, 30) }), "a hidden button was pressed");
    assert.ok(!pressable({ disabled: true, offsetParent: {}, ...box(80, 30) }), "a disabled button was pressed");
    assert.ok(!pressable({ disabled: false, offsetParent: {}, ...box(0, 0) }), "a collapsed button was pressed");
    assert.ok(!pressable(null));
    assert.ok(!pressable(undefined));
    // No layout to measure is not a reason to refuse: only a measurement that says
    // there is nothing there.
    assert.ok(pressable({ disabled: false, offsetParent: {} }));
});

test("only an ad that has taken the player over is run to its end", () => {
    // Seeking to the end is how an unskippable break is got rid of, and it is also how
    // you would destroy the thing someone is watching. "ad-showing" is set for an
    // overlay banner too, and for a server-stitched break, and in both of those the
    // element still holds the film: its duration is the film's, and ending it ends the
    // film. Only "ad-interrupting" says the ad has the element to itself.
    const film = { duration: 252 };
    const advert = { duration: 30 };

    assert.ok(interrupting({ className: "html5-video-player ad-showing ad-interrupting" }, advert));
    assert.ok(!interrupting({ className: "html5-video-player ad-showing" }, film),
        "an overlay ad ended the video the viewer was watching");
    assert.ok(!interrupting({ className: "html5-video-player" }, film));
    // A class merely containing the word is not the class.
    assert.ok(!interrupting({ className: "html5-video-player not-ad-interrupting-really" }, advert));
    // Nothing to seek in yet.
    assert.ok(!interrupting({ className: "ad-interrupting" }, { duration: NaN }));
    assert.ok(!interrupting({ className: "ad-interrupting" }, { duration: 0 }));
    assert.ok(!interrupting(null, advert));
    assert.ok(!interrupting({ className: "ad-interrupting" }, null));
});

test("ads in a continuation are removed", () => {
    // Scrolling the feed appends more items through this shape.
    const continuation = {
        onResponseReceivedEndpoints: [{
            appendContinuationItemsAction: {
                continuationItems: [
                    { richItemRenderer: { content: { videoRenderer: { videoId: "a" } } } },
                    { adSlotRenderer: {} },
                ],
            },
        }],
    };

    prune(continuation);

    const items = continuation.onResponseReceivedEndpoints[0]
        .appendContinuationItemsAction.continuationItems;
    assert.strictEqual(items.length, 1);
});

test("search ads are removed", () => {
    const search = {
        contents: {
            twoColumnSearchResultsRenderer: {
                primaryContents: {
                    sectionListRenderer: {
                        contents: [{
                            itemSectionRenderer: {
                                contents: [
                                    { searchPyvRenderer: { ads: [{}] } },
                                    { videoRenderer: { videoId: "a" } },
                                    { promotedSparklesTextSearchRenderer: {} },
                                ],
                            },
                        }],
                    },
                },
            },
        },
    };

    prune(search);

    const items = search.contents.twoColumnSearchResultsRenderer.primaryContents
        .sectionListRenderer.contents[0].itemSectionRenderer.contents;
    assert.strictEqual(items.length, 1);
    assert.strictEqual(items[0].videoRenderer.videoId, "a");
});

test("a Shorts ad is removed from the sequence", () => {
    const reels = {
        entries: [
            { command: { reelWatchEndpoint: { videoId: "a" } } },
            { command: { adSlotRenderer: {} } },
        ],
    };

    prune(reels);

    assert.strictEqual(JSON.stringify(reels).indexOf("adSlotRenderer"), -1);
});

// ---------------------------------------------------------------- not breaking things

test("an ordinary payload is untouched", () => {
    const before = {
        videoDetails: { videoId: "abc", title: "A video about advertising", lengthSeconds: "212" },
        captions: { playerCaptionsTracklistRenderer: { captionTracks: [{ languageCode: "en" }] } },
        microformat: { playerMicroformatRenderer: { category: "Education" } },
    };
    const copy = JSON.parse(JSON.stringify(before));

    prune(copy);

    assert.deepStrictEqual(copy, before, "something that was not an ad was removed");
});

test("fields named after Object.prototype members survive", () => {
    // The lookup tables are keyed by names taken out of remote JSON. Built as {} they
    // inherit constructor, toString and the rest, so every one of these read back as
    // "this is an ad key" and the field was deleted.
    const response = {
        videoDetails: { constructor: "x", toString: "y", valueOf: 1, hasOwnProperty: 2, keep: 3 },
    };

    prune(response);

    assert.deepStrictEqual(response.videoDetails,
        { constructor: "x", toString: "y", valueOf: 1, hasOwnProperty: 2, keep: 3 });
});

test("a list entry with a prototype-named field is not taken for an ad", () => {
    // Same root cause, worse symptom: the entry was spliced out of the list, so a real
    // video disappeared from the feed rather than a field disappearing from it.
    const feed = {
        contents: [
            { videoRenderer: { videoId: "a" }, constructor: "ctor" },
            { videoRenderer: { videoId: "b" } },
        ],
    };

    prune(feed);

    assert.deepStrictEqual(feed.contents.map(x => x.videoRenderer.videoId), ["a", "b"]);
    assert.ok(!isAdObject({ constructor: "x" }), "an ordinary object read as an ad");
    assert.ok(!isAdObject({ toString: "x" }));
});

test("ad renderers that are also ad keys are spliced out, not emptied", () => {
    // adPlacementRenderer and adsEngagementPanelRenderer appear both as a field and as
    // a list entry. Matching them only by key left {} behind, which is the blank gap in
    // the feed this design exists to avoid.
    const feed = {
        contents: [
            { videoRenderer: { videoId: "a" } },
            { adPlacementRenderer: { config: {} } },
            { adsEngagementPanelRenderer: {} },
            { videoRenderer: { videoId: "b" } },
        ],
    };

    prune(feed);

    assert.strictEqual(feed.contents.length, 2, "an ad was emptied instead of removed");
    assert.ok(!feed.contents.some(x => Object.keys(x).length === 0), "an empty shell survived");
});

test("a video whose own metadata mentions ads is kept", () => {
    // The word is not the thing: only the known keys and renderers go.
    const response = { videoDetails: { title: "How adPlacements work", shortDescription: "adSlots explained" } };

    prune(response);

    assert.strictEqual(response.videoDetails.title, "How adPlacements work");
    assert.strictEqual(response.videoDetails.shortDescription, "adSlots explained");
});

test("a payload that refers to itself does not hang", () => {
    const node = { name: "loop", adPlacements: [{}] };
    node.self = node;
    node.children = [node];

    prune(node);   // would spin forever without the cycle guard

    assert.ok(!("adPlacements" in node));
    assert.strictEqual(node.self, node, "the structure was damaged");
});

test("deeply nested payloads do not overflow the stack", () => {
    // Real player responses nest far enough that a recursive walk is a real risk.
    let deep = { adPlacements: [{}] };
    for (let i = 0; i < 20000; i++) deep = { child: deep };

    prune(deep);   // recursion would throw RangeError here

    let cursor = deep;
    while (cursor.child) cursor = cursor.child;
    assert.ok(!("adPlacements" in cursor), "the deepest ad was not reached");
});

test("null and primitives are handled", () => {
    assert.strictEqual(prune(null), null);
    assert.strictEqual(prune(undefined), undefined);
    assert.strictEqual(prune(7), 7);
    assert.strictEqual(prune("adPlacements"), "adPlacements");
});

// ---------------------------------------------------------------- the ad test itself

test("an ad wrapper is recognised and a video is not", () => {
    assert.ok(isAdObject({ adSlotRenderer: {} }));
    assert.ok(isAdObject({ richItemRenderer: {}, promotedVideoRenderer: {} }));
    assert.ok(!isAdObject({ videoRenderer: {} }));
    assert.ok(!isAdObject(null));
    assert.ok(!isAdObject([{ adSlotRenderer: {} }]), "an array is a list, not an ad");
});

// ---------------------------------------------------------------- the browser half

// Everything above tests functions the module seam hands out. The ad skip is not one
// of those: it is a timer reading the page, and it is the part with the most to go
// wrong, because it mutes and seeks a video element the viewer is watching. So run the
// shipped file the way a browser does, against a page small enough to describe, and
// drive its timer by hand.
const vm = require("node:vm");
const fs = require("node:fs");

function loadInBrowser(options) {
    const on = Object.assign({ hostname: "www.youtube.com", href: "https://www.youtube.com/", topFrame: true }, options || {});
    const page = {
        players: [],
        ticks: [],
        styles: [],
        lookups: [],
        delays: [],
        clicks: [],          // capture-phase click listeners the script installed
        navigations: [],     // where it asked the browser to go, and how
        pushed: [],          // what reached the real history API
        storage: (options && options.storageMap) || new Map(),
        replaced: [],   // the ones that asked to replace rather than push
    };
    // Match the way a browser does: every class in the selector has to be on the
    // element. Checking one at a time would let the shipped file drop the
    // ".html5-video-player" half of its selector and still pass here.
    const hasAll = (player, classes) => {
        const own = " " + player.className + " ";
        return classes.every(c => own.indexOf(" " + c + " ") >= 0);
    };
    const find = (selector) => {
        const classes = selector.trim().split(".").filter(Boolean);
        return page.players.find(p => hasAll(p, classes)) || null;
    };

    const document = {
        readyState: "complete",
        head: { appendChild: (s) => page.styles.push(s) },
        documentElement: { appendChild: (s) => page.styles.push(s) },
        createElement: () => ({}),
        addEventListener: (type, fn, capture) => {
            if (type === "click" && capture) page.clicks.push(fn);
        },
        querySelectorAll: () => [],
        querySelector(selector) {
            page.lookups.push(selector);
            // Only the two whole-player selectors resolve here. A selector scoped to a
            // player belongs to that player, and finding one at the document would mean
            // the shipped file had stopped scoping it.
            for (const one of selector.split(",")) {
                const found = find(one);
                if (found) return found;
            }
            return null;
        },
    };

    const context = {
        window: {},
        document,
        location: {
            hostname: on.hostname,
            search: "",
            href: on.href,
            assign: (url) => page.navigations.push(url),
            replace: (url) => { page.navigations.push(url); page.replaced.push(url); },
        },
        sessionStorage: {
            getItem: (k) => (page.storage.has(k) ? page.storage.get(k) : null),
            setItem: (k, v) => page.storage.set(k, String(v)),
        },
        setTimeout: () => 0,
        history: {
            pushState: (...args) => page.pushed.push(["pushState", ...args]),
            replaceState: (...args) => page.pushed.push(["replaceState", ...args]),
        },
        URL,
        setInterval: (fn, delay) => { page.ticks.push(fn); page.delays.push(delay); return 1; },
        WeakSet, JSON, Object, Array, RegExp, Math, performance: { now: () => 0 },
    };
    context.window = context;
    context.window.top = on.topFrame ? context : {};
    // window.name survives a same-tab navigation, so the harness carries it the way a
    // tab does when one is handed in.
    context.name = on.windowName || "";
    vm.createContext(context);
    vm.runInContext(fs.readFileSync(path.join(__dirname, "adblock.js"), "utf8"), context);

    if (page.ticks.length !== 1) throw new Error("expected one timer, got " + page.ticks.length);
    page.tick = () => { page.lookups.length = 0; page.ticks[0](); };
    page.context = context;
    Object.defineProperty(page, "windowName", { get: () => context.name });
    return page;
}

/// A player element with one video in it, and optionally a skip button. The skip is
/// handed back only for a selector that actually names a skip button class, so a
/// shipped file asking for something else does not silently get one.
const SKIP_CLASSES = [
    "ytp-skip-ad-button", "ytp-ad-skip-button",
    "ytp-ad-skip-button-modern", "ytp-ad-skip-button-slot",
];
function fakePlayer(className, video, skip) {
    return {
        className,
        querySelector(selector) {
            if (selector === "video") return video;
            const names = SKIP_CLASSES.filter(c => selector.indexOf("." + c) >= 0);
            return names.length > 0 ? (skip || null) : null;
        },
    };
}

test("the break is muted and run out, and the mute is lifted when it ends", () => {
    const media = { duration: 30, currentTime: 0, muted: false };
    const player = fakePlayer("html5-video-player ad-showing ad-interrupting", media);
    const page = loadInBrowser();
    page.players = [player];

    page.tick();
    assert.strictEqual(media.muted, true, "the ad was left audible");
    assert.strictEqual(media.currentTime, 30, "the ad was not run out");

    // The break ends into an overlay ad: ad-showing stays, the element goes back to
    // holding the film. This is where the mute used to be stranded.
    player.className = "html5-video-player ad-showing";
    media.duration = 252;
    media.currentTime = 3;

    page.tick();
    assert.strictEqual(media.muted, false, "the film was left playing silently");
    assert.strictEqual(media.currentTime, 3, "the film was seeked to its end");
});

test("a second player showing an ad does not strand the first one muted", () => {
    const first = { duration: 15, currentTime: 0, muted: false };
    const second = { duration: 20, currentTime: 0, muted: false };
    const preview = fakePlayer("html5-video-player ad-interrupting", first);
    const watch = fakePlayer("html5-video-player", second);
    const page = loadInBrowser();
    page.players = [preview, watch];

    page.tick();
    assert.strictEqual(first.muted, true);

    // The preview's ad ends and the watch player's begins.
    preview.className = "html5-video-player";
    watch.className = "html5-video-player ad-interrupting";

    page.tick();
    assert.strictEqual(first.muted, false, "the first player was left muted for good");
    assert.strictEqual(second.muted, true);
});

// A note for the next reader, so this is not rediscovered: the shipped file asks for
// ".html5-video-player.ad-interrupting" and the harness would be just as happy with
// ".ad-interrupting", because only players exist in it. Expressing the difference
// would mean teaching the stub about elements that are not players, to guard against
// YouTube one day putting that class on something else. The qualified selector is the
// right thing to ask for and it stays; the gap is deliberate.

test("a skip button is pressed in preference to seeking", () => {
    let clicks = 0;
    const media = { duration: 30, currentTime: 0, muted: false };
    const skip = {
        disabled: false, offsetParent: {},
        getBoundingClientRect: () => ({ width: 90, height: 36 }),
        click: () => clicks++,
    };
    const page = loadInBrowser();
    page.players = [fakePlayer("html5-video-player ad-showing ad-interrupting", media, skip)];

    page.tick();

    assert.strictEqual(clicks, 1, "the skip button was not pressed");
    assert.strictEqual(media.currentTime, 0, "it seeked as well as skipping");
});

test("an ad showing over the film gets its button pressed, and the film left alone", () => {
    // ad-showing without ad-interrupting: the element still holds the film, so a
    // pressable skip may be pressed and nothing may be muted or seeked. The button here
    // is one of SKIP_SELECTORS; an overlay's own close button is not among them, so
    // this pins the ad-showing path rather than claiming overlays get dismissed.
    let clicks = 0;
    const film = { duration: 252, currentTime: 40, muted: false };
    const button = {
        disabled: false, offsetParent: {},
        getBoundingClientRect: () => ({ width: 24, height: 24 }),
        click: () => clicks++,
    };
    const page = loadInBrowser();
    page.players = [fakePlayer("html5-video-player ad-showing", film, button)];

    page.tick();

    assert.strictEqual(clicks, 1, "the ad-showing path never pressed anything");
    assert.deepStrictEqual(film, { duration: 252, currentTime: 40, muted: false },
        "the film was touched while it was still the thing playing");
});

/// A button that swallows clicks, counting them.
function stubbornButton(counter) {
    return {
        disabled: false, offsetParent: {},
        getBoundingClientRect: () => ({ width: 90, height: 36 }),
        click: () => counter.clicks++,
    };
}

test("a skip button that does nothing is given up on", () => {
    // A script click is not a real one. A button that ignores it would otherwise be
    // pressed for the whole break with the seek sitting unreachable behind it, which is
    // the ad playing out in full.
    const counter = { clicks: 0 };
    const media = { duration: 30, currentTime: 0, muted: false };
    const page = loadInBrowser();
    page.players = [fakePlayer("html5-video-player ad-showing ad-interrupting", media, stubbornButton(counter))];

    page.tick();
    // Silenced on the first tick, before any of the pressing. Muting only once the
    // pressing gave up left the ad audible for a second and a half every time.
    assert.strictEqual(media.muted, true, "the ad was audible while the button was tried");
    assert.strictEqual(media.currentTime, 0, "it seeked before trying the button");

    for (let i = 0; i < 11; i++) page.tick();

    assert.strictEqual(counter.clicks, GIVE_UP_ON_SKIP_AFTER,
        "pressed " + counter.clicks + " times, expected to stop at " + GIVE_UP_ON_SKIP_AFTER);
    assert.strictEqual(media.currentTime, 30, "the ad was never run out");
});

test("giving up on one ad does not give up on the next", () => {
    // Without a reset, the first ad of the session spends the allowance and every skip
    // button for the rest of the tab's life is already given up on before it is tried.
    const counter = { clicks: 0 };
    const media = { duration: 30, currentTime: 0, muted: false };
    const player = fakePlayer("html5-video-player ad-showing ad-interrupting", media, stubbornButton(counter));
    const page = loadInBrowser();
    page.players = [player];

    for (let i = 0; i < 8; i++) page.tick();
    const spent = counter.clicks;

    // The ad ends and the film plays for a while.
    player.className = "html5-video-player";
    page.tick();
    page.tick();
    // A later ad, on the same player.
    player.className = "html5-video-player ad-showing ad-interrupting";
    media.currentTime = 0;
    page.tick();

    assert.strictEqual(counter.clicks, spent + 1, "the second ad's skip was never pressed");
});

test("the same button, parked and brought back, gets its own tries", () => {
    // The pod's next ad reuses the button node: it goes non-pressable during the
    // countdown and comes back. The break never ends and the node never changes, so
    // neither of the other two resets is reached, and without this one the second ad's
    // button is already spent before it is offered.
    const counter = { clicks: 0 };
    const media = { duration: 30, currentTime: 0, muted: false };
    const button = stubbornButton(counter);
    const page = loadInBrowser();
    page.players = [fakePlayer("html5-video-player ad-showing ad-interrupting", media, button)];

    for (let i = 0; i < 8; i++) page.tick();
    const spent = counter.clicks;

    button.offsetParent = null;     // the countdown for the next ad in the pod
    page.tick();
    page.tick();
    button.offsetParent = {};       // and it is offered again

    page.tick();

    assert.strictEqual(counter.clicks, spent + 1,
        "the button came back and was never tried again");
});

test("a fresh button gets its own tries", () => {
    // Same unbroken break, second ad in the pod, a new button node. The count is per
    // button, so the new one starts from nothing.
    const counter = { clicks: 0 };
    const media = { duration: 30, currentTime: 0, muted: false };
    let button = stubbornButton(counter);
    const player = {
        className: "html5-video-player ad-showing ad-interrupting",
        querySelector(selector) {
            if (selector === "video") return media;
            return SKIP_CLASSES.some(c => selector.indexOf("." + c) >= 0) ? button : null;
        },
    };
    const page = loadInBrowser();
    page.players = [player];

    for (let i = 0; i < 8; i++) page.tick();
    const spent = counter.clicks;

    button = stubbornButton(counter);   // the pod moves on, a new button appears
    page.tick();

    assert.strictEqual(counter.clicks, spent + 1, "the new button inherited the old one's tally");
});

test("a break longer than any real ad is not seeked", () => {
    // The backstop for the day ad-interrupting appears over a stitched break that
    // still holds the film. Muting an ad is recoverable; ending the film is not.
    const film = { duration: 3600, currentTime: 120, muted: false };
    const page = loadInBrowser();
    page.players = [fakePlayer("html5-video-player ad-showing ad-interrupting", film)];

    page.tick();

    assert.strictEqual(film.currentTime, 120, "an hour-long video was ended as if it were an ad");
    assert.strictEqual(film.muted, false);
});

test("an ordinary video is not touched", () => {
    const media = { duration: 252, currentTime: 40, muted: false };
    const page = loadInBrowser();
    page.players = [fakePlayer("html5-video-player playing-mode", media)];

    page.tick();
    page.tick();

    assert.deepStrictEqual(media, { duration: 252, currentTime: 40, muted: false });
    // And it got that far by looking, not by throwing on the first statement: the tick
    // swallows everything, so "nothing changed" alone would also describe a crash.
    assert.ok(page.lookups.length > 0, "the tick never reached the page");
});

test("nothing the player lays out is hidden", () => {
    // The most expensive line in the file. Hiding "#player-ads" was measured to hang
    // the player on every ad-scheduled load, and the symptom is a video that never
    // starts, with no error and nothing pointing at a stylesheet. Putting one of these
    // back is a one-word edit, so the gate has to be what catches it.
    const page = loadInBrowser();

    assert.strictEqual(page.delays[0], 300,
        "the skip timer's cadence changed; the file reasons about 300ms");
    assert.strictEqual(page.styles.length, 1, "the cosmetic rules were not injected");
    const css = page.styles[0].textContent;
    for (const forbidden of ["#player-ads", ".ytp-ad", ".ytd-companion-slot-renderer"]) {
        assert.ok(css.indexOf(forbidden) < 0,
            "the cosmetic rules hide " + forbidden + ", which the player waits on");
    }
    // And it is still doing its job on the page furniture, which is not the player's.
    assert.ok(css.indexOf("ytd-ad-slot-renderer") >= 0);
});

test("assigning the player response prunes it on the way in", () => {
    // This is the whole of the blocking: the ad has to be gone before the player reads
    // the object, and the only moment early enough is the assignment itself.
    const page = loadInBrowser();

    // A top-level "var", because that is how the page's inline script assigns it, and
    // it is a different path through the spec from a plain property write: a global var
    // declaration over an existing accessor has to leave the accessor in place and go
    // through its setter.
    vm.runInContext(`var ytInitialPlayerResponse = {
        adPlacements: [{ adPlacementRenderer: {} }],
        playerConfig: { daiConfig: { enableDai: true }, audioConfig: { loudnessDb: 1 } },
        videoDetails: { videoId: "abc", title: "A video" },
    };`, page.context);

    // Field by field, not deepStrictEqual: these objects were built inside the vm's own
    // realm, so their prototype is not this one's and a strict deep compare fails on
    // that alone.
    const held = page.context.ytInitialPlayerResponse;
    assert.ok(!("adPlacements" in held), "the break survived the assignment");
    assert.ok(!("daiConfig" in held.playerConfig), "the stitched-ad config survived");
    assert.strictEqual(held.videoDetails.videoId, "abc");
    assert.strictEqual(held.videoDetails.title, "A video");
    assert.strictEqual(held.playerConfig.audioConfig.loudnessDb, 1);
});

test("a payload that cannot be pruned is still handed over", () => {
    // Losing the ad matters much less than losing the page: the assignment comes from
    // YouTube's own inline bootstrap, so a throw here does not fail the ad removal, it
    // aborts the rest of that script and the page never finishes starting.
    //
    // A field that throws when read is the way to make the walk fail. A frozen payload
    // is not: prune already swallows the delete, so nothing reaches the setter.
    const page = loadInBrowser();
    const hostile = { videoDetails: { videoId: "abc" } };
    Object.defineProperty(hostile, "trap", {
        enumerable: true,
        get() { throw new Error("boom"); },
    });

    page.context.ytInitialData = hostile;

    const held = page.context.ytInitialData;
    assert.strictEqual(held, hostile, "the payload was dropped rather than handed over");
    assert.strictEqual(held.videoDetails.videoId, "abc");
});

// ------------------------------------------- in-page navigation to a video

/// A click the way the script reads one: composedPath first, then the target.
function clickOn(page, anchor, extra) {
    let preventedDefault = false;
    const event = Object.assign({
        defaultPrevented: false,
        button: 0,
        ctrlKey: false, shiftKey: false, altKey: false, metaKey: false,
        composedPath: () => (anchor ? [anchor] : []),
        target: anchor,
        preventDefault: () => { preventedDefault = true; },
        stopPropagation: () => { },
    }, extra || {});
    page.clicks.forEach(fn => fn(event));
    return preventedDefault;
}

const anchorTo = (href) => ({ tagName: "A", href, target: "" });

test("clicking a video is turned into a real navigation", () => {
    // The whole reason this exists: an in-page navigation never reassigns the payload,
    // so the pruning never runs and the ad arrives whole. Measured on a real click from
    // the feed, the window payload had no videoId at all while a 15 second ad played.
    const page = loadInBrowser();

    const prevented = clickOn(page, anchorTo("https://www.youtube.com/watch?v=abc"));

    assert.ok(prevented, "the in-page navigation was allowed to proceed");
    assert.deepStrictEqual(page.navigations, ["https://www.youtube.com/watch?v=abc"]);
});

test("a click meant for a new tab is left alone", () => {
    // Ctrl, shift, middle button: each of those is asking for a new tab, and answering
    // with a navigation in this one would be answering a different question.
    for (const extra of [{ ctrlKey: true }, { shiftKey: true }, { metaKey: true },
                         { altKey: true }, { button: 1 }, { defaultPrevented: true }]) {
        const page = loadInBrowser();
        const prevented = clickOn(page, anchorTo("https://www.youtube.com/watch?v=abc"), extra);
        assert.ok(!prevented, `${JSON.stringify(extra)} was taken over`);
        assert.deepStrictEqual(page.navigations, [], `${JSON.stringify(extra)} navigated`);
    }
});

test("only a watch link is taken over", () => {
    const page = loadInBrowser();

    for (const href of [
        "https://www.youtube.com/",                       // the feed
        "https://www.youtube.com/results?search_query=x", // search
        "https://www.youtube.com/@someone",               // a channel
        "https://example.com/watch?v=abc",                // another site entirely
        "https://www.youtube.com/watchlist",              // merely starts the same
        "http://www.youtube.com/watch?v=abc",             // not over https
        "https://www.youtube.com@evil.example/watch?v=a", // a host that only reads right
        "https://youtube.com.evil.example/watch?v=abc",
        // Shorts and live are in-page navigations too, and deliberately left alone: see
        // the note in adblock.js. Their ads are not handled, and pretending otherwise
        // here would hide that.
        "https://www.youtube.com/shorts/abc",
        "https://www.youtube.com/live/abc",
    ]) {
        assert.ok(!clickOn(page, anchorTo(href)), href + " was taken over");
    }
    assert.deepStrictEqual(page.navigations, []);

    // And a link aimed at a new tab stays the browser's business.
    const newTab = anchorTo("https://www.youtube.com/watch?v=abc");
    newTab.target = "_blank";
    assert.ok(!clickOn(page, newTab), "a target=_blank link was taken over");
});

test("a routed navigation with no click is caught too", () => {
    // The player's own next-video call goes straight through the history API.
    const page = loadInBrowser();

    page.context.history.pushState({}, "", "/watch?v=def");

    assert.deepStrictEqual(page.navigations, ["https://www.youtube.com/watch?v=def"]);
    assert.deepStrictEqual(page.pushed, [], "the in-page navigation went ahead as well");
});

test("history calls that are not a new video still work", () => {
    const page = loadInBrowser();

    page.context.history.pushState({}, "", "/results?search_query=x");
    page.context.history.replaceState({}, "", "/");
    page.context.history.pushState({}, "");                     // no url at all

    assert.deepStrictEqual(page.navigations, [], "an ordinary route was hijacked");
    assert.strictEqual(page.pushed.length, 3, "an ordinary route was swallowed");
});

test("a second click while the first load is in flight supersedes it", () => {
    // location.assign does not take effect at once, so a click a moment later arrives
    // while the page is still the old one. Dropping it handed the click back to the
    // app's own router, which routed to the new video in-page with its ad intact, and
    // then the older load landed underneath and replaced it: an ad, and the wrong video.
    const page = loadInBrowser();

    assert.ok(clickOn(page, anchorTo("https://www.youtube.com/watch?v=abc")));
    assert.ok(clickOn(page, anchorTo("https://www.youtube.com/watch?v=xyz")),
        "the second click was handed back to the page's own router");

    assert.deepStrictEqual(page.navigations, [
        "https://www.youtube.com/watch?v=abc",
        "https://www.youtube.com/watch?v=xyz",
    ]);
});

test("a route to the page we are already on is not a navigation", () => {
    // YouTube replaces state with the current url on a fresh load. Treating that as a
    // navigation would reload the page it just finished loading, forever.
    const page = loadInBrowser();
    page.context.location.href = "https://www.youtube.com/watch?v=abc";

    page.context.history.replaceState({}, "", "/watch?v=abc");

    assert.deepStrictEqual(page.navigations, [], "the page reloaded itself");
    assert.strictEqual(page.pushed.length, 1);
});

test("music, studio and the rest of youtube.com are left alone", () => {
    // Everything under youtube.com uses the /watch?v= shape, and on music.youtube.com
    // that shape is every track in a queue: forcing a load there turns a song change
    // into an app reload. Only the watch site gets this.
    for (const hostname of ["music.youtube.com", "studio.youtube.com", "tv.youtube.com",
                            "www.youtube-nocookie.com"]) {
        const page = loadInBrowser({ hostname, href: `https://${hostname}/` });
        assert.strictEqual(page.clicks.length, 0, hostname + " installed the click hook");

        page.context.history.pushState({}, "", "/watch?v=abc");
        assert.deepStrictEqual(page.navigations, [], hostname + " was sent away");
        assert.strictEqual(page.pushed.length, 1, hostname + " had its router swallowed");
    }
});

test("an embed in a frame is left alone", () => {
    // The script is injected into subframes too. An embed that navigated itself to a
    // watch page would be refused by frame-ancestors, and the video the reader came for
    // becomes a "refused to connect" box.
    const page = loadInBrowser({ topFrame: false });

    assert.strictEqual(page.clicks.length, 0, "a subframe installed the click hook");
    page.context.history.pushState({}, "", "/watch?v=abc");
    assert.deepStrictEqual(page.navigations, [], "a subframe navigated itself");
});

test("a timestamp on the video already playing is a seek, not a reload", () => {
    // Chapter marks, comment timestamps, the description's own chapter list: all of them
    // point at the current video with a different query. The player seeks for those, and
    // reloading would throw away the buffer to arrive back at the same place.
    const page = loadInBrowser({ href: "https://www.youtube.com/watch?v=abc" });

    clickOn(page, anchorTo("https://www.youtube.com/watch?v=abc&t=612"));
    page.context.history.pushState({}, "", "/watch?v=abc&t=900");

    assert.deepStrictEqual(page.navigations, [], "a seek reloaded the page");

    // A different video from the same page is still a navigation.
    clickOn(page, anchorTo("https://www.youtube.com/watch?v=xyz"));
    assert.deepStrictEqual(page.navigations, ["https://www.youtube.com/watch?v=xyz"]);
});

test("a link aimed at another frame or window is left alone", () => {
    const page = loadInBrowser();

    for (const target of ["_blank", "_top", "_parent", "somewindow"]) {
        const anchor = anchorTo("https://www.youtube.com/watch?v=abc");
        anchor.target = target;
        assert.ok(!clickOn(page, anchor), `target=${target} was taken over`);
    }
    assert.deepStrictEqual(page.navigations, []);
});

test("replaceState is answered by replacing, not by pushing", () => {
    // Otherwise the history gains a step the viewer never took, and Back lands on it.
    const page = loadInBrowser();

    page.context.history.replaceState({}, "", "/watch?v=abc");

    assert.deepStrictEqual(page.navigations, ["https://www.youtube.com/watch?v=abc"]);
    assert.deepStrictEqual(page.replaced, ["https://www.youtube.com/watch?v=abc"],
        "it pushed a history entry where the page asked to replace one");
});

test("a ping-pong between two videos is stopped", () => {
    // A reload loop from the inside: force a load, the document that arrives routes
    // somewhere, and that route asks to force another. Two ids alternating is the shape
    // the same-url and same-video rules cannot catch, so this is what the storage guard
    // is actually for. An earlier version of this test used one id, which the same-video
    // rule answered first, so the guard was never reached and deleting it changed nothing.
    const tab = new Map();          // one tab's sessionStorage, shared across its documents
    const shared = () => ({ storageMap: tab });

    const first = loadInBrowser({ ...shared(), href: "https://www.youtube.com/" });
    first.context.history.pushState({}, "", "/watch?v=abc");
    assert.deepStrictEqual(first.navigations, ["https://www.youtube.com/watch?v=abc"]);

    const second = loadInBrowser({ ...shared(), href: "https://www.youtube.com/watch?v=abc" });
    second.context.history.pushState({}, "", "/watch?v=xyz");
    assert.deepStrictEqual(second.navigations, ["https://www.youtube.com/watch?v=xyz"]);

    // Back to the first video: this is the second lap, and it has to stop here.
    const third = loadInBrowser({ ...shared(), href: "https://www.youtube.com/watch?v=xyz" });
    third.context.history.pushState({}, "", "/watch?v=abc");
    assert.deepStrictEqual(third.navigations, [], "the tab went round the loop again");
});

test("an old entry is dropped rather than kept for the session", () => {
    // One key holding a map, pruned on every write. Without the pruning the map grows
    // for the life of the tab, and the write that finally hits the quota throws.
    const tab = new Map();
    tab.set("gg.fv", JSON.stringify({ stale: Date.now() - 60000, fresh: Date.now() - 1000 }));
    const page = loadInBrowser({ storageMap: tab });

    page.context.history.pushState({}, "", "/watch?v=new");

    const stored = JSON.parse(tab.get("gg.fv"));
    assert.ok(!("stale" in stored), "an entry well past the window was kept");
    assert.ok("fresh" in stored, "an entry inside the window was dropped");
    assert.ok("new" in stored, "the video just forced was not recorded");
});

test("a video forced longer ago than the window is forced again", () => {
    const tab = new Map();
    tab.set("gg.fv", JSON.stringify({ abc: Date.now() - 60000 }));
    const page = loadInBrowser({ storageMap: tab });

    page.context.history.pushState({}, "", "/watch?v=abc");

    assert.deepStrictEqual(page.navigations, ["https://www.youtube.com/watch?v=abc"],
        "a video from earlier in the session was refused as if it were a loop");
});

test("changing your mind back to a superseded video still works", () => {
    // Click a, change to b before a lands, then change back. The mark goes on when the
    // navigation is issued, so without releasing the superseded one, a would be refused
    // for ten seconds and route in-page with its ad.
    const tab = new Map();
    const first = loadInBrowser({ storageMap: tab });
    clickOn(first, anchorTo("https://www.youtube.com/watch?v=abc"));
    clickOn(first, anchorTo("https://www.youtube.com/watch?v=xyz"));

    // xyz is the one that lands.
    const landed = loadInBrowser({ storageMap: tab, href: "https://www.youtube.com/watch?v=xyz" });
    assert.ok(clickOn(landed, anchorTo("https://www.youtube.com/watch?v=abc")),
        "going back to the superseded video was handed to the page's own router");
    assert.deepStrictEqual(landed.navigations, ["https://www.youtube.com/watch?v=abc"]);
});

test("watching several videos in a row keeps working", () => {
    // The shape of an ordinary session, and the one a bound on "forced navigations"
    // cannot tell from a loop: every video clicked is also a video forced. A counter in
    // window.name was tried here and stopped taking navigations over after the third,
    // so the ads came back for the rest of the tab's life with nothing to show why.
    // Storage and window.name both carry across, because a tab carries both: a counter
    // that lives in either one is what this has to be able to see.
    const tab = new Map();
    let at = "https://www.youtube.com/";
    let name = "";

    for (let n = 1; n <= 6; n++) {
        const page = loadInBrowser({ storageMap: tab, href: at, windowName: name });
        const next = "https://www.youtube.com/watch?v=v" + n;
        assert.ok(clickOn(page, anchorTo(next)), "video " + n + " was handed to the router");
        assert.deepStrictEqual(page.navigations, [next]);
        at = next;
        name = page.windowName;
    }
});

test("a viewer who unmutes an ad is left alone", () => {
    // We silence a break once. Someone who deliberately turns the sound back on wants
    // to hear it, and re-muting them a third of a second later is the same overriding
    // this code avoids in the other direction.
    const media = { duration: 30, currentTime: 0, muted: false };
    const page = loadInBrowser();
    page.players = [fakePlayer("html5-video-player ad-showing ad-interrupting", media)];

    page.tick();
    assert.strictEqual(media.muted, true);

    media.muted = false;            // the viewer turns it back on
    page.tick();
    page.tick();

    assert.strictEqual(media.muted, false, "the viewer was re-muted");
});

test("a video the viewer muted themselves stays muted", () => {
    // We only ever lift a mute we applied. Muting during an ad we did not silence is
    // the viewer's business, and unmuting it afterwards would be us overriding them.
    const media = { duration: 30, currentTime: 0, muted: true };
    const player = fakePlayer("html5-video-player ad-interrupting", media);
    const page = loadInBrowser();
    page.players = [player];

    page.tick();
    player.className = "html5-video-player";
    page.tick();

    assert.strictEqual(media.muted, true, "the viewer's own mute was lifted");
});

console.log(failures === 0
    ? "adblock: OK"
    : "adblock: " + failures + " failed");
process.exit(failures === 0 ? 0 : 1);
