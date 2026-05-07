using DependencyAnalyzer;

ThreadPool.SetMinThreads(100, 100);
ThreadPool.SetMaxThreads(500, 500);

// Solution + output directory come from CLI args or env vars. Hardcoding them
// would leak the local repo layout, so we require configured values.
string solutionPath = args.Length > 0
    ? args[0]
    : Environment.GetEnvironmentVariable("LOCAL_SOLUTION_PATH")
      ?? throw new InvalidOperationException(
          "Solution path required. Pass it as the first CLI arg or set LOCAL_SOLUTION_PATH.");
string outputDirectory = args.Length > 1
    ? args[1]
    : Environment.GetEnvironmentVariable("LOCAL_OUTPUT_DIRECTORY")
      ?? Path.GetDirectoryName(solutionPath)
      ?? Directory.GetCurrentDirectory();

// Choose which analysis to run (can run multiple)
bool runPerWebRequestAnalysis = false;
bool runTransientAnalysis = false;
bool runAsyncUnsafeAnalysis = true; // New analyzer for async-unsafe patterns

if (runPerWebRequestAnalysis)
{
    await PerWebRequestManualResolutionRunner.Run(solutionPath, outputDirectory);
}

if (runTransientAnalysis)
{
    await TransientLeakRunner.Run(solutionPath, outputDirectory);
}

if (runAsyncUnsafeAnalysis)
{
    // Analyze async-unsafe manual resolution patterns (PerWebRequest through transient chains)
    // Optional: specify a project filter as third parameter
    await PerWebRequestAsyncUnsafeRunner.Run(solutionPath, outputDirectory);
}
