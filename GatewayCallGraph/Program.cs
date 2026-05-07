using GatewayCallGraph;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using RandomCodeAnalysis.Analyzers;
using System.Diagnostics;

const string DefaultOutputPath = @"gateway-call-graph.json";

// Solution path comes from arg[0] or LOCAL_SOLUTION_PATH env var. Hardcoding
// it would leak the local repo layout, so we require a configured value.
var solutionPath = args.Length > 0
    ? args[0]
    : Environment.GetEnvironmentVariable("LOCAL_SOLUTION_PATH")
      ?? throw new InvalidOperationException(
          "Solution path required. Pass it as the first CLI argument or set the " +
          "LOCAL_SOLUTION_PATH environment variable.");
var outputPath = args.Length > 1 ? args[1] : DefaultOutputPath;
var seedFilter = args.Length > 2 ? args[2] : null; // optional: only run seeds whose method name contains this

// Walk every member across every category. Each member is a (type, method, category) tuple
// flattened from BoundarySeeds.All so the CLI can also be filtered by method name.
var allSeeds = BoundarySeeds.All.SelectMany(c => c.Members.Select(m => (m.TypeFullName, m.MethodName))).ToArray();
var selectedSeeds = seedFilter == null
    ? allSeeds
    : allSeeds.Where(s => s.MethodName.Contains(seedFilter, StringComparison.OrdinalIgnoreCase)).ToArray();

Console.WriteLine($"[GatewayCallGraph] Solution:    {solutionPath}");
Console.WriteLine($"[GatewayCallGraph] Output:      {outputPath}");
Console.WriteLine($"[GatewayCallGraph] Seed filter: {seedFilter ?? "(none)"}");
Console.WriteLine($"[GatewayCallGraph] Seeds:       {selectedSeeds.Length} of {allSeeds.Length}");

if (selectedSeeds.Length == 0)
{
    Console.WriteLine("[GatewayCallGraph] No boundary seeds matched. Populate BoundarySeeds.All or relax the filter.");
    return;
}

ThreadPool.SetMinThreads(Environment.ProcessorCount * 2, Environment.ProcessorCount * 2);
MSBuildLocator.RegisterDefaults();

Console.WriteLine($"[GatewayCallGraph] Opening workspace...");
var stopwatch = Stopwatch.StartNew();
using var workspace = MSBuildWorkspace.Create();
var solution = await workspace.OpenSolutionAsync(solutionPath);
stopwatch.Stop();
Console.WriteLine($"[GatewayCallGraph] Workspace open in {stopwatch.ElapsedMilliseconds} ms ({solution.Projects.Count()} projects)");

var analyzer = new CallChainAnalyzer(solution);
var graph = new CallGraph();
var builder = new GraphBuilder(solution, graph);

foreach (var (typeFullName, methodName) in selectedSeeds)
{
    Console.WriteLine($"[GatewayCallGraph] Seed: {typeFullName}.{methodName}");
    try
    {
        var rootNode = await analyzer.FindFullMethodChain(typeFullName, methodName, shallow: false);
        await builder.AddTreeAsync(rootNode);
        Console.WriteLine($"[GatewayCallGraph]   -> graph now {graph.Nodes.Count} nodes / {graph.Edges.Count} edges");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[GatewayCallGraph]   !! {ex.GetType().Name}: {ex.Message}");
    }
}

Console.WriteLine($"[GatewayCallGraph] Writing graph: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges -> {outputPath}");
GraphJsonWriter.Write(outputPath, graph);
Console.WriteLine($"[GatewayCallGraph] Done.");
