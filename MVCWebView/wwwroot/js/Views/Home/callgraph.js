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
// Hide-impls is derived state — recomputed before every fetch from
// selectedTypeByInterface + the last rendered graph's dispatch edges.
// User intent lives in selectedTypeByInterface (sticky); this is just
// the wire-format flat list the server expects on every request.
let hiddenImplFqns = new Set();

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

    // Derive hide-impls from the current sticky selections + the LAST
    // rendered graph's dispatch edges. On the very first load currentGraph
    // is null and the result is empty — correct, no filter to apply yet.
    hiddenImplFqns = deriveHiddenImpls(currentGraph);

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
            // Re-derive against the freshly fetched graph so the next
            // render or export sees the up-to-date hide list (focus may
            // have pruned dispatch sites that were in the previous graph).
            hiddenImplFqns = deriveHiddenImpls(currentGraph);
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
            // Render shape:
            //   foreach accounts                  (foreach + source)
            //   foreach                           (no source — defensive)
            //   .Select(accounts)                 (LINQ + source)
            //   .Select()                         (LINQ + no source)
            // The source is the enumerable expression for foreach/LINQ, or
            // the condition text for for/while/do — see LoopDetector.
            const src = e.loop.source;
            let subkind;
            if (e.loop.kind === "enumerable_loop") {
                subkind = src ? `.${e.loop.subkind}(${src})` : `.${e.loop.subkind}()`;
            } else {
                subkind = src ? `${e.loop.subkind} ${src}` : e.loop.subkind;
            }
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

/* ---------- Per-interface impl picker (side panel) ---------- */

/**
 * User's selection per interface (or virtual base class) — the dispatch
 * source's TYPE FQN maps to a concrete impl class FQN. Sticky across
 * navigation: a selection persists even when the interface isn't visible
 * in the current view, and reapplies automatically when it reappears.
 *
 * <c>null</c> entry / missing key both mean "(All impls)" — no filtering
 * for that interface. Selections are committed only on Apply.
 */
const selectedTypeByInterface = new Map();

/**
 * Pending picks the user has made but hasn't applied yet. Same shape as
 * <c>selectedTypeByInterface</c>; cleared on Apply or Reset. The dropdown
 * shows the pending value but the rendered graph only reflects the
 * committed selections until Apply.
 */
const pendingSelections = new Map();

const ALL_IMPLS_VALUE = "__all__";

/**
 * Rebuild the picker rows from the current rendered graph. Source of truth:
 *
 *   1. Walk every dispatch edge in <paramref name="graph"/>.
 *   2. Group by the from-node's TYPE FQN (interface or virtual base class).
 *   3. For each group, collect every concrete type that appears in any
 *      ServesTypes list across the group's edges. Dedupe.
 *   4. Show a row per group with ≥2 distinct concrete types.
 *
 * The user picks one concrete type per row. On Apply, the JS computes the
 * flat hide list from the current graph's dispatch edges + the picks and
 * sends it as <c>hideImpls</c>; the server's CallGraphHideImpls post-pass
 * does the actual filtering against the cached canonical graph.
 *
 * Selections are sticky in <c>selectedTypeByInterface</c>. If the user
 * picked Munis for IUtilityBillingGateway and then navigates somewhere
 * IUtilityBillingGateway isn't dispatched, the row vanishes but the pick
 * survives. Navigate back, the row reappears with the pick still applied.
 */
