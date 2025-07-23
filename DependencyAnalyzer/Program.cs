using DependencyAnalyzer;
using System.Diagnostics;

/*
 * Infrastructure.InSite.LinqToSql.StandardRepositoryRegistrar
 * Api.TylerPayments.ApiTPRegistrar
 * InSite.Backstage.WindsorInstaller
 * InSiteMVC.Registrar
 * Infrastructure.Api.InSite.Registration.InSiteApiRegistrar
 * Infrastructure.InSite.LinqToSql.StandardRepositoryRegistrar
 * InSite.Bll.StandardAutoPayRegistrar
 */
/*
var fileToUpdate = new List<string> { 
    "Infrastructure.InSite.LinqToSql.StandardRepositoryRegistrar",
    "Api.TylerPayments.ApiTPRegistrar",
    "InSite.Backstage.WindsorInstaller",
    "InSiteMVC.Registrar",
    "Infrastructure.Api.InSite.Registration.InSiteApiRegistrar",
    "Infrastructure.InSite.LinqToSql.StandardRepositoryRegistrar",
    "InSite.Bll.StandardAutoPayRegistrar"
};

MSBuildLocator.RegisterDefaults();

foreach (var file in fileToUpdate)
{
    await rewriter.RewriteRegisterForAsync(
        "C:\\TylerDev\\onlineservices\\Source\\InSite.sln", 
        file
    );
}
*/

var stopwatch = Stopwatch.StartNew(); // Start the timer




var solutionAnalyzer = await SolutionAnalyzer.BuildSolutionAnalyzer("C:\\TylerDev\\onlineservices\\Source\\InSite.sln");
var dependencyMap = DependencyAnalyzer.DependencyAnalyzer.GetClassDependencies(solutionAnalyzer);
var graph = DependencyAnalyzer.DependencyAnalyzer.BuildFullDependencyGraph(dependencyMap, solutionAnalyzer);


stopwatch.Stop(); // Stop the timer
Console.WriteLine($"~~~ TIMER ~~~");
Console.WriteLine($"Elapsed time: {stopwatch.ElapsedMilliseconds} ms");
Console.WriteLine($"~~~ TIMER ~~~");

Console.WriteLine("standard install registration");
var node = graph.Values.FirstOrDefault(n => n.ClassName == "Infrastructure.InSite.LinqToSql.ECommerce.BillRepository");
Console.WriteLine(node.PrintRegistrations());

File.WriteAllText("C:\\Users\\christopher.nagy\\workspace\\DependencyAnalyzer\\dependency-output.txt", node.PrintDependencyTree());
File.WriteAllText("C:\\Users\\christopher.nagy\\workspace\\DependencyAnalyzer\\usedBy-output.txt", node.PrintConsumerTree());
