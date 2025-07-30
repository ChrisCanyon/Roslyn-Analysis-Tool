# DependencyAnalyzer

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
  - `new` operator used instead of DI
  - Unused service methods
- 💻 **Interactive UI**
  - Search class names
  - View dependency graphs and tree outputs
  - Switch between analysis modes

---

## 🚀 Getting Started

### Prerequisites

- .NET 8 SDK
- [Graphviz](https://graphviz.org/) (for rendering `.dot` files)

### Setup

1. Clone the repo:
   ```bash
   git clone https://github.com/ChrisCanyon/DependencyAnalyzer.git
   cd DependencyAnalyzer
