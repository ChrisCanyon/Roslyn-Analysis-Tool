using DependencyAnalyzer.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace MVCWebView.Controllers
{
    public class HomeController : Controller
    {
        DependencyGraph _graph;

        public HomeController( 
            DependencyGraph graph)
        {
            _graph = graph;
        }

        public class ClassSelectItem
        {
            public required string Display { get; set; }
            public required string Value { get; set; }
        }

        public class ClassSelectItemComparer : IEqualityComparer<ClassSelectItem>
        {
            public bool Equals(ClassSelectItem? x, ClassSelectItem? y)
            {
                if (x is null || y is null)
                    return false;

                return GetKey(x) == GetKey(y);
            }

            public int GetHashCode(ClassSelectItem? obj)
            {
                return GetKey(obj).GetHashCode();
            }

            private static string GetKey(ClassSelectItem? selectItem)
            {
                return selectItem == null ? "" : selectItem.Value;
            }
        }

            public IActionResult Index()
        {
            var allProjects = _graph.Nodes.Select(x => x.ProjectName)
                                    .Distinct().ToList();

            ViewData["ProjectList"] = allProjects;

            var allImplementations = _graph.Nodes.Select(x => {
                    if(x.ServiceInterface != null)
                    {
                        return new ClassSelectItem
                        {
                            Display = $"{x.ImplementationType.Name} : {x.ServiceInterface.Name}",
                            Value = $"{x.ImplementationType.ToDisplayString()} : {x.ServiceInterface.ToDisplayString()}"
                        };
                    }
                    else
                    {
                        return new ClassSelectItem
                        {
                            Display = $"{x.ImplementationType.Name}",
                            Value = $"{x.ImplementationType.ToDisplayString()}"
                        };
                    } 
                }).Distinct(new ClassSelectItemComparer()).ToList();

            ViewData["ClassNames"] = allImplementations;

            return View();
        }
    }
}
