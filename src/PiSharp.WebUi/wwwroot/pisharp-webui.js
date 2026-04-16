// PiSharp.WebUi — client-side code highlighting integration.
// Consumers must include highlight.js (or Prism.js) in their host page.
// This script auto-highlights <code> blocks with language-* classes after
// Blazor renders.

window.PiSharpWebUi = {
    highlightAll: function () {
        if (typeof hljs !== 'undefined') {
            document.querySelectorAll('pre code[class*="language-"]').forEach(function (block) {
                if (!block.dataset.highlighted) {
                    hljs.highlightElement(block);
                }
            });
        } else if (typeof Prism !== 'undefined') {
            Prism.highlightAll();
        }
    }
};
