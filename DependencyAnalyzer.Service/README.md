# DependencyAnalyzer Service

A persistent local API service that loads your solution's dependency analysis once and provides fast, queryable access to the analysis results.

## Why Use This Service?

- **Load Once, Query Many**: The solution analysis takes several minutes to load. This service keeps it in memory so you can query multiple times without reloading
- **API Access**: Provides structured JSON responses instead of text parsing
- **Fast Queries**: Once loaded, queries return in milliseconds
- **Local Changes**: Works with your local file system and uncommitted changes
- **Multiple Clients**: Can be accessed from any HTTP client (Claude, Python, PowerShell, curl, etc.)

## Quick Start

1. **Start the service:**
   ```cmd
   cd DependencyAnalyzer.Service
   dotnet run
   ```
   Or use the provided script:
   ```cmd
   run-service.cmd
   ```

2. **Load a solution:**
   ```powershell
   Invoke-RestMethod -Method Post -Uri "http://localhost:5123/load" `
     -Body '{"solutionPath":"C:\\YourPath\\Solution.sln"}' `
     -ContentType "application/json"
   ```

3. **Query the analysis:**
   ```powershell
   # Get transient leaks
   Invoke-RestMethod "http://localhost:5123/transient-leaks"

   # Get node info
   Invoke-RestMethod "http://localhost:5123/node/YourClassName"
   ```

## API Endpoints

### Status & Loading

- `GET /status` - Check if solution is loaded and get statistics
- `POST /load` - Load a solution (body: `{"solutionPath": "...", "forceReload": false}`)

### Querying Nodes

- `GET /nodes?project={name}` - List all nodes (optionally filtered by project)
- `GET /node/{className}?project={name}` - Get specific node details
- `GET /search?pattern={text}&searchType={class|method|dependency}` - Search for patterns

### Analysis Reports

- `GET /transient-leaks?project={name}` - Find transient memory leaks
- `GET /captive-dependencies/{className}` - Find captive dependency issues
- `GET /manual-resolutions?project={name}&type={typeName}` - Get manual resolutions

### Advanced Queries

- `POST /query` - Execute custom queries
  ```json
  {
    "queryType": "excessive-deps",
    "parameters": {
      "threshold": 10
    }
  }
  ```

## Client Examples

### PowerShell
```powershell
# Use the provided client script
. .\client-example.ps1
Get-ServiceStatus
Load-Solution "C:\\Path\\To\\Solution.sln"
Get-TransientLeaks -project "YourProject"
```

### Python
```python
# Use the provided client
python client-example.py load "C:\\Path\\To\\Solution.sln"
python client-example.py leaks --project YourProject
python client-example.py node ClassName
```

### Claude Desktop (via fetch)
Once the service is running, I can query it directly:
```javascript
fetch("http://localhost:5123/transient-leaks")
  .then(r => r.json())
  .then(console.log)
```

## Swagger UI

When running in development mode, Swagger UI is available at:
`http://localhost:5123/swagger`

## Performance Tips

1. **Initial Load**: The first load takes several minutes (same as the console app)
2. **Cached Queries**: Some queries are cached until the next refresh
3. **Memory Usage**: The service keeps the entire analysis in memory (~500MB-2GB depending on solution size)
4. **Partial Refresh**: Use `/refresh-file` for incremental updates (not fully implemented yet)

## Architecture

The service maintains these components in memory:
- `DependencyGraph` - The full dependency graph of all classes
- `SolutionAnalyzer` - Registration and type information
- `ManualResolutionParser` - Manual resolve/release tracking
- Query cache - Results of expensive queries

## For LLM Integration

This service is designed to be easily queryable by LLMs like Claude:

1. Structured JSON responses (no text parsing needed)
2. RESTful API design
3. Clear error messages
4. Stateless queries after initial load
5. Fast response times for interactive use

## Development

To extend the service:

1. Add new endpoints in `Program.cs`
2. Add new query types in the `ExecuteQuery` method
3. Implement incremental refresh in `RefreshFileAsync`
4. Add more cache strategies for expensive operations