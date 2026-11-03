// Runs AgentServer.PasswordStateJs, LoginStateJs and the account-choosing scripts against a
// small stand-in for a page, so what they decide is exercised rather than read. Run by
// SavedLoginsTests with the scripts written to files: node LoginScripts.harness.js
// <passwordState.js> <loginState.js> <addPick.js> <pickState.js> <clearForPick.js> <emptyPassword.js> <removePick.js>
// The stand-in has only what the scripts use; the real engine is the live test's job.
"use strict";
const fs = require("fs");
const passwordStateSource = fs.readFileSync(process.argv[2], "utf8");
const loginStateSource = fs.readFileSync(process.argv[3], "utf8");
const [addPickSource, pickStateSource, clearSource, emptySource, removeSource] =
    [4, 5, 6, 7, 8].map(i => fs.readFileSync(process.argv[i], "utf8"));

let failures = 0;
function check(name, actual, expected) {
    const a = JSON.stringify(actual), e = JSON.stringify(expected);
    if (a === e) console.log("ok   " + name);
    else { failures++; console.log("FAIL " + name + ": got " + a + ", wanted " + e); }
}

class El {
    constructor(tag, props = {}, children = []) {
        this.tagName = tag.toUpperCase();
        this.attrs = {};
        this.rect = { width: 120, height: 24 };
        this.style = {};
        this.children = [];
        this.parent = null;
        for (const [k, v] of Object.entries(props)) {
            if (k === "attrs") Object.assign(this.attrs, v);
            else this[k] = v;
        }
        for (const c of children) this.append(c);
    }
    append(c) { c.parent = this; this.children.push(c); return c; }
    before(c) { const at = this.parent.children.indexOf(this); c.parent = this.parent; this.parent.children.splice(at, 0, c); }
    remove() { if (this.parent) this.parent.children.splice(this.parent.children.indexOf(this), 1); this.parent = null; }
    getAttribute(n) { return Object.prototype.hasOwnProperty.call(this.attrs, n) ? this.attrs[n] : null; }
    setAttribute(n, v) { this.attrs[n] = String(v); }
    getBoundingClientRect() { return this.rect; }
    descendants() {
        const out = [];
        const walk = el => { for (const c of el.children) { out.push(c); walk(c); } };
        walk(this);
        return out;
    }
    querySelectorAll(selector) {
        if (this.throws) throw new Error("this page throws");
        const all = this.descendants();
        switch (selector) {
            case "*": return all;
            case "input": return all.filter(e => e.tagName === "INPUT");
            case "iframe, frame": return all.filter(e => e.tagName === "IFRAME" || e.tagName === "FRAME");
            case "input[type=password]": return all.filter(e => e.tagName === "INPUT" && e.type === "password");
            default: {
                const m = /^\[data-gergur-login="([^"]+)"\]$/.exec(selector);
                if (m) return all.filter(e => e.getAttribute("data-gergur-login") === m[1]);
                throw new Error("the stand-in does not know the selector " + selector);
            }
        }
    }
    querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
    compareDocumentPosition(other) {
        const order = docOrder();
        return order.indexOf(other) < order.indexOf(this) ? 2 : 4;   // PRECEDING : FOLLOWING
    }
}
class Input extends El {
    constructor(props = {}) { super("input", Object.assign({ type: "text", value: "", name: "", id: "", disabled: false, readOnly: false }, props)); }
}
class Button extends El {
    constructor(props = {}) { super("button", Object.assign({ type: "submit", disabled: false }, props)); }
}
class Form extends El {
    constructor(props = {}, children = []) { super("form", props, children); }
    get elements() { return this.descendants().filter(e => e instanceof Input || e instanceof Button); }
}
function frame(doc) { return new El("iframe", { contentDocument: doc }); }
function foreignFrame() {
    const f = new El("iframe");
    Object.defineProperty(f, "contentDocument", { get() { throw new Error("blocked a frame with a different origin"); } });
    return f;
}
function shadowHost(root) { return new El("div", { shadowRoot: root }); }

let page;
function docOrder() { return [page, ...page.descendants()]; }
function load(...children) {
    page = new El("document", {}, children);
    page.createElement = tag => tag === "input" ? new Input() : new El(tag);
    for (const input of page.descendants().filter(e => e instanceof Input || e instanceof Button)) {
        let up = input.parent;
        while (up && !(up instanceof Form)) up = up.parent;
        input.form = up || null;
    }
    return page;
}

