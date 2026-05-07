# Roslyn Analysis Tool (RAT)

A toolkit for understanding large .NET solutions through static analysis. It loads your solution with Roslyn once, builds several models of it, and surfaces them through both a CLI and an interactive web UI.

It does two main things:

1. **DI dependency analysis** — parses Castle Windsor and `Microsoft.Extensions.DependencyInjection` registrations across the solution, builds a full implementation/interface dependency graph, and detects lifetime mismatches, transient leaks, manual disposal of injected services, and unsafe `PerWebRequest`/async resolution patterns.
2. **Per-controller call-graph analysis** — given any controller action, walks down through every invocation in its body to the I/O boundary (gateway methods, repositories, HTTP clients, DB primitives), tagging each leaf with what kind of side effect it represents. Produces an interactive Graphviz SVG with loop, conditional, and dispatch annotations, plus a path-aware polynomial estimate of how many times each boundary is hit per controller call (`N²+2N+1` and friends).

---

## What you get

### Dependency UI (`/`)

- Search any class by name (Awesomplete autocomplete on every implementation in the loaded solution).
- View the **consumer tree** (who depends on this class) and **dependency tree** (what this class depends on).
- Render the entire project's graph or every controller's graph at once.
- Click any node in a rendered graph to drill in and re-root.
- Side panel shows error reports per class:
  - Captive dependencies (singleton holding a transient, etc.)
  - Manual `container.Resolve` / `IServiceProvider.GetService` sites
  - Manual `Release`/`Dispose` of injected services
  - Async-unsafe `PerWebRequest`-through-transient chains

### Call-graph UI (`/Home/CallGraph`)

- Pick any controller action in the solution.
- See a left-to-right Graphviz graph rooted at that action, walking down through every callee until it terminates at a boundary or leaves source.
- Boundary leaves are color-coded by category:
  - **Red box** — external system gateway (HTTP, web service, message bus)
  - **Blue cylinder** — database query (repositories, DbContext, Dapper, SqlConnection)
  - **Purple hexagon** — mixed I/O (touches both)
- Edges are colored by what they lead to, so a path that ends in a DB query is blue all the way back to the controller, a path that ends in HTTP is red, a path that fans into both is purple.
- Loop edges and conditional edges (`if`/`else`/`?:`) are annotated with their kind, the truncated condition text, and line number.
- **Click a node to focus** — the graph prunes to that node's ancestors + the node + its descendants, keeping the upstream caller chain so you can see how a hotspot is reached.
- **Interface impl picker** (right panel) — when an interface dispatches to multiple in-source impls, toggle which ones the graph fans out to and re-render.
- **Boundary call counts** (right panel) — path-aware polynomial in N for each boundary, computed from a Kahn's-algorithm DAG DP over the rendered graph. A boundary called once at the top and once inside a loop shows up as `N+1`; a boundary in a doubly-nested loop shows up as `N²`. Severity-colored.
- **Expand past boundaries** toggle — keeps walking into a boundary's body instead of stopping at it, useful for understanding what the boundary itself does.

---

## Repo layout

```
DependencyAnalyzer/                    # .NET console app — DI analyses (text reports + DOT)
DependencyAnalyzer.Service/            # Windows service host (long-running periodic analysis)
GatewayCallGraph/                      # The call-graph engine (Roslyn down-walker, boundary inference, polynomial DP, Graphviz renderer). Includes a CLI for batch graph generation.
MVCWebView/                            # ASP.NET Core MVC — both UIs live here
RandomCodeAnalysis/                    # Roslyn-level analyzers shared by the above (CallChainAnalyzer, MethodReferenceCache, manual-resolution parsers)
RoslynGraphUi.Shared/                  # Razor Class Library — base layout, pan/zoom + history + click-to-drill JS, loading overlay. Reused by both UIs.
audit/                                 # Gitignored — local audit findings
output/                                # Gitignored — text/SVG output from the CLI tools
RoslynAnalysisTool.sln                 # Solution
run-service.cmd                        # Runs DependencyAnalyzer.Service against the configured solution
```

