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
            ReadTree(CSharpSyntaxTree.ParseText(new string(characters), path: path).GetRoot(), path, isTestCode, testAssembly, result);
            return result;
        }

        private static void ReadTree(SyntaxNode root, string path, bool isTestCode, string testAssembly, List<TestRuleViolation> result)
        {
            var aliases = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Where(u => u.Alias != null && u.Name != null)
                .GroupBy(u => u.Alias!.Name.Identifier.ValueText)
                .ToDictionary(g => g.Key, g => g.Last().Name!.ToString().Replace("global::", ""));
            Func<string, string> resolveName = name =>
            {
                name = name.Replace("global::", "");
                var parts = name.Split('.');
                return aliases.TryGetValue(parts[0], out var target) ? target + name.Substring(parts[0].Length) : name;
            };
            var model = CSharpCompilation.Create("TestRuleInput", new[] { root.SyntaxTree },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) }).GetSemanticModel(root.SyntaxTree);
            var staticAssert = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Any(u => u.StaticKeyword != default && u.Name?.ToString().Replace("global::", "") == "Xunit.Assert");
            var staticThread = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
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

            foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
            {
                var name = resolveName(attribute.Name.ToString()).Split('.').Last();
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
            foreach (var file in EnumerateSourceFiles(root))
            {
                var extension = System.IO.Path.GetExtension(file);
                var source = File.ReadAllText(file);
                var path = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
                if (extension == ".cs")
                {
                    bool isTestCode = System.IO.Path.GetFullPath(file).StartsWith(testRoot, StringComparison.OrdinalIgnoreCase);
                    if (isTestCode || source.Contains("InternalsVisibleTo", StringComparison.Ordinal))
                        result.AddRange(ReadCSharp(path, source, isTestCode, testAssembly));
                }
                else if (source.Contains("InternalsVisibleTo", StringComparison.Ordinal))
                    result.AddRange(ReadBuildFile(path, source, testAssembly));
            }
            return result.OrderBy(v => v.Path, StringComparer.Ordinal).ThenBy(v => v.Line).ThenBy(v => v.Column).ToArray();
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
