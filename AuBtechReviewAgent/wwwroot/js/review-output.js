// Review Output page helpers (served by the app itself, so the strict Content-Security-Policy allows it).
// scrollTo: brings a citation into view when the reader steps through the citations that need attention.
window.traceableReview = {
    scrollTo: function (id) {
        const el = document.getElementById(id);
        if (!el) return false;
        el.scrollIntoView({ behavior: 'smooth', block: 'center' });
        el.focus({ preventScroll: true });
        return true;
    }
};
