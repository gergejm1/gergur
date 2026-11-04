using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Gergur.Tabs;

/// <summary>What the toolbar's now-playing panel can ask of a tab.</summary>
internal enum MediaAction
{
    Toggle,
    Play,
    Pause,
    Next,
    Previous,
}

/// <summary>
/// What a page says about its media: the song's name as the site gives it to Windows, whether
/// it is playing, and which of the site's own buttons can be pressed from outside.
/// </summary>
internal sealed record MediaState(string Title, string Artist, bool HasMedia, bool Playing, bool Paused, bool CanNext, bool CanPrevious);

/// <summary>
/// Play, pause, next and previous for a page's music, from outside the page.
///
/// A site that plays music hands Windows its own buttons through the Media Session API
/// (navigator.mediaSession.setActionHandler): that is how the media keys and the Windows
/// volume overlay reach it, and it is the site's own idea of "next", a playlist's next song
/// on YouTube. A page cannot be asked for those handlers afterwards, so <see cref="HookScript"/>
/// notes each one as the site hands it over, at document creation and before any of the
/// site's own script, and <see cref="CommandScript"/> presses the one asked for. A page that
/// hands over none still has its media elements, which are played and paused directly.
///
/// Runs in the page's own world, since that is where the site's handlers live; the page
/// could therefore lie about what it plays, which costs no more than a wrong title in the
/// panel. Nothing it says is ever run or opened. Measured, not assumed: WebView2 already
/// registers every page's media session with Windows, so the hardware media keys reach a
/// playing tab with Gergur behind other windows, and nothing here is needed for that.
/// </summary>
internal static class MediaControl
{
    /// <summary>How long a text from the page may be once it is in the panel.</summary>
    internal const int MaxText = 200;

    /// <summary>
    /// Starts a YouTube song from its beginning when YouTube starts it from the saved place
    /// in a signed-in person's watch history and that would cost them the song: the place is
    /// near its end (the last 30 s, or the last tenth), or it is a music video at all. This is
    /// what "the playlist always skips a song" was, measured on 2026-11-04 on the real profile
    /// with nothing seeking: a song already heard loaded at 154 s of 155, YouTube called it
    /// ended within half a second, and the playlist moved on, as if the song had been skipped.
    /// Whether an ordinary browser gets the same saved place was not established, since only
    /// this signed-in session shows it; uBO Lite's rewrite of the player's request is a suspect.
    ///
    /// The player sets the time in its own handler for the same "loadedmetadata" event, after
    /// this capture listener has run, so the time is read just after the event and at the
    /// player's first seek, and not again once the song plays: a seek by the person is theirs.
    /// One look per song, so a stream reloaded part way through is left alone. A link that
    /// names its own time (t=, start=) is left alone, and so is the song a reload of the page
    /// comes back to. Only in a playlist (list= in the address), which is what was measured:
    /// outside one a resumed video goes on as YouTube has it. Not during an ad. The top page of
    /// YouTube only.
    /// </summary>
    internal const string SongStartScript = """
        (() => {
          try {
            if (window !== window.top || location.protocol !== 'https:') return;
            if (!/^(www\.|m\.|music\.)?youtube\.com$/.test(location.hostname)) return;
            const key = Symbol.for('gergur.songstart');
            if (window[key]) return;
            Object.defineProperty(window, key, { value: true });
            let last = null, armed = false;
            const playerOf = v => (v instanceof HTMLVideoElement ? v.closest('#movie_player') : null);
            const inAd = p => /(^|\s)ad-(showing|interrupting)(\s|$)/.test(p.className);
            const reloaded = (() => {
              try { const n = performance.getEntriesByType('navigation')[0]; return !!n && n.type === 'reload'; } catch (err) { return false; }
            })();
            const look = v => {
              if (!armed) return;
              const p = playerOf(v);
              const t = v.currentTime, d = v.duration;
              if (!p || !(t >= 1) || inAd(p)) return;
              armed = false;
              if (!isFinite(d) || d <= 0) return;
              const u = new URL(location.href);
              if (!u.searchParams.has('list') || u.searchParams.has('t') || u.searchParams.has('start')) return;
              const r = typeof p.getPlayerResponse === 'function' ? p.getPlayerResponse() : null;
              const about = r && r.microformat && r.microformat.playerMicroformatRenderer;
              if (!(about && about.category === 'Music') && d - t > 30 && t < d * 0.9) return;
              if (typeof p.seekTo === 'function') p.seekTo(0, true); else v.currentTime = 0;
            };
            document.addEventListener('loadedmetadata', e => {
              try {
                const v = e.target, p = playerOf(v);
                if (!p || typeof p.getVideoData !== 'function') return;
                if (inAd(p)) return;
                const id = (p.getVideoData() || {}).video_id;
                if (!id || id === last) return;
                armed = !(reloaded && last === null);
                last = id;
                setTimeout(() => { try { look(v); } catch (err) { } }, 0);
              } catch (err) { }
            }, true);
            document.addEventListener('seeking', e => { try { look(e.target); } catch (err) { } }, true);
            document.addEventListener('playing', e => {
              const v = e.target;
              setTimeout(() => {
                try {
                  const p = playerOf(v);
                  if (p && inAd(p)) return;   // the song it is waiting for has not begun
                  look(v);
                } catch (err) { }
                armed = false;
              }, 0);
            }, true);
          } catch (err) { }
        })();
        """;

