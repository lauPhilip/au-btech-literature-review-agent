// The colour theme. Loaded in <head> before the stylesheets, so the page has the right theme on its first
// paint and never flashes white in dark mode. The Content-Security-Policy forbids inline scripts, so this
// is a file (App.razor loads it).
//
// The theme is data-theme="dark" on <html>; without it the page is light. The choice made with a
// ThemeToggle button is kept in localStorage. Without a choice, the system setting (prefers-color-scheme)
// is followed only when <html> carries data-follow-system="true", which App.razor sets from
// ThemeToggle.FollowSystem once every page uses the theme-aware colours.
(function () {
    'use strict';

    var key = 'traceableai-theme';
    var root = document.documentElement;
    var media = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
    var chosen = null; // the choice made on this page, used when localStorage is not available

    function stored() {
        try {
            var value = window.localStorage.getItem(key);
            return value === 'dark' || value === 'light' ? value : null;
        } catch (e) {
            return null;
        }
    }

    function wanted() {
        var choice = chosen || stored();
        if (choice) return choice;
        if (root.getAttribute('data-follow-system') === 'true' && media && media.matches) return 'dark';
        return 'light';
    }

    function apply() {
        if (wanted() === 'dark') {
            if (root.getAttribute('data-theme') !== 'dark') root.setAttribute('data-theme', 'dark');
        } else if (root.hasAttribute('data-theme')) {
            root.removeAttribute('data-theme');
        }
    }

    apply();

    // Blazor's enhanced navigation copies the <html> element's attributes from the next page, which would
    // drop the theme; put it back whenever data-theme changes. apply() changes nothing when it is right,
    // so this cannot loop.
    if (window.MutationObserver) {
        new MutationObserver(apply).observe(root, { attributes: true, attributeFilter: ['data-theme', 'data-follow-system'] });
    }
    if (media && media.addEventListener) media.addEventListener('change', apply);
    // Another tab switched the theme.
    window.addEventListener('storage', function (e) { if (e.key === key) { chosen = null; apply(); } });

    // Every ThemeToggle is a <button data-theme-toggle>. One listener on the document serves them all, also
    // those Blazor renders later, and works on pages that are not interactive.
    document.addEventListener('click', function (e) {
        var target = e.target;
        var button = target && target.closest ? target.closest('[data-theme-toggle]') : null;
        if (!button) return;
        chosen = root.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
        try { window.localStorage.setItem(key, chosen); } catch (err) { /* private mode: the choice lasts for this page */ }
        apply();
    });
})();
