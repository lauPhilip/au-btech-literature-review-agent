// Review Output page helpers (served by the app itself, so the strict Content-Security-Policy allows it).
// scrollTo: brings a citation into view when the reader steps through the citations that need attention.
window.traceableReview = {
    scrollTo: function (id) {
        const el = document.getElementById(id);
        if (!el) return false;
        el.scrollIntoView({ behavior: 'smooth', block: 'center' });
        el.focus({ preventScroll: true });
        return true;
    },
    // revealOnSmallScreens: after "Start review" on a phone, where the run panel sits below the form,
    // scroll it into view so the reviewer sees the run begin. On wide screens both are already visible.
    revealOnSmallScreens: function (id) {
        if (window.matchMedia('(min-width: 1024px)').matches) return false;
        const el = document.getElementById(id);
        if (!el) return false;
        el.scrollIntoView({ behavior: 'smooth', block: 'start' });
        return true;
    }
};
