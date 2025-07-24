using DependencyAnalyzer;
using System.Diagnostics;

var stopwatch = Stopwatch.StartNew(); // Start the timer

var solutionAnalyzer = await SolutionAnalyzer.BuildSolutionAnalyzer(
                            "C:\\TylerDev\\onlineservices\\Source\\InSite.sln",
                            new RegistrationHelper() //Replace with implementation that can read your projects registration pattern
                            );

var dependencyAnalyzer = new DependencyAnalyzer.DependencyAnalyzer(solutionAnalyzer);
var graph = dependencyAnalyzer.BuildFullDependencyGraph();

stopwatch.Stop(); // Stop the timer
Console.WriteLine($"~~~ SETUP TIMER ~~~");
Console.WriteLine($"Elapsed time: {stopwatch.ElapsedMilliseconds} ms");
Console.WriteLine($"~~~ END TIMER ~~~");

var comparer = new FullyQualifiedNameComparer();

//var n = graph.Values.First(n => n.ClassName == "Core.InSite.SiteContext");
var n = graph.Values.First(n => n.ClassName == "Core.Services.IEmailService");

foreach (var entry in n.RegistrationInfo)
{
    var sb = NodePrinter.PrintConsumerTreeForProject(n, entry.Value.ProjectName);
    sb.Write();
}
//Console.WriteLine(n.PrintRegistrations());

var reportRunner = new ErrorReportRunner(graph);
var projectIssues = reportRunner.FindLifetimeMismatches();

var path = Directory.GetParent(Directory.GetCurrentDirectory()).Parent.Parent.Parent.FullName;
path = Path.Combine(path, "output");

var test = Path.Combine(path, "dependency-output.txt");

File.WriteAllText(Path.Combine(path, "dependency-output.txt"), NodePrinter.PrintDependencyTree(n));
File.WriteAllText(Path.Combine(path, "usedBy-output.txt"), NodePrinter.PrintConsumerTree(n));
File.WriteAllText(Path.Combine(path, "Lifetime-Issues.txt"), projectIssues);