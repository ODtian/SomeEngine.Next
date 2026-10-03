using Microsoft.CodeAnalysis;

namespace SomeEngine.Views.Generators;

internal static class ViewDiagnostics
{
    internal static readonly DiagnosticDescriptor ChooseStorage = new(
        "SEV002", "Choose view storage", "View '{0}' defaults to sparse storage; declare IComponent or ISparseComponent to select storage explicitly",
        "Views", DiagnosticSeverity.Info, true);

    internal static readonly DiagnosticDescriptor PartialViewRequired = new(
        "SEV003", "Allow generated view declarations", "Make '{0}' partial to add generated declarations, or declare its component storage explicitly",
        "Views", DiagnosticSeverity.Error, true);

    internal static readonly DiagnosticDescriptor PartialContainerRequired = new(
        "SEV005", "Allow nested view functions", "Make enclosing type '{0}' partial to generate its nested view functions",
        "Views", DiagnosticSeverity.Error, true);
}
