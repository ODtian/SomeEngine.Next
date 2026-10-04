#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.TestRules
{
    public sealed class TestRuleViolation
    {
        public string Rule { get; }
        public string Path { get; }
        public int Line { get; }
        public int Column { get; }
        public string Member { get; }
        public string Code { get; }

        public TestRuleViolation(string rule, string path, int line, int column, string member, string code)
        {
            Rule = rule;
            Path = path;
            Line = line;
            Column = column;
            Member = member;
            Code = code;
        }
    }

    public static class TestRuleScanner
    {
        public static IReadOnlyList<TestRuleViolation> ReadCSharp(string path, string source, bool isTestCode, string testAssembly)
        {
            var result = new List<TestRuleViolation>();
            var tree = ReadSyntaxTree(path, source);
            var compilation = CreateCompilation(new[] { tree });
            ReadTree(tree.GetRoot(), compilation.GetSemanticModel(tree), path, isTestCode, testAssembly, result);
            return result;
        }

        private static SyntaxTree ReadSyntaxTree(string path, string source)
        {
            // Scan every conditional branch while preserving source positions.
            var firstTree = CSharpSyntaxTree.ParseText(source, path: path).GetRoot();
            var characters = source.ToCharArray();
            foreach (var trivia in firstTree.DescendantTrivia(descendIntoTrivia: true))
            {
                var directive = trivia.GetStructure();
                if (directive is IfDirectiveTriviaSyntax || directive is ElifDirectiveTriviaSyntax ||
                    directive is ElseDirectiveTriviaSyntax || directive is EndIfDirectiveTriviaSyntax)
                    for (int index = trivia.Span.Start; index < trivia.Span.End; index++)
                        if (characters[index] != '\r' && characters[index] != '\n')
                            characters[index] = ' ';
            }
            return CSharpSyntaxTree.ParseText(new string(characters), path: path);
        }

        private static CSharpCompilation CreateCompilation(IEnumerable<SyntaxTree> trees) =>
            CSharpCompilation.Create("TestRuleInput", trees,
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

        private static void ReadTree(SyntaxNode root, SemanticModel model, string path, bool isTestCode, string testAssembly, List<TestRuleViolation> result, bool readAssemblyAttributes = true)
        {
            var imports = model.Compilation.SyntaxTrees.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>())
                .Where(u => u.SyntaxTree == root.SyntaxTree || u.GlobalKeyword != default).ToArray();
            var aliases = imports
                .Where(u => u.Alias != null && u.Name != null)
                .GroupBy(u => u.Alias!.Name.Identifier.ValueText)
                .ToDictionary(g => g.Key, g => g.Last().Name!.ToString().Replace("global::", ""));
            Func<string, string> resolveName = name =>
            {
                name = name.Replace("global::", "");
                var parts = name.Split('.');
                return aliases.TryGetValue(parts[0], out var target) ? target + name.Substring(parts[0].Length) : name;
            };
            var staticAssert = imports
                .Any(u => u.StaticKeyword != default && u.Name?.ToString().Replace("global::", "") == "Xunit.Assert");
            var staticThread = imports
                .Any(u => u.StaticKeyword != default && u.Name?.ToString().Replace("global::", "") == "System.Threading.Thread");
            Func<InvocationExpressionSyntax, string?> readAssertionName = call =>
            {
                if (call.Expression is MemberAccessExpressionSyntax access)
                {
                    var name = resolveName(access.Expression.ToString());
                    if (name == "Assert" || name == "Xunit.Assert")
                        return access.Name.Identifier.ValueText;
                }
                if (staticAssert && call.Expression is SimpleNameSyntax simple)
                    return simple.Identifier.ValueText;
                return null;
            };

            foreach (var attribute in readAssemblyAttributes ? root.DescendantNodes().OfType<AttributeSyntax>() : Enumerable.Empty<AttributeSyntax>())
            {
                var name = model.GetSymbolInfo(attribute).Symbol?.ContainingType?.Name ??
                    resolveName(attribute.Name.ToString()).Split('.').Last();
                if (name != "InternalsVisibleTo" && name != "InternalsVisibleToAttribute")
                    continue;
                var expression = attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
                var value = expression == null ? default : model.GetConstantValue(expression);
                if (value.HasValue && value.Value is string assembly && ReadAssemblyName(assembly) == testAssembly)
                    AddViolation(result, "TEST001", path, attribute, "assembly");
            }

            if (isTestCode)
            {
                foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (call.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == "Sleep")
                    {
                        var name = resolveName(access.Expression.ToString());
                        if (name == "Thread" || name == "System.Threading.Thread")
                            AddViolation(result, "TEST003", path, call);
                    }
                    else if (staticThread && call.Expression is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == "Sleep")
                        AddViolation(result, "TEST003", path, call);

                    var assertion = readAssertionName(call);
                    if (assertion == null || assertion == "Fail" || assertion.StartsWith("Throws", StringComparison.Ordinal))
                        continue;
                    var scope = call.Ancestors().FirstOrDefault(n => n is BaseMethodDeclarationSyntax || n is PropertyDeclarationSyntax) ?? root;
                    var messageVariables = new HashSet<string>(StringComparer.Ordinal);
                    Func<SyntaxNode, bool> readsMessage = expression => expression.DescendantNodesAndSelf().Any(n =>
                        (n is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "Message" && IsExceptionMessage(member, model)) ||
                        (n is MemberBindingExpressionSyntax binding && binding.Name.Identifier.ValueText == "Message" && IsExceptionMessage(binding, model)) ||
                        (n is IdentifierNameSyntax variable && messageVariables.Contains(variable.Identifier.ValueText)));
                    bool changed;
                    do
                    {
                        changed = false;
                        foreach (var variable in scope.DescendantNodes().OfType<VariableDeclaratorSyntax>())
                            if (variable.Initializer != null && readsMessage(variable.Initializer.Value))
                                changed |= messageVariables.Add(variable.Identifier.ValueText);
                        foreach (var assignment in scope.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                            if (assignment.Left is IdentifierNameSyntax variable && readsMessage(assignment.Right))
                                changed |= messageVariables.Add(variable.Identifier.ValueText);
                    } while (changed);
                    var arguments = call.ArgumentList.Arguments.AsEnumerable();
                    if (assertion == "True" || assertion == "False")
                        arguments = arguments.Take(1);
                    if (arguments.Any(a => readsMessage(a.Expression)))
                        AddViolation(result, "TEST002", path, call);
                }
            }

        }

        public static IReadOnlyList<TestRuleViolation> ReadBuildFile(string path, string source, string testAssembly)
        {
            var result = new List<TestRuleViolation>();
            var document = XDocument.Parse(source, LoadOptions.SetLineInfo);
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (System.IO.Path.GetExtension(path) == ".csproj")
            {
                string projectName = System.IO.Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
                properties["MSBuildProjectName"] = projectName;
                properties["AssemblyName"] = projectName;
            }
            foreach (var group in document.Descendants().Where(element => element.Name.LocalName == "PropertyGroup"))
                foreach (var property in group.Elements())
                    properties[property.Name.LocalName] = property.Value.Trim();
            foreach (var item in document.Descendants())
            {
                string? target = null;
                if (item.Name.LocalName == "InternalsVisibleTo")
                    target = (string?)item.Attribute("Include") ?? (string?)item.Attribute("Update");
                if (item.Name.LocalName == "AssemblyAttribute" &&
                    ((string?)item.Attribute("Include"))?.Replace("Attribute", "").EndsWith("InternalsVisibleTo", StringComparison.Ordinal) == true)
                    target = item.Elements().FirstOrDefault(e => e.Name.LocalName == "_Parameter1")?.Value;
                if (target == null || !target.Split(';').Any(value => TargetsTestAssembly(value, testAssembly, properties)))
                    continue;
                var location = (IXmlLineInfo)item;
                result.Add(new TestRuleViolation("TEST001", path, location.LineNumber, location.LinePosition,
                    "assembly", item.ToString(SaveOptions.DisableFormatting).Replace("\t", " ").Replace("\r", "").Replace("\n", "")));
            }
            return result;
        }

        private static bool TargetsTestAssembly(string value, string testAssembly, IReadOnlyDictionary<string, string> properties)
        {
            for (int pass = 0; pass <= properties.Count; pass++)
            {
                string expanded = Regex.Replace(value, @"\$\(([^)]+)\)", match =>
                    properties.TryGetValue(match.Groups[1].Value, out string? property) ? property : match.Value);
                if (expanded == value)
                    break;
                value = expanded;
            }
            string assembly = ReadAssemblyName(value);
            string pattern = string.Concat(Regex.Split(assembly, @"(\$\([^)]+\))")
                .Select(part => part.StartsWith("$(", StringComparison.Ordinal) ? ".+" : Regex.Escape(part)));
            return Regex.IsMatch(testAssembly, "^" + pattern + "$", RegexOptions.CultureInvariant);
        }

        public static IReadOnlyList<TestRuleViolation> ReadProject(string root, string projectDirectory, string testAssembly)
        {
            var result = new List<TestRuleViolation>();
            var testRoot = System.IO.Path.GetFullPath(projectDirectory).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            var files = EnumerateSourceFiles(root).ToArray();
            var projectDirectories = files.Where(file => System.IO.Path.GetExtension(file) == ".csproj")
                .Select(file => System.IO.Path.GetDirectoryName(file)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sources = new Dictionary<string, List<SyntaxTree>>(StringComparer.OrdinalIgnoreCase);
            var sourceTexts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var extension = System.IO.Path.GetExtension(file);
                var source = File.ReadAllText(file);
                var path = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
                if (extension == ".cs")
                {
                    var directory = System.IO.Path.GetDirectoryName(file)!;
                    while (!projectDirectories.Contains(directory) && directory != root)
                        directory = System.IO.Path.GetDirectoryName(directory) ?? root;
                    if (!sources.TryGetValue(directory, out var trees))
                        sources.Add(directory, trees = new List<SyntaxTree>());
                    trees.Add(ReadSyntaxTree(path, source));
                    sourceTexts.Add(path, source);
                }
                else if (source.Contains("InternalsVisibleTo", StringComparison.Ordinal))
                    result.AddRange(ReadBuildFile(path, source, testAssembly));
            }
            foreach (var trees in sources.Values)
            {
                bool hasFriends = trees.Any(tree => tree.GetRoot().DescendantNodes().Any(node =>
                    node is AttributeSyntax attribute && IsFriendName(attribute.Name.ToString()) ||
                    node is UsingDirectiveSyntax import && import.Alias != null && import.Name != null && IsFriendName(import.Name.ToString())));
                var inspected = trees.Where(tree => hasFriends && tree.GetRoot().DescendantNodes().OfType<AttributeListSyntax>().Any(list => list.Target?.Identifier.ValueText == "assembly") ||
                    System.IO.Path.GetFullPath(System.IO.Path.Combine(root, tree.FilePath)).StartsWith(testRoot, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (inspected.Length == 0)
                    continue;
                var originalTrees = hasFriends ? trees.Select(tree => CSharpSyntaxTree.ParseText(sourceTexts[tree.FilePath], path: tree.FilePath)).ToArray() : Array.Empty<SyntaxTree>();
                var conditions = originalTrees.SelectMany(tree => tree.GetRoot().DescendantTrivia(descendIntoTrivia: true))
                    .Select(trivia => trivia.GetStructure())
                    .Select(directive => directive is IfDirectiveTriviaSyntax start ? start.Condition :
                        directive is ElifDirectiveTriviaSyntax next ? next.Condition : null)
                    .Where(condition => condition != null).ToArray();
                var symbols = conditions.SelectMany(condition => condition!.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                    .Select(identifier => identifier.Identifier.ValueText).Distinct(StringComparer.Ordinal).ToArray();
                var compilation = CreateCompilation(trees);
                foreach (var tree in inspected)
                {
                    bool isTestCode = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, tree.FilePath)).StartsWith(testRoot, StringComparison.OrdinalIgnoreCase);
                    ReadTree(tree.GetRoot(), compilation.GetSemanticModel(tree), tree.FilePath, isTestCode, testAssembly, result, hasFriends);
                }
                if (conditions.Length == 0)
                    continue;
                foreach (var definitions in ReadDefinitions(symbols, 0))
                {
                    var options = CSharpParseOptions.Default.WithPreprocessorSymbols(definitions);
                    var branchTrees = originalTrees.Select(tree => CSharpSyntaxTree.ParseText(sourceTexts[tree.FilePath], options, tree.FilePath)).ToArray();
                    var branchCompilation = CreateCompilation(branchTrees);
                    foreach (var tree in branchTrees.Where(tree => tree.GetRoot().DescendantNodes().OfType<AttributeListSyntax>()
                        .Any(list => list.Target?.Identifier.ValueText == "assembly")))
                        ReadTree(tree.GetRoot(), branchCompilation.GetSemanticModel(tree), tree.FilePath, false, testAssembly, result);
                }
            }
            return result.GroupBy(v => (v.Rule, v.Path, v.Line, v.Column)).Select(group => group.First())
                .OrderBy(v => v.Path, StringComparer.Ordinal).ThenBy(v => v.Line).ThenBy(v => v.Column).ToArray();
        }

        private static IEnumerable<string[]> ReadDefinitions(string[] symbols, int index)
        {
            if (index == symbols.Length)
            {
                yield return Array.Empty<string>();
                yield break;
            }
            foreach (var remaining in ReadDefinitions(symbols, index + 1))
            {
                yield return remaining;
                yield return new[] { symbols[index] }.Concat(remaining).ToArray();
            }
        }

        private static bool IsFriendName(string name)
        {
            var last = name.Split('.').Last();
            return last == "InternalsVisibleTo" || last == "InternalsVisibleToAttribute";
        }

        private static IEnumerable<string> EnumerateSourceFiles(string directory)
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var extension = System.IO.Path.GetExtension(file);
                if (extension == ".cs" || extension == ".csproj" || extension == ".props" || extension == ".targets")
                    yield return file;
            }
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = System.IO.Path.GetFileName(child);
                if (name == "bin" || name == "obj" || name == ".git" || name == "external" || name.StartsWith(".", StringComparison.Ordinal))
                    continue;
                foreach (var file in EnumerateSourceFiles(child))
                    yield return file;
            }
        }

        private static bool IsExceptionMessage(SyntaxNode expression, SemanticModel model)
        {
            var type = model.GetSymbolInfo(expression).Symbol?.ContainingType;
            if (type == null)
                return true;
            while (type != null)
            {
                if (type.TypeKind == TypeKind.Error || type.ToDisplayString() == "System.Exception")
                    return true;
                type = type.BaseType;
            }
            return false;
        }

        private static string ReadAssemblyName(string value) => value.Split(',')[0].Trim();

        private static void AddViolation(List<TestRuleViolation> result, string rule, string path, SyntaxNode node, string? member = null)
        {
            var location = node.GetLocation().GetLineSpan().StartLinePosition;
            var names = node.Ancestors().Reverse().Select(n =>
                n is BaseTypeDeclarationSyntax type ? type.Identifier.ValueText :
                n is MethodDeclarationSyntax method ? method.Identifier.ValueText :
                n is ConstructorDeclarationSyntax constructor ? constructor.Identifier.ValueText :
                n is LocalFunctionStatementSyntax local ? local.Identifier.ValueText :
                n is PropertyDeclarationSyntax property ? property.Identifier.ValueText : null).Where(n => n != null);
            var code = string.Join(" ", node.DescendantTokens().Select(t => t.Text.Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n")));
            result.Add(new TestRuleViolation(rule, path, location.Line + 1, location.Character + 1, member ?? string.Join(".", names), code));
        }
    }

    public static class TestRuleCommand
    {
        public static int Main(string[] arguments)
        {
            if (arguments.Length != 3)
            {
                Console.Error.WriteLine("Usage: SomeEngine.TestRules <repository> <test-project-directory> <test-assembly>");
                return 2;
            }
            var root = System.IO.Path.GetFullPath(arguments[0]);
            var projectDirectory = arguments[1];
            var testAssembly = arguments[2];
            var violations = TestRuleScanner.ReadProject(root, projectDirectory, testAssembly);
            foreach (var violation in violations)
                Console.Error.WriteLine("{0}({1},{2}): error {3}: {4}: {5}. Rule source: {6}",
                    System.IO.Path.Combine(root, violation.Path), violation.Line, violation.Column,
                    violation.Rule, violation.Member, violation.Code,
                    System.IO.Path.Combine(root, "wiki", "harness", "Behavior-Checks.md"));
            Console.WriteLine("Test rules: {0}; {1} violations.", testAssembly, violations.Count);
            return violations.Count == 0 ? 0 : 1;
        }
    }
}
