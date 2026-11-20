"use strict";
(() => {
    const baseUrl = new URL(".", document.currentScript.src);
    const billing = document.querySelector("#billing-status");
    const email = document.querySelector("#email-status");
    (async () => {
        try {
            const response = await fetch(new URL("api/status", baseUrl), { cache: "no-store", credentials: "omit", signal: AbortSignal.timeout(15000) });
            if (!response.ok) throw new Error("Status unavailable");
            const status = await response.json();
            billing.textContent = status.billing === "configured" ? "Configured — requests still need live confirmation" : "Not configured";
            email.textContent = status.email === "configured" ? "Configured — submitting a request attempts real email delivery" : "Not configured";
        } catch {
            billing.textContent = "Status unavailable";
            email.textContent = "Status unavailable";
        }
    })();
})();
