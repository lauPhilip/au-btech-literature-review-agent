// The report on screen as A4 pages (Components/Pages/SpecMatrix.razor, wiki page 06), like its printout: white
// sheets (dark in the dark theme) on a neutral desk, even margins, and a footer with the site, "page of pages" and
// "AI-generated · machine-marked". Served by the app, so the strict Content-Security-Policy allows it.
//
// Blazor owns the report's elements, so this script never moves one. It measures the rendered text, adds space above
// any block that would cross the foot of a page (so the block starts on the next page), and draws the sheets and their
// footers in [data-page-overlay], an element Blazor leaves empty, behind the text (and, for a table that runs over
// a page, the repeated header row and the hidden gap in [data-page-top], in front of it; see paged-tables.js). It runs again when the report
// changes (an open citation, an edit), when the window is resized and after printing. Below 768 px, and on paper,
// the report is one continuous sheet: print has its own pages (wwwroot/css/journal-print.css).
(function () {
    const MIN_WIDTH = 768;
    const GAP = 24;                  // px between two sheets on the desk
    const A4 = 297 / 210;            // height over width
    const MARGIN = 15 / 210;         // 15 mm of 210 mm, as on paper
    const BLOCKS = 'p, h1, h2, h3, h4, h5, li, tr, caption, figcaption, pre, blockquote, dt, dd, summary, button';
    const HEADINGS = /^H[1-5]$/;
    let timer = 0;
    let printing = false;

    function sheets() { return Array.from(document.querySelectorAll('[data-paged-sheet]')); }

    function overlayOf(sheet) {
        const holder = sheet.parentElement;
        return holder ? holder.querySelector(':scope > [data-page-overlay]') : null;
    }

    function topOf(sheet) {
        const holder = sheet.parentElement;
        return holder ? holder.querySelector(':scope > [data-page-top]') : null;
    }

    // Takes back every space this script added, so the report flows as Blazor drew it.
    function reset(sheet) {
        sheet.querySelectorAll('[data-page-pushed]').forEach(function (el) {
            el.style.removeProperty('padding-top');
            el.style.removeProperty('margin-top');
            el.removeAttribute('data-page-pushed');
        });
        sheet.querySelectorAll('[data-front-page]').forEach(function (f) { f.style.removeProperty('zoom'); });
        sheet.removeAttribute('data-paged');
        sheet.style.removeProperty('--sheet-pad');
        sheet.style.removeProperty('min-height');
        const overlay = overlayOf(sheet);
        if (overlay) overlay.replaceChildren();
        const above = topOf(sheet);
        if (above) above.replaceChildren();
    }

    // Units kept whole when they fit on a page: a figure, a diagram, an image, a box marked to stay together, a box with
    // a border or a background (a panel, a card, a table), and a grid of more than one column (figures side by side),
    // so no figure or panel is cut across two sheets and no row of a grid is torn apart.
    const ATOMS = 'svg, figure, img, [data-page-block], .print-keep';
    function isAtom(el) {
        if (el.matches('svg, img')) return el.getBoundingClientRect().height >= 48; // not an icon
        if (el.matches(ATOMS)) return true;
        const style = getComputedStyle(el);
        if (style.display.includes('grid') && style.gridTemplateColumns.trim().split(/\s+/).length > 1) return true;
        if (style.display.startsWith('inline') || style.display.startsWith('table-') || style.display === 'list-item') return false;
        const framed = parseFloat(style.borderTopWidth) > 0 && parseFloat(style.borderBottomWidth) > 0;
        const filled = style.backgroundColor !== 'rgba(0, 0, 0, 0)' && style.backgroundColor !== 'transparent';
        return (framed || filled) && el.getBoundingClientRect().height >= 48;
    }

    // The outermost unit around an element that fits on a page, or null.
    function atomOf(el, sheet, body) {
        let found = null;
        for (let a = el; a && a !== sheet; a = a.parentElement) {
            if (a.hasAttribute('data-front-page')) return null;
            if (isAtom(a) && a.getBoundingClientRect().height <= body) found = a;
        }
        return found;
    }

    // The blocks to place, in reading order: units kept whole, and otherwise the innermost blocks; text in an inline
    // element counts with its nearest block.
    function blocks(sheet, body) {
        const found = [];
        const seen = new Set();
        sheet.querySelectorAll(BLOCKS + ', ' + ATOMS).forEach(function (el) {
            let block = atomOf(el, sheet, body);
            if (!block) {
                if (el.matches(ATOMS) || el.closest('svg') || (el.querySelector(BLOCKS) && el.tagName !== 'TR') || (el.closest('tr') && el.tagName !== 'TR')) return;
                block = el;
            }
            while (block !== sheet && block.parentElement && getComputedStyle(block).display.startsWith('inline')) block = block.parentElement;
            // Items side by side in a row (buttons, chips) move together with their row.
            const row = block.parentElement;
            if (row && row !== sheet && !row.hasAttribute('data-front-page')) {
                const style = getComputedStyle(row);
                if (style.display.includes('flex') && !style.flexDirection.startsWith('column')) block = row;
            }
            if (block === sheet || seen.has(block) || block.getClientRects().length === 0) return;
            const position = getComputedStyle(block).position;
            if (position === 'absolute' || position === 'fixed') return;
            seen.add(block);
            found.push(block);
        });
        return found;
    }

    // The element to move for a block: the block itself, or the outermost box it opens (a bordered box, a section or a
    // table whose first thing it is), so a box never leaves its top border behind on the page before.
    function opener(el, sheet) {
        // A body row of a table moves alone (its cells grow); the rows above it stay where they are.
        if (el.tagName === 'TR' && el.parentElement && el.parentElement.tagName === 'TBODY') return el;
        let target = el;
        while (target.parentElement && target.parentElement !== sheet && !target.parentElement.hasAttribute('data-front-page')) {
            let prev = target.previousElementSibling;
            while (prev && prev.getClientRects().length === 0) prev = prev.previousElementSibling;
            if (prev) break;
            const display = getComputedStyle(target.parentElement).display;
            if (display.includes('grid') || display.includes('flex')) break;
            target = target.parentElement;
        }
        return target;
    }

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
            t.setAttribute('data-page-pushed', '');
        });
    }

    // Moves an element so its top is at "to" (from the sheet's top); a margin can collapse with a neighbour's, so it
    // measures again and makes up the rest.
    function moveTo(el, to, top) {
        for (let k = 0; k < 3; k++) {
            const was = top(el);
            const delta = to - was;
            if (delta < 1) return;
            push(el, delta);
            // A table row keeps its top while its cells grow, so measuring it again says nothing.
            if (el.tagName === 'TR' || top(el) - was < delta / 2) return;
        }
    }

    function paginate(sheet) {
        reset(sheet);
        if (printing || window.innerWidth < MIN_WIDTH || sheet.clientWidth < MIN_WIDTH - 80) {
            sheet.setAttribute('data-paged', 'off');
            return;
        }
        const width = sheet.clientWidth;
        const height = Math.round(width * A4);
        const pad = Math.round(width * MARGIN);
        const step = height + GAP;
        const body = height - 2 * pad;
        sheet.style.setProperty('--sheet-pad', pad + 'px');
        sheet.setAttribute('data-paged', 'on');

        const front = sheet.querySelector('[data-front-page]');
        if (front) {
            // The front page is one unit with its two columns: it is never split, and if it is taller than a page it
            // is scaled down to fit (at most to 70 %), as a journal sets its front page smaller.
            const tall = front.getBoundingClientRect().bottom - sheet.getBoundingClientRect().top - pad;
            if (tall > body) front.style.setProperty('zoom', String(Math.max(0.7, Math.floor(body / tall * 1000) / 1000)));
        }
        const list = blocks(sheet, body).filter(function (el) { return !front || !front.contains(el); });
        let frontDone = !front;
        const top = function (el) { return el.getBoundingClientRect().top - sheet.getBoundingClientRect().top; };
        let guard = list.length * 3;
        // A table that runs on to another page shows its header row again at the top of that page (issue #628).
        const headers = new Map(), tablePage = new Map(), splits = [];
        for (let i = 0; i < list.length && guard-- > 0; i++) {
            const el = list[i];
            const y = top(el);
            const h = el.getBoundingClientRect().height;
            const page = Math.floor(y / step);
            const start = page * step + pad;
            const end = page * step + height - pad;
            const next = (page + 1) * step + pad;
            let to = null;
            if (!frontDone && !front.contains(el)) {
                // The front page fills its own sheet: the body starts on the next one.
                frontDone = true;
                if (y > start + 1) to = next;
            }
            if (to === null) {
                if (y < start - 1) to = start;                               // in the top margin
                else if (y > end) to = next;                                 // in the bottom margin or the gap
                else if (y + h > end + 1 && h <= body) to = next;            // crosses the foot of the page
            }
            // The first body row of a table on a new page makes room for the repeated header row above it.
            const table = el.tagName === 'TR' ? el.closest('table') : null;
            const head = table && el.parentElement.tagName === 'TBODY' ? table.querySelector(':scope > thead') : null;
            if (table) {
                const landing = Math.floor((to === null ? y : to) / step);
                const seen = tablePage.get(table);
                if (head && seen !== undefined && seen !== landing) {
                    const need = landing * step + pad + head.getBoundingClientRect().height;
                    if ((to === null ? y : to) < need) to = need;
                    headers.set(landing, (headers.get(landing) || []).concat(table));
                }
                if (el.parentElement.tagName === 'TBODY' && to !== null && landing > page) splits.push({ table: table, y: y, from: page, to: landing });
                tablePage.set(table, landing);
            }
            if (to === null) continue;
            // A heading goes with the block under it; the loop then looks at this block again.
            const before = i > 0 ? list[i - 1] : null;
            if (before && HEADINGS.test(before.tagName) && !(front && front.contains(before)) && to === next) {
                const b = top(before);
                if (Math.floor(b / step) === page && b > start + 1) {
                    moveTo(opener(before, sheet), next, top);
                    i -= 1;
                    continue;
                }
            }
            const target = opener(el, sheet);
            moveTo(target, to, top);
        }

        const content = sheet.scrollHeight - pad;
        const pages = Math.max(1, Math.floor((content - 1) / step) + 1);
        sheet.style.setProperty('min-height', (pages * step - GAP) + 'px');
        draw(sheet, pages, height, pad, headers, splits);
    }

    function footOf(n, pages, pad, site) {
        const foot = document.createElement('div');
        foot.className = 'paged-foot';
        foot.style.cssText = 'left:' + pad + 'px;right:' + pad + 'px;bottom:' + Math.round(pad * 0.45) + 'px';
        [site, (n + 1) + ' of ' + pages, 'AI-generated · machine-marked'].forEach(function (text) {
            const span = document.createElement('span');
            span.textContent = text;
            foot.appendChild(span);
        });
        return foot;
    }

    function draw(sheet, pages, height, pad, headers, splits) {
        const overlay = overlayOf(sheet);
        if (!overlay) return;
        const site = window.location.hostname;
        const parts = [];
        for (let n = 0; n < pages; n++) {
            const page = document.createElement('div');
            page.className = 'paged-page';
            page.style.setProperty('top', (n * (height + GAP)) + 'px');
            page.style.setProperty('height', height + 'px');
            page.appendChild(footOf(n, pages, pad, site));
            parts.push(page);
        }
        overlay.replaceChildren.apply(overlay, parts);
        // Tables that run over a page: the rows' gap is hidden and the header row is drawn again (paged-tables.js).
        const above = topOf(sheet);
        if (above && window.traceablePageTables) above.replaceChildren.apply(above, window.traceablePageTables.parts(above,
            { height: height, pad: pad, gap: GAP, step: height + GAP }, headers, splits, function (n) { return footOf(n, pages, pad, site); }));
    }

    function run() {
        timer = 0;
        sheets().forEach(paginate);
    }

    function later(ms) {
        if (timer) clearTimeout(timer);
        timer = setTimeout(run, ms);
    }

    // A change inside a report (Blazor re-rendering it) or a new report on the page pages it again. Only nodes and text
    // count: the space this script adds is a style, so its own work does not start it again.
    new MutationObserver(function (records) {
        for (const r of records) {
            const node = r.target.nodeType === 1 ? r.target : r.target.parentElement;
            if (!node || node.closest('[data-page-overlay], [data-page-top]')) continue;
            if (node.closest('[data-paged-sheet]') || (r.addedNodes.length > 0 && document.querySelector('[data-paged-sheet]:not([data-paged])'))) {
                later(200);
                return;
            }
        }
    }).observe(document.documentElement, { childList: true, subtree: true, characterData: true });

    window.addEventListener('resize', function () { later(250); });
    window.addEventListener('load', function () { later(50); });
    if (document.fonts && document.fonts.ready) document.fonts.ready.then(function () { later(50); });
    window.addEventListener('beforeprint', function () { printing = true; sheets().forEach(reset); });
    window.addEventListener('afterprint', function () { printing = false; later(50); });
    if (window.matchMedia) {
        window.matchMedia('print').addEventListener('change', function (e) {
            printing = e.matches;
            if (e.matches) sheets().forEach(reset); else later(50);
        });
    }
    later(100);

    window.traceablePages = { refresh: run };
})();
