using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.ECS.SourceGen;

internal static class JobGenerator
{
    private static readonly DiagnosticDescriptor DuplicateAccess = new("SECSSG102", "Job inputs alias the same data",
        "{0}", "ECS", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor ControlledWrite = new("SECSSG103", "Controlled values require typed writes",
        "{0}", "ECS", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor UnsafeStorage = new("SECSSG110", "Job storage requires declared resources",
        "{0}", "ECS", DiagnosticSeverity.Error, true);
    private sealed class Input(IParameterSymbol parameter, ITypeSymbol type, string kind, bool write)
    {
        internal readonly IParameterSymbol Parameter = parameter;
        internal readonly ITypeSymbol Type = type;
        internal readonly string Kind = kind;
        internal readonly bool Write = write;
        internal INamedTypeSymbol? Resources;
    }

    internal static void Emit(SourceProductionContext output, INamedTypeSymbol type, bool entity, Compilation compilation)
    {
        IMethodSymbol[] methods = type.GetMembers("Execute").OfType<IMethodSymbol>().Where(method => !method.IsImplicitlyDeclared).ToArray();
        if (methods.Length != 1 || !methods[0].ReturnsVoid || methods[0].IsAsync || methods[0].TypeParameters.Length != 0)
        { EcsGenerator.Error(output, type, "Provide one synchronous void Execute method for the generated job."); return; }
        IMethodSymbol execute = methods[0];
        if (WritesInstanceState(execute, type, compilation))
        { EcsGenerator.Error(output, execute, "Keep job instance fields as inputs; write results through declared components or resources.", true); return; }
        var inputs = new List<Input>();
        foreach (IParameterSymbol parameter in execute.Parameters)
        {
            if (entity && parameter.Type.ToDisplayString() == "SomeEngine.ECS.Entities.Entity" && parameter.RefKind != RefKind.Ref && parameter.RefKind != RefKind.Out)
            { inputs.Add(new(parameter, parameter.Type, "entity", false)); continue; }
            if (parameter.Type is INamedTypeSymbol named && named.TypeArguments.Length == 1)
            {
                string full = named.OriginalDefinition.ToDisplayString();
                if (!entity && full == "System.ReadOnlySpan<T>" && named.TypeArguments[0].ToDisplayString() == "SomeEngine.ECS.Entities.Entity")
                { inputs.Add(new(parameter, named.TypeArguments[0], "entities", false)); continue; }
                if (entity && (full == "SomeEngine.ECS.DynamicBuffer<T>" || full == "SomeEngine.ECS.ReadOnlyBuffer<T>"))
                {
                    if (parameter.RefKind != RefKind.None) { EcsGenerator.Error(output, parameter, "Pass buffer views by value."); return; }
                    inputs.Add(new(parameter, named.TypeArguments[0], "buffer", named.Name == "DynamicBuffer")); continue;
                }
                if (!entity && (full == "System.Span<T>" || full == "System.ReadOnlySpan<T>" || full == "SomeEngine.ECS.SharedReadOnlySpan<T>"))
                { inputs.Add(new(parameter, named.TypeArguments[0], full.Contains("SharedReadOnlySpan") ? "shared" : "column", named.Name == "Span")); continue; }
            }
            if (entity && parameter.Type.IsValueType && parameter.RefKind != RefKind.Out)
            { inputs.Add(new(parameter, parameter.Type, Shared(parameter.Type) ? "shared" : Sparse(parameter.Type) ? "sparse" : "column", parameter.RefKind == RefKind.Ref)); continue; }
            EcsGenerator.Error(output, parameter, entity ? "Entity jobs accept Entity, in/ref component values and buffer views."
                : "Chunk jobs accept component Span, ReadOnlySpan and SharedReadOnlySpan parameters."); return;
        }
        foreach (Input input in inputs)
        {
            AttributeData? resourceAttribute = input.Parameter.GetAttributes().FirstOrDefault(attribute =>
                attribute.AttributeClass?.Name == "JobResourcesAttribute"
                && attribute.AttributeClass.ContainingNamespace.ToDisplayString() == "SomeEngine.ECS.Systems");
            if (resourceAttribute is not null)
            {
                INamedTypeSymbol? descriptor = resourceAttribute.AttributeClass!.TypeArguments.FirstOrDefault() as INamedTypeSymbol
                    ?? resourceAttribute.ConstructorArguments.FirstOrDefault().Value as INamedTypeSymbol;
                if (descriptor?.IsUnboundGenericType == true && input.Type is INamedTypeSymbol component
                    && descriptor.Arity == component.TypeArguments.Length)
                    descriptor = descriptor.ConstructedFrom.Construct(component.TypeArguments.ToArray());
                bool matches = descriptor?.AllInterfaces.Any(value => value.OriginalDefinition.ToDisplayString()
                    == "SomeEngine.ECS.Systems.IJobComponentResources<TComponent>"
                    && SymbolEqualityComparer.Default.Equals(value.TypeArguments[0], input.Type)) == true;
                if (input.Kind != "column" || descriptor?.IsValueType != true || !matches)
                { EcsGenerator.Error(output, input.Parameter, "Select a struct descriptor implementing IJobComponentResources for this component."); return; }
                input.Resources = descriptor;
            }
            if (input.Write && input.Kind != "buffer" && Controlled(input.Type))
            {
                output.ReportDiagnostic(Diagnostic.Create(ControlledWrite, input.Parameter.Locations.FirstOrDefault(),
                    "Write shared, indexed and controlled values through their typed structural entry."));
                return;
            }
            if (!entity && input.Kind == "column" && Shared(input.Type))
            { EcsGenerator.Error(output, input.Parameter, "Read shared columns through SharedReadOnlySpan."); return; }
        }

        var storageTypes = new JobStorageTypeAnalysis(compilation);
        for (int index = 0; index < inputs.Count; index++)
        {
            Input input = inputs[index];
            for (int previous = 0; previous < index; previous++)
                if (SymbolEqualityComparer.Default.Equals(input.Type, inputs[previous].Type)
                    && (input.Kind == "entity" && inputs[previous].Kind == "entity"
                        || input.Kind == inputs[previous].Kind && (input.Write || inputs[previous].Write)))
                {
                    output.ReportDiagnostic(Diagnostic.Create(DuplicateAccess, input.Parameter.Locations.FirstOrDefault(),
                        "Declare one direct input for each entity identity or writable value."));
                    return;
                }
            if (input.Kind is "entity" or "entities" || input.Resources is not null) continue;
            if (!storageTypes.IsAliasFree(input.Type))
            {
                output.ReportDiagnostic(Diagnostic.Create(UnsafeStorage, input.Parameter.Locations.FirstOrDefault(),
                    "Declare a typed resource descriptor for values containing references or native handles."));
                return;
            }
        }

        AttributeData[] additional = type.GetAttributes().Where(attribute => attribute.AttributeClass?.ToDisplayString()
            is "SomeEngine.ECS.Systems.JobReadAttribute" or "SomeEngine.ECS.Systems.JobWriteAttribute").ToArray();
        bool collectsAdditional = type.GetMembers().OfType<IMethodSymbol>().Any(method => !method.IsStatic
            && method.Name.EndsWith("CollectAdditionalResources", StringComparison.Ordinal));
        bool declaresAdditional = type.GetMembers().OfType<IPropertySymbol>().Any(property => property.IsStatic
            && property.Name.EndsWith("AdditionalAccesses", StringComparison.Ordinal));
        foreach (IFieldSymbol field in type.GetMembers().OfType<IFieldSymbol>())
            if (!field.IsStatic && field.Type.IsReferenceType && field.Type.SpecialType != SpecialType.System_String
                && additional.Length == 0 && !collectsAdditional && !declaresAdditional)
            { EcsGenerator.Error(output, field, "Declare captured reference access with JobRead or JobWrite."); return; }

        string self = EcsGenerator.Name(type);
        var code = new StringBuilder();
        code.Append("private static readonly global::SomeEngine.ECS.Systems.ChunkAccess[] __ecsComponents = new global::SomeEngine.ECS.Systems.ChunkAccess[] {\n");
        var components = new Dictionary<ITypeSymbol, (string Kind, bool Write, bool HasResources)>(SymbolEqualityComparer.Default);
        foreach (Input input in inputs.Where(value => value.Kind is not ("entity" or "entities" or "sparse")))
        {
            ITypeSymbol key = input.Type;
            if (components.TryGetValue(key, out var previous)) components[key] = (previous.Kind, previous.Write || input.Write,
                previous.HasResources || input.Resources is not null);
            else components.Add(key, (input.Kind, input.Write, input.Resources is not null));
        }
        foreach (var component in components)
            code.Append("global::SomeEngine.ECS.Systems.ChunkAccess.").Append(component.Value.Write ? "Write" : "Read")
                .Append(component.Value.Kind == "buffer" ? "Buffer" : "").Append('<').Append(EcsGenerator.Name(component.Key))
                .Append(component.Value.HasResources ? ">(hasResources: true),\n" : ">(),\n");
        code.Append("};\nstatic global::System.ReadOnlySpan<global::SomeEngine.ECS.Systems.ChunkAccess> global::SomeEngine.ECS.Systems.IChunkAction.Components => __ecsComponents;\n");
        if (inputs.Any(input => input.Resources is not null))
        {
            code.Append("static void global::SomeEngine.ECS.Systems.IChunkAction.CollectResources(global::SomeEngine.ECS.Systems.SystemChunk rows, ref global::SomeEngine.ECS.Systems.JobResourceAccessBuilder resources) {\n");
            var collected = new HashSet<string>();
            foreach (Input input in inputs)
                if (input.Resources is not null && collected.Add(EcsGenerator.Name(input.Type) + EcsGenerator.Name(input.Resources)))
                    code.Append("global::SomeEngine.ECS.Systems.ComponentResources.CollectRange<")
                        .Append(EcsGenerator.Name(input.Type)).Append(", ").Append(EcsGenerator.Name(input.Resources))
                        .Append(">(rows.Read<").Append(EcsGenerator.Name(input.Type)).Append(">(), ref resources);\n");
            code.Append("}\n");
        }
        if (additional.Length != 0 || inputs.Any(input => input.Kind == "sparse"))
        {
            code.Append("private static readonly global::SomeEngine.ECS.Systems.SystemAccesses __ecsAdditional = new global::SomeEngine.ECS.Systems.SystemAccesses()");
            foreach (Input input in inputs.Where(input => input.Kind == "sparse"))
                code.Append(input.Write ? ".WriteSparse<" : ".ReadSparse<").Append(EcsGenerator.Name(input.Type)).Append(">()");
            foreach (AttributeData attribute in additional)
            {
                if (attribute.ConstructorArguments[0].Value is not ITypeSymbol value) continue;
                int storage = attribute.ConstructorArguments.Length > 1 && attribute.ConstructorArguments[1].Value is int location ? location : 0;
                bool write = attribute.AttributeClass!.Name == "JobWriteAttribute";
                if (write && storage == 0 && Controlled(value))
                { EcsGenerator.Error(output, type, "Record random structural writes in the command producer."); return; }
                code.Append('.').Append(write ? "Write" : "Read").Append(storage == 1 ? "Sparse" : storage == 2 ? "Resource" : "Random")
                    .Append('<').Append(EcsGenerator.Name(value)).Append(">()");
            }
            code.Append(";\nstatic global::SomeEngine.ECS.Systems.SystemAccesses? global::SomeEngine.ECS.Systems.IChunkAction.AdditionalAccesses => __ecsAdditional;\n");
        }
        code.Append("void global::SomeEngine.ECS.Systems.IChunkAction.Execute(global::SomeEngine.ECS.Systems.SystemChunk __rows) {\n");
        if (inputs.Any(value => value.Kind is "entity" or "entities")) code.Append("var __entities = __rows.Entities;\n");
        for (int i = 0; i < inputs.Count; i++)
        {
            Input input = inputs[i];
            if (input.Kind is "entity" or "entities" or "buffer" or "sparse") continue;
            code.Append("var __column").Append(i).Append(" = __rows.")
                .Append(input.Kind == "shared" ? "ReadShared" : input.Write ? "Write" : "Read")
                .Append('<').Append(EcsGenerator.Name(input.Type)).Append(">();\n");
        }
        if (entity) code.Append("for (int __row = 0; __row < __rows.Count; __row++) {\n");
        foreach (Input input in inputs.Where(input => input.Kind == "sparse"))
            code.Append("if (!__rows.HasSparse<").Append(EcsGenerator.Name(input.Type)).Append(">(__row)) continue;\n");
        for (int i = 0; i < inputs.Count; i++)
            if (inputs[i].Kind == "buffer") code.Append("using var __buffer").Append(i).Append(" = __rows.")
                .Append(inputs[i].Write ? "GetBuffer" : "ReadBuffer").Append('<').Append(EcsGenerator.Name(inputs[i].Type)).Append(">(__row);\n");
        code.Append(execute.IsStatic ? self : "this").Append(".Execute(");
        for (int i = 0; i < inputs.Count; i++)
        {
            if (i != 0) code.Append(", "); Input input = inputs[i];
            if (input.Parameter.RefKind == RefKind.Ref) code.Append("ref ");
            else if (input.Parameter.RefKind is RefKind.In or RefKind.RefReadOnlyParameter) code.Append("in ");
            code.Append(input.Kind == "entity" ? "__entities[__row]" : input.Kind == "entities" ? "__entities" : input.Kind == "buffer" ? "__buffer" + i
                : input.Kind == "sparse" ? "__rows." + (input.Write ? "GetSparse" : "ReadSparse") + "<" + EcsGenerator.Name(input.Type) + ">(__row)"
                : "__column" + i + (entity ? "[__row]" : ""));
        }
        code.Append(");\n"); if (entity) code.Append("}\n"); code.Append("}\n");
        code.Append("public void Execute(global::SomeEngine.ECS.World world, global::SomeEngine.Job.JobHandle dependency = default, global::SomeEngine.ECS.Query? query = null) { dependency.Complete(); global::SomeEngine.ECS.Systems.SystemRunner.Execute<")
            .Append(self).Append(">(world, this, query); }\n");
        code.Append("public void ExecuteParallel(global::SomeEngine.ECS.World world, global::SomeEngine.Job.JobHandle dependency = default, global::SomeEngine.ECS.Query? query = null) { dependency.Complete(); new global::SomeEngine.ECS.Systems.SystemRunner(world).ExecuteParallel<")
            .Append(self).Append(">(this, query); }\n");
        foreach (bool parallel in new[] { false, true })
            code.Append("public global::SomeEngine.Job.JobHandle ").Append(parallel ? "ScheduleParallel" : "Schedule")
                .Append("(global::SomeEngine.ECS.World world, global::SomeEngine.Job.JobHandle dependency = default, global::SomeEngine.ECS.Query? query = null) => new global::SomeEngine.ECS.Systems.SystemRunner(world).Schedule<")
                .Append(self).Append(">(this, dependency, ").Append(parallel ? "true" : "false").Append(", query);\n");
        EcsGenerator.Add(output, type, "Job", EcsGenerator.Wrap(type, "global::SomeEngine.ECS.Systems.IChunkAction", code.ToString()));
    }

    private static bool Shared(ITypeSymbol type) => EcsGenerator.HasInterface(type, "SomeEngine.ECS.ISharedComponent");
    private static bool Sparse(ITypeSymbol type) => EcsGenerator.HasInterface(type, "SomeEngine.ECS.Components.ISparseComponent");
    private static bool Controlled(ITypeSymbol type)
        => Shared(type) || type is INamedTypeSymbol named && named.AllInterfaces.Any(value => value.Name
            is "IControlledComponent" or "IComputedComponent" or "IGroupComponent" or "IIndexedComponent");

    private static bool WritesInstanceState(IMethodSymbol method, INamedTypeSymbol type, Compilation compilation)
    {
        foreach (SyntaxReference reference in method.DeclaringSyntaxReferences)
        {
            SyntaxNode syntax = reference.GetSyntax();
            SemanticModel model = compilation.GetSemanticModel(syntax.SyntaxTree);
            foreach (SyntaxNode node in syntax.DescendantNodes())
            {
                ExpressionSyntax? target = node is AssignmentExpressionSyntax assign ? assign.Left
                    : node is PrefixUnaryExpressionSyntax prefix && (prefix.OperatorToken.Text == "++" || prefix.OperatorToken.Text == "--") ? prefix.Operand
                    : node is PostfixUnaryExpressionSyntax postfix && (postfix.OperatorToken.Text == "++" || postfix.OperatorToken.Text == "--") ? postfix.Operand : null;
                for (ExpressionSyntax? value = target; value is not null;)
                {
                    ISymbol? symbol = model.GetSymbolInfo(value).Symbol;
                    if (symbol is IFieldSymbol field && !field.IsStatic && SymbolEqualityComparer.Default.Equals(field.ContainingType, type)
                        || symbol is IPropertySymbol property && !property.IsStatic && SymbolEqualityComparer.Default.Equals(property.ContainingType, type)) return true;
                    value = value is MemberAccessExpressionSyntax member ? member.Expression
                        : value is ElementAccessExpressionSyntax element ? element.Expression : null;
                    if (value is not null && model.GetTypeInfo(value).Type?.IsReferenceType == true) break;
                }
            }
        }
        return false;
    }
}
