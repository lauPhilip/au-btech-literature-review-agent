// How the A4 pages of the report (wwwroot/js/paged-preview.js, wiki page 06) move a block, and two things they cannot
// do by moving a block whole (issue #737). A figure taller than a page is scaled down until it fits on one, with its
// box, its caption and its notes. A paragraph that cannot move whole, because it is taller than a page or because its
// heading would then stand alone, is broken between two of its lines: the rest of it starts at the top of the next
// page. Blazor owns the report's elements, so none of this changes the report's DOM: a moved block gets padding or a
// margin, a figure a height, and a broken paragraph an attribute and two lengths that its ::before (report-paper.css)
// uses to push its later lines down.
(function () {
    const MIN_LINES = 2; // a page never ends with the first line of a paragraph, nor starts with its last
    const HEADING_ROOM = 64; // px kept free above a figure scaled to a page

    // Adds space above an element: on a table row its cells grow; on a box with a border or a background its margin,
    // so the box moves whole; on anything else its padding, which never collapses with a neighbour's margin.
    function push(el, px) {
        let targets = [el];
        let side = 'padding-top';
        if (el.tagName === 'TR') targets = Array.from(el.children);
        else {
            const style = getComputedStyle(el);
            const boxed = parseFloat(style.borderTopWidth) > 0 || (style.backgroundColor !== 'rgba(0, 0, 0, 0)' && style.backgroundColor !== 'transparent') || el.tagName === 'TABLE';
            if (boxed) side = 'margin-top';
        }
        targets.forEach(function (t) {
            const now = parseFloat(getComputedStyle(t)[side === 'margin-top' ? 'marginTop' : 'paddingTop']) || 0;
            t.style.setProperty(side, (now + px) + 'px');
            // The padding added so far: padding does not move the box's own top, so the box is placed by its text.
            t.setAttribute('data-page-pushed', String(added(t) + (side === 'padding-top' && el.tagName !== 'TR' ? px : 0)));
        });
    }

    // Moves an element so its text starts at "to" (from the sheet's top); a margin can collapse with a neighbour's, so
    // it measures again and makes up the rest.
    function moveTo(el, to, top) {
        for (let k = 0; k < 3; k++) {
            const was = top(el) + added(el);
            const delta = to - was;
            if (delta < 1) return;
            push(el, delta);
            // A table row keeps its top while its cells grow, so measuring it again says nothing.
            if (el.tagName === 'TR' || top(el) + added(el) - was < delta / 2) return;
        }
    }

    function added(el) { return parseFloat(el.getAttribute('data-page-pushed')) || 0; }

    // A figure whose box does not fit on a page gets a height that makes it fit; an SVG keeps its proportions
    // (its viewBox scales), an image keeps them with a width of auto.
    function figures(sheet, body, front, isAtom) {
        sheet.querySelectorAll('svg, img').forEach(function (g) {
            if ((front && front.contains(g)) || (g.parentElement && g.parentElement.closest('svg'))) return;
            const tall = g.getBoundingClientRect().height;
            if (tall < 48) return;
            // The box around it: the outermost unit kept whole whose other parts take at most a third of a page.
            let extra = 0;
            for (let a = g.parentElement; a && a !== sheet; a = a.parentElement) {
                const more = a.getBoundingClientRect().height - tall;
                if (more > body / 3) break;
                if (isAtom(a)) extra = more;
            }
            if (tall + extra <= body) return;
            // Room is left for a heading over it, so the heading does not stand alone on the page before.
            g.setAttribute('data-page-fit', g.style.height || '');
            g.style.setProperty('height', Math.max(48, Math.floor(body - extra - HEADING_ROOM)) + 'px');
            if (g.tagName === 'IMG') g.style.setProperty('width', 'auto');
        });
    }

    // The lines of a block, from the top: the boxes of its text merged where they overlap.
    function lines(el) {
        const range = document.createRange();
        range.selectNodeContents(el);
        const rects = Array.from(range.getClientRects()).filter(function (r) { return r.height > 0 && r.width > 0; })
            .sort(function (a, b) { return a.top - b.top; });
        const out = [];
        rects.forEach(function (r) {
            const last = out[out.length - 1];
            if (last && r.top < last.bottom - 1) last.bottom = Math.max(last.bottom, r.bottom);
            else out.push({ top: r.top, bottom: r.bottom });
        });
        return out;
    }

    // Breaks a paragraph so that its lines below "end" start at "next", and so on for every later page it reaches
    // ("step" apart; all from the sheet's top). Each break is a band of the float's shape, from just inside the first
    // line to move down to the top of the next page. True when its text now ends above each foot it reaches; false
    // when it cannot break here: too few lines would be left on either side of the first break, or the browser does
    // not move the lines.
    function split(el, sheetTop, end, next, step) {
        if (el.tagName !== 'P' || el.hasAttribute('data-page-split')) return false;
        const style = getComputedStyle(el);
        const content = el.getBoundingClientRect().top + parseFloat(style.paddingTop) + parseFloat(style.borderTopWidth) - sheetTop;
        const bands = [];
        for (let n = 0; n < 8; n++, end += step, next += step) {
            const all = lines(el);
            let k = all.findIndex(function (l) { return l.bottom - sheetTop > end + 1; });
            if (k < 0) return true; // its text ends above the foot; only the space under its last line crosses
            k = Math.min(k, all.length - MIN_LINES);
            if (k < (n === 0 ? MIN_LINES : 1) || all[k].top - sheetTop < next - step) break;
            // The band starts just inside the glyphs of the line to move, so it never touches the line above.
            const at = all[k].top - sheetTop - content + 1, to = next - content;
            bands.push('0 ' + at + 'px, 100% ' + at + 'px, 100% ' + to + 'px, 0 ' + to + 'px');
            el.style.setProperty('--page-split-shape', 'polygon(' + bands.join(', ') + ')');
            el.style.setProperty('--page-split-end', to + 'px');
            el.setAttribute('data-page-split', '');
            const after = lines(el);
            if (!after[k] || Math.abs(after[k].top - sheetTop - next) >= 24 || Math.abs(after[k - 1].top - all[k - 1].top) >= 2) {
                undo(el);
                return false;
            }
        }
        return bands.length > 0;
    }

    // Where the run of headings just before list[i] starts on this page (its first line of text, as a moved heading's
    // box may start on the page before), or the block's own top when no heading leads it.
    function ledBy(list, i, page, step, sheet) {
        const st = sheet.getBoundingClientRect().top;
        let from = list[i].getBoundingClientRect().top - st;
        for (let j = i - 1; j >= 0 && /^H[1-5]$/.test(list[j].tagName); j--) {
            const l = lines(list[j])[0];
            const t = (l || list[j].getBoundingClientRect()).top - st;
            if (Math.floor(t / step) !== page) break;
            from = t;
        }
        return from;
    }

    function undo(el) {
        el.style.removeProperty('--page-split-shape');
        el.style.removeProperty('--page-split-end');
        el.removeAttribute('data-page-split');
    }

    // Takes back every height and break this script added.
    function reset(sheet) {
        sheet.querySelectorAll('[data-page-split]').forEach(undo);
        sheet.querySelectorAll('[data-page-fit]').forEach(function (g) {
            const was = g.getAttribute('data-page-fit');
            if (was) g.style.setProperty('height', was); else g.style.removeProperty('height');
            if (g.tagName === 'IMG') g.style.removeProperty('width');
            g.removeAttribute('data-page-fit');
        });
    }

    window.traceablePageFit = { moveTo: moveTo, added: added, figures: figures, split: split, ledBy: ledBy, reset: reset };
})();
