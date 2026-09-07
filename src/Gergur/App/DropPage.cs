namespace Gergur.App;

/// <summary>
/// The page the phone loads. Served by <see cref="DropServer"/> and deliberately
/// self-contained: no fonts, scripts or styles fetched from anywhere, so it works on a
/// network with no internet and nothing about it leaves the machine.
///
/// Add it to the iPhone home screen and it opens full screen with its own icon, which
/// is as close to an app as this needs to be. It carries the pairing key in its own url,
/// so every request it makes reuses that rather than storing a second copy.
/// </summary>
internal static class DropPage
{
    /// <summary>
    /// Escapes text destined for the page. Everything here originates locally, but the
    /// habit is cheap and the alternative is remembering which strings are safe.
    /// </summary>
    internal static string Escape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&#39;");

    /// <summary>
    /// The page a share-sheet Shortcut lands on. It flashes briefly, so it says what
    /// happened in one line and nothing else.
    /// </summary>
    /// <param name="good">
    /// False turns the tick into a warning. A page that says something went wrong under
    /// a green tick is worse than no page.
    /// </param>
    public static string Result(string title, string detail, bool good = true) => """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>__TITLE__</title>
        <style>
          :root { color-scheme: dark; }
          body { margin:0; height:100vh; display:grid; place-items:center; text-align:center;
                 background:#0a0d16; color:#e8eef8;
                 font:400 17px/1.5 -apple-system,"Segoe UI",system-ui,sans-serif; }
          .tick { width:64px; height:64px; border-radius:50%; background:__ACCENT__; margin:0 auto 1.25rem;
                  display:grid; place-items:center; font-size:32px; color:#fff; }
          h1 { font-size:1.35rem; margin:0 0 .35rem; font-weight:600; }
          p { margin:0; color:#8894ac; }
        </style></head>
        <body><div><div class="tick">__GLYPH__</div><h1>__TITLE__</h1><p>__DETAIL__</p></div></body></html>
        """
        // Both of these are chosen here, never passed in, so neither is a way into the markup.
        .Replace("__ACCENT__", good ? "#3d7bfa" : "#c2503c")
        .Replace("__GLYPH__", good ? "&#10003;" : "&#33;")
        .Replace("__TITLE__", Escape(title))
        .Replace("__DETAIL__", Escape(detail));