### Key types

- `DependencyAnalyzer.Parsers.DependencyAnalyzer` — reads Windsor + M.DI registrations, produces a `DependencyGraph`.
- `DependencyAnalyzer.Parsers.ManualResolutionParser` — finds every manual `Resolve`/`GetService` and `Release`/`Dispose` site.
- `GatewayCallGraph.ControllerEnumerator` — lists every MVC/API action across the loaded solution.
- `GatewayCallGraph.ControllerGraphBuilder` — the down-walker; resolves every `InvocationExpressionSyntax` in a method body, recurses through callees, fans out to interface impls and virtual overrides, and tags boundaries.
- `GatewayCallGraph.BoundaryInferrer` — taint propagation from framework I/O primitives (`HttpClient`, `SqlConnection`, EF, Dapper, etc.) up to the nearest "intent surface" (e.g. tag `IPaymentGateway.Charge`, not `HttpClient.SendAsync`).
- `GatewayCallGraph.BoundarySeeds` — hand-curated exact-match boundary list (loaded from `boundary-seeds.local.json`, gitignored) plus regex patterns (e.g. `.*Repository`).
- `GatewayCallGraph.LoopDetector` / `ConditionalDetector` — inspect the syntax tree around each call site for enclosing loops and `if`/`?:` conditions.
- `GatewayCallGraph.BoundaryCallCount` — DAG DP that computes, for each boundary, a polynomial in N where coefficients reflect how many call paths reach it at each loop-nesting depth.
- `GatewayCallGraph.CallGraphFocus` — render-time prune to ancestors+focus+descendants when the user clicks a node.
- `GatewayCallGraph.GraphvizRenderer` — shells out to `dot -Tsvg`, tags nodes with `xlink:title` so the front-end can read the FQN on click.

---

## Getting Started

### Prerequisites

