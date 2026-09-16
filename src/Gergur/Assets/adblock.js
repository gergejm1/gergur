// Gergur page-level ad cleanup, injected at document creation on every page.
//
// Two jobs: (1) cosmetic filtering, to hide the ad containers host-blocking leaves
// behind; (2) YouTube, where host-blocking cannot help at all because the ads come
// from the same hosts and increasingly through the same media stream as the video.
// The only thing that works there is removing the ad payload from the player data
// before the player reads it, which is the technique uBlock Origin uses.
//
// The pruning core is exported under node so its behaviour can be tested against real
// payload shapes; the browser wiring below it never runs there.
(function () {
    "use strict";

    // ---------------------------------------------------------------- pruning core

    // Keys whose value is ad payload wherever they appear. Deleting these is what
    // stops the player scheduling a break at all.
    var AD_KEYS = [
        "adPlacements",
        "adSlots",
        "playerAds",
        "adBreakHeartbeatParams",
        "adParams",
        "importantForAds",
        "daiConfig",
        "playerAdParams",
    ];

    // Objects that *are* an ad. These arrive as elements of a list of things to
    // render, so they are removed from the list rather than emptied: leaving an empty
    // shell behind is what produced the blank gaps in the feed that cosmetic rules
    // then had to paper over.
    //
    // The second group is the player's own ad message types. Every watch page carries
    // a preloadMessageNames list naming the renderers the server may send, and these
    // are the ad-carrying entries of it, so the list is YouTube's rather than guessed.
    // Only whole containers are here: the pieces an ad is built from (adBadgeViewModel,
    // skipAdViewModel and the like) are deliberately left out, because matching on a
    // part would take out whatever legitimately contained it.
    var AD_RENDERERS = [
        "adSlotRenderer",
        "displayAdRenderer",
        "promotedSparklesWebRenderer",
        "promotedSparklesTextSearchRenderer",
        "promotedVideoRenderer",
        "compactPromotedVideoRenderer",
        "compactPromotedItemRenderer",
        "inFeedAdLayoutRenderer",
        "searchPyvRenderer",
        "adDivergingVideoRenderer",
        "brandVideoShelfRenderer",
        "brandVideoSingletonRenderer",
        "instreamVideoAdRenderer",
        "adActionInterstitialRenderer",
        "adBreakServiceRenderer",
        "aboveFeedAdLayoutRenderer",
        "inPlayerAdLayoutRenderer",
        "playerBytesAdLayoutRenderer",
        "playerLegacyDesktopWatchAdsRenderer",
        "adsEngagementPanelContentRenderer",
        // These two also turn up as a field rather than a list entry, which is why they
        // used to sit in AD_KEYS. They only need to be here: prune deletes a key found
        // in either set, so this covers both shapes and AD_KEYS covers neither better.
        "adPlacementRenderer",
        "adsEngagementPanelRenderer",
    ];

    // Object.create(null), not {}. These are looked up with keys taken straight out of
    // remote JSON, and a plain object answers "yes" to constructor, toString, valueOf
    // and hasOwnProperty because it inherits them. A payload with a field named any of
    // those would have had that field deleted, and a list entry carrying one would have
    // been thrown out as an ad. Neither would have looked like anything but YouTube
    // changing its shape again.
    var AD_KEY_SET = Object.create(null);
    var AD_RENDERER_SET = Object.create(null);
    var i;
    for (i = 0; i < AD_KEYS.length; i++) AD_KEY_SET[AD_KEYS[i]] = true;
    for (i = 0; i < AD_RENDERERS.length; i++) AD_RENDERER_SET[AD_RENDERERS[i]] = true;

    /// True when this object is itself an advertisement rather than something
    /// containing one, which is any object carrying a key that names an ad renderer.
    function isAdObject(value) {
        if (!value || typeof value !== "object" || Array.isArray(value)) return false;
        for (var key in value) {
            if (Object.prototype.hasOwnProperty.call(value, key) && AD_RENDERER_SET[key]) return true;
        }
        return false;
    }

    // Fields that are bookkeeping rather than something to look at. Every renderer in
    // a real feed carries trackingParams, and counting that as content was enough on
    // its own to defeat the check below: the ad came out, the wrapper kept its tracking
    // id, and the hole stayed in the grid. None of these render, so none of them is a
    // reason to keep a wrapper that has nothing else left in it.
    var BOOKKEEPING = Object.create(null);
    // Logging only. "targetId" and "identifier" look like they belong here and do
    // not: a continuation append addresses the section it extends by targetId, so
    // treating one as weightless would let a section be removed out from under it.
    //
    // "rowIndex", "colIndex" and "onFocusEffect" are here because a real home feed put
    // them on its ad wrappers and they kept three empty shells in the grid: a position
    // in a layout and a focus animation are not something to look at, and a wrapper
    // holding nothing else is a hole whatever coordinates it carries.
    ["trackingParams", "clickTrackingParams", "sectionIdentifier",
        "itemSectionIdentifier", "loggingDirectives", "adLayoutLoggingData",
        "rowIndex", "colIndex", "onFocusEffect",
    ].forEach(function (name) { BOOKKEEPING[name] = true; });

    /// Whether this is a wrapper we hollowed out: something we deleted a key from, or
    /// containing something we did, with no value of any kind left anywhere inside it.
    ///
    /// The feed does not hand us ads as list entries. It hands us
    /// {richItemRenderer: {content: {adSlotRenderer: ...}}}, so the entry itself is an
    /// ordinary wrapper and only the thing two levels down is the ad. Deleting the ad
    /// leaves {richItemRenderer: {content: {}}} sitting in the list, which is the blank
    /// gap in the feed, and no cosmetic rule can match it because nothing about it says
    /// "ad" any more.
    ///
    /// Requiring that we gutted it is what keeps this from reaching further than it
    /// should: an empty object we never touched is left exactly where it was, so this
    /// can only ever remove something an ad used to be inside. It stops at the first
    /// string, number or boolean it finds, so anything with real content costs nothing.
    function isSpentWrapper(value, gutted) {
        if (value === null || typeof value !== "object") return false;

        // The cycle guard is allocated only once a wrapper turns out to be big enough
        // to need one. This runs per list entry, inside the assignment, while the page
        // is waiting to start, and a feed entry is a handful of nodes: paying for a
        // WeakSet on each of a thousand of them was most of what the pass cost.
        var seen = null;
        var visited = 0;
        var stack = [value];
        var sawGutted = false;

        while (stack.length > 0) {
            var node = stack.pop();
            if (node === null || typeof node !== "object") return false;   // real content
            if (seen !== null) {
                if (seen.has(node)) continue;
                seen.add(node);
            } else if (++visited > 32) {
                seen = new WeakSet();
                seen.add(node);
            }
            if (gutted.has(node)) sawGutted = true;

            if (Array.isArray(node)) {
                for (var a = 0; a < node.length; a++) stack.push(node[a]);
                continue;
            }
            for (var key in node) {
                if (!Object.prototype.hasOwnProperty.call(node, key)) continue;
                if (BOOKKEEPING[key]) continue;
                stack.push(node[key]);
            }
        }
        return sawGutted;
    }

    // Walks the whole payload rather than the handful of keys that used to be checked
    // at the top level. Ads live several levels down in everything except the watch
    // page: in continuation items, in the up-next list, in the feed's rich grid, in
    // the Shorts sequence. Iterative, because these payloads nest deeply enough to
    // overflow a recursive walk, and cycle-guarded because they are not always trees.
    //
    // Two passes, because whether a list entry still holds anything is only knowable
    // once everything under it has been pruned, and one pop-order walk reaches the
    // list before it reaches what is in it.
    function prune(root) {
        if (!root || typeof root !== "object") return root;

        var gutted = new WeakSet();
        var didGut = false;
        var seen = new WeakSet();
        var stack = [root];

        while (stack.length > 0) {
            var node = stack.pop();
            if (!node || typeof node !== "object") continue;
            if (seen.has(node)) continue;
            seen.add(node);

            if (Array.isArray(node)) {
                for (var a = node.length - 1; a >= 0; a--) {
                    if (isAdObject(node[a])) {
                        node.splice(a, 1);
                        // The list counts as gutted too. A shelf whose every entry was
                        // an ad is left as an empty list inside a wrapper that renders,
                        // which is the same hole reached by the other road.
                        gutted.add(node);
                        didGut = true;
                    }
                    else if (node[a] && typeof node[a] === "object") stack.push(node[a]);
                }
                continue;
            }

            for (var key in node) {
                if (!Object.prototype.hasOwnProperty.call(node, key)) continue;
                if (AD_KEY_SET[key] || AD_RENDERER_SET[key]) {
                    try { delete node[key]; } catch (e) { }
                    gutted.add(node);
                    didGut = true;
                    continue;
                }
                var child = node[key];
                if (child && typeof child === "object") stack.push(child);
            }
        }

        // Second pass: take out the wrappers the first one emptied. Skipped outright
        // when nothing was removed, which is every page with no ad on it, because this
        // runs inside the assignment and holds up the page's own bootstrap while it does.
        if (!didGut) return root;

        seen = new WeakSet();
        stack = [root];
        while (stack.length > 0) {
            var target = stack.pop();
            if (!target || typeof target !== "object") continue;
            if (seen.has(target)) continue;
            seen.add(target);

            if (Array.isArray(target)) {
                for (var b = target.length - 1; b >= 0; b--) {
                    if (isSpentWrapper(target[b], gutted)) target.splice(b, 1);
                    else if (target[b] && typeof target[b] === "object") stack.push(target[b]);
                }
                continue;
            }
            for (var k in target) {
                if (!Object.prototype.hasOwnProperty.call(target, k)) continue;
                var v = target[k];
                if (v && typeof v === "object") stack.push(v);
            }
        }
        return root;
    }

    /// Ten minutes. Long enough that no real break comes near it, short enough that
    /// nothing anyone sat down to watch is under it.
    var LONGEST_PLAUSIBLE_AD = 600;

    /// How many ticks running the same skip button may be pressed without the ad going
    /// away. A script click is not a real one, and a button that ignores it would other-
    /// wise be pressed for the whole break with the seek sitting unreachable behind it,
    /// which is the ad playing out in full. After this many we stop believing it.
    var GIVE_UP_ON_SKIP_AFTER = 5;

    /// Whether the thing in the video element is an ad standing between the viewer and
    /// what they asked for, so that running it to its end is a kindness.
    ///
    /// "ad-showing" on its own is not that. An overlay ad, and a server-stitched break
    /// if the daiConfig pruning ever stops taking, both set it while the element still
    /// holds the film, and seeking to the end there ends what the viewer is watching,
    /// silently and looking exactly like a YouTube fault. "ad-interrupting" is the class
    /// that says the ad has taken the element over, so only that earns the seek.
    function interrupting(player, video) {
        if (!player || !video) return false;
        if (!/(^|\s)ad-interrupting(\s|$)/.test(player.className || "")) return false;
        if (!isFinite(video.duration) || video.duration <= 0) return false;
        // And a length an ad could plausibly be. If the class ever appears over a
        // server-stitched break that still holds the film, this is what stands between
        // a wrong guess and ending a feature-length video someone is watching. Refusing
        // to seek costs a muted ad; seeking wrongly costs them the thing itself.
        return video.duration <= LONGEST_PLAUSIBLE_AD;
    }

    /// Whether pressing this button would actually do anything. YouTube keeps the skip
    /// button in the DOM through the countdown with its slot hidden, so "there is a
    /// button" is true for the whole unskippable stretch. Taking that as reason to
    /// click it and wait let the ad play out in full.
    function pressable(button) {
        if (!button || button.disabled || button.offsetParent === null) return false;
        // offsetParent alone says nothing about a button hidden by visibility or
        // collapsed to nothing, and both are ways YouTube could park the countdown
        // button tomorrow without ever taking it out of the DOM.
        if (typeof button.getBoundingClientRect === "function") {
            var box = button.getBoundingClientRect();
            if (!box || box.width <= 0 || box.height <= 0) return false;
        }
        return true;
    }

    // The test seam. In a browser there is no module object and this is skipped.
    if (typeof module === "object" && module && module.exports) {
        module.exports = {
            prune: prune,
            isAdObject: isAdObject,
            pressable: pressable,
            GIVE_UP_ON_SKIP_AFTER: GIVE_UP_ON_SKIP_AFTER,
            interrupting: interrupting,
            AD_KEYS: AD_KEYS,
            AD_RENDERERS: AD_RENDERERS,
        };
        return;
    }

    // ---------------------------------------------------------------- cosmetic filtering

    var cosmeticCss = [
        "ins.adsbygoogle", ".adsbygoogle",
        "[id^='google_ads_iframe']", "[id^='div-gpt-ad']", "[id^='taboola-']",
        "iframe[src*='doubleclick.net']", "iframe[src*='googlesyndication']",
        "iframe[src*='adsystem']", "amp-ad", ".OUTBRAIN", "[data-outbrain]",
        ".trc_rbox_container", ".ad-banner-container", ".advertisement-label",
        "[aria-label='advertisement']", "[aria-label='Advertisement']",
        ".GoogleActiveViewElement", "#carbonads", ".carbon-ads",
        // YouTube page furniture. The data-level pruning above removes most of these
        // before they can render; these catch what a stale payload shape leaves behind.
        //
        // Nothing the player owns is in this list, and that is not an oversight.
        // Hiding "#player-ads" was measured to hang the player outright: on a video
        // YouTube had scheduled an ad for, the video never started, 3 runs out of 3,
        // and it started every time with that one selector removed. The ad module
        // waits on a container it can no longer lay out. The same goes for the
        // ".ytp-ad-*" slots inside the player and for ".ytd-companion-slot-renderer",
        // the companion beside it, which the player lays out as part of the same break.
        // Those ads are removed from the payload above instead, which is both earlier
        // and safer. A test pins this, because putting one of them back costs the video
        // and nothing about the symptom points here.
        "#masthead-ad", "ytd-display-ad-renderer", "ytd-ad-slot-renderer",
        "ytd-in-feed-ad-layout-renderer", "ytd-banner-promo-renderer",
        "ytd-statement-banner-renderer", "ytd-brand-video-shelf-renderer",
        "ytd-merch-shelf-renderer",
        "ytd-promoted-sparkles-web-renderer", "ytd-promoted-video-renderer",
        "ytd-compact-promoted-video-renderer", "ytd-search-pyv-renderer",
    ].join(",") + " { display: none !important; }";

    function injectCss() {
        try {
            var style = document.createElement("style");
            style.textContent = cosmeticCss;
            (document.head || document.documentElement).appendChild(style);
        } catch (e) { }
    }
    if (document.readyState === "loading")
        document.addEventListener("DOMContentLoaded", injectCss);
    else
        injectCss();

    // ---------------------------------------------------------------- YouTube

    // youtube-nocookie.com too: an embed is the same player serving the same breaks,
    // and leaving it out meant an embedded video was the one place ads still ran.
    if (!/(^|\.)youtube(-nocookie)?\.com$/.test(location.hostname)) return;

    // The page ships the first video's data inline, in a script that runs before
    // anything can hook a network call. Pruning it on DOMContentLoaded, which is what
    // this used to do, is too late: the player reads the object as soon as it is
    // assigned, so the ad break was already scheduled and deleting the keys afterwards
    // changed nothing. Taking the assignment itself is the only point that is early
    // enough. In-page navigation does not reassign it at all, which is what the section
    // below is for: measured by watching a click from the feed, the payload on window
    // still had no videoId while a 15 second ad ran.
    //
    // This is the whole of it, and the narrowness is the point. Removing the ad
    // payload from a *fetched* youtubei response instead was measured against real
    // playback and hangs the player: by then it has committed to the break and waits
    // forever for an ad that will never arrive. A fetch wrapper, a JSON.parse hook and
    // a Response.json hook were each tried and each did it, whether they pruned ad
    // keys, only ad renderers, or a single key. Before the assignment is the only
    // moment removing an ad leaves the player able to carry on.
    //
    // Measured by loading a watch page repeatedly and reading whether YouTube had
    // scheduled an ad for that load out of the page's own script text, so a run it
    // offered no ad on could be told from one where the ad was taken away. Only loads
    // that were served an ad are counted. This hook on its own: five of five started
    // the video straight away and played none of it. Each of the response hooks, over
    // four to five such loads apiece, never started the video at all. Re-measured three
    // of three after the prune changes that followed review, with the whole of the
    // shipped configuration on.
    ["ytInitialPlayerResponse", "ytInitialData", "ytInitialReelWatchSequenceResponse"]
        .forEach(function (name) {
            var held = window[name];
            try {
                if (held) prune(held);
                Object.defineProperty(window, name, {
                    configurable: true,
                    enumerable: true,
                    get: function () { return held; },
                    set: function (value) {
                        // The page assigns this from its own inline bootstrap script. A
                        // throw in here does not fail the ad removal, it aborts the rest
                        // of that script, and the page never finishes starting. Keeping
                        // an unpruned payload is the better of the two.
                        try { held = prune(value); } catch (e) { held = value; }
                    },
                });
            } catch (e) {
                // Either the property is already defined non-configurably, or pruning
                // what was there threw. Either way this name goes unhooked and the rest
                // still get theirs, which is why the whole body is inside the try: a
                // throw escaping here would take the other two names and the skip below
                // with it.
            }
        });

    // ------------------------------------------------- in-page navigation to a video
    //
    // Clicking a video from the feed never reloads the document. YouTube fetches the
    // next player response instead, and that is the one response nothing here may touch:
    // pruning a fetched response hangs the player, for the reason set out above. So the
    // ad arrives whole, and watching anything meant reloading the page by hand first.
    //
    // Make the navigation a real one. The payload then comes back inline, the hook above
    // prunes it before the player is given it, and the video starts clean. It costs a
    // document load per video, which is exactly what reloading by hand was costing.
    //
    // Narrowly, though, because a forced reload is a heavy thing to do to a page:
    //
    //   - the watch site only. Everything under youtube.com shares the /watch?v= shape,
    //     and on music.youtube.com that shape is every track in a queue: forcing there
    //     would reload the whole app between songs. Studio and TV are no better.
    //   - the top frame only. This script is injected into subframes as well, and an
    //     embed that navigated itself to /watch would be refused by frame-ancestors and
    //     leave a "refused to connect" box where the video was.
    //   - plain left clicks only. A middle click or ctrl-click is asking for a new tab,
    //     and a link aimed at another frame or window is not ours to answer.
    //   - a different video only. A chapter or comment timestamp points at the video
    //     already playing, and the player seeks for those. Reloading instead would throw
    //     away the buffer to arrive at the same place.
    //   - "/watch" only. Shorts and /live/ route in-page the same way and their ads are
    //     therefore still not handled, which is a real gap and not an oversight: Shorts
    //     is a swipe feed, and a document load per short would be the music problem
    //     again. Closing it needs a different idea, not a wider match here.
    // Scoped to a function rather than returning from the script: the skip below still
    // has work to do on music and inside an embed, where an ad that renders is just as
    // unwelcome as one here.
    var WATCH_SITE = /^(www\.|m\.)?youtube\.com$/;
    if (WATCH_SITE.test(location.hostname) && window.top === window) forceRealNavigation();

    function forceRealNavigation() {
        // How long a video stays remembered as "just forced". Long enough to catch the
        // load-rewrite-load ping-pong, short enough that coming back to a video later in
        // the session is still handled.
        var FORCED_WINDOW_MS = 10000;
        var FORCED_KEY = "gg.fv";

        /// The url to load instead, or null to leave the navigation alone.
        function watchUrl(url) {
            try {
                var parsed = new URL(url, location.href);
                if (parsed.protocol !== "https:") return null;
                if (!WATCH_SITE.test(parsed.hostname) || parsed.pathname !== "/watch") return null;

                // The same video is a seek, whatever else changed in the query.
                var here = new URL(location.href);
                if (here.pathname === "/watch"
                    && here.searchParams.get("v") === parsed.searchParams.get("v")) return null;

                return parsed.href;
            } catch (e) {
                return null;
            }
        }

        /// The videos this tab has forced a load of lately, oldest entries dropped.
        /// One key rather than one per video: an entry per video for the life of a tab
        /// grows without limit, and the write that finally hits the quota would throw,
        /// be swallowed, and take the loop guard down with it in silence.
        function recentlyForced() {
            try {
                var raw = sessionStorage.getItem(FORCED_KEY);
                var all = raw ? JSON.parse(raw) : {};
                var cutoff = Date.now() - FORCED_WINDOW_MS;
                var live = {};
                for (var id in all) {
                    if (Object.prototype.hasOwnProperty.call(all, id) && all[id] > cutoff)
                        live[id] = all[id];
                }
                return live;
            } catch (e) {
                return {};
            }
        }

        /// Whether this video has just been forced, which is how a reload loop looks from
        /// the inside: load, the page rewrites its own url, load again. YouTube does
        /// rewrite watch urls on arrival, and not always to the same string, so matching
        /// the whole url is not enough to bound it.
        ///
        /// The mark goes on when the navigation is issued rather than when it arrives,
        /// which would strand a video the viewer changed their mind about: click a, click
        /// b before a lands, then click a again and it would be refused for ten seconds
        /// with the ad intact. So issuing a navigation releases the one it supersedes.
        var lastIssued = null;

        function justForced(url) {
            try {
                var id = new URL(url, location.href).searchParams.get("v");
                // A watch url with no video id, a playlist landing page. Nothing to
                // remember it by, and nothing needs to: one no-id url routing to another
                // is refused by the same-video rule above, both being null, and in a
                // no-id/id alternation the id side is refused on its next lap by its own
                // record here.
                if (!id) return false;

                var live = recentlyForced();
                if (live[id]) return true;
                if (lastIssued && lastIssued !== id) delete live[lastIssued];
                live[id] = Date.now();
                lastIssued = id;
                sessionStorage.setItem(FORCED_KEY, JSON.stringify(live));
            } catch (e) {
                // No storage to remember with, so no guard against a two-video
                // ping-pong. That is a loop nothing has been seen to produce: the
                // rewrite YouTube actually does is to the same video, and watchUrl
                // refuses that with no storage at all. If one ever does appear it looks
                // like a tab reloading between two videos and never settling. A counter
                // in window.name was
                // tried here and was worse than the thing it guarded, because it
                // counted every video a tab ever forced rather than a run of them, so
                // the blocking switched itself off after the third video and stayed off.
            }
            return false;
        }

        /// Takes the navigation over. A second click while the first load is still in
        /// flight supersedes it rather than being dropped: handing that click back to the
        /// router would let the app route to the new video in-page, with its ad intact,
        /// and then the older load would land underneath and replace it.
        function leaveFor(url, replacing) {
            // No same-url check here: watchUrl has already refused anything that resolves
            // to the video this page is showing, which is the only way url could equal
            // location.href by the time it gets this far.
            if (justForced(url)) return false;
            // replaceState asked to replace an entry, so replace one. Pushing instead puts a
            // step in the history the viewer never took, and Back then goes somewhere odd.
            if (replacing) location.replace(url);
            else location.assign(url);
            return true;
        }

        document.addEventListener("click", function (event) {
            try {
                if (event.defaultPrevented || event.button !== 0) return;
                if (event.ctrlKey || event.shiftKey || event.altKey || event.metaKey) return;

                // composedPath first, because the anchor is often inside a shadow root and
                // the event target is then the host rather than the link.
                var anchor = null;
                var path = typeof event.composedPath === "function" ? event.composedPath() : [];
                for (var i = 0; i < path.length; i++) {
                    if (path[i] && path[i].tagName === "A" && path[i].href) { anchor = path[i]; break; }
                }
                if (!anchor && event.target && event.target.closest)
                    anchor = event.target.closest("a[href]");
                if (!anchor) return;
                // Anything aimed elsewhere belongs to the browser, not to us.
                if (anchor.target && anchor.target !== "_self") return;

                var url = watchUrl(anchor.href);
                if (url && leaveFor(url, false)) {
                    event.preventDefault();
                    event.stopPropagation();
                }
            } catch (e) { }
        }, true);

        // The backstop, for a navigation nothing clicked: the player's own next-video call,
        // a keyboard shortcut, anything routed straight through the history API.
        ["pushState", "replaceState"].forEach(function (name) {
            var native = history[name];
            if (typeof native !== "function") return;
            try {
                history[name] = function (state, title, url) {
                    try {
                        if (url !== undefined && url !== null) {
                            var target = watchUrl(url);
                            if (target && leaveFor(target, name === "replaceState")) return;
                        }
                    } catch (e) { }
                    return native.apply(this, arguments);
                };
            } catch (e) { }
        });
    }

    // Last resort, for an ad that renders anyway: press skip the moment it appears,
    // and otherwise run the break out silently. Only touches the page while the
    // player says an ad is showing, so it cannot interfere with ordinary playback.
    // No survey button here. Clicking ".ytp-ad-survey-answer-button" would dismiss the
    // overlay, but it does it by submitting an answer we invented to Google under the
    // user's own session. Blocking an ad does not require speaking for them.
    //
    // Nothing else picks that up either, and that is the honest state of it: a survey
    // or banner overlay sets "ad-showing" alone, so the seek below never runs on it,
    // and the cosmetic list leaves the player's own furniture alone for the reasons
    // given up there. Removing it from the payload is the only thing that handles it,
    // so if one ever gets through it stays on screen.
    var SKIP_SELECTORS = [
        ".ytp-skip-ad-button",
        ".ytp-ad-skip-button",
        ".ytp-ad-skip-button-modern",
        ".ytp-ad-skip-button-slot button",
    ].join(",");

    // The element we muted, not just a flag. The player runs the ad through the same
    // video element as the film, so muting and walking away would leave the film
    // silent. Unmute that element rather than whatever the current player hands back:
    // the page can hold more than one video (a hover preview, a miniplayer), and
    // clearing the reference without lifting the mute would leave whatever we silenced
    // silent for the rest of the session with nothing on screen to explain it.
    var mutedElement = null;

    var pressedButton = null;
    var pressedTimes = 0;

    // 300ms rather than anything longer because a skip button has to be caught while it
    // is up, and rather than anything shorter because this runs for the life of every
    // YouTube tab. The body is two querySelector calls when nothing is happening.
    setInterval(function () {
        try {
            // Ask for the player an ad has taken over, not the first player on the page.
            // A watch page behind a hover preview, or a miniplayer running while the
            // feed scrolls, put another .html5-video-player ahead of it in document
            // order, and reading that one's class list meant the ad went unnoticed.
            var adPlayer = document.querySelector(".html5-video-player.ad-interrupting");
            var adVideo = adPlayer ? adPlayer.querySelector("video") : null;

            // Lift our mute the moment the element we silenced is no longer the one an
            // ad has taken over, which is the same gate the mute went on. Keying this
            // on "no ad anywhere" instead left the film playing silently every time a
            // break ended into an overlay ad, and left a second player muted for good.
            if (mutedElement && mutedElement !== adVideo) {
                mutedElement.muted = false;
                mutedElement = null;
            }

            var showing = adPlayer || document.querySelector(".html5-video-player.ad-showing");
            if (!showing) {
                pressedButton = null;
                pressedTimes = 0;
                return;
            }

            // Silence first, press second. Pressing can take a second and a half of
            // tries before it gives up, and there is no reason for the ad to be audible
            // through them when the unmute is keyed on the element and lifts by itself.
            // Once per break, not once per tick. A viewer who deliberately unmutes an
            // ad is entitled to hear it, and re-muting them 300ms later would be the
            // same overriding this code goes out of its way to avoid in the other
            // direction. mutedElement is cleared when the break ends, so the next one
            // gets silenced again.
            var isBreak = interrupting(adPlayer, adVideo);
            if (isBreak && !adVideo.muted && mutedElement !== adVideo) {
                adVideo.muted = true;
                mutedElement = adVideo;
            }

            // Scoped to that player, for the reason above.
            var skip = showing.querySelector(SKIP_SELECTORS);
            if (pressable(skip)) {
                if (skip !== pressedButton) {
                    pressedButton = skip;
                    pressedTimes = 0;
                }
                if (pressedTimes < GIVE_UP_ON_SKIP_AFTER) {
                    pressedTimes++;
                    skip.click();
                    return;
                }
            } else {
                // Not pressable any more, so the next button that is gets its own tries.
                // Without this a second ad in the same pod, reusing the same button node
                // behind the same unbroken ad-showing, would start already given up on.
                pressedButton = null;
                pressedTimes = 0;
            }
            if (isBreak) adVideo.currentTime = adVideo.duration;
        } catch (e) { }
    }, 300);
})();
