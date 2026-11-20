"use strict";
(() => {
    const script = document.currentScript;
    if (!script || document.getElementById("medicount-chat-launcher")) return;

    // Keep the application path when hosted as an IIS subapplication.
    const baseUrl = new URL(".", script.src);
    const stylesheet = document.createElement("link");
    stylesheet.rel = "stylesheet";
    stylesheet.href = new URL("medicount-chat.css", baseUrl).href;
    document.head.append(stylesheet);

    const root = document.createElement("div");
    root.id = "medicount-chat-root";
    const launcher = document.createElement("button");
    launcher.id = "medicount-chat-launcher";
    launcher.type = "button";
    launcher.setAttribute("aria-expanded", "false");
    launcher.setAttribute("aria-controls", "medicount-chat-frame");
    const mark = document.createElement("span");
    mark.className = "mc-launcher-mark";
    const logo = document.createElement("img");
    logo.src = new URL("assets/medicount-brand-mark.png", baseUrl).href;
    logo.alt = "";
    logo.width = 325;
    logo.height = 346;
    mark.append(logo);
    const label = document.createElement("span");
    label.textContent = "Billing help";
    launcher.append(mark, label);
    let frame;

    function close() {
        if (frame) frame.hidden = true;
        launcher.hidden = false;
        launcher.setAttribute("aria-expanded", "false");
        launcher.focus();
    }

    launcher.addEventListener("click", () => {
        if (!frame) {
            frame = document.createElement("iframe");
            frame.id = "medicount-chat-frame";
            frame.title = "Medicount Billing Assistant";
            frame.src = new URL("widget.html", baseUrl).href;
            frame.referrerPolicy = "no-referrer";
            frame.setAttribute("sandbox", "allow-scripts allow-same-origin allow-forms allow-popups allow-popups-to-escape-sandbox");
            root.append(frame);
        }
        frame.hidden = false;
        launcher.hidden = true;
        launcher.setAttribute("aria-expanded", "true");
        frame.focus();
    });

    window.addEventListener("message", event => {
        if (frame && event.origin === baseUrl.origin && event.source === frame.contentWindow && event.data === "medicount:close") close();
    });
    root.append(launcher);
    if (document.body) document.body.append(root);
    else document.addEventListener("DOMContentLoaded", () => document.body.append(root), { once: true });
})();
