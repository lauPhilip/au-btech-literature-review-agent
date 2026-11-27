// Which elements of the report are one unit on an A4 page (wwwroot/js/paged-preview.js, wiki page 06). It only
// reads the rendered report: isAtom/atomOf find the units kept whole (a figure, a panel, a table), blocks lists
// the blocks to place in reading order, and opener says which element to move for a block. Served by the app (strict CSP).
(function () {
    const BLOCKS = 'p, h1, h2, h3, h4, h5, li, tr, caption, figcaption, pre, blockquote, dt, dd, summary, button';

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

    window.traceablePageBlocks = { BLOCKS: BLOCKS, blocks: blocks, opener: opener, isAtom: isAtom };
})();
