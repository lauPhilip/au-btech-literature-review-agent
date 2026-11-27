// Renders the Mermaid diagram on the Review Output page, and the diagrams of the developer wiki under /docs.
// - Mermaid 11.17.2 is served by the app itself (wwwroot/lib/mermaid, MIT licence), not from a CDN, so the
//   page loads no third-party script and the exact version is part of the repository.
// - securityLevel "strict": the diagram text comes from the language model, so no HTML or click handlers.
// - If the diagram still cannot be parsed, its source is shown as text instead of Mermaid's error bomb.
window.traceableMermaid = (function () {
    let loading = null;
    let counter = 0;

    function load() {
        if (window.mermaid) return Promise.resolve(window.mermaid);
        if (!loading) {
            loading = new Promise(function (resolve, reject) {
                const script = document.createElement('script');
                script.src = 'lib/mermaid/mermaid.min.js';
                script.onload = function () {
                    window.mermaid.initialize({ startOnLoad: false, theme: 'default', securityLevel: 'strict', suppressErrorRendering: true });
                    resolve(window.mermaid);
                };
                script.onerror = function () { loading = null; reject(new Error('Mermaid could not be loaded')); };
                document.head.appendChild(script);
            });
        }
        return loading;
    }

    function showSource(element, code, reason) {
        const note = document.createElement('p');
        note.className = 'text-[11px] text-gray-500 mb-2';
        note.textContent = reason;
        const pre = document.createElement('pre');
        pre.className = 'text-[10px] text-left whitespace-pre-wrap text-gray-600 bg-white border border-gray-200 p-2 w-full';
        pre.textContent = code;
        element.replaceChildren(note, pre);
    }

    async function render(elementId, code) {
        const element = document.getElementById(elementId);
        if (!element || !code) return false;
        if (element.dataset.renderedCode === code) return true; // already drawn for this code
        element.dataset.renderedCode = code;
        try {
            const mermaid = await load();
            const id = 'traceable-mermaid-' + (++counter);
            const result = await mermaid.render(id, code);
            element.innerHTML = result.svg;
            return true;
        } catch (e) {
            showSource(element, code, 'The diagram could not be drawn, so its source is shown instead.');
            return false;
        }
    }

    // The developer wiki (Components/Pages/Wiki.razor) is a plain page: its diagrams arrive as code blocks of Mermaid
    // source, and each is drawn in its place. A page that arrives later by enhanced navigation is caught by the
    // observer. Without scripts, or if a diagram cannot be drawn, the source stays readable.
    let wikiCounter = 0;
    function drawWikiDiagrams() {
        document.querySelectorAll('.ui-wiki pre > code.language-mermaid').forEach(function (code) {
            const holder = document.createElement('figure');
            holder.id = 'wiki-diagram-' + (++wikiCounter);
            holder.className = 'overflow-x-auto border border-line bg-white p-4';  // white in both themes: Mermaid draws dark lines
            const source = code.textContent;
            code.parentElement.replaceWith(holder);
            render(holder.id, source);
        });
    }
    function watchWiki() {
        drawWikiDiagrams();
        new MutationObserver(function () {
            if (document.querySelector('.ui-wiki pre > code.language-mermaid')) drawWikiDiagrams();
        }).observe(document.body, { childList: true, subtree: true });
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', watchWiki);
    else watchWiki();

    return { render: render };
})();
