using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.ECS.SourceGen;

[Generator]
public sealed class SerializationGenerator : IIncrementalGenerator
{
    private const string ComponentsNamespace = "SomeEngine.ECS.Components";
    private const string SerializationAttributeName = "SomeEngine.ECS.Serialization.SerializableComponentAttribute";

    private static readonly DiagnosticDescriptor BadStableRule = new(
        id: "SECSSER001",
        title: "Serializable type stable id must be a valid GUID",
        messageFormat: "Serializable type '{0}' has invalid stable id '{1}'",
        category: "SomeEngine.ECS.SourceGen.Serialization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateStableRule = new(
        id: "SECSSER002",
        title: "Serializable stable id must be unique",
        messageFormat: "Serializable type '{0}' uses stable id '{1}', which is already used by '{2}'",
        category: "SomeEngine.ECS.SourceGen.Serialization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedFieldDiagnostic = new(
        id: "SECSSER003",
        title: "Serializable field shape is unsupported",
        messageFormat: "Field '{0}' in serializable type '{1}' uses unsupported type '{2}'",
        category: "SomeEngine.ECS.SourceGen.Serialization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InaccessibleFieldDiagnostic = new(
        id: "SECSSER004",
        title: "Serializable field must be accessible to generated code",
        messageFormat: "Field '{0}' in serializable type '{1}' must not be private or readonly",
        category: "SomeEngine.ECS.SourceGen.Serialization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DirectBackingDiagnostic = new(
        id: "SECSSER005",
        title: "Dynamic buffer backing components are not serializable values",
        messageFormat: "Serializable type '{0}' must not directly use dynamic-buffer backing type '{1}'",
        category: "SomeEngine.ECS.SourceGen.Serialization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<ImmutableArray<INamedTypeSymbol>> serializableTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is StructDeclarationSyntax { AttributeLists.Count: > 0 },
                static (syntaxContext, _) => GetSerializableSymbol(syntaxContext))
            .Where(static symbol => symbol is not null)
            .Select(static (symbol, _) => symbol!)
            .Collect();

