using DependencyAnalyzer;
using DependencyAnalyzer.Parsers;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews()
    .AddRazorRuntimeCompilation();

MSBuildLocator.RegisterDefaults();
using var workspace = MSBuildWorkspace.Create();

//string solutionPath = "C:\\TylerDev\\eagle-vitals\\eagle-vitals.sln";
string solutionPath = "C:\\TylerDev\\onlineservices\\Source\\InSite.sln";
//string solutionPath = "C:\\TylerDev\\onlineservices\\Source\\Prepaid\\Prepaid.sln";
//string solutionPath = "C:\\TylerDev\\Capital\\Source\\Capital.sln";

//Generate full dependency graph for project and register as single to cache it
var s = await workspace.OpenSolutionAsync(solutionPath);
ManualResolutionParser manParse = await ManualResolutionParser.Build(s);
SolutionAnalyzer solutionAnalyzer = await SolutionAnalyzer.Build(s);
DependencyAnalyzer.DependencyAnalyzer dependencyAnalyzer = new DependencyAnalyzer.DependencyAnalyzer(solutionAnalyzer);
DependencyGraph graph = dependencyAnalyzer.BuildFullDependencyGraph();

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
