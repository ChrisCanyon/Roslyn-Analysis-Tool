# 🧩 DependencyAnalyzer

A Roslyn-powered static analysis tool that inspects and visualizes dependency injection usage in .NET applications — with a focus on detecting **lifestyle mismatches**, **unused registrations**, and **risky dependency chains**.

---

## 🔍 What It Does

✅ Analyzes DI containers to:
- Detect **singleton → scoped/transient** lifestyle violations  
- Trace **transitive dependencies** across the object graph  
- Highlight **unregistered services** and **unused registrations**  
- Group and report issues by **project**, **node**, or **lifetime**  
- Output high-resolution **Graphviz diagrams** and **text reports**

---

## 📦 Supported Features

- ✔️ Castle Windsor registration detection (with helper registration support)
- ✔️ Multiple lifetime strategies (Singleton, PerWebRequest, Transient)
- ✔️ Graph generation using DOT format (`.dot`)
- ✔️ High-resolution SVG/PNG export with customizable styles
- ✔️ Project-based grouping and analysis
- ✔️ JSON export for downstream tooling

---
