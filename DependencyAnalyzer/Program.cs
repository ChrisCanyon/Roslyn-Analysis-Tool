using DependencyAnalyzer;
using Microsoft.Build.Locator;



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

var solutionAnalyzer = await SolutionAnalyzer.BuildSolutionAnalyzer("C:\\TylerDev\\onlineservices\\Source\\InSite.sln");

// 2. Build dependency map
var dependencyMap = DependencyAnalyzer.DependencyAnalyzer.GetClassDependencies(solutionAnalyzer);

// 3. Build full graph
var graph = DependencyAnalyzer.DependencyAnalyzer.BuildFullDependencyGraph(dependencyMap, solutionAnalyzer);

// 4. Find a root class you're interested in (by class name, namespace, etc.)

//normal registration
Console.WriteLine("One off registration");
var normalRegistration = graph.Values.FirstOrDefault(n => n.ClassName == "Core.InSite.SiteContext");
Console.WriteLine(normalRegistration.PrintRegistrations());


//standard install registration
Console.WriteLine("standard install registration");
var installerRegi = graph.Values.FirstOrDefault(n => n.ClassName == "Infrastructure.InSite.LinqToSql.ECommerce.vXTaxPaymentDetailsRepository");
Console.WriteLine(installerRegi.PrintRegistrations());

/*

if (rootNode is not null)
{
    // 5. Print the dependency tree
    File.WriteAllText("C:\\Users\\christopher.nagy\\workspace\\DependencyAnalyzer\\dependency-output.txt", rootNode.PrintDependencyTree());
    File.WriteAllText("C:\\Users\\christopher.nagy\\workspace\\DependencyAnalyzer\\usedBy-output.txt", rootNode.PrintConsumerTree());
}
else
{
    Console.WriteLine("Target class not found.");
}

*/