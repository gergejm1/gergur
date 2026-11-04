using System.Text.Json;
using System.Text.RegularExpressions;
using Gergur.Tabs;

namespace Gergur.Music;

/// <summary>What YouTube's player says about the video it has: its own answer, not the page's title.</summary>
/// <param name="VideoId">The video the answer is about; it can lag the address for a moment after a song changes.</param>
/// <param name="Category">YouTube's category, "Music" for a song.</param>
/// <param name="MusicVideoType">Set on YouTube Music (an official video, a track); "" elsewhere.</param>
internal sealed record PlayerAbout(string VideoId, string Title, string Author, int Seconds, bool Live, string Category, string MusicVideoType);

/// <summary>Audio a page heard while one video was on, oldest first.</summary>
internal sealed record HeardPart(string VideoId, short[] Samples);

/// <summary>One read of a page: what it plays, and the audio it heard since the last read.</summary>
/// <param name="Host">The page's host.</param>
/// <param name="VideoId">The video in the address now, "" for none.</param>
/// <param name="About">The player's own answer, null when there is no YouTube player.</param>
/// <param name="SessionTitle">What the page hands Windows as the song's title (media session), "" for none.</param>
/// <param name="SessionArtist">And as its artist.</param>
/// <param name="Listening">The page has a listener running.</param>
/// <param name="AudioState">That listener's audio context state ("running", "suspended"), "" for none.</param>
/// <param name="Parts">What it heard, oldest first.</param>
/// <param name="Ear">Which listener: a new one for each document, "" for none.</param>
internal sealed record PageHearing(string Host, string VideoId, PlayerAbout? About, string SessionTitle, string SessionArtist, bool Listening, string AudioState, IReadOnlyList<HeardPart> Parts, string Ear = "");

/// <summary>A song as it is learned: which one (<paramref name="Key"/>, the same video on either YouTube), and its names.</summary>
/// <param name="Seconds">How long the song is, 0 when not known.</param>
internal sealed record SongInfo(string Key, string Title, string Artist, string Site, int Seconds);

/// <summary>
/// The page side of the song recognizer: a listener that runs in the page, hearing what its
/// video plays (<c>captureStream</c>, mono at <see cref="AudioFingerprint.SampleRate"/>), and
/// what YouTube's player says the video is. Nothing here keeps audio: the browser takes what
/// the page heard every few seconds, and only fingerprints are kept (<see cref="SongLibrary"/>).
///
/// The listener does not change what is heard: its output goes nowhere (a gain of zero), the
/// video is not touched, and a muted tab is heard all the same. It skips digital silence and
/// YouTube's ads, and stops by itself when the browser has not taken from it for
/// <see cref="IdleStop"/>, so a tab nobody asks about any more is not listened to.
/// </summary>
internal static class SongPage
{
    /// <summary>How much the page holds when nobody takes it: older audio goes first.</summary>
    public const int KeptSeconds = 20;

    /// <summary>How long the listener runs without being taken from before it stops itself.</summary>
    public static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(15);

    /// <summary>The longest answer read; <see cref="KeptSeconds"/> of audio in base64 is well under it.</summary>
    internal const int MaxAnswer = 1_000_000;