- **.NET 8 SDK**
- **[Graphviz](https://graphviz.org/)** — the web app shells out to `dot -Tsvg`. Make sure `dot` is on your PATH (Windows installer puts it in `C:\Program Files\Graphviz\bin`).

### Clone

```bash
git clone https://github.com/ChrisCanyon/DependencyAnalyzer.git
cd DependencyAnalyzer
```

### Configure

The solution path is read from configuration so it stays out of the repo. Create `MVCWebView/appsettings.local.json` (see `appsettings.local.example.json`):

```json
{
  "SolutionPath": "C:\\path\\to\\Your.sln"
}
```

Or set `LOCAL_SOLUTION_PATH` as an environment variable. Both the web app and the CLI tools read it.

### Optional: boundary seeds

The call-graph analyzer covers most cases automatically through:
- The taint-from-leaf inferrer (catches anything that ends up at HttpClient/SqlConnection/etc.)
- Regex patterns in code (`.*Repository` → database)

For interface methods you specifically want tagged regardless of their downstream shape (e.g. `IPaymentGateway.Charge`), copy `GatewayCallGraph/boundary-seeds.local.example.json` to `boundary-seeds.local.json` (gitignored) and add entries:

```json
{
  "external_system_gateway": [
    { "type": "MyCompany.Payments.IPaymentGateway", "method": "Charge" }
  ]
}
```

### Run the web app

```bash
cd MVCWebView
dotnet run
```

Wait for `Now listening on: https://localhost:7011` (large solutions take 30-90 seconds the first time — the workspace open and DI graph build dominate).

- **Dependency UI** — `https://localhost:7011/`
- **Call-graph UI** — `https://localhost:7011/Home/CallGraph`

### Run the CLI

The console `DependencyAnalyzer` runs the DI analyses and writes text/DOT reports:

```bash
dotnet run --project DependencyAnalyzer -- "C:\path\to\Your.sln" "C:\path\to\output"
# or with env vars:
$env:LOCAL_SOLUTION_PATH = "C:\path\to\Your.sln"
$env:LOCAL_OUTPUT_DIRECTORY = "C:\path\to\output"
dotnet run --project DependencyAnalyzer
```

Toggle which analyses run in `DependencyAnalyzer/Program.cs` (`runPerWebRequestAnalysis`, `runTransientAnalysis`, `runAsyncUnsafeAnalysis`).

The `GatewayCallGraph` console runs the call-graph engine in batch mode against every seeded boundary:

```bash
dotnet run --project GatewayCallGraph -- "C:\path\to\Your.sln" "out.json" "Charge"
# args: solutionPath, outputJson, optional method-name filter
```

---

## How the call-graph walker works

At a high level:

1. **Enumerate roots.** `ControllerEnumerator` lists every controller action in the solution.
2. **Walk down.** For each invocation in the action's body, `ControllerGraphBuilder` resolves the symbol, decides whether it's a routing node (interface with multiple impls — pass through) or a leaf (boundary), and recurses into in-source callees. Visited symbols are deduped.
3. **Classify boundaries** in priority order:
   - Hand-curated seed match (`BoundarySeeds.Match`)
   - Inferred surface (`BoundaryInferrer` — propagates taint from framework I/O primitives up to the nearest "intent layer" like a `*Gateway`, `*Service`, `*Client`)
   - Framework primitive leaf (HttpClient, SqlConnection, Dapper, EF, SmtpClient, etc.) when nothing better wraps it
4. **Tag dispatch.** Interface methods become routing-only nodes (untagged). Virtual class methods *are* concrete boundaries in their own right and also fan out to overrides — both base and derived can be reached at runtime, both get nodes, dispatch edges connect them.
5. **Annotate edges.** `LoopDetector` flags `for`/`foreach`/`while`/`do` and LINQ enumerable lambdas. `ConditionalDetector` flags enclosing `if`/`else`/`?:` and truncates the condition expression.
6. **Render.** `GraphvizRenderer` produces DOT and shells out to `dot -Tsvg`. Each node gets an `xlink:title` carrying its FQN so the front-end can read it on click.
7. **Cache.** Graphs and rendered SVGs are cached in `IMemoryCache` keyed on `(fqn, expandPastBoundaries, hiddenImpls, focus)`. A per-key `SemaphoreSlim` ensures parallel `Graph` + `GraphJson` requests for the same key share one build instead of racing on the inferrer's shared state.

### Boundary call counts (the polynomials)

For each boundary node the engine runs a Kahn's-algorithm DP over the DAG. Each edge contributes a multiplier (1 for plain, N for a single loop, N² for a doubly-nested loop). The result is a polynomial in N where coefficients tell you the structure of how the boundary is reached:

- `1` — called once on a single straight-line path
- `N` — called inside one loop
- `N + 2` — called once outside a loop and twice inside a loop  
- `N² + N` — called inside a loop, and inside another loop nested in a loop

The side panel sorts these alphabetically by FQN and color-codes by leading degree (yellow for N, orange for N², red for N³, hot-red for N⁴+) so the worst hotspots are obvious at a glance.

---

## Privacy / what's gitignored

Internal type names and solution paths are kept out of source control:

- `appsettings.local.json` — your solution path
- `boundary-seeds.local.json` — your codebase's specific boundary FQNs
- `audit/` — any audit findings you generate
- `output/` — CLI report output
- `*.local.json` files in general

Each has a committed `.example.json` showing the expected shape.

---

## Limitations

- The down-walker uses static dispatch — when a base class method calls an overridable method, we tag the base and fan out to overrides as siblings, but we don't model the runtime "base method actually calls override" pattern (would require whole-program devirtualization).
- BCL/`System.*`/`Microsoft.*`/`Newtonsoft.*` calls are filtered from the intent-surface fallback so e.g. `string.Substring` doesn't promote its containing class to a boundary.
- `dot -Tsvg` is invoked synchronously per request; very large graphs (hundreds of nodes) can take a couple seconds.
- No persistence between sessions — everything lives in `IMemoryCache` for the lifetime of the process.