global.HTMLInputElement = Input;
global.HTMLButtonElement = Button;
global.Node = { DOCUMENT_POSITION_PRECEDING: 2 };
global.getComputedStyle = el => ({ visibility: el.style.visibility || "visible", display: el.style.display || "inline" });
function run(source) {
    global.document = page;
    return new Function("return " + source)();
}
const passwordState = () => run(passwordStateSource);
const loginState = () => run(loginStateSource);
const addPick = () => run(addPickSource);
const pickState = () => run(pickStateSource);
const clearForPick = () => run(clearSource);
const emptyPassword = () => run(emptySource);
const removePick = () => run(removeSource);

// ---------------------------------------------------------------- PasswordStateJs
load();
check("an empty page holds no password", passwordState(), 0);
load(new Input({ type: "password" }));
check("an empty password field is none", passwordState(), 0);
load(new Input({ type: "password", value: "x" }));
check("a filled password field is one, as dots", passwordState(), 1);
load(new Input({ type: "text", value: "x", attrs: { autocomplete: "current-password" } }));
check("a revealed field that says current-password is in plain text", passwordState(), 2);
load(new Input({ type: "text", value: "x", attrs: { autocomplete: "new-password" } }));
check("so is new-password", passwordState(), 2);
load(new Input({ type: "text", value: "x", name: "password" }));
check("a revealed field named password is in plain text", passwordState(), 2);
load(new Input({ type: "text", value: "x", id: "loginPwd" }));
check("as is one with pwd in its id", passwordState(), 2);
load(new Input({ type: "text", value: "x", name: "pass" }));
check("and one named just pass", passwordState(), 2);
load(new Input({ type: "text", value: "x", name: "passport" }), new Input({ type: "text", value: "x", id: "compass" }));
check("passport and compass are not passwords", passwordState(), 0);
load(new Input({ type: "text", value: "x", attrs: { "data-gergur-login": "pass-abc" } }));
check("a field /login marked is one after it turns to text", passwordState(), 2);
load(new Input({ type: "checkbox", value: "on", name: "show_password" }), new Input({ type: "checkbox", value: "on", id: "rememberPassword" }));
check("a show-password or remember-password checkbox is not a password", passwordState(), 0);
load(new Input({ type: "hidden", value: "x", name: "password" }));
check("a hidden field holding one counts, as not on screen", passwordState(), 1);
load(new Input({ type: "email", value: "someone@example.com", attrs: { autocomplete: "username" } }));
check("a filled username is not a password", passwordState(), 0);
load(shadowHost(new El("shadow", {}, [new Input({ type: "password", value: "x" })])));
check("a password in an open shadow root counts", passwordState(), 1);
load(frame(new El("document", {}, [new Input({ type: "password", value: "x" })])));
check("a password in a frame this page can reach counts", passwordState(), 1);
load(foreignFrame(), new Input({ type: "text", value: "hello" }));
check("a frame from another site is passed over", passwordState(), 0);
load(new Input({ type: "password", value: "x" }), new Input({ type: "text", value: "x", name: "password2" }));
check("dots and plain text together read as plain text", passwordState(), 2);
load();
page.throws = true;
check("a page that throws reads as a password in plain text, not as none", passwordState(), 2);

// ---------------------------------------------------------------- LoginStateJs
load(new Input({ type: "text" }));
check("no password field", loginState(), { fields: 0, elsewhere: false });
load(shadowHost(new El("shadow", {}, [new Input({ type: "password" })])));
check("one only in a shadow root is elsewhere", loginState(), { fields: 0, elsewhere: true });
load(frame(new El("document", {}, [new Input({ type: "password" })])));
check("one only in a reachable frame is elsewhere", loginState(), { fields: 0, elsewhere: true });
load(foreignFrame());
check("a frame from another site is not taken for a sign-in form", loginState(), { fields: 0, elsewhere: false });

let user, pass, go;
load(new Form({}, [
    user = new Input({ type: "email", value: "someone@example.com" }),
    pass = new Input({ type: "password", value: "secret-value" }),
    go = new Button({ type: "submit" }),
]));
check("a filled form: the account, filled, a button, a field to type in", loginState(),
    { fields: 1, filled: true, user: "someone@example.com", button: true, userField: true, pageUser: null });