    /// <summary>
    /// Notes the site's media handlers as it hands them over. The original is called first,
    /// so an action the engine refuses (and throws for) is never noted as there.
    /// </summary>
    internal const string HookScript = """
        (() => {
          try {
            const key = Symbol.for('gergur.media');
            if (window[key] || typeof MediaSession !== 'function') return;
            const proto = MediaSession.prototype;
            const original = proto.setActionHandler;
            if (typeof original !== 'function') return;
            const handlers = new Map();
            const wrapped = {
              setActionHandler(action, handler) {
                const result = original.call(this, action, handler);
                if (typeof handler === 'function') handlers.set(String(action), handler);
                else handlers.delete(String(action));
                return result;
              }
            }.setActionHandler;
            Object.defineProperty(proto, 'setActionHandler', { value: wrapped, writable: true, enumerable: true, configurable: true });
            Object.defineProperty(window, key, { value: Object.freeze({
              has: action => handlers.has(action),
              run: action => {
                const handler = handlers.get(action);
                if (!handler) return false;
                handler({ action });
                return true;
              }
            }) });
          } catch (e) { }
        })();
        """;

    /// <summary>
    /// YouTube's own next and previous buttons on its player, for when it has not handed its
    /// handlers over: it hands "next" over on a schedule of its own, and a press a few seconds
    /// into a playlist found none (measured). Only on YouTube, only a button that is shown and
    /// not marked disabled. Defines ytButton(selector) for the scripts below.
    /// </summary>
    private const string YouTubeButtons = """
          const ytButton = selector => {
            if (!/(^|\.)youtube\.com$/.test(location.hostname)) return null;
            const b = document.querySelector(selector);
            return b && b.offsetParent !== null && b.getAttribute('aria-disabled') !== 'true' ? b : null;
          };
        """;

    /// <summary>What the page plays, as an object: <see cref="Parse"/> reads it.</summary>
    internal const string ReadScript = """
        (() => {
        """ + YouTubeButtons + """
          const api = window[Symbol.for('gergur.media')];
          const has = action => { try { return !!(api && api.has(action)); } catch (e) { return false; } };
          const loaded = Array.from(document.querySelectorAll('video, audio')).filter(m => (m.currentSrc || m.srcObject) && m.readyState > 0);
          const playing = loaded.some(m => !m.paused && !m.ended);
          const session = navigator.mediaSession;
          const meta = session && session.metadata;
          const state = session ? session.playbackState : 'none';
          return {
            title: meta ? String(meta.title || '') : '',
            artist: meta ? String(meta.artist || '') : '',
            media: loaded.length > 0 || has('play') || has('pause'),
            playing: playing || state === 'playing',
            paused: !playing && state === 'paused',
            next: has('nexttrack') || !!ytButton('.ytp-next-button'),
            previous: has('previoustrack') || !!ytButton('.ytp-prev-button')
          };
        })()
        """;

