using DependencyAnalyzer;

ThreadPool.SetMinThreads(100, 100);
ThreadPool.SetMaxThreads(500, 500);

string solutionPath = "C:\\Path\\To\\Solution.sln";
string outputDirectory = "C:\\Path\\To\\Output";

// Choose which analysis to run (can run both)
bool runPerWebRequestAnalysis = false;
bool runTransientAnalysis = true;

if (runPerWebRequestAnalysis)
{
    await PerWebRequestAnalysisRunner.Run(solutionPath, outputDirectory);
}

if (runTransientAnalysis)
{
    await TransientManualResolutionRunner.Run(solutionPath, outputDirectory);
}
