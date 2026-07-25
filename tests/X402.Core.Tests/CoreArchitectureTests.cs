using System.Reflection;
using X402.Licensing;

namespace X402.Core.Tests;

/// <summary>
/// Verifies that X402.Core keeps its promise: the free core performs no network licence check
/// and emits no telemetry. This is tested by denying networking and telemetry assembly references
/// and by scanning all method signatures and fields for networking/telemetry types.
///
/// Residual gap: a telemetry type used ONLY in a method body (never in a signature or field)
/// remains undetectable by reflection, since corelib is always referenced and method bodies
/// cannot be scanned without IL analysis. Closing that would require IL inspection, which is
/// disproportionate for this library. This test covers signatures, fields, and generic arguments,
/// leaving only dead code hiding-places uncovered.
/// </summary>
public sealed class CoreArchitectureTests
{
    private static readonly Assembly Core = typeof(IFeatureGate).Assembly;

    /// <remarks>
    /// Forbidden telemetry types that indicate telemetry emission without a network call.
    /// These types enable observable data emission (traces, metrics, events) with no dependency
    /// on networking. Some live in corelib and cannot be denied at assembly level, but are
    /// detected here by type name.
    /// </remarks>
    private static readonly string[] ForbiddenTelemetryTypes =
    {
        "System.Diagnostics.Tracing.EventSource",
        "System.Diagnostics.ActivitySource",
        "System.Diagnostics.Activity",
        "System.Diagnostics.DiagnosticListener",
        "System.Diagnostics.DiagnosticSource",
    };

    [Fact]
    public void Core_does_not_reference_any_networking_assembly()
    {
        // US-17 requires that no network licence check or telemetry can exist in the core.
        // Guarantee it through the absence of a dependency rather than through discipline.
        var referenced = Core.GetReferencedAssemblies().Select(a => a.Name).ToList();

        // Check for all System.Net.* assemblies using prefix match.
        referenced.ShouldNotContain(name =>
            name!.StartsWith("System.Net", StringComparison.Ordinal));

        // Also deny DiagnosticSource, which enables telemetry emission.
        referenced.ShouldNotContain(name =>
            name!.StartsWith("System.Diagnostics.DiagnosticSource", StringComparison.Ordinal));
    }

    [Fact]
    public void Core_does_not_use_any_networking_or_telemetry_type()
    {
        // A reference can be elided by the compiler; we also check the types actually used.
        // Scan method signatures (return types and parameter types), field types, and generic arguments.
        var networkingAndTelemetryTypes = Core.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Instance | BindingFlags.Static
                                          | BindingFlags.DeclaredOnly))
            .SelectMany(m =>
            {
                var types = new[] { m.ReturnType }.AsEnumerable();
                types = types.Concat(m.GetParameters().Select(p => p.ParameterType));
                types = types.Concat(m.ReturnType.GetGenericArguments());
                types = types.Concat(m.GetGenericArguments());
                return types;
            })
            .Concat(Core.GetTypes()
                .SelectMany(t => t.GetFields(BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                    | BindingFlags.DeclaredOnly))
                .SelectMany(f =>
                {
                    var types = new[] { f.FieldType }.AsEnumerable();
                    types = types.Concat(f.FieldType.GetGenericArguments());
                    return types;
                }))
            .Where(t => t.Namespace?.StartsWith("System.Net", StringComparison.Ordinal) == true
                     || ForbiddenTelemetryTypes.Contains(t.FullName))
            .Distinct()
            .ToList();

        networkingAndTelemetryTypes.ShouldBeEmpty();
    }
}
