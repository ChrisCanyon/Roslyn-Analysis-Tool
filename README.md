# Roslyn Analysis Tool (RAT)

Analyze and visualize dependency relationships in a .NET solution. Detect problems like lifetime mismatches, unused services, manual disposal, and more — with graphical output and a clean UI.

---

## 🔧 Features

- 🔍 **Analyze DI registrations**
  - Castle Windsor: `Component.For<>.ImplementedBy<>.LifestyleX()`
  - Microsoft.Extensions.DependencyInjection (M.DI): `services.AddX<>()` + factory/lambda methods
- 📊 **Generate visual dependency graphs**
  - Graphviz DOT & SVG for:
    - Consumer trees
    - Dependency trees
    - Full solution graph
- ⚠️ **Detect anti-patterns**
  - Captive dependencies (e.g., singleton depending on transient)
  - Manual disposal of injected services
- 💻 **Interactive UI**
  - Search class names
  - View dependency graphs and tree outputs

---

## 🚀 Getting Started

### Prerequisites

- .NET 8 SDK
- [Graphviz](https://graphviz.org/) (for rendering `.dot` files)
  - Ensure graphviz is in your PATH
### Setup

1. Clone the repo:
   ```bash
   git clone https://github.com/ChrisCanyon/DependencyAnalyzer.git
   cd DependencyAnalyzer

2. Add your solution path:
   ```csharp
   //In MVCWebView/Program.cs
    SolutionAnalyzer solutionAnalyzer = await SolutionAnalyzer.BuildSolutionAnalyzer("C:\\PathToYour\\Solution.sln");
3. That's it!  
    Run the MVCWebView to see your code in a whole new way
    It can take a few minute for large repos to be analyzed before the UI loads. Check the console for logs if you think it is taking a while
