using DependencyAnalyzer;
using Microsoft.CodeAnalysis.MSBuild;
using System.Diagnostics;

var stopwatch = Stopwatch.StartNew(); // Start the timer
string solutionPath = "C:\\TylerDev\\eagle-vitals\\eagle-vitals.sln";
//string solutionPath = "C:\\TylerDev\\onlineservices\\Source\\InSite.sln";
//string solutionPath = "C:\\TylerDev\\onlineservices\\Source\\Prepaid\\Prepaid.sln";
//string solutionPath = "C:\\TylerDev\\Capital\\Source\\Capital.sln";

using var workspace = MSBuildWorkspace.Create();
var s = await workspace.OpenSolutionAsync(solutionPath);
var solutionAnalyzer = await SolutionAnalyzer.Build(s);
var dependencyAnalyzer = new DependencyAnalyzer.DependencyAnalyzer(solutionAnalyzer);
var graph = dependencyAnalyzer.BuildFullDependencyGraph();

stopwatch.Stop(); // Stop the timer
Console.WriteLine($"~~~ SETUP TIMER ~~~");
Console.WriteLine($"Elapsed time: {stopwatch.ElapsedMilliseconds} ms");
Console.WriteLine($"~~~ END TIMER ~~~");

//Do something here if you want to play around on the command line