    /// <summary>
    /// Presses one of the site's buttons, or plays and pauses its media directly when it gave
    /// none, and on YouTube presses its player's own next and previous when the handlers are
    /// not there. Answers what it did ("play", "pause", "next", "previous") or "none".
    /// </summary>
    internal static string CommandScript(MediaAction action)
    {
        string name = action switch
        {
            MediaAction.Toggle => "toggle",
            MediaAction.Play => "play",
            MediaAction.Pause => "pause",
            MediaAction.Next => "next",
            MediaAction.Previous => "previous",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        // The action is one of the fixed words above, never text from anywhere else.
        return """
            ((action) => {
            """ + YouTubeButtons + """
              const api = window[Symbol.for('gergur.media')];
              const loaded = Array.from(document.querySelectorAll('video, audio')).filter(m => (m.currentSrc || m.srcObject) && m.readyState > 0);
              const playing = loaded.filter(m => !m.paused && !m.ended);
              const session = navigator.mediaSession;
              let act = action;
              if (act === 'toggle') act = (playing.length > 0 || (session && session.playbackState === 'playing')) ? 'pause' : 'play';
              const handler = act === 'next' ? 'nexttrack' : act === 'previous' ? 'previoustrack' : act;
              try { if (api && api.run(handler)) return act; } catch (e) { }
              const button = act === 'next' ? ytButton('.ytp-next-button') : act === 'previous' ? ytButton('.ytp-prev-button') : null;
              if (button) { button.click(); return act; }
              if (act === 'pause') { playing.forEach(m => m.pause()); return playing.length > 0 ? act : 'none'; }
              if (act === 'play') {
                const m = loaded.find(x => x.paused);
                if (!m) return 'none';
                const p = m.play();
                if (p && p.catch) p.catch(() => { });
                return act;
              }
              if (act === 'previous') {
                const m = playing[0] || loaded[0];
                if (m) { m.currentTime = 0; return act; }
              }
              return 'none';
            })('ACTION')
            """.Replace("ACTION", name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads <see cref="ReadScript"/>'s answer as ExecuteScriptAsync hands it back (JSON).
    /// Null for anything that is not that object. Texts are cut to <see cref="MaxText"/> and
    /// cleared of control characters, since the page wrote them.
    /// </summary>
    internal static MediaState? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 64 * 1024)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            return new MediaState(
                Clean(Text(root, "title")),
                Clean(Text(root, "artist")),
                Flag(root, "media"),
                Flag(root, "playing"),
                Flag(root, "paused"),
                Flag(root, "next"),
                Flag(root, "previous"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Flag(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>A page's text made fit for one line of the panel.</summary>
    internal static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var sb = new StringBuilder(Math.Min(text.Length, MaxText));
        bool space = false;
        foreach (char c in text)
        {
            // Controls, spaces of every kind, and invisible formatting (direction overrides,
            // zero-width joiners, the byte order mark), which could reorder or hide what shows.
            if (char.IsControl(c) || char.IsWhiteSpace(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
            {
                space = sb.Length > 0;
                continue;
            }
            if (space)
            {
                sb.Append(' ');
                space = false;
            }
            sb.Append(c);
            if (sb.Length >= MaxText)
                break;
        }
        return sb.ToString();
    }

    /// <summary>What a command's answer says was done: the word, unquoted, or "none".</summary>
    internal static string Done(string? json)
    {
        if (json is null)
            return "none";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() ?? "none" : "none";
        }
        catch (JsonException)
        {
            return "none";
        }
    }
}
