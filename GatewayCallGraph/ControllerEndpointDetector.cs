using Microsoft.CodeAnalysis;

namespace GatewayCallGraph;

/// <summary>
/// Decides whether an <see cref="IMethodSymbol"/> represents a controller action
/// (the boundary at which we stop walking up the call chain).
///
/// Targets .NET Framework 4.8 ASP.NET MVC 5 codebases — looks for inheritance
/// from System.Web.Mvc.Controller / System.Web.Http.ApiController and standard
/// MVC public-method conventions.
/// </summary>
public static class ControllerEndpointDetector
{
    private static readonly string[] ControllerBaseTypes =
    {
        "System.Web.Mvc.Controller",
        "System.Web.Mvc.ControllerBase",
        "System.Web.Http.ApiController",
    };

    public static bool IsControllerAction(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary) return false;
        if (method.DeclaredAccessibility != Accessibility.Public) return false;
        if (method.IsStatic) return false;

        var containingType = method.ContainingType;
        if (containingType == null) return false;

        if (!InheritsFromController(containingType)) return false;

        if (IsDeclaredOnControllerBase(method)) return false;

        return true;
    }

    private static bool InheritsFromController(INamedTypeSymbol type)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var name = t.ToDisplayString();
            foreach (var baseType in ControllerBaseTypes)
            {
                if (name == baseType) return true;
            }
        }
        return false;
    }

    private static bool IsDeclaredOnControllerBase(IMethodSymbol method)
    {
        var declaringType = method.ContainingType?.ToDisplayString();
        if (declaringType == null) return false;

        foreach (var baseType in ControllerBaseTypes)
        {
            if (declaringType == baseType) return true;
        }
        return false;
    }
}
