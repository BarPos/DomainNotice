// Small helpers for the DomainNotice Console (called through JS interop).
window.domainNotice = {
    timeZone: function () {
        try { return Intl.DateTimeFormat().resolvedOptions().timeZone || null; } catch (e) { return null; }
    },
    copy: async function (text) {
        try { await navigator.clipboard.writeText(text); return true; } catch (e) { return false; }
    },
    download: function (url) {
        var a = document.createElement("a");
        a.href = url;
        a.rel = "noopener";
        document.body.appendChild(a);
        a.click();
        a.remove();
    }
};

// Blazor is started here (autostart="false" in App.razor) with our own reconnection handler. The built-in one can stay
// on "Reconnecting" for good after a server restart (an update): when the sign-in cookie is no longer accepted, every
// attempt fails as if the server were still down. This one retries every few seconds while the server is down. Once
// the server serves pages again but no longer knows this page's session (it restarted) or refuses the connection, it
// opens the sign-in page, which goes straight back to this page when the sign-in cookie is still valid.
(function () {
    var running = false;
    function sleep(ms) { return new Promise(function (resolve) { setTimeout(resolve, ms); }); }
    function overlay() { return document.getElementById("dn-reconnect"); }
    function show(key) {
        var el = overlay();
        if (!el) return;
        el.querySelector(".dn-reconnect-text").textContent = el.dataset[key] || "";
        el.hidden = false;
    }
    function hide() { var el = overlay(); if (el) el.hidden = true; }

    async function serverServesPages() {
        try {
            var response = await fetch("/account/login", { cache: "no-store", credentials: "same-origin", redirect: "manual" });
            return response.ok || response.type === "opaqueredirect";
        } catch (e) {
            return false;
        }
    }

    function openSignIn() {
        var here = location.pathname + location.search;
        if (here.indexOf("/account/") === 0) here = "/";
        location.replace("/account/login?returnUrl=" + encodeURIComponent(here));
    }

    async function recover() {
        if (running) return;
        running = true;
        show("down");
        try {
            var refusedWhileUp = 0;
            for (var attempt = 0; ; attempt++) {
                var result;
                try {
                    result = await Blazor.reconnect();   // true: reconnected; false: the server refused this session
                } catch (e) {
                    result = null;                        // failed: the server is down, or refuses the connection
                }
                if (result === true) { hide(); return; }
                if (result === false) break;
                // Up but still failing: for example the sign-in cookie is no longer accepted (expired, or older than
                // the update), so the connection is refused before it reaches the session. Reconnecting cannot work.
                if (await serverServesPages()) {
                    if (++refusedWhileUp >= 2) break;
                } else {
                    refusedWhileUp = 0;
                }
                if (attempt === 2) show("restarting");
                await sleep(attempt < 10 ? 1000 : 3000);
            }
            show("back");
            while (!(await serverServesPages())) await sleep(2000);
            openSignIn();
        } finally {
            running = false;
        }
    }

    Blazor.start({
        circuit: {
            reconnectionHandler: {
                onConnectionDown: function () { recover(); },
                onConnectionUp: function () { hide(); }
            }
        }
    });
})();
