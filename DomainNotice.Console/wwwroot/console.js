// Small helpers for the DomainNotice Console (called through JS interop).
window.domainNotice = {
    timeZone: function () {
        try { return Intl.DateTimeFormat().resolvedOptions().timeZone || null; } catch (e) { return null; }
    },
    getTheme: function () {
        try { return window.localStorage.getItem("domainnotice.theme"); } catch (e) { return null; }
    },
    setTheme: function (value) {
        try { window.localStorage.setItem("domainnotice.theme", value); } catch (e) { }
    },
    prefersDark: function () {
        try { return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches; } catch (e) { return false; }
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
