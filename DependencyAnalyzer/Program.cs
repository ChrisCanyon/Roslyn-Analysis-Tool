using DependencyAnalyzer;
using DependencyAnalyzer.RegistrationParsers;
using DependencyAnalyzer.Visualizers;
using System.Diagnostics;

var stopwatch = Stopwatch.StartNew(); // Start the timer

var solutionAnalyzer = await SolutionAnalyzer.BuildSolutionAnalyzer(
                            "C:\\TylerDev\\onlineservices\\Source\\InSite.sln",
                            new WindsorRegistrationParser() //Replace with implementation that can read your projects registration pattern
                            );

var dependencyAnalyzer = new DependencyAnalyzer.DependencyAnalyzer(solutionAnalyzer);
var graph = dependencyAnalyzer.BuildFullDependencyGraph();

stopwatch.Stop(); // Stop the timer
Console.WriteLine($"~~~ SETUP TIMER ~~~");
Console.WriteLine($"Elapsed time: {stopwatch.ElapsedMilliseconds} ms");
Console.WriteLine($"~~~ END TIMER ~~~");

var comparer = new FullyQualifiedNameComparer();

//var n = graph.Values.First(n => n.ClassName == "Core.InSite.SiteContext");
//var n = graph.Values.First(n => n.ClassName == "Core.Services.IEmailService");
//var n = graph.Values.First(n => n.ClassName == "InSite.Bll.AutoPayManager");
//var n = graph.Values.First(n => n.ClassName == "Core.Clients.IConduitConnectionFactory");
//var n = graph.Values.First(n => n.ClassName == "Infrastructure.Incode.InvisionGateway.UtilityBilling.AutopayBillRefresher");
var n = graph.Values.First(n => n.ClassName == "InSiteMVC.Areas.EasyPay.Managers.FormsManager");

foreach (var entry in n.RegistrationInfo.Take(1))
{
    NodePrinter.PrintConsumerTreeForProject(n, entry.Value.ProjectName).Write();
    NodePrinter.PrintDependencyTreeForProject(n, entry.Value.ProjectName).Write();
    GraphvizConverter.CreateConsumerGraphvizForProject(n, entry.Value.ProjectName);
    GraphvizConverter.CreateDependencyGraphvizForProject(n, entry.Value.ProjectName);
    GraphvizConverter.CreateGraphvizForProjectNode(n, entry.Value.ProjectName);
    GraphvizConverter.CreateFullGraphvizForProject(graph, entry.Value.ProjectName);
    GraphvizConverter.CreateControllerGraphvizForProject(graph, entry.Value.ProjectName);
}
//Console.WriteLine(n.PrintRegistrations());

var reportRunner = new ErrorReportRunner(graph);
var projectIssues = reportRunner.FindLifetimeMismatches();

var path = Directory.GetParent(Directory.GetCurrentDirectory()).Parent.Parent.Parent.FullName;
path = Path.Combine(path, "output");

File.WriteAllText(Path.Combine(path, "Lifetime-Issues.txt"), projectIssues);