    /// <summary>A YouTube video id: eleven of letters, digits, '-' and '_'.</summary>
    private static readonly Regex VideoIdShape = new("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads the page and takes what its listener heard. With <paramref name="listen"/> it
    /// starts a listener on the media playing when there is none yet; without, it only takes
    /// from one already running. Answers JSON (<see cref="Parse"/>).
    /// </summary>
    public static string TakeScript(bool listen) => TakeTemplate
        .Replace("__LISTEN__", listen ? "true" : "false", StringComparison.Ordinal)
        .Replace("__RATE__", AudioFingerprint.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("__KEEP__", KeptSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("__IDLE__", ((int)IdleStop.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>Stops the page's listener, if it has one.</summary>
    public const string StopScript = """
        (() => {
          try { const ear = window[Symbol.for('gergur.music')]; if (ear && typeof ear.stop === 'function') ear.stop(); } catch (e) { }
          return true;
        })()
        """;

    private const string TakeTemplate = """
        (() => {
          const KEY = Symbol.for('gergur.music');
          const RATE = __RATE__, KEEP = __KEEP__ * RATE, IDLE = __IDLE__;
          const player = document.getElementById('movie_player');
          const idNow = () => { try { return new URLSearchParams(location.search).get('v') || ''; } catch (e) { return ''; } };
          const adShowing = () => { try { return !!(player && player.classList && player.classList.contains('ad-showing')); } catch (e) { return false; } };
          let about = null;
          try {
            const r = player && typeof player.getPlayerResponse === 'function' ? player.getPlayerResponse() : null;
            const d = r && r.videoDetails;
            if (d) {
              const m = (r.microformat && r.microformat.playerMicroformatRenderer) || {};
              about = { id: String(d.videoId || ''), title: String(d.title || ''), author: String(d.author || ''),
                seconds: Number(d.lengthSeconds) || 0, live: d.isLiveContent === true || d.isLive === true,
                category: String(m.category || ''), type: String(d.musicVideoType || '') };
            }
          } catch (e) { about = null; }
          let session = null;
          try {
            const md = navigator.mediaSession && navigator.mediaSession.metadata;
            if (md) session = { title: String(md.title || ''), artist: String(md.artist || '') };
          } catch (e) { session = null; }

          function start() {
            // Something of the page's own where the listener would go: leave it be.
            const own = Object.getOwnPropertyDescriptor(window, KEY);
            if (own && !own.configurable) return null;
            const media = Array.from(document.querySelectorAll('video, audio')).find(m => !m.paused && !m.ended);
            if (!media || typeof media.captureStream !== 'function') return null;
            const stream = media.captureStream();
            const ctx = new AudioContext({ sampleRate: RATE });
            const proc = ctx.createScriptProcessor(4096, 1, 1);
            const sink = ctx.createGain();
            sink.gain.value = 0;
            let src = null, parts = [], kept = 0, last = Date.now(), done = false;
            const name = Math.random().toString(36).slice(2, 12);
            // A playlist's next song arrives as a new track on the same stream.
            const attach = () => {
              if (done) return;
              if (src) { try { src.disconnect(); } catch (e) { } src = null; }
              const tracks = stream.getAudioTracks();
              if (tracks.length) { src = ctx.createMediaStreamSource(new MediaStream(tracks)); src.connect(proc); }
            };
            const stop = () => {
              if (done) return;
              done = true;
              try { proc.onaudioprocess = null; proc.disconnect(); sink.disconnect(); if (src) src.disconnect(); } catch (e) { }
              try { stream.getTracks().forEach(t => t.stop()); } catch (e) { }
              try { ctx.close(); } catch (e) { }
              try { if (window[KEY] === ear) delete window[KEY]; } catch (e) { }
            };
            proc.onaudioprocess = e => {
              if (Date.now() - last > IDLE) { stop(); return; }
              if (adShowing()) return;
              const d = e.inputBuffer.getChannelData(0);
              let peak = 0;
              for (let i = 0; i < d.length; i++) { const a = d[i] < 0 ? -d[i] : d[i]; if (a > peak) peak = a; }
              if (peak < 0.0005) return;
              const pcm = new Int16Array(d.length);
              for (let i = 0; i < d.length; i++) { const v = Math.round(d[i] * 32767); pcm[i] = v > 32767 ? 32767 : v < -32768 ? -32768 : v; }
              const id = idNow();
              const tail = parts[parts.length - 1];
              if (tail && tail.id === id) tail.chunks.push(pcm); else parts.push({ id, chunks: [pcm] });
              kept += pcm.length;
              while (kept > KEEP && parts.length) {
                const first = parts[0], c = first.chunks.shift();
                kept -= c.length;
                if (!first.chunks.length) parts.shift();
              }
            };
            const encode = chunks => {
              let n = 0;
              for (const c of chunks) n += c.length;
              const all = new Int16Array(n);
              let k = 0;
              for (const c of chunks) { all.set(c, k); k += c.length; }
              const bytes = new Uint8Array(all.buffer);
              let s = '';
              for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
              return btoa(s);
            };
            const ear = Object.freeze({
              take() {
                last = Date.now();
                const out = parts.map(p => ({ id: p.id, pcm: encode(p.chunks) }));
                parts = [];
                kept = 0;
                return { state: String(ctx.state), ear: name, parts: out };
              },
              stop
            });
            stream.addEventListener('addtrack', attach);
            attach();
            proc.connect(sink);
            sink.connect(ctx.destination);
            try { const resumed = ctx.resume(); if (resumed && resumed.catch) resumed.catch(() => { }); } catch (e) { }
            Object.defineProperty(window, KEY, { value: ear, configurable: true, enumerable: false, writable: false });
            return ear;
          }

          let ear = null;
          try { ear = window[KEY]; } catch (e) { ear = null; }
          if (!ear || typeof ear.take !== 'function') ear = null;
          if (!ear && __LISTEN__) { try { ear = start(); } catch (e) { ear = null; } }
          let heard = null;
          try { heard = ear ? ear.take() : null; } catch (e) { heard = null; }
          return JSON.stringify({ host: String(location.hostname || ''), id: idNow(), about, session, ad: adShowing(), heard });
        })()
        """;

    /// <summary>
    /// The answer of <see cref="TakeScript"/>, as the engine returns it (a JSON string holding
    /// JSON). Null for anything else: the page's own script decides what it says, so every
    /// field is checked, texts are cut to one clean line, and audio that does not decode, or is
    /// more than the page could have held, is not taken.
    /// </summary>
    internal static PageHearing? Parse(string? engineJson)
    {
        if (engineJson is null || engineJson.Length > MaxAnswer + 64)
            return null;
        try
        {
            using var outer = JsonDocument.Parse(engineJson);
            if (outer.RootElement.ValueKind != JsonValueKind.String || outer.RootElement.GetString() is not { } inner || inner.Length > MaxAnswer)
                return null;
            using var doc = JsonDocument.Parse(inner);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            PlayerAbout? about = null;
            if (root.TryGetProperty("about", out var a) && a.ValueKind == JsonValueKind.Object)
            {
                about = new PlayerAbout(
                    Text(a, "id"), Text(a, "title"), Text(a, "author"),
                    a.TryGetProperty("seconds", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetDouble(out double sec) && sec is >= 0 and < 1e7 ? (int)sec : 0,
                    a.TryGetProperty("live", out var live) && live.ValueKind == JsonValueKind.True,
                    Text(a, "category"), Text(a, "type"));
            }
            string sessionTitle = "", sessionArtist = "";
            if (root.TryGetProperty("session", out var ms) && ms.ValueKind == JsonValueKind.Object)
            {
                sessionTitle = Text(ms, "title");
                sessionArtist = Text(ms, "artist");
            }
            var parts = new List<HeardPart>();
            bool listening = false;
            string state = "", ear = "";
            if (root.TryGetProperty("heard", out var heard) && heard.ValueKind == JsonValueKind.Object)
            {
                listening = true;
                state = Text(heard, "state");
                ear = Text(heard, "ear");
                long total = 0;
                if (heard.TryGetProperty("parts", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in list.EnumerateArray())
                    {
                        if (part.ValueKind != JsonValueKind.Object || Decode(Text(part, "pcm", raw: true)) is not { } samples)
                            return null;
                        total += samples.Length;
                        if (total > (KeptSeconds + 1) * AudioFingerprint.SampleRate)
                            return null;
                        if (samples.Length > 0)
                            parts.Add(new HeardPart(Text(part, "id"), samples));
                    }
                }
            }
            return new PageHearing(Text(root, "host"), Text(root, "id"), about, sessionTitle, sessionArtist, listening, state, parts, ear);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonElement e, string name, bool raw = false)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            return "";
        string s = v.GetString() ?? "";
        return raw ? s : MediaControl.Clean(s);
    }

    /// <summary>Little-endian 16-bit samples from base64, as the page's Int16Array lays them out; null when it is not that.</summary>
    internal static short[]? Decode(string base64)
    {
        if (base64.Length == 0)
            return [];
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
        if (bytes.Length % 2 != 0)
            return null;
        var samples = new short[bytes.Length / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(bytes[2 * i] | bytes[2 * i + 1] << 8);
        return samples;
    }

    /// <summary>
    /// The host of an address on one of the sites songs are learned from (youtube.com,
    /// www., m. and music.youtube.com), lower case; null for any other address. Read from the
    /// tab's own address, which the page cannot choose.
    /// </summary>
    internal static string? SongSiteOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;
        string host = uri.Host.ToLowerInvariant();
        return host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com" ? host : null;
    }

    /// <summary>The longest video learned as a song: past this it is a mix, an album or a concert.</summary>
    public const int LongestSong = 600;

    /// <summary>
    /// Whether the page plays a song to learn, and which: a video YouTube itself files under
    /// Music (on youtube.com), or anything YouTube Music plays, that is not live and not longer
    /// than <see cref="LongestSong"/>. Only when the player's answer is about the video in the
    /// address, since for a moment after a playlist moves on it still describes the last one.
    /// </summary>
    internal static SongInfo? Judge(PageHearing page)
    {
        if (page.About is not { } about || !VideoIdShape.IsMatch(page.VideoId) || about.VideoId != page.VideoId || about.Live)
            return null;
        if (about.Seconds > LongestSong)
            return null;
        string host = page.Host.ToLowerInvariant();
        string key = "youtube:" + page.VideoId;
        if (host == "music.youtube.com")
        {
            var (t, a) = page.SessionTitle.Length > 0 ? (page.SessionTitle, page.SessionArtist) : SongNames.FromVideo(about.Title, about.Author);
            return t.Length == 0 ? null : new SongInfo(key, t, a, "music.youtube.com", about.Seconds);
        }
        if (host is "www.youtube.com" or "youtube.com" or "m.youtube.com")
        {
            if (about.Category != "Music" || about.Seconds == 0)
                return null;
            var (t, a) = SongNames.FromVideo(about.Title, about.Author);
            return t.Length == 0 ? null : new SongInfo(key, t, a, "youtube.com", about.Seconds);
        }
        return null;
    }
}

/// <summary>A song's name and artist from a music video's title and channel, the way they are usually written.</summary>
internal static partial class SongNames
{
    // "(Official Video)", "[Lyrics]", "(Official 4K Remaster)" and the like.
    [GeneratedRegex(@"\s*[\(\[][^\)\]]*\b(official|video|audio|lyrics?|visuali[sz]er|hd|hq|4k|remaster(ed)?|mv|m/v)\b[^\)\]]*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BracketNoise();

    // A bare "M/V" or "Official Music Video" left at the end.
    [GeneratedRegex(@"\s+(m/v|mv|official\s+(music\s+)?video)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingNoise();

    /// <summary>
    /// "Artist - Song (Official Video)" by "ArtistVEVO" is Song by Artist; a "Artist - Topic"
    /// channel's titles are the song's alone. Otherwise the title as it is, by the channel.
    /// </summary>
    internal static (string Title, string Artist) FromVideo(string videoTitle, string channel)
    {
        string artist = MediaControl.Clean(channel);
        bool topic = artist.EndsWith(" - Topic", StringComparison.Ordinal);
        if (topic)
            artist = artist[..^" - Topic".Length].Trim();
        else if (artist.Length > 4 && artist.EndsWith("VEVO", StringComparison.Ordinal))
            artist = artist[..^4].Trim();
        string title = TrailingNoise().Replace(BracketNoise().Replace(MediaControl.Clean(videoTitle), ""), "").Trim();
        int dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (!topic && dash > 0 && dash + 3 < title.Length)
        {
            artist = title[..dash].Trim();
            title = title[(dash + 3)..].Trim();
        }
        return (title, artist);
    }
}
