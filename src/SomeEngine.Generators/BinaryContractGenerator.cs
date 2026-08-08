using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.Generators;

#pragma warning disable RS2008 // This generator is intentionally delivered without editing analyzer release metadata.

[Generator]
public sealed class BinaryContractGenerator : IIncrementalGenerator
{
    private const string ContractAttributeName = "SomeEngine.Serialization.BinaryContractAttribute";
    private const string NativeLayoutAttributeName = "SomeEngine.Serialization.BinaryNativeLayoutAttribute";
    private const string NameAttributeName = "SomeEngine.Serialization.BinaryNameAttribute";
    private const string IgnoreAttributeName = "SomeEngine.Serialization.BinaryIgnoreAttribute";
    private const string ChunkAttributeName = "SomeEngine.Serialization.BinaryChunkAttribute";
    private const string UnionAttributeName = "SomeEngine.Serialization.BinaryUnionAttribute";
    private const string UnionCaseAttributeName = "SomeEngine.Serialization.BinaryUnionCaseAttribute";
    private const string StructLayoutAttributeName = "System.Runtime.InteropServices.StructLayoutAttribute";
    private const string GeneratedMemberPrefix = "__SomeEngineBinaryContract_";

    private static readonly SymbolDisplayFormat QualifiedTypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat StableNameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly DiagnosticDescriptor InvalidContract = new(
        "SEBC001",
        "Binary contract shape cannot be generated",
        "Binary contract '{0}' cannot be generated: {1}",
        "SomeEngine.Serialization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidMemberAccess = new(
        "SEBC002",
        "Binary contract member is not publicly writable",
        "Binary contract member '{0}' must be a public instance field, or a public instance property with a public getter and public setter",
        "SomeEngine.Serialization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedMemberShape = new(
        "SEBC003",
        "Binary contract member shape is unsupported",
        "Binary contract member '{0}' has unsupported type '{1}': {2}",
        "SomeEngine.Serialization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidLogicalName = new(
        "SEBC004",
        "Binary logical name is invalid or duplicated",
        "Binary contract element '{0}' has an invalid or duplicated logical name: {1}",
        "SomeEngine.Serialization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateFieldKey = new(
        "SEBC005",
        "Binary field key is duplicated",
        "Binary contract '{0}' has FNV-1a field-key collision 0x{1:X16} between logical names {2}",
        "SomeEngine.Serialization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor CyclicContractGraph = new(
        "SEBC006",
        "Binary contract graph is cyclic",
        "Binary contract '{0}' belongs to cyclic object graph: {1}",
        "SomeEngine.Serialization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MissingConstructor = new(
        "SEBC007",
        "Binary contract class cannot be constructed",
        "Binary contract class '{0}' must declare or inherit a parameterless instance constructor",
        "SomeEngine.Serialization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidNestedContract = new(
        "SEBC008",
        "Nested binary contract is unavailable",
        "Binary member '{0}' depends on contract '{1}', but that nested contract could not be generated",
        "SomeEngine.Serialization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateTypeId = new(
        "SEBC009",
        "Binary type id is duplicated",
        "Binary contract '{0}' produces type id '{1}', which is also produced by {2}",
        "SomeEngine.Serialization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidNativeLayout = new(
        "SEBC010",
        "Native binary layout cannot be proven",
        "Native binary layout for contract '{0}' cannot be proven: {1}",
        "SomeEngine.Serialization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidChunkReference = new(
        "SEBC011",
        "Binary chunk reference cannot be generated",
        "Binary chunk payload '{0}' cannot be generated: {1}",
        "SomeEngine.Serialization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<INamedTypeSymbol> candidates = context.SyntaxProvider.ForAttributeWithMetadataName(
            ContractAttributeName,
            static (node, _) => node is TypeDeclarationSyntax,
            static (attributeContext, _) => (INamedTypeSymbol)attributeContext.TargetSymbol);

