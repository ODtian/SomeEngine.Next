using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Harness.QualityAnalyzer;

/// <summary>
/// Duplicate enum gate. Detects singular/plural enum declarations that reuse
/// meaningful member names. Common values such as None or Unknown are not
/// evidence that two otherwise unrelated enum domains should be merged.
/// </summary>
internal static class DuplicateEnumAnalyzer
{
    public const string RuleId = "SE052";

    private static readonly ImmutableHashSet<string> IgnoredMembers =
        ImmutableHashSet.Create(
            System.StringComparer.OrdinalIgnoreCase,
            "None",
            "Unknown",
            "Unspecified",
            "Default",
            "Invalid");

    public static readonly ImmutableArray<DiagnosticDescriptor> Descriptors =
        ImmutableArray.Create(
            Rules.Create(RuleId,
                "Duplicate enum members",
                "Enum '{0}' shares members with enum '{1}': [{2}]. Merge or drop one. See [[DRY]].",
                Rules.CategoryNaming,
                DiagnosticSeverity.Warning));

    public static void Register(AnalysisContext context)
    {
        context.RegisterCompilationStartAction(ctx =>
        {
            var enums = new List<EnumInfo>();
            var gate = new object();

            ctx.RegisterSyntaxNodeAction(c => Collect(c, enums, gate), SyntaxKind.EnumDeclaration);
            ctx.RegisterCompilationEndAction(c => ReportDuplicates(c, enums, gate));
        });
    }

    private static void Collect(
        SyntaxNodeAnalysisContext ctx,
        List<EnumInfo> enums,
        object gate)
    {
        var decl = (EnumDeclarationSyntax)ctx.Node;
        var name = decl.Identifier.ValueText;
        var members = decl.Members.Select(m => m.Identifier.ValueText).ToArray();

        lock (gate)
        {
            enums.Add(new EnumInfo(name, members, decl.Identifier.GetLocation()));
        }
    }

    private static void ReportDuplicates(
        CompilationAnalysisContext context,
        List<EnumInfo> enums,
        object gate)
    {
        EnumInfo[] snapshot;
        lock (gate)
        {
            snapshot = enums
                .OrderBy(info => info.Location.SourceTree?.FilePath ?? string.Empty, System.StringComparer.Ordinal)
                .ThenBy(info => info.Location.SourceSpan.Start)
                .ThenBy(info => info.Name, System.StringComparer.Ordinal)
                .ToArray();
        }

        for (var leftIndex = 0; leftIndex < snapshot.Length; leftIndex++)
        {
            EnumInfo left = snapshot[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < snapshot.Length; rightIndex++)
            {
                EnumInfo right = snapshot[rightIndex];
                if (!NamesSuggestDuplicate(left.Name, right.Name))
                {
                    continue;
                }

                var shared = right.Members
                    .Intersect(left.Members, System.StringComparer.OrdinalIgnoreCase)
                    .Where(member => !IgnoredMembers.Contains(member))
                    .Distinct(System.StringComparer.OrdinalIgnoreCase)
                    .OrderBy(member => member, System.StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (shared.Length == 0)
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors[0],
                    right.Location,
                    right.Name,
                    left.Name,
                    string.Join(", ", shared)));
            }
        }
    }

    private static bool NamesSuggestDuplicate(string left, string right) =>
        System.String.Equals(
            RemovePluralSuffix(left),
            RemovePluralSuffix(right),
            System.StringComparison.OrdinalIgnoreCase);

    private static string RemovePluralSuffix(string value) =>
        value.Length > 1 && value.EndsWith("s", System.StringComparison.OrdinalIgnoreCase)
            ? value.Substring(0, value.Length - 1)
            : value;

    private sealed class EnumInfo
    {
        public EnumInfo(string name, IReadOnlyList<string> members, Location location)
        {
            Name = name;
            Members = members;
            Location = location;
        }

        public string Name { get; }

        public IReadOnlyList<string> Members { get; }

        public Location Location { get; }
    }
}