    /// <summary>
    /// How to build the share-sheet Shortcut by hand. An iCloud shortcut link cannot be
    /// generated from here, so this gives the exact url to paste and the handful of taps
    /// around it, on the phone where you are doing them.
    /// </summary>
    public static string Setup(string key, string? address, int port)
    {
        // No address means no adapter worth naming and no Host header to fall back on.
        // Printing "your-pc-ip" as though it were one, with a Copy button beside it,
        // sends you to Shortcuts with an address that cannot work and nothing to say why.
        if (string.IsNullOrWhiteSpace(address))
        {
            return Result(
                "Cannot tell what this PC's address is",
                "Check both devices are on the same Wi-Fi, then open this page again.",
                good: false);
        }

        string url = $"http://{address}:{port}/share?k={key}&text=";
        return """
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
            <meta name="apple-mobile-web-app-capable" content="yes">
            <title>Send from any app</title>
            <style>
              :root { color-scheme: dark; }
              * { box-sizing:border-box; }
              body { margin:0; background:#0a0d16; color:#e8eef8;
                     font:400 16px/1.6 -apple-system,"Segoe UI",system-ui,sans-serif;
                     padding: env(safe-area-inset-top) 16px env(safe-area-inset-bottom); }
              h1 { font-size:1.3rem; margin:1.25rem 0 .25rem; }
              .lead { color:#8894ac; margin:0 0 1.25rem; }
              ol { padding-left:1.2rem; margin:0 0 1.5rem; }
              li { margin-bottom:.85rem; }
              code { background:#141c30; border:1px solid #2a3a5c; border-radius:6px;
                     padding:.1rem .35rem; font-size:.85rem; }
              .url { background:#141c30; border:1px solid #2a3a5c; border-radius:10px;
                     padding:12px; word-break:break-all; font-size:.85rem; margin-bottom:.6rem; }
              button { background:#3d7bfa; color:#fff; border:0; border-radius:10px;
                       padding:12px 16px; font:600 15px/1 inherit; width:100%; min-height:48px; }
              a { color:#6ea2ff; }
              .note { color:#667; font-size:.85rem; margin-top:1.5rem; }
            </style></head>
            <body>
            <h1>Send from any app</h1>
            <p class="lead">Adds Gergur to the iPhone share sheet, so you can send a page or a
            note without opening this first.</p>

            <div class="url" id="url">__URL__</div>
            <button id="copy">Copy the address</button>

            <ol>
              <li>Open <strong>Shortcuts</strong>, tap <strong>+</strong> to make a new one.</li>
              <li>Add the action <strong>Get Contents of URL</strong>.</li>
              <li>Paste the address above into its URL field.</li>
              <li>With the cursor still at the end, tap <strong>Shortcut Input</strong> on the
                  keyboard bar so it sits right after <code>text=</code>.</li>
              <li>Open the shortcut's settings and turn on <strong>Show in Share Sheet</strong>.</li>
              <li>Set it to accept <strong>Text</strong> and <strong>URLs</strong>.</li>
              <li>Name it <strong>Send to Gergur</strong>.</li>
            </ol>

            <p>Now Share from Safari, Photos or Notes, pick <strong>Send to Gergur</strong>, and it
            lands on your PC.</p>
            <p><a href="__HOME__">Back to the drop</a></p>
            <p class="note">The address contains your pairing key, so treat it like a password.
            Anyone who has it can send to this PC while both are on your Wi-Fi.</p>

            <script>
            // navigator.clipboard exists only in a secure context, and this page is
            // served over plain http on a LAN address, so on the phone it is simply not
            // there. Saying "Copied" anyway sent people to Shortcuts to paste whatever
            // was on the clipboard before, then debug a shortcut that never had the url.
            document.getElementById("copy").addEventListener("click", function () {
              var button = document.getElementById("copy");
              var block = document.getElementById("url");
              var ok = function () { button.textContent = "Copied"; };
              var no = function () { button.textContent = "Select the address above and copy it"; };

              var select = function () {
                var range = document.createRange();
                range.selectNodeContents(block);
                var selection = window.getSelection();
                selection.removeAllRanges();
                selection.addRange(range);
                return range;
              };

              if (navigator.clipboard && window.isSecureContext) {
                navigator.clipboard.writeText(block.textContent).then(ok, function () { select(); no(); });
                return;
              }
              // The old way still works outside a secure context on iOS Safari.
              select();
              var copied = false;
              try { copied = document.execCommand("copy"); } catch (e) { copied = false; }
              if (copied) { ok(); } else { no(); }
            });
            </script>
            </body></html>
            """
            .Replace("__URL__", Escape(url))
            .Replace("__HOME__", Escape($"/?k={key}"));
    }