        context.RegisterSourceOutput(
            context.CompilationProvider.Combine(candidates.Collect()),
            static (productionContext, input) => Generate(productionContext, input.Left, input.Right));
    }

    private static void Generate(
        SourceProductionContext context,
        Compilation compilation,
        ImmutableArray<INamedTypeSymbol> discovered)
    {
        string catalogNamespace =
            "SomeEngine.GeneratedContracts.Assembly_" +
            Sanitize(compilation.AssemblyName ?? "UnknownAssembly");
        string catalogTypeName = "global::" + catalogNamespace + ".GeneratedBinaryContractCatalog";
        List<INamedTypeSymbol> candidates = Deduplicate(discovered)
            .OrderBy(static type => StableName(type), StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
            return;

        Dictionary<INamedTypeSymbol, ContractModel> models =
            new(SymbolEqualityComparer.Default);
        foreach (INamedTypeSymbol candidate in candidates)
        {
            ContractModel model = CreateModel(context, candidate);
            models.Add(candidate, model);
        }

        foreach (ContractModel model in models.Values)
        {
            if (model.IsManuallyImplemented)
                continue;
            ValidateContractType(context, model);
            BuildMembers(context, model, models);
            BuildChunkReferences(context, model);
        }

        RejectDuplicateLogicalTypeNames(context, models.Values);
        RejectDuplicateTypeIds(context, models.Values);
        RejectCycles(context, models.Values);
        RejectInvalidClosures(context, models.Values);
        BuildNativeLayouts(context, models);

        List<ContractModel> valid = models.Values
            .Where(static model => model.IsValid)
            .OrderBy(static model => StableName(model.Symbol), StringComparer.Ordinal)
            .ToList();
        if (valid.Count == 0)
            return;

        foreach (ContractModel model in valid.Where(static model => !model.IsManuallyImplemented))
        {
            bool runtimeFingerprint = RequiresRuntimeFingerprint(model, new HashSet<ContractModel>());
            model.RequiresRuntimeFingerprint = runtimeFingerprint;
            if (!runtimeFingerprint)
                model.Fingerprint = ComputeFingerprint(model);
            string hintName = Sanitize(StableName(model.Symbol)) + ".BinaryContract.g.cs";
            context.AddSource(hintName, EmitContract(model, catalogTypeName));
        }

        context.AddSource(
            "GeneratedBinaryContractCatalog.g.cs",
            EmitCatalog(valid, catalogNamespace));
    }

    private static IEnumerable<INamedTypeSymbol> Deduplicate(ImmutableArray<INamedTypeSymbol> discovered)
    {
        HashSet<INamedTypeSymbol> seen = new(SymbolEqualityComparer.Default);
        foreach (INamedTypeSymbol symbol in discovered)
        {
            if (seen.Add(symbol))
                yield return symbol;
        }
    }

    private static ContractModel CreateModel(SourceProductionContext context, INamedTypeSymbol type)
    {
        AttributeData? attribute = FindAttribute(type, ContractAttributeName);
        uint epoch = 1;
        string? explicitLogicalName = null;
        bool compatibilityValid = true;

        if (attribute is not null && attribute.ConstructorArguments.Length != 0)
        {
            object? value = attribute.ConstructorArguments[0].Value;
            long raw = value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (raw != (long)CompatibilityMode.ExactSchema)
                compatibilityValid = false;
        }

        if (attribute is not null)
        {
            foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
            {
                if (string.Equals(argument.Key, "Epoch", StringComparison.Ordinal) && argument.Value.Value is not null)
                    epoch = Convert.ToUInt32(argument.Value.Value, CultureInfo.InvariantCulture);
                else if (string.Equals(argument.Key, "LogicalName", StringComparison.Ordinal))
                    explicitLogicalName = argument.Value.Value as string;
            }
        }

        string logicalName = explicitLogicalName ?? StableName(type);
        ContractModel model = new(type, epoch, logicalName);
        model.IsManuallyImplemented = ImplementsContractInterface(type);
        AttributeData? nativeLayout = FindAttribute(type, NativeLayoutAttributeName);
        if (nativeLayout is not null)
        {
            model.NativeLayoutRequested = true;
            if (nativeLayout.ConstructorArguments.Length != 0)
                model.NativeAbiToken = nativeLayout.ConstructorArguments[0].Value as string;
        }
        if (!compatibilityValid)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidContract,
                ContractLocation(type),
                StableName(type),
                "BinaryCompatibility must be ExactSchema; compatibility and migration codecs are not supported"));
            model.IsValid = false;
        }

        if (string.IsNullOrWhiteSpace(logicalName))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidLogicalName,
                ContractLocation(type),
                StableName(type),
                "the contract logical name must contain non-whitespace text"));
            model.IsValid = false;
            model.LogicalName = StableName(type);
        }

        model.TypeId = ComputeTypeId(model.LogicalName);
        return model;
    }

    private static bool ImplementsContractInterface(INamedTypeSymbol type)
    {
        foreach (INamedTypeSymbol implemented in type.AllInterfaces)
        {
            if (implemented.Arity != 1 ||
                !string.Equals(implemented.Name, "IBinaryContract", StringComparison.Ordinal) ||
                !string.Equals(
                    implemented.ContainingNamespace?.ToDisplayString(),
                    "SomeEngine.Serialization",
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(implemented.TypeArguments[0], type))
                return true;
        }

        return false;
    }

    private static void ValidateContractType(SourceProductionContext context, ContractModel model)
    {
        INamedTypeSymbol type = model.Symbol;
        List<string> reasons = new();
        if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct)
            reasons.Add("only classes and structs are supported");
        if (type.Arity != 0)
            reasons.Add("generic contracts are not supported");
        if (type.IsStatic)
            reasons.Add("static types are not contracts");
        if (type.TypeKind == TypeKind.Class && type.IsAbstract)
            reasons.Add("abstract contract classes cannot be constructed");
        if (type.TypeKind == TypeKind.Class &&
            type.BaseType is INamedTypeSymbol baseType &&
            baseType.SpecialType != SpecialType.System_Object)
        {
            reasons.Add(
                $"contract inheritance from '{StableName(baseType)}' is unsupported because inherited storage cannot be silently omitted");
        }
        if (type.TypeKind == TypeKind.Struct && type.IsReadOnly)
            reasons.Add("readonly contract structs cannot be populated");
        if (type.IsRefLikeType)
            reasons.Add("ref-like contract structs are not supported");
        if (!IsPartial(type))
            reasons.Add("every declaration of the contract must be partial");
        if (IsFileLocal(type))
            reasons.Add("file-local contracts cannot be placed in the assembly catalog");
        if (!IsAssemblyAccessible(type))
            reasons.Add("the contract and its containing types must be accessible from the generated assembly catalog");

        for (INamedTypeSymbol? containing = type.ContainingType; containing is not null; containing = containing.ContainingType)
        {
            if (containing.Arity != 0)
                reasons.Add($"containing type '{StableName(containing)}' is generic");
            if (!IsPartial(containing))
                reasons.Add($"containing type '{StableName(containing)}' is not partial");
            if (IsFileLocal(containing))
                reasons.Add($"containing type '{StableName(containing)}' is file-local");
            if (containing.TypeKind != TypeKind.Class &&
                containing.TypeKind != TypeKind.Struct &&
                containing.TypeKind != TypeKind.Interface)
            {
                reasons.Add($"containing type '{StableName(containing)}' cannot be reopened by generated code");
            }
        }

        string[] reservedNames =
        {
            "TypeId",
            "SchemaFingerprint",
            "Compatibility",
            "SchemaEpoch",
            "Write",
            "Read",
            "SpanView",
            "View",
            "ValidateCanonical",
            "CreateView",
            "OpenDocumentViewAsync",
        };
        foreach (ISymbol member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared)
                continue;
            if (reservedNames.Contains(member.Name, StringComparer.Ordinal) ||
                member.Name.StartsWith(GeneratedMemberPrefix, StringComparison.Ordinal))
            {
                reasons.Add($"member name '{member.Name}' is reserved by the generated contract implementation");
            }
        }

        if (reasons.Count != 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidContract,
                ContractLocation(type),
                StableName(type),
                string.Join("; ", reasons.Distinct(StringComparer.Ordinal))));
            model.IsValid = false;
        }

        if (type.TypeKind == TypeKind.Class &&
            !type.IsStatic &&
            !type.InstanceConstructors.Any(static constructor => constructor.Parameters.Length == 0))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MissingConstructor,
                ContractLocation(type),
                StableName(type)));
            model.IsValid = false;
        }
    }

    private static void BuildChunkReferences(SourceProductionContext context, ContractModel model)
    {
        foreach (ISymbol payload in model.Symbol.GetMembers()
                     .Where(static member => !member.IsStatic && !member.IsImplicitlyDeclared))
        {
            AttributeData? attribute = FindAttribute(payload, ChunkAttributeName);
            if (attribute is null)
                continue;

            string? keyName = attribute.ConstructorArguments.Length > 0
                ? attribute.ConstructorArguments[0].Value as string
                : null;
            string? lengthName = attribute.ConstructorArguments.Length > 1
                ? attribute.ConstructorArguments[1].Value as string
                : null;
            List<string> reasons = new();
            if (!HasAttribute(payload, IgnoreAttributeName))
                reasons.Add("the payload must also be marked BinaryIgnore so it is never encoded inline");

            MemberModel? key = model.Members.FirstOrDefault(member =>
                string.Equals(member.Symbol.Name, keyName, StringComparison.Ordinal));
            MemberModel? length = model.Members.FirstOrDefault(member =>
                string.Equals(member.Symbol.Name, lengthName, StringComparison.Ordinal));
            if (key is null || key.Shape.Kind != ShapeKind.Primitive || key.Shape.PrimitiveKind != PrimitiveKind.UInt64)
                reasons.Add($"key member '{keyName}' must be an encoded public UInt64 field or property");
            if (length is null || length.Shape.Kind != ShapeKind.Primitive ||
                (length.Shape.PrimitiveKind != PrimitiveKind.UInt64 && length.Shape.PrimitiveKind != PrimitiveKind.Int64))
            {
                reasons.Add($"decoded-length member '{lengthName}' must be an encoded public UInt64 or Int64 field or property");
            }

            string accessorName = payload.Name + "Chunk";
            if (model.Symbol.GetMembers(accessorName).Any(static member => !member.IsImplicitlyDeclared))
                reasons.Add($"generated accessor name '{accessorName}' is already declared");
            if (model.ChunkReferences.Any(chunk => string.Equals(chunk.AccessorName, accessorName, StringComparison.Ordinal)))
                reasons.Add($"generated accessor name '{accessorName}' is duplicated");

            if (reasons.Count != 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidChunkReference,
                    MemberLocation(payload),
                    payload.ToDisplayString(),
                    string.Join("; ", reasons)));
                model.IsValid = false;
                continue;
            }

            model.ChunkReferences.Add(new ChunkReferenceModel(
                accessorName,
                key!,
                length!));
        }
    }

    private static void BuildMembers(
        SourceProductionContext context,
        ContractModel model,
        Dictionary<INamedTypeSymbol, ContractModel> models)
    {
        IEnumerable<ISymbol> publicInstanceMembers = model.Symbol.GetMembers()
            .Where(static member => !member.IsStatic &&
                                    !member.IsImplicitlyDeclared &&
                                    member.DeclaredAccessibility == Accessibility.Public &&
                                    (member is IFieldSymbol || member is IPropertySymbol))
            .OrderBy(static member => member.Locations.FirstOrDefault()?.SourceTree?.FilePath, StringComparer.Ordinal)
            .ThenBy(static member => member.Locations.FirstOrDefault()?.SourceSpan.Start ?? int.MaxValue);

        foreach (ISymbol symbol in publicInstanceMembers)
        {
            if (HasAttribute(symbol, IgnoreAttributeName))
                continue;

            ITypeSymbol memberType;
            if (symbol is IPropertySymbol property)
            {
                if (property.IsIndexer ||
                    property.GetMethod?.DeclaredAccessibility != Accessibility.Public ||
                    property.SetMethod?.DeclaredAccessibility != Accessibility.Public)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        InvalidMemberAccess,
                        MemberLocation(symbol),
                        symbol.ToDisplayString()));
                    model.IsValid = false;
                    continue;
                }

                memberType = property.Type;
            }
            else
            {
                IFieldSymbol field = (IFieldSymbol)symbol;
                if (field.IsReadOnly || field.IsConst)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        InvalidMemberAccess,
                        MemberLocation(symbol),
                        symbol.ToDisplayString()));
                    model.IsValid = false;
                    continue;
                }

                memberType = field.Type;
            }

            string logicalName = symbol.Name;
            AttributeData? nameAttribute = FindAttribute(symbol, NameAttributeName);
            if (nameAttribute is not null && nameAttribute.ConstructorArguments.Length != 0)
                logicalName = nameAttribute.ConstructorArguments[0].Value as string ?? string.Empty;
            if (string.IsNullOrWhiteSpace(logicalName))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidLogicalName,
                    MemberLocation(symbol),
                    symbol.ToDisplayString(),
                    "the field logical name must contain non-whitespace text"));
                model.IsValid = false;
                continue;
            }

            if (!TryClassify(memberType, models, out ShapeModel? shape, out string reason))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedMemberShape,
                    MemberLocation(symbol),
                    symbol.ToDisplayString(),
                    memberType.ToDisplayString(QualifiedTypeFormat),
                    reason));
                model.IsValid = false;
                continue;
            }

            model.Members.Add(new MemberModel(
                symbol,
                memberType,
                logicalName,
                ComputeFieldKey(logicalName),
                shape!));
        }

        foreach (IGrouping<string, MemberModel> duplicate in model.Members
                     .GroupBy(static member => member.LogicalName, StringComparer.Ordinal)
                     .Where(static group => group.Count() > 1))
        {
            string members = string.Join(", ", duplicate.Select(static member => member.Symbol.Name));
            foreach (MemberModel member in duplicate)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidLogicalName,
                    MemberLocation(member.Symbol),
                    member.Symbol.ToDisplayString(),
                    $"logical name '{duplicate.Key}' is shared by members {members}"));
            }

            model.IsValid = false;
        }

        foreach (IGrouping<ulong, MemberModel> collision in model.Members
                     .GroupBy(static member => member.FieldKey)
                     .Where(static group => group.Select(member => member.LogicalName).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DuplicateFieldKey,
                MemberLocation(collision.First().Symbol),
                StableName(model.Symbol),
                collision.Key,
                string.Join(", ", collision.Select(static member => $"'{member.LogicalName}'"))));
            model.IsValid = false;
        }

        model.Members.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.LogicalName, right.LogicalName));
        for (int index = 0; index < model.Members.Count; index++)
            model.Members[index].Index = index;
    }

    private static void BuildNativeLayouts(
        SourceProductionContext context,
        Dictionary<INamedTypeSymbol, ContractModel> models)
    {
        ContractModel[] requested = models.Values
            .Where(static model => model.NativeLayoutRequested)
            .OrderBy(static model => StableName(model.Symbol), StringComparer.Ordinal)
            .ToArray();

        foreach (ContractModel model in requested)
            TryBuildNativeLayout(model, models);

        foreach (ContractModel model in requested)
        {
            if (model.NativeLayout is not null)
                continue;

            context.ReportDiagnostic(Diagnostic.Create(
                InvalidNativeLayout,
                ContractLocation(model.Symbol),
                StableName(model.Symbol),
                model.NativeLayoutFailure ?? "the generator did not establish a complete native-layout proof"));
        }
    }

    private static bool TryBuildNativeLayout(
        ContractModel model,
        Dictionary<INamedTypeSymbol, ContractModel> models)
    {
        if (model.NativeValidationState == NativeValidationState.Valid)
            return true;
        if (model.NativeValidationState == NativeValidationState.Invalid)
            return false;
        if (model.NativeValidationState == NativeValidationState.Visiting)
            return FailNativeLayout(model, "recursive value-type storage is not supported");

        model.NativeValidationState = NativeValidationState.Visiting;
        if (!model.IsValid)
            return FailNativeLayout(model, "the canonical binary contract is invalid and cannot host generated native proof metadata");
        if (model.IsManuallyImplemented)
            return FailNativeLayout(model, "manually implemented contracts cannot receive a generated native-layout proof");
        if (model.Symbol.TypeKind != TypeKind.Struct || model.Symbol.IsRefLikeType)
            return FailNativeLayout(model, "only non-ref contract structs can have proven native layout");
        if (string.IsNullOrWhiteSpace(model.NativeAbiToken))
            return FailNativeLayout(model, "BinaryNativeLayout requires a non-empty ABI token");
        if (model.Symbol.GetMembers("NativeLayoutProof").Any(static member => !member.IsImplicitlyDeclared))
            return FailNativeLayout(model, "member name 'NativeLayoutProof' is reserved by the generated native proof");

        if (!TryReadNativeStructLayout(model.Symbol, out int pack, out int? explicitSize, out string layoutReason))
            return FailNativeLayout(model, layoutReason);

        List<IFieldSymbol> storageFields = model.Symbol.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static field => !field.IsStatic && !field.IsConst)
            .ToList();
        if (storageFields.Count == 0)
            return FailNativeLayout(model, "empty structs have runtime-defined storage not covered by scalar fields");

        string? storageDeclaration = null;
        foreach (IFieldSymbol field in storageFields)
        {
            if (!TryGetStorageDeclaration(field, out string declaration, out _, out _))
            {
                return FailNativeLayout(
                    model,
                    $"storage field '{field.Name}' has no source declaration whose physical order can be proven");
            }

            if (storageDeclaration is null)
                storageDeclaration = declaration;
            else if (!string.Equals(storageDeclaration, declaration, StringComparison.Ordinal))
            {
                return FailNativeLayout(
                    model,
                    "instance storage is split across partial declarations, so sequential field order is not deterministic");
            }
        }

        storageFields.Sort(static (left, right) =>
        {
            TryGetStorageDeclaration(left, out _, out int leftStart, out string leftName);
            TryGetStorageDeclaration(right, out _, out int rightStart, out string rightName);
            int order = leftStart.CompareTo(rightStart);
            return order != 0 ? order : StringComparer.Ordinal.Compare(leftName, rightName);
        });

        List<NativeLayoutFieldModel> layoutFields = new(storageFields.Count);
        int currentOffset = 0;
        int coveredFieldBytes = 0;
        int requiredAlignment = 1;
        try
        {
            foreach (IFieldSymbol field in storageFields)
            {
                if (field.IsFixedSizeBuffer)
                {
                    return FailNativeLayout(
                        model,
                        $"fixed buffer field '{NativeStorageName(field)}' is unsupported; use recursively proven fixed-size structs instead");
                }

                if (!TryClassifyNativeField(field.Type, models, out NativeFieldShape? shape, out string fieldReason))
                {
                    return FailNativeLayout(
                        model,
                        $"storage member '{NativeStorageName(field)}' of type '{StableName(field.Type)}' is unsupported: {fieldReason}");
                }

                int effectiveAlignment = Math.Min(pack, shape!.NaturalAlignment);
                int offset = AlignUp(currentOffset, effectiveAlignment);
                layoutFields.Add(new NativeLayoutFieldModel(
                    NativeStorageName(field),
                    shape.TypeDescriptor,
                    offset,
                    shape.Size,
                    shape.CoveredFieldBytes,
                    effectiveAlignment,
                    shape.NestedLayoutFingerprint));
                currentOffset = checked(offset + shape.Size);
                coveredFieldBytes = checked(coveredFieldBytes + shape.CoveredFieldBytes);
                requiredAlignment = Math.Max(requiredAlignment, effectiveAlignment);
            }
        }
        catch (OverflowException)
        {
            return FailNativeLayout(model, "the computed native layout exceeds the supported 32-bit element-size domain");
        }

        int computedSize;
        try
        {
            computedSize = AlignUp(currentOffset, requiredAlignment);
        }
        catch (OverflowException)
        {
            return FailNativeLayout(model, "the aligned native layout size exceeds the supported 32-bit element-size domain");
        }

        if (explicitSize.HasValue && explicitSize.Value != computedSize)
        {
            return FailNativeLayout(
                model,
                $"StructLayout.Size={explicitSize.Value} does not equal the computed sequential size {computedSize}");
        }

        int generatedSize = explicitSize ?? computedSize;
        if (generatedSize != coveredFieldBytes)
        {
            return FailNativeLayout(
                model,
                $"the {generatedSize}-byte layout contains {generatedSize - coveredFieldBytes} byte(s) of internal or tail padding; " +
                "native raw layouts must be fully covered by deterministic fields");
        }

        if (generatedSize % requiredAlignment != 0)
        {
            return FailNativeLayout(
                model,
                $"the generated size {generatedSize} is not a multiple of required alignment {requiredAlignment}");
        }

        ulong fingerprint = ComputeNativeLayoutFingerprint(
            model,
            model.NativeAbiToken!,
            pack,
            generatedSize,
            coveredFieldBytes,
            requiredAlignment,
            layoutFields);
        model.NativeLayout = new NativeLayoutModel(
            model.NativeAbiToken!,
            fingerprint,
            generatedSize,
            coveredFieldBytes,
            requiredAlignment,
            pack,
            layoutFields);
        model.NativeValidationState = NativeValidationState.Valid;
        return true;
    }

    private static bool TryReadNativeStructLayout(
        INamedTypeSymbol type,
        out int pack,
        out int? explicitSize,
        out string reason)
    {
        pack = 0;
        explicitSize = null;
        AttributeData? layout = FindAttribute(type, StructLayoutAttributeName);
        if (layout is null || layout.ConstructorArguments.Length == 0)
        {
            reason = "StructLayout(LayoutKind.Sequential, Pack = ...) must be declared explicitly";
            return false;
        }

        long layoutKind = layout.ConstructorArguments[0].Value is object value
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : long.MinValue;
        if (layoutKind != 0)
        {
            reason = layoutKind == 2
                ? "explicit/overlapping layout is unsupported"
                : "auto layout is unsupported; LayoutKind.Sequential is required";
            return false;
        }

        bool hasExplicitPack = false;
        foreach (KeyValuePair<string, TypedConstant> argument in layout.NamedArguments)
        {
            if (string.Equals(argument.Key, "Pack", StringComparison.Ordinal) && argument.Value.Value is not null)
            {
                pack = Convert.ToInt32(argument.Value.Value, CultureInfo.InvariantCulture);
                hasExplicitPack = true;
            }
            else if (string.Equals(argument.Key, "Size", StringComparison.Ordinal) && argument.Value.Value is not null)
            {
                explicitSize = Convert.ToInt32(argument.Value.Value, CultureInfo.InvariantCulture);
            }
        }

        if (!hasExplicitPack)
        {
            reason = "StructLayout.Pack must be specified explicitly";
            return false;
        }

        if (!IsSupportedPack(pack))
        {
            reason = $"StructLayout.Pack={pack} is not a fixed supported pack (1, 2, 4, 8, 16, 32, 64, or 128)";
            return false;
        }

        if (explicitSize.HasValue && explicitSize.Value <= 0)
        {
            reason = "StructLayout.Size must be positive when specified";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryClassifyNativeField(
        ITypeSymbol type,
        Dictionary<INamedTypeSymbol, ContractModel> models,
        out NativeFieldShape? shape,
        out string reason)
    {
        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType)
        {
            if (!TryGetFixedScalarLayout(enumType.EnumUnderlyingType!, out int enumSize, out string scalarToken))
            {
                shape = null;
                reason = "the enum underlying type is not a fixed unmanaged scalar";
                return false;
            }

            shape = new NativeFieldShape(
                enumSize,
                enumSize,
                enumSize,
                $"enum:{StableName(enumType)}:{scalarToken}",
                nestedLayoutFingerprint: 0);
            reason = string.Empty;
            return true;
        }

        if (TryGetFixedScalarLayout(type, out int scalarSize, out string typeToken))
        {
            shape = new NativeFieldShape(
                scalarSize,
                scalarSize,
                scalarSize,
                typeToken,
                nestedLayoutFingerprint: 0);
            reason = string.Empty;
            return true;
        }

        if (type is INamedTypeSymbol nestedStruct && nestedStruct.TypeKind == TypeKind.Struct)
        {
            if (!models.TryGetValue(nestedStruct, out ContractModel? nestedContract))
            {
                shape = null;
                reason = "nested structs must be BinaryContract types in the same source-generation compilation";
                return false;
            }

            if (!nestedContract.NativeLayoutRequested)
            {
                shape = null;
                reason = $"nested struct '{StableName(nestedStruct)}' is not marked with BinaryNativeLayout";
                return false;
            }

            if (!TryBuildNativeLayout(nestedContract, models) || nestedContract.NativeLayout is null)
            {
                shape = null;
                reason = $"nested struct '{StableName(nestedStruct)}' has no valid native proof: " +
                    (nestedContract.NativeLayoutFailure ?? "unknown proof failure");
                return false;
            }

            NativeLayoutModel nested = nestedContract.NativeLayout;
            shape = new NativeFieldShape(
                nested.GeneratedSize,
                nested.RequiredAlignment,
                nested.CoveredFieldBytes,
                $"struct:{StableName(nestedStruct)}",
                nested.LayoutFingerprint);
            reason = string.Empty;
            return true;
        }

        shape = null;
        reason = "only fixed unmanaged scalars, enums, or recursively proven native structs are allowed";
        return false;
    }

    private static bool TryGetFixedScalarLayout(ITypeSymbol type, out int size, out string typeToken)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
                size = 1;
                break;
            case SpecialType.System_Char:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
                size = 2;
                break;
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Single:
                size = 4;
                break;
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Double:
                size = 8;
                break;
            default:
                size = 0;
                typeToken = string.Empty;
                return false;
        }

        typeToken = $"scalar:{StableName(type)}";
        return true;
    }

    private static bool TryGetStorageDeclaration(
        IFieldSymbol field,
        out string declarationIdentity,
        out int sourceStart,
        out string storageName)
    {
        ISymbol declarationSymbol = field.AssociatedSymbol ?? field;
        SyntaxReference? reference = declarationSymbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null)
        {
            declarationIdentity = string.Empty;
            sourceStart = int.MaxValue;
            storageName = NativeStorageName(field);
            return false;
        }

        SyntaxNode syntax = reference.GetSyntax();
        TypeDeclarationSyntax? containingType = syntax.AncestorsAndSelf()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();
        if (containingType is null)
        {
            declarationIdentity = string.Empty;
            sourceStart = int.MaxValue;
            storageName = NativeStorageName(field);
            return false;
        }

        declarationIdentity = (syntax.SyntaxTree.FilePath ?? string.Empty) + ":" +
            containingType.SpanStart.ToString(CultureInfo.InvariantCulture);
        sourceStart = syntax.SpanStart;
        storageName = NativeStorageName(field);
        return true;
    }

    private static string NativeStorageName(IFieldSymbol field)
    {
        if (field.AssociatedSymbol is IPropertySymbol property)
            return "property:" + property.Name;
        if (field.AssociatedSymbol is IEventSymbol @event)
            return "event:" + @event.Name;
        return "field:" + field.Name;
    }

    private static int AlignUp(int value, int alignment)
    {
        int mask = alignment - 1;
        return checked((value + mask) & ~mask);
    }

    private static bool IsSupportedPack(int pack) =>
        pack == 1 || pack == 2 || pack == 4 || pack == 8 ||
        pack == 16 || pack == 32 || pack == 64 || pack == 128;

    private static ulong ComputeNativeLayoutFingerprint(
        ContractModel model,
        string abiToken,
        int pack,
        int generatedSize,
        int coveredFieldBytes,
        int requiredAlignment,
        IReadOnlyList<NativeLayoutFieldModel> fields)
    {
        WriteDescriptorSetr descriptor = new();
        descriptor.WriteToken("SomeEngine.BinaryNativeLayout.v1");
        descriptor.WriteToken(model.LogicalName);
        descriptor.WriteToken(abiToken);
        descriptor.WriteUInt32(checked((uint)pack));
        descriptor.WriteUInt32(checked((uint)generatedSize));
        descriptor.WriteUInt32(checked((uint)coveredFieldBytes));
        descriptor.WriteUInt32(checked((uint)requiredAlignment));
        descriptor.WriteUInt32(checked((uint)fields.Count));
        for (int index = 0; index < fields.Count; index++)
        {
            NativeLayoutFieldModel field = fields[index];
            descriptor.WriteUInt32(checked((uint)index));
            descriptor.WriteToken(field.StorageName);
            descriptor.WriteToken(field.TypeDescriptor);
            descriptor.WriteUInt32(checked((uint)field.Offset));
            descriptor.WriteUInt32(checked((uint)field.Size));
            descriptor.WriteUInt32(checked((uint)field.CoveredFieldBytes));
            descriptor.WriteUInt32(checked((uint)field.Alignment));
            descriptor.WriteUInt64(field.NestedLayoutFingerprint);
        }

        byte[] hash;
        using (SHA256 sha = SHA256.Create())
            hash = sha.ComputeHash(descriptor.ToArray());
        ulong fingerprint = 0;
        for (int index = 0; index < sizeof(ulong); index++)
            fingerprint = (fingerprint << 8) | hash[index];
        return fingerprint;
    }

    private static bool FailNativeLayout(ContractModel model, string reason)
    {
        model.NativeLayout = null;
        model.NativeLayoutFailure = reason;
        model.NativeValidationState = NativeValidationState.Invalid;
        return false;
    }

    private static bool TryClassify(
        ITypeSymbol type,
        Dictionary<INamedTypeSymbol, ContractModel> models,
        out ShapeModel? shape,
        out string reason)
    {
        if (type is IArrayTypeSymbol array)
        {
            if (!array.IsSZArray)
            {
                shape = null;
                reason = "only single-dimensional, zero-based arrays are supported";
                return false;
            }

            if (!TryClassify(array.ElementType, models, out ShapeModel? element, out string elementReason))
            {
                shape = null;
                reason = $"array element type is unsupported: {elementReason}";
                return false;
            }

            shape = ShapeModel.Collection(
                CollectionKind.Array,
                element!,
                array.ElementType,
                AllowsNull(type));
            reason = string.Empty;
            return true;
        }

        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType)
        {
            PrimitiveKind underlying = ClassifyPrimitive(enumType.EnumUnderlyingType!);
            if (underlying == PrimitiveKind.None)
            {
                shape = null;
                reason = "the enum has an unsupported underlying type";
                return false;
            }

            shape = ShapeModel.Enum(enumType, underlying);
            reason = string.Empty;
            return true;
        }

        PrimitiveKind primitive = ClassifyPrimitive(type);
        if (primitive != PrimitiveKind.None)
        {
            shape = ShapeModel.Primitive(primitive);
            reason = string.Empty;
            return true;
        }

        if (type.SpecialType == SpecialType.System_String)
        {
            shape = ShapeModel.String(AllowsNull(type));
            reason = string.Empty;
            return true;
        }

        if (type is INamedTypeSymbol named)
        {
            if (IsNullable(named, out ITypeSymbol? nullableValue))
            {
                if (IsMemoryOfByte(nullableValue!))
                {
                    shape = ShapeModel.Memory(allowsNull: true);
                    reason = string.Empty;
                    return true;
                }

                if (nullableValue is INamedTypeSymbol nullableEnum &&
                    nullableEnum.TypeKind == TypeKind.Enum)
                {
                    PrimitiveKind underlying = ClassifyPrimitive(nullableEnum.EnumUnderlyingType!);
                    if (underlying == PrimitiveKind.None)
                    {
                        shape = null;
                        reason = "the nullable enum has an unsupported underlying type";
                        return false;
                    }

                    shape = ShapeModel.NullableEnum(nullableEnum, underlying);
                    reason = string.Empty;
                    return true;
                }

                if (nullableValue is INamedTypeSymbol nestedStruct &&
                    nestedStruct.TypeKind == TypeKind.Struct &&
                    models.TryGetValue(nestedStruct, out ContractModel? nullableContract))
                {
                    shape = ShapeModel.NullableContract(nullableContract);
                    reason = string.Empty;
                    return true;
                }

                shape = null;
                reason = "only nullable enums, Memory<byte>?, and Nullable<nested contract struct> are supported nullable value types";
                return false;
            }

            if (IsMemoryOfByte(named))
            {
                shape = ShapeModel.Memory(allowsNull: false);
                reason = string.Empty;
                return true;
            }

            if (models.TryGetValue(named, out ContractModel? contract))
            {
                if (named.TypeKind == TypeKind.Class)
                    shape = ShapeModel.ContractClass(contract, AllowsNull(type));
                else if (named.TypeKind == TypeKind.Struct)
                    shape = ShapeModel.ContractStruct(contract);
                else
                {
                    shape = null;
                    reason = "the referenced contract is not a class or struct";
                    return false;
                }

                reason = string.Empty;
                return true;
            }

            if (TryGetCollectionKind(named, out CollectionKind collectionKind))
            {
                ITypeSymbol elementType = named.TypeArguments[0];
                if (!TryClassify(elementType, models, out ShapeModel? element, out string elementReason))
                {
                    shape = null;
                    reason = $"collection element type is unsupported: {elementReason}";
                    return false;
                }

                shape = ShapeModel.Collection(collectionKind, element!, elementType, AllowsNull(type));
                reason = string.Empty;
                return true;
            }

            if (TryGetDictionaryKind(named, out DictionaryKind dictionaryKind))
            {
                ITypeSymbol keyType = named.TypeArguments[0];
                ITypeSymbol valueType = named.TypeArguments[1];
                if (!TryClassify(keyType, models, out ShapeModel? key, out string keyReason))
                {
                    shape = null;
                    reason = $"dictionary key type is unsupported: {keyReason}";
                    return false;
                }

                if (!IsCanonicalDictionaryKey(key!))
                {
                    shape = null;
                    reason = "dictionary keys must be non-nullable string, bool, integral, char, Guid, or enum values with a canonical total ordering";
                    return false;
                }

                if (!TryClassify(valueType, models, out ShapeModel? value, out string valueReason))
                {
                    shape = null;
                    reason = $"dictionary value type is unsupported: {valueReason}";
                    return false;
                }

                shape = ShapeModel.Dictionary(
                    dictionaryKind,
                    key!,
                    keyType,
                    value!,
                    valueType,
                    AllowsNull(type));
                reason = string.Empty;
                return true;
            }

            AttributeData? unionAttribute = FindAttribute(named, UnionAttributeName);
            if (unionAttribute is not null)
            {
                if (!TryBuildUnion(named, unionAttribute, models, out UnionModel? union, out reason))
                {
                    shape = null;
                    return false;
                }

                shape = ShapeModel.UnionShape(union!, AllowsNull(type));
                reason = string.Empty;
                return true;
            }

            if (FindAttribute(named, ContractAttributeName) is not null)
            {
                shape = null;
                reason = "the referenced contract is outside this source-generation compilation, so its schema closure is unavailable";
                return false;
            }
        }

        shape = null;
        if (type.TypeKind == TypeKind.Dynamic)
            reason = "dynamic requires runtime polymorphism and is never a binary contract shape";
        else if (type.SpecialType == SpecialType.System_Object)
            reason = "object requires runtime polymorphism; declare an explicit [BinaryUnion] instead";
        else if (type.TypeKind == TypeKind.Delegate)
            reason = "delegates are executable behavior and cannot be serialized";
        else if (type.TypeKind == TypeKind.Pointer || type.TypeKind == TypeKind.FunctionPointer)
            reason = "pointer values are process-local and cannot be serialized";
        else if (type.TypeKind == TypeKind.Interface || (type.TypeKind == TypeKind.Class && type.IsAbstract))
            reason = "interface and abstract-base polymorphism requires an explicit closed [BinaryUnion] declaration";
        else
            reason = "supported shapes are primitives, enums, nullable-aware strings, Memory<byte>, nested contracts, arrays, List<T>/IList<T>, canonical Dictionary<TKey,TValue>/IDictionary<TKey,TValue>, and explicit closed unions";
        return false;
    }

    private static bool AllowsNull(ITypeSymbol type) =>
        type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated;

    private static bool TryBuildUnion(
        INamedTypeSymbol unionType,
        AttributeData attribute,
        Dictionary<INamedTypeSymbol, ContractModel> models,
        out UnionModel? union,
        out string reason)
    {
        if (unionType.TypeKind != TypeKind.Interface &&
            (unionType.TypeKind != TypeKind.Class || !unionType.IsAbstract))
        {
            union = null;
            reason = "[BinaryUnion] is valid only on an interface or abstract base class";
            return false;
        }

        if (attribute.ConstructorArguments.Length != 1 ||
            attribute.ConstructorArguments[0].Kind != TypedConstantKind.Array)
        {
            union = null;
            reason = "[BinaryUnion] must declare its complete case list";
            return false;
        }

        List<UnionCaseModel> cases = new();
        HashSet<INamedTypeSymbol> seen = new(SymbolEqualityComparer.Default);
        HashSet<uint> tags = new();
        foreach (TypedConstant item in attribute.ConstructorArguments[0].Values)
        {
            if (item.Value is not INamedTypeSymbol caseType)
            {
                union = null;
                reason = "every [BinaryUnion] case must be a named type";
                return false;
            }

            if (!seen.Add(caseType))
            {
                union = null;
                reason = $"union case '{StableName(caseType)}' is duplicated";
                return false;
            }

            if (!models.TryGetValue(caseType, out ContractModel? contract))
            {
                union = null;
                reason = $"union case '{StableName(caseType)}' must be a [BinaryContract] in this compilation";
                return false;
            }

            if (caseType.TypeKind != TypeKind.Class || caseType.IsAbstract || !caseType.IsSealed)
            {
                union = null;
                reason = $"union case '{StableName(caseType)}' must be a sealed, concrete class";
                return false;
            }

            if (!IsAssignableTo(caseType, unionType))
            {
                union = null;
                reason = $"union case '{StableName(caseType)}' is not assignable to '{StableName(unionType)}'";
                return false;
            }

            AttributeData? caseAttribute = FindAttribute(caseType, UnionCaseAttributeName);
            if (caseAttribute is null ||
                caseAttribute.ConstructorArguments.Length != 1 ||
                caseAttribute.ConstructorArguments[0].Value is null)
            {
                union = null;
                reason = $"union case '{StableName(caseType)}' must declare an explicit [BinaryUnionCase(tag)]";
                return false;
            }

            uint tag = Convert.ToUInt32(caseAttribute.ConstructorArguments[0].Value, CultureInfo.InvariantCulture);
            if (tag == 0)
            {
                union = null;
                reason = $"union case '{StableName(caseType)}' uses reserved tag zero";
                return false;
            }
            if (!tags.Add(tag))
            {
                union = null;
                reason = $"binary union tag {tag.ToString(CultureInfo.InvariantCulture)} is duplicated";
                return false;
            }

            cases.Add(new UnionCaseModel(tag, contract));
        }

        if (cases.Count == 0)
        {
            union = null;
            reason = "a binary union must declare at least one case";
            return false;
        }

        cases.Sort(static (left, right) => left.Tag.CompareTo(right.Tag));
        union = new UnionModel(unionType, cases);
        reason = string.Empty;
        return true;
    }

    private static bool IsAssignableTo(INamedTypeSymbol candidate, INamedTypeSymbol target)
    {
        if (target.TypeKind == TypeKind.Interface)
            return candidate.AllInterfaces.Any(item => SymbolEqualityComparer.Default.Equals(item, target));

        for (INamedTypeSymbol? current = candidate.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, target))
                return true;
        }

        return false;
    }

    private static bool IsCanonicalDictionaryKey(ShapeModel shape)
    {
        if (shape.Kind == ShapeKind.String)
            return !shape.AllowsNull;
        if (shape.Kind == ShapeKind.Enum)
            return true;
        if (shape.Kind != ShapeKind.Primitive)
            return false;
        return shape.PrimitiveKind != PrimitiveKind.Single && shape.PrimitiveKind != PrimitiveKind.Double;
    }

    private static PrimitiveKind ClassifyPrimitive(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return PrimitiveKind.Boolean;
            case SpecialType.System_Byte: return PrimitiveKind.Byte;
            case SpecialType.System_SByte: return PrimitiveKind.SByte;
            case SpecialType.System_Int16: return PrimitiveKind.Int16;
            case SpecialType.System_UInt16: return PrimitiveKind.UInt16;
            case SpecialType.System_Int32: return PrimitiveKind.Int32;
            case SpecialType.System_UInt32: return PrimitiveKind.UInt32;
            case SpecialType.System_Int64: return PrimitiveKind.Int64;
            case SpecialType.System_UInt64: return PrimitiveKind.UInt64;
            case SpecialType.System_Single: return PrimitiveKind.Single;
            case SpecialType.System_Double: return PrimitiveKind.Double;
            case SpecialType.System_Char: return PrimitiveKind.Char;
        }

        if (type is INamedTypeSymbol named &&
            string.Equals(named.Name, "Guid", StringComparison.Ordinal) &&
            string.Equals(named.ContainingNamespace?.ToDisplayString(), "System", StringComparison.Ordinal))
        {
            return PrimitiveKind.Guid;
        }

        return PrimitiveKind.None;
    }

    private static bool IsNullable(INamedTypeSymbol named, out ITypeSymbol? valueType)
    {
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && named.TypeArguments.Length == 1)
        {
            valueType = named.TypeArguments[0];
            return true;
        }

        valueType = null;
        return false;
    }

    private static bool IsMemoryOfByte(ITypeSymbol type)
    {
        return type is INamedTypeSymbol named &&
               named.Arity == 1 &&
               string.Equals(named.Name, "Memory", StringComparison.Ordinal) &&
               string.Equals(named.ContainingNamespace?.ToDisplayString(), "System", StringComparison.Ordinal) &&
               named.TypeArguments[0].SpecialType == SpecialType.System_Byte;
    }

    private static bool TryGetCollectionKind(INamedTypeSymbol type, out CollectionKind kind)
    {
        if (type.Arity == 1 &&
            string.Equals(type.ContainingNamespace?.ToDisplayString(), "System.Collections.Generic", StringComparison.Ordinal))
        {
            if (type.TypeKind == TypeKind.Class && string.Equals(type.Name, "List", StringComparison.Ordinal))
            {
                kind = CollectionKind.List;
                return true;
            }
            if (type.TypeKind == TypeKind.Interface && string.Equals(type.Name, "IList", StringComparison.Ordinal))
            {
                kind = CollectionKind.IList;
                return true;
            }
        }

        kind = default;
        return false;
    }

    private static bool TryGetDictionaryKind(INamedTypeSymbol type, out DictionaryKind kind)
    {
        if (type.Arity == 2 &&
            string.Equals(type.ContainingNamespace?.ToDisplayString(), "System.Collections.Generic", StringComparison.Ordinal))
        {
            if (type.TypeKind == TypeKind.Class && string.Equals(type.Name, "Dictionary", StringComparison.Ordinal))
            {
                kind = DictionaryKind.Dictionary;
                return true;
            }
            if (type.TypeKind == TypeKind.Interface && string.Equals(type.Name, "IDictionary", StringComparison.Ordinal))
            {
                kind = DictionaryKind.IDictionary;
                return true;
            }
        }

        kind = default;
        return false;
    }

    private static void RejectDuplicateLogicalTypeNames(
        SourceProductionContext context,
        IEnumerable<ContractModel> models)
    {
        foreach (IGrouping<string, ContractModel> duplicate in models
                     .GroupBy(static model => model.LogicalName, StringComparer.Ordinal)
                     .Where(static group => group.Count() > 1))
        {
            string contracts = string.Join(", ", duplicate.Select(static model => $"'{StableName(model.Symbol)}'"));
            foreach (ContractModel model in duplicate)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidLogicalName,
                    ContractLocation(model.Symbol),
                    StableName(model.Symbol),
                    $"logical contract name '{duplicate.Key}' is shared by {contracts}"));
                model.IsValid = false;
            }
        }
    }

    private static void RejectDuplicateTypeIds(
        SourceProductionContext context,
        IEnumerable<ContractModel> models)
    {
        foreach (IGrouping<string, ContractModel> collision in models
                     .GroupBy(static model => model.TypeId, StringComparer.Ordinal)
                     .Where(static group => group.Select(model => model.LogicalName).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            string contracts = string.Join(", ", collision.Select(static model => $"'{StableName(model.Symbol)}'"));
            foreach (ContractModel model in collision)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DuplicateTypeId,
                    ContractLocation(model.Symbol),
                    StableName(model.Symbol),
                    collision.Key,
                    contracts));
                model.IsValid = false;
            }
        }
    }

    private static void RejectCycles(SourceProductionContext context, IEnumerable<ContractModel> source)
    {
        List<ContractModel> models = source.ToList();
        Dictionary<ContractModel, VisitState> states = new();
        List<ContractModel> stack = new();
        foreach (ContractModel model in models)
        {
            if (!states.ContainsKey(model))
                VisitContract(model, states, stack);
        }

        foreach (ContractModel model in models.Where(static model => model.CycleDescription is not null))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                CyclicContractGraph,
                ContractLocation(model.Symbol),
                StableName(model.Symbol),
                model.CycleDescription));
            model.IsValid = false;
        }
    }

    private static void VisitContract(
        ContractModel model,
        Dictionary<ContractModel, VisitState> states,
        List<ContractModel> stack)
    {
        states[model] = VisitState.Visiting;
        stack.Add(model);
        foreach (ContractModel dependency in model.Members
                     .SelectMany(static member => EnumerateNestedContracts(member.Shape))
                     .Distinct())
        {
            if (!states.TryGetValue(dependency, out VisitState state))
            {
                VisitContract(dependency, states, stack);
                continue;
            }

            if (state != VisitState.Visiting)
                continue;

            int start = stack.IndexOf(dependency);
            if (start < 0)
                continue;
            string cycle = string.Join(
                " -> ",
                stack.Skip(start).Select(static item => item.LogicalName).Concat(new[] { dependency.LogicalName }));
            for (int index = start; index < stack.Count; index++)
                stack[index].CycleDescription = stack[index].CycleDescription ?? cycle;
        }

        stack.RemoveAt(stack.Count - 1);
        states[model] = VisitState.Visited;
    }

    private static void RejectInvalidClosures(
        SourceProductionContext context,
        IEnumerable<ContractModel> source)
    {
        List<ContractModel> models = source.ToList();
        bool changed;
        do
        {
            changed = false;
            foreach (ContractModel model in models.Where(static model => model.IsValid))
            {
                MemberModel? invalidMember = model.Members.FirstOrDefault(
                    static member => EnumerateNestedContracts(member.Shape).Any(nested => !nested.IsValid));
                if (invalidMember is null)
                    continue;

                ContractModel nested = EnumerateNestedContracts(invalidMember.Shape)
                    .First(static item => !item.IsValid);
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidNestedContract,
                    MemberLocation(invalidMember.Symbol),
                    invalidMember.Symbol.ToDisplayString(),
                    StableName(nested.Symbol)));
                model.IsValid = false;
                changed = true;
            }
        }
        while (changed);
    }

    private static bool RequiresRuntimeFingerprint(
        ContractModel model,
        HashSet<ContractModel> visiting)
    {
        if (model.IsManuallyImplemented)
            return true;
        if (model.RequiresRuntimeFingerprint.HasValue)
            return model.RequiresRuntimeFingerprint.Value;
        if (!visiting.Add(model))
            throw new InvalidOperationException("A binary-contract cycle escaped validation.");

        bool result = model.Members.Any(member =>
            EnumerateNestedContracts(member.Shape)
                .Any(nested => RequiresRuntimeFingerprint(nested, visiting)));
        visiting.Remove(model);
        model.RequiresRuntimeFingerprint = result;
        return result;
    }

    private static IEnumerable<ContractModel> EnumerateNestedContracts(ShapeModel shape)
    {
        if (shape.NestedContract is not null)
            yield return shape.NestedContract;
        if (shape.Element is not null)
        {
            foreach (ContractModel nested in EnumerateNestedContracts(shape.Element))
                yield return nested;
        }
        if (shape.Key is not null)
        {
            foreach (ContractModel nested in EnumerateNestedContracts(shape.Key))
                yield return nested;
        }
        if (shape.Value is not null)
        {
            foreach (ContractModel nested in EnumerateNestedContracts(shape.Value))
                yield return nested;
        }
        if (shape.Union is not null)
        {
            foreach (UnionCaseModel unionCase in shape.Union.Cases)
                yield return unionCase.Contract;
        }
    }

    private static ulong ComputeFingerprint(ContractModel model)
    {
        if (model.IsManuallyImplemented)
            throw new InvalidOperationException("A manually implemented contract has no generator-computable fingerprint.");
        if (model.Fingerprint.HasValue)
            return model.Fingerprint.Value;

        WriteDescriptorSetr descriptor = new();
        descriptor.WriteToken("SomeEngine.BinaryContract.v2");
        descriptor.WriteToken(model.LogicalName);
        descriptor.WriteByte((byte)model.Compatibility);
        descriptor.WriteUInt32(model.Epoch);
        descriptor.WriteByte(model.Symbol.TypeKind == TypeKind.Class ? (byte)1 : (byte)2);
        descriptor.WriteUInt32(checked((uint)model.Members.Count));
        foreach (MemberModel member in model.Members)
        {
            descriptor.WriteToken(member.LogicalName);
            descriptor.WriteUInt64(member.FieldKey);
            WriteShapeDescriptor(descriptor, member.Shape);
        }

        byte[] hash;
        using (SHA256 sha = SHA256.Create())
            hash = sha.ComputeHash(descriptor.ToArray());
        ulong fingerprint = 0;
        for (int index = 0; index < sizeof(ulong); index++)
            fingerprint = (fingerprint << 8) | hash[index];
        model.Fingerprint = fingerprint;
        return fingerprint;
    }

    private static void WriteShapeDescriptor(WriteDescriptorSetr descriptor, ShapeModel shape)
    {
        descriptor.WriteByte((byte)shape.Kind);
        switch (shape.Kind)
        {
            case ShapeKind.Primitive:
                descriptor.WriteByte((byte)shape.PrimitiveKind);
                break;
            case ShapeKind.Enum:
            case ShapeKind.NullableEnum:
                descriptor.WriteToken(StableName(shape.EnumType!));
                descriptor.WriteByte((byte)shape.PrimitiveKind);
                IFieldSymbol[] values = shape.EnumType!.GetMembers()
                    .OfType<IFieldSymbol>()
                    .Where(static field => field.HasConstantValue && !field.IsImplicitlyDeclared)
                    .OrderBy(static field => field.Name, StringComparer.Ordinal)
                    .ToArray();
                descriptor.WriteUInt32(checked((uint)values.Length));
                foreach (IFieldSymbol value in values)
                {
                    descriptor.WriteToken(value.Name);
                    descriptor.WriteUInt64(EnumConstantBits(value.ConstantValue!, shape.PrimitiveKind));
                }
                break;
            case ShapeKind.String:
            case ShapeKind.Memory:
                descriptor.WriteByte(shape.AllowsNull ? (byte)1 : (byte)0);
                break;
            case ShapeKind.ContractClass:
                descriptor.WriteByte(shape.AllowsNull ? (byte)1 : (byte)0);
                descriptor.WriteToken(shape.NestedContract!.LogicalName);
                descriptor.WriteUInt64(ComputeFingerprint(shape.NestedContract));
                break;
            case ShapeKind.ContractStruct:
            case ShapeKind.NullableContractStruct:
                descriptor.WriteToken(shape.NestedContract!.LogicalName);
                descriptor.WriteUInt64(ComputeFingerprint(shape.NestedContract));
                break;
            case ShapeKind.Collection:
                descriptor.WriteByte((byte)shape.CollectionKind);
                descriptor.WriteByte(shape.AllowsNull ? (byte)1 : (byte)0);
                WriteShapeDescriptor(descriptor, shape.Element!);
                break;
            case ShapeKind.Dictionary:
                descriptor.WriteByte((byte)shape.DictionaryKind);
                descriptor.WriteByte(shape.AllowsNull ? (byte)1 : (byte)0);
                WriteShapeDescriptor(descriptor, shape.Key!);
                WriteShapeDescriptor(descriptor, shape.Value!);
                break;
            case ShapeKind.Union:
                descriptor.WriteByte(shape.AllowsNull ? (byte)1 : (byte)0);
                descriptor.WriteToken(StableName(shape.Union!.Symbol));
                descriptor.WriteUInt32(checked((uint)shape.Union.Cases.Count));
                foreach (UnionCaseModel unionCase in shape.Union.Cases)
                {
                    descriptor.WriteUInt32(unionCase.Tag);
                    descriptor.WriteToken(unionCase.Contract.LogicalName);
                    descriptor.WriteUInt64(ComputeFingerprint(unionCase.Contract));
                }
                break;
            default:
                throw new InvalidOperationException($"Unknown binary shape {shape.Kind}.");
        }
    }

    private static ulong EnumConstantBits(object value, PrimitiveKind underlying)
    {
        switch (underlying)
        {
            case PrimitiveKind.SByte:
            case PrimitiveKind.Int16:
            case PrimitiveKind.Int32:
            case PrimitiveKind.Int64:
                return unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture));
            default:
                return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
        }
    }

    private static string ComputeTypeId(string logicalName)
    {
        byte[] hash;
        using (SHA256 sha = SHA256.Create())
            hash = sha.ComputeHash(Encoding.UTF8.GetBytes(logicalName));
        StringBuilder value = new(36);
        for (int index = 0; index < 16; index++)
        {
            if (index == 4 || index == 6 || index == 8 || index == 10)
                value.Append('-');
            value.Append(hash[index].ToString("x2", CultureInfo.InvariantCulture));
        }

        return value.ToString();
    }

    private static ulong ComputeFieldKey(string logicalName)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        byte[] bytes = Encoding.UTF8.GetBytes(logicalName);
        ulong hash = offsetBasis;
        foreach (byte value in bytes)
        {
            hash ^= value;
            hash *= prime;
        }

        return hash;
    }

    private static string EmitContract(ContractModel model, string catalogTypeName)
    {
        StringBuilder source = new();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable enable");
        source.AppendLine();
        if (!model.Symbol.ContainingNamespace.IsGlobalNamespace)
        {
            source.Append("namespace ").Append(model.Symbol.ContainingNamespace.ToDisplayString()).AppendLine(";");
            source.AppendLine();
        }

        List<INamedTypeSymbol> containingTypes = new();
        for (INamedTypeSymbol? containing = model.Symbol.ContainingType;
             containing is not null;
             containing = containing.ContainingType)
        {
            containingTypes.Add(containing);
        }
        containingTypes.Reverse();

        int indent = 0;
        foreach (INamedTypeSymbol containing in containingTypes)
        {
            AppendIndent(source, indent).AppendLine(PartialTypeHeader(containing, interfaceType: null));
            AppendIndent(source, indent).AppendLine("{");
            indent++;
        }

        AppendIndent(source, indent).AppendLine(PartialTypeHeader(model.Symbol, model.Symbol));
        AppendIndent(source, indent).AppendLine("{");
        indent++;

        AppendIndent(source, indent)
            .Append("public static global::System.Guid TypeId => new global::System.Guid(")
            .Append(Literal(model.TypeId)).AppendLine(");");
        if (model.RequiresRuntimeFingerprint == true)
        {
            AppendIndent(source, indent)
                .Append("public static ulong SchemaFingerprint { get; } = ")
                .Append(GeneratedMemberPrefix).AppendLine("ComputeSchemaFingerprint();");
        }
        else
        {
            AppendIndent(source, indent)
                .Append("public static ulong SchemaFingerprint => 0x")
                .Append(model.Fingerprint!.Value.ToString("X16", CultureInfo.InvariantCulture)).AppendLine("UL;");
        }
        AppendIndent(source, indent)
            .AppendLine("public static global::SomeEngine.Serialization.BinaryCompatibility Compatibility => global::SomeEngine.Serialization.BinaryCompatibility.ExactSchema;");
        AppendIndent(source, indent)
            .Append("public static uint SchemaEpoch => ")
            .Append(model.Epoch.ToString(CultureInfo.InvariantCulture)).AppendLine("U;");
        foreach (ChunkReferenceModel chunk in model.ChunkReferences)
        {
            AppendIndent(source, indent)
                .Append("public global::SomeEngine.Serialization.Containers.BinaryChunkRef ")
                .Append(chunk.AccessorName).Append(" => new global::SomeEngine.Serialization.Containers.BinaryChunkRef(")
                .Append("this.").Append(Escape(chunk.Key.Symbol.Name)).Append(", checked((long)this.")
                .Append(Escape(chunk.DecodedLength.Symbol.Name)).AppendLine("));");
        }
        if (model.NativeLayout is NativeLayoutModel nativeLayout)
        {
            string contractType = model.Symbol.ToDisplayString(QualifiedTypeFormat);
            AppendIndent(source, indent)
                .Append("public static global::SomeEngine.Serialization.NativeLayoutProof<")
                .Append(contractType)
                .AppendLine("> NativeLayoutProof { get; } =");
            AppendIndent(source, indent + 1)
                .Append("global::SomeEngine.Serialization.NativeLayoutProof<")
                .Append(contractType)
                .AppendLine(">.CreateGenerated(");
            AppendIndent(source, indent + 2)
                .Append("0x")
                .Append(nativeLayout.LayoutFingerprint.ToString("X16", CultureInfo.InvariantCulture))
                .AppendLine("UL,");
            AppendIndent(source, indent + 2)
                .Append(nativeLayout.GeneratedSize.ToString(CultureInfo.InvariantCulture))
                .AppendLine(",");
            AppendIndent(source, indent + 2)
                .Append(nativeLayout.CoveredFieldBytes.ToString(CultureInfo.InvariantCulture))
                .AppendLine(",");
            AppendIndent(source, indent + 2)
                .Append(nativeLayout.RequiredAlignment.ToString(CultureInfo.InvariantCulture))
                .AppendLine(",");
            AppendIndent(source, indent + 2)
                .Append(Literal(nativeLayout.AbiToken))
                .AppendLine(");");
        }
        source.AppendLine();

        EmitWriteMethod(source, model, catalogTypeName, indent);
        source.AppendLine();
        EmitReadMethod(source, model, indent);
        source.AppendLine();
        EmitViewSurface(source, model, indent);
        foreach (MemberModel member in model.Members)
        {
            source.AppendLine();
            EmitMemberHelpers(source, member, indent);
        }
        if (model.RequiresRuntimeFingerprint == true)
        {
            source.AppendLine();
            EmitRuntimeFingerprintHelpers(source, model, indent);
        }

        indent--;
        AppendIndent(source, indent).AppendLine("}");
        for (int index = containingTypes.Count - 1; index >= 0; index--)
        {
            indent--;
            AppendIndent(source, indent).AppendLine("}");
        }

        return source.ToString();
    }

    private static void EmitWriteMethod(
        StringBuilder source,
        ContractModel model,
        string catalogTypeName,
        int indent)
    {
        string contractType = model.Symbol.ToDisplayString(QualifiedTypeFormat);
        AppendIndent(source, indent)
            .Append("public static void Write(ref global::SomeEngine.Serialization.BinaryDataWriter writer, ")
            .Append(contractType).AppendLine(" value)");
        AppendIndent(source, indent).AppendLine("{");
        if (model.Symbol.TypeKind == TypeKind.Class)
        {
            AppendIndent(source, indent + 1).AppendLine("if (value is null)");
            AppendIndent(source, indent + 2).AppendLine("throw new global::System.ArgumentNullException(nameof(value));");
        }
        AppendIndent(source, indent + 1).Append(catalogTypeName).AppendLine(".EnterWriteObject();");
        AppendIndent(source, indent + 1).AppendLine("try");
        AppendIndent(source, indent + 1).AppendLine("{");

        foreach (MemberModel member in model.Members)
        {
            AppendIndent(source, indent + 2)
                .Append(WriteHelperName(member)).Append("(ref writer, value.")
                .Append(Escape(member.Symbol.Name)).AppendLine(");");
        }

        AppendIndent(source, indent + 1).AppendLine("}");
        AppendIndent(source, indent + 1).AppendLine("finally");
        AppendIndent(source, indent + 1).AppendLine("{");
        AppendIndent(source, indent + 2).Append(catalogTypeName).AppendLine(".ExitWriteObject();");
        AppendIndent(source, indent + 1).AppendLine("}");
        AppendIndent(source, indent).AppendLine("}");
    }

    private static void EmitReadMethod(StringBuilder source, ContractModel model, int indent)
    {
        string contractType = model.Symbol.ToDisplayString(QualifiedTypeFormat);
        AppendIndent(source, indent)
            .Append("public static ").Append(contractType)
            .AppendLine(" Read(ref global::SomeEngine.Serialization.BinaryDataReader reader)");
        AppendIndent(source, indent).AppendLine("{");
        AppendIndent(source, indent + 1).AppendLine("reader.EnterObject();");
        AppendIndent(source, indent + 1).AppendLine("try");
        AppendIndent(source, indent + 1).AppendLine("{");

        foreach (MemberModel member in model.Members)
        {
            AppendIndent(source, indent + 2)
                .Append(member.Type.ToDisplayString(QualifiedTypeFormat)).Append(" __field")
                .Append(member.Index.ToString(CultureInfo.InvariantCulture))
                .Append(" = ").Append(ReadHelperName(member)).AppendLine("(ref reader);");
        }

        EmitConstruction(source, model, contractType, indent + 2);
        AppendIndent(source, indent + 1).AppendLine("}");
        AppendIndent(source, indent + 1).AppendLine("finally");
        AppendIndent(source, indent + 1).AppendLine("{");
        AppendIndent(source, indent + 2).AppendLine("reader.ExitObject();");
        AppendIndent(source, indent + 1).AppendLine("}");
        AppendIndent(source, indent).AppendLine("}");
    }

    private static bool RequiresNonNullReference(ShapeModel shape) =>
        !shape.AllowsNull &&
        (shape.Kind == ShapeKind.String ||
         shape.Kind == ShapeKind.ContractClass ||
         shape.Kind == ShapeKind.Collection ||
         shape.Kind == ShapeKind.Dictionary ||
         shape.Kind == ShapeKind.Union);

    private static void EmitConstruction(
        StringBuilder source,
        ContractModel model,
        string contractType,
        int indent)
    {
        if (model.Symbol.TypeKind == TypeKind.Class)
        {
            AppendIndent(source, indent).Append("reader.ReserveAllocation(32L");
            foreach (MemberModel member in model.Members)
            {
                source.Append(" + global::System.Math.Max(global::System.IntPtr.Size, global::System.Runtime.CompilerServices.Unsafe.SizeOf<")
                    .Append(member.Type.ToDisplayString(QualifiedTypeFormat)).Append(">())");
            }
            source.Append(", ").Append(Literal($"contract '{model.LogicalName}'"))
                .AppendLine(");");
        }

        if (model.Members.Count == 0)
        {
            AppendIndent(source, indent).Append("return new ").Append(contractType).AppendLine("();");
            return;
        }

        AppendIndent(source, indent).Append("return new ").Append(contractType).AppendLine();
        AppendIndent(source, indent).AppendLine("{");
        foreach (MemberModel member in model.Members)
        {
            AppendIndent(source, indent + 1).Append(Escape(member.Symbol.Name)).Append(" = __field")
                .Append(member.Index.ToString(CultureInfo.InvariantCulture)).AppendLine(",");
        }
        AppendIndent(source, indent).AppendLine("};");
    }

    private static void EmitMemberHelpers(StringBuilder source, MemberModel member, int indent)
    {
        string typeName = member.Type.ToDisplayString(QualifiedTypeFormat);
        AppendIndent(source, indent).Append("private static void ").Append(WriteHelperName(member))
            .Append("(ref global::SomeEngine.Serialization.BinaryDataWriter writer, ")
            .Append(typeName).AppendLine(" value)");
        AppendIndent(source, indent).AppendLine("{");
        EmitWriteValue(source, member.Shape, "value", indent + 1, member.Index.ToString(CultureInfo.InvariantCulture));
        AppendIndent(source, indent).AppendLine("}");
        source.AppendLine();
        AppendIndent(source, indent).Append("private static ").Append(typeName).Append(' ')
            .Append(ReadHelperName(member))
            .AppendLine("(ref global::SomeEngine.Serialization.BinaryDataReader reader)");
        AppendIndent(source, indent).AppendLine("{");
        AppendIndent(source, indent + 1).Append(typeName).AppendLine(" result = default!;");
        EmitReadValue(source, member.Shape, "result", indent + 1, member.Index.ToString(CultureInfo.InvariantCulture));
        AppendIndent(source, indent + 1).AppendLine("return result;");
        AppendIndent(source, indent).AppendLine("}");
    }

    private static void EmitWriteValue(
        StringBuilder source,
        ShapeModel shape,
        string expression,
        int indent,
        string suffix)
    {
        switch (shape.Kind)
        {
            case ShapeKind.Primitive:
                AppendIndent(source, indent).Append("writer.").Append(WriterMethod(shape.PrimitiveKind))
                    .Append('(').Append(expression).AppendLine(");");
                return;
            case ShapeKind.Enum:
                AppendIndent(source, indent).Append("writer.").Append(WriterMethod(shape.PrimitiveKind))
                    .Append("((").Append(PrimitiveType(shape.PrimitiveKind)).Append(')')
                    .Append(expression).AppendLine(");");
                return;
            case ShapeKind.NullableEnum:
                AppendIndent(source, indent).Append("if (!").Append(expression).AppendLine(".HasValue)");
                AppendIndent(source, indent).AppendLine("{");
                AppendIndent(source, indent + 1).AppendLine("writer.WriteBoolean(false);");
                AppendIndent(source, indent).AppendLine("}");
                AppendIndent(source, indent).AppendLine("else");
                AppendIndent(source, indent).AppendLine("{");
                AppendIndent(source, indent + 1).AppendLine("writer.WriteBoolean(true);");
                AppendIndent(source, indent + 1).Append("writer.").Append(WriterMethod(shape.PrimitiveKind))
                    .Append("((").Append(PrimitiveType(shape.PrimitiveKind)).Append(')')
                    .Append(expression).AppendLine(".Value);");
                AppendIndent(source, indent).AppendLine("}");
                return;
            case ShapeKind.String:
                if (!shape.AllowsNull)
                {
                    AppendIndent(source, indent).Append("if (").Append(expression).AppendLine(" is null)");
                    AppendIndent(source, indent + 1)
                        .AppendLine("throw new global::System.IO.InvalidDataException(\"A non-nullable string contract value cannot be null.\");");
                }
                AppendIndent(source, indent).Append("writer.WriteString(").Append(expression).AppendLine(");");
                return;
            case ShapeKind.Memory:
                if (shape.AllowsNull)
                {
                    AppendIndent(source, indent).Append("writer.WriteMemory(").Append(expression)
                        .Append(".HasValue ? (global::System.ReadOnlyMemory<byte>?)")
                        .Append(expression).AppendLine(".Value : null);");
                }
                else
                {
                    AppendIndent(source, indent).Append("writer.WriteMemory((global::System.ReadOnlyMemory<byte>)")
                        .Append(expression).AppendLine(");");
                }
                return;
            case ShapeKind.ContractClass:
                AppendIndent(source, indent).Append("if (").Append(expression).AppendLine(" is null)");
                AppendIndent(source, indent).AppendLine("{");
                if (shape.AllowsNull)
                    AppendIndent(source, indent + 1).AppendLine("writer.WriteBoolean(false);");
                else
                    AppendIndent(source, indent + 1).AppendLine("throw new global::System.IO.InvalidDataException(\"A non-nullable nested contract value cannot be null.\");");
                AppendIndent(source, indent).AppendLine("}");
                AppendIndent(source, indent).AppendLine("else");
                AppendIndent(source, indent).AppendLine("{");
                AppendIndent(source, indent + 1).AppendLine("writer.WriteBoolean(true);");
                AppendIndent(source, indent + 1).Append(shape.NestedContract!.Symbol.ToDisplayString(QualifiedTypeFormat))
                    .Append(".Write(ref writer, ").Append(expression).AppendLine(");");
                AppendIndent(source, indent).AppendLine("}");
                return;
            case ShapeKind.ContractStruct:
                AppendIndent(source, indent).Append(shape.NestedContract!.Symbol.ToDisplayString(QualifiedTypeFormat))
                    .Append(".Write(ref writer, ").Append(expression).AppendLine(");");
                return;
            case ShapeKind.NullableContractStruct:
                AppendIndent(source, indent).Append("if (!").Append(expression).AppendLine(".HasValue)");
                AppendIndent(source, indent).AppendLine("{");
                AppendIndent(source, indent + 1).AppendLine("writer.WriteBoolean(false);");
                AppendIndent(source, indent).AppendLine("}");
                AppendIndent(source, indent).AppendLine("else");
                AppendIndent(source, indent).AppendLine("{");
                AppendIndent(source, indent + 1).AppendLine("writer.WriteBoolean(true);");
                AppendIndent(source, indent + 1).Append(shape.NestedContract!.Symbol.ToDisplayString(QualifiedTypeFormat))
                    .Append(".Write(ref writer, ").Append(expression).AppendLine(".Value);");
                AppendIndent(source, indent).AppendLine("}");
                return;
            case ShapeKind.Collection:
                AppendIndent(source, indent).Append("if (").Append(expression).AppendLine(" is null)");
                AppendIndent(source, indent).AppendLine("{");
                if (shape.AllowsNull)
                    AppendIndent(source, indent + 1).AppendLine("writer.WriteBoolean(false);");
                else
                    AppendIndent(source, indent + 1).AppendLine("throw new global::System.IO.InvalidDataException(\"A non-nullable collection contract value cannot be null.\");");
                AppendIndent(source, indent).AppendLine("}");
                AppendIndent(source, indent).AppendLine("else");
                AppendIndent(source, indent).AppendLine("{");
                AppendIndent(source, indent + 1).AppendLine("writer.WriteBoolean(true);");
                AppendIndent(source, indent + 1).Append("int __elementCount").Append(suffix)
                    .Append(" = ").Append(expression)
                    .Append(shape.CollectionKind == CollectionKind.Array ? ".Length" : ".Count").AppendLine(";");
                AppendIndent(source, indent + 1).Append("writer.WriteInt32(__elementCount")
                    .Append(suffix).AppendLine(");");
                AppendIndent(source, indent + 1).Append("for (int __elementIndex").Append(suffix)
                    .Append(" = 0; __elementIndex").Append(suffix).Append(" < __elementCount").Append(suffix)
                    .Append("; __elementIndex").Append(suffix).AppendLine("++)");
                AppendIndent(source, indent + 1).AppendLine("{");
                AppendIndent(source, indent + 2).Append(shape.ElementType!.ToDisplayString(QualifiedTypeFormat))
                    .Append(" __element").Append(suffix).Append(" = ").Append(expression)
                    .Append("[__elementIndex").Append(suffix).AppendLine("];");
                EmitWriteValue(source, shape.Element!, "__element" + suffix, indent + 2, suffix + "Element");
                AppendIndent(source, indent + 1).AppendLine("}");
                AppendIndent(source, indent).AppendLine("}");
                return;
            case ShapeKind.Dictionary:
                EmitWriteDictionary(source, shape, expression, indent, suffix);
                return;
            case ShapeKind.Union:
                EmitWriteUnion(source, shape, expression, indent, suffix);
                return;
            default:
                throw new InvalidOperationException($"Unknown binary shape {shape.Kind}.");
        }
    }

    private static void EmitReadValue(
        StringBuilder source,
        ShapeModel shape,
        string target,
        int indent,
        string suffix)
    {
        switch (shape.Kind)
        {
            case ShapeKind.Primitive:
                AppendIndent(source, indent).Append(target).Append(" = reader.")
                    .Append(ReaderMethod(shape.PrimitiveKind)).AppendLine("();");
                return;
            case ShapeKind.Enum:
                AppendIndent(source, indent).Append(target).Append(" = (")
                    .Append(shape.EnumType!.ToDisplayString(QualifiedTypeFormat)).Append(")reader.")
                    .Append(ReaderMethod(shape.PrimitiveKind)).AppendLine("();");
                return;
            case ShapeKind.NullableEnum:
                AppendIndent(source, indent).AppendLine("if (!reader.ReadBoolean())");
                AppendIndent(source, indent).AppendLine("{");
                AppendIndent(source, indent + 1).Append(target).AppendLine(" = null;");
                AppendIndent(source, indent).AppendLine("}");
                AppendIndent(source, indent).AppendLine("else");
                AppendIndent(source, indent).AppendLine("{");
                AppendIndent(source, indent + 1).Append(target).Append(" = (")
                    .Append(shape.EnumType!.ToDisplayString(QualifiedTypeFormat)).Append(")reader.")
                    .Append(ReaderMethod(shape.PrimitiveKind)).AppendLine("();");
                AppendIndent(source, indent).AppendLine("}");
                return;
            case ShapeKind.String:
                AppendIndent(source, indent).Append(target).Append(" = reader.ReadString()");
                if (!shape.AllowsNull)
                    source.Append(" ?? throw new global::System.IO.InvalidDataException(\"A non-nullable string contract value was encoded as null.\")");
                source.AppendLine(";");
                return;
            case ShapeKind.Memory:
                if (shape.AllowsNull)
                {
                    AppendIndent(source, indent).Append(target).AppendLine(" = reader.ReadMemory();");
                }
                else
                {
                    AppendIndent(source, indent).Append("global::System.Memory<byte>? __memory").Append(suffix)
                        .AppendLine(" = reader.ReadMemory();");
                    AppendIndent(source, indent).Append("if (!__memory").Append(suffix).AppendLine(".HasValue)");
                    AppendIndent(source, indent + 1)
                        .AppendLine("throw new global::System.IO.InvalidDataException(\"A non-nullable Memory<byte> contract value was encoded as null.\");");
                    AppendIndent(source, indent).Append(target).Append(" = __memory").Append(suffix).AppendLine(".Value;");
                }
                return;
            case ShapeKind.ContractClass:
                AppendIndent(source, indent).AppendLine("if (!reader.ReadBoolean())");
                AppendIndent(source, indent).AppendLine("{");
                if (shape.AllowsNull)
                    AppendIndent(source, indent + 1).Append(target).AppendLine(" = default!;");
                else
                    AppendIndent(source, indent + 1).AppendLine("throw new global::System.IO.InvalidDataException(\"A non-nullable nested contract value was encoded as null.\");");
                AppendIndent(source, indent).AppendLine("}");
                AppendIndent(source, indent).AppendLine("else");
                AppendIndent(source, indent).AppendLine("{");
                AppendIndent(source, indent + 1).Append(target).Append(" = ")
                    .Append(shape.NestedContract!.Symbol.ToDisplayString(QualifiedTypeFormat))
                    .AppendLine(".Read(ref reader);");
                AppendIndent(source, indent).AppendLine("}");
                return;
            case ShapeKind.ContractStruct:
                AppendIndent(source, indent).Append(target).Append(" = ")
                    .Append(shape.NestedContract!.Symbol.ToDisplayString(QualifiedTypeFormat))
                    .AppendLine(".Read(ref reader);");
                return;
            case ShapeKind.NullableContractStruct:
                AppendIndent(source, indent).AppendLine("if (!reader.ReadBoolean())");
                AppendIndent(source, indent).AppendLine("{");
                AppendIndent(source, indent + 1).Append(target).AppendLine(" = null;");
                AppendIndent(source, indent).AppendLine("}");
                AppendIndent(source, indent).AppendLine("else");
                AppendIndent(source, indent).AppendLine("{");
                AppendIndent(source, indent + 1).Append(target).Append(" = ")
                    .Append(shape.NestedContract!.Symbol.ToDisplayString(QualifiedTypeFormat))
                    .AppendLine(".Read(ref reader);");
                AppendIndent(source, indent).AppendLine("}");
                return;
            case ShapeKind.Collection:
                AppendIndent(source, indent).AppendLine("if (!reader.ReadBoolean())");
                AppendIndent(source, indent).AppendLine("{");
                if (shape.AllowsNull)
                    AppendIndent(source, indent + 1).Append(target).AppendLine(" = default!;");
                else
                    AppendIndent(source, indent + 1).AppendLine("throw new global::System.IO.InvalidDataException(\"A non-nullable collection contract value was encoded as null.\");");
                AppendIndent(source, indent).AppendLine("}");
                AppendIndent(source, indent).AppendLine("else");
                AppendIndent(source, indent).AppendLine("{");
                AppendIndent(source, indent + 1).Append("int __elementCount").Append(suffix)
                    .Append(" = reader.ReadCollectionCount(\"list element\", global::System.Runtime.CompilerServices.Unsafe.SizeOf<")
                    .Append(shape.ElementType!.ToDisplayString(QualifiedTypeFormat))
                    .AppendLine(">(), fixedAllocationBytes: 56);");
                if (shape.CollectionKind == CollectionKind.Array)
                {
                    AppendIndent(source, indent + 1).Append(shape.ElementType!.ToDisplayString(QualifiedTypeFormat))
                        .Append("[] __elements").Append(suffix).Append(" = new ")
                        .Append(shape.ElementType.ToDisplayString(QualifiedTypeFormat)).Append("[__elementCount")
                        .Append(suffix).AppendLine("];");
                }
                else
                {
                    AppendIndent(source, indent + 1)
                        .Append("global::System.Collections.Generic.List<")
                        .Append(shape.ElementType!.ToDisplayString(QualifiedTypeFormat)).Append("> __elements")
                        .Append(suffix).Append(" = new global::System.Collections.Generic.List<")
                        .Append(shape.ElementType.ToDisplayString(QualifiedTypeFormat)).Append(">(__elementCount")
                        .Append(suffix).AppendLine(");");
                }
                AppendIndent(source, indent + 1).Append("for (int __elementIndex").Append(suffix)
                    .Append(" = 0; __elementIndex").Append(suffix).Append(" < __elementCount")
                    .Append(suffix).Append("; __elementIndex").Append(suffix).AppendLine("++)");
                AppendIndent(source, indent + 1).AppendLine("{");
                AppendIndent(source, indent + 2).Append(shape.ElementType.ToDisplayString(QualifiedTypeFormat))
                    .Append(" __element").Append(suffix).AppendLine(" = default!;");
                EmitReadValue(source, shape.Element!, "__element" + suffix, indent + 2, suffix + "Element");
                if (shape.CollectionKind == CollectionKind.Array)
                {
                    AppendIndent(source, indent + 2).Append("__elements").Append(suffix)
                        .Append("[__elementIndex").Append(suffix).Append("] = __element")
                        .Append(suffix).AppendLine(";");
                }
                else
                {
                    AppendIndent(source, indent + 2).Append("__elements").Append(suffix).Append(".Add(__element")
                        .Append(suffix).AppendLine(");");
                }
                AppendIndent(source, indent + 1).AppendLine("}");
                AppendIndent(source, indent + 1).Append(target).Append(" = __elements")
                    .Append(suffix).AppendLine(";");
                AppendIndent(source, indent).AppendLine("}");
                return;
            case ShapeKind.Dictionary:
                EmitReadDictionary(source, shape, target, indent, suffix);
                return;
            case ShapeKind.Union:
                EmitReadUnion(source, shape, target, indent, suffix);
                return;
            default:
                throw new InvalidOperationException($"Unknown binary shape {shape.Kind}.");
        }
    }

    private static void EmitWriteDictionary(
        StringBuilder source,
        ShapeModel shape,
        string expression,
        int indent,
        string suffix)
    {
        string keyType = shape.KeyType!.ToDisplayString(QualifiedTypeFormat);
        string valueType = shape.ValueType!.ToDisplayString(QualifiedTypeFormat);
        string entryType = "global::System.Collections.Generic.KeyValuePair<" + keyType + ", " + valueType + ">";
        AppendIndent(source, indent).Append("if (").Append(expression).AppendLine(" is null)");
        AppendIndent(source, indent).AppendLine("{");
        if (shape.AllowsNull)
            AppendIndent(source, indent + 1).AppendLine("writer.WriteBoolean(false);");
        else
            AppendIndent(source, indent + 1).AppendLine("throw new global::System.IO.InvalidDataException(\"A non-nullable dictionary contract value cannot be null.\");");
        AppendIndent(source, indent).AppendLine("}");
        AppendIndent(source, indent).AppendLine("else");
        AppendIndent(source, indent).AppendLine("{");
        AppendIndent(source, indent + 1).AppendLine("writer.WriteBoolean(true);");
        AppendIndent(source, indent + 1).Append("global::System.Collections.Generic.List<")
            .Append(entryType).Append("> __entries").Append(suffix).Append(" = new global::System.Collections.Generic.List<")
            .Append(entryType).Append(">(").Append(expression).AppendLine(".Count);");
        AppendIndent(source, indent + 1).Append("foreach (").Append(entryType).Append(" __entry")
            .Append(suffix).Append(" in ").Append(expression).AppendLine(")");
        AppendIndent(source, indent + 2).Append("__entries").Append(suffix).Append(".Add(__entry")
            .Append(suffix).AppendLine(");");
        AppendIndent(source, indent + 1).Append("__entries").Append(suffix)
            .Append(".Sort(static (__left, __right) => ")
            .Append(CanonicalComparison(shape.Key!, "__left.Key", "__right.Key")).AppendLine(");");
        AppendIndent(source, indent + 1).Append("writer.WriteInt32(__entries").Append(suffix).AppendLine(".Count);");
        AppendIndent(source, indent + 1).Append("for (int __entryIndex").Append(suffix)
            .Append(" = 0; __entryIndex").Append(suffix).Append(" < __entries").Append(suffix)
            .Append(".Count; __entryIndex").Append(suffix).AppendLine("++)");
        AppendIndent(source, indent + 1).AppendLine("{");
        AppendIndent(source, indent + 2).Append(entryType).Append(" __orderedEntry").Append(suffix)
            .Append(" = __entries").Append(suffix).Append("[__entryIndex").Append(suffix).AppendLine("];");
        AppendIndent(source, indent + 2).Append("if (__entryIndex").Append(suffix).Append(" != 0 && ")
            .Append(CanonicalComparison(
                shape.Key!,
                "__entries" + suffix + "[__entryIndex" + suffix + " - 1].Key",
                "__orderedEntry" + suffix + ".Key"))
            .AppendLine(" >= 0)");
        AppendIndent(source, indent + 3)
            .AppendLine("throw new global::System.IO.InvalidDataException(\"A dictionary produced duplicate or non-canonical keys.\");");
        AppendIndent(source, indent + 2).Append(keyType).Append(" __key").Append(suffix)
            .Append(" = __orderedEntry").Append(suffix).AppendLine(".Key;");
        AppendIndent(source, indent + 2).Append(valueType).Append(" __value").Append(suffix)
            .Append(" = __orderedEntry").Append(suffix).AppendLine(".Value;");
        EmitWriteValue(source, shape.Key!, "__key" + suffix, indent + 2, suffix + "Key");
        EmitWriteValue(source, shape.Value!, "__value" + suffix, indent + 2, suffix + "Value");
        AppendIndent(source, indent + 1).AppendLine("}");
        AppendIndent(source, indent).AppendLine("}");
    }

    private static void EmitReadDictionary(
        StringBuilder source,
        ShapeModel shape,
        string target,
        int indent,
        string suffix)
    {
        string keyType = shape.KeyType!.ToDisplayString(QualifiedTypeFormat);
        string valueType = shape.ValueType!.ToDisplayString(QualifiedTypeFormat);
        string entryType = "global::System.Collections.Generic.KeyValuePair<" + keyType + ", " + valueType + ">";
        AppendIndent(source, indent).AppendLine("if (!reader.ReadBoolean())");
        AppendIndent(source, indent).AppendLine("{");
        if (shape.AllowsNull)
            AppendIndent(source, indent + 1).Append(target).AppendLine(" = default!;");
        else
            AppendIndent(source, indent + 1).AppendLine("throw new global::System.IO.InvalidDataException(\"A non-nullable dictionary contract value was encoded as null.\");");
        AppendIndent(source, indent).AppendLine("}");
        AppendIndent(source, indent).AppendLine("else");
        AppendIndent(source, indent).AppendLine("{");
        AppendIndent(source, indent + 1).Append("int __entryCount").Append(suffix)
            .Append(" = reader.ReadCollectionCount(\"dictionary entry\", global::System.Math.Max(64, global::System.Runtime.CompilerServices.Unsafe.SizeOf<")
            .Append(entryType).AppendLine(">() + 32), fixedAllocationBytes: 80);");
        AppendIndent(source, indent + 1).Append("global::System.Collections.Generic.Dictionary<")
            .Append(keyType).Append(", ").Append(valueType).Append("> __dictionary").Append(suffix)
            .Append(" = new global::System.Collections.Generic.Dictionary<").Append(keyType).Append(", ")
            .Append(valueType).Append(">(__entryCount").Append(suffix).AppendLine(");");
        AppendIndent(source, indent + 1).Append(keyType).Append(" __previousKey").Append(suffix)
            .AppendLine(" = default!;");
        AppendIndent(source, indent + 1).Append("for (int __entryIndex").Append(suffix)
            .Append(" = 0; __entryIndex").Append(suffix).Append(" < __entryCount").Append(suffix)
            .Append("; __entryIndex").Append(suffix).AppendLine("++)");
        AppendIndent(source, indent + 1).AppendLine("{");
        AppendIndent(source, indent + 2).Append(keyType).Append(" __key").Append(suffix).AppendLine(" = default!;");
        EmitReadValue(source, shape.Key!, "__key" + suffix, indent + 2, suffix + "Key");
        AppendIndent(source, indent + 2).Append("if (__entryIndex").Append(suffix).Append(" != 0 && ")
            .Append(CanonicalComparison(shape.Key!, "__previousKey" + suffix, "__key" + suffix)).AppendLine(" >= 0)");
        AppendIndent(source, indent + 3)
            .AppendLine("throw new global::System.IO.InvalidDataException(\"Dictionary keys are duplicated or not in canonical ascending order.\");");
        AppendIndent(source, indent + 2).Append(valueType).Append(" __value").Append(suffix).AppendLine(" = default!;");
        EmitReadValue(source, shape.Value!, "__value" + suffix, indent + 2, suffix + "Value");
        AppendIndent(source, indent + 2).Append("if (!__dictionary").Append(suffix).Append(".TryAdd(__key")
            .Append(suffix).Append(", __value").Append(suffix).AppendLine("))");
        AppendIndent(source, indent + 3)
            .AppendLine("throw new global::System.IO.InvalidDataException(\"Dictionary contains a duplicate key.\");");
        AppendIndent(source, indent + 2).Append("__previousKey").Append(suffix).Append(" = __key")
            .Append(suffix).AppendLine(";");
        AppendIndent(source, indent + 1).AppendLine("}");
        AppendIndent(source, indent + 1).Append(target).Append(" = __dictionary").Append(suffix).AppendLine(";");
        AppendIndent(source, indent).AppendLine("}");
        _ = entryType;
    }

    private static void EmitWriteUnion(
        StringBuilder source,
        ShapeModel shape,
        string expression,
        int indent,
        string suffix)
    {
        AppendIndent(source, indent).Append("if (").Append(expression).AppendLine(" is null)");
        AppendIndent(source, indent).AppendLine("{");
        if (shape.AllowsNull)
            AppendIndent(source, indent + 1).AppendLine("writer.WriteBoolean(false);");
        else
            AppendIndent(source, indent + 1).AppendLine("throw new global::System.IO.InvalidDataException(\"A non-nullable union contract value cannot be null.\");");
        AppendIndent(source, indent).AppendLine("}");
        AppendIndent(source, indent).AppendLine("else");
        AppendIndent(source, indent).AppendLine("{");
        AppendIndent(source, indent + 1).AppendLine("writer.WriteBoolean(true);");
        AppendIndent(source, indent + 1).Append("switch (").Append(expression).AppendLine(")");
        AppendIndent(source, indent + 1).AppendLine("{");
        foreach (UnionCaseModel unionCase in shape.Union!.Cases)
        {
            string caseType = unionCase.Contract.Symbol.ToDisplayString(QualifiedTypeFormat);
            AppendIndent(source, indent + 2).Append("case ").Append(caseType).Append(" __unionCase")
                .Append(suffix).AppendLine(":");
            AppendIndent(source, indent + 3).Append("writer.WriteUInt32(")
                .Append(unionCase.Tag.ToString(CultureInfo.InvariantCulture)).AppendLine("U);");
            AppendIndent(source, indent + 3).Append(caseType).Append(".Write(ref writer, __unionCase")
                .Append(suffix).AppendLine(");");
            AppendIndent(source, indent + 3).AppendLine("break;");
        }
        AppendIndent(source, indent + 2).AppendLine("default:");
        AppendIndent(source, indent + 3)
            .AppendLine("throw new global::System.IO.InvalidDataException(\"Runtime value is not a declared case of the closed binary union.\");");
        AppendIndent(source, indent + 1).AppendLine("}");
        AppendIndent(source, indent).AppendLine("}");
    }

    private static void EmitReadUnion(
        StringBuilder source,
        ShapeModel shape,
        string target,
        int indent,
        string suffix)
    {
        AppendIndent(source, indent).AppendLine("if (!reader.ReadBoolean())");
        AppendIndent(source, indent).AppendLine("{");
        if (shape.AllowsNull)
            AppendIndent(source, indent + 1).Append(target).AppendLine(" = default!;");
        else
            AppendIndent(source, indent + 1).AppendLine("throw new global::System.IO.InvalidDataException(\"A non-nullable union contract value was encoded as null.\");");
        AppendIndent(source, indent).AppendLine("}");
        AppendIndent(source, indent).AppendLine("else");
        AppendIndent(source, indent).AppendLine("{");
        AppendIndent(source, indent + 1).Append("uint __unionTag").Append(suffix).AppendLine(" = reader.ReadUInt32();");
        AppendIndent(source, indent + 1).Append("switch (__unionTag").Append(suffix).AppendLine(")");
        AppendIndent(source, indent + 1).AppendLine("{");
        foreach (UnionCaseModel unionCase in shape.Union!.Cases)
        {
            AppendIndent(source, indent + 2).Append("case ")
                .Append(unionCase.Tag.ToString(CultureInfo.InvariantCulture)).AppendLine("U:");
            AppendIndent(source, indent + 3).Append(target).Append(" = ")
                .Append(unionCase.Contract.Symbol.ToDisplayString(QualifiedTypeFormat)).AppendLine(".Read(ref reader);");
            AppendIndent(source, indent + 3).AppendLine("break;");
        }
        AppendIndent(source, indent + 2).AppendLine("default:");
        AppendIndent(source, indent + 3).Append("throw new global::System.IO.InvalidDataException($\"Unknown closed binary union tag {")
            .Append("__unionTag").Append(suffix).AppendLine("}.\");");
        AppendIndent(source, indent + 1).AppendLine("}");
        AppendIndent(source, indent).AppendLine("}");
    }

    private static string CanonicalComparison(ShapeModel key, string left, string right)
    {
        if (key.Kind == ShapeKind.String)
            return "global::System.StringComparer.Ordinal.Compare(" + left + ", " + right + ")";
        if (key.Kind == ShapeKind.Enum)
        {
            string primitive = PrimitiveType(key.PrimitiveKind);
            return "((" + primitive + ")" + left + ").CompareTo((" + primitive + ")" + right + ")";
        }
        return left + ".CompareTo(" + right + ")";
    }

    private static void EmitViewSurface(StringBuilder source, ContractModel model, int indent)
    {
        string contractType = model.Symbol.ToDisplayString(QualifiedTypeFormat);
        AppendIndent(source, indent).AppendLine("public static void ValidateCanonical(");
        AppendIndent(source, indent + 1).AppendLine("global::System.ReadOnlySpan<byte> source,");
        AppendIndent(source, indent + 1).AppendLine("global::SomeEngine.Serialization.BinaryReadLimits? limits = null)");
        AppendIndent(source, indent).AppendLine("{");
        AppendIndent(source, indent + 1)
            .AppendLine("global::SomeEngine.Serialization.BinaryViewReader reader = new global::SomeEngine.Serialization.BinaryViewReader(source, limits);");
        AppendIndent(source, indent + 1).Append(GeneratedMemberPrefix).AppendLine("ValidateView(ref reader);");
        AppendIndent(source, indent + 1).Append("reader.EnsureFullyConsumed(")
            .Append(Literal($"contract view '{model.LogicalName}'")).AppendLine(");");
        AppendIndent(source, indent).AppendLine("}");
        source.AppendLine();

        AppendIndent(source, indent).AppendLine("public static View CreateView(");
        AppendIndent(source, indent + 1).AppendLine("global::SomeEngine.Serialization.BinaryContractViewOwner owner,");
        AppendIndent(source, indent + 1).AppendLine("global::SomeEngine.Serialization.BinaryReadLimits? limits = null)");
        AppendIndent(source, indent).AppendLine("    => new View(owner, limits);");
        source.AppendLine();
        AppendIndent(source, indent)
            .Append("public static global::System.Threading.Tasks.ValueTask<global::SomeEngine.Serialization.Containers.BinaryDocumentView<")
            .Append(contractType).AppendLine(", View>> OpenDocumentViewAsync(");
        AppendIndent(source, indent + 1).AppendLine("global::SomeEngine.Serialization.IO.IRangeSource source,");
        AppendIndent(source, indent + 1).AppendLine("bool ownsSource = false,");
        AppendIndent(source, indent + 1).AppendLine("global::SomeEngine.Serialization.BinaryReadLimits? limits = null,");
        AppendIndent(source, indent + 1).AppendLine("global::System.Threading.CancellationToken cancellationToken = default)");
        AppendIndent(source, indent).Append("    => global::SomeEngine.Serialization.Containers.BinaryDocumentView<")
            .Append(contractType).Append(", View>.OpenAsync(source, ownsSource, limits, cancellationToken);")
            .AppendLine();
        source.AppendLine();

        EmitViewValidator(source, model, indent);
        source.AppendLine();
        EmitViewFieldLocator(source, model, indent);

        foreach (MemberModel member in model.Members)
        {
            source.AppendLine();
            EmitViewFieldDecoder(source, member, indent);
        }

        source.AppendLine();
        EmitSpanViewType(source, model, contractType, indent);
        source.AppendLine();
        EmitLongViewType(source, model, contractType, indent);
    }

    private static void EmitViewValidator(StringBuilder source, ContractModel model, int indent)
    {
        AppendIndent(source, indent).Append("internal static void ").Append(GeneratedMemberPrefix)
            .AppendLine("ValidateView(ref global::SomeEngine.Serialization.BinaryViewReader reader)");
        AppendIndent(source, indent).AppendLine("{");
        AppendIndent(source, indent + 1).AppendLine("reader.EnterObject();");
        AppendIndent(source, indent + 1).AppendLine("try");
        AppendIndent(source, indent + 1).AppendLine("{");
        foreach (MemberModel member in model.Members)
        {
            AppendIndent(source, indent + 2).Append(ViewValidateHelperName(member))
                .AppendLine("(ref reader);");
        }
        AppendIndent(source, indent + 1).AppendLine("}");
        AppendIndent(source, indent + 1).AppendLine("finally");
        AppendIndent(source, indent + 1).AppendLine("{");
        AppendIndent(source, indent + 2).AppendLine("reader.ExitObject();");
        AppendIndent(source, indent + 1).AppendLine("}");
        AppendIndent(source, indent).AppendLine("}");

        foreach (MemberModel member in model.Members)
        {
            source.AppendLine();
            AppendIndent(source, indent).Append("private static void ").Append(ViewValidateHelperName(member))
                .AppendLine("(ref global::SomeEngine.Serialization.BinaryViewReader reader)");
            AppendIndent(source, indent).AppendLine("{");
            EmitValidateViewValue(source, member.Shape, indent + 1, member.Index.ToString(CultureInfo.InvariantCulture));
            AppendIndent(source, indent).AppendLine("}");
        }
    }

    private static void EmitViewFieldLocator(StringBuilder source, ContractModel model, int indent)
    {
        AppendIndent(source, indent).Append("private static bool ").Append(GeneratedMemberPrefix)
            .AppendLine("TryGetViewField(");
        AppendIndent(source, indent + 1).AppendLine("global::System.ReadOnlySpan<byte> source,");
        AppendIndent(source, indent + 1).AppendLine("int requestedIndex,");
        AppendIndent(source, indent + 1).AppendLine("global::SomeEngine.Serialization.BinaryReadLimits? limits,");
        AppendIndent(source, indent + 1).AppendLine("out global::System.ReadOnlySpan<byte> encoded)");
        AppendIndent(source, indent).AppendLine("{");
        AppendIndent(source, indent + 1)
            .AppendLine("global::SomeEngine.Serialization.BinaryViewReader reader = new global::SomeEngine.Serialization.BinaryViewReader(source, limits);");
        AppendIndent(source, indent + 1).AppendLine("reader.EnterObject();");
        AppendIndent(source, indent + 1).AppendLine("try");
        AppendIndent(source, indent + 1).AppendLine("{");
        foreach (MemberModel member in model.Members)
        {
            AppendIndent(source, indent + 2).Append("int __fieldStart")
                .Append(member.Index.ToString(CultureInfo.InvariantCulture)).AppendLine(" = reader.Position;");
            AppendIndent(source, indent + 2).Append(ViewValidateHelperName(member)).AppendLine("(ref reader);");
            AppendIndent(source, indent + 2).Append("if (requestedIndex == ")
                .Append(member.Index.ToString(CultureInfo.InvariantCulture)).AppendLine(")");
            AppendIndent(source, indent + 2).AppendLine("{");
            AppendIndent(source, indent + 3).Append("encoded = source.Slice(__fieldStart")
                .Append(member.Index.ToString(CultureInfo.InvariantCulture)).Append(", reader.Position - __fieldStart")
                .Append(member.Index.ToString(CultureInfo.InvariantCulture)).AppendLine(");");
            AppendIndent(source, indent + 3).AppendLine("return true;");
            AppendIndent(source, indent + 2).AppendLine("}");
        }
        AppendIndent(source, indent + 2).AppendLine("encoded = default;");
        AppendIndent(source, indent + 2).AppendLine("return false;");
        AppendIndent(source, indent + 1).AppendLine("}");
        AppendIndent(source, indent + 1).AppendLine("finally");
        AppendIndent(source, indent + 1).AppendLine("{");
        AppendIndent(source, indent + 2).AppendLine("reader.ExitObject();");
        AppendIndent(source, indent + 1).AppendLine("}");
        AppendIndent(source, indent).AppendLine("}");
    }

    private static void EmitValidateViewValue(
        StringBuilder source,
        ShapeModel shape,
        int indent,
        string suffix)
    {
        switch (shape.Kind)
        {
            case ShapeKind.Primitive:
                AppendIndent(source, indent).Append("_ = reader.").Append(ReaderMethod(shape.PrimitiveKind)).AppendLine("();");
                return;
            case ShapeKind.Enum:
                AppendIndent(source, indent).Append("_ = reader.").Append(ReaderMethod(shape.PrimitiveKind)).AppendLine("();");
                return;
            case ShapeKind.NullableEnum:
                AppendIndent(source, indent).Append("if (reader.ReadBoolean())").AppendLine();
                AppendIndent(source, indent + 1).Append("_ = reader.")
                    .Append(ReaderMethod(shape.PrimitiveKind)).AppendLine("();");
                return;
            case ShapeKind.String:
                AppendIndent(source, indent).Append("_ = reader.ReadStringBytes(out bool __isNull")
                    .Append(suffix).AppendLine(");");
                if (!shape.AllowsNull)
                {
                    AppendIndent(source, indent).Append("if (__isNull").Append(suffix).AppendLine(")");
                    AppendIndent(source, indent + 1)
                        .AppendLine("throw new global::System.IO.InvalidDataException(\"A non-nullable string contract value was encoded as null.\");");
                }
                return;
            case ShapeKind.Memory:
                AppendIndent(source, indent).Append("_ = reader.ReadMemoryBytes(out bool __isNull")
                    .Append(suffix).AppendLine(");");
                if (!shape.AllowsNull)
                {
                    AppendIndent(source, indent).Append("if (__isNull").Append(suffix).AppendLine(")");
                    AppendIndent(source, indent + 1)
                        .AppendLine("throw new global::System.IO.InvalidDataException(\"A non-nullable Memory<byte> contract value was encoded as null.\");");
                }
                return;
            case ShapeKind.ContractClass:
                EmitValidatePresentContract(source, shape, indent, suffix, nullableStruct: false);
                return;
            case ShapeKind.ContractStruct:
                EmitNestedViewValidation(source, shape.NestedContract!, indent);
                return;
            case ShapeKind.NullableContractStruct:
                EmitValidatePresentContract(source, shape, indent, suffix, nullableStruct: true);
                return;
            case ShapeKind.Collection:
                AppendIndent(source, indent).Append("bool __collectionPresent").Append(suffix)
                    .AppendLine(" = reader.ReadBoolean();");
                AppendIndent(source, indent).Append("if (!__collectionPresent").Append(suffix).AppendLine(")");
                AppendIndent(source, indent).AppendLine("{");
                if (!shape.AllowsNull)
                    AppendIndent(source, indent + 1).AppendLine("throw new global::System.IO.InvalidDataException(\"A non-nullable collection contract value was encoded as null.\");");
                AppendIndent(source, indent).AppendLine("}");
                AppendIndent(source, indent).AppendLine("else");
                AppendIndent(source, indent).AppendLine("{");
                AppendIndent(source, indent + 1).Append("int __elementCount").Append(suffix)
                    .AppendLine(" = reader.ReadCollectionCount(\"collection element\");");
                AppendIndent(source, indent + 1).Append("for (int __elementIndex").Append(suffix)
                    .Append(" = 0; __elementIndex").Append(suffix).Append(" < __elementCount").Append(suffix)
                    .Append("; __elementIndex").Append(suffix).AppendLine("++)");
                AppendIndent(source, indent + 1).AppendLine("{");
                EmitValidateViewValue(source, shape.Element!, indent + 2, suffix + "Element");
                AppendIndent(source, indent + 1).AppendLine("}");
                AppendIndent(source, indent).AppendLine("}");
                return;
            case ShapeKind.Dictionary:
                EmitValidateViewDictionary(source, shape, indent, suffix);
                return;
            case ShapeKind.Union:
                AppendIndent(source, indent).Append("bool __unionPresent").Append(suffix)
                    .AppendLine(" = reader.ReadBoolean();");
                AppendIndent(source, indent).Append("if (!__unionPresent").Append(suffix).AppendLine(")");
                AppendIndent(source, indent).AppendLine("{");
                if (!shape.AllowsNull)
                    AppendIndent(source, indent + 1).AppendLine("throw new global::System.IO.InvalidDataException(\"A non-nullable union contract value was encoded as null.\");");
                AppendIndent(source, indent).AppendLine("}");
                AppendIndent(source, indent).AppendLine("else");
                AppendIndent(source, indent).AppendLine("{");
                AppendIndent(source, indent + 1).Append("uint __unionTag").Append(suffix).AppendLine(" = reader.ReadUInt32();");
                AppendIndent(source, indent + 1).Append("switch (__unionTag").Append(suffix).AppendLine(")");
                AppendIndent(source, indent + 1).AppendLine("{");
                foreach (UnionCaseModel unionCase in shape.Union!.Cases)
                {
                    AppendIndent(source, indent + 2).Append("case ").Append(unionCase.Tag.ToString(CultureInfo.InvariantCulture)).AppendLine("U:");
                    EmitNestedViewValidation(source, unionCase.Contract, indent + 3);
                    AppendIndent(source, indent + 3).AppendLine("break;");
                }
                AppendIndent(source, indent + 2).AppendLine("default:");
                AppendIndent(source, indent + 3).Append("throw new global::System.IO.InvalidDataException($\"Unknown closed binary union tag {")
                    .Append("__unionTag").Append(suffix).AppendLine("}.\");");
                AppendIndent(source, indent + 1).AppendLine("}");
                AppendIndent(source, indent).AppendLine("}");
                return;
            default:
                throw new InvalidOperationException($"Unknown binary view shape {shape.Kind}.");
        }
    }

    private static void EmitValidatePresentContract(
        StringBuilder source,
        ShapeModel shape,
        int indent,
        string suffix,
        bool nullableStruct)
    {
        AppendIndent(source, indent).Append("bool __contractPresent").Append(suffix)
            .AppendLine(" = reader.ReadBoolean();");
        AppendIndent(source, indent).Append("if (!__contractPresent").Append(suffix).AppendLine(")");
        AppendIndent(source, indent).AppendLine("{");
        if (!shape.AllowsNull && !nullableStruct)
            AppendIndent(source, indent + 1).AppendLine("throw new global::System.IO.InvalidDataException(\"A non-nullable nested contract value was encoded as null.\");");
        AppendIndent(source, indent).AppendLine("}");
        AppendIndent(source, indent).AppendLine("else");
        AppendIndent(source, indent).AppendLine("{");
        EmitNestedViewValidation(source, shape.NestedContract!, indent + 1);
        AppendIndent(source, indent).AppendLine("}");
    }

    private static void EmitNestedViewValidation(StringBuilder source, ContractModel nested, int indent)
    {
        AppendIndent(source, indent).Append(nested.Symbol.ToDisplayString(QualifiedTypeFormat))
            .Append(nested.IsManuallyImplemented ? ".ValidateView(ref reader);" : ".__SomeEngineBinaryContract_ValidateView(ref reader);")
            .AppendLine();
    }

    private static void EmitValidateViewDictionary(
        StringBuilder source,
        ShapeModel shape,
        int indent,
        string suffix)
    {
        AppendIndent(source, indent).Append("bool __dictionaryPresent").Append(suffix)
            .AppendLine(" = reader.ReadBoolean();");
        AppendIndent(source, indent).Append("if (!__dictionaryPresent").Append(suffix).AppendLine(")");
        AppendIndent(source, indent).AppendLine("{");
        if (!shape.AllowsNull)
            AppendIndent(source, indent + 1).AppendLine("throw new global::System.IO.InvalidDataException(\"A non-nullable dictionary contract value was encoded as null.\");");
        AppendIndent(source, indent).AppendLine("}");
        AppendIndent(source, indent).AppendLine("else");
        AppendIndent(source, indent).AppendLine("{");
        AppendIndent(source, indent + 1).Append("int __entryCount").Append(suffix)
            .AppendLine(" = reader.ReadCollectionCount(\"dictionary entry\");");
        string keyType = shape.Key!.Kind == ShapeKind.String
            ? "global::System.ReadOnlySpan<byte>"
            : shape.KeyType!.ToDisplayString(QualifiedTypeFormat);
        AppendIndent(source, indent + 1).Append(keyType).Append(" __previousKey").Append(suffix)
            .AppendLine(" = default;");
        AppendIndent(source, indent + 1).Append("for (int __entryIndex").Append(suffix)
            .Append(" = 0; __entryIndex").Append(suffix).Append(" < __entryCount").Append(suffix)
            .Append("; __entryIndex").Append(suffix).AppendLine("++)");
        AppendIndent(source, indent + 1).AppendLine("{");
        AppendIndent(source, indent + 2).Append(keyType).Append(" __key").Append(suffix).Append(" = ")
            .Append(ViewKeyReadExpression(shape.Key, suffix)).AppendLine(";");
        string comparison = shape.Key.Kind == ShapeKind.String
            ? "global::SomeEngine.Serialization.BinaryViewReader.CompareUtf8Ordinal(__previousKey" + suffix + ", __key" + suffix + ")"
            : CanonicalComparison(shape.Key, "__previousKey" + suffix, "__key" + suffix);
        AppendIndent(source, indent + 2).Append("if (__entryIndex").Append(suffix).Append(" != 0 && ")
            .Append(comparison).AppendLine(" >= 0)");
        AppendIndent(source, indent + 3)
            .AppendLine("throw new global::System.IO.InvalidDataException(\"Dictionary keys are duplicated or not in canonical ascending order.\");");
        EmitValidateViewValue(source, shape.Value!, indent + 2, suffix + "Value");
        AppendIndent(source, indent + 2).Append("__previousKey").Append(suffix).Append(" = __key")
            .Append(suffix).AppendLine(";");
        AppendIndent(source, indent + 1).AppendLine("}");
        AppendIndent(source, indent).AppendLine("}");
    }

    private static string ViewKeyReadExpression(ShapeModel key, string suffix)
    {
        if (key.Kind == ShapeKind.String)
            return "reader.ReadStringBytes(out bool __keyNull" + suffix + ") is var __keyBytes" + suffix + " && !__keyNull" + suffix + " ? __keyBytes" + suffix + " : throw new global::System.IO.InvalidDataException(\"Dictionary string key was encoded as null.\")";
        if (key.Kind == ShapeKind.Enum)
            return "(" + key.EnumType!.ToDisplayString(QualifiedTypeFormat) + ")reader." + ReaderMethod(key.PrimitiveKind) + "()";
        return "reader." + ReaderMethod(key.PrimitiveKind) + "()";
    }

    private static void EmitViewFieldDecoder(StringBuilder source, MemberModel member, int indent)
    {
        if (member.Shape.Kind == ShapeKind.Primitive ||
            member.Shape.Kind == ShapeKind.Enum ||
            member.Shape.Kind == ShapeKind.NullableEnum)
        {
            string typeName = member.Type.ToDisplayString(QualifiedTypeFormat);
            AppendIndent(source, indent).Append("private static ").Append(typeName).Append(' ')
                .Append(ViewDecodeHelperName(member)).AppendLine("(");
            AppendIndent(source, indent + 1).AppendLine("global::System.ReadOnlySpan<byte> source,");
            AppendIndent(source, indent + 1).AppendLine("global::SomeEngine.Serialization.BinaryReadLimits? limits)");
            AppendIndent(source, indent).AppendLine("{");
            AppendIndent(source, indent + 1).Append("if (!").Append(GeneratedMemberPrefix)
                .Append("TryGetViewField(source, ").Append(member.Index.ToString(CultureInfo.InvariantCulture))
                .AppendLine(", limits, out global::System.ReadOnlySpan<byte> encoded))");
            AppendIndent(source, indent + 2).AppendLine("throw new global::System.IO.InvalidDataException(\"Contract view field is absent.\");");
            AppendIndent(source, indent + 1)
                .AppendLine("global::SomeEngine.Serialization.BinaryViewReader reader = new global::SomeEngine.Serialization.BinaryViewReader(encoded, limits);");
            string read = "reader." + ReaderMethod(member.Shape.PrimitiveKind) + "()";
            if (member.Shape.Kind == ShapeKind.Enum)
                read = "(" + member.Shape.EnumType!.ToDisplayString(QualifiedTypeFormat) + ")" + read;
            else if (member.Shape.Kind == ShapeKind.NullableEnum)
                read = "reader.ReadBoolean() ? (" +
                    member.Shape.EnumType!.ToDisplayString(QualifiedTypeFormat) +
                    "?)(" + member.Shape.EnumType.ToDisplayString(QualifiedTypeFormat) + ")" +
                    read + " : null";
            AppendIndent(source, indent + 1).Append(typeName).Append(" result = ").Append(read).AppendLine(";");
            AppendIndent(source, indent + 1).AppendLine("reader.EnsureFullyConsumed(\"primitive view field\");");
            AppendIndent(source, indent + 1).AppendLine("return result;");
            AppendIndent(source, indent).AppendLine("}");
        }
        else if (member.Shape.Kind == ShapeKind.String || member.Shape.Kind == ShapeKind.Memory)
        {
            bool isString = member.Shape.Kind == ShapeKind.String;
            AppendIndent(source, indent).Append("private static bool ").Append(ViewSliceHelperName(member)).AppendLine("(");
            AppendIndent(source, indent + 1).AppendLine("global::System.ReadOnlySpan<byte> source,");
            AppendIndent(source, indent + 1).AppendLine("global::SomeEngine.Serialization.BinaryReadLimits? limits,");
            AppendIndent(source, indent + 1).AppendLine("out global::System.ReadOnlySpan<byte> value)");
            AppendIndent(source, indent).AppendLine("{");
            AppendIndent(source, indent + 1).Append("if (!").Append(GeneratedMemberPrefix)
                .Append("TryGetViewField(source, ").Append(member.Index.ToString(CultureInfo.InvariantCulture))
                .AppendLine(", limits, out global::System.ReadOnlySpan<byte> encoded))");
            AppendIndent(source, indent + 1).AppendLine("{");
            AppendIndent(source, indent + 2).AppendLine("value = default;");
            AppendIndent(source, indent + 2).AppendLine("return false;");
            AppendIndent(source, indent + 1).AppendLine("}");
            AppendIndent(source, indent + 1)
                .AppendLine("global::SomeEngine.Serialization.BinaryViewReader reader = new global::SomeEngine.Serialization.BinaryViewReader(encoded, limits);");
            AppendIndent(source, indent + 1).Append("_ = reader.")
                .Append(isString ? "ReadStringBytes" : "ReadMemoryBytes")
                .AppendLine("(out bool isNull);");
            AppendIndent(source, indent + 1).AppendLine("reader.EnsureFullyConsumed(\"span view field\");");
            if (!member.Shape.AllowsNull)
            {
                AppendIndent(source, indent + 1).AppendLine("if (isNull)");
                AppendIndent(source, indent + 2).AppendLine("throw new global::System.IO.InvalidDataException(\"Non-nullable span view field was encoded as null.\");");
            }
            AppendIndent(source, indent + 1).AppendLine("if (isNull)");
            AppendIndent(source, indent + 1).AppendLine("{");
            AppendIndent(source, indent + 2).AppendLine("value = default;");
            AppendIndent(source, indent + 2).AppendLine("return false;");
            AppendIndent(source, indent + 1).AppendLine("}");
            AppendIndent(source, indent + 1).AppendLine("value = encoded[4..];");
            AppendIndent(source, indent + 1).AppendLine("return true;");
            AppendIndent(source, indent).AppendLine("}");
        }
    }

    private static void EmitSpanViewType(
        StringBuilder source,
        ContractModel model,
        string contractType,
        int indent)
    {
        AppendIndent(source, indent).AppendLine("public readonly ref struct SpanView");
        AppendIndent(source, indent).AppendLine("{");
        AppendIndent(source, indent + 1).AppendLine("private readonly global::System.ReadOnlySpan<byte> _source;");
        AppendIndent(source, indent + 1).AppendLine("private readonly global::SomeEngine.Serialization.BinaryReadLimits _limits;");
        source.AppendLine();
        AppendIndent(source, indent + 1).AppendLine("public SpanView(");
        AppendIndent(source, indent + 2).AppendLine("global::System.ReadOnlySpan<byte> source,");
        AppendIndent(source, indent + 2).AppendLine("global::SomeEngine.Serialization.BinaryReadLimits? limits = null)");
        AppendIndent(source, indent + 1).AppendLine("{");
        AppendIndent(source, indent + 2).AppendLine("global::SomeEngine.Serialization.BinaryReadLimits effectiveLimits = limits ?? global::SomeEngine.Serialization.BinaryReadLimits.Default;");
        AppendIndent(source, indent + 2).AppendLine("ValidateCanonical(source, effectiveLimits);");
        AppendIndent(source, indent + 2).AppendLine("_source = source;");
        AppendIndent(source, indent + 2).AppendLine("_limits = effectiveLimits;");
        AppendIndent(source, indent + 1).AppendLine("}");
        source.AppendLine();
        AppendIndent(source, indent + 1).AppendLine("public int EncodedLength => _source.Length;");
        AppendIndent(source, indent + 1).AppendLine("public void Validate() => ValidateCanonical(_source, _limits);");
        foreach (MemberModel member in model.Members)
        {
            source.AppendLine();
            EmitViewAccessors(source, member, indent + 1, "_source", "_limits");
        }
        EmitChunkViewAccessors(source, model, indent + 1);
        AppendIndent(source, indent).AppendLine("}");
    }

    private static void EmitLongViewType(
        StringBuilder source,
        ContractModel model,
        string contractType,
        int indent)
    {
        AppendIndent(source, indent).AppendLine("public readonly struct View");
        AppendIndent(source, indent).AppendLine("{");
        AppendIndent(source, indent + 1)
            .AppendLine("private readonly global::SomeEngine.Serialization.BinaryContractViewOwner _owner;");
        AppendIndent(source, indent + 1).AppendLine("private readonly global::SomeEngine.Serialization.BinaryReadLimits _limits;");
        source.AppendLine();
        AppendIndent(source, indent + 1).AppendLine("public View(");
        AppendIndent(source, indent + 2)
            .AppendLine("global::SomeEngine.Serialization.BinaryContractViewOwner owner,");
        AppendIndent(source, indent + 2).AppendLine("global::SomeEngine.Serialization.BinaryReadLimits? limits = null)");
        AppendIndent(source, indent + 1).AppendLine("{");
        AppendIndent(source, indent + 2).AppendLine("global::System.ArgumentNullException.ThrowIfNull(owner);");
        AppendIndent(source, indent + 2).AppendLine("global::SomeEngine.Serialization.BinaryReadLimits effectiveLimits = limits ?? global::SomeEngine.Serialization.BinaryReadLimits.Default;");
        AppendIndent(source, indent + 2).AppendLine("ValidateCanonical(owner.Span, effectiveLimits);");
        AppendIndent(source, indent + 2).AppendLine("_owner = owner;");
        AppendIndent(source, indent + 2).AppendLine("_limits = effectiveLimits;");
        AppendIndent(source, indent + 1).AppendLine("}");
        source.AppendLine();
        AppendIndent(source, indent + 1).AppendLine("public int EncodedLength => _owner.Length;");
        AppendIndent(source, indent + 1).AppendLine("public void Validate() => ValidateCanonical(_owner.Span, _limits);");
        foreach (MemberModel member in model.Members)
        {
            source.AppendLine();
            EmitViewAccessors(source, member, indent + 1, "_owner.Span", "_limits");
        }
        EmitChunkViewAccessors(source, model, indent + 1);
        AppendIndent(source, indent).AppendLine("}");
    }

    private static void EmitChunkViewAccessors(
        StringBuilder source,
        ContractModel model,
        int indent)
    {
        foreach (ChunkReferenceModel chunk in model.ChunkReferences)
        {
            source.AppendLine();
            AppendIndent(source, indent)
                .Append("public global::SomeEngine.Serialization.Containers.BinaryChunkRef ")
                .Append(chunk.AccessorName).Append(" => new global::SomeEngine.Serialization.Containers.BinaryChunkRef(")
                .Append(ViewGetterStem(chunk.Key)).Append("(), checked((long)")
                .Append(ViewGetterStem(chunk.DecodedLength)).AppendLine("()));");
        }
    }

    private static void EmitViewAccessors(
        StringBuilder source,
        MemberModel member,
        int indent,
        string sourceExpression,
        string limitsExpression)
    {
        string stem = ViewGetterStem(member);
        AppendIndent(source, indent).Append("public bool Try").Append(stem)
            .AppendLine("Encoded(out global::System.ReadOnlySpan<byte> encoded)");
        AppendIndent(source, indent).Append("    => ").Append(GeneratedMemberPrefix)
            .Append("TryGetViewField(").Append(sourceExpression).Append(", ")
            .Append(member.Index.ToString(CultureInfo.InvariantCulture)).Append(", ")
            .Append(limitsExpression).AppendLine(", out encoded);");
        AppendIndent(source, indent).Append("public global::System.ReadOnlySpan<byte> ").Append(stem)
            .AppendLine("Encoded()");
        AppendIndent(source, indent).AppendLine("{");
        AppendIndent(source, indent + 1).Append("if (!Try").Append(stem).AppendLine("Encoded(out global::System.ReadOnlySpan<byte> encoded))");
        AppendIndent(source, indent + 2).Append("throw new global::System.IO.InvalidDataException(")
            .Append(Literal($"Contract view field '{member.LogicalName}' is absent.")).AppendLine(");");
        AppendIndent(source, indent + 1).AppendLine("return encoded;");
        AppendIndent(source, indent).AppendLine("}");

        if (member.Shape.Kind == ShapeKind.Primitive ||
            member.Shape.Kind == ShapeKind.Enum ||
            member.Shape.Kind == ShapeKind.NullableEnum)
        {
            AppendIndent(source, indent).Append("public ").Append(member.Type.ToDisplayString(QualifiedTypeFormat))
                .Append(' ').Append(stem).Append("() => ").Append(ViewDecodeHelperName(member))
                .Append('(').Append(sourceExpression).Append(", ").Append(limitsExpression).AppendLine(");");
        }
        else if (member.Shape.Kind == ShapeKind.String || member.Shape.Kind == ShapeKind.Memory)
        {
            AppendIndent(source, indent).Append("public bool Try").Append(stem)
                .Append(member.Shape.Kind == ShapeKind.String ? "Utf8" : "Bytes")
                .AppendLine("(out global::System.ReadOnlySpan<byte> value)");
            AppendIndent(source, indent).Append("    => ").Append(ViewSliceHelperName(member))
                .Append('(').Append(sourceExpression).Append(", ").Append(limitsExpression)
                .AppendLine(", out value);");
        }
    }

    private static string ViewValidateHelperName(MemberModel member) =>
        GeneratedMemberPrefix + "ValidateViewField" + member.Index.ToString(CultureInfo.InvariantCulture);

    private static string ViewDecodeHelperName(MemberModel member) =>
        GeneratedMemberPrefix + "DecodeViewField" + member.Index.ToString(CultureInfo.InvariantCulture);

    private static string ViewSliceHelperName(MemberModel member) =>
        GeneratedMemberPrefix + "SliceViewField" + member.Index.ToString(CultureInfo.InvariantCulture);

    private static string ViewGetterStem(MemberModel member)
    {
        string stem = "Get" + member.Symbol.Name;
        return string.Equals(stem, "GetType", StringComparison.Ordinal) ||
               string.Equals(stem, "GetHashCode", StringComparison.Ordinal)
            ? stem + "Value"
            : stem;
    }

    private static void EmitRuntimeFingerprintHelpers(
        StringBuilder source,
        ContractModel model,
        int indent)
    {
        int descriptorLength = RuntimeDescriptorLength(model);
        AppendIndent(source, indent).Append("private static ulong ")
            .Append(GeneratedMemberPrefix).AppendLine("ComputeSchemaFingerprint()");
        AppendIndent(source, indent).AppendLine("{");
        AppendIndent(source, indent + 1)
            .Append("byte[] __descriptorBuffer = global::System.GC.AllocateUninitializedArray<byte>(")
            .Append(descriptorLength.ToString(CultureInfo.InvariantCulture)).AppendLine(");");
        AppendIndent(source, indent + 1)
            .AppendLine("global::SomeEngine.Serialization.BinaryDataWriter __descriptor = new global::SomeEngine.Serialization.BinaryDataWriter(__descriptorBuffer);");
        EmitRuntimeToken(source, "SomeEngine.BinaryContract.v2", indent + 1);
        EmitRuntimeToken(source, model.LogicalName, indent + 1);
        AppendIndent(source, indent + 1).Append("__descriptor.WriteByte(")
            .Append(((byte)model.Compatibility).ToString(CultureInfo.InvariantCulture)).AppendLine(");");
        AppendIndent(source, indent + 1).Append("__descriptor.WriteUInt32(")
            .Append(model.Epoch.ToString(CultureInfo.InvariantCulture)).AppendLine("U);");
        AppendIndent(source, indent + 1).Append("__descriptor.WriteByte(")
            .Append(model.Symbol.TypeKind == TypeKind.Class ? "1" : "2").AppendLine(");");
        AppendIndent(source, indent + 1).Append("__descriptor.WriteUInt32(")
            .Append(model.Members.Count.ToString(CultureInfo.InvariantCulture)).AppendLine("U);");
        foreach (MemberModel member in model.Members)
        {
            EmitRuntimeToken(source, member.LogicalName, indent + 1);
            AppendIndent(source, indent + 1).Append("__descriptor.WriteUInt64(0x")
                .Append(member.FieldKey.ToString("X16", CultureInfo.InvariantCulture)).AppendLine("UL);");
            EmitRuntimeShapeDescriptor(source, member.Shape, indent + 1);
        }
        AppendIndent(source, indent + 1).Append("if (__descriptor.WrittenCount != ")
            .Append(descriptorLength.ToString(CultureInfo.InvariantCulture)).AppendLine(")");
        AppendIndent(source, indent + 2)
            .AppendLine("throw new global::System.InvalidOperationException(\"Generated schema descriptor length changed.\");");
        AppendIndent(source, indent + 1).AppendLine("global::System.Span<byte> __hash = stackalloc byte[32];");
        AppendIndent(source, indent + 1)
            .AppendLine("global::System.Security.Cryptography.SHA256.HashData(__descriptorBuffer, __hash);");
        AppendIndent(source, indent + 1)
            .AppendLine("return global::System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(__hash);");
        AppendIndent(source, indent).AppendLine("}");
    }

    private static int RuntimeDescriptorLength(ContractModel model)
    {
        int length = checked(
            RuntimeTokenLength("SomeEngine.BinaryContract.v2")
            + RuntimeTokenLength(model.LogicalName)
            + sizeof(byte)
            + sizeof(uint)
            + sizeof(byte)
            + sizeof(uint));
        foreach (MemberModel member in model.Members)
        {
            length = checked(
                length
                + RuntimeTokenLength(member.LogicalName)
                + sizeof(ulong)
                + RuntimeShapeDescriptorLength(member.Shape));
        }
        return length;
    }

    private static int RuntimeShapeDescriptorLength(ShapeModel shape)
    {
        int length = sizeof(byte);
        switch (shape.Kind)
        {
            case ShapeKind.Primitive:
                return checked(length + sizeof(byte));
            case ShapeKind.Enum:
            case ShapeKind.NullableEnum:
            {
                length = checked(length + RuntimeTokenLength(StableName(shape.EnumType!)) + sizeof(byte) + sizeof(uint));
                foreach (IFieldSymbol value in shape.EnumType!.GetMembers()
                             .OfType<IFieldSymbol>()
                             .Where(static field => field.HasConstantValue && !field.IsImplicitlyDeclared))
                {
                    length = checked(length + RuntimeTokenLength(value.Name) + sizeof(ulong));
                }
                return length;
            }
            case ShapeKind.String:
            case ShapeKind.Memory:
                return checked(length + sizeof(byte));
            case ShapeKind.ContractClass:
                return checked(length + sizeof(byte) + RuntimeTokenLength(shape.NestedContract!.LogicalName) + sizeof(ulong));
            case ShapeKind.ContractStruct:
            case ShapeKind.NullableContractStruct:
                return checked(length + RuntimeTokenLength(shape.NestedContract!.LogicalName) + sizeof(ulong));
            case ShapeKind.Collection:
                return checked(length + sizeof(byte) + sizeof(byte) + RuntimeShapeDescriptorLength(shape.Element!));
            case ShapeKind.Dictionary:
                return checked(
                    length + sizeof(byte) + sizeof(byte)
                    + RuntimeShapeDescriptorLength(shape.Key!)
                    + RuntimeShapeDescriptorLength(shape.Value!));
            case ShapeKind.Union:
                length = checked(
                    length + sizeof(byte) + RuntimeTokenLength(StableName(shape.Union!.Symbol)) + sizeof(uint));
                foreach (UnionCaseModel unionCase in shape.Union.Cases)
                {
                    length = checked(
                        length + sizeof(uint) + RuntimeTokenLength(unionCase.Contract.LogicalName) + sizeof(ulong));
                }
                return length;
            default:
                throw new InvalidOperationException($"Unknown binary shape {shape.Kind}.");
        }
    }

    private static int RuntimeTokenLength(string value)
        => checked(sizeof(int) + Encoding.UTF8.GetByteCount(value));

    private static void EmitRuntimeShapeDescriptor(StringBuilder source, ShapeModel shape, int indent)
    {
        AppendIndent(source, indent).Append("__descriptor.WriteByte(")
            .Append(((byte)shape.Kind).ToString(CultureInfo.InvariantCulture)).AppendLine(");");
        switch (shape.Kind)
        {
            case ShapeKind.Primitive:
                AppendIndent(source, indent).Append("__descriptor.WriteByte(")
                    .Append(((byte)shape.PrimitiveKind).ToString(CultureInfo.InvariantCulture)).AppendLine(");");
                break;
            case ShapeKind.Enum:
            case ShapeKind.NullableEnum:
                EmitRuntimeToken(source, StableName(shape.EnumType!), indent);
                AppendIndent(source, indent).Append("__descriptor.WriteByte(")
                    .Append(((byte)shape.PrimitiveKind).ToString(CultureInfo.InvariantCulture)).AppendLine(");");
                IFieldSymbol[] values = shape.EnumType!.GetMembers()
                    .OfType<IFieldSymbol>()
                    .Where(static field => field.HasConstantValue && !field.IsImplicitlyDeclared)
                    .OrderBy(static field => field.Name, StringComparer.Ordinal)
                    .ToArray();
                AppendIndent(source, indent).Append("__descriptor.WriteUInt32(")
                    .Append(values.Length.ToString(CultureInfo.InvariantCulture)).AppendLine("U);");
                foreach (IFieldSymbol value in values)
                {
                    EmitRuntimeToken(source, value.Name, indent);
                    AppendIndent(source, indent).Append("__descriptor.WriteUInt64(0x")
                        .Append(EnumConstantBits(value.ConstantValue!, shape.PrimitiveKind)
                            .ToString("X16", CultureInfo.InvariantCulture)).AppendLine("UL);");
                }
                break;
            case ShapeKind.String:
            case ShapeKind.Memory:
                AppendIndent(source, indent).Append("__descriptor.WriteByte(")
                    .Append(shape.AllowsNull ? "1" : "0").AppendLine(");");
                break;
            case ShapeKind.ContractClass:
                AppendIndent(source, indent).Append("__descriptor.WriteByte(")
                    .Append(shape.AllowsNull ? "1" : "0").AppendLine(");");
                EmitRuntimeToken(source, shape.NestedContract!.LogicalName, indent);
                AppendIndent(source, indent).Append("__descriptor.WriteUInt64(")
                    .Append(shape.NestedContract.Symbol.ToDisplayString(QualifiedTypeFormat))
                    .AppendLine(".SchemaFingerprint);");
                break;
            case ShapeKind.ContractStruct:
            case ShapeKind.NullableContractStruct:
                EmitRuntimeToken(source, shape.NestedContract!.LogicalName, indent);
                AppendIndent(source, indent).Append("__descriptor.WriteUInt64(")
                    .Append(shape.NestedContract.Symbol.ToDisplayString(QualifiedTypeFormat))
                    .AppendLine(".SchemaFingerprint);");
                break;
            case ShapeKind.Collection:
                AppendIndent(source, indent).Append("__descriptor.WriteByte(")
                    .Append(((byte)shape.CollectionKind).ToString(CultureInfo.InvariantCulture)).AppendLine(");");
                AppendIndent(source, indent).Append("__descriptor.WriteByte(")
                    .Append(shape.AllowsNull ? "1" : "0").AppendLine(");");
                EmitRuntimeShapeDescriptor(source, shape.Element!, indent);
                break;
            case ShapeKind.Dictionary:
                AppendIndent(source, indent).Append("__descriptor.WriteByte(")
                    .Append(((byte)shape.DictionaryKind).ToString(CultureInfo.InvariantCulture)).AppendLine(");");
                AppendIndent(source, indent).Append("__descriptor.WriteByte(")
                    .Append(shape.AllowsNull ? "1" : "0").AppendLine(");");
                EmitRuntimeShapeDescriptor(source, shape.Key!, indent);
                EmitRuntimeShapeDescriptor(source, shape.Value!, indent);
                break;
            case ShapeKind.Union:
                AppendIndent(source, indent).Append("__descriptor.WriteByte(")
                    .Append(shape.AllowsNull ? "1" : "0").AppendLine(");");
                EmitRuntimeToken(source, StableName(shape.Union!.Symbol), indent);
                AppendIndent(source, indent).Append("__descriptor.WriteUInt32(")
                    .Append(shape.Union.Cases.Count.ToString(CultureInfo.InvariantCulture)).AppendLine("U);");
                foreach (UnionCaseModel unionCase in shape.Union.Cases)
                {
                    AppendIndent(source, indent).Append("__descriptor.WriteUInt32(")
                        .Append(unionCase.Tag.ToString(CultureInfo.InvariantCulture)).AppendLine("U);");
                    EmitRuntimeToken(source, unionCase.Contract.LogicalName, indent);
                    AppendIndent(source, indent).Append("__descriptor.WriteUInt64(")
                        .Append(unionCase.Contract.Symbol.ToDisplayString(QualifiedTypeFormat))
                        .AppendLine(".SchemaFingerprint);");
                }
                break;
            default:
                throw new InvalidOperationException($"Unknown binary shape {shape.Kind}.");
        }
    }

    private static void EmitRuntimeToken(StringBuilder source, string token, int indent)
    {
        AppendIndent(source, indent).Append("__descriptor.WriteString(")
            .Append(Literal(token)).AppendLine(");");
    }

    private static string EmitCatalog(
        IReadOnlyList<ContractModel> models,
        string catalogNamespace)
    {
        StringBuilder source = new();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable enable");
        source.AppendLine();
        source.Append("namespace ").Append(catalogNamespace).AppendLine(";");
        source.AppendLine();
        // Every consuming assembly gets its own generated catalog. Keep it assembly-local so a
        // downstream project can declare contracts while referencing another contract assembly
        // without importing a second type with the same metadata name.
        source.AppendLine("internal static class GeneratedBinaryContractCatalog");
        source.AppendLine("{");
        source.AppendLine("    [global::System.ThreadStatic]");
        source.AppendLine("    private static int s_writeObjectDepth;");
        source.AppendLine();
        source.AppendLine("    public static void RegisterAll(global::SomeEngine.Serialization.BinaryContractCatalog catalog)");
        source.AppendLine("    {");
        source.AppendLine("        if (catalog is null)");
        source.AppendLine("            throw new global::System.ArgumentNullException(nameof(catalog));");
        foreach (ContractModel model in models)
        {
            source.Append("        catalog.Register<")
                .Append(model.Symbol.ToDisplayString(QualifiedTypeFormat)).AppendLine(">();");
        }
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    internal static void EnterWriteObject()");
        source.AppendLine("    {");
        source.AppendLine("        if (s_writeObjectDepth >= 128)");
        source.AppendLine("            throw new global::System.IO.InvalidDataException(\"Binary object depth exceeds the generated writer limit 128.\");");
        source.AppendLine("        s_writeObjectDepth++;");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    internal static void ExitWriteObject()");
        source.AppendLine("    {");
        source.AppendLine("        if (s_writeObjectDepth <= 0)");
        source.AppendLine("            throw new global::System.InvalidOperationException(\"Binary writer object-depth accounting is unbalanced.\");");
        source.AppendLine("        s_writeObjectDepth--;");
        source.AppendLine("    }");
        source.AppendLine("}");
        return source.ToString();
    }

    private static string PartialTypeHeader(INamedTypeSymbol type, INamedTypeSymbol? interfaceType)
    {
        StringBuilder header = new();
        string accessibility = AccessibilityText(type.DeclaredAccessibility);
        if (accessibility.Length != 0)
            header.Append(accessibility).Append(' ');
        if (type.TypeKind == TypeKind.Class)
        {
            if (type.IsStatic)
                header.Append("static ");
            else
            {
                if (type.IsAbstract)
                    header.Append("abstract ");
                if (type.IsSealed)
                    header.Append("sealed ");
            }
            header.Append(type.IsRecord ? "partial record class " : "partial class ");
        }
        else if (type.TypeKind == TypeKind.Struct)
        {
            if (type.IsReadOnly)
                header.Append("readonly ");
            if (type.IsRefLikeType)
                header.Append("ref ");
            header.Append(type.IsRecord ? "partial record struct " : "partial struct ");
        }
        else
        {
            header.Append("partial interface ");
        }
        header.Append(Escape(type.Name));
        if (interfaceType is not null)
        {
            header.Append(" : global::SomeEngine.Serialization.IBinaryContract<")
                .Append(interfaceType.ToDisplayString(QualifiedTypeFormat)).Append(">, ")
                .Append("global::SomeEngine.Serialization.IBinaryViewContract<")
                .Append(interfaceType.ToDisplayString(QualifiedTypeFormat)).Append(", ")
                .Append(interfaceType.ToDisplayString(QualifiedTypeFormat)).Append(".View>");
        }
        return header.ToString();
    }

    private static string AccessibilityText(Accessibility accessibility)
    {
        switch (accessibility)
        {
            case Accessibility.Public: return "public";
            case Accessibility.Internal: return "internal";
            case Accessibility.Private: return "private";
            case Accessibility.Protected: return "protected";
            case Accessibility.ProtectedOrInternal: return "protected internal";
            case Accessibility.ProtectedAndInternal: return "private protected";
            default: return string.Empty;
        }
    }

    private static bool IsAssemblyAccessible(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility == Accessibility.Private ||
                current.DeclaredAccessibility == Accessibility.Protected ||
                current.DeclaredAccessibility == Accessibility.ProtectedAndInternal)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsPartial(INamedTypeSymbol type)
    {
        if (type.DeclaringSyntaxReferences.Length == 0)
            return false;
        foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not TypeDeclarationSyntax declaration ||
                !declaration.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PartialKeyword)))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsFileLocal(INamedTypeSymbol type)
    {
        return type.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is TypeDeclarationSyntax declaration &&
            declaration.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.FileKeyword)));
    }

    private static string WriterMethod(PrimitiveKind primitive)
    {
        switch (primitive)
        {
            case PrimitiveKind.Boolean: return "WriteBoolean";
            case PrimitiveKind.Byte: return "WriteByte";
            case PrimitiveKind.SByte: return "WriteSByte";
            case PrimitiveKind.Int16: return "WriteInt16";
            case PrimitiveKind.UInt16: return "WriteUInt16";
            case PrimitiveKind.Int32: return "WriteInt32";
            case PrimitiveKind.UInt32: return "WriteUInt32";
            case PrimitiveKind.Int64: return "WriteInt64";
            case PrimitiveKind.UInt64: return "WriteUInt64";
            case PrimitiveKind.Single: return "WriteSingle";
            case PrimitiveKind.Double: return "WriteDouble";
            case PrimitiveKind.Char: return "WriteChar";
            case PrimitiveKind.Guid: return "WriteGuid";
            default: throw new InvalidOperationException($"Unsupported primitive {primitive}.");
        }
    }

    private static string ReaderMethod(PrimitiveKind primitive)
    {
        switch (primitive)
        {
            case PrimitiveKind.Boolean: return "ReadBoolean";
            case PrimitiveKind.Byte: return "ReadByte";
            case PrimitiveKind.SByte: return "ReadSByte";
            case PrimitiveKind.Int16: return "ReadInt16";
            case PrimitiveKind.UInt16: return "ReadUInt16";
            case PrimitiveKind.Int32: return "ReadInt32";
            case PrimitiveKind.UInt32: return "ReadUInt32";
            case PrimitiveKind.Int64: return "ReadInt64";
            case PrimitiveKind.UInt64: return "ReadUInt64";
            case PrimitiveKind.Single: return "ReadSingle";
            case PrimitiveKind.Double: return "ReadDouble";
            case PrimitiveKind.Char: return "ReadChar";
            case PrimitiveKind.Guid: return "ReadGuid";
            default: throw new InvalidOperationException($"Unsupported primitive {primitive}.");
        }
    }

    private static string PrimitiveType(PrimitiveKind primitive)
    {
        switch (primitive)
        {
            case PrimitiveKind.Byte: return "byte";
            case PrimitiveKind.SByte: return "sbyte";
            case PrimitiveKind.Int16: return "short";
            case PrimitiveKind.UInt16: return "ushort";
            case PrimitiveKind.Int32: return "int";
            case PrimitiveKind.UInt32: return "uint";
            case PrimitiveKind.Int64: return "long";
            case PrimitiveKind.UInt64: return "ulong";
            default: throw new InvalidOperationException($"Primitive {primitive} is not a legal enum underlying type.");
        }
    }

    private static AttributeData? FindAttribute(ISymbol symbol, string metadataName)
    {
        return symbol.GetAttributes().FirstOrDefault(attribute =>
            string.Equals(attribute.AttributeClass?.ToDisplayString(), metadataName, StringComparison.Ordinal));
    }

    private static bool HasAttribute(ISymbol symbol, string metadataName) =>
        FindAttribute(symbol, metadataName) is not null;

    private static Location? ContractLocation(INamedTypeSymbol type) =>
        FindAttribute(type, ContractAttributeName)?.ApplicationSyntaxReference?.GetSyntax().GetLocation() ??
        type.Locations.FirstOrDefault();

    private static Location? MemberLocation(ISymbol member) => member.Locations.FirstOrDefault();

    private static string StableName(ITypeSymbol type) => type.ToDisplayString(StableNameFormat);

    private static string Escape(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ||
        SyntaxFacts.GetContextualKeywordKind(identifier) != SyntaxKind.None
            ? "@" + identifier
            : identifier;

    private static string Literal(string value) =>
        Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);

    private static string Sanitize(string value)
    {
        StringBuilder result = new(value.Length);
        foreach (char character in value)
            result.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        return result.ToString();
    }

    private static StringBuilder AppendIndent(StringBuilder source, int indent) =>
        source.Append(' ', checked(indent * 4));

    private static string WriteHelperName(MemberModel member) =>
        GeneratedMemberPrefix + "WriteField" + member.Index.ToString(CultureInfo.InvariantCulture);

    private static string ReadHelperName(MemberModel member) =>
        GeneratedMemberPrefix + "ReadField" + member.Index.ToString(CultureInfo.InvariantCulture);

    private enum CompatibilityMode : byte
    {
        ExactSchema = 0,
    }

    private enum PrimitiveKind : byte
    {
        None,
        Boolean,
        Byte,
        SByte,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Single,
        Double,
        Char,
        Guid,
    }

    private enum ShapeKind : byte
    {
        Primitive = 1,
        Enum = 2,
        String = 3,
        Memory = 4,
        ContractClass = 5,
        ContractStruct = 6,
        NullableContractStruct = 7,
        Collection = 8,
        Dictionary = 9,
        Union = 10,
        NullableEnum = 11,
    }

    private enum CollectionKind : byte
    {
        Array = 1,
        List = 2,
        IList = 3,
    }

    private enum DictionaryKind : byte
    {
        Dictionary = 1,
        IDictionary = 2,
    }

    private enum VisitState : byte
    {
        Visiting,
        Visited,
    }

    private enum NativeValidationState : byte
    {
        Unvisited,
        Visiting,
        Valid,
        Invalid,
    }

    private sealed class ContractModel
    {
        internal ContractModel(
            INamedTypeSymbol symbol,
            uint epoch,
            string logicalName)
        {
            Symbol = symbol;
            Epoch = epoch;
            LogicalName = logicalName;
        }

        internal INamedTypeSymbol Symbol { get; }
        internal CompatibilityMode Compatibility => CompatibilityMode.ExactSchema;
        internal uint Epoch { get; }
        internal string LogicalName { get; set; }
        internal string TypeId { get; set; } = string.Empty;
        internal List<MemberModel> Members { get; } = new();
        internal bool IsValid { get; set; } = true;
        internal bool IsManuallyImplemented { get; set; }
        internal bool? RequiresRuntimeFingerprint { get; set; }
        internal string? CycleDescription { get; set; }
        internal ulong? Fingerprint { get; set; }
        internal bool NativeLayoutRequested { get; set; }
        internal string? NativeAbiToken { get; set; }
        internal NativeValidationState NativeValidationState { get; set; }
        internal NativeLayoutModel? NativeLayout { get; set; }
        internal string? NativeLayoutFailure { get; set; }
        internal List<ChunkReferenceModel> ChunkReferences { get; } = new();
    }

    private sealed class ChunkReferenceModel
    {
        internal ChunkReferenceModel(
            string accessorName,
            MemberModel key,
            MemberModel decodedLength)
        {
            AccessorName = accessorName;
            Key = key;
            DecodedLength = decodedLength;
        }

        internal string AccessorName { get; }
        internal MemberModel Key { get; }
        internal MemberModel DecodedLength { get; }
    }

    private sealed class NativeLayoutModel
    {
        internal NativeLayoutModel(
            string abiToken,
            ulong layoutFingerprint,
            int generatedSize,
            int coveredFieldBytes,
            int requiredAlignment,
            int pack,
            IReadOnlyList<NativeLayoutFieldModel> fields)
        {
            AbiToken = abiToken;
            LayoutFingerprint = layoutFingerprint;
            GeneratedSize = generatedSize;
            CoveredFieldBytes = coveredFieldBytes;
            RequiredAlignment = requiredAlignment;
            Pack = pack;
            Fields = fields;
        }

        internal string AbiToken { get; }
        internal ulong LayoutFingerprint { get; }
        internal int GeneratedSize { get; }
        internal int CoveredFieldBytes { get; }
        internal int RequiredAlignment { get; }
        internal int Pack { get; }
        internal IReadOnlyList<NativeLayoutFieldModel> Fields { get; }
    }

    private sealed class NativeLayoutFieldModel
    {
        internal NativeLayoutFieldModel(
            string storageName,
            string typeDescriptor,
            int offset,
            int size,
            int coveredFieldBytes,
            int alignment,
            ulong nestedLayoutFingerprint)
        {
            StorageName = storageName;
            TypeDescriptor = typeDescriptor;
            Offset = offset;
            Size = size;
            CoveredFieldBytes = coveredFieldBytes;
            Alignment = alignment;
            NestedLayoutFingerprint = nestedLayoutFingerprint;
        }

        internal string StorageName { get; }
        internal string TypeDescriptor { get; }
        internal int Offset { get; }
        internal int Size { get; }
        internal int CoveredFieldBytes { get; }
        internal int Alignment { get; }
        internal ulong NestedLayoutFingerprint { get; }
    }

    private sealed class NativeFieldShape
    {
        internal NativeFieldShape(
            int size,
            int naturalAlignment,
            int coveredFieldBytes,
            string typeDescriptor,
            ulong nestedLayoutFingerprint)
        {
            Size = size;
            NaturalAlignment = naturalAlignment;
            CoveredFieldBytes = coveredFieldBytes;
            TypeDescriptor = typeDescriptor;
            NestedLayoutFingerprint = nestedLayoutFingerprint;
        }

        internal int Size { get; }
        internal int NaturalAlignment { get; }
        internal int CoveredFieldBytes { get; }
        internal string TypeDescriptor { get; }
        internal ulong NestedLayoutFingerprint { get; }
    }

    private sealed class MemberModel
    {
        internal MemberModel(
            ISymbol symbol,
            ITypeSymbol type,
            string logicalName,
            ulong fieldKey,
            ShapeModel shape)
        {
            Symbol = symbol;
            Type = type;
            LogicalName = logicalName;
            FieldKey = fieldKey;
            Shape = shape;
        }

        internal ISymbol Symbol { get; }
        internal ITypeSymbol Type { get; }
        internal string LogicalName { get; }
        internal ulong FieldKey { get; }
        internal ShapeModel Shape { get; }
        internal int Index { get; set; }
    }

    private sealed class ShapeModel
    {
        private ShapeModel(
            ShapeKind kind,
            PrimitiveKind primitiveKind = PrimitiveKind.None,
            INamedTypeSymbol? enumType = null,
            ContractModel? nestedContract = null,
            ShapeModel? element = null,
            ITypeSymbol? elementType = null,
            bool allowsNull = false,
            CollectionKind collectionKind = default,
            DictionaryKind dictionaryKind = default,
            ShapeModel? key = null,
            ITypeSymbol? keyType = null,
            ShapeModel? value = null,
            ITypeSymbol? valueType = null,
            UnionModel? union = null)
        {
            Kind = kind;
            PrimitiveKind = primitiveKind;
            EnumType = enumType;
            NestedContract = nestedContract;
            Element = element;
            ElementType = elementType;
            AllowsNull = allowsNull;
            CollectionKind = collectionKind;
            DictionaryKind = dictionaryKind;
            Key = key;
            KeyType = keyType;
            Value = value;
            ValueType = valueType;
            Union = union;
        }

        internal ShapeKind Kind { get; }
        internal PrimitiveKind PrimitiveKind { get; }
        internal INamedTypeSymbol? EnumType { get; }
        internal ContractModel? NestedContract { get; }
        internal ShapeModel? Element { get; }
        internal ITypeSymbol? ElementType { get; }
        internal bool AllowsNull { get; }
        internal CollectionKind CollectionKind { get; }
        internal DictionaryKind DictionaryKind { get; }
        internal ShapeModel? Key { get; }
        internal ITypeSymbol? KeyType { get; }
        internal ShapeModel? Value { get; }
        internal ITypeSymbol? ValueType { get; }
        internal UnionModel? Union { get; }

        internal static ShapeModel Primitive(PrimitiveKind kind) => new(ShapeKind.Primitive, primitiveKind: kind);
        internal static ShapeModel Enum(INamedTypeSymbol type, PrimitiveKind underlying) =>
            new(ShapeKind.Enum, primitiveKind: underlying, enumType: type);
        internal static ShapeModel NullableEnum(INamedTypeSymbol type, PrimitiveKind underlying) =>
            new(ShapeKind.NullableEnum, primitiveKind: underlying, enumType: type);
        internal static ShapeModel String(bool allowsNull) => new(ShapeKind.String, allowsNull: allowsNull);
        internal static ShapeModel Memory(bool allowsNull) => new(ShapeKind.Memory, allowsNull: allowsNull);
        internal static ShapeModel ContractClass(ContractModel contract, bool allowsNull) =>
            new(ShapeKind.ContractClass, nestedContract: contract, allowsNull: allowsNull);
        internal static ShapeModel ContractStruct(ContractModel contract) =>
            new(ShapeKind.ContractStruct, nestedContract: contract);
        internal static ShapeModel NullableContract(ContractModel contract) =>
            new(ShapeKind.NullableContractStruct, nestedContract: contract);
        internal static ShapeModel Collection(
            CollectionKind collectionKind,
            ShapeModel element,
            ITypeSymbol elementType,
            bool allowsNull) =>
            new(
                ShapeKind.Collection,
                element: element,
                elementType: elementType,
                allowsNull: allowsNull,
                collectionKind: collectionKind);
        internal static ShapeModel Dictionary(
            DictionaryKind dictionaryKind,
            ShapeModel key,
            ITypeSymbol keyType,
            ShapeModel value,
            ITypeSymbol valueType,
            bool allowsNull) =>
            new(
                ShapeKind.Dictionary,
                allowsNull: allowsNull,
                dictionaryKind: dictionaryKind,
                key: key,
                keyType: keyType,
                value: value,
                valueType: valueType);
        internal static ShapeModel UnionShape(UnionModel union, bool allowsNull) =>
            new(ShapeKind.Union, allowsNull: allowsNull, union: union);
    }

    private sealed class UnionModel
    {
        internal UnionModel(INamedTypeSymbol symbol, IReadOnlyList<UnionCaseModel> cases)
        {
            Symbol = symbol;
            Cases = cases;
        }

        internal INamedTypeSymbol Symbol { get; }
        internal IReadOnlyList<UnionCaseModel> Cases { get; }
    }

    private sealed class UnionCaseModel
    {
        internal UnionCaseModel(uint tag, ContractModel contract)
        {
            Tag = tag;
            Contract = contract;
        }

        internal uint Tag { get; }
        internal ContractModel Contract { get; }
    }

    private sealed class WriteDescriptorSetr
    {
        private readonly MemoryStream _stream = new();

        internal void WriteByte(byte value) => _stream.WriteByte(value);

        internal void WriteUInt32(uint value)
        {
            _stream.WriteByte((byte)value);
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)(value >> 16));
            _stream.WriteByte((byte)(value >> 24));
        }

        internal void WriteUInt64(ulong value)
        {
            for (int shift = 0; shift < 64; shift += 8)
                _stream.WriteByte((byte)(value >> shift));
        }

        internal void WriteToken(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            WriteUInt32(checked((uint)bytes.Length));
            _stream.Write(bytes, 0, bytes.Length);
        }

        internal byte[] ToArray() => _stream.ToArray();
    }
}

#pragma warning restore RS2008
