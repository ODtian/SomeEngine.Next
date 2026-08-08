using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.ECS.SourceGen;

[Generator]
public sealed class JobEntityGenerator : IIncrementalGenerator
{
    private const string SystemsNamespace = "SomeEngine.ECS.Systems";
    private const string ComponentsNamespace = "SomeEngine.ECS.Components";
    private const string EcsNamespace = "SomeEngine.ECS";
    private const string EntitiesNamespace = "SomeEngine.ECS.Entities";

    private static readonly DiagnosticDescriptor MissingExecute = Create(
        "SECSSG100",
        "IJobEntity requires one Execute method",
        "Job entity '{0}' must declare exactly one non-generic instance Execute method returning void");

    private static readonly DiagnosticDescriptor UnsupportedParameter = Create(
        "SECSSG101",
        "IJobEntity Execute parameter is unsupported",
        "Parameter '{0}' on job entity '{1}' has unsupported shape '{2}'");

    private static readonly DiagnosticDescriptor DuplicateDirectAccess = Create(
        "SECSSG102",
        "IJobEntity direct aliases must be unique",
        "Parameter '{0}' on job entity '{1}' aliases component family '{2}' already used by another Execute parameter");

    private static readonly DiagnosticDescriptor DerivedWrite = Create(
        "SECSSG103",
        "Derived relationship components are read-only",
        "Parameter '{0}' on job entity '{1}' requests writable access to derived relationship component '{2}'");

    private static readonly DiagnosticDescriptor ManagedField = Create(
        "SECSSG104",
        "IJobEntity fields must be unmanaged by-value configuration",
        "Field '{0}' on job entity '{1}' is not unmanaged; indirect/reference aliases require a future declared lookup or certified external resource surface");

    private static readonly DiagnosticDescriptor NestedJob = Create(
        "SECSSG105",
        "Nested IJobEntity types are not supported",
        "Job entity '{0}' must be declared at namespace scope so its generated extension surface has stable accessibility");

    private static readonly DiagnosticDescriptor InaccessibleJob = Create(
        "SECSSG106",
        "IJobEntity must be accessible to generated code",
        "Job entity '{0}' and its Execute method must be public or internal");

    private static readonly DiagnosticDescriptor PacketLocalFieldMutation = new(
        id: "SECSSG107",
        title: "IJobEntity field mutation is packet-local",
        messageFormat: "Field '{0}' on job entity '{1}' is mutable; ScheduleParallel copies the job per packet and does not publish field write-back",
        category: "SomeEngine.ECS.SourceGen",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ParallelUnavailable = new(
        id: "SECSSG108",
        title: "IJobEntity is serial-only",
        messageFormat: "Job entity '{0}' is serial-only because '{1}'; generated Schedule and Execute remain available",
        category: "SomeEngine.ECS.SourceGen",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor RefLikeJob = Create(
        "SECSSG109",
        "IJobEntity must be an ordinary value type",
        "Job entity '{0}' is ref-like and cannot be copied into a scheduled packet owner");

    private static readonly DiagnosticDescriptor ManagedDirectStorage = Create(
        "SECSSG110",
        "IJobEntity direct storage must be alias-free unmanaged data",
        "Parameter '{0}' on job entity '{1}' accesses '{2}', which contains managed references, pointers, or external aliases; use a certified lookup/resource adapter instead");

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<ImmutableArray<INamedTypeSymbol>> jobs = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is StructDeclarationSyntax,
                static (syntaxContext, _) => GetJobSymbol(syntaxContext))
            .Where(static symbol => symbol is not null)
            .Select(static (symbol, _) => symbol!)
            .Collect();

        context.RegisterSourceOutput(jobs, static (sourceContext, symbols) => Execute(sourceContext, symbols));
    }

    private static INamedTypeSymbol? GetJobSymbol(GeneratorSyntaxContext context)
    {
        var syntax = (StructDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(syntax) is not INamedTypeSymbol symbol)
            return null;
        return Implements(symbol, SystemsNamespace, "IJobEntity") ? symbol : null;
    }

    private static void Execute(
        SourceProductionContext context,
        ImmutableArray<INamedTypeSymbol> symbols)
    {
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (INamedTypeSymbol symbol in symbols)
        {
            if (!seen.Add(symbol))
                continue;
            JobModel? model = BuildModel(context, symbol);
            if (model is null)
                continue;
            context.AddSource(model.HintName, Generate(model));
        }
    }

