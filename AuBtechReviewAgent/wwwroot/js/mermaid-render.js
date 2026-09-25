// Renders the Mermaid diagram on the Review Output page.
// - Mermaid is pinned to major version 11 (the unpinned CDN URL silently moved to a new major version).
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
                script.src = 'https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js';
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
        pre.className = 'text-[10px] text-left whitespace-pre-wrap text-gray-600 bg-white border border-gray-200 rounded p-2 w-full';
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

    return { render: render };
})();