        context.RegisterSourceOutput(serializableTypes, static (sourceContext, types) => Execute(sourceContext, types));
    }

    private static INamedTypeSymbol? GetSerializableSymbol(GeneratorSyntaxContext context)
    {
        var syntax = (StructDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(syntax) is not INamedTypeSymbol symbol)
            return null;

        return HasSerializableAttribute(symbol) ? symbol : null;
    }

    private static void Execute(SourceProductionContext context, ImmutableArray<INamedTypeSymbol> symbols)
    {
        var unique = new List<INamedTypeSymbol>();
        var seenSymbols = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var symbol in symbols)
        {
            if (seenSymbols.Add(symbol))
                unique.Add(symbol);
        }

        var models = new List<SerializableModel>();
        var bySymbol = new Dictionary<INamedTypeSymbol, SerializableModel>(SymbolEqualityComparer.Default);
        var byStableId = new Dictionary<Guid, SerializableModel>();

        foreach (var symbol in unique)
        {
            if (!TryStableId(symbol, context, out var stableId, out var stableIdText))
                continue;

            var model = new SerializableModel(symbol, stableId, stableIdText);
            if (byStableId.TryGetValue(stableId, out var existing))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DuplicateStableRule,
                    symbol.Locations.FirstOrDefault(),
                    symbol.ToDisplayString(),
                    stableId,
                    existing.Symbol.ToDisplayString()));
                continue;
            }

            byStableId.Add(stableId, model);
            bySymbol.Add(symbol, model);
            models.Add(model);
        }

        bool ok = true;
        foreach (var model in models)
        {
            if (!PopulateModel(model, bySymbol, context))
                ok = false;
        }

        if (!ok)
            return;

        RefreshReferenceFlags(models);

        if (models.Count == 0)
            return;

        context.AddSource("SomeEngine.ECS.Serialization.Module.g.cs", GenerateSource(models));
    }

    private static bool PopulateModel(
        SerializableModel model,
        IReadOnlyDictionary<INamedTypeSymbol, SerializableModel> modelsBySymbol,
        SourceProductionContext context)
    {
        model.Kind = GetSerializableKind(model.Symbol);
        if (model.Kind == SerializableKind.Unsupported)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                UnsupportedFieldDiagnostic,
                model.Symbol.Locations.FirstOrDefault(),
                model.Symbol.Name,
                model.Symbol.ToDisplayString(),
                model.Symbol.ToDisplayString()));
            return false;
        }

        bool ok = true;
        IOrderedEnumerable<IFieldSymbol> fields = model.Symbol.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static field => !field.IsStatic)
            .OrderBy(static field => field.Locations.FirstOrDefault()?.SourceSpan.Start ?? int.MaxValue);

        foreach (var field in fields)
        {
            if (field.DeclaredAccessibility == Accessibility.Private || field.IsReadOnly)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InaccessibleFieldDiagnostic,
                    field.Locations.FirstOrDefault(),
                    field.Name,
                    model.Symbol.ToDisplayString()));
                ok = false;
                continue;
            }

            if (IsBufferBacking(field.Type))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DirectBackingDiagnostic,
                    field.Locations.FirstOrDefault(),
                    model.Symbol.ToDisplayString(),
                    field.Type.ToDisplayString()));
                ok = false;
                continue;
            }

            FieldModel? fieldModel = CreateFieldModel(field, modelsBySymbol);
            if (fieldModel is null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedFieldDiagnostic,
                    field.Locations.FirstOrDefault(),
                    field.Name,
                    model.Symbol.ToDisplayString(),
                    field.Type.ToDisplayString()));
                ok = false;
                continue;
            }

            model.Fields.Add(fieldModel);
        }

        model.SchemaHash = ComputeSchemaHash(model);
        return ok;
    }

    private static void RefreshReferenceFlags(IReadOnlyList<SerializableModel> models)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var model in models)
            {
                bool contains = model.Fields.Any(static field => field.ContainsEntityReferences);
                if (model.ContainsEntityReferences == contains)
                    continue;

                model.ContainsEntityReferences = contains;
                changed = true;
            }
        }
        while (changed);
    }

    private static FieldModel? CreateFieldModel(
        IFieldSymbol field,
        IReadOnlyDictionary<INamedTypeSymbol, SerializableModel> modelsBySymbol)
    {
        ITypeSymbol type = field.Type;
        if (type.SpecialType == SpecialType.System_Boolean)
            return new FieldModel(field, FieldKind.Boolean);
        if (type.SpecialType == SpecialType.System_Byte)
            return new FieldModel(field, FieldKind.Byte);
        if (type.SpecialType == SpecialType.System_Int32)
            return new FieldModel(field, FieldKind.Int32);
        if (type.SpecialType == SpecialType.System_UInt32)
            return new FieldModel(field, FieldKind.UInt32);
        if (type.SpecialType == SpecialType.System_Int64)
            return new FieldModel(field, FieldKind.Int64);
        if (type.SpecialType == SpecialType.System_Single)
            return new FieldModel(field, FieldKind.Single);
        if (type.SpecialType == SpecialType.System_Double)
            return new FieldModel(field, FieldKind.Double);
        if (type.SpecialType == SpecialType.System_String)
            return new FieldModel(field, FieldKind.String);
        if (type.TypeKind == TypeKind.Enum)
            return new FieldModel(field, FieldKind.Enum);
        if (type.ToDisplayString() == "System.Guid")
            return new FieldModel(field, FieldKind.Guid);
        if (type.ToDisplayString() == "SomeEngine.ECS.Entities.Entity")
            return new FieldModel(field, FieldKind.Entity);
        if (type is INamedTypeSymbol named &&
            modelsBySymbol.TryGetValue(named, out var nested))
        {
            return new FieldModel(field, FieldKind.Nested, nested);
        }

        return null;
    }

    private static string GenerateSource(IReadOnlyList<SerializableModel> models)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine("internal static partial class GameSerializationModule");
        builder.AppendLine("{");
        builder.AppendLine("    public static void RegisterAll(global::SomeEngine.ECS.Serialization.SerializationRegistry registry)");
        builder.AppendLine("    {");
        foreach (var model in models.OrderBy(static model => model.StableId))
            builder.AppendLine($"        {GetRegistrationLine(model)}");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        builder.AppendLine();

        foreach (var model in models)
        {
            if (model.Kind == SerializableKind.Tag)
                continue;

            GenerateCodec(builder, model);
            if (model.ContainsEntityReferences)
                GeneratePatcher(builder, model);
        }

        return builder.ToString();
    }

    private static string GetRegistrationLine(SerializableModel model)
    {
        string typeKey = $"new global::SomeEngine.ECS.Serialization.SerializationTypeKey(new global::System.Guid(\"{model.StableIdText}\"), \"{model.Symbol.ToDisplayString()}\", 0x{model.SchemaHash:X8}u)";
        string typeName = model.TypeName;
        string codec = GetCodecName(model);
        string patcher = GetPatcherName(model);

        return model.Kind switch
        {
            SerializableKind.Component when model.ContainsEntityReferences =>
                $"registry.Register<{typeName}, {codec}, {patcher}>({typeKey});",
            SerializableKind.Component =>
                $"registry.Register<{typeName}, {codec}>({typeKey});",
            SerializableKind.Tag =>
                $"registry.RegisterTag<{typeName}>({typeKey});",
            SerializableKind.Shared when model.ContainsEntityReferences =>
                $"registry.RegisterShared<{typeName}, {codec}, {patcher}>({typeKey});",
            SerializableKind.Shared =>
                $"registry.RegisterShared<{typeName}, {codec}>({typeKey});",
            SerializableKind.Buffer when model.ContainsEntityReferences =>
                $"registry.RegisterBuffer<{typeName}, {codec}, {patcher}>({typeKey});",
            SerializableKind.Buffer =>
                $"registry.RegisterBuffer<{typeName}, {codec}>({typeKey});",
            SerializableKind.Sparse when model.ContainsEntityReferences =>
                $"registry.RegisterSparse<{typeName}, {codec}, {patcher}>({typeKey});",
            SerializableKind.Sparse =>
                $"registry.RegisterSparse<{typeName}, {codec}>({typeKey});",
            SerializableKind.Relation when model.ContainsEntityReferences =>
                $"registry.RegisterRelation<{typeName}, {codec}, {patcher}>({typeKey});",
            SerializableKind.Relation =>
                $"registry.RegisterRelation<{typeName}, {codec}>({typeKey});",
            _ => throw new InvalidOperationException("Unsupported serializable kind."),
        };
    }

    private static void GenerateCodec(StringBuilder builder, SerializableModel model)
    {
        builder.AppendLine($"internal struct {GetCodecName(model)} : global::SomeEngine.ECS.Serialization.IComponentCodec<{model.TypeName}>");
        builder.AppendLine("{");
        builder.AppendLine($"    public void Write(ref global::SomeEngine.ECS.Serialization.DataWriter writer, in {model.TypeName} value)");
        builder.AppendLine("    {");
        foreach (var field in model.Fields)
            builder.AppendLine($"        {GetWriteLine(field)}");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine($"    public void Read(ref global::SomeEngine.ECS.Serialization.DataReader reader, out {model.TypeName} value)");
        builder.AppendLine("    {");
        builder.AppendLine("        value = default;");
        foreach (var field in model.Fields)
        {
            foreach (var line in GetReadLines(field))
                builder.AppendLine($"        {line}");
        }
        builder.AppendLine("    }");
        builder.AppendLine("}");
        builder.AppendLine();
    }

    private static void GeneratePatcher(StringBuilder builder, SerializableModel model)
    {
        builder.AppendLine($"internal struct {GetPatcherName(model)} : global::SomeEngine.ECS.Serialization.IReferencePatcher<{model.TypeName}>");
        builder.AppendLine("{");
        builder.AppendLine($"    public void Remap(ref {model.TypeName} value, global::SomeEngine.ECS.Serialization.IReferenceRemapper remapper)");
        builder.AppendLine("    {");
        foreach (var field in model.Fields.Where(static field => field.ContainsEntityReferences))
        {
            if (field.Kind == FieldKind.Entity)
            {
                string temp = "__mapped_" + field.Symbol.Name;
                builder.AppendLine($"        if (!remapper.TryMap(value.{field.Symbol.Name}, out var {temp}))");
                builder.AppendLine($"            throw new global::System.InvalidOperationException(\"Missing entity remap for field {field.Symbol.Name}.\");");
                builder.AppendLine($"        value.{field.Symbol.Name} = {temp};");
            }
            else if (field.Kind == FieldKind.Nested)
            {
                builder.AppendLine($"        var __patcher_{field.Symbol.Name} = new {GetPatcherName(field.Nested!)}();");
                builder.AppendLine($"        __patcher_{field.Symbol.Name}.Remap(ref value.{field.Symbol.Name}, remapper);");
            }
        }
        builder.AppendLine("    }");
        builder.AppendLine("}");
        builder.AppendLine();
    }

    private static string GetWriteLine(FieldModel field)
    {
        string access = "value." + field.Symbol.Name;
        return field.Kind switch
        {
            FieldKind.Boolean => $"writer.WriteBoolean({access});",
            FieldKind.Byte => $"writer.WriteByte({access});",
            FieldKind.Int32 => $"writer.WriteInt32({access});",
            FieldKind.UInt32 => $"writer.WriteUInt32({access});",
            FieldKind.Int64 => $"writer.WriteInt64({access});",
            FieldKind.Single => $"writer.WriteSingle({access});",
            FieldKind.Double => $"writer.WriteDouble({access});",
            FieldKind.Guid => $"writer.WriteGuid({access});",
            FieldKind.String => $"writer.WriteString({access});",
            FieldKind.Entity => $"writer.WriteEntity({access});",
            FieldKind.Enum => $"writer.WriteInt64(global::System.Convert.ToInt64({access}));",
            FieldKind.Nested => $"new {GetCodecName(field.Nested!)}().Write(ref writer, in {access});",
            _ => throw new InvalidOperationException("Unsupported field kind."),
        };
    }

    private static IEnumerable<string> GetReadLines(FieldModel field)
    {
        string access = "value." + field.Symbol.Name;
        switch (field.Kind)
        {
            case FieldKind.Boolean:
                yield return $"{access} = reader.ReadBoolean();";
                break;
            case FieldKind.Byte:
                yield return $"{access} = reader.ReadByte();";
                break;
            case FieldKind.Int32:
                yield return $"{access} = reader.ReadInt32();";
                break;
            case FieldKind.UInt32:
                yield return $"{access} = reader.ReadUInt32();";
                break;
            case FieldKind.Int64:
                yield return $"{access} = reader.ReadInt64();";
                break;
            case FieldKind.Single:
                yield return $"{access} = reader.ReadSingle();";
                break;
            case FieldKind.Double:
                yield return $"{access} = reader.ReadDouble();";
                break;
            case FieldKind.Guid:
                yield return $"{access} = reader.ReadGuid();";
                break;
            case FieldKind.String:
                yield return $"{access} = reader.ReadString();";
                break;
            case FieldKind.Entity:
                yield return $"{access} = reader.ReadEntity();";
                break;
            case FieldKind.Enum:
                yield return $"{access} = ({field.TypeName})reader.ReadInt64();";
                break;
            case FieldKind.Nested:
                yield return $"new {GetCodecName(field.Nested!)}().Read(ref reader, out var __nested_{field.Symbol.Name});";
                yield return $"{access} = __nested_{field.Symbol.Name};";
                break;
            default:
                throw new InvalidOperationException("Unsupported field kind.");
        }
    }

    private static bool TryStableId(
        INamedTypeSymbol symbol,
        SourceProductionContext context,
        out Guid stableId,
        out string stableIdText)
    {
        stableId = default;
        stableIdText = string.Empty;
        AttributeData? attr = symbol.GetAttributes().FirstOrDefault(static attr =>
            attr.AttributeClass?.ToDisplayString() == SerializationAttributeName);
        string? value = attr?.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value as string : null;
        if (value is null || !Guid.TryParse(value, out stableId))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                BadStableRule,
                symbol.Locations.FirstOrDefault(),
                symbol.ToDisplayString(),
                value ?? string.Empty));
            return false;
        }

        stableIdText = stableId.ToString("D");
        return true;
    }

    private static SerializableKind GetSerializableKind(INamedTypeSymbol symbol)
    {
        if (ImplementsInterface(symbol, ComponentsNamespace, "ITag"))
            return SerializableKind.Tag;
        if (ImplementsInterface(symbol, ComponentsNamespace, "ISharedComponent"))
            return SerializableKind.Shared;
        if (ImplementsInterface(symbol, ComponentsNamespace, "IBufferElement"))
            return SerializableKind.Buffer;
        if (ImplementsInterface(symbol, ComponentsNamespace, "ISparseComponent"))
            return SerializableKind.Sparse;
        if (ImplementsInterface(symbol, ComponentsNamespace, "IRelation") ||
            ImplementsInterface(symbol, ComponentsNamespace, "IExclusiveRelation"))
            return SerializableKind.Relation;
        if (ImplementsInterface(symbol, ComponentsNamespace, "IComponent"))
            return SerializableKind.Component;

        return SerializableKind.Unsupported;
    }

    private static bool HasSerializableAttribute(INamedTypeSymbol symbol)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == SerializationAttributeName)
                return true;
        }

        return false;
    }

    private static bool ImplementsInterface(INamedTypeSymbol type, string @namespace, string name)
    {
        foreach (var iface in type.AllInterfaces)
        {
            INamedTypeSymbol candidate = iface.OriginalDefinition;
            if (candidate.Name == name && candidate.ContainingNamespace.ToDisplayString() == @namespace)
                return true;
        }

        return false;
    }

    private static bool IsBufferBacking(ITypeSymbol type)
    {
        return type is INamedTypeSymbol named &&
               named.ContainingNamespace.ToDisplayString() == ComponentsNamespace &&
               (named.Name == "DynamicBufferHeader" || named.Name == "DynamicBufferInline");
    }

    private static uint ComputeSchemaHash(SerializableModel model)
    {
        uint hash = 2166136261u;
        AddString(ref hash, model.Symbol.ToDisplayString());
        AddString(ref hash, model.Kind.ToString());
        foreach (var field in model.Fields)
        {
            AddString(ref hash, field.Symbol.Name);
            AddString(ref hash, field.TypeName);
            hash = (hash ^ (uint)field.Kind) * 16777619u;
        }

        return hash == 0 ? 1u : hash;
    }

    private static void AddString(ref uint hash, string value)
    {
        foreach (byte b in Encoding.UTF8.GetBytes(value))
            hash = (hash ^ b) * 16777619u;
    }

    private static string GetCodecName(SerializableModel model) => "__SomeEngine_ECSSerializationCodec_" + Sanitize(model.Symbol);
    private static string GetPatcherName(SerializableModel model) => "__SomeEngine_ECSSerializationPatcher_" + Sanitize(model.Symbol);

    private static string Sanitize(INamedTypeSymbol symbol)
    {
        var builder = new StringBuilder(symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        for (int i = 0; i < builder.Length; i++)
        {
            if (!char.IsLetterOrDigit(builder[i]))
                builder[i] = '_';
        }

        return builder.ToString();
    }

    private sealed class SerializableModel
    {
        public SerializableModel(INamedTypeSymbol symbol, Guid stableId, string stableIdText)
        {
            Symbol = symbol;
            StableId = stableId;
            StableIdText = stableIdText;
        }

        public INamedTypeSymbol Symbol { get; }
        public Guid StableId { get; }
        public string StableIdText { get; }
        public string TypeName => Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        public SerializableKind Kind { get; set; }
        public List<FieldModel> Fields { get; } = new();
        public bool ContainsEntityReferences { get; set; }
        public uint SchemaHash { get; set; }
    }

    private sealed class FieldModel
    {
        public FieldModel(
            IFieldSymbol symbol,
            FieldKind kind,
            SerializableModel? nested = null)
        {
            Symbol = symbol;
            Kind = kind;
            Nested = nested;
        }

        public IFieldSymbol Symbol { get; }
        public FieldKind Kind { get; }
        public bool ContainsEntityReferences => Kind == FieldKind.Entity ||
                                                (Kind == FieldKind.Nested && Nested is { ContainsEntityReferences: true });
        public SerializableModel? Nested { get; }
        public string TypeName => Symbol.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private enum SerializableKind
    {
        Unsupported,
        Component,
        Tag,
        Shared,
        Buffer,
        Sparse,
        Relation,
    }

    private enum FieldKind
    {
        Boolean,
        Byte,
        Int32,
        UInt32,
        Int64,
        Single,
        Double,
        Guid,
        String,
        Entity,
        Enum,
        Nested,
    }
}

