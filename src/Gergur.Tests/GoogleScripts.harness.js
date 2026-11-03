// Runs GoogleSignIn.ChoiceScript and MarkJs against a small stand-in for Google's account
// chooser, so what they decide is exercised rather than read. Run by GoogleSignInTests:
// node GoogleScripts.harness.js <choice.js for origin http://accounts.localhost:41791>
//   <mark person.b@example.com> <mark person.c@example.com> <mark nobody@example.com>
// Made-up accounts only. The real page is the live test's job, and Google's own is nobody's.
"use strict";
const fs = require("fs");
const vm = require("vm");
const [choiceSource, markB, markC, markNobody] = [2, 3, 4, 5].map(i => fs.readFileSync(process.argv[i], "utf8"));
const ORIGIN = "http://accounts.localhost:41791";

let failures = 0;
function check(name, actual, expected) {
    const a = JSON.stringify(actual), e = JSON.stringify(expected);
    if (a === e) console.log("ok   " + name);
    else { failures++; console.log("FAIL " + name + ": got " + a + ", wanted " + e); }
}

class El {
    constructor(attrs = {}, children = [], shown = true) {
        this.attrs = Object.assign({}, attrs);
        this.rect = shown ? { width: 300, height: 48 } : { width: 0, height: 0 };
        this.children = [];
        this.parent = null;
        for (const c of children) { c.parent = this; this.children.push(c); }
    }
    getAttribute(n) { return Object.prototype.hasOwnProperty.call(this.attrs, n) ? this.attrs[n] : null; }
    setAttribute(n, v) { this.attrs[n] = String(v); }
    removeAttribute(n) { delete this.attrs[n]; }
    getBoundingClientRect() { return this.rect; }
    matches(selector) {
        return selector.split(",").some(part => {
            const m = /^\[([\w-]+)\]$/.exec(part.trim());
            if (!m) throw new Error("the stand-in does not know the selector " + part);
            return this.getAttribute(m[1]) !== null;
        });
    }
    closest(selector) {
        for (let el = this; el; el = el.parent) if (el.matches(selector)) return el;
        return null;
    }
    all() { return this.children.flatMap(c => [c, ...c.all()]); }
}

function chooser() {
    const a = new El({ "data-identifier": "person.a@example.com" }, [new El({ class: "name" })]);
    const b = new El({ "data-identifier": "Person.B@Example.com" }, [new El({ class: "name" }), new El({ class: "email" })]);
    const c = new El({ "data-email": "person.c@example.com" }, [], false);   // not shown
    const another = new El({ "data-other": "use another account" });
    const old = new El({ "data-gergur-google": "stale" });
    const root = new El({}, [a, b, c, another, old]);
    const listeners = {};
    const document = {
        querySelectorAll: s => root.all().filter(el => el.matches(s)),
        addEventListener: (type, f, capture) => { (listeners[type] = listeners[type] || []).push({ f, capture }); },
    };
    return { root, a, b, c, another, old, document, listeners };
}

function run(source, page, origin, inFrame) {
    const posted = [];
    const ctx = { document: page.document, location: { origin }, posted, JSON, Set, Object };
    ctx.window = ctx;
    ctx.top = inFrame ? {} : ctx;
    ctx.chrome = { webview: { postMessage: m => posted.push(m) } };
    const value = vm.runInNewContext(source, ctx);
    return { value, posted };
}

function fire(page, type, target, key) {
    for (const l of page.listeners[type] || []) l.f({ type, target, key, isTrusted: true });
}

// The choice script: only on the sign-in origin's top page, only an account entry, only an address.
{
    const page = chooser();
    const { posted } = run(choiceSource, page, ORIGIN, false);
    check("listens in the capture phase", (page.listeners.click || []).map(l => l.capture), [true]);
    fire(page, "click", page.b.children[1]);
    check("a click inside an entry posts its account", posted, ["GERGUR_GOOGLE:Person.B@Example.com"]);
    fire(page, "click", page.another);
    check("a click elsewhere posts nothing", posted.length, 1);
    fire(page, "keydown", page.a, "a");
    check("a letter posts nothing", posted.length, 1);
    fire(page, "keydown", page.a, "Enter");
    check("Enter on an entry posts it", posted[1], "GERGUR_GOOGLE:person.a@example.com");
    page.a.attrs["data-identifier"] = "not an address";
    fire(page, "click", page.a);
    check("an entry that is not an address posts nothing", posted.length, 2);
}
{
    const page = chooser();
    run(choiceSource, page, "https://example.com", false);
    check("does nothing on another site", Object.keys(page.listeners), []);
    const framed = chooser();
    run(choiceSource, framed, ORIGIN, true);
    check("does nothing in a frame", Object.keys(framed.listeners), []);
}

// The mark: the shown entry for the account, matched without regard to case, old marks gone,
// and only a count of the accounts said.
{
    const page = chooser();
    const { value } = run(markB, page, ORIGIN, false);
    check("finds the account, counting the shown ones", value, { found: true, accounts: 2 });
    check("marks that entry", page.b.getAttribute("data-gergur-google"), "n0nce");
    check("and no other", [page.a, page.c, page.old].map(el => el.getAttribute("data-gergur-google")), [null, null, null]);
}
{
    const page = chooser();
    check("an entry not shown is not found", run(markC, page, ORIGIN, false).value, { found: false, accounts: 2 });
    check("an account not listed is not found", run(markNobody, page, ORIGIN, false).value, { found: false, accounts: 2 });
    check("and nothing is marked", page.root.all().filter(el => el.getAttribute("data-gergur-google") !== null).length, 0);
}

console.log(failures === 0 ? "all passed" : failures + " failed");
process.exit(failures === 0 ? 0 : 1);
