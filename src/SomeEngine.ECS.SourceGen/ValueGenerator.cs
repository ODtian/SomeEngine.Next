using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.ECS.SourceGen;

internal static class ValueGenerator
{
    private sealed class Member(ISymbol symbol, ITypeSymbol type)
    {
        internal readonly ISymbol Symbol = symbol;
        internal readonly ITypeSymbol Type = type;
        internal string Name => EcsGenerator.Identifier(Symbol.Name);
        internal INamedTypeSymbol? Codec => Symbol.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString()
            == "SomeEngine.ECS.Serialization.FieldCodecAttribute")?.ConstructorArguments[0].Value as INamedTypeSymbol;
    }
    private sealed class Unsupported(ITypeSymbol type) : Exception { internal readonly ITypeSymbol Type = type; }

    internal static void Emit(SourceProductionContext output, INamedTypeSymbol type, bool component, bool codec)
    {
        var members = new List<Member>();
        foreach (ISymbol symbol in type.GetMembers().OrderBy(value => value.Locations.FirstOrDefault()?.SourceSpan.Start ?? int.MaxValue))
            if (symbol is IFieldSymbol field && !field.IsStatic && !field.IsImplicitlyDeclared) members.Add(new(field, field.Type));
            else if (symbol is IPropertySymbol property && !property.IsStatic && !property.IsIndexer && property.SetMethod is not null)
                members.Add(new(property, property.Type));
        string self = EcsGenerator.Name(type);
        var interfaces = new List<string>();
        if (component && !EcsGenerator.HasInterface(type, "SomeEngine.ECS.IComponent")) interfaces.Add("global::SomeEngine.ECS.IComponent");
        bool remap = component && members.Any(member => References(member.Type))
            && !type.AllInterfaces.Any(value => value.Name == "IEntityReferenceValue");
        if (remap) interfaces.Add("global::SomeEngine.ECS.IEntityReferenceValue<" + self + ">");
        var code = new StringBuilder();
        code.Append("private readonly struct __ValueInput { }\nprivate ").Append(EcsGenerator.Identifier(type.Name)).Append("(__ValueInput __input");
        for (int i = 0; i < members.Count; i++) code.Append(", ").Append(EcsGenerator.Name(members[i].Type)).Append(" __value").Append(i);
        IMethodSymbol? primary = type.InstanceConstructors.FirstOrDefault(constructor => constructor.DeclaringSyntaxReferences
            .Any(reference => reference.GetSyntax() is TypeDeclarationSyntax));
        code.Append(") : this(");
        if (primary is not null) code.Append(string.Join(", ", primary.Parameters.Select(parameter => "default(" + EcsGenerator.Name(parameter.Type) + ")!")));
        code.Append(") {\n");
        for (int i = 0; i < members.Count; i++) code.Append("this.").Append(members[i].Name).Append(" = __value").Append(i).Append(";\n");
        code.Append("}\n");
        try
        {
            if (codec) EmitCodec(code, type, self, members);
            if (component && members.Any(member => Owned(member.Type))) EmitOperations(code, type, self, members);
            if (remap) EmitRemap(code, self, members);
            if (component) EmitFields(code, self, members);
        }
        catch (Unsupported unsupported)
        { EcsGenerator.Error(output, type, "Provide FieldCodec or generated value operations for " + unsupported.Type.ToDisplayString() + "."); return; }
        EcsGenerator.Add(output, type, "Value", EcsGenerator.Wrap(type, string.Join(", ", interfaces), code.ToString()));
    }

    private static void Construct(StringBuilder code, string self, IEnumerable<string> values)
        => code.Append("new ").Append(self).Append("(default(__ValueInput)").Append(string.Concat(values.Select(value => ", " + value + "!"))).Append(')');

    private static void EmitCodec(StringBuilder code, INamedTypeSymbol type, string self, List<Member> members)
    {
        code.Append("public readonly struct Codec : global::SomeEngine.ECS.Serialization.IDurableComponentCodec<").Append(self).Append("> {\n")
            .Append("public void Write(ref global::SomeEngine.ECS.Serialization.DataWriter writer, in ").Append(self).Append(" value) {\n");
        int id = 0;
        foreach (Member member in members)
            if (member.Codec is { } codec) code.Append("new ").Append(EcsGenerator.Name(codec)).Append("().Write(ref writer, value.").Append(member.Name).Append(");\n");
            else Write(code, member.Type, "value." + member.Name, ref id);
        code.Append("}\npublic void Read(ref global::SomeEngine.ECS.Serialization.DataReader reader, out ").Append(self).Append(" value) {\n");
        for (int i = 0; i < members.Count; i++)
        {
            Member member = members[i]; string local = "__value" + i;
            if (member.Codec is { } codec) code.Append("new ").Append(EcsGenerator.Name(codec)).Append("().Read(ref reader, out ")
                .Append(EcsGenerator.Name(member.Type)).Append(' ').Append(local).Append(");\n");
            else Read(code, member.Type, local, ref id);
        }
        code.Append("value = "); Construct(code, self, Enumerable.Range(0, members.Count).Select(i => "__value" + i));
        string registration = EcsGenerator.HasInterface(type, "SomeEngine.ECS.ISharedComponent") ? "RegisterShared" : "Register";
        code.Append(";\n}\n}\npublic static global::SomeEngine.ECS.Serialization.SerializationRegistry RegisterCodec(global::SomeEngine.ECS.Serialization.SerializationRegistry registry, global::SomeEngine.ECS.Serialization.SerializationTypeKey key) => registry.").Append(registration).Append('<')
            .Append(self).Append(", Codec>(key);\n");
    }

    private static string? Primitive(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_Boolean => "Boolean", SpecialType.System_Byte => "Byte", SpecialType.System_SByte => "SByte",
        SpecialType.System_Int16 => "Int16", SpecialType.System_UInt16 => "UInt16", SpecialType.System_Int32 => "Int32",
        SpecialType.System_UInt32 => "UInt32", SpecialType.System_Int64 => "Int64", SpecialType.System_UInt64 => "UInt64",
        SpecialType.System_Single => "Single", SpecialType.System_Double => "Double", SpecialType.System_Char => "Char",
        SpecialType.System_String => "String", _ => type.ToDisplayString() switch
        { "System.Guid" => "Guid", "SomeEngine.ECS.Entities.Entity" => "Entity", _ => null }
    };

    private static void Write(StringBuilder code, ITypeSymbol type, string value, ref int id)
    {
        string? primitive = Primitive(type);
        if (primitive is not null) { code.Append("writer.Write").Append(primitive).Append('(').Append(value).Append(");\n"); return; }
        if (type is INamedTypeSymbol named && named.TypeKind == TypeKind.Enum)
        { code.Append("writer.Write").Append(Primitive(named.EnumUnderlyingType!)).Append("((").Append(EcsGenerator.Name(named.EnumUnderlyingType!)).Append(')').Append(value).Append(");\n"); return; }
        if (type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            code.Append("writer.WriteBoolean(").Append(value).Append(".HasValue); if (").Append(value).Append(".HasValue) {\n");
            Write(code, nullable.TypeArguments[0], value + ".Value", ref id); code.Append("}\n"); return;
        }
        if (type is IArrayTypeSymbol array && array.Rank == 1)
        {
            int number = id++;
            code.Append("writer.WriteInt32(").Append(value).Append(" is null ? -1 : ").Append(value).Append(".Length); if (")
                .Append(value).Append(" is not null) foreach (var __item").Append(number).Append(" in ").Append(value).Append(") {\n");
            Write(code, array.ElementType, "__item" + number, ref id); code.Append("}\n"); return;
        }
        if (EcsGenerator.HasAttribute(type, "SomeEngine.ECS.Serialization.GenerateCodecAttribute"))
        { code.Append("new ").Append(EcsGenerator.Name(type)).Append(".Codec().Write(ref writer, ").Append(value).Append(");\n"); return; }
        throw new Unsupported(type);
    }

    private static void Read(StringBuilder code, ITypeSymbol type, string local, ref int id)
    {
        string? primitive = Primitive(type);
        if (primitive is not null) { code.Append(EcsGenerator.Name(type)).Append(' ').Append(local).Append(" = reader.Read").Append(primitive).Append("()").Append(type.SpecialType == SpecialType.System_String ? "!" : "").Append(";\n"); return; }
        if (type is INamedTypeSymbol named && named.TypeKind == TypeKind.Enum)
        { code.Append(EcsGenerator.Name(type)).Append(' ').Append(local).Append(" = (").Append(EcsGenerator.Name(type)).Append(")reader.Read").Append(Primitive(named.EnumUnderlyingType!)).Append("();\n"); return; }
        if (type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            int number = id++;
            code.Append(EcsGenerator.Name(type)).Append(' ').Append(local).Append("; if (reader.ReadBoolean()) {\n");
            Read(code, nullable.TypeArguments[0], "__nullable" + number, ref id);
            code.Append(local).Append(" = __nullable").Append(number).Append("; } else ").Append(local).Append(" = null;\n"); return;
        }
        if (type is IArrayTypeSymbol array && array.Rank == 1)
        {
            int number = id++;
            code.Append("int __length").Append(number).Append(" = reader.ReadInt32();\n").Append(EcsGenerator.Name(type)).Append(' ').Append(local)
                .Append("; if (__length").Append(number).Append(" < 0) ").Append(local).Append(" = null!; else {\n")
                .Append(local).Append(" = (").Append(EcsGenerator.Name(type)).Append(")global::System.Array.CreateInstance(typeof(")
                .Append(EcsGenerator.Name(array.ElementType)).Append("), __length").Append(number).Append(");\n")
                .Append("for (int __index").Append(number).Append(" = 0; __index").Append(number).Append(" < __length").Append(number).Append("; __index").Append(number).Append("++) {\n");
            Read(code, array.ElementType, "__item" + number, ref id);
            code.Append(local).Append("[__index").Append(number).Append("] = __item").Append(number).Append(";\n}\n}\n"); return;
        }
        if (EcsGenerator.HasAttribute(type, "SomeEngine.ECS.Serialization.GenerateCodecAttribute"))
        { code.Append("new ").Append(EcsGenerator.Name(type)).Append(".Codec().Read(ref reader, out ").Append(EcsGenerator.Name(type)).Append(' ').Append(local).Append(");\n"); return; }
        throw new Unsupported(type);
    }

    private static bool Owned(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol or ITypeParameterSymbol || type.IsReferenceType && type.SpecialType != SpecialType.System_String
            || EcsGenerator.HasInterface(type, "System.IDisposable")
            || type is INamedTypeSymbol operations && operations.AllInterfaces.Any(value => value.Name == "IComponentOperations")) return true;
        if (!EcsGenerator.HasAttribute(type, "SomeEngine.ECS.GenerateComponentAttribute") || type is not INamedTypeSymbol named) return false;
        return named.GetMembers().Any(member => member is IFieldSymbol field && !field.IsStatic && !field.IsImplicitlyDeclared && Owned(field.Type)
            || member is IPropertySymbol property && !property.IsStatic && !property.IsIndexer && property.SetMethod is not null && Owned(property.Type));
    }

    private static void EmitOperations(StringBuilder code, INamedTypeSymbol type, string self, List<Member> members)
    {
        code.Append("public readonly struct ValueOperations : global::SomeEngine.ECS.Storage.IComponentOperations<").Append(self).Append("> {\n")
            .Append("public ").Append(self).Append(" Clone(global::SomeEngine.ECS.World world, in ").Append(self).Append(" value) {\n");
        int id = 0;
        for (int i = 0; i < members.Count; i++) Clone(code, members[i].Type, "value." + members[i].Name, "__value" + i, ref id);
        code.Append("return "); Construct(code, self, Enumerable.Range(0, members.Count).Select(i => "__value" + i));
        code.Append(";\n}\npublic void Release(global::SomeEngine.ECS.World world, ref ").Append(self).Append(" value) {\n");
        foreach (Member member in members) Release(code, member.Type, "value." + member.Name, ref id);
        code.Append("value = default;\n}\n}\n");
        bool module = type.TypeParameters.Length == 0;
        for (INamedTypeSymbol? parent = type; parent is not null; parent = parent.ContainingType)
            module &= parent.TypeParameters.Length == 0 && parent.DeclaredAccessibility is Accessibility.Internal or Accessibility.Public;
        if (module) code.Append("[global::System.Runtime.CompilerServices.ModuleInitializer]\n");
        code.Append("public static void RegisterComponentOperations() => global::SomeEngine.ECS.ComponentValues.Register<")
            .Append(self).Append(", ValueOperations>(new ValueOperations());\n");
    }

    private static void Clone(StringBuilder code, ITypeSymbol type, string value, string local, ref int id)
    {
        if (type is IArrayTypeSymbol array && array.Rank == 1)
        {
            int number = id++;
            code.Append(EcsGenerator.Name(type)).Append(' ').Append(local).Append(" = ").Append(value).Append(" is null ? null! : (")
                .Append(EcsGenerator.Name(type)).Append(')').Append(value).Append(".Clone();\n");
            if (Owned(array.ElementType))
            {
                code.Append("if (").Append(local).Append(" is not null) for (int __index").Append(number).Append(" = 0; __index").Append(number)
                    .Append(" < ").Append(local).Append(".Length; __index").Append(number).Append("++) {\n");
                Clone(code, array.ElementType, value + "[__index" + number + "]", "__item" + number, ref id);
                code.Append(local).Append("[__index").Append(number).Append("] = __item").Append(number).Append(";\n}\n");
            }
            return;
        }
        if (type.IsReferenceType && type.SpecialType != SpecialType.System_String)
        {
            if (!EcsGenerator.HasInterface(type, "System.ICloneable")) throw new Unsupported(type);
            code.Append(EcsGenerator.Name(type)).Append(' ').Append(local).Append(" = ").Append(value).Append(" is null ? null! : (")
                .Append(EcsGenerator.Name(type)).Append(")((global::System.ICloneable)").Append(value).Append(").Clone();\n"); return;
        }
        code.Append(EcsGenerator.Name(type)).Append(' ').Append(local).Append(" = ");
        if (Owned(type)) code.Append("global::SomeEngine.ECS.ComponentValues.Clone(world, ").Append(value).Append(')');
        else code.Append(value);
        code.Append(";\n");
    }

    private static void Release(StringBuilder code, ITypeSymbol type, string value, ref int id)
    {
        if (type is IArrayTypeSymbol array && Owned(array.ElementType))
        {
            int number = id++; code.Append("if (").Append(value).Append(" is not null) foreach (var __release").Append(number).Append(" in ").Append(value).Append(") {\n");
            Release(code, array.ElementType, "__release" + number, ref id); code.Append("}\n");
        }
        else if (type.IsReferenceType && EcsGenerator.HasInterface(type, "System.IDisposable"))
            code.Append("((global::System.IDisposable?)").Append(value).Append(")?.Dispose();\n");
        else if (type.IsValueType && Owned(type))
        { int number = id++; code.Append("var __release").Append(number).Append(" = ").Append(value).Append("; global::SomeEngine.ECS.ComponentValues.Release(world, ref __release").Append(number).Append(");\n"); }
    }

    private static bool References(ITypeSymbol type) => type.ToDisplayString() == "SomeEngine.ECS.Entities.Entity"
        || type is ITypeParameterSymbol
        || type is IArrayTypeSymbol array && References(array.ElementType)
        || type is INamedTypeSymbol named && named.AllInterfaces.Any(value => value.Name == "IEntityReferenceValue")
        || EcsGenerator.HasAttribute(type, "SomeEngine.ECS.GenerateComponentAttribute");

    private static void EmitRemap(StringBuilder code, string self, List<Member> members)
    {
        code.Append("public static void Remap(ref ").Append(self).Append(" value, in global::SomeEngine.ECS.EntityMap entities) {\n");
        int id = 0;
        for (int i = 0; i < members.Count; i++)
        {
            code.Append("var __value").Append(i).Append(" = value.").Append(members[i].Name).Append(";\n");
            Remap(code, members[i].Type, "__value" + i, ref id);
        }
        code.Append("value = "); Construct(code, self, Enumerable.Range(0, members.Count).Select(i => "__value" + i)); code.Append(";\n}\n");
    }

    private static void Remap(StringBuilder code, ITypeSymbol type, string value, ref int id)
    {
        if (type.ToDisplayString() == "SomeEngine.ECS.Entities.Entity") { code.Append(value).Append(" = entities.Map(").Append(value).Append(");\n"); return; }
        if (type is IArrayTypeSymbol array && References(array.ElementType))
        {
            int number = id++; code.Append("if (").Append(value).Append(" is not null) for (int __map").Append(number).Append(" = 0; __map").Append(number)
                .Append(" < ").Append(value).Append(".Length; __map").Append(number).Append("++) {\n");
            Remap(code, array.ElementType, value + "[__map" + number + "]", ref id); code.Append("}\n"); return;
        }
        if (References(type)) code.Append("global::SomeEngine.ECS.ComponentValues.Remap(ref ").Append(value).Append(", in entities);\n");
    }

    private static void EmitFields(StringBuilder code, string self, List<Member> members)
    {
        int[] entities = Enumerable.Range(0, members.Count).Where(i => members[i].Type.ToDisplayString() == "SomeEngine.ECS.Entities.Entity").ToArray();
        if (entities.Length == 0) return;
        code.Append("public readonly struct EntityFields : global::SomeEngine.ECS.IEntityFieldWriter<").Append(self).Append("> {\n");
        for (int i = 0; i < entities.Length; i++) code.Append("public const int ").Append(EcsGenerator.Identifier(members[entities[i]].Symbol.Name + "Field")).Append(" = ").Append(i).Append(";\n");
        code.Append("public static void Set(ref ").Append(self).Append(" value, int field, global::SomeEngine.ECS.Entities.Entity entity) { switch (field) {\n");
        for (int i = 0; i < entities.Length; i++)
        {
            int memberIndex = entities[i]; code.Append("case ").Append(i).Append(": value = ");
            Construct(code, self, Enumerable.Range(0, members.Count).Select(index => index == memberIndex ? "entity" : "value." + members[index].Name));
            code.Append("; return;\n");
        }
        code.Append("default: throw new global::System.ArgumentOutOfRangeException(nameof(field));\n} } }\n");
    }
}
