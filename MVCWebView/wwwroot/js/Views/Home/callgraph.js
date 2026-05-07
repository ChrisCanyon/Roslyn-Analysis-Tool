/*
 * Gateway-call-graph page glue. The shared RoslynGraphUi handles SVG mechanics;
 * this file owns the page-specific concerns:
 *   - "load by FQN" via /api/CallGraph/Graph?fqn=...
 *   - dual fetch of GraphJson to populate the stats panel
 *   - click-to-drill: re-root the graph at any clicked in-source method
 */

document.addEventListener("DOMContentLoaded", () => {
    RoslynGraphUi.init({
        svgContainerId: "svgOutput",
        loadingOverlayId: "loadingOverlay",
        onNodeClick: (fqn) => {
            // Clicking a node re-roots the graph at that method. The input field
            // isn't kept in sync because the user may click an intermediate that
            // isn't a controller action.
            loadByFqn(fqn);
        },
        onBack: (previousView) => {
            if (previousView && previousView.fqn) {
                loadByFqn(previousView.fqn);
            }
        },
    });

    initControllerSearch();
});

/**
 * Wire up Awesomplete with full-namespace substring search across both the
 * visible label "[Project] Full.Namespace.Controller.Method" and the
 * underlying API value (also fully-qualified). Without this the native
 * <datalist> only filters by the value attribute, which makes it impossible
 * to disambiguate the ~10 different HomeControllers across the solution.
 */
function initControllerSearch() {
    const input = document.getElementById("controllerInput");
    if (!input || typeof Awesomplete !== "function") return;

    const items = (window.controllerActions || []).map(a => ({
        label: a.display, // visible row, includes full namespace
        value: a.value,   // becomes input.value when selected; sent to the API
    }));

    const aw = new Awesomplete(input, {
        list: items,
        minChars: 1,
        maxItems: 25,
        autoFirst: true,
        // Match user input as a case-insensitive substring against BOTH the
        // visible label AND the underlying value. Either match keeps the row.
        filter: (suggestion, userInput) => {
            const needle = userInput.toLowerCase();
            return suggestion.label.toLowerCase().includes(needle)
                || suggestion.value.toLowerCase().includes(needle);
        },
        // Show the label in the dropdown (which contains the namespace) but
        // when the user selects, the input field gets the bare value (FQN).
        item: (suggestion, userInput) => {
            const li = document.createElement("li");
            li.setAttribute("role", "option");
            li.textContent = suggestion.label;
            return li;
        },
        replace: (suggestion) => {
            input.value = suggestion.value;
        },
    });

    // Auto-load the graph when the user picks a suggestion (Enter or click).
    input.addEventListener("awesomplete-selectcomplete", () => {
        loadController();
    });
}

async function loadController() {
    const input = document.getElementById("controllerInput");
    const fqn = input.value.trim();
    if (!fqn) {
        window.alert("Pick a controller action from the list first.");
        return;
    }
    await loadByFqn(fqn);
}

async function loadByFqn(fqn) {
    RoslynGraphUi.showLoading();
    document.getElementById("currentRoot").textContent = fqn;

    try {
        // Fire SVG and JSON in parallel so the stats panel updates with the graph.
        const [svgResp, jsonResp] = await Promise.all([
            fetch(`/api/CallGraph/Graph?fqn=${encodeURIComponent(fqn)}`),
            fetch(`/api/CallGraph/GraphJson?fqn=${encodeURIComponent(fqn)}`),
        ]);

        if (!svgResp.ok) {
            const errText = await svgResp.text();
            document.getElementById("svgOutput").innerText = `Failed: ${errText}`;
            return;
        }

        const svg = await svgResp.text();
        RoslynGraphUi.renderSvg(svg, {
            contextToPush: { fqn },
        });

        if (jsonResp.ok) {
            const graph = await jsonResp.json();
            updateStats(graph);
        }
    } finally {
        RoslynGraphUi.hideLoading();
    }
}

