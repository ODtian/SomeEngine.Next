using System;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace SomeEngine.Generators;

public sealed partial class BinaryContractGenerator
{
    private const string TextAttributeName = "SomeEngine.Serialization.TextSerializableAttribute";
    private static readonly DiagnosticDescriptor InvalidTextShape = new("SEBC013", "Text encoding is unavailable",
        "Text encoding for '{0}' cannot be generated: {1}", "SomeEngine.Serialization", DiagnosticSeverity.Error, true);
    private const string Json = "global::System.Text.Json.";
    private const string Text = "global::SomeEngine.Serialization.TextSerializer";

    private static bool SupportsText(ContractModel model, out string failure)
    {
        foreach (MemberModel member in model.Members)
            if (HasAttribute(member.Symbol, "SomeEngine.Serialization.TextIgnoreAttribute")
                && member.Symbol is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true })
            { failure = "required field '" + member.Symbol.Name + "' cannot be excluded from text"; return false; }
        foreach (MemberModel member in TextMembers(model))
            if (!SupportsText(member.Shape, out failure)) { failure = member.Symbol.Name + ": " + failure; return false; }
        failure = "";
        return true;
    }

    private static bool SupportsText(ShapeModel shape, out string failure)
    {
        if (shape.Kind == ShapeKind.Dictionary)
            return SupportsText(shape.Key!, out failure) && SupportsText(shape.Value!, out failure);
        if (shape.Kind == ShapeKind.Union)
        {
            foreach (UnionCaseModel item in shape.Union!.Cases)
                if (!HasTextCodec(item.Contract))
                { failure = "union case '" + item.Contract.Symbol.Name + "' must provide static text encoding"; return false; }
        }
        if (shape.Element is not null) return SupportsText(shape.Element, out failure);
        if (shape.NestedContract is { } nested && !HasTextCodec(nested))
        { failure = "nested type '" + nested.Symbol.Name + "' must provide static text encoding"; return false; }
        failure = "";
        return true;
    }

    private static bool HasTextCodec(ContractModel model) => HasAttribute(model.Symbol, TextAttributeName)
        || model.Symbol.AllInterfaces.Any(type => type.OriginalDefinition.ToDisplayString() == "SomeEngine.Serialization.ITextSerializable<TSelf>");

    private static void EmitTextMethods(StringBuilder output, ContractModel model, int indent)
    {
        string type = model.Symbol.ToDisplayString(QualifiedTypeFormat);
        void Line(string code) => AppendIndent(output, indent).AppendLine(code);
        bool objectReader = SupportsObjectReader(model.Symbol);
        bool constructFirst = objectReader && TextMembers(model).All(member =>
            member.Symbol is not IPropertySymbol { IsRequired: true } and not IFieldSymbol { IsRequired: true });
        if (!constructFirst)
        {
            if (model.Symbol.TypeKind == TypeKind.Class
                && model.Symbol.InstanceConstructors.Any(constructor => constructor.IsImplicitlyDeclared && constructor.Parameters.Length == 0))
                Line("public " + Escape(model.Symbol.Name) + "() { }");
            Line("private readonly struct __TextReadTag { }");
            Line("#pragma warning disable CS8618 // The reader checks required fields before returning.");
            Line("[global::System.Diagnostics.CodeAnalysis.SetsRequiredMembers]");
        }
        string arguments = "";
        if (model.Symbol.TypeKind == TypeKind.Struct)
        {
            IMethodSymbol? primary = model.Symbol.InstanceConstructors.FirstOrDefault(constructor =>
                constructor.Parameters.Length > 0 && constructor.DeclaringSyntaxReferences.Any(reference =>
                    reference.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax));
            if (primary is not null)
                arguments = string.Join(", ", primary.Parameters.Select(parameter =>
                    "default(" + parameter.Type.ToDisplayString(QualifiedTypeFormat) + ")!"));
        }
        if (constructFirst)
        {
            Line("public static " + type + " ReadText(" + Json + "JsonElement fields) {");
            Line("var result = new " + type + "();");
        }
        else Line("private " + Escape(model.Symbol.Name) + "(" + Json + "JsonElement fields, __TextReadTag _) : this(" + arguments + ") {");
        bool disposable = model.Symbol.AllInterfaces.Any(value => value.ToDisplayString() == "System.IDisposable");
        if (disposable) Line("try {");
        EmitTextFields(output, model, indent + 1, constructFirst ? "result." : "this.");
        if (constructFirst) Line("return result;");
        if (disposable)
        {
            Line("} catch (global::System.Exception failure) {");
            Line("try { ((global::System.IDisposable)" + (constructFirst ? "result" : "this") + ").Dispose(); }");
            Line("catch (global::System.Exception cleanup) { throw new global::System.AggregateException(failure, cleanup); }");
            Line("throw; }");
        }
        Line("}");
        if (!constructFirst) Line("#pragma warning restore CS8618");
        if (!constructFirst) Line("public static " + type + " ReadText(" + Json + "JsonElement value) => new(value, default(__TextReadTag));");
        if (objectReader)
        {
            Line("public static void ReadTextInto(" + Json + "JsonElement fields, " + type + " target) {");
            Line("global::System.ArgumentNullException.ThrowIfNull(target);");
            EmitTextFields(output, model, indent + 1, "target.", requireMembers: false);
            Line("}");
        }
        Line("public static void WriteText(" + Json + "Utf8JsonWriter writer, " + type + " value) {");
        if (model.Symbol.IsReferenceType) Line("global::System.ArgumentNullException.ThrowIfNull(value);");
        Line("writer.WriteStartObject();");
        foreach (MemberModel member in TextMembers(model))
        {
            Line("writer.WritePropertyName(" + Literal(member.LogicalName) + ");");
            Line("__WriteText" + member.Index + "(writer, value." + Escape(member.Symbol.Name) + ");");
        }
        Line("writer.WriteEndObject(); }");
        foreach (MemberModel member in TextMembers(model))
            EmitTextHelpers(output, member.Shape, member.Type, member.Index.ToString(), indent);
    }

    private static void EmitTextFields(StringBuilder output, ContractModel model, int indent, string target, bool requireMembers = true)
    {
        void Line(string code) => AppendIndent(output, indent).AppendLine(code);
        Line("if (fields.ValueKind != " + Json + "JsonValueKind.Object) throw new " + Json + "JsonException(\"Expected an object.\");");
        foreach (MemberModel member in TextMembers(model)) Line("bool seen" + member.Index + " = false;");
        Line("foreach (var field in fields.EnumerateObject()) { switch (field.Name) {");
        foreach (MemberModel member in TextMembers(model))
        {
            string index = member.Index.ToString();
            Line("case " + Literal(member.LogicalName) + ": {");
            Line("if (seen" + index + ") throw new " + Json + "JsonException(" + Literal("Duplicate field: " + member.LogicalName) + ");");
            Line("seen" + index + " = true;");
            if (member.Shape.Kind == ShapeKind.Collection && member.Shape.CollectionKind is CollectionKind.List or CollectionKind.IList)
            {
                Line("var existing = " + target + Escape(member.Symbol.Name) + ";");
                string elementType = member.Shape.ElementType!.ToDisplayString(QualifiedTypeFormat);
                Line("if (field.Value.ValueKind == " + Json + "JsonValueKind.Array && existing is not null && !((global::System.Collections.Generic.ICollection<" + elementType + ">)existing).IsReadOnly) {");
                Line("existing.Clear(); foreach (var item in field.Value.EnumerateArray()) existing.Add(__ReadText" + index + "Element(item));");
                Line("} else " + target + Escape(member.Symbol.Name) + " = __ReadText" + index + "(field.Value);");
            }
            else Line(target + Escape(member.Symbol.Name) + " = __ReadText" + index + "(field.Value);");
            Line("break; }");
        }
        Line("default: throw new " + Json + "JsonException(\"Unknown field: \" + field.Name); } }");
        if (requireMembers)
            foreach (MemberModel member in TextMembers(model).Where(member => member.Symbol is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true }))
                Line("if (!seen" + member.Index + ") throw new " + Json + "JsonException(" + Literal("Missing required field: " + member.LogicalName) + ");");
    }

    private static System.Collections.Generic.IEnumerable<MemberModel> TextMembers(ContractModel model)
        => model.Members.Where(member => !HasAttribute(member.Symbol, "SomeEngine.Serialization.TextIgnoreAttribute"));

    private static void EmitTextHelpers(StringBuilder output, ShapeModel shape, ITypeSymbol type, string suffix, int indent)
    {
        void Line(string code) => AppendIndent(output, indent).AppendLine(code);
        string typeName = type.ToDisplayString(QualifiedTypeFormat);
        string value = shape.Kind is ShapeKind.NullableEnum or ShapeKind.NullableContractStruct
            || shape.Kind == ShapeKind.Memory && shape.AllowsNull
            || shape.Kind == ShapeKind.Collection && shape.CollectionKind == CollectionKind.NullableImmutableArray ? "value.Value" : "value";
        bool nullable = shape.AllowsNull || shape.Kind is ShapeKind.NullableEnum or ShapeKind.NullableContractStruct
            || shape.CollectionKind == CollectionKind.NullableImmutableArray;
        Line("private static void __WriteText" + suffix + "(" + Json + "Utf8JsonWriter writer, " + typeName + " value) {");
        if (nullable) Line("if (value is null) { writer.WriteNullValue(); return; }");
        else if (type.IsReferenceType) Line("global::System.ArgumentNullException.ThrowIfNull(value);");
        switch (shape.Kind)
        {
            case ShapeKind.Primitive:
                Line(WriteTextPrimitive(shape.PrimitiveKind, "value"));
                break;
            case ShapeKind.String: Line("writer.WriteStringValue(value);"); break;
            case ShapeKind.Enum:
            case ShapeKind.NullableEnum: Line("writer.WriteStringValue(" + value + ".ToString());"); break;
            case ShapeKind.Memory: Line("writer.WriteBase64StringValue(" + value + ".Span);"); break;
            case ShapeKind.ContractClass:
            case ShapeKind.ContractStruct:
            case ShapeKind.NullableContractStruct:
                Line(shape.NestedContract!.Symbol.ToDisplayString(QualifiedTypeFormat) + ".WriteText(writer, " + value + ");");
                break;
            case ShapeKind.Collection:
                if (shape.CollectionKind is CollectionKind.ImmutableArray or CollectionKind.NullableImmutableArray)
                    Line("if (" + value + ".IsDefault) throw new " + Json + "JsonException(\"An immutable array must be initialized.\");");
                Line("writer.WriteStartArray(); foreach (var item in " + value + ") __WriteText" + suffix + "Element(writer, item); writer.WriteEndArray();");
                break;
            case ShapeKind.Dictionary:
                string entry = "global::System.Collections.Generic.KeyValuePair<" + shape.KeyType!.ToDisplayString(QualifiedTypeFormat)
                    + ", " + shape.ValueType!.ToDisplayString(QualifiedTypeFormat) + ">";
                Line("var entries = new global::System.Collections.Generic.List<" + entry + ">(value);");
                Line("entries.Sort(static (left, right) => " + CanonicalComparison(shape.Key!, "left.Key", "right.Key") + ");");
                Line("writer.WriteStartArray();");
                Line("for (int i = 0; i < entries.Count; i++) {");
                Line("var item = entries[i];");
                Line("if (i > 0 && " + CanonicalComparison(shape.Key!, "entries[i - 1].Key", "item.Key")
                    + " == 0) throw new " + Json + "JsonException(\"Duplicate dictionary key.\");");
                Line("writer.WriteStartObject(); writer.WritePropertyName(\"Key\"); __WriteText" + suffix + "Key(writer, item.Key);");
                Line("writer.WritePropertyName(\"Value\"); __WriteText" + suffix + "Value(writer, item.Value); writer.WriteEndObject(); }");
                Line("writer.WriteEndArray();");
                break;
            case ShapeKind.Union:
                Line("writer.WriteStartObject(); switch (value) {");
                foreach (UnionCaseModel item in shape.Union!.Cases)
                {
                    string caseType = item.Contract.Symbol.ToDisplayString(QualifiedTypeFormat);
                    Line("case " + caseType + " item" + item.Tag + ":");
                    Line("writer.WriteNumber(\"Tag\", " + item.Tag + "U); writer.WritePropertyName(\"Value\");");
                    Line(caseType + ".WriteText(writer, item" + item.Tag + "); break;");
                }
                Line("default: throw new " + Json + "JsonException(\"Value is not a declared union case.\"); }");
                Line("writer.WriteEndObject();");
                break;
        }
        Line("}");
        Line("private static " + typeName + " __ReadText" + suffix + "(" + Json + "JsonElement value) {");
        if (nullable) Line("if (value.ValueKind == " + Json + "JsonValueKind.Null) return default;");
        else Line("if (value.ValueKind == " + Json + "JsonValueKind.Null) throw new " + Json + "JsonException(\"A non-nullable value was null.\");");
        switch (shape.Kind)
        {
            case ShapeKind.Primitive: Line("return " + ReadTextPrimitive(shape.PrimitiveKind) + ";"); break;
            case ShapeKind.String: Line("return value.GetString()!;"); break;
            case ShapeKind.Enum:
            case ShapeKind.NullableEnum:
                Line("return global::System.Enum.Parse<" + shape.EnumType!.ToDisplayString(QualifiedTypeFormat) + ">(value.GetString()!, false);");
                break;
            case ShapeKind.Memory:
                Line("return new " + type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(QualifiedTypeFormat).TrimEnd('?') + "(value.GetBytesFromBase64());");
                break;
            case ShapeKind.ContractClass:
            case ShapeKind.ContractStruct:
            case ShapeKind.NullableContractStruct:
                Line("return " + shape.NestedContract!.Symbol.ToDisplayString(QualifiedTypeFormat) + ".ReadText(value);");
                break;
            case ShapeKind.Collection:
                string element = shape.ElementType!.ToDisplayString(QualifiedTypeFormat);
                Line("var values = new global::System.Collections.Generic.List<" + element + ">(value.GetArrayLength());");
                Line("foreach (var item in value.EnumerateArray()) values.Add(__ReadText" + suffix + "Element(item));");
                string result = shape.CollectionKind switch
                {
                    CollectionKind.Array => "values.ToArray()",
                    CollectionKind.ImmutableArray or CollectionKind.NullableImmutableArray => "global::System.Collections.Immutable.ImmutableArray.CreateRange(values)",
                    CollectionKind.ImmutableList => "global::System.Collections.Immutable.ImmutableList.CreateRange(values)",
                    _ => "values",
                };
                Line("return " + result + ";");
                break;
            case ShapeKind.Dictionary:
                Line("var values = new global::System.Collections.Generic.Dictionary<" + shape.KeyType!.ToDisplayString(QualifiedTypeFormat)
                    + ", " + shape.ValueType!.ToDisplayString(QualifiedTypeFormat) + ">(value.GetArrayLength());");
                Line("foreach (var item in value.EnumerateArray()) {");
                Line("var pair = " + Text + ".ReadPair(item, \"Key\", \"Value\");");
                Line("var key = __ReadText" + suffix + "Key(pair.First);");
                Line("if (values.ContainsKey(key)) throw new " + Json + "JsonException(\"Duplicate dictionary key.\");");
                Line("values.Add(key, __ReadText" + suffix + "Value(pair.Second)); }");
                Line("return values;");
                break;
            case ShapeKind.Union:
                Line("var pair = " + Text + ".ReadPair(value, \"Tag\", \"Value\");");
                Line("return pair.First.GetUInt32() switch {");
                foreach (UnionCaseModel item in shape.Union!.Cases)
                    Line(item.Tag + "U => " + item.Contract.Symbol.ToDisplayString(QualifiedTypeFormat) + ".ReadText(pair.Second),");
                Line("_ => throw new " + Json + "JsonException(\"Unknown union tag.\"), };");
                break;
        }
        Line("}");
        if (shape.Element is not null) EmitTextHelpers(output, shape.Element, shape.ElementType!, suffix + "Element", indent);
        if (shape.Kind == ShapeKind.Dictionary)
        {
            EmitTextHelpers(output, shape.Key!, shape.KeyType!, suffix + "Key", indent);
            EmitTextHelpers(output, shape.Value!, shape.ValueType!, suffix + "Value", indent);
        }
    }

    private static string WriteTextPrimitive(PrimitiveKind kind, string value) => kind switch
    {
        PrimitiveKind.Boolean => "writer.WriteBooleanValue(" + value + ");",
        PrimitiveKind.Char => "writer.WriteStringValue(" + value + ".ToString());",
        PrimitiveKind.Guid => "writer.WriteStringValue(" + value + ");",
        PrimitiveKind.Single or PrimitiveKind.Double => Text + ".WriteNumber(writer, " + value + ");",
        _ => "writer.WriteNumberValue(" + value + ");",
    };

    private static string ReadTextPrimitive(PrimitiveKind kind) => kind switch
    {
        PrimitiveKind.Char => Text + ".ReadCharacter(value)",
        PrimitiveKind.Single => "(float)" + Text + ".ReadNumber(value)",
        PrimitiveKind.Double => Text + ".ReadNumber(value)",
        _ => "value.Get" + kind + "()",
    };
}
