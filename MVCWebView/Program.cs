using DependencyAnalyzer;
using DependencyAnalyzer.Models;
using DependencyAnalyzer.Parsers;
using GatewayCallGraph;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using System.Diagnostics;

static void WriteGreenText(string text)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine(text);
    Console.ResetColor();
}

WriteGreenText("       __             ___\r\n" +
    "      // )    ___--\"\"    \"-.\r\n" +
    " \\ |,\"( /`--\"\"              `.                   \r\n" +
    "  \\/ o                        \\\r\n" +
    "  (   _.-.              ,'\"    ;  \r\n" +
    "   |\\\"   /`. \\  ,      /       |\r\n" +
    "   | \\  ' .'`.; |      |       \\.______________________________\r\n" +
    "     _-'.'    | |--..,,,\\_    \\________------------\"\"\"\"\"\"\"\"\"\"\"\"\r\n" +
    "    '''\"   _-'.'       ___\"-   )\r\n" +
    "          '''\"        '''---~\"\"");
WriteGreenText($"~~~ Building RAT ~~~");

var builder = WebApplication.CreateBuilder(args);

// Pull SolutionPath from appsettings.local.json (gitignored), the LOCAL_SOLUTION_PATH
// env var, or appsettings.{Environment}.json — in that order. Throw a clear error
// if none are set so the developer knows where to put their path.
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
string solutionPath = builder.Configuration["SolutionPath"]
    ?? Environment.GetEnvironmentVariable("LOCAL_SOLUTION_PATH")
    ?? throw new InvalidOperationException(
        "SolutionPath is not configured. Create MVCWebView/appsettings.local.json with " +
        "{ \"SolutionPath\": \"C:\\\\path\\\\to\\\\Your.sln\" } or set LOCAL_SOLUTION_PATH. " +
        "See MVCWebView/appsettings.local.example.json for the expected shape.");

// Add services to the container.
builder.Services.AddControllersWithViews()
    .AddRazorRuntimeCompilation()
    .AddJsonOptions(opts =>
    {
        // Emit enum values (NodeKind, LoopKind, ...) as snake_lower strings so
        // the front-end can compare against e.g. "gateway_method" without caring
        // about underlying ordinals.
        opts.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter(
                System.Text.Json.JsonNamingPolicy.SnakeCaseLower));
    });

ThreadPool.SetMinThreads(Environment.ProcessorCount * 2, Environment.ProcessorCount * 2);

MSBuildLocator.RegisterDefaults();
WriteGreenText($"~~~ Opening Workspace ~~~");
using var workspace = MSBuildWorkspace.Create();

//Generate full dependency graph for project and register as single to cache it
var stopwatch = Stopwatch.StartNew();
var s = await workspace.OpenSolutionAsync(solutionPath);
stopwatch.Stop(); 
WriteGreenText($"~~~ Workspace open ~~~");
WriteGreenText($"\tElapsed time: {stopwatch.ElapsedMilliseconds} ms");

stopwatch.Restart();
SolutionAnalyzer solutionAnalyzer = await SolutionAnalyzer.Build(s);
stopwatch.Stop();
WriteGreenText($"~~~ SolutionAnalyzer build ~~~");
WriteGreenText($"\tElapsed time: {stopwatch.ElapsedMilliseconds} ms");
stopwatch.Restart();
ManualResolutionParser manParse = new ManualResolutionParser(s, solutionAnalyzer);
await manParse.Build();
stopwatch.Stop(); 
WriteGreenText($"~~~ ManualResolutionParser build ~~~");
WriteGreenText($"\tElapsed time: {stopwatch.ElapsedMilliseconds} ms");
stopwatch.Restart();

DependencyAnalyzer.Parsers.DependencyAnalyzer dependencyAnalyzer = new DependencyAnalyzer.Parsers.DependencyAnalyzer(solutionAnalyzer, manParse);
DependencyGraph graph = dependencyAnalyzer.BuildFullDependencyGraph();
stopwatch.Stop(); 
WriteGreenText($"~~~ graph build ~~~");
WriteGreenText($"\tElapsed time: {stopwatch.ElapsedMilliseconds} ms");

WriteGreenText(
    $"           _   \r\n" +
    $"          | |  \r\n" +
    $" _ __ __ _| |_ \r\n" +
    $"| '__/ _` | __|\r\n" +
    $"| | | (_| | |_ \r\n" +
    $"|_|  \\__,_|\\__|\n");

WriteGreenText(
    $"                (\\\r\n" +
    $"                 \\\\\r\n" +
    $"                  ))\r\n" +
    $"                 //\r\n" +
    $"          .-.   //  .-.\r\n" +
    $"         /   \\-((=-/   \\\r\n" +
    $"         \\      \\\\     /\r\n" +
    $"          `( ____))_ )`\r\n" +
    $"          .-'   //  '-.\r\n" +
    $"         /     ((      \\\r\n" +
    $"        |       *       |\r\n" +
    $"         \\             /\r\n" +
    $"          \\   |_w_|   /\r\n" +
    $"          _)  \\ ` /  (_\r\n" +
    $"        (((---'   '---)))\n");

WriteGreenText(
    $"                        ,d     \r\n" +
    $"                        88     \r\n" +
    $"8b,dPPYba, ,adPPYYba, MM88MMM  \r\n" +
    $"88P'   \"Y8 \"\"     `Y8   88     \r\n" +
    $"88         ,adPPPPP88   88     \r\n" +
    $"88         88,    ,88   88,    \r\n" +
    $"88         `\"8bbdP\"Y8   \"Y888  \n");

builder.Services.AddSingleton(solutionAnalyzer);
builder.Services.AddSingleton(dependencyAnalyzer);
builder.Services.AddSingleton(graph);
builder.Services.AddSingleton(s);
builder.Services.AddScoped<ErrorReportGenerator>();
builder.Services.AddSingleton(manParse);

// Gateway-call-graph services (separate UI surface; share the loaded Solution).
builder.Services.AddSingleton(new ControllerGraphBuilder(s));
builder.Services.AddSingleton(new ControllerEnumerator(s));
builder.Services.AddMemoryCache();

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
