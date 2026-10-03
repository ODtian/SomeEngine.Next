using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.Views.Generators;

public sealed partial class ViewGenerator
{
    private static void GenerateContentViews(StringBuilder text, IMethodSymbol method)
    {
        foreach (IParameterSymbol parameter in method.Parameters.Where(ViewCalls.IsContentParameter))
        {
            IMethodSymbol call = ((INamedTypeSymbol)parameter.Type).DelegateInvokeMethod!;
            string contentName = GetComposeViewName(method) + "_" + parameter.Name + "_Content";
            string contentType = "@" + contentName + GenerateTypeParameters(method.TypeParameters);
            string delegateName = "content";
            while (call.Parameters.Any(value => value.Name == delegateName))
                delegateName += "_";
            string keyName = "contentKey";
            while (call.Parameters.Any(value => value.Name == keyName) || keyName == delegateName)
                keyName += "_";
            string delegateParameter = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + " @" + delegateName;
            string parameters = GenerateParameters(call.Parameters);
            string arguments = GenerateArguments(call.Parameters);
            var callbacks = call.Parameters.Where(parameter => IsCallback(parameter.Type)).ToArray();
            string separator = call.Parameters.Length > 0 ? ", " : "";
            text.Append("    private readonly record struct ").Append(contentType).Append('(')
                .Append(delegateParameter).Append(separator).Append(parameters).Append(')')
                .Append(" : global::SomeEngine.Views.IView, global::SomeEngine.ECS.Components.ISparseComponent")
                .AppendLine(GenerateConstraints(method.TypeParameters)).AppendLine("    {");
            GenerateParameterEquality(text, contentType,
                new[] { (delegateName, parameter.Type, true) }.Concat(call.Parameters.Select(value => (value.Name, value.Type, false))));
            text.AppendLine("        public global::SomeEngine.Views.BuildTask Build()\n        {");
            if (callbacks.Length > 0)
                text.Append("            var callbacks = new @").Append(contentName).Append("Callbacks")
                    .Append(GenerateTypeParameters(method.TypeParameters))
                    .AppendLine("(global::SomeEngine.Views.Scope.World, global::SomeEngine.Views.Scope.Entity);");
            text.Append(call.ReturnsVoid ? "            " : "            return ")
                .Append("this.@").Append(delegateName).Append(".Invoke(")
                .Append(string.Join(", ", call.Parameters.Select(parameter => IsCallback(parameter.Type)
                    ? GenerateCallbackArgument(parameter.Name, parameter.Type) : "this.@" + parameter.Name))).AppendLine(");");
            if (call.ReturnsVoid)
                text.AppendLine("            return default;");
            text.AppendLine("        }\n    }");
            if (callbacks.Length > 0)
                GenerateCallbackFunctions(text, contentName, method, callbacks.Select(parameter => (parameter.Name, parameter.Type)));
            text.Append("    private static ").Append(call.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .Append(" @Declare").Append(contentName).Append(GenerateTypeParameters(method.TypeParameters))
                .Append("(ulong @").Append(keyName).Append(", ").Append(delegateParameter).Append(separator).Append(parameters).Append(')')
                .AppendLine(GenerateConstraints(method.TypeParameters)).AppendLine("    {")
                .Append("        global::SomeEngine.Views.Scope.View(new ").Append(contentType).Append('(')
                .Append('@').Append(delegateName).Append(separator).Append(arguments).Append("), @").Append(keyName).AppendLine(");");
            if (!call.ReturnsVoid)
                text.AppendLine("        return default;");
            text.AppendLine("    }");
        }
    }

    private static void GenerateParameterEquality(StringBuilder text, string type,
        IEnumerable<(string Name, ITypeSymbol Type, bool Content)> parameters)
    {
        var fields = parameters.Where(field => field.Content || !IsCallback(field.Type)).ToArray();
        text.Append("        public bool Equals(").Append(type).Append(" other) => ")
            .Append(fields.Length == 0 ? "true" : string.Join(" && ", fields.Select(field => UsesReferenceEquality(field.Type, field.Content)
                ? "global::System.Object.ReferenceEquals(this.@" + field.Name + ", other.@" + field.Name + ")"
                : "global::System.Collections.Generic.EqualityComparer<" + field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) +
                    ">.Default.Equals(this.@" + field.Name + ", other.@" + field.Name + ")")))
            .AppendLine(";")
            .AppendLine("        public override int GetHashCode()\n        {\n            var hash = new global::System.HashCode();");
        foreach (var field in fields)
            text.Append("            hash.Add(").Append(UsesReferenceEquality(field.Type, field.Content)
                ? "this.@" + field.Name + " is null ? 0 : global::System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this.@" + field.Name + ")"
                : "this.@" + field.Name).AppendLine(");");
        text.AppendLine("            return hash.ToHashCode();\n        }");
    }

