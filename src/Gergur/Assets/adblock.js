// Gergur page-level ad cleanup, injected at document creation on every page.
// Two jobs: (1) cosmetic filtering - hide ad containers host-blocking leaves
// behind; (2) YouTube - prune ad payloads from player data before the player
// reads them (the same core technique uBlock Origin uses), with a skip/fast-
// forward fallback for anything that slips through.
(function () {
    "use strict";

    // ---------- 1. Cosmetic filtering (safe, specific selectors only) ----------
    var cosmeticCss = [
        "ins.adsbygoogle", ".adsbygoogle",
        "[id^='google_ads_iframe']", "[id^='div-gpt-ad']", "[id^='taboola-']",
        "iframe[src*='doubleclick.net']", "iframe[src*='googlesyndication']",
        "iframe[src*='adsystem']", "amp-ad", ".OUTBRAIN", "[data-outbrain]",
        ".trc_rbox_container", ".ad-banner-container", ".advertisement-label",
        "[aria-label='advertisement']", "[aria-label='Advertisement']",
        ".GoogleActiveViewElement", "#carbonads", ".carbon-ads",
        // YouTube page furniture
        "#masthead-ad", "ytd-display-ad-renderer", "ytd-ad-slot-renderer",
        "ytd-in-feed-ad-layout-renderer", "ytd-banner-promo-renderer",
        "ytd-statement-banner-renderer", "ytd-brand-video-shelf-renderer",
        "#player-ads", ".ytd-companion-slot-renderer", "ytd-merch-shelf-renderer",
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

    // ---------- 2. YouTube ad neutralizer ----------
    if (!/(^|\.)youtube\.com$/.test(location.hostname)) return;

    function prune(obj) {
        if (obj && typeof obj === "object") {
            try {
                delete obj.adPlacements;
                delete obj.adSlots;
                delete obj.playerAds;
                delete obj.adBreakHeartbeatParams;
                if (obj.playerResponse) prune(obj.playerResponse);
                if (obj.playerConfig && obj.playerConfig.daiConfig) delete obj.playerConfig.daiConfig;
            } catch (e) { }
        }
        return obj;
    }

    // Player data arrives via JSON.parse (XHR paths) and Response.json (fetch).
    var origParse = JSON.parse;
    JSON.parse = function () { return prune(origParse.apply(this, arguments)); };
    var origJson = Response.prototype.json;
    Response.prototype.json = function () { return origJson.call(this).then(prune); };
    // The first page load ships data inline before any hook can run.
    document.addEventListener("DOMContentLoaded", function () {
        try {
            if (window.ytInitialPlayerResponse) prune(window.ytInitialPlayerResponse);
        } catch (e) { }
    });

    // Fallback: if an ad still renders, skip it or jump to its end, muted.
    setInterval(function () {
        try {
            var skip = document.querySelector(
                ".ytp-skip-ad-button, .ytp-ad-skip-button, .ytp-ad-skip-button-modern");
            if (skip) skip.click();
            var adVideo = document.querySelector(".ad-showing video, .ad-interrupting video");
            if (adVideo && isFinite(adVideo.duration) && adVideo.duration > 0) {
                adVideo.muted = true;
                adVideo.currentTime = adVideo.duration;
            }
        } catch (e) { }
    }, 500);
})();
