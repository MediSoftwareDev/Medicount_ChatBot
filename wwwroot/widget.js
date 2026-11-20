"use strict";
(() => {
    var a = document.querySelector("#stream"),
        E = document.querySelector("#composer"),
        y = document.querySelector("#message"),
        g = document.querySelector("#error"),
        C = document.querySelector("#busy"),
        k = document.querySelector("#session-warning"),
        $ = document.querySelector("#session-warning-text"),
        A = document.querySelector("#extend-session"),
        w = "",
        v = !1,
        T = !1,
        S, x, h,
        l = new Map;
    function s(e, t = "", n) {
        let o = document.createElement(e);
        return o.className = t,
            n !== void 0 && (o.textContent = n), o
    }

    function P(e, t) {
        let n = new DOMParser().parseFromString(t, "text/html");
        function o(i, r) {
            if (i.nodeType === Node.TEXT_NODE) {
                r.append(document.createTextNode(i.textContent ?? ""));
                return
            }
            if (i instanceof HTMLElement) {
                if (["B", "STRONG", "I", "EM", "BR"].includes(i.tagName)) {
                    let c = document.createElement(i.tagName.toLowerCase());
                    r.append(c);
                    for (let b of i.childNodes) o(b, c);
                    return
                }
                if (i.tagName === "A" && ["https://www.chartswap.com", "https://www.chartswap.com/", "https://medicount.patientwallet.com/#/"].includes(i.getAttribute("href") ?? "")) {
                    let c = s("a");
                    c.href = i.getAttribute("href"),
                        c.target = "_blank", c.rel = "noopener noreferrer",
                        c.textContent = `${i.textContent ?? ""} (opens in a new tab)`,
                        r.append(c);
                    return
                } for (let c of i.childNodes) o(c, r)
            }
        }
        for (let i of n.body.childNodes) o(i, e)
    }
    function L() {
        let e = document.activeElement;
        e instanceof HTMLElement && a.contains(e) ? e.scrollIntoView({ block: "nearest", inline: "nearest", behavior: "instant" }) : a.scrollTop = a.scrollHeight
    }
    function F(e) {
        g.textContent = e,
            g.hidden = !1,
            g.tabIndex = -1,
            g.focus()
    }
    function M() {
        a.querySelectorAll(".mc-chips,.mc-form,.mc-review,.mc-link-card").forEach(e => e.remove()), l.clear(), h = void 0
    }
    function N(e) {
        let t = s("div", "mc-visitor", e); a.append(t)
    }
    function H(e = !1) {
        T = !0, w = "", l.clear(), M(), e && a.replaceChildren(), y.disabled = !0, E.querySelector("button").disabled = !0, clearTimeout(S), clearTimeout(x), k.hidden = !0
    }
    function _(e) {
        clearTimeout(S), clearTimeout(x), k.hidden = !0; let t = e.absoluteMs <= e.idleMs, n = Math.min(e.idleMs, e.absoluteMs); x = setTimeout(() => { k.hidden = !1, A.hidden = t; let o = n < 2 * 6e4 ? "less than two minutes" : "two minutes"; $.textContent = t ? `This conversation reaches its maximum duration in ${o}. Use Restart conversation to begin a new session; current entries will be cleared.` : "Your session will expire in two minutes. Select Keep chatting to extend it by 30 minutes and keep your entries." }, Math.max(0, n - 2 * 6e4)), S = setTimeout(() => { H(!0), F("Your session has expired. Use Restart conversation to begin again.") }, n)
    }
    async function m(e, t) {
        let n = await fetch(`/api/${e}`,
            {
                method: "POST", credentials: "omit", cache: "no-store",
                headers: { "content-type": "application/json", ...w ? { Authorization: `Bearer ${w}` } : {} },
                body: JSON.stringify(t ?? {}), signal: AbortSignal.timeout(25e3)
            }),
            o = await n.json();
        if (!n.ok) {
            if (n.status === 401) throw H(!0),
                new Error("Your session has expired. Use Restart conversation to begin again.");
            let i = o.error;
            throw new Error(i === "CHECK_FIELDS" ? "Please complete every field and check the date, email address and phone number."
                : n.status === 429 ? "Please wait a moment before trying again."
                    : n.status === 409 ? "This option is no longer available. Please use the current form or restart the conversation."
                        : "We could not complete that request. Please try again, or call 888-410-6986.")
        } return o.sessionTiming && _(o.sessionTiming), o
    }
    async function p(e) {
        if (!v) {
            v = !0, C.hidden = !1, g.hidden = !0, a.setAttribute("aria-busy", "true"),
                E.querySelector("button").disabled = !0;
            try {
                await e()
            }
            catch (t) {
                 F(t instanceof Error && t.name !== "TimeoutError" && t.name !== "TypeError" ? t.message
                     : "Connection interrupted. Please try again. No payment status has been confirmed.")
            }
            finally {
                v = !1, C.hidden = !0, a.setAttribute("aria-busy", "false"),
                    E.querySelector("button").disabled = T,
                    L()
            }
        }
    }
    function q(e, t, n = "mc-chip") {
        let o = s("button", n, e); return o.type = "button", o.addEventListener("click", t), o
    }
    function Y(e, t, n, o) {
        if (e.type === "checkbox") {
            const label = s("label", "mc-checkbox");
            const input = s("input");
            input.type = "checkbox";
            input.name = e.id;
            input.value = "yes";
            input.checked = n[e.id] === "yes";
            label.append(input, document.createTextNode(e.label));
            t.append(label);
            return;
        }
        let i = s("label", "", e.label), r = s("input"); r.id = `${t.id}-${e.id}`, r.name = e.id, r.type = e.type ?? "text", r.required = e.required, r.autocomplete = "off", r.maxLength = e.maxLength ?? 1e3, r.value = n[e.id] ?? "", r.placeholder = e.mask === "dob" ? "MM/DD/YYYY" : `Enter ${e.label.toLowerCase()}`, e.inputmode && (r.inputMode = e.inputmode), i.htmlFor = r.id, e.type === "email" || e.type === "tel" ? r.setAttribute("autocomplete", e.type) : (o && e.id === "name" || e.label === "Your name") && r.setAttribute("autocomplete", "name"), r.setAttribute("aria-describedby", `${t.id}-instructions`), e.mask === "dob" && (r.pattern = "[0-9]{2}/[0-9]{2}/[0-9]{4}", r.addEventListener("input", () => { let c = r.value.replace(/\D/g, "").slice(0, 8); r.value = [c.slice(0, 2), c.slice(2, 4), c.slice(4)].filter(Boolean).join("/") })), t.append(i, r)
    }
    function O(e) {
        let t = s("form", "mc-form"); t.id = e.formId;
        let n = s("h2", "", e.title); n.id = `${t.id}-title`, t.setAttribute("aria-labelledby", n.id), t.append(n);
        // let o = s(
        //     "p",
        //     "mc-note",
        //     "All fields are required. Review your entries before submitting."
        // );
        let o = s(
            "p",
            "mc-note",
            e.formId === "insurance_update"
                ? "Enter your insurance details and review before submitting."
                : "Review your entries before submitting."
        );
        o.id = `${t.id}-instructions`, t.append(o),
            e.fields.forEach(r => Y(r, t, l.get(e.formId) ?? {},
                e.title === "Your contact details"));
        let i = s("button", "mc-primary", "Review my entries");
        i.type = "submit",
            t.append(i, s("p", "mc-note", e.note)),
            t.addEventListener("submit", r => {
                r.preventDefault(),
                    t.reportValidity() && p(async () => {
                        let c = Object.fromEntries(
                            new FormData(t).entries()),
                            b = await m("form", {
                                formId: e.formId,
                                values: c,
                                confirmed: !1
                            });
                        if (!b.reviewToken) {
                            t.reset(), l.delete(e.formId), u(b);
                            return
                        }
                        l.set(e.formId, c), t.hidden = !0;
                        let d = s("section", "mc-review"); d.append(s("h2", "", "Please confirm your entries"));
                        for (let f of e.fields) {
                            let R = s("div", "mc-review-row");
                            R.append(s("span", "", f.label),
                                s("strong", "", f.type === "checkbox" ? (c[f.id] === "yes" ? "Yes" : "No") : (c[f.id] || "\u2014"))),
                                d.append(R)
                        }
                        let I = s("div", "mc-form-actions");

                        I.append(q("Confirm & submit", () => {
                            p(async () => {
                                let f = await m("form",
                                    {
                                        formId: e.formId,
                                        values: c,
                                        confirmed: true,
                                        reviewToken: b.reviewToken
                                    });

                                // d.remove(),
                                //     f.retryForm ? (t.hidden = !1, u(f),
                                //     t.querySelector("input")?.focus())
                                //     : (l.delete(e.formId), M(), u(f))

                                d.remove();

                                if (f.retryForm) {

                                    t.hidden = false;
                                    u(f);
                                    t.querySelector("input")?.focus();

                                } else {

                                    l.delete(e.formId);

                                    // Close the completed form.
                                    t.remove();

                                    // Clear form-specific UI state.
                                    M();

                                    // Display the next chatbot step.
                                    u(f);
                                }

                            })
                        }, "mc-primary"), q("\u270E Edit my entries", () => {
                            v
                                || (d.remove(),
                                    t.hidden = !1,
                                    t.querySelector("input")?.focus())
                        }, "mc-secondary")), d.append(I), a.append(d), d.querySelector("button")?.focus(), L()
                    })
            }), a.append(t), t.querySelector("input")?.focus()
    }

    function u(e) {
        (e.status === "closed" || e.status === "expired") && a.replaceChildren(),
            e.visitorEcho && N(e.visitorEcho);
        for (let t of e.messages)
            if (t.kind === "text") {
                let n = s("div", `mc-bubble ${t.tone ?? "normal"}`); P(n, t.html);
                let o = s("time", "mc-time", new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })); n.append(o), a.append(n)
            }
            else if (t.kind === "buttons") {
                let n = s("div", "mc-chips");
                for (let o of t.buttons) n.append(q(o.label, () => { p(async () => { let i = await m("choose", { buttonId: o.id }); i.retryForm || M(), N(o.label), u(i), i.retryForm && a.querySelector(".mc-form input")?.focus() }) }));
                a.append(n)
            }
            else if (t.kind === "form") O(t);
            else if (t.kind === "link_card") {
                if (!["https://medicount.patientwallet.com/#/",
                    "https://www.chartswap.com"].includes(t.url)) continue;
                let n = s("section", "mc-link-card");
                n.append(s("h2", "", t.title));
                let o = s("a", "mc-primary", "Open secure payment page (opens in a new tab)");
                o.href = t.url, o.target = "_blank", o.rel = "noopener noreferrer",
                    o.addEventListener("click", () => {
                        m("event", {
                            name: "hosted_payment_opened",
                            data: {}
                        }).catch(() => { })
                    }), n.append(o, s("p", "mc-note", t.note)), a.append(n), t.buttonsAfter && u({ messages: [t.buttonsAfter] })
            }
            else t.kind === "closure" && a.append(s("p", "mc-reference", `Reference: ${t.reference}`));
        if (e.status === "closed" || e.status === "expired") H(),
            document.querySelector("#restart").focus();
        else if (!a.querySelector(".mc-form:not([hidden]),.mc-review")) {
            let t = a.querySelector(".mc-link-card h2");
            t ? (t.tabIndex = -1, t.focus()) : a.querySelector(".mc-chips button")?.focus()
        } L()
    }
    async function B() {
        w = "",
            T = !1,
            l.clear(),
            h = void 0,
            a.replaceChildren(),
            y.disabled = !1,
            y.value = "";
        let e = await m("session");
        w = e.token,
            u(e)
    }
    E.addEventListener("submit", e => {
        e.preventDefault();
        let t = y.value.trim(); !t || T || (y.value = "", p(async () => { let n = await m("message", { text: t }); n.retryForm || M(), u(n) }))
    });
    document.querySelector("#restart").addEventListener("click", () => { p(B) });
    a.addEventListener("focusin", e => { e.target instanceof HTMLElement && e.target.closest(".mc-form,.mc-review") && (h = e.target), L() });
    A.addEventListener("click", () => {
        p(async () => {
            await m("keepalive"),
                ((h?.isConnected && !h.closest("[hidden]") ? h : a.querySelector(".mc-form:not([hidden]) input,.mc-review button")) ?? y).focus()
        })
    });
    function D() {
        window.parent.postMessage("medicount:close", "*")
    }
    document.querySelector("#close").addEventListener("click", D);
    document.addEventListener("keydown", e => { e.key === "Escape" && D() }); p(B);
})();