    private static void GenerateContentBody(SourceProductionContext output, Compilation compilation,
        CSharpParseOptions options, List<SyntaxTree> declarations, IMethodSymbol method)
    {
        var source = (MethodDeclarationSyntax)method.DeclaringSyntaxReferences[0].GetSyntax(output.CancellationToken);
        SemanticModel model = compilation.GetSemanticModel(source.SyntaxTree);
        var body = (MethodDeclarationSyntax)new ContentCalls(model, method).Visit(source)!;
        body = body.WithIdentifier(SyntaxFactory.Identifier("Build" + GetComposeViewName(method) + "Content"))
            .WithAttributeLists(SyntaxFactory.SingletonList(SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Attribute(SyntaxFactory.ParseName("global::SomeEngine.Views.Compose.Composable"))))))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space)).AddRange(source.Modifiers.Where(value =>
                    value.IsKind(SyntaxKind.AsyncKeyword) || value.IsKind(SyntaxKind.UnsafeKeyword))));
        MemberDeclarationSyntax member = body;
        foreach (SyntaxNode ancestor in source.Ancestors())
        {
            if (ancestor is TypeDeclarationSyntax type)
                member = type.WithMembers(SyntaxFactory.SingletonList(member)).WithAttributeLists(default)
                    .WithBaseList(null).WithParameterList(null);
            else if (ancestor is BaseNamespaceDeclarationSyntax space)
                member = space.WithMembers(SyntaxFactory.SingletonList(member));
        }
        var root = ((CompilationUnitSyntax)source.SyntaxTree.GetRoot(output.CancellationToken))
            .WithMembers(SyntaxFactory.SingletonList(member)).WithAttributeLists(default);
        root = root.WithUsings(SyntaxFactory.List(root.Usings.Where(value => !value.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))));
        string file = GetFileName(method.ContainingType, GetComposeViewName(method) + ".ContentBody");
        root = root.WithLeadingTrivia(SyntaxFactory.ParseLeadingTrivia("// <auto-generated/>\n").AddRange(root.GetLeadingTrivia()));
        output.AddSource(file, Microsoft.CodeAnalysis.Text.SourceText.From(root.ToFullString(), Encoding.UTF8));
        declarations.Add(CSharpSyntaxTree.Create(root, options, file, Encoding.UTF8));
    }

    private sealed class ContentCalls(SemanticModel model, IMethodSymbol method) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var rewritten = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            SyntaxNode position = node.Expression is MemberAccessExpressionSyntax access ? access.Name : node.Expression;
            ulong key = GetCallKey(position.GetLocation().GetMappedLineSpan());
            rewritten = rewritten.WithAdditionalAnnotations(new SyntaxAnnotation("ViewCallKey",
                key.ToString(CultureInfo.InvariantCulture)));
            ExpressionSyntax receiver = node.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Invoke" } member
                ? member.Expression : node.Expression;
            if (model.GetSymbolInfo(receiver).Symbol is not IParameterSymbol parameter || !ViewCalls.IsContentParameter(parameter))
                return rewritten;
            string name = method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) +
                ".@Declare" + GetComposeViewName(method) + "_" + parameter.Name + "_Content" + GenerateTypeParameters(method.TypeParameters);
            var arguments = rewritten.ArgumentList.Arguments.Insert(0, SyntaxFactory.Argument(receiver.WithoutTrivia()))
                .Insert(0, SyntaxFactory.Argument(SyntaxFactory.ParseExpression(key.ToString(CultureInfo.InvariantCulture) + "UL")));
            return rewritten.WithExpression(SyntaxFactory.ParseExpression(name).WithTriviaFrom(rewritten.Expression))
                .WithArgumentList(rewritten.ArgumentList.WithArguments(arguments));
        }
    }
}
