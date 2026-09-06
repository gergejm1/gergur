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

        <ul id="list"></ul>
        <div class="empty" id="empty" hidden>Nothing here yet.</div>

        <script>
        // The key travels in this page's own url; reuse it rather than keeping a copy.
        var KEY = new URLSearchParams(location.search).get("k") || "";
        var list = document.getElementById("list");
        var empty = document.getElementById("empty");
        var status = document.getElementById("status");

        function say(text) { status.textContent = text; if (text) setTimeout(function () { status.textContent = ""; }, 2500); }
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
          return fetch(url("/items"), { cache: "no-store" })
            .then(function (r) { return r.ok ? r.json() : []; })
            .then(render)
            .catch(function () { say("offline"); });
        }

        document.getElementById("send").addEventListener("click", function () {
          var box = document.getElementById("text");
          var text = box.value.trim();
          if (!text) return;
          fetch(url("/send"), {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ text: text })
          }).then(function () { box.value = ""; say("sent"); return refresh(); })
            .catch(function () { say("failed"); });
        });

        document.getElementById("pick").addEventListener("change", function (e) {
          var files = Array.prototype.slice.call(e.target.files || []);
          if (!files.length) return;
          var label = document.getElementById("picklabel");
          var done = 0;
          label.textContent = "Sending 0 of " + files.length;
          // Posted as the raw body with the name in the query, so there is no multipart
          // parser on the other end to get wrong.
          files.reduce(function (chain, file) {
            return chain.then(function () {
              return fetch(url("/upload") + "&name=" + encodeURIComponent(file.name), {
                method: "POST", body: file
              }).then(function () { label.textContent = "Sending " + (++done) + " of " + files.length; });
            });
          }, Promise.resolve())
            .then(function () { label.textContent = "Choose a photo or file"; say("sent"); return refresh(); })
            .catch(function () { label.textContent = "Choose a photo or file"; say("upload failed"); });
          e.target.value = "";
        });

        refresh();
        setInterval(refresh, 4000);
        document.addEventListener("visibilitychange", function () { if (!document.hidden) refresh(); });
        </script>
        </body>
        </html>
        """;
}
