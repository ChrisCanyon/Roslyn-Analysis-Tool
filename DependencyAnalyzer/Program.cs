using DependencyAnalyzer;

var types = await SolutionAnalyzer.GetAllTypesInSolutionAsync("C:\\TylerDev\\onlineservices\\Source\\InSite.sln");

// 2. Build dependency map
var dependencyMap = DependencyAnalyzer.DependencyAnalyzer.GetClassDependencies(types);

// 3. Build full graph
var graph = DependencyAnalyzer.DependencyAnalyzer.BuildFullDependencyGraph(dependencyMap, types);

// 4. Find a root class you're interested in (by class name, namespace, etc.)
var rootNode = graph.Values.FirstOrDefault(n => n.ClassName == "Emails.InSite.Helpers.AutoPayEmailHelper");

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


















