/*
 * roslyn-graph-ui.js — shared client for Roslyn-driven graph UIs.
 *
 * Owns the generic mechanics: SVG pan/zoom, click-to-drill, history-stack
 * navigation, loading overlay, center-on-node. Host pages provide endpoint
 * URLs and view-specific callbacks via RoslynGraphUi.init(config).
 *
 * Backwards-compatible note: MVCWebView's older index.js exposed a handful of
 * top-level functions (loadSingleNode, loadEntireProject, etc.) that its Razor
 * page wires to onclick handlers. Those live in MVCWebView's own per-page JS
 * now and call into this library.
 *
 * Usage:
 *   RoslynGraphUi.init({
 *     svgContainerId: "svgOutput",            // element to receive SVG HTML
 *     loadingOverlayId: "loadingOverlay",     // optional
 *     onNodeClick: (nodeId) => { ... },       // called when a node is clicked
 *     onBack: (previousView) => { ... },      // called when Back is invoked
 *   });
 *
 * The host then drives navigation by calling:
 *   RoslynGraphUi.showLoading() / hideLoading()
 *   RoslynGraphUi.renderSvg(svgString, { centerOnNodeId, contextToPush })
 *   RoslynGraphUi.back()
 */
(function (global) {
    "use strict";

    const config = {
        svgContainerId: "svgOutput",
        loadingOverlayId: "loadingOverlay",
        onNodeClick: null,
        onBack: null,
    };

    const historyStack = [];

    function init(userConfig) {
        Object.assign(config, userConfig || {});
    }

    function showLoading() {
        const overlay = document.getElementById(config.loadingOverlayId);
        if (!overlay) return;
        const img = overlay.querySelector("img");
        overlay.classList.add("is-visible");
        if (img) {
            img.classList.remove("grow");
            void img.offsetWidth; // reflow to restart animation
            img.classList.add("grow");
        }
    }

    function hideLoading() {
        const overlay = document.getElementById(config.loadingOverlayId);
        if (overlay) overlay.classList.remove("is-visible");
    }

    /**
     * Replace the current SVG with a new one and rebind pan/zoom + click handlers.
     * @param {string} svgString - The raw SVG markup.
     * @param {object} opts
     * @param {string=} opts.centerOnNodeId - Node title to pan to after render.
     * @param {*=} opts.contextToPush - Opaque value pushed onto the history stack.
     */
    function renderSvg(svgString, opts) {
        opts = opts || {};
        const container = document.getElementById(config.svgContainerId);
        if (!container) return;
        container.innerHTML = svgString;

        if (opts.contextToPush !== undefined) {
            historyStack.push(opts.contextToPush);
        }

        configureSvg(opts.centerOnNodeId);
    }

    function configureSvg(centerOnNodeId) {
        const container = document.getElementById(config.svgContainerId);
        const svg = container ? container.querySelector("svg") : null;
        if (!svg) return;

        svg.removeAttribute("width");
        svg.removeAttribute("height");
        svg.setAttribute("width", "100%");
        svg.setAttribute("height", "100%");
        svg.setAttribute("preserveAspectRatio", "xMidYMid meet");
        svg.style.maxWidth = "100%";
        svg.style.height = "100%";

        if (!svg.hasAttribute("viewBox")) {
            try {
                const bbox = svg.getBBox();
                svg.setAttribute("viewBox", `0 0 ${bbox.width} ${bbox.height}`);
            } catch (e) {
                // getBBox can throw if the SVG is hidden; pan/zoom still works without viewBox
            }
        }

        if (typeof svgPanZoom === "function") {
            // Destroy the previous instance (it points at a detached SVG element
            // and otherwise leaks + leaves the new graph stuck in the prior viewport).
            if (global.panZoomInstance) {
                try { global.panZoomInstance.destroy(); } catch (e) { /* ignore */ }
                global.panZoomInstance = null;
            }
            try {
                global.panZoomInstance = svgPanZoom(svg, {
                    zoomEnabled: true,
                    controlIconsEnabled: true,
                    fit: true,
                    center: true,
                    minZoom: 0.5,
                    maxZoom: 1000,
                    contain: true,
                });
                // Defensive re-fit/re-center: with controlIconsEnabled the bbox
                // can shift after the constructor returns, leaving the viewport
                // anchored to the previous graph's coordinates.
                global.panZoomInstance.resize();
                global.panZoomInstance.fit();
                global.panZoomInstance.center();
            } catch (e) {
                console.warn("svgPanZoom init failed:", e);
            }
        }

        attachClickHandlers();
        if (centerOnNodeId) centerOnNode(centerOnNodeId);
    }

    function attachClickHandlers() {
        const container = document.getElementById(config.svgContainerId);
        const svg = container ? container.querySelector("svg") : null;
        if (!svg) return;

        const nodes = svg.querySelectorAll("g.node");
        nodes.forEach((node) => {
            const titleElement = node.querySelector("title");
            if (!titleElement) return;
            const nodeId = titleElement.textContent ? titleElement.textContent.trim() : null;
            if (!nodeId) return;

            node.style.cursor = "pointer";
            node.addEventListener("click", () => {
                if (typeof config.onNodeClick === "function") {
                    config.onNodeClick(nodeId);
                }
            });
        });
    }

    function findNodeByTitle(nodeId) {
        const container = document.getElementById(config.svgContainerId);
        const svg = container ? container.querySelector("svg") : null;
        if (!svg) return null;
        const nodes = svg.querySelectorAll("g.node");
        for (const node of nodes) {
            const t = node.querySelector("title");
            if (t && t.textContent && t.textContent.trim() === nodeId) return node;
        }
        return null;
    }

    function centerOnNode(nodeId) {
        const node = findNodeByTitle(nodeId);
        if (!node || !global.panZoomInstance) return;
        const container = document.getElementById(config.svgContainerId);
        const svg = container ? container.querySelector("svg") : null;
        if (!svg) return;

        let bbox;
        try { bbox = node.getBBox(); } catch (e) { return; }
        const zoom = global.panZoomInstance.getZoom();
        global.panZoomInstance.pan({
            x: svg.clientWidth / 2 - (bbox.x + bbox.width / 2) * zoom,
            y: svg.clientHeight / 2 - (bbox.y + bbox.height / 2) * zoom,
        });
    }

    function back() {
        if (historyStack.length < 2) {
            console.warn("RoslynGraphUi: nothing to go back to.");
            return;
        }
        historyStack.pop(); // current view
        const previous = historyStack.pop(); // host will re-push when it re-renders
        if (typeof config.onBack === "function") {
            config.onBack(previous);
        }
    }

    function getHistory() {
        return historyStack.slice();
    }

    function clearHistory() {
        historyStack.length = 0;
    }

    global.RoslynGraphUi = {
        init,
        showLoading,
        hideLoading,
        renderSvg,
        back,
        centerOnNode,
        findNodeByTitle,
        getHistory,
        clearHistory,
    };
})(window);
