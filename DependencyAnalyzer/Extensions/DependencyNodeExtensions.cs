using DependencyAnalyzer.Comparers;
using DependencyAnalyzer.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DependencyAnalyzer.Extensions
{
    public static class DependencyNodeExtensions
    {
        private static readonly FullyQualifiedNameComparer Cmp = new();

        public static RawDependency GetRawDependency(this DependencyNode dependant, DependencyNode dependency)
        {
            return dependant.RawDependencies.First(x => dependency.SatisfiesDependency(x.Type));
        }

        public static bool SatisfiesDependency(this DependencyNode node, INamedTypeSymbol requested)
        {
            var cmp = new FullyQualifiedNameComparer();

            // Interface request
            if (requested.TypeKind == TypeKind.Interface)
            {
                if (node.ServiceInterface is null) return false;

                if (!requested.IsUnboundGenericType)
                {
                    // Closed request: exact match OR open registration with same original def
                    if (cmp.Equals(node.ServiceInterface, requested)) return true; // exact closed
                    if (node.ServiceInterface.IsUnboundGenericType &&
                        cmp.Equals(node.ServiceInterface.OriginalDefinition, requested.OriginalDefinition))
                        return true; // open generic registration satisfies closed
                    return false;
                }
                else
                {
                    // Open request: match by original definition
                    return cmp.Equals(node.ServiceInterface.OriginalDefinition, requested.OriginalDefinition);
                }
            }

            // Class request
            if (requested.TypeKind == TypeKind.Class)
            {
                if (!requested.IsUnboundGenericType)
                {
                    if (cmp.Equals(node.ImplementationType, requested)) return true; // exact closed impl
                    if (node.ImplementationType.IsUnboundGenericType &&
                        cmp.Equals(node.ImplementationType.OriginalDefinition, requested.OriginalDefinition))
                        return true; // open impl satisfies closed
                    return false;
                }
                else
                {
                    return cmp.Equals(node.ImplementationType.OriginalDefinition, requested.OriginalDefinition);
                }
            }

            return false;
        }

        public static List<INamedTypeSymbol> UnsatisfiedDependencies(this DependencyNode node)
        {
            if (node._unsatisfiedDependencies != null) return node._unsatisfiedDependencies;

            var ret = new List<INamedTypeSymbol>();

            foreach (var rawDep in node.RawDependencies)
            {
                if (!node.DependsOn.Any(x => x.SatisfiesDependency(rawDep.Type)))
                {
                    ret.Add(rawDep.Type);
                }
            }

            node._unsatisfiedDependencies = ret;
            return ret;
        }

        public static List<INamedTypeSymbol> SatisfiedDependencies(this DependencyNode node)
        {
            if (node._satisfiedDependencies != null) return node._satisfiedDependencies;

            var ret = new List<INamedTypeSymbol>();

            foreach (var rawDep in node.RawDependencies)
            {
                if (node.DependsOn.Any(x => x.SatisfiesDependency(rawDep.Type)))
                {
                    ret.Add(rawDep.Type);
                }
            }

            node._satisfiedDependencies = ret;
            return ret;
        }

        public static bool IsPotentiallyStateful(this DependencyNode node)
        {
            return node.PotentialStateFields().Any();
        }

        public static List<ISymbol> PotentialStateFields(this DependencyNode node) {
            if(node._potentialStateFields != null) return node._potentialStateFields;

            var ret = new List<ISymbol>();

            var members = node.ImplementationType.GetMembers();

            var fieldsAndEvents = members
                .Where(m => (m is IFieldSymbol f && !f.IsConst)
                            || m is IEventSymbol)
                .Cast<ISymbol>()
                .ToList();

            // 2. Remove fields that correspond to dependencies (constructor or manual)
            var dependencyTypes = node.SatisfiedDependencies();
            var cmp = new FullyQualifiedNameComparer();

            ret = fieldsAndEvents.Where(member =>
            {
                INamedTypeSymbol? memberType = GetMemberType(member);
                return memberType != null &&
                       !dependencyTypes.Any(depType =>
                           cmp.Equals(depType, memberType));
            }).ToList();

            //remove loggers
            ret = ret.Where(x => !IsLoggingType(x)).ToList();

            node._potentialStateFields = ret;
            return ret;
        }

        private static INamedTypeSymbol? GetMemberType(ISymbol member) =>
            member switch
            {
                IFieldSymbol f => f.Type as INamedTypeSymbol,
                IEventSymbol e => e.Type as INamedTypeSymbol,
                _ => null
            };

        private static bool IsLoggingType(ISymbol type)
        {
            var name = type.Name;

            return name.Contains("Log", StringComparison.OrdinalIgnoreCase);
        }
    }
}
