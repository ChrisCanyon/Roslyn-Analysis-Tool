using DependencyAnalyzer;
using DependencyAnalyzer.RegistrationParsers;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews()
    .AddRazorRuntimeCompilation();

SolutionAnalyzer solutionAnalyzer = await SolutionAnalyzer.BuildSolutionAnalyzer(
                            "C:\\TylerDev\\onlineservices\\Source\\InSite.sln",
                            new WindsorRegistrationParser() //Replace with implementation that can read your projects registration pattern
                            );
DependencyAnalyzer.DependencyAnalyzer dependencyAnalyzer = new DependencyAnalyzer.DependencyAnalyzer(solutionAnalyzer);
DependencyGraph graph = dependencyAnalyzer.BuildFullDependencyGraph();
solutionAnalyzer = null;
dependencyAnalyzer = null;

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
//builder.Services.AddSingleton(solutionAnalyzer);
//builder.Services.AddSingleton(dependencyAnalyzer);
builder.Services.AddSingleton(graph);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
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
