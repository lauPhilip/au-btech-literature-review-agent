// A table that runs over to another page in the report's A4 pages (wwwroot/js/paged-preview.js; issue #628).
// paged-preview.js moves a body row to the next page by growing its cells, so the table's box would run on through the
// foot of one sheet, the gap and the head of the next. This file draws what a reader expects instead, in
// [data-page-top], an element Blazor leaves empty that lies in front of the text: the table closes at the foot of the
// page with a line, the margins and the gap are shown clear, and the table's header row is drawn again at the head of
// each page it continues on. It draws on top of the table and never changes it. Served by the app (strict CSP).
(function () {
    function box(className, top, height, left, width) {
        const el = document.createElement('div');
        el.className = className;
        el.style.setProperty('top', top + 'px');
        el.style.setProperty('height', Math.max(0, height) + 'px');
        el.style.setProperty('left', left + 'px');
        el.style.setProperty('width', width + 'px');
        return el;
    }

    // The header row of a table, set again as its own table above the first row on a page.
    function repeatedHead(table, base, top) {
        const original = table.querySelector(':scope > thead');
        const wrap = table.parentElement;
        if (!original || !wrap) return null;
        const outer = wrap.getBoundingClientRect();
        const look = getComputedStyle(table);
        const frame = box('paged-head ' + wrap.className.replace(/overflow-x-auto|print:overflow-visible/g, ''), top, 0, outer.left - base.left, outer.width);
        frame.style.removeProperty('height');
        frame.setAttribute('data-page-head', '');
        ['fontFamily', 'fontSize', 'lineHeight', 'color'].forEach(function (name) { frame.style[name] = look[name]; });
        const copy = document.createElement('table');
        copy.className = table.className.replace(/min-w-\[[^\]]*\]/g, '');
        copy.style.setProperty('width', table.getBoundingClientRect().width + 'px');
        copy.style.setProperty('table-layout', 'fixed');
        const head = original.cloneNode(true);
        head.querySelectorAll('[id]').forEach(function (n) { n.removeAttribute('id'); });
        head.querySelectorAll('tr').forEach(function (r) { r.classList.remove('sticky'); });
        const cells = original.querySelectorAll('th, td');
        head.querySelectorAll('th, td').forEach(function (cell, n) {
            if (cells[n]) cell.style.setProperty('width', cells[n].getBoundingClientRect().width + 'px');
        });
        copy.appendChild(head);
        frame.appendChild(copy);
        return frame;
    }

    // splits: each body row that was moved to a later page, with its place before it moved (y, page "from") and its new
    // page ("to"). headers: page number -> the tables whose header row goes at the head of that page.
    window.traceablePageTables = {
        parts: function (layer, g, headers, splits, footOf) {
            const out = [];
            const base = layer.getBoundingClientRect();
            splits.forEach(function (s) {
                const wrap = s.table.parentElement;
                const rect = (wrap || s.table).getBoundingClientRect();
                const left = rect.left - base.left - 1;
                const width = rect.width + 2;
                // The foot of the page the table leaves: clear from the row's old place, closed by a line.
                out.push(box('paged-mask paged-mask-end', s.y, s.from * g.step + g.height - s.y, left, width));
                for (let n = s.from; n < s.to; n++) {
                    if (n > s.from) out.push(box('paged-mask', n * g.step, g.height, left, width));
                    out.push(box('paged-mask paged-mask-desk', n * g.step + g.height, g.gap, left, width));
                }
                out.push(box('paged-mask', s.to * g.step, g.pad, left, width));
                // The footer of the page it leaves sits under the clear strip, so it is drawn again on top.
                const holder = document.createElement('div');
                holder.className = 'paged-page-top';
                holder.style.setProperty('top', (s.from * g.step) + 'px');
                holder.style.setProperty('height', g.height + 'px');
                holder.appendChild(footOf(s.from));
                out.push(holder);
            });
            if (headers) headers.forEach(function (tables, n) {
                tables.forEach(function (table) {
                    const head = repeatedHead(table, base, n * g.step + g.pad);
                    if (head) out.push(head);
                });
            });
            return out;
        }
    };
})();
