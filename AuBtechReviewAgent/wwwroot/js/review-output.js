// Page helpers for every page (served by the app itself, so the strict Content-Security-Policy allows it).
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
    },
    // focus: moves keyboard focus to an element, e.g. the first field with a problem.
    focus: function (id) {
        const el = document.getElementById(id);
        if (!el) return false;
        el.focus();
        return true;
    },
    // trapFocus / releaseFocus: keeps Tab inside a dialog while it is open (WCAG 2.4.3), and puts focus back
    // on the element that opened it when it closes.
    trapFocus: function (id) {
        const box = document.getElementById(id);
        if (!box) return false;
        window.traceableReview._opener = document.activeElement;
        const selector = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';
        box._trap = function (e) {
            if (e.key !== 'Tab') return;
            const items = Array.from(box.querySelectorAll(selector)).filter(x => x.offsetParent !== null);
            if (items.length === 0) return;
            const first = items[0], last = items[items.length - 1];
            if (e.shiftKey && document.activeElement === first) { last.focus(); e.preventDefault(); }
            else if (!e.shiftKey && document.activeElement === last) { first.focus(); e.preventDefault(); }
        };
        box.addEventListener('keydown', box._trap);
        const firstField = box.querySelector('input, select, textarea, button');
        if (firstField) firstField.focus();
        return true;
    },
    // copyText: puts text on the clipboard (used for the statement on AI use); false when the browser refuses.
    copyText: async function (text) {
        try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
    },
    // print: opens the browser's print window, where "Save as PDF" gives a PDF of the report.
    print: function () { window.print(); return true; },
    releaseFocus: function () {
        const opener = window.traceableReview._opener;
        window.traceableReview._opener = null;
        if (opener && document.body.contains(opener)) opener.focus();
        return true;
    }
};

// "Skip to content": move focus to the page's main region. Handled here because the page has <base href="/">,
// so a plain href="#main" would navigate to the home page instead of jumping within this one.
document.addEventListener('click', function (e) {
    const link = e.target.closest && e.target.closest('[data-skip-to]');
    if (!link) return;
    const target = document.getElementById(link.getAttribute('data-skip-to'));
    if (!target) return;
    e.preventDefault();
    if (!target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1');
    target.focus();
    target.scrollIntoView({ block: 'start' });
});

// Escape hides an open term explanation (WCAG 1.4.13: extra content on hover or focus can be dismissed).
document.addEventListener('keydown', function (e) {
    if (e.key !== 'Escape') return;
    const term = document.activeElement && document.activeElement.closest && document.activeElement.closest('.ui-term');
    if (term) term.blur();
    document.querySelectorAll('.ui-term:hover').forEach(function (t) {
        t.classList.add('ui-term-dismissed');
        t.addEventListener('mouseleave', function () { t.classList.remove('ui-term-dismissed'); }, { once: true });
    });
});
