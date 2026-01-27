using DependencyAnalyzer.Parsers;
using DependencyAnalyzer.Models;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using System.Diagnostics;
using RandomCodeAnalysis.Analyzers;
using RandomCodeAnalysis;
using Microsoft.CodeAnalysis;
using RandomCodeAnalysis.Models.MethodChain;

ThreadPool.SetMinThreads(100, 100);
ThreadPool.SetMaxThreads(500, 500);

MSBuildLocator.RegisterDefaults();
using var workspace = MSBuildWorkspace.Create();

string solutionPath = "C:\\PathToSln.sln";

//Generate full dependency graph for project and register as single to cache it
var stopwatch = Stopwatch.StartNew();
var s = await workspace.OpenSolutionAsync(solutionPath);
stopwatch.Stop();
Console.WriteLine($"~~~ Workspace open ~~~");
Console.WriteLine($"\tElapsed time: {stopwatch.ElapsedMilliseconds} ms");

stopwatch.Restart();


var fullyQualifiedTypeName = "Fully.Qualified.Class.Name";
var methodName = "MethodName";

var analyzer = new CallChainAnalyzer(s);
var topNode = await analyzer.FindFullMethodChain(fullyQualifiedTypeName, methodName, false);
stopwatch.Stop();
Console.WriteLine($"~~~ Top Node Mapping ~~~");
Console.WriteLine($"\tElapsed time: {stopwatch.ElapsedMilliseconds} ms ({stopwatch.Elapsed.TotalMinutes:F2} minutes)");
var reporter = new CallChainReporter();
return;

await reporter.GenerateAsyncConversionReport(topNode, s);

var subnodeTasks = topNode.CallerNodes.Select(callerNode => Task.Run(async () =>
{
    var method = callerNode.ReferencedMethod; // IMethodSymbol

    var fullyQualifiedTypeNameLocal = method.ContainingType
        .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
        .Replace("global::", "");

    var methodNameLocal = method.Name;

    Console.WriteLine($"~~~ Start: {fullyQualifiedTypeNameLocal} {methodNameLocal} Analysis ~~~");

    var localStopwatch = Stopwatch.StartNew();
    var result = await analyzer.FindFullMethodChain(fullyQualifiedTypeNameLocal, methodNameLocal, false);

    
    await reporter.GenerateAsyncConversionReport(result, s);

    localStopwatch.Stop();
    Console.WriteLine($"~~~ {fullyQualifiedTypeNameLocal} {methodNameLocal} Analysis Sub Node Mapping ~~~\n\tElapsed time: {localStopwatch.ElapsedMilliseconds} ms ({localStopwatch.Elapsed.TotalMinutes:F2} minutes)");

    return result;
})).ToList();

await Task.WhenAll(subnodeTasks);


/*
stopwatch.Restart();
SolutionAnalyzer solutionAnalyzer = await SolutionAnalyzer.Build(s);
stopwatch.Stop();
Console.WriteLine($"~~~ SolutionAnalyzer build ~~~");
Console.WriteLine($"\tElapsed time: {stopwatch.ElapsedMilliseconds} ms");

stopwatch.Restart();
ManualResolutionParser manParse = new ManualResolutionParser(s, solutionAnalyzer);
await manParse.Build();
stopwatch.Stop();
Console.WriteLine($"~~~ ManualResolutionParser build ~~~");
Console.WriteLine($"\tElapsed time: {stopwatch.ElapsedMilliseconds} ms");
stopwatch.Restart();

DependencyAnalyzer.Parsers.DependencyAnalyzer dependencyAnalyzer = new DependencyAnalyzer.Parsers.DependencyAnalyzer(solutionAnalyzer, manParse);
DependencyGraph graph = dependencyAnalyzer.BuildFullDependencyGraph();
stopwatch.Stop();
Console.WriteLine($"~~~ graph build ~~~");
Console.WriteLine($"\tElapsed time: {stopwatch.ElapsedMilliseconds} ms");
*/

//Do something here if you want to play around on the command line
