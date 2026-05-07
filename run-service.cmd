@echo off
echo ========================================
echo Starting DependencyAnalyzer Service
echo ========================================
echo.
echo The service will load your solution once and keep it in memory.
echo You can then query it multiple times without reloading.
echo.
echo Service will be available at: http://localhost:5123
echo Swagger UI will be at: http://localhost:5123/swagger
echo.
echo Press Ctrl+C to stop the service
echo ========================================
echo.

cd DependencyAnalyzer.Service
dotnet run --urls "http://localhost:5123"