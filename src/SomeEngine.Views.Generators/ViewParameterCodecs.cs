using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SomeEngine.Views.Generators;

internal static class ViewParameterCodecs
{
    internal static IFieldSymbol[] GetFields(INamedTypeSymbol type) => type.GetMembers().OfType<IFieldSymbol>()
        .Where(field => !field.IsStatic).OrderBy(field => field.MetadataName, StringComparer.Ordinal).ToArray();

    internal static bool CanEncode(IFieldSymbol[] fields) =>
        fields.All(field => !field.IsFixedSizeBuffer && GetEncoding(field.Type) is not null);

    internal static void Generate(StringBuilder text, INamedTypeSymbol type, IFieldSymbol[] fields,
        string typeParameters, string constraints)
    {
        string typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        text.Append("    private struct ParameterCodec").Append(typeParameters)
            .Append(" : global::SomeEngine.ECS.Serialization.ICanonicalComponentCodec<").Append(typeName).Append('>');
        bool references = fields.Any(field => GetEncoding(field.Type) == "Entity");
        if (references)
            text.Append(", global::SomeEngine.ECS.Serialization.IReferencePatcher<").Append(typeName).Append('>');
        text.AppendLine(constraints).AppendLine("    {")
            .Append("        public void Write(ref global::SomeEngine.ECS.Serialization.DataWriter writer, in ")
            .Append(typeName).AppendLine(" value)\n        {\n            var parameters = value;");
        for (int index = 0; index < fields.Length; index++)
        {
            ITypeSymbol fieldType = fields[index].Type;
            string cast = fieldType.TypeKind == TypeKind.Enum
                ? "(" + ((INamedTypeSymbol)fieldType).EnumUnderlyingType!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ")" : "";
            text.Append("            writer.Write").Append(GetEncoding(fieldType)).Append('(').Append(cast)
                .Append(GetFieldAccessor(fields[index], index)).AppendLine("(ref parameters));");
        }
        text.AppendLine("        }").Append("        public void Read(ref global::SomeEngine.ECS.Serialization.DataReader reader, out ")
            .Append(typeName).AppendLine(" value)\n        {\n            value = default;");
        for (int index = 0; index < fields.Length; index++)
        {
            ITypeSymbol fieldType = fields[index].Type;
            string cast = fieldType.TypeKind == TypeKind.Enum
                ? "(" + fieldType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ")" : "";
            text.Append("            ").Append(GetFieldAccessor(fields[index], index)).Append("(ref value) = ").Append(cast)
                .Append("reader.Read").Append(GetEncoding(fieldType)).Append("()")
                .Append(GetEncoding(fieldType) == "String" ? "!" : "").AppendLine(";");
        }
        text.AppendLine("        }");
        if (references)
        {
            text.Append("        public void Remap(ref ").Append(typeName)
                .AppendLine(" value, global::SomeEngine.ECS.Serialization.IReferenceRemapper remapper)\n        {");
            for (int index = 0; index < fields.Length; index++)
            {
                if (GetEncoding(fields[index].Type) != "Entity")
                    continue;
                text.Append("            if (remapper.TryMap(").Append(GetFieldAccessor(fields[index], index)).Append("(ref value), out var entity")
                    .Append(index).AppendLine("))")
                    .Append("                ").Append(GetFieldAccessor(fields[index], index)).Append("(ref value) = entity").Append(index).AppendLine(";");
            }
            text.AppendLine("        }");
        }
        for (int index = 0; index < fields.Length; index++)
        {
            IFieldSymbol field = fields[index];
            text.Append("        [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = ")
                .Append(SymbolDisplay.FormatLiteral(field.MetadataName, true)).AppendLine(")]")
                .Append("        private static extern ref ").Append(field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .Append(' ').Append(GetFieldAccessor(field, index)).Append("(ref ").Append(typeName).AppendLine(" value);");
        }
        text.AppendLine("    }");
    }

    private static string GetFieldAccessor(IFieldSymbol field, int index)
    {
        string name = field.AssociatedSymbol?.Name ?? field.MetadataName;
        name = new string(name.Where(character => char.IsLetterOrDigit(character) || character == '_').ToArray());
        return "Get" + name + "Field" + index;
    }

    private static string? GetEncoding(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumeration)
            return GetEncoding(enumeration.EnumUnderlyingType!);
        return type.SpecialType switch
        {
            SpecialType.System_Boolean => "Boolean",
            SpecialType.System_Byte => "Byte",
            SpecialType.System_SByte => "SByte",
            SpecialType.System_Int16 => "Int16",
            SpecialType.System_UInt16 => "UInt16",
            SpecialType.System_Int32 => "Int32",
            SpecialType.System_UInt32 => "UInt32",
            SpecialType.System_Int64 => "Int64",
            SpecialType.System_UInt64 => "UInt64",
            SpecialType.System_Char => "Char",
            SpecialType.System_Single => "Single",
            SpecialType.System_Double => "Double",
            SpecialType.System_String => "String",
            _ => type.ToDisplayString() switch
            {
                "System.Guid" => "Guid",
                "SomeEngine.ECS.Entities.Entity" => "Entity",
                _ => null,
            },
        };
    }
}