    private static JobModel? BuildModel(SourceProductionContext context, INamedTypeSymbol job)
    {
        bool valid = true;
        if (job.IsRefLikeType)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                RefLikeJob,
                job.Locations.FirstOrDefault(),
                job.ToDisplayString()));
            valid = false;
        }
        if (job.ContainingType is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                NestedJob,
                job.Locations.FirstOrDefault(),
                job.ToDisplayString()));
            valid = false;
        }
        if (!IsGeneratedAccessible(job.DeclaredAccessibility))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InaccessibleJob,
                job.Locations.FirstOrDefault(),
                job.ToDisplayString()));
            valid = false;
        }

        IMethodSymbol[] methods = job.GetMembers("Execute")
            .OfType<IMethodSymbol>()
            .Where(static method => !method.IsStatic &&
                                    method.MethodKind == MethodKind.Ordinary &&
                                    method.Arity == 0 &&
                                    !method.IsAsync &&
                                    method.ReturnsVoid)
            .ToArray();
        if (methods.Length != 1)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MissingExecute,
                job.Locations.FirstOrDefault(),
                job.ToDisplayString()));
            return null;
        }

        IMethodSymbol execute = methods[0];
        if (!IsGeneratedAccessible(execute.DeclaredAccessibility))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InaccessibleJob,
                execute.Locations.FirstOrDefault(),
                job.ToDisplayString()));
            valid = false;
        }

        foreach (IFieldSymbol field in job.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst)
                continue;
            ISymbol diagnosticMember = field.AssociatedSymbol ?? field;
            if (!IsSafeDirectType(field.Type))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    ManagedField,
                    diagnosticMember.Locations.FirstOrDefault(),
                    diagnosticMember.Name,
                    job.ToDisplayString()));
                valid = false;
            }
            else if (!field.IsReadOnly && !field.IsImplicitlyDeclared)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    PacketLocalFieldMutation,
                    field.Locations.FirstOrDefault(),
                    field.Name,
                    job.ToDisplayString()));
            }
        }

        var parameters = new List<ParameterModel>();
        var aliases = new HashSet<(ParameterKind Kind, ITypeSymbol Type)>(ParameterAliasComparer.Instance);
        bool supportsParallel = true;
        string? serialReason = null;
        foreach (IParameterSymbol parameter in execute.Parameters)
        {
            ParameterModel? parameterModel = Classify(parameter);
            if (parameterModel is null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedParameter,
                    parameter.Locations.FirstOrDefault(),
                    parameter.Name,
                    job.ToDisplayString(),
                    parameter.ToDisplayString()));
                valid = false;
                continue;
            }

            ParameterModel model = parameterModel;
            if (!aliases.Add((model.AliasKind, model.ValueType)))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DuplicateDirectAccess,
                    parameter.Locations.FirstOrDefault(),
                    parameter.Name,
                    job.ToDisplayString(),
                    model.ValueType.ToDisplayString()));
                valid = false;
            }

            if (model.Writable && Implements(model.ValueType, ComponentsNamespace, "IRelationshipTarget"))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DerivedWrite,
                    parameter.Locations.FirstOrDefault(),
                    parameter.Name,
                    job.ToDisplayString(),
                    model.ValueType.ToDisplayString()));
                valid = false;
            }

            if (model.Writable && Implements(model.ValueType, ComponentsNamespace, "IRelationshipSource"))
            {
                supportsParallel = false;
                serialReason ??= $"{model.ValueType.Name} is a canonical relationship source";
            }
            if (model.Kind != ParameterKind.Entity && !IsSafeDirectType(model.ValueType))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    ManagedDirectStorage,
                    parameter.Locations.FirstOrDefault(),
                    parameter.Name,
                    job.ToDisplayString(),
                    model.ValueType.ToDisplayString()));
                valid = false;
            }
            parameters.Add(model);
        }

        if (!supportsParallel)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                ParallelUnavailable,
                job.Locations.FirstOrDefault(),
                job.ToDisplayString(),
                serialReason ?? "its direct access set has no packet alias proof"));
        }

        if (!valid)
            return null;

        string metadataName = job.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        uint hash = StableHash(metadataName);
        return new JobModel(
            job,
            parameters,
            supportsParallel,
            $"SomeEngine.JobEntity.{Sanitize(job.Name)}.{hash:x8}.g.cs",
            $"__SomeEngineJobEntity_{Sanitize(job.Name)}_{hash:x8}");
    }

    private static ParameterModel? Classify(IParameterSymbol parameter)
    {
        if (IsType(parameter.Type, EntitiesNamespace, "Entity") && parameter.RefKind == RefKind.None)
            return new ParameterModel(ParameterKind.Entity, parameter.Type, writable: false);

        if (Implements(parameter.Type, ComponentsNamespace, "ISparseComponent"))
        {
            if (parameter.RefKind == RefKind.In)
                return new ParameterModel(ParameterKind.SparseRead, parameter.Type, writable: false);
            if (parameter.RefKind == RefKind.Ref)
                return new ParameterModel(ParameterKind.SparseReadWrite, parameter.Type, writable: true);
            return null;
        }

        if (Implements(parameter.Type, EcsNamespace, "IComponent"))
        {
            if (parameter.RefKind == RefKind.In)
                return new ParameterModel(ParameterKind.TableRead, parameter.Type, writable: false);
            if (parameter.RefKind == RefKind.Ref)
                return new ParameterModel(ParameterKind.TableReadWrite, parameter.Type, writable: true);
            return null;
        }

        if (parameter.Type is not INamedTypeSymbol named)
            return null;

        if (TryWrapper(named, EcsNamespace, "BufferView", ComponentsNamespace, "IBufferElement", out ITypeSymbol? bufferRead) &&
            parameter.RefKind == RefKind.None)
        {
            return new ParameterModel(ParameterKind.BufferRead, bufferRead!, writable: false);
        }
        if (TryWrapper(named, EcsNamespace, "DynamicBuffer", ComponentsNamespace, "IBufferElement", out ITypeSymbol? bufferWrite) &&
            parameter.RefKind == RefKind.None)
        {
            return new ParameterModel(ParameterKind.BufferReadWrite, bufferWrite!, writable: true);
        }
        return null;
    }

    private static bool TryWrapper(
        INamedTypeSymbol type,
        string @namespace,
        string name,
        string constraintNamespace,
        string constraintName,
        out ITypeSymbol? valueType)
    {
        if (type.IsGenericType &&
            type.Name == name &&
            type.ContainingNamespace.ToDisplayString() == @namespace &&
            type.TypeArguments.Length == 1 &&
            Implements(type.TypeArguments[0], constraintNamespace, constraintName))
        {
            valueType = type.TypeArguments[0];
            return true;
        }
        valueType = null;
        return false;
    }

    private static string Generate(JobModel model)
    {
        string jobType = model.Job.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string methodTypeParameters = TypeParameterList(model.Job);
        string constraints = ConstraintClauses(model.Job);
        string adapterType = $"Adapter{methodTypeParameters}";
        string cacheType = $"Cache{methodTypeParameters}";

        string queryBuilder = "new global::SomeEngine.ECS.Queries.QueryDefinitionBuilder()";
        foreach (ParameterModel parameter in model.Parameters)
        {
            string type = parameter.TypeName;
            queryBuilder += parameter.Kind switch
            {
                ParameterKind.TableRead => $".Read<{type}>()",
                ParameterKind.TableReadWrite => $".ReadWrite<{type}>()",
                ParameterKind.BufferRead => $".ReadBuffer<{type}>()",
                ParameterKind.BufferReadWrite => $".WriteBuffer<{type}>()",
                _ => string.Empty,
            };
        }
        queryBuilder += ".Build()";

        string[] accesses = model.Parameters
            .Where(static parameter => parameter.Kind != ParameterKind.Entity)
            .Select(static parameter => parameter.AccessExpression)
            .ToArray();
        string descriptorArguments = accesses.Length == 0
            ? queryBuilder
            : queryBuilder + ",\n                " + string.Join(",\n                ", accesses);

        var locals = new List<string>();
        var arguments = new List<string>();
        var sparseChecks = new List<string>();
        for (int i = 0; i < model.Parameters.Count; i++)
        {
            ParameterModel parameter = model.Parameters[i];
            string local = $"__value{i}";
            switch (parameter.Kind)
            {
                case ParameterKind.Entity:
                    arguments.Add("row.Entity");
                    break;
                case ParameterKind.TableRead:
                    locals.Add($"            ref readonly {parameter.TypeName} {local} = ref row.Read<{parameter.TypeName}>();");
                    arguments.Add($"in {local}");
                    break;
                case ParameterKind.TableReadWrite:
                    locals.Add($"            ref {parameter.TypeName} {local} = ref row.ReadWrite<{parameter.TypeName}>();");
                    arguments.Add($"ref {local}");
                    break;
                case ParameterKind.BufferRead:
                    locals.Add($"            var {local} = row.ReadBuffer<{parameter.TypeName}>();");
                    arguments.Add(local);
                    break;
                case ParameterKind.BufferReadWrite:
                    locals.Add($"            var {local} = row.ReadWriteBuffer<{parameter.TypeName}>();");
                    arguments.Add(local);
                    break;
                case ParameterKind.SparseRead:
                    sparseChecks.Add($"            if (!row.HasSparse<{parameter.TypeName}>()) return;");
                    locals.Add($"            ref readonly {parameter.TypeName} {local} = ref row.ReadSparse<{parameter.TypeName}>();");
                    arguments.Add($"in {local}");
                    break;
                case ParameterKind.SparseReadWrite:
                    sparseChecks.Add($"            if (!row.HasSparse<{parameter.TypeName}>()) return;");
                    locals.Add($"            ref {parameter.TypeName} {local} = ref row.ReadWriteSparse<{parameter.TypeName}>();");
                    arguments.Add($"ref {local}");
                    break;
            }
        }

        string parallelMethods = model.SupportsParallel
            ? $$"""

    public static global::SomeEngine.Job.JobHandle ScheduleParallel{{methodTypeParameters}}(
        this in {{jobType}} job,
        global::SomeEngine.ECS.World world,
        global::SomeEngine.Job.JobHandle dependency = default)
{{constraints}}
        => global::SomeEngine.ECS.Systems.JobEntityRuntime.ScheduleParallel(
            world, in job, default({{adapterType}}), {{cacheType}}.Descriptor, default, dependency);

    public static global::SomeEngine.Job.JobHandle ScheduleParallel{{methodTypeParameters}}(
        this in {{jobType}} job,
        global::SomeEngine.ECS.World world,
        global::SomeEngine.ECS.Systems.JobEntityScheduleOptions options,
        global::SomeEngine.Job.JobHandle dependency = default)
{{constraints}}
        => global::SomeEngine.ECS.Systems.JobEntityRuntime.ScheduleParallel(
            world, in job, default({{adapterType}}), {{cacheType}}.Descriptor, options, dependency);
"""
            : string.Empty;

        string namespaceOpen = model.Job.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : $"namespace {model.Job.ContainingNamespace.ToDisplayString()}\n{{\n";
        string namespaceClose = model.Job.ContainingNamespace.IsGlobalNamespace ? string.Empty : "}\n";
        string extensionVisibility = model.Job.DeclaredAccessibility == Accessibility.Public
            ? "public"
            : "internal";

        return $$"""
// <auto-generated />
#nullable enable
{{namespaceOpen}}[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
{{extensionVisibility}} static class {{model.ExtensionClass}}
{
    public static global::SomeEngine.ECS.Systems.GeneratedQueryAccessDescriptor GetGeneratedQueryAccess{{methodTypeParameters}}(
        this in {{jobType}} job)
{{constraints}}
        => {{cacheType}}.Descriptor;

    public static global::SomeEngine.Job.JobHandle Schedule{{methodTypeParameters}}(
        this in {{jobType}} job,
        global::SomeEngine.ECS.World world,
        global::SomeEngine.Job.JobHandle dependency = default)
{{constraints}}
        => global::SomeEngine.ECS.Systems.JobEntityRuntime.Schedule(
            world, in job, default({{adapterType}}), {{cacheType}}.Descriptor, default, dependency);

    public static global::SomeEngine.Job.JobHandle Schedule{{methodTypeParameters}}(
        this in {{jobType}} job,
        global::SomeEngine.ECS.World world,
        global::SomeEngine.ECS.Systems.JobEntityScheduleOptions options,
        global::SomeEngine.Job.JobHandle dependency = default)
{{constraints}}
        => global::SomeEngine.ECS.Systems.JobEntityRuntime.Schedule(
            world, in job, default({{adapterType}}), {{cacheType}}.Descriptor, options, dependency);

    public static void Execute{{methodTypeParameters}}(
        this in {{jobType}} job,
        global::SomeEngine.ECS.World world,
        global::SomeEngine.Job.JobHandle dependency = default)
{{constraints}}
        => global::SomeEngine.ECS.Systems.JobEntityRuntime.Execute(
            world, in job, default({{adapterType}}), {{cacheType}}.Descriptor, default, dependency);

    public static void Execute{{methodTypeParameters}}(
        this in {{jobType}} job,
        global::SomeEngine.ECS.World world,
        global::SomeEngine.ECS.Systems.JobEntityScheduleOptions options,
        global::SomeEngine.Job.JobHandle dependency = default)
{{constraints}}
        => global::SomeEngine.ECS.Systems.JobEntityRuntime.Execute(
            world, in job, default({{adapterType}}), {{cacheType}}.Descriptor, options, dependency);
{{parallelMethods}}
    [global::System.Runtime.CompilerServices.UnsafeAccessor(
        global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticMethod,
        Name = "GeneratedTable")]
    private static extern global::SomeEngine.ECS.Systems.GeneratedQueryAccess CreateGeneratedTableAccess<T>(
        global::SomeEngine.ECS.Systems.GeneratedQueryAccess declaringType,
        global::SomeEngine.ECS.Systems.GeneratedQueryMode mode)
        where T : struct, global::SomeEngine.ECS.IComponent;

    [global::System.Runtime.CompilerServices.UnsafeAccessor(
        global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticMethod,
        Name = "GeneratedBuffer")]
    private static extern global::SomeEngine.ECS.Systems.GeneratedQueryAccess CreateGeneratedBufferAccess<T>(
        global::SomeEngine.ECS.Systems.GeneratedQueryAccess declaringType,
        global::SomeEngine.ECS.Systems.GeneratedQueryMode mode)
        where T : struct, global::SomeEngine.ECS.Components.IBufferElement;

    [global::System.Runtime.CompilerServices.UnsafeAccessor(
        global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticMethod,
        Name = "GeneratedSparse")]
    private static extern global::SomeEngine.ECS.Systems.GeneratedQueryAccess CreateGeneratedSparseAccess<T>(
        global::SomeEngine.ECS.Systems.GeneratedQueryAccess declaringType,
        global::SomeEngine.ECS.Systems.GeneratedQueryMode mode)
        where T : struct, global::SomeEngine.ECS.Components.ISparseComponent;

    private static class Cache{{methodTypeParameters}}
{{constraints}}
    {
        internal static readonly global::SomeEngine.ECS.Systems.GeneratedQueryAccessDescriptor Descriptor =
            new global::SomeEngine.ECS.Systems.GeneratedQueryAccessDescriptor(
                {{descriptorArguments}});
    }

    private readonly struct Adapter{{methodTypeParameters}} :
        global::SomeEngine.ECS.Systems.IGeneratedJobEntityAdapter<{{jobType}}>
{{constraints}}
    {
        public void Execute(
            ref {{jobType}} job,
            ref global::SomeEngine.ECS.Systems.JobEntityRow row)
        {
{{string.Join("\n", sparseChecks)}}
{{string.Join("\n", locals)}}
            job.Execute({{string.Join(", ", arguments)}});
        }
    }
}
{{namespaceClose}}
""";
    }

    private static string TypeParameterList(INamedTypeSymbol type) =>
        type.TypeParameters.Length == 0
            ? string.Empty
            : "<" + string.Join(", ", type.TypeParameters.Select(static parameter => parameter.Name)) + ">";

    private static string ConstraintClauses(INamedTypeSymbol type)
    {
        if (type.TypeParameters.Length == 0)
            return string.Empty;
        var clauses = new StringBuilder();
        foreach (ITypeParameterSymbol parameter in type.TypeParameters)
        {
            var constraints = new List<string>();
            if (parameter.HasUnmanagedTypeConstraint)
                constraints.Add("unmanaged");
            else if (parameter.HasValueTypeConstraint)
                constraints.Add("struct");
            else if (parameter.HasReferenceTypeConstraint)
                constraints.Add("class");
            else if (parameter.HasNotNullConstraint)
                constraints.Add("notnull");
            constraints.AddRange(parameter.ConstraintTypes.Select(
                static constraint => constraint.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            if (parameter.HasConstructorConstraint)
                constraints.Add("new()");
            if (constraints.Count != 0)
                clauses.Append("        where ").Append(parameter.Name).Append(" : ")
                    .Append(string.Join(", ", constraints)).AppendLine();
        }
        return clauses.ToString().TrimEnd();
    }

    private static bool IsType(ITypeSymbol type, string @namespace, string name) =>
        type.Name == name && type.ContainingNamespace.ToDisplayString() == @namespace;

    private static bool Implements(ITypeSymbol type, string @namespace, string name)
    {
        if (type is ITypeParameterSymbol parameter)
        {
            return parameter.ConstraintTypes.Any(constraint => Implements(constraint, @namespace, name));
        }
        if (type is not INamedTypeSymbol named)
            return false;
        if (IsType(named.OriginalDefinition, @namespace, name))
            return true;
        return named.AllInterfaces.Any(iface => IsType(iface.OriginalDefinition, @namespace, name));
    }

    private static bool IsGeneratedAccessible(Accessibility accessibility) =>
        accessibility is Accessibility.Public or Accessibility.Internal;

    private static bool IsSafeDirectType(ITypeSymbol type)
    {
        return IsSafeDirectType(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));
    }

    private static bool IsSafeDirectType(ITypeSymbol type, HashSet<ITypeSymbol> seen)
    {
        if (!type.IsUnmanagedType || type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer)
            return false;
        if (type.SpecialType is SpecialType.System_IntPtr or SpecialType.System_UIntPtr)
            return false;
        if (type is ITypeParameterSymbol)
            return true;
        if (type is not INamedTypeSymbol named || named.TypeKind == TypeKind.Enum || !seen.Add(type))
            return true;
        foreach (IFieldSymbol field in named.GetMembers().OfType<IFieldSymbol>())
        {
            // Auto-properties are represented by implicitly-declared instance backing fields.
            // They are part of the stored value just like explicitly-declared fields, so skipping
            // them would allow a native-sized handle (or another alias-bearing type) to receive a
            // generated alias certificate.
            if (field.IsStatic || field.IsConst)
                continue;
            if (field.IsFixedSizeBuffer || !IsSafeDirectType(field.Type, seen))
                return false;
        }
        return true;
    }

    private static DiagnosticDescriptor Create(string id, string title, string message) =>
        new(
            id,
            title,
            message,
            "SomeEngine.ECS.SourceGen",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

    private static uint StableHash(string value)
    {
        uint hash = 2166136261;
        for (int i = 0; i < value.Length; i++)
            hash = (hash ^ value[i]) * 16777619;
        return hash;
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
            builder.Append(char.IsLetterOrDigit(value[i]) ? value[i] : '_');
        return builder.ToString();
    }

    private enum ParameterKind
    {
        Entity,
        TableRead,
        TableReadWrite,
        BufferRead,
        BufferReadWrite,
        SparseRead,
        SparseReadWrite,
    }

    private sealed class ParameterModel
    {
        internal ParameterModel(
            ParameterKind kind,
            ITypeSymbol valueType,
            bool writable)
        {
            Kind = kind;
            ValueType = valueType;
            Writable = writable;
        }

        internal ParameterKind Kind { get; }
        internal ParameterKind AliasKind => Kind switch
        {
            ParameterKind.TableRead or ParameterKind.TableReadWrite => ParameterKind.TableRead,
            ParameterKind.BufferRead or ParameterKind.BufferReadWrite => ParameterKind.BufferRead,
            ParameterKind.SparseRead or ParameterKind.SparseReadWrite => ParameterKind.SparseRead,
            _ => Kind,
        };
        internal ITypeSymbol ValueType { get; }
        internal bool Writable { get; }
        internal bool HasStaticAliasProof => !ContainsTypeParameter(ValueType);
        internal string TypeName => ValueType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        internal string AccessExpression => Kind switch
        {
            ParameterKind.TableRead => HasStaticAliasProof
                ? $"CreateGeneratedTableAccess<{TypeName}>(default, global::SomeEngine.ECS.Systems.GeneratedQueryMode.Read)"
                : $"global::SomeEngine.ECS.Systems.GeneratedQueryAccess.Table<{TypeName}>(global::SomeEngine.ECS.Systems.GeneratedQueryMode.Read)",
            ParameterKind.TableReadWrite => HasStaticAliasProof
                ? $"CreateGeneratedTableAccess<{TypeName}>(default, global::SomeEngine.ECS.Systems.GeneratedQueryMode.ReadWrite)"
                : $"global::SomeEngine.ECS.Systems.GeneratedQueryAccess.Table<{TypeName}>(global::SomeEngine.ECS.Systems.GeneratedQueryMode.ReadWrite)",
            ParameterKind.BufferRead => HasStaticAliasProof
                ? $"CreateGeneratedBufferAccess<{TypeName}>(default, global::SomeEngine.ECS.Systems.GeneratedQueryMode.Read)"
                : $"global::SomeEngine.ECS.Systems.GeneratedQueryAccess.Buffer<{TypeName}>(global::SomeEngine.ECS.Systems.GeneratedQueryMode.Read)",
            ParameterKind.BufferReadWrite => HasStaticAliasProof
                ? $"CreateGeneratedBufferAccess<{TypeName}>(default, global::SomeEngine.ECS.Systems.GeneratedQueryMode.ReadWrite)"
                : $"global::SomeEngine.ECS.Systems.GeneratedQueryAccess.Buffer<{TypeName}>(global::SomeEngine.ECS.Systems.GeneratedQueryMode.ReadWrite)",
            ParameterKind.SparseRead => HasStaticAliasProof
                ? $"CreateGeneratedSparseAccess<{TypeName}>(default, global::SomeEngine.ECS.Systems.GeneratedQueryMode.Read)"
                : $"global::SomeEngine.ECS.Systems.GeneratedQueryAccess.Sparse<{TypeName}>(global::SomeEngine.ECS.Systems.GeneratedQueryMode.Read)",
            ParameterKind.SparseReadWrite => HasStaticAliasProof
                ? $"CreateGeneratedSparseAccess<{TypeName}>(default, global::SomeEngine.ECS.Systems.GeneratedQueryMode.ReadWrite)"
                : $"global::SomeEngine.ECS.Systems.GeneratedQueryAccess.Sparse<{TypeName}>(global::SomeEngine.ECS.Systems.GeneratedQueryMode.ReadWrite)",
            _ => throw new InvalidOperationException(),
        };
    }

    private static bool ContainsTypeParameter(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol)
            return true;
        if (type is IArrayTypeSymbol array)
            return ContainsTypeParameter(array.ElementType);
        if (type is IPointerTypeSymbol pointer)
            return ContainsTypeParameter(pointer.PointedAtType);
        if (type is not INamedTypeSymbol named)
            return false;

        for (int i = 0; i < named.TypeArguments.Length; i++)
        {
            if (ContainsTypeParameter(named.TypeArguments[i]))
                return true;
        }
        return false;
    }

    private sealed class ParameterAliasComparer : IEqualityComparer<(ParameterKind Kind, ITypeSymbol Type)>
    {
        internal static ParameterAliasComparer Instance { get; } = new();

        public bool Equals(
            (ParameterKind Kind, ITypeSymbol Type) x,
            (ParameterKind Kind, ITypeSymbol Type) y) =>
            x.Kind == y.Kind && SymbolEqualityComparer.Default.Equals(x.Type, y.Type);

        public int GetHashCode((ParameterKind Kind, ITypeSymbol Type) obj) =>
            unchecked(((int)obj.Kind * 397) ^ SymbolEqualityComparer.Default.GetHashCode(obj.Type));
    }

    private sealed class JobModel
    {
        internal JobModel(
            INamedTypeSymbol job,
            List<ParameterModel> parameters,
            bool supportsParallel,
            string hintName,
            string extensionClass)
        {
            Job = job;
            Parameters = parameters;
            SupportsParallel = supportsParallel;
            HintName = hintName;
            ExtensionClass = extensionClass;
        }

        internal INamedTypeSymbol Job { get; }
        internal List<ParameterModel> Parameters { get; }
        internal bool SupportsParallel { get; }
        internal string HintName { get; }
        internal string ExtensionClass { get; }
    }
}