function rebuildInterfaceImplList(graph) {
    const container = document.getElementById("interfaceImplList");
    if (!container) return;

    const rows = computePickerRows(graph);

    if (rows.length === 0) {
        container.className = "ii-empty";
        container.textContent = "(no interfaces with multiple impls in this graph)";
        updateApplyBadge();
        return;
    }

    container.className = "";
    container.innerHTML = "";

    for (const row of rows) {
        const wrap = document.createElement("div");
        wrap.className = "ii-iface";

        const header = document.createElement("div");
        header.className = "ii-iface-header-static";
        header.title = row.typeFqn;
        header.textContent = shortNameOfFqn(row.typeFqn);
        wrap.appendChild(header);

        const sel = document.createElement("select");
        sel.className = "ii-select";

        const allOpt = document.createElement("option");
        allOpt.value = ALL_IMPLS_VALUE;
        allOpt.textContent = `(All impls — ${row.concretes.length})`;
        sel.appendChild(allOpt);

        // Sort concretes by short name for stable scanning between renders.
        const sorted = [...row.concretes].sort((a, b) =>
            shortNameOfFqn(a).localeCompare(shortNameOfFqn(b)));
        for (const concreteFqn of sorted) {
            const opt = document.createElement("option");
            opt.value = concreteFqn;
            opt.textContent = shortNameOfFqn(concreteFqn);
            opt.title = concreteFqn;
            sel.appendChild(opt);
        }

        // Initial dropdown value: pending pick > committed selection > All.
        const pending = pendingSelections.has(row.typeFqn) ? pendingSelections.get(row.typeFqn) : undefined;
        const applied = selectedTypeByInterface.get(row.typeFqn) || null;
        const effective = pending !== undefined ? pending : applied;
        sel.value = effective ?? ALL_IMPLS_VALUE;

        // Yellow ring while a pick differs from the applied selection.
        if (pending !== undefined && pending !== applied) {
            sel.classList.add("ii-select-pending");
        }

        sel.addEventListener("change", () => onImplDropdownChange(row.typeFqn, sel.value));
        wrap.appendChild(sel);

        container.appendChild(wrap);
    }

    updateApplyBadge();
}

/**
 * Read the picker snapshot from the graph payload. The server computes this
 * once per maximal graph (in DispatchServesTypesResolver) and carries it
 * unchanged through every post-pass prune — so the picker offers the SAME
 * options regardless of whether the user is currently focused, collapsed,
 * or hiding impls. Same controller means same options.
 *
 * Returned shape: <c>[{ typeFqn, concretes: string[] }]</c>, one entry per
 * interface or virtual-base type with ≥2 concrete impls in this controller's
 * call graph. Sorted by short name for stable scanning.
 */
function computePickerRows(graph) {
    if (!graph || !Array.isArray(graph.interfaceImpls)) return [];

    const rows = [];
    for (const info of graph.interfaceImpls) {
        const concretes = Array.isArray(info.concretes) ? info.concretes : [];
        if (concretes.length < 2) continue; // nothing to pick between
        rows.push({ typeFqn: info.typeFqn, concretes });
    }
    rows.sort((a, b) => shortNameOfFqn(a.typeFqn).localeCompare(shortNameOfFqn(b.typeFqn)));
    return rows;
}

/**
 * The from-node FQN is "Namespace.Type.Method(args)". Strip the trailing
 * .Method(args) so the picker keys on the bare type FQN — picking once
 * for "IUtilityBillingGateway" applies across all of its methods.
 */
function stripMethodFromFqn(methodFqn) {
    const noArgs = methodFqn.split("(")[0];
    const lastDot = noArgs.lastIndexOf(".");
    return lastDot < 0 ? noArgs : noArgs.substring(0, lastDot);
}

/**
 * User picked something in a dropdown. Compare to the committed selection;
 * if different, record as pending. If returned-to-applied, clear the pending
 * entry. Updates the Apply badge and re-renders the picker (so the yellow
 * pending ring shows up); doesn't fetch.
 */
function onImplDropdownChange(typeFqn, value) {
    const newPick = value === ALL_IMPLS_VALUE ? null : value;
    const applied = selectedTypeByInterface.get(typeFqn) || null;
    if (newPick === applied) {
        pendingSelections.delete(typeFqn);
    } else {
        pendingSelections.set(typeFqn, newPick);
    }
    rebuildInterfaceImplList(currentGraph);
}

