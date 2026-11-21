// The pause button of the landing page's citation example (Components/Landing/CitationLoop.razor).
// The example itself is CSS animation (Styles/tailwind.input.css, .tai-loop); this only switches it between
// playing and paused by setting data-state on the example. Without a data-state the CSS decides: playing,
// or paused for visitors whose system asks for less motion. The button's name and icon follow data-state
// through CSS, so they are right before this script runs and on pages Blazor adds later.
// It is a file and not an inline script, because the Content-Security-Policy allows only scripts from this site.
(function () {
    'use strict';

    var reduce = window.matchMedia ? window.matchMedia('(prefers-reduced-motion: reduce)') : null;

    // One listener on the document serves every example, also one that enhanced navigation brings in later.
    document.addEventListener('click', function (e) {
        var target = e.target;
        var button = target && target.closest ? target.closest('[data-loop-toggle]') : null;
        if (!button) return;
        var loop = button.closest('.tai-loop');
        if (!loop) return;
        var state = loop.getAttribute('data-state');
        var playing = state ? state === 'playing' : !(reduce && reduce.matches);
        loop.setAttribute('data-state', playing ? 'paused' : 'playing');
    });
})();
