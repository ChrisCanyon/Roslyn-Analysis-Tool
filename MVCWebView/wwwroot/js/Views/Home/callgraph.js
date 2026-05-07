/*
 * Gateway-call-graph page glue. The shared RoslynGraphUi handles SVG mechanics;
 * this file owns the page-specific concerns:
 *   - "load by FQN" via /api/CallGraph/Graph?fqn=...
 *   - dual fetch of GraphJson to populate the stats panel
 *   - click-to-drill: re-root the graph at any clicked in-source method
 */

// Latest graph JSON, cached so the click handler can ask "is this an interface
// with dispatch impls?" without re-fetching.
let currentGraph = null;
// The controller-level FQN we last loaded a graph FOR (the unchanging "root"
// of the current view). Distinct from currentFocus, which prunes the graph
// to ancestors+descendants of the clicked node within that root's graph.
let currentRootFqn = null;
// The currently focused-on FQN, or null for "show full graph." Set by clicking
// a node, cleared by the "clear focus" link or loading a new controller.
let currentFocus = null;
// FQNs of impls the user has toggled OFF. Persists across re-renders so the
// hidden state stays sticky as the user navigates.
const hiddenImplFqns = new Set();

document.addEventListener("DOMContentLoaded", () => {
    RoslynGraphUi.init({
        svgContainerId: "svgOutput",
        loadingOverlayId: "loadingOverlay",
        onNodeClick: (fqn) => {
            // Click = focus on the clicked node within the CURRENT root's graph.
            // We keep the upstream context (root -> ... -> clicked) and the
            // clicked node's downstream subtree; siblings get pruned.
            // Impl visibility is handled in the Interface Impls side panel.
            focusOnFqn(fqn);
        },
        onBack: (previousView) => {
            if (!previousView || !previousView.fqn) return;
            // Restore both root and focus so Back is a true undo of whatever
            // navigation step (re-root or focus) put us where we are.
            currentFocus = previousView.focus || null;
            renderCurrentView(previousView.fqn);
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
    // Loading by FQN switches the view's *root* — clear any active focus so
    // we always start with the full graph for a freshly chosen controller.
    currentFocus = null;
    await renderCurrentView(fqn);
}

/**
 * Re-render the graph for the given root FQN, applying the current focus,
 * expand toggle, and hidden-impls set. All re-render paths funnel through
 * here so they stay consistent.
 */
async function renderCurrentView(rootFqn) {
    // Boundary nodes carry a "\n[boundary: ...]" suffix in their click-side
    // identifier (it's their Graphviz tooltip, not the bare FQN). Strip it
    // before sending to the server or displaying as the current root.
    const cleanRoot = stripBoundarySuffix(rootFqn);
    currentRootFqn = cleanRoot;

    RoslynGraphUi.showLoading();
    document.getElementById("currentRoot").textContent = cleanRoot;
    updateFocusBanner();

    const expandQs = isExpandEnabled() ? "&expand=true" : "";
    const hideQs = hiddenImplFqns.size > 0
        ? `&hideImpls=${encodeURIComponent([...hiddenImplFqns].join("|"))}`
        : "";
    const focusQs = currentFocus
        ? `&focus=${encodeURIComponent(currentFocus)}`
        : "";

    try {
        // Fire SVG and JSON in parallel so the stats panel updates with the graph.
        const [svgResp, jsonResp] = await Promise.all([
            fetch(`/api/CallGraph/Graph?fqn=${encodeURIComponent(cleanRoot)}${expandQs}${hideQs}${focusQs}`),
            fetch(`/api/CallGraph/GraphJson?fqn=${encodeURIComponent(cleanRoot)}${expandQs}${hideQs}${focusQs}`),
        ]);

        if (!svgResp.ok) {
            const errText = await svgResp.text();
            document.getElementById("svgOutput").innerText = `Failed: ${errText}`;
            return;
        }

        const svg = await svgResp.text();
        // Push (root, focus) so Back can restore both. The shared lib treats
        // contextToPush as opaque; only our onBack callback reads it.
        RoslynGraphUi.renderSvg(svg, {
            contextToPush: { fqn: cleanRoot, focus: currentFocus },
        });

        if (jsonResp.ok) {
            const graph = await jsonResp.json();
            currentGraph = graph; // stash so click handler can probe for impls
            updateStats(graph);
        }

        // A view is now exportable. Clear any stale status from a previous
        // export so the user doesn't see "exported to X" pinned to a
        // different graph.
        const exportBtn = document.getElementById("exportBtn");
        if (exportBtn) exportBtn.disabled = false;
        setExportStatus("", null);
    } finally {
        RoslynGraphUi.hideLoading();
    }
}

/**
 * POST the current view's params to /api/CallGraph/Export. Mirrors the GET
 * /Graph + /GraphJson params exactly so the dump matches what the user sees.
 * Server returns the absolute folder path; we surface it under the button.
 */
async function exportCurrentView() {
    if (!currentRootFqn) return;
    const btn = document.getElementById("exportBtn");
    if (btn) btn.disabled = true;
    setExportStatus("Exporting…", null);

    const params = new URLSearchParams();
    params.set("fqn", currentRootFqn);
    if (isExpandEnabled()) params.set("expand", "true");
    if (hiddenImplFqns.size > 0) params.set("hideImpls", [...hiddenImplFqns].join("|"));
    if (currentFocus) params.set("focus", currentFocus);

    try {
        const resp = await fetch(`/api/CallGraph/Export?${params.toString()}`, { method: "POST" });
        if (!resp.ok) {
            const errText = await resp.text();
            setExportStatus(`Export failed: ${errText}`, "error");
            return;
        }
        const data = await resp.json();
        setExportStatus(`Exported to ${data.folder}`, "success");
    } catch (err) {
        setExportStatus(`Export failed: ${err.message || err}`, "error");
    } finally {
        if (btn) btn.disabled = false;
    }
}

function setExportStatus(text, kind) {
    const el = document.getElementById("exportStatus");
    if (!el) return;
    el.textContent = text;
    el.classList.remove("success", "error");
    if (kind) el.classList.add(kind);
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

    // Refresh the interface-impls picker so newly discovered interfaces show
    // up and previously hidden impls are still toggleable from memory.
    rebuildInterfaceImplList(graph);

    const byId = new Map(graph.nodes.map(n => [n.id, n]));

    if (loopEdges.length === 0) {
        document.getElementById("loopList").textContent = "(none)";
    } else {
        const lines = loopEdges.map(e => {
            const fromNode = byId.get(e.from);
            const toNode = byId.get(e.to);
            const subkind = e.loop.kind === "enumerable_loop" ? `.${e.loop.subkind}()` : e.loop.subkind;
            const file = e.loop.file ? shortenPath(e.loop.file) : "?";
            return `${shortName(fromNode)} -> ${shortName(toNode)}\n  ${subkind} @ ${file}:${e.loop.line}`;
        });
        document.getElementById("loopList").textContent = lines.join("\n\n");
    }

    // Conditional edges: independent of loops. A call site can be in both.
    // We list every edge that has a `conditional` annotation; format mirrors
    // the loop list. Else branches are flagged so the reader knows the call
    // only happens when the parent if predicate is FALSE.
    const conditionalEdges = graph.edges.filter(e => e.conditional);
    if (conditionalEdges.length === 0) {
        document.getElementById("conditionalList").textContent = "(none)";
    } else {
        const condLines = conditionalEdges.map(e => {
            const fromNode = byId.get(e.from);
            const toNode = byId.get(e.to);
            const c = e.conditional;
            const file = c.file ? shortenPath(c.file) : "?";
            const prefix = c.kind === "ternary" ? "?:"
                : c.isElseBranch ? "else of:"
                : "if";
            const cond = c.condition ? ` ${c.condition}` : "";
            return `${shortName(fromNode)} -> ${shortName(toNode)}\n  ${prefix}${cond} @ ${file}:${c.line}`;
        });
        document.getElementById("conditionalList").textContent = condLines.join("\n\n");
    }
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

/**
 * Read the "Expand past boundaries" checkbox. Defaults to false if the input
 * is missing (e.g. older cached HTML). The state is included as a query string
 * on every Graph/GraphJson fetch so the server caches expanded vs. non-expanded
 * variants separately.
 */
function isExpandEnabled() {
    const el = document.getElementById("expandToggle");
    return !!(el && el.checked);
}

/**
 * Re-render the current graph when the toggle flips. We use the FQN already
 * displayed in the "Current root" panel rather than re-reading from the input
 * because the user may have drilled into a non-controller intermediate.
 */
function onExpandToggleChanged() {
    if (!currentRootFqn) return; // nothing loaded yet
    renderCurrentView(currentRootFqn);
}

/* ---------- Focus mode ---------- */

/**
 * Focus on a node within the current root's graph. Pruned to ancestors of
 * the focused node + the focused node + its descendants; siblings off other
 * branches go away. Re-fetches with the new focus query param.
 *
 * The root stays the same. To return to the full graph, call clearFocus().
 */
function focusOnFqn(fqn) {
    if (!currentRootFqn) return;
    const cleanFqn = stripBoundarySuffix(fqn);
    if (cleanFqn === currentFocus) return; // no change
    currentFocus = cleanFqn;
    renderCurrentView(currentRootFqn);
}

function clearFocus() {
    if (!currentFocus) return;
    currentFocus = null;
    if (currentRootFqn) renderCurrentView(currentRootFqn);
}

function updateFocusBanner() {
    const banner = document.getElementById("focusBanner");
    if (!banner) return;
    if (currentFocus) {
        banner.style.display = "block";
        const target = banner.querySelector(".focus-target");
        if (target) target.textContent = currentFocus;
    } else {
        banner.style.display = "none";
    }
}

/**
 * Boundary nodes carry a "\n[boundary: ...]" suffix in their click-side
 * identifier (it's their Graphviz tooltip). Strip it for any place we need
 * the bare FQN (server param, banner text, equality comparisons).
 */
function stripBoundarySuffix(fqn) {
    if (!fqn) return fqn;
    const i = fqn.indexOf("\n");
    return i === -1 ? fqn : fqn.substring(0, i);
}

/* ---------- Interface impls picker (side panel) ---------- */

/**
 * Per-interface remembered impl FQNs. Hidden impls don't appear in the current
 * graph's edges (server-side filter), so without this we'd lose the ability to
 * un-hide them after the first apply. Accumulates across loads.
 */
const knownImplsByInterface = new Map();

/**
 * Pending toggle changes the user has made but hasn't applied yet. Each entry
 * is `implFqn -> intendedHiddenState`. Cleared on Apply or Reset. The pending
 * state is what gets shown with yellow/green color in the panel; only on
 * Apply does it merge into hiddenImplFqns and trigger a rebuild.
 */
const pendingImplChanges = new Map();

/**
 * Update the side-panel "Interface impls" section based on the current graph.
 * Walks every node with outgoing dispatch edges, accumulates impls into the
 * per-interface memory, then renders one collapsible row per interface that
 * has 2+ known impls (or 1+ where any impl is currently/pending hidden).
 *
 * Single-impl interfaces with everything visible are skipped — they offer
 * nothing to toggle and would just be noise.
 */
function rebuildInterfaceImplList(graph) {
    const container = document.getElementById("interfaceImplList");
    if (!container) return;

    // Index: interfaceFqn -> Set<implFqn>, accumulated from the current graph.
    if (graph) {
        const nodesById = new Map(graph.nodes.map(n => [n.id, n]));
        const dispatchByFrom = new Map();
        for (const e of graph.edges) {
            if (!e.dispatch) continue;
            if (!dispatchByFrom.has(e.from)) dispatchByFrom.set(e.from, []);
            dispatchByFrom.get(e.from).push(e.to);
        }
        for (const [fromId, toIds] of dispatchByFrom) {
            const ifaceFqn = nodesById.get(fromId)?.fqn;
            if (!ifaceFqn) continue;
            if (!knownImplsByInterface.has(ifaceFqn)) knownImplsByInterface.set(ifaceFqn, new Set());
            const set = knownImplsByInterface.get(ifaceFqn);
            for (const tid of toIds) {
                const impl = nodesById.get(tid);
                if (impl) set.add(impl.fqn);
            }
        }
    }

    // Render. Sort by interface short name for predictable scanning.
    const interfaces = [...knownImplsByInterface.entries()]
        .filter(([, impls]) => {
            // Skip single-impl interfaces with no hidden/pending toggles —
            // nothing to choose between.
            if (impls.size >= 2) return true;
            return [...impls].some(f => hiddenImplFqns.has(f) || pendingImplChanges.has(f));
        })
        .sort((a, b) => shortNameOfFqn(a[0]).localeCompare(shortNameOfFqn(b[0])));

    if (interfaces.length === 0) {
        container.className = "ii-empty";
        container.textContent = "(no interfaces with multiple impls in this graph)";
        updateApplyBadge();
        return;
    }

    container.className = "";
    container.innerHTML = "";

    for (const [ifaceFqn, implsSet] of interfaces) {
        const impls = [...implsSet].sort((a, b) => a.localeCompare(b));
        const wrap = document.createElement("div");
        wrap.className = "ii-iface";
        // Remember expand state across re-renders. We use a data attribute on
        // a hidden marker keyed by FQN; if present, expand by default.
        if (expandedInterfaces.has(ifaceFqn)) wrap.classList.add("expanded");

        const header = document.createElement("div");
        header.className = "ii-iface-header";
        header.title = ifaceFqn;
        const caret = document.createElement("span");
        caret.className = "ii-caret";
        header.appendChild(caret);
        header.appendChild(document.createTextNode(" " + shortNameOfFqn(ifaceFqn) + ` (${impls.length})`));
        header.addEventListener("click", () => {
            wrap.classList.toggle("expanded");
            if (wrap.classList.contains("expanded")) expandedInterfaces.add(ifaceFqn);
            else expandedInterfaces.delete(ifaceFqn);
        });
        wrap.appendChild(header);

        const list = document.createElement("div");
        list.className = "ii-impl-list";
        for (const implFqn of impls) {
            const baseHidden = hiddenImplFqns.has(implFqn);
            const pending = pendingImplChanges.has(implFqn) ? pendingImplChanges.get(implFqn) : null;
            const effectiveHidden = pending !== null ? pending : baseHidden;

            const lbl = document.createElement("label");
            lbl.className = "ii-impl";
            if (pending !== null) {
                lbl.classList.add(pending ? "ii-impl-pending" : "ii-impl-pending-show");
            }

            const cb = document.createElement("input");
            cb.type = "checkbox";
            cb.checked = !effectiveHidden;
            cb.addEventListener("change", () => onImplCheckboxChange(implFqn, cb.checked));
            lbl.appendChild(cb);
            lbl.appendChild(document.createTextNode(shortNameOfFqn(implFqn)));
            list.appendChild(lbl);
        }
        wrap.appendChild(list);
        container.appendChild(wrap);
    }

    updateApplyBadge();
}

const expandedInterfaces = new Set();

/**
 * User flipped a checkbox. Compare to the current applied state; if the new
 * state differs, record it as pending. If it matches the applied state (i.e.
 * the user toggled twice and is back where they started), drop the pending
 * entry. Updates the Apply badge but doesn't fetch.
 */
function onImplCheckboxChange(implFqn, visible) {
    const intendedHidden = !visible;
    const baseHidden = hiddenImplFqns.has(implFqn);
    if (intendedHidden === baseHidden) {
        pendingImplChanges.delete(implFqn);
    } else {
        pendingImplChanges.set(implFqn, intendedHidden);
    }
    updateApplyBadge();
    // Re-render only the row colors — cheaper than a full rebuild, and we
    // don't want to lose checkbox focus or expand state.
    rebuildInterfaceImplList(currentGraph);
}

function updateApplyBadge() {
    const btn = document.getElementById("iiApplyBtn");
    const badge = document.getElementById("iiPendingBadge");
    const n = pendingImplChanges.size;
    if (btn) btn.disabled = n === 0;
    if (btn) btn.textContent = n === 0 ? "Apply" : `Apply (${n})`;
    if (badge) {
        badge.style.display = n === 0 ? "none" : "inline-block";
        badge.textContent = String(n);
    }
}

function applyImplChanges() {
    if (pendingImplChanges.size === 0) return;
    for (const [implFqn, hidden] of pendingImplChanges) {
        if (hidden) hiddenImplFqns.add(implFqn);
        else hiddenImplFqns.delete(implFqn);
    }
    pendingImplChanges.clear();
    if (currentRootFqn) renderCurrentView(currentRootFqn);
}

function resetImplChanges() {
    if (hiddenImplFqns.size === 0 && pendingImplChanges.size === 0) return;
    hiddenImplFqns.clear();
    pendingImplChanges.clear();
    if (currentRootFqn) renderCurrentView(currentRootFqn);
}

/**
 * Last-two-segment short name of an FQN, dropping argument lists. Mirrors what
 * the graph renderer shows on each node so the picker labels feel familiar.
 */
function shortNameOfFqn(fqn) {
    const noArgs = fqn.split("(")[0];
    const parts = noArgs.split(".");
    return parts.length >= 2 ? parts.slice(-2).join(".") : noArgs;
}