function updateStats(graph) {
    const nodeCount = graph.nodes.length;
    const edgeCount = graph.edges.length;
    const boundaryCount = graph.nodes.filter(n => n.boundary).length;
    const controllerCount = graph.nodes.filter(n => n.kind === "controller_action").length;
    const loopEdges = graph.edges.filter(e => e.loop);
    const statementLoops = loopEdges.filter(e => e.loop.kind === "statement_loop").length;
    const enumerableLoops = loopEdges.filter(e => e.loop.kind === "enumerable_loop").length;

    // Per-category boundary counts so users can see e.g. "3 external_system_gateway"
    // even when we add more categories (database_query, etc.) later.
    const boundaryByCategory = {};
    for (const n of graph.nodes) {
        if (!n.boundary) continue;
        boundaryByCategory[n.boundary] = (boundaryByCategory[n.boundary] || 0) + 1;
    }
    const categoryLines = Object.entries(boundaryByCategory)
        .map(([name, count]) => `  ${name}: ${count}`)
        .join("\n") || "  (none)";

    document.getElementById("graphStats").textContent =
        `${nodeCount} nodes, ${edgeCount} edges
${boundaryCount} boundary node${boundaryCount === 1 ? "" : "s"} reached
${controllerCount} controller action${controllerCount === 1 ? "" : "s"} on graph
${loopEdges.length} loop edge${loopEdges.length === 1 ? "" : "s"} (${statementLoops} statement, ${enumerableLoops} LINQ)
boundaries by category:
${categoryLines}`;

    document.getElementById("boundaryCallCounts").innerHTML = formatBoundaryCallCounts(graph);

    if (loopEdges.length === 0) {
        document.getElementById("loopList").textContent = "(none)";
        return;
    }

    const lines = loopEdges.map(e => {
        const fromNode = graph.nodes[e.from];
        const toNode = graph.nodes[e.to];
        const subkind = e.loop.kind === "enumerable_loop" ? `.${e.loop.subkind}()` : e.loop.subkind;
        const file = e.loop.file ? shortenPath(e.loop.file) : "?";
        return `${shortName(fromNode)} -> ${shortName(toNode)}\n  ${subkind} @ ${file}:${e.loop.line}`;
    });
    document.getElementById("loopList").textContent = lines.join("\n\n");
}

function shortName(node) {
    if (!node) return "?";
    const t = node.type ? node.type.split(".").pop() : "?";
    return `${t}.${node.method}`;
}

/**
 * Render the path-aware call-count list as colored HTML. The polynomials come
 * from the server (BoundaryCallCountAnalyzer); we just style each row by the
 * severity of its leading degree:
 *   degree 0 (constant)   -> neutral grey  (no loop in any path)
 *   degree 1 (N)          -> yellow
 *   degree 2 (N^2)        -> orange
 *   degree 3 (N^3)        -> red
 *   degree 4+             -> bright red (something is very wrong)
 *
 * Server sort is alphabetical-by-FQN so methods on the same class cluster.
 */
function formatBoundaryCallCounts(graph) {
    const counts = graph.boundaryCallCounts || [];
    if (counts.length === 0) return "(none)";
    const byId = new Map(graph.nodes.map(n => [n.id, n]));
    return counts.map(c => {
        const node = byId.get(c.nodeId);
        const name = htmlEscape(shortName(node));
        const formula = htmlEscape(c.formula);
        const degree = leadingDegree(c.coefficients);
        const severityClass = degree >= 4 ? "cc-d4"
            : degree === 3 ? "cc-d3"
            : degree === 2 ? "cc-d2"
            : degree === 1 ? "cc-d1"
            : "cc-d0";
        return `<span class="cc-row"><span class="cc-name">${name}:</span> <span class="cc-formula ${severityClass}">${formula}</span></span>`;
    }).join("");
}

function leadingDegree(coefficients) {
    if (!coefficients) return 0;
    for (let i = coefficients.length - 1; i >= 0; i--) {
        if (coefficients[i] !== 0) return i;
    }
    return 0;
}

function htmlEscape(s) {
    if (s == null) return "";
    return String(s)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;");
}

function shortenPath(path) {
    // Show last 2 path segments — full Windows paths overflow the panel.
    const norm = path.replace(/\\/g, "/");
    const parts = norm.split("/").filter(Boolean);
    return parts.slice(-2).join("/");
}

function loadPrevious() {
    RoslynGraphUi.back();
}
