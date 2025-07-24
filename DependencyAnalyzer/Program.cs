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


var ApiTPRegistrations = solutionAnalyzer.RegistrationInfos.Where(x => x.ProjectName.Contains("Api.TylerPayments"));







//Check for self references

var comparer = new FullyQualifiedNameComparer();

//Check for unregistered dependencies
foreach(var node in graph.Values)
{
    foreach (var registration in node.RegistrationInfo)
    {
        foreach(var dependency in node.DependsOn)
        {
            if (!dependency.RegistrationInfo.ContainsKey(registration.Key))
            {
                //if the depedency is an implementation
                if(node.Implements.Any(x => comparer.Equals(dependency.Class, x.Class))){
                    Console.WriteLine($"~~~ Circular Dependency ~~~");
                    Console.WriteLine($"Class: {node.ClassName}");
                    Console.WriteLine($"Relies on: {dependency.ClassName}");
                    Console.WriteLine($"For project {registration.Key}");
                    Console.WriteLine($"~~~ END ~~~");
                }
                Console.WriteLine($"~~~ Dependency Unregistered Error ~~~");
                Console.WriteLine($"Class: {node.ClassName}");
                Console.WriteLine($"Relies on: {dependency.ClassName}");
                Console.WriteLine($"For project {registration.Key}");
                Console.WriteLine($"but {dependency.ClassName} was not registered in that project");
                Console.WriteLine($"~~~ END ~~~");
            }
        }
    }
}





stopwatch.Stop(); // Stop the timer
Console.WriteLine($"~~~ TIMER ~~~");
Console.WriteLine($"Elapsed time: {stopwatch.ElapsedMilliseconds} ms");
Console.WriteLine($"~~~ TIMER ~~~");

var n = graph.Values.FirstOrDefault(n => n.ClassName == "Infrastructure.InSite.LinqToSql.ECommerce.BillRepository");
Console.WriteLine(n.PrintRegistrations());

File.WriteAllText("C:\\Users\\christopher.nagy\\workspace\\DependencyAnalyzer\\dependency-output.txt", n.PrintDependencyTree());
File.WriteAllText("C:\\Users\\christopher.nagy\\workspace\\DependencyAnalyzer\\usedBy-output.txt", n.PrintConsumerTree());
