using DependencyAnalyzer;
using DependencyAnalyzer.Parsers;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews()
    .AddRazorRuntimeCompilation();

MSBuildLocator.RegisterDefaults();
using var workspace = MSBuildWorkspace.Create();

string solutionPath = "C:\\PathToYour\\Solution.sln";

//Generate full dependency graph for project and register as single to cache it
var stopwatch = Stopwatch.StartNew();
var s = await workspace.OpenSolutionAsync(solutionPath);
stopwatch.Stop(); 
Console.WriteLine($"~~~ Workspace open ~~~");
Console.WriteLine($"\tElapsed time: {stopwatch.ElapsedMilliseconds} ms");

stopwatch.Restart();
SolutionAnalyzer solutionAnalyzer = await SolutionAnalyzer.Build(s);
stopwatch.Stop();
Console.WriteLine($"~~~ SolutionAnalyzer build ~~~");
Console.WriteLine($"\tElapsed time: {stopwatch.ElapsedMilliseconds} ms");

stopwatch.Restart();
ManualResolutionParser manParse = new ManualResolutionParser(s, solutionAnalyzer);
await manParse.Build();
stopwatch.Stop(); 
Console.WriteLine($"~~~ ManualResolutionParser build ~~~");
Console.WriteLine($"\tElapsed time: {stopwatch.ElapsedMilliseconds} ms");
stopwatch.Restart();

DependencyAnalyzer.DependencyAnalyzer dependencyAnalyzer = new DependencyAnalyzer.DependencyAnalyzer(solutionAnalyzer);
DependencyGraph graph = dependencyAnalyzer.BuildFullDependencyGraph();
stopwatch.Stop(); 
Console.WriteLine($"~~~ graph build ~~~");
Console.WriteLine($"\tElapsed time: {stopwatch.ElapsedMilliseconds} ms");

builder.Services.AddSingleton(solutionAnalyzer);
builder.Services.AddSingleton(dependencyAnalyzer);
builder.Services.AddSingleton(graph);
builder.Services.AddSingleton(s);
builder.Services.AddScoped<ErrorReportRunner>();
builder.Services.AddSingleton(manParse);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
