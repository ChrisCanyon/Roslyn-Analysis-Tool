using DependencyAnalyzer;

var classes = await SolutionAnalyzer.GetAllClassesInSolutionAsync("C:\\Path\\To\\Solution\\SolutionName.sln");


var emailHelper = classes.Where(x => x.Name == "EmailHelper");

var emailHelperDependencies = DependencyAnalyzer.DependencyAnalyzer.AnalyzeDependencies(emailHelper);

foreach (var classAndDependency in emailHelperDependencies)
{
    Console.WriteLine($"Dependencies for {classAndDependency.Class.Name}");
    foreach (var dependency in classAndDependency.DependsOn)
    {
        Console.WriteLine($"\tDependencies for {dependency.Name}");
    }
}






