using Microsoft.AspNetCore.Mvc;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using DependencyAnalyzer.Parsers;
using DependencyAnalyzer.Models;
using DependencyAnalyzer.Extensions;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Register as singleton to maintain state
builder.Services.AddSingleton<AnalysisService>();
builder.Services.AddHostedService<AnalysisService>(provider => provider.GetRequiredService<AnalysisService>());

var app = builder.Build();

// Configure pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();

// API Endpoints
app.MapGet("/status", ([FromServices] AnalysisService service) =>
{
    return new
    {
        loaded = service.IsLoaded,
        loadTime = service.LoadTime,
        projectCount = service.ProjectCount,
        nodeCount = service.NodeCount,
        lastRefresh = service.LastRefresh,
        currentSolution = service.CurrentSolutionPath
    };
});

app.MapPost("/load", async ([FromBody] LoadRequest request, [FromServices] AnalysisService service) =>
{
    try
    {
        await service.LoadSolutionAsync(request.SolutionPath, request.ForceReload);
        return Results.Ok(new { success = true, message = "Solution loaded successfully" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, error = ex.Message });
    }
});

app.MapGet("/nodes", ([FromServices] AnalysisService service, [FromQuery] string? project) =>
{
    if (!service.IsLoaded)
        return Results.BadRequest("Solution not loaded");

    var nodes = service.GetNodes(project);
    return Results.Ok(nodes);
});

app.MapGet("/node/{className}", ([FromServices] AnalysisService service, string className, [FromQuery] string? project) =>
{
    if (!service.IsLoaded)
        return Results.BadRequest("Solution not loaded");

    var node = service.FindNode(className, project);
    if (node == null)
        return Results.NotFound($"Node {className} not found");

    return Results.Ok(new
    {
        node.ClassName,
        node.Lifetime,
        node.ProjectName,
        Dependencies = node.DependsOn.Select(d => d.ClassName),
        Consumers = node.DependedOnBy.Select(d => d.ClassName),
        RawDependencies = node.RawDependencies.Select(r => new { r.Type.Name })
    });
});

app.MapGet("/transient-leaks", ([FromServices] AnalysisService service, [FromQuery] string? project) =>
{
    if (!service.IsLoaded)
        return Results.BadRequest("Solution not loaded");

    var leaks = service.FindTransientLeaks(project);
    return Results.Ok(leaks);
});

app.MapGet("/captive-dependencies/{className}", ([FromServices] AnalysisService service, string className) =>
{
    if (!service.IsLoaded)
        return Results.BadRequest("Solution not loaded");

    var captives = service.FindCaptiveDependencies(className);
    return Results.Ok(captives);
});

app.MapPost("/query", ([FromBody] QueryRequest query, [FromServices] AnalysisService service) =>
{
    if (!service.IsLoaded)
        return Results.BadRequest("Solution not loaded");

    var result = service.ExecuteQuery(query);
    return Results.Ok(result);
});

app.MapGet("/manual-resolutions", ([FromServices] AnalysisService service, [FromQuery] string? project, [FromQuery] string? type) =>
{
    if (!service.IsLoaded)
        return Results.BadRequest("Solution not loaded");

    var resolutions = service.GetManualResolutions(project, type);
    return Results.Ok(resolutions);
});

app.MapPost("/refresh-file", async ([FromBody] RefreshFileRequest request, [FromServices] AnalysisService service) =>
{
    if (!service.IsLoaded)
        return Results.BadRequest("Solution not loaded");

    await service.RefreshFileAsync(request.FilePath);
    return Results.Ok(new { success = true, message = "File refreshed" });
});

app.MapGet("/search", ([FromServices] AnalysisService service, [FromQuery] string pattern, [FromQuery] string? searchType) =>
{
    if (!service.IsLoaded)
        return Results.BadRequest("Solution not loaded");

    var results = searchType?.ToLower() switch
    {
        "class" => service.SearchClasses(pattern),
        "method" => service.SearchMethods(pattern),
        "dependency" => service.SearchDependencies(pattern),
        _ => service.SearchAll(pattern)
    };

    return Results.Ok(results);
});

app.Run("http://localhost:5123");

// Request/Response Models
public record LoadRequest(string SolutionPath, bool ForceReload = false);
public record QueryRequest(string QueryType, Dictionary<string, object> Parameters);
public record RefreshFileRequest(string FilePath);

// The main analysis service that holds everything in memory
public class AnalysisService : BackgroundService
{
    private DependencyGraph? _dependencyGraph;
    private SolutionAnalyzer? _solutionAnalyzer;
    private ManualResolutionParser? _manualParser;
    private Microsoft.CodeAnalysis.Solution? _solution;
    private readonly ConcurrentDictionary<string, object> _cache = new();

