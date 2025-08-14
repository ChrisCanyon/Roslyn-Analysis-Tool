using DependencyAnalyzer;
using DependencyAnalyzer.Parsers;
using DependencyAnalyzer.Visualizers;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using System.Diagnostics;

MSBuildLocator.RegisterDefaults();
using var workspace = MSBuildWorkspace.Create();

string solutionPath = "C:\\TylerDev\\onlineservices\\Source\\InSite.sln";

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

DependencyAnalyzer.DependencyAnalyzer dependencyAnalyzer = new DependencyAnalyzer.DependencyAnalyzer(solutionAnalyzer, manParse);
DependencyGraph graph = dependencyAnalyzer.BuildFullDependencyGraph();
stopwatch.Stop();
Console.WriteLine($"~~~ graph build ~~~");
Console.WriteLine($"\tElapsed time: {stopwatch.ElapsedMilliseconds} ms");

var billRefreshNodes = graph.Nodes.Where(x => x.ClassName == "Core.InSite.ECommerce.Bills.BillRefresher").First();
var TPHandlerResolverMVC = graph.Nodes.Where(x => x.ClassName == "InSite.Bll.TylerPayments.Resolvers.TylerPaymentsHandlerResolver").First();
var BuildingProjectsHandlerNodes = graph.Nodes.Where(x => x.ClassName == "InSite.Bll.TylerPayments.Handlers.BuildingProjectsHandler");

GraphvizConverter.CreateFullGraphvizForProject(graph, "InSiteMVC", false);

//Do something here if you want to play around on the command line
var x = 1;