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