function updateApplyBadge() {
    const btn = document.getElementById("iiApplyBtn");
    const badge = document.getElementById("iiPendingBadge");
    const n = pendingSelections.size;
    if (btn) btn.disabled = n === 0;
    if (btn) btn.textContent = n === 0 ? "Apply" : `Apply (${n})`;
    if (badge) {
        badge.style.display = n === 0 ? "none" : "inline-block";
        badge.textContent = String(n);
    }
}

/**
 * Commit pending picks → selectedTypeByInterface and re-render. Multiple
 * picks across multiple interfaces batch into ONE re-render — no N fetches.
 */
function applyImplChanges() {
    if (pendingSelections.size === 0) return;
    for (const [typeFqn, pick] of pendingSelections) {
        if (pick === null) selectedTypeByInterface.delete(typeFqn);
        else selectedTypeByInterface.set(typeFqn, pick);
    }
    pendingSelections.clear();
    if (currentRootFqn) renderCurrentView(currentRootFqn);
}

/**
 * Drop every selection (committed + pending) and re-render. Equivalent to
 * "(All impls)" everywhere.
 */
function resetImplChanges() {
    if (selectedTypeByInterface.size === 0 && pendingSelections.size === 0) return;
    selectedTypeByInterface.clear();
    pendingSelections.clear();
    if (currentRootFqn) renderCurrentView(currentRootFqn);
}

/**
 * Translate the user's per-interface picks into a flat hide list of
 * impl-method FQNs. The server's hideImpls query param expects this shape
 * (CallGraphHideImpls.Prune drops nodes whose FQN is in the set, plus
 * orphan subtrees). Computed against <paramref name="graph"/>'s dispatch
 * edges so the result reflects the current view.
 *
 * Per-edge rule: if the edge's from-type has a selection AND the edge's
 * ServesTypes does NOT contain the picked concrete class, hide the To
 * node. Edges whose ServesTypes DO contain the picked class survive —
 * automatically correct for both override (Munis.M serves [Munis]) and
 * inheritance (RestApi.M serves [RestApi, Munis] when Munis inherits).
 *
 * Reachability fallback: if no edge at a given dispatch site serves the
 * picked class (e.g. user picked something not present in this view), we
 * skip filtering for that site rather than hiding everything — keeps the
 * graph sensible when picks are sticky across navigation.
 */
function deriveHiddenImpls(graph) {
    const hidden = new Set();
    if (!graph || selectedTypeByInterface.size === 0) return hidden;

    const nodesById = new Map(graph.nodes.map(n => [n.id, n]));
    const edgesByFrom = new Map();
    for (const e of graph.edges) {
        if (!e.dispatch) continue;
        if (!edgesByFrom.has(e.from)) edgesByFrom.set(e.from, []);
        edgesByFrom.get(e.from).push(e);
    }

    for (const [, edges] of edgesByFrom) {
        // Per dispatch site, does the picked type for this from-type appear
        // in ANY edge's ServesTypes? If not, skip — picked class isn't
        // reachable from this site, hiding everything would be wrong.
        let pickReachable = false;
        let picked = null;
        for (const e of edges) {
            const fromNode = nodesById.get(e.from);
            if (!fromNode) continue;
            const fromTypeFqn = stripMethodFromFqn(fromNode.fqn);
            picked = selectedTypeByInterface.get(fromTypeFqn) || null;
            if (!picked) break;
            const serves = Array.isArray(e.servesTypes) ? e.servesTypes : [];
            if (serves.includes(picked)) { pickReachable = true; break; }
        }
        if (!picked || !pickReachable) continue;

        for (const e of edges) {
            const serves = Array.isArray(e.servesTypes) ? e.servesTypes : [];
            if (serves.includes(picked)) continue;
            const toFqn = nodesById.get(e.to)?.fqn;
            if (toFqn) hidden.add(toFqn);
        }
    }
    return hidden;
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
