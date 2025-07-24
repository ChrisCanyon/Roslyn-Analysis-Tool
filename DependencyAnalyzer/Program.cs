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

var n = graph.Values.First(n => n.ClassName == "Core.InSite.SiteContext");

/*
foreach(var entry in n.RegistrationInfo)
{
    var registration = entry.Value;
    foreach (var dependentReference in n.DependedOnBy)
    {
        var registrationForProject = dependentReference.RegistrationInfo.Where(x => x.Value.ProjectName == registration.ProjectName);
        if(registrationForProject.Count() > 1)
        {
            Console.WriteLine($"Double register for class: {dependentReference.ClassName} in project: {registration.ProjectName}");
            continue;
        }
        if(registrationForProject.Count() == 0)
        {
            continue;
        }

        var dependantRegistration = registrationForProject.First().Value;
        if (dependantRegistration.RegistrationType > registration.RegistrationType)
        {
            Console.WriteLine($"LIFETIME VIOLATION");
            Console.WriteLine($"    Class: {dependentReference.ClassName} has lifetime of {dependantRegistration.RegistrationType}");
            Console.WriteLine($"    but references shorter lived class: {n.ClassName} with lifetime {registration.RegistrationType}");
            Console.WriteLine($"    in project: {registration.ProjectName}");
        }
    }
}
*/

foreach (var entry in n.RegistrationInfo)
{
    var sb = NodePrinter.PrintConsumerTreeForProject(n, entry.Value.ProjectName);
    sb.Write();
}
//Console.WriteLine(n.PrintRegistrations());

File.WriteAllText("C:\\Users\\christopher.nagy\\workspace\\DependencyAnalyzer\\dependency-output.txt", NodePrinter.PrintDependencyTree(n));
File.WriteAllText("C:\\Users\\christopher.nagy\\workspace\\DependencyAnalyzer\\usedBy-output.txt", NodePrinter.PrintConsumerTree(n));
