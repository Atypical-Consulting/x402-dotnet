using System.Reflection;
using X402.Licensing;

namespace X402.Core.Tests;

public sealed class CoreArchitectureTests
{
    private static readonly Assembly Core = typeof(IFeatureGate).Assembly;

    [Fact]
    public void Core_does_not_reference_any_networking_assembly()
    {
        // US-17 exige qu'aucune vérification de licence réseau ni télémétrie ne puisse exister
        // dans le noyau. Le garantir par l'absence de dépendance plutôt que par la discipline.
        var forbidden = new[] { "System.Net.Http", "System.Net.Sockets", "System.Net.Primitives" };

        var referenced = Core.GetReferencedAssemblies().Select(a => a.Name).ToList();

        referenced.ShouldNotContain(name => forbidden.Contains(name));
    }

    [Fact]
    public void Core_does_not_use_any_networking_type()
    {
        // Une référence peut être élidée par le compilateur ; on vérifie aussi les types utilisés.
        var networkingTypes = Core.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Instance | BindingFlags.Static
                                          | BindingFlags.DeclaredOnly))
            .Select(m => m.ReturnType)
            .Concat(Core.GetTypes().SelectMany(t => t.GetFields(BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.DeclaredOnly)).Select(f => f.FieldType))
            .Where(t => t.Namespace?.StartsWith("System.Net", StringComparison.Ordinal) == true)
            .Distinct()
            .ToList();

        networkingTypes.ShouldBeEmpty();
    }
}
