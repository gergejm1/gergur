// Runs AgentServer.PasswordStateJs and LoginStateJs against a small stand-in for a page, so
// what they decide is exercised rather than read. Run by SavedLoginsTests with the two
// scripts written to files: node LoginScripts.harness.js <passwordState.js> <loginState.js>
// The stand-in has only what the scripts use; the real engine is the live test's job.
"use strict";
const fs = require("fs");
const passwordStateSource = fs.readFileSync(process.argv[2], "utf8");
const loginStateSource = fs.readFileSync(process.argv[3], "utf8");

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
    constructor(props = {}) { super("input", Object.assign({ type: "text", value: "", name: "", id: "", disabled: false }, props)); }
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
check("a filled form: the account, filled, a button", loginState(), { fields: 1, filled: true, user: "someone@example.com", button: true });
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
    loginState(), { fields: 1, filled: false, user: null, button: true });

load(new Input({ type: "password", value: "x", rect: { width: 0, height: 0 } }),
    new Input({ type: "password", value: "", style: { visibility: "hidden" } }),
    new Input({ type: "password", value: "y" }));
check("hidden password fields are not counted, and no form means no button",
    loginState(), { fields: 1, filled: true, user: null, button: false });

load(new Form({}, [new Input({ type: "password", value: "a" }), new Input({ type: "password", value: "" })]));
check("two shown password fields are counted as two", loginState().fields, 2);

console.log(failures === 0 ? "all passed" : failures + " failed");
process.exit(failures === 0 ? 0 : 1);
