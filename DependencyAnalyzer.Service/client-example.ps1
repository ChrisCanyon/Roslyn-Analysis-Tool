# PowerShell client example for DependencyAnalyzer Service

$baseUrl = "http://localhost:5123"

# Check service status
function Get-ServiceStatus {
    $response = Invoke-RestMethod "$baseUrl/status"
    Write-Host "Service Status:" -ForegroundColor Green
    $response | ConvertTo-Json
}

# Load a solution
function Load-Solution {
    param([string]$solutionPath)

    $body = @{
        solutionPath = $solutionPath
        forceReload = $false
    } | ConvertTo-Json

    Write-Host "Loading solution..." -ForegroundColor Yellow
    $response = Invoke-RestMethod "$baseUrl/load" -Method Post -Body $body -ContentType "application/json"
    $response | ConvertTo-Json
}

# Find transient leaks
function Get-TransientLeaks {
    param([string]$project)

    $url = "$baseUrl/transient-leaks"
    if ($project) {
        $url += "?project=$project"
    }

    $response = Invoke-RestMethod $url
    Write-Host "Found $($response.Count) transient leaks" -ForegroundColor Yellow
    $response | Format-Table -AutoSize
}

# Get node information
function Get-NodeInfo {
    param([string]$className)

    $response = Invoke-RestMethod "$baseUrl/node/$className"
    $response | ConvertTo-Json -Depth 3
}

# Search for classes
function Search-Classes {
    param([string]$pattern)

    $response = Invoke-RestMethod "$baseUrl/search?pattern=$pattern&searchType=class"
    $response | Format-Table -AutoSize
}

# Find captive dependencies
function Get-CaptiveDependencies {
    param([string]$className)

    $response = Invoke-RestMethod "$baseUrl/captive-dependencies/$className"
    Write-Host "Captive dependencies for $className`:" -ForegroundColor Green
    $response | Format-Table -AutoSize
}

# Example usage
Write-Host "DependencyAnalyzer Service Client" -ForegroundColor Cyan
Write-Host "=================================" -ForegroundColor Cyan

# Check if service is running
Get-ServiceStatus

# Load a solution (update path as needed)
# Load-Solution "C:\path\to\YourSolution.sln"

# Query for transient leaks
# Get-TransientLeaks -project "YourProject"

# Get specific node info
# Get-NodeInfo "SomeClass"

# Search for classes
# Search-Classes "Gateway"

# Find captive dependencies
# Get-CaptiveDependencies "SomeTransientService"