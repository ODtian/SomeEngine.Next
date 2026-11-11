using System.Diagnostics;
using System.Formats.Tar;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.ArchitectureRules;

public readonly record struct TypeNameViolation(string Path, int Line, int Column, string Name, string Word);

public static class TypeNameRules
{
    private static readonly HashSet<string> ForbiddenWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authoring", "Lease", "Cursor", "Plan", "Slot", "Owner", "Runtime", "Publication", "Admission",
        "Canonical", "Exact", "Stable", "Physical", "Logical", "Prepared", "Pending", "Snapshot",
        "Fingerprint", "Module", "Program", "Sink", "Contract", "Abi", "Invocation",
    };
    private static readonly Regex Words = new(@"[A-Z]+(?=[A-Z][a-z]|[0-9_]|$)|[A-Z]?[a-z]+|[0-9]+", RegexOptions.CultureInvariant);

    public static IReadOnlyList<TypeNameViolation> Check(string repository, string revision, string[]? paths = null)
    {
        HashSet<string> previous = ReadPreviousNames(repository, revision);
        using Process files = StartGit(repository, "ls-files", "--cached", "--others", "--exclude-standard", "--", "*.cs");
        string[] names = files.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        files.WaitForExit();
        var violations = new List<TypeNameViolation>();
        foreach (string name in names.Distinct(StringComparer.Ordinal))
        {
            string path = name.TrimEnd('\r').Replace('\\', '/');
            if (!IsSource(path) || paths is not null && !paths.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal))) continue;
            string fullPath = Path.Combine(repository, path);
            if (File.Exists(fullPath)) violations.AddRange(CheckTree(path, Parse(File.ReadAllText(fullPath)), previous));
        }
        return violations;
    }

    public static IReadOnlyList<TypeNameViolation> CheckSource(string path, string source, string previousSource = "")
        => CheckTree(path, Parse(source), DeclaredNames(Parse(previousSource)).ToHashSet(StringComparer.Ordinal));

    private static HashSet<string> ReadPreviousNames(string repository, string revision)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using Process archive = StartGit(repository, "archive", "--format=tar", revision);
        using (var tar = new TarReader(archive.StandardOutput.BaseStream, leaveOpen: true))
            while (tar.GetNextEntry() is { } entry)
                if (entry.DataStream is { } data && IsSource(entry.Name))
                {
                    using var text = new StreamReader(data, leaveOpen: true);
                    names.UnionWith(DeclaredNames(Parse(text.ReadToEnd())));
                }
        archive.StandardOutput.BaseStream.CopyTo(Stream.Null);
        archive.WaitForExit();
        if (archive.ExitCode != 0) throw new InvalidOperationException(archive.StandardError.ReadToEnd());
        return names;
    }

    private static IReadOnlyList<TypeNameViolation> CheckTree(string path, SyntaxNode root, HashSet<string> previous)
    {
        var violations = new List<TypeNameViolation>();
        foreach (SyntaxNode declaration in Declarations(root))
        {
            if (previous.Contains(FullName(declaration))) continue;
            SyntaxToken identifier = Identifier(declaration);
            string? word = Words.Matches(identifier.ValueText).Select(match => match.Value).FirstOrDefault(ForbiddenWords.Contains);
            if (word is null) continue;
            FileLinePositionSpan location = identifier.GetLocation().GetLineSpan();
            violations.Add(new(path, location.StartLinePosition.Line + 1, location.StartLinePosition.Character + 1, identifier.ValueText, word));
        }
        return violations;
    }

    private static bool IsSource(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
        && !path.Split('/').Any(part => part is "external" or "bin" or "obj" or ".git");
    private static SyntaxNode Parse(string source) => CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();
    private static IEnumerable<SyntaxNode> Declarations(SyntaxNode root)
        => root.DescendantNodes().Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
    private static IEnumerable<string> DeclaredNames(SyntaxNode root) => Declarations(root).Select(FullName);
    private static SyntaxToken Identifier(SyntaxNode node) => node is BaseTypeDeclarationSyntax type
        ? type.Identifier : ((DelegateDeclarationSyntax)node).Identifier;
    private static string TypePart(SyntaxNode node)
    {
        int arity = node switch
        {
            TypeDeclarationSyntax type => type.TypeParameterList?.Parameters.Count ?? 0,
            DelegateDeclarationSyntax value => value.TypeParameterList?.Parameters.Count ?? 0,
            _ => 0,
        };
        return Identifier(node).ValueText + "`" + arity;
    }
    private static string FullName(SyntaxNode node)
    {
        string space = string.Join('.', node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(value => value.Name.ToString()));
        string types = string.Join('+', node.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Cast<SyntaxNode>().Append(node).Select(TypePart));
        return space + "." + types;
    }
    private static Process StartGit(string repository, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }
}