check("the username field is marked with the nonce", user.getAttribute("data-gergur-login"), "user-n0nce");
check("the field is marked with the nonce", pass.getAttribute("data-gergur-login"), "pass-n0nce");
check("and so is the button", go.getAttribute("data-gergur-login"), "go-n0nce");
check("and the answer never holds the password", JSON.stringify(loginState()).includes("secret-value"), false);

load(new Form({}, [
    new Input({ type: "password", value: "" }),
    new Input({ type: "email", value: "after@example.com" }),
    new Button({ type: "button" }),
    new Button({ type: "submit", disabled: true }),
    new Input({ type: "submit" }),
]));
check("empty; a username after it is not its; type=button and disabled passed over for input submit",
    loginState(), { fields: 1, filled: false, user: null, button: true, userField: false, pageUser: null });

load(new Input({ type: "password", value: "x", rect: { width: 0, height: 0 } }),
    new Input({ type: "password", value: "", style: { visibility: "hidden" } }),
    new Input({ type: "password", value: "y" }));
check("hidden password fields are not counted, and no form means no button",
    loginState(), { fields: 1, filled: true, user: null, button: false, userField: false, pageUser: null });

load(new Form({}, [new Input({ type: "password", value: "a" }), new Input({ type: "password", value: "" })]));
check("two shown password fields are counted as two", loginState().fields, 2);

// ---------------------------------------------------------------- the account a page carries
const lookFor = (...fields) => { load(new Form({}, [...fields, new Input({ type: "password" })])); return loginState(); };
check("a read-only username is the account, not a field to type in",
    lookFor(new Input({ type: "email", value: "person-a", readOnly: true })),
    { fields: 1, filled: false, user: "person-a", button: false, userField: false, pageUser: null });
check("a hidden field named username is the account a two-step page carries",
    lookFor(new Input({ type: "hidden", name: "username", value: "person-a" })).pageUser, "person-a");
check("so is a hidden field whose autocomplete says username",
    lookFor(new Input({ type: "hidden", name: "x1", value: "person-a", attrs: { autocomplete: "username" } })).pageUser, "person-a");
check("and one with the id identifier",
    lookFor(new Input({ type: "hidden", id: "identifier", value: "person-a@example.com" })).pageUser, "person-a@example.com");
check("a hidden token named like login_challenge is not an account",
    lookFor(new Input({ type: "hidden", name: "login_challenge", value: "abc123" })).pageUser, null);
check("nor a value with a space in it",
    lookFor(new Input({ type: "hidden", name: "username", value: "two words" })).pageUser, null);
load(new Form({}, [new Input({ type: "password" }), new Input({ type: "hidden", name: "username", value: "after" })]));
check("nor one after the password field", loginState().pageUser, null);
check("an editable username field means no page account",
    lookFor(new Input({ type: "hidden", name: "username", value: "person-a" }), new Input({ type: "text" })).pageUser, null);

// ---------------------------------------------------------------- choosing an account
let pw;
load(new Form({}, [new Input({ type: "hidden", name: "username", value: "person-a" }), pw = new Input({ type: "password" }), new Button()]));
loginState();
check("a username field of Gergur's own goes in", addPick(), true);
const added = page.descendants().find(e => e.getAttribute && e.getAttribute("data-gergur-login") === "pick-n0nce");
check("marked, a username field", [added && added.tagName, added && added.type, added && added.autocomplete], ["INPUT", "text", "username"]);
check("just before the password field", pw.parent.children.indexOf(added) + 1, pw.parent.children.indexOf(pw));
added.value = "person-b";
pw.value = "not-a-real-pass-99";
const picked = pickState();
check("what was chosen: the username and that the password is filled", picked, { user: "person-b", filled: true });
check("never the password", JSON.stringify(picked).includes("not-a-real-pass"), false);
emptyPassword();
check("emptying the password leaves the username", [added.value, pw.value], ["person-b", ""]);
pw.value = "x";
clearForPick();
check("clearing for a pick empties both", [added.value, pw.value], ["", ""]);
removePick();
check("and the added field is taken away again", page.descendants().includes(added), false);
load(new Form({}, [new Input({ type: "text" })]));
check("with no marked password field nothing is added", addPick(), false);

console.log(failures === 0 ? "all passed" : failures + " failed");
process.exit(failures === 0 ? 0 : 1);
