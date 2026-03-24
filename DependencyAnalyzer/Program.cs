using DependencyAnalyzer;

ThreadPool.SetMinThreads(100, 100);
ThreadPool.SetMaxThreads(500, 500);

string solutionPath = "C:\\Path\\To\\Solution.sln";
string outputDirectory = "C:\\Path\\To\\Output";

await PerWebRequestAnalysisRunner.Run(solutionPath, outputDirectory);