    public const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
        <meta name="apple-mobile-web-app-capable" content="yes">
        <meta name="apple-mobile-web-app-status-bar-style" content="black-translucent">
        <meta name="apple-mobile-web-app-title" content="Gergur Drop">
        <meta name="theme-color" content="#0a0d16">
        <title>Gergur Drop</title>
        <style>
          :root { color-scheme: dark; }
          * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
          body {
            margin: 0; background: #0a0d16; color: #e8eef8;
            font: 400 16px/1.5 -apple-system, "Segoe UI", system-ui, sans-serif;
            padding: env(safe-area-inset-top) 0 env(safe-area-inset-bottom);
          }
          header {
            padding: 14px 16px; background: #101625; position: sticky; top: 0;
            border-bottom: 1px solid #2a3a5c; display: flex; align-items: center; gap: 10px;
          }
          header h1 { font-size: 17px; margin: 0; font-weight: 600; flex: 1; }
          #status { font-size: 13px; color: #8894ac; }
          .compose { padding: 12px 16px; display: flex; gap: 8px; align-items: flex-end; }
          textarea {
            flex: 1; background: #141c30; color: #e8eef8; border: 1px solid #2a3a5c;
            border-radius: 10px; padding: 10px 12px; font: inherit; resize: none; min-height: 44px;
          }
          button {
            background: #3d7bfa; color: #fff; border: 0; border-radius: 10px;
            padding: 12px 16px; font: 600 15px/1 inherit; min-height: 44px;
          }
          button:disabled { opacity: .5; }
          .file { padding: 0 16px 12px; }
          .file label {
            display: block; text-align: center; padding: 12px; border: 1px dashed #2a3a5c;
            border-radius: 10px; color: #8894ac; font-size: 15px;
          }
          .file input { display: none; }
          ul { list-style: none; margin: 0; padding: 0 16px 24px; }
          li { border-top: 1px solid #1a2338; padding: 12px 0; display: flex; gap: 10px; }
          .who { font-size: 12px; color: #667; min-width: 44px; padding-top: 2px; }
          .body { flex: 1; min-width: 0; }
          .body a { color: #6ea2ff; word-break: break-all; }
          .msg { white-space: pre-wrap; word-break: break-word; }
          .meta { font-size: 12px; color: #667; margin-top: 2px; }
          .empty { color: #667; text-align: center; padding: 40px 16px; }
          .setup { display: block; text-align: center; color: #6ea2ff; font-size: 15px;
            padding: 10px; text-decoration: none; }
        </style>
        </head>
        <body>
        <header><h1>Gergur Drop</h1><span id="status"></span></header>

        <div class="compose">
          <textarea id="text" rows="1" placeholder="Send a message or link" enterkeyhint="send"></textarea>
          <button id="send">Send</button>
        </div>
        <div class="file">
          <label for="pick" id="picklabel">Choose a photo or file</label>
          <input id="pick" type="file" multiple>
        </div>

        <div class="file"><a class="setup" id="setup">Send from any app instead</a></div>

        <ul id="list"></ul>
        <div class="empty" id="empty" hidden>Nothing here yet.</div>

        <script>
        // The key travels in this page's own url; reuse it rather than keeping a copy.
        var KEY = new URLSearchParams(location.search).get("k") || "";
        var list = document.getElementById("list");
        var empty = document.getElementById("empty");
        var status = document.getElementById("status");

        // The pending timer is cleared first: without that a second message inherits the
        // first one's countdown and can vanish almost immediately.
        var sayTimer = null;
        function say(text, hold) {
          status.textContent = text;
          if (sayTimer) { clearTimeout(sayTimer); sayTimer = null; }
          if (text) sayTimer = setTimeout(function () { status.textContent = ""; }, hold || 2500);
        }
        function url(path) { return path + (path.indexOf("?") < 0 ? "?" : "&") + "k=" + encodeURIComponent(KEY); }

        function when(iso) {
          var d = new Date(iso), now = new Date();
          var t = d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
          return d.toDateString() === now.toDateString() ? t : d.toLocaleDateString() + " " + t;
        }

        function size(n) {
          if (n >= 1048576) return (n / 1048576).toFixed(1) + " MB";
          if (n >= 1024) return Math.round(n / 1024) + " KB";
          return n + " B";
        }

        function render(items) {
          list.textContent = "";
          // Put the ordinary wording back: the error path in refresh() rewrites it.
          empty.textContent = "Nothing here yet.";
          empty.hidden = items.length > 0;
          items.forEach(function (item) {
            var li = document.createElement("li");
            var who = document.createElement("div");
            who.className = "who";
            who.textContent = item.from === "phone" ? "phone" : "PC";
            var body = document.createElement("div");
            body.className = "body";

            if (item.kind === "file") {
              var a = document.createElement("a");
              a.href = url("/file/" + encodeURIComponent(item.id));
              a.textContent = item.text;
              a.setAttribute("download", item.text);
              body.appendChild(a);
              var meta = document.createElement("div");
              meta.className = "meta";
              meta.textContent = size(item.size) + "  ·  " + when(item.at);
              body.appendChild(meta);
            } else if (item.kind === "link") {
              var link = document.createElement("a");
              link.href = item.text;
              link.textContent = item.text;
              link.rel = "noreferrer";
              body.appendChild(link);
              var lm = document.createElement("div");
              lm.className = "meta";
              lm.textContent = when(item.at);
              body.appendChild(lm);
            } else {
              var p = document.createElement("div");
              p.className = "msg";
              p.textContent = item.text;
              body.appendChild(p);
              var tm = document.createElement("div");
              tm.className = "meta";
              tm.textContent = when(item.at);
              body.appendChild(tm);
            }
            li.appendChild(who);
            li.appendChild(body);
            list.appendChild(li);
          });
        }

        function refresh() {
          // A 403 rendered as an empty list, so a wrong or stale key looked exactly like
          // a drop with nothing in it.
          return fetch(url("/items"), { cache: "no-store" })
            .then(ok)
            .then(function (r) { return r.json(); })
            .then(render)
            .catch(function (err) {
              var why = err.message || "offline";
              say(why);
              // The status line clears itself after a couple of seconds, and the list is
              // hidden until a render happens, so without this the first failed load
              // leaves a blank page with nothing on it at all.
              if (list.children.length === 0) {
                empty.textContent = why + ". Reopen this page from the pairing link.";
                empty.hidden = false;
              }
            });
        }

        // fetch only rejects on a network failure, so without this every refusal the
        // server sends (empty, too large, incomplete) rendered as "sent" and the photo
        // got deleted off the phone in the belief it had arrived.
        function ok(r) {
          if (r.ok) return r;
          return r.json().catch(function () { return {}; }).then(function (body) {
            throw new Error(body.error || ("refused (" + r.status + ")"));
          });
        }

        document.getElementById("send").addEventListener("click", function () {
          var box = document.getElementById("text");
          var text = box.value.trim();
          if (!text) return;
          fetch(url("/send"), {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ text: text })
          }).then(ok)
            .then(function () { box.value = ""; say("sent"); return refresh(); })
            .catch(function (err) { say(err.message || "failed", 6000); });
        });

        document.getElementById("pick").addEventListener("change", function (e) {
          var files = Array.prototype.slice.call(e.target.files || []);
          if (!files.length) return;
          var label = document.getElementById("picklabel");
          var done = 0;
          label.textContent = "Sending 0 of " + files.length;
          // Posted as the raw body with the name in the query, so there is no multipart
          // parser on the other end to get wrong.
          // Every file is attempted, even after one fails. Aborting the chain left the
          // rest of a multi-photo send silently unsent, with only the failing name shown.
          var failures = [];
          files.reduce(function (chain, file) {
            return chain.then(function () {
              return fetch(url("/upload") + "&name=" + encodeURIComponent(file.name), {
                method: "POST", body: file
              }).then(ok)
                .then(function () { label.textContent = "Sending " + (++done) + " of " + files.length; })
                .catch(function (err) { failures.push(file.name + ": " + (err.message || "failed")); });
            });
          }, Promise.resolve())
            .then(function () {
              label.textContent = "Choose a photo or file";
              if (failures.length === 0) { say("sent"); }
              else if (failures.length === 1) { say(failures[0], 6000); }
              else { say(done + " sent, " + failures.length + " failed: " + failures.join("; "), 8000); }
              return refresh();
            });
          e.target.value = "";
        });

        // The key travels with every link on this page, never as a second stored copy.
        document.getElementById("setup").href = url("/setup");

        refresh();
        setInterval(refresh, 4000);
        document.addEventListener("visibilitychange", function () { if (!document.hidden) refresh(); });
        </script>
        </body>
        </html>
        """;
}