    public bool IsLoaded => _dependencyGraph != null;
    public DateTime? LoadTime { get; private set; }
    public DateTime? LastRefresh { get; private set; }
    public string? CurrentSolutionPath { get; private set; }
    public int NodeCount => _dependencyGraph?.Nodes.Count ?? 0;
    public int ProjectCount => _solution?.Projects.Count() ?? 0;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Background service - could auto-load a default solution
        return Task.CompletedTask;
    }

    public async Task LoadSolutionAsync(string solutionPath, bool forceReload = false)
    {
        if (IsLoaded && !forceReload && CurrentSolutionPath == solutionPath)
        {
            Console.WriteLine("Solution already loaded, using cached data");
            return;
        }

        Console.WriteLine($"Loading solution: {solutionPath}");
        var startTime = DateTime.Now;

        // Register MSBuild
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();

        // Load solution
        var workspace = MSBuildWorkspace.Create();
        _solution = await workspace.OpenSolutionAsync(solutionPath);

        // Build analyzers
        Console.WriteLine("Building SolutionAnalyzer...");
        _solutionAnalyzer = await SolutionAnalyzer.Build(_solution);

        Console.WriteLine("Building ManualResolutionParser...");
        _manualParser = new ManualResolutionParser(_solution, _solutionAnalyzer);
        await _manualParser.Build(includeDisposalAnalysis: false); // Faster load

        Console.WriteLine("Building DependencyGraph...");
        var dependencyAnalyzer = new DependencyAnalyzer.Parsers.DependencyAnalyzer(_solutionAnalyzer, _manualParser);
        _dependencyGraph = dependencyAnalyzer.BuildFullDependencyGraph();

        CurrentSolutionPath = solutionPath;
        LoadTime = DateTime.Now;
        LastRefresh = DateTime.Now;

        var loadDuration = DateTime.Now - startTime;
        Console.WriteLine($"Solution loaded in {loadDuration.TotalSeconds:F1} seconds");
        Console.WriteLine($"Loaded {NodeCount} nodes from {ProjectCount} projects");

        // Clear cache on reload
        _cache.Clear();
    }

    public IEnumerable<DependencyNode> GetNodes(string? project = null)
    {
        if (_dependencyGraph == null) return Enumerable.Empty<DependencyNode>();

        var nodes = _dependencyGraph.Nodes;
        if (!string.IsNullOrEmpty(project))
            nodes = nodes.Where(n => n.ProjectName == project).ToList();

        return nodes;
    }

    public DependencyNode? FindNode(string className, string? project = null)
    {
        if (_dependencyGraph == null) return null;

        var query = _dependencyGraph.Nodes.Where(n =>
            string.Equals(n.ClassName, className, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(project))
            query = query.Where(n => n.ProjectName == project);

        return query.FirstOrDefault();
    }

    public IEnumerable<object> FindTransientLeaks(string? project = null)
    {
        if (_dependencyGraph == null || _manualParser == null)
            return Enumerable.Empty<object>();

        var cacheKey = $"transient-leaks-{project}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return (IEnumerable<object>)cached;

        var results = new List<object>();
        var resolutions = _manualParser.ManuallyResolvedSymbols ?? new List<ManualResolveInfo>();

        if (!string.IsNullOrEmpty(project))
            resolutions = resolutions.Where(r => r.Project == project).ToList();

        foreach (var resolution in resolutions)
        {
            var nodes = _dependencyGraph.Nodes.Where(n =>
                n.SatisfiesDependency(resolution.ResolvedType) &&
                (string.IsNullOrEmpty(project) || n.ProjectName == project));

            foreach (var node in nodes.Where(n => n.Lifetime == LifetimeTypes.Transient))
            {
                var disposals = _manualParser.ManuallyDisposedSymbols
                    ?.Where(d => node.SatisfiesDependency(d.DisposedType) && d.Project == node.ProjectName)
                    .ToList() ?? new List<ManualDisposeInfo>();

                results.Add(new
                {
                    Type = node.ClassName,
                    Project = node.ProjectName,
                    Lifetime = node.Lifetime.ToString(),
                    ResolutionPath = resolution.InvocationPath,
                    HasRelease = disposals.Any(),
                    ReleaseLocations = disposals.Select(d => new
                    {
                        d.FileAndLine,
                        d.CodeSnippet
                    })
                });
            }
        }

        _cache[cacheKey] = results;
        return results;
    }

    public IEnumerable<object> FindCaptiveDependencies(string className)
    {
        if (_dependencyGraph == null) return Enumerable.Empty<object>();

        var node = FindNode(className);
        if (node == null) return Enumerable.Empty<object>();

        var results = new List<object>();
        var visited = new HashSet<DependencyNode>();

        void FindLongerLived(DependencyNode target, DependencyNode current)
        {
            if (!visited.Add(current)) return;

            foreach (var consumer in current.DependedOnBy)
            {
                if (consumer.Lifetime > target.Lifetime)
                {
                    results.Add(new
                    {
                        Consumer = consumer.ClassName,
                        ConsumerLifetime = consumer.Lifetime.ToString(),
                        Target = target.ClassName,
                        TargetLifetime = target.Lifetime.ToString(),
                        Project = consumer.ProjectName
                    });
                }
                FindLongerLived(target, consumer);
            }
        }

        FindLongerLived(node, node);
        return results;
    }

    public object ExecuteQuery(QueryRequest query)
    {
        // Extensible query system
        return query.QueryType.ToLower() switch
        {
            "cycles" => FindCycles(query.Parameters),
            "stateful" => FindStatefulServices(query.Parameters),
            "excessive-deps" => FindExcessiveDependencies(query.Parameters),
            _ => new { error = $"Unknown query type: {query.QueryType}" }
        };
    }

    public IEnumerable<object> GetManualResolutions(string? project, string? type)
    {
        if (_manualParser == null) return Enumerable.Empty<object>();

        var resolutions = _manualParser.ManuallyResolvedSymbols ?? new List<ManualResolveInfo>();

        if (!string.IsNullOrEmpty(project))
            resolutions = resolutions.Where(r => r.Project == project).ToList();

        if (!string.IsNullOrEmpty(type))
            resolutions = resolutions.Where(r => r.ResolvedType.Name.Contains(type)).ToList();

        return resolutions.Select(r => new
        {
            Type = r.ResolvedType.ToDisplayString(),
            r.Project,
            r.InvocationPath
        });
    }

    public async Task RefreshFileAsync(string filePath)
    {
        // Incremental refresh for a single file
        // This would require partial reanalysis
        LastRefresh = DateTime.Now;
        _cache.Clear(); // Invalidate cache

        // TODO: Implement incremental analysis
        await Task.CompletedTask;
    }

    public IEnumerable<object> SearchClasses(string pattern)
    {
        if (_dependencyGraph == null) return Enumerable.Empty<object>();

        return _dependencyGraph.Nodes
            .Where(n => n.ClassName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .Select(n => new { n.ClassName, n.ProjectName, n.Lifetime });
    }

    public IEnumerable<object> SearchMethods(string pattern)
    {
        // Search in manual resolutions for method patterns
        if (_manualParser == null) return Enumerable.Empty<object>();

        return _manualParser.ManuallyResolvedSymbols
            .Where(r => r.InvocationPath.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .Select(r => new { Method = r.InvocationPath, r.Project });
    }

    public IEnumerable<object> SearchDependencies(string pattern)
    {
        if (_dependencyGraph == null) return Enumerable.Empty<object>();

        return _dependencyGraph.Nodes
            .Where(n => n.DependsOn.Any(d => d.ClassName.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            .Select(n => new
            {
                Class = n.ClassName,
                Dependencies = n.DependsOn
                    .Where(d => d.ClassName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    .Select(d => d.ClassName)
            });
    }

    public object SearchAll(string pattern)
    {
        return new
        {
            Classes = SearchClasses(pattern),
            Methods = SearchMethods(pattern),
            Dependencies = SearchDependencies(pattern)
        };
    }

    private object FindCycles(Dictionary<string, object> parameters)
    {
        // Implement cycle detection
        return new { cycles = new[] { "Not implemented yet" } };
    }

    private object FindStatefulServices(Dictionary<string, object> parameters)
    {
        // Implement stateful service detection
        return new { stateful = new[] { "Not implemented yet" } };
    }

    private object FindExcessiveDependencies(Dictionary<string, object> parameters)
    {
        var threshold = parameters.ContainsKey("threshold") ? Convert.ToInt32(parameters["threshold"]) : 10;

        if (_dependencyGraph == null) return new { nodes = Enumerable.Empty<object>() };

        var excessive = _dependencyGraph.Nodes
            .Where(n => n.DependsOn.Count > threshold)
            .Select(n => new
            {
                n.ClassName,
                DependencyCount = n.DependsOn.Count,
                n.ProjectName,
                Dependencies = n.DependsOn.Select(d => d.ClassName)
            })
            .OrderByDescending(n => n.DependencyCount);

        return new { nodes = excessive, threshold };
    }
}