using DependencyAnalyzer.Parsers;
using DependencyAnalyzer.Models;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using System.Diagnostics;

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

//Do something here if you want to play around on the command line
