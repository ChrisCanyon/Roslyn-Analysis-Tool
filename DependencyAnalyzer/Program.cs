using DependencyAnalyzer;
using System.Diagnostics;

var stopwatch = Stopwatch.StartNew(); // Start the timer

var solutionAnalyzer = await SolutionAnalyzer.BuildSolutionAnalyzer("C:\\TylerDev\\onlineservices\\Source\\InSite.sln");

var dependencyAnalyzer = new DependencyAnalyzer.DependencyAnalyzer(solutionAnalyzer);
var graph = dependencyAnalyzer.BuildFullDependencyGraph();

stopwatch.Stop(); // Stop the timer
Console.WriteLine($"~~~ SETUP TIMER ~~~");
Console.WriteLine($"Elapsed time: {stopwatch.ElapsedMilliseconds} ms");
Console.WriteLine($"~~~ END TIMER ~~~");
