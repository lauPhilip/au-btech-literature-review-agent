// The report on screen as A4 pages (Components/Pages/SpecMatrix.razor, wiki page 06), like its printout: white
// sheets (dark in the dark theme) on a neutral desk, even margins, and a footer with the site, "page of pages" and
// "AI-generated · machine-marked". Served by the app, so the strict Content-Security-Policy allows it.
//
// Which elements count as a block is decided in paged-blocks.js.
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
    const HEADINGS = /^(H[1-5]|CAPTION)$/;
    const blocks = function (sheet, body) { return window.traceablePageBlocks.blocks(sheet, body); };
    const opener = function (el, sheet) { return window.traceablePageBlocks.opener(el, sheet); };
    const fit = window.traceablePageFit; // moving blocks, figures scaled to a page, paragraphs broken (paged-fit.js)
    const moveTo = fit.moveTo;
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
        fit.reset(sheet);
        sheet.removeAttribute('data-paged');
        sheet.style.removeProperty('--sheet-pad');
        sheet.style.removeProperty('min-height');
        const overlay = overlayOf(sheet);
        if (overlay) overlay.replaceChildren();
        const above = topOf(sheet);
        if (above) above.replaceChildren();
    }

    // The first of the blocks before list[i] that must move with it to the next page: every heading and caption in a row
    // before it, and, for the first body row of a table, the table's header row. Only blocks on this page and below its
    // top margin count; -1 when there are none.
    function chainStart(list, i, el, page, start, step, front, top) {
        const table = el.tagName === 'TR' && el.parentElement.tagName === 'TBODY' ? el.closest('table') : null;
        const own = function (b) { return table && b.tagName === 'TR' && b.parentElement.tagName === 'THEAD' && b.closest('table') === table; };
        let first = -1;
        for (let j = i - 1; j >= 0 && (HEADINGS.test(list[j].tagName) || own(list[j])) && !(front && front.contains(list[j])); j--) {
            const b = top(list[j]);
            if (Math.floor(b / step) !== page || b <= start + 1) break;
            first = j;
        }
        return first;
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
        fit.figures(sheet, body, front, window.traceablePageBlocks.isAtom);
        const list = blocks(sheet, body).filter(function (el) { return !front || !front.contains(el); });
        let frontDone = !front;
        const top = function (el) { return el.getBoundingClientRect().top - sheet.getBoundingClientRect().top; };
        let guard = list.length * 3;
        // A table that runs on to another page shows its header row again at the top of that page (issue #628).
        const headers = new Map(), tablePage = new Map(), splits = [], moved = new Set();
        for (let i = 0; i < list.length && guard-- > 0; i++) {
            const el = list[i];
            // A block looked at again after a move (below) is placed by its text, not by its padded box.
            const shift = fit.added(el);
            const y = top(el) + shift;
            const h = el.getBoundingClientRect().height - shift;
            const page = Math.floor(y / step);
            const start = page * step + pad;
            const end = page * step + height - pad;
            const next = (page + 1) * step + pad;
            let to = null, again;
            if (!frontDone && !front.contains(el)) {
                // The front page fills its own sheet: the body starts on the next one.
                frontDone = true;
                if (y > start + 1) to = next;
            }
            if (to === null) {
                if (y < start - 1) to = start;                               // in the top margin
                else if (y > end) to = next;                                 // in the bottom margin or the gap
                else if (y + h > end + 1) {                                  // crosses the foot of the page
                    // A paragraph that cannot move whole, being taller than a page or than the room under the headings
                    // above it on this page, is broken between two lines (issue #737). With too few lines here it
                    // moves to the next page and is looked at again there.
                    const led = fit.ledBy(list, i, page, step, sheet);
                    const whole = h <= body && y + h - led <= body && !moved.has(el); // still crossing after its headings moved
                    if (!whole && fit.split(el, sheet.getBoundingClientRect().top, end, next, step)) continue;
                    if (h <= body) to = next;
                    else if (el.tagName === 'P' && y > start + 1 && !el.hasAttribute('data-page-pushed')) to = next;
                }
            }
            again = to === next && el.tagName === 'P' && h > body;
            // A table's heading, caption and header row go with its first body row (issue #691): when that row moves to the
            // next page, the header row above it and the headings before that move too, so a page never ends with a table
            // that has no row. The chain is moved from its first element and this row is looked at again.
            const table = el.tagName === 'TR' ? el.closest('table') : null;
            if (to === next && table && el.parentElement.tagName === 'TBODY') {
                const first = chainStart(list, i, el, page, start, step, front, top);
                if (first >= 0) {
                    moveTo(opener(list[first], sheet), next, top);
                    tablePage.set(table, page + 1);
                    i -= 1;
                    continue;
                }
            }
            // The first body row of a table on a new page makes room for the repeated header row above it.
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
            // Headings go with the block under them: every heading in a row before it (a chapter and its first section)
            // moves from the first of them, so none is left alone at the foot of the page. The loop then looks at this
            // block again.
            if (to === next) {
                const first = chainStart(list, i, el, page, start, step, front, top);
                // The run moves once, and only when it fits on a page with the block, or with a paragraph's first lines.
                const need = el.tagName === 'P' ? Math.min(h, 3 * (parseFloat(getComputedStyle(el).lineHeight) || 30)) : h;
                if (first >= 0 && !moved.has(list[first]) && y + need - top(list[first]) <= body) {
                    moved.add(list[first]).add(el);
                    moveTo(opener(list[first], sheet), next, top);
                    i -= 1;
                    continue;
                }
            }
            const target = opener(el, sheet);
            moveTo(target, to, top);
            if (again) i -= 1;
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

    // A citation's card opens after its paragraph, and paging can push it to the next sheet, out of view. A card that
    // is new since the last pass and not fully on screen is scrolled to (issue #671). Blazor may reuse the element for
    // another citation, so a card counts as new when its label or the element before it is not the one seen last.
    const shown = new WeakMap();
    function revealCitationCards() {
        document.querySelectorAll('[data-citation-panel]').forEach(function (panel) {
            const seen = shown.get(panel);
            const label = panel.getAttribute('aria-label');
            const before = panel.previousElementSibling;
            if (seen && seen.label === label && seen.before === before) return;
            shown.set(panel, { label: label, before: before });
            const r = panel.getBoundingClientRect();
            if (r.top >= 0 && r.bottom <= window.innerHeight) return;
            const still = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
            panel.scrollIntoView({ behavior: still ? 'auto' : 'smooth', block: 'nearest' });
        });
    }

    function run() {
        timer = 0;
        sheets().forEach(paginate);
        revealCitationCards();
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
