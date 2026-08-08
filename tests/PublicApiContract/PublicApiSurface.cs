using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SomeEngine.Testing;

/// <summary>
/// Produces a deterministic, reviewable fingerprint of the runtime-visible public contract.
/// Keep this source linked into each API-owning test assembly so all baselines use identical rules.
/// </summary>
internal static class PublicApiSurface
{
    private const BindingFlags DeclaredMembers =
        BindingFlags.Public |
        BindingFlags.NonPublic |
        BindingFlags.Instance |
        BindingFlags.Static |
        BindingFlags.DeclaredOnly;

    private const BindingFlags DeclaredInstanceFields =
        BindingFlags.Public |
        BindingFlags.NonPublic |
        BindingFlags.Instance |
        BindingFlags.DeclaredOnly;

    private static readonly object BuildGate = new();
    private static NullabilityInfoContext _nullability = new();

    internal static string Build(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (BuildGate)
        {
            _nullability = new NullabilityInfoContext();
            var output = new StringBuilder();
            AppendAssemblyAndModuleContracts(output, assembly);
            foreach (Type forwarded in assembly.GetForwardedTypes().OrderBy(TypeName, StringComparer.Ordinal))
                output.Append("forwarded-type ").Append(TypeName(forwarded)).Append('\n');

            foreach (Type type in assembly.DefinedTypes
                         .Select(static type => type.AsType())
                         .Where(IsExternallyVisible)
                         .OrderBy(TypeName, StringComparer.Ordinal))
            {
                AppendType(output, type);
                AppendLayoutFields(output, type);
                AppendConstructors(output, type);
                AppendMethods(output, type);
                AppendProperties(output, type);
                AppendEvents(output, type);
                AppendFields(output, type);
            }

            return output.ToString();
        }
    }

    private static void AppendAssemblyAndModuleContracts(StringBuilder output, Assembly assembly)
    {
        AppendContractAttributes(output, assembly.GetCustomAttributesData(), "assembly-attrs");
        output.Append('\n');
        AppendContractAttributes(
            output,
            assembly.ManifestModule.GetCustomAttributesData(),
            "module-attrs");
        output.Append('\n');
    }

    internal static string Sha256(string surface) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(surface)));

    internal static string FailureMessage(string assemblyName, string expected, string actual, string surface) =>
        $"{assemblyName} public API changed. Expected {expected}, actual {actual}. " +
        "Review the complete surface before updating the baseline.\n" + surface;

    private static void AppendType(StringBuilder output, Type type)
    {
        output.Append("type ")
            .Append(TypeVisibility(type)).Append(' ')
            .Append(TypeKind(type)).Append(' ')
            .Append(TypeModifiers(type))
            .Append(TypeName(type));

        if (type.IsEnum)
            output.Append(" underlying ").Append(TypeName(Enum.GetUnderlyingType(type)));
        else if (type.BaseType is not null && type.BaseType != typeof(object) && !type.IsInterface &&
                 type.BaseType != typeof(ValueType) && type.BaseType != typeof(MulticastDelegate))
            output.Append(" : ").Append(TypeName(type.BaseType));

        Type[] interfaces = type.GetInterfaces();
        if (interfaces.Length > 0)
        {
            output.Append(" implements ")
                .AppendJoin(',', interfaces.Select(TypeName).Order(StringComparer.Ordinal));
        }

        AppendGenericParameters(output, type.GetGenericArguments());
        AppendLayout(output, type);
        AppendContractAttributes(output, type.GetCustomAttributesData());
        output.Append('\n');
    }

    private static void AppendConstructors(StringBuilder output, Type type)
    {
        foreach (string line in type.GetConstructors(DeclaredMembers)
                     .Where(IsExternallyVisible)
                     .Select(constructor => ConstructorLine(type, constructor))
                     .Order(StringComparer.Ordinal))
            output.Append(line);
    }

    private static string ConstructorLine(Type type, ConstructorInfo constructor)
    {
        var line = new StringBuilder("  ctor ")
            .Append(MemberVisibility(constructor)).Append(' ')
            .Append(TypeName(type)).Append('(')
            .AppendJoin(',', constructor.GetParameters().Select(ParameterSignature))
            .Append(')');
        AppendContractAttributes(line, constructor.GetCustomAttributesData());
        return line.Append('\n').ToString();
    }

    private static void AppendMethods(StringBuilder output, Type type)
    {
        foreach (string line in type.GetMethods(DeclaredMembers)
                     .Where(IsExternallyVisible)
                     .Where(static method =>
                         !method.IsSpecialName || method.Name.StartsWith("op_", StringComparison.Ordinal))
                     .Select(MethodLine)
                     .Order(StringComparer.Ordinal))
            output.Append(line);
    }

    private static string MethodLine(MethodInfo method)
    {
        var line = new StringBuilder("  method ")
            .Append(MemberVisibility(method)).Append(' ')
            .Append(MethodModifiers(method))
            .Append(ReturnSignature(method.ReturnParameter, method.ReturnType)).Append(' ')
            .Append(method.Name);

        if (method.IsGenericMethodDefinition)
        {
            Type[] arguments = method.GetGenericArguments();
            line.Append('<').AppendJoin(',', arguments.Select(GenericParameterDeclaration)).Append('>');
            AppendGenericParameters(line, arguments);
        }

        line.Append('(')
            .AppendJoin(',', method.GetParameters().Select(ParameterSignature))
            .Append(')');
        AppendContractAttributes(line, method.GetCustomAttributesData());
        AppendContractAttributes(line, method.ReturnParameter.GetCustomAttributesData(), " return-attrs");
        return line.Append('\n').ToString();
    }

    private static void AppendProperties(StringBuilder output, Type type)
    {
        foreach (string line in type.GetProperties(DeclaredMembers)
                     .Where(IsExternallyVisible)
                     .Select(PropertyLine)
                     .Order(StringComparer.Ordinal))
            output.Append(line);
    }

    private static string PropertyLine(PropertyInfo property)
    {
        MethodInfo? getter = property.GetGetMethod(nonPublic: true);
        MethodInfo? setter = property.GetSetMethod(nonPublic: true);
        string propertyType = property.PropertyType.IsByRef && getter is not null
            ? ReturnSignature(getter.ReturnParameter, property.PropertyType, includeNullability: false)
            : TypeName(property.PropertyType) + CustomModifiers(property);

        var line = new StringBuilder("  property ");
        if (HasAttribute(property.GetCustomAttributesData(), typeof(RequiredMemberAttribute).FullName!))
            line.Append("required ");
        line.Append(propertyType)
            .Append(NullabilitySignature(property.PropertyType, TryNullability(property)))
            .Append(' ').Append(property.Name);

        ParameterInfo[] indices = property.GetIndexParameters();
        if (indices.Length > 0)
            line.Append('[').AppendJoin(',', indices.Select(ParameterSignature)).Append(']');

        line.Append(" { ");
        AppendAccessor(line, getter, "get");
        AppendAccessor(line, setter, setter is not null && IsInitOnly(setter) ? "init" : "set");
        line.Append('}');
        AppendContractAttributes(line, property.GetCustomAttributesData());
        return line.Append('\n').ToString();
    }

    private static void AppendEvents(StringBuilder output, Type type)
    {
        foreach (string line in type.GetEvents(DeclaredMembers)
                     .Where(IsExternallyVisible)
                     .Select(EventLine)
                     .Order(StringComparer.Ordinal))
            output.Append(line);
    }

    private static string EventLine(EventInfo @event)
    {
        Type eventType = @event.EventHandlerType!;
        var line = new StringBuilder("  event ")
            .Append(TypeName(eventType))
            .Append(NullabilitySignature(eventType, TryNullability(@event)))
            .Append(' ').Append(@event.Name).Append(" { ");
        AppendAccessor(line, @event.GetAddMethod(nonPublic: true), "add");
        AppendAccessor(line, @event.GetRemoveMethod(nonPublic: true), "remove");
        AppendAccessor(line, @event.GetRaiseMethod(nonPublic: true), "raise");
        line.Append('}');
        AppendContractAttributes(line, @event.GetCustomAttributesData());
        return line.Append('\n').ToString();
    }

    private static void AppendAccessor(StringBuilder output, MethodInfo? accessor, string kind)
    {
        if (!IsExternallyVisibleAccessor(accessor))
            return;

        output.Append(MemberVisibility(accessor!)).Append(' ')
            .Append(MethodModifiers(accessor!)).Append(kind).Append('(')
            .AppendJoin(',', accessor!.GetParameters().Select(ParameterSignature))
            .Append(")->").Append(ReturnSignature(accessor.ReturnParameter, accessor.ReturnType));
        AppendContractAttributes(output, accessor.GetCustomAttributesData());
        AppendContractAttributes(output, accessor.ReturnParameter.GetCustomAttributesData(), " return-attrs");
        output.Append("; ");
    }

    private static void AppendFields(StringBuilder output, Type type)
    {
        foreach (string line in type.GetFields(DeclaredMembers)
                     .Where(IsExternallyVisible)
                     .Select(FieldLine)
                     .Order(StringComparer.Ordinal))
            output.Append(line);
    }

    private static string FieldLine(FieldInfo field)
    {
        var line = new StringBuilder("  field ").Append(MemberVisibility(field)).Append(' ');
        if (field.IsLiteral)
            line.Append("const ");
        else if (field.IsStatic)
            line.Append("static ");
        if (field.IsInitOnly)
            line.Append("readonly ");
        if (HasAttribute(field.GetCustomAttributesData(), typeof(RequiredMemberAttribute).FullName!))
            line.Append("required ");
        line.Append(TypeName(field.FieldType))
            .Append(CustomModifiers(field))
            .Append(NullabilitySignature(field.FieldType, TryNullability(field)))
            .Append(' ').Append(field.Name);
        if (field.IsLiteral)
            line.Append(" = ").Append(DefaultValue(field.GetRawConstantValue()));
        AppendContractAttributes(line, field.GetCustomAttributesData());
        return line.Append('\n').ToString();
    }

    private static bool IsExternallyVisible(Type type)
    {
        bool ownVisibility = type.IsPublic || type.IsNestedPublic || type.IsNestedFamily ||
            type.IsNestedFamORAssem;
        return ownVisibility && (type.DeclaringType is null || IsExternallyVisible(type.DeclaringType));
    }

    private static bool IsExternallyVisible(MethodBase method) =>
        method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static bool IsExternallyVisible(FieldInfo field) =>
        field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly;

    private static bool IsExternallyVisible(PropertyInfo property) =>
        IsExternallyVisibleAccessor(property.GetGetMethod(nonPublic: true)) ||
        IsExternallyVisibleAccessor(property.GetSetMethod(nonPublic: true));

    private static bool IsExternallyVisible(EventInfo @event) =>
        IsExternallyVisibleAccessor(@event.GetAddMethod(nonPublic: true)) ||
        IsExternallyVisibleAccessor(@event.GetRemoveMethod(nonPublic: true)) ||
        IsExternallyVisibleAccessor(@event.GetRaiseMethod(nonPublic: true));

    private static bool IsExternallyVisibleAccessor(MethodBase? method) =>
        method is not null && IsExternallyVisible(method);

    private static string TypeVisibility(Type type) =>
        type.IsPublic || type.IsNestedPublic ? "public" :
        type.IsNestedFamily ? "protected" :
        type.IsNestedFamORAssem ? "protected internal" :
        throw new InvalidOperationException($"Type '{type}' is not externally visible.");

    private static string MemberVisibility(MethodBase method) =>
        method.IsPublic ? "public" :
        method.IsFamily ? "protected" :
        method.IsFamilyOrAssembly ? "protected internal" :
        throw new InvalidOperationException($"Method '{method}' is not externally visible.");

    private static string MemberVisibility(FieldInfo field) =>
        field.IsPublic ? "public" :
        field.IsFamily ? "protected" :
        field.IsFamilyOrAssembly ? "protected internal" :
        throw new InvalidOperationException($"Field '{field}' is not externally visible.");

    private static void AppendGenericParameters(StringBuilder output, IEnumerable<Type> arguments)
    {
        foreach (Type argument in arguments.Where(static argument => argument.IsGenericParameter))
        {
            GenericParameterAttributes attributes = argument.GenericParameterAttributes;
            var constraints = new List<string>();
            if (HasAttribute(argument.GetCustomAttributesData(),
                    "System.Runtime.CompilerServices.IsUnmanagedAttribute"))
                constraints.Add("unmanaged");
            else if ((attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
                constraints.Add("struct");
            if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
                constraints.Add("class");
            if ((attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0 &&
                !constraints.Contains("struct", StringComparer.Ordinal) &&
                !constraints.Contains("unmanaged", StringComparer.Ordinal))
                constraints.Add("new()");
            constraints.AddRange(argument.GetGenericParameterConstraints()
                .Where(static constraint => constraint != typeof(ValueType))
                .Select(TypeName)
                .Order(StringComparer.Ordinal));
            if ((attributes & GenericParameterAttributes.AllowByRefLike) != 0)
                constraints.Add("allows ref struct");

            output.Append(" generic[")
                .Append(argument.GenericParameterPosition.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(GenericParameterDeclaration(argument));
            if (constraints.Count > 0)
                output.Append(" where ").AppendJoin('&', constraints);
            AppendContractAttributes(output, argument.GetCustomAttributesData());
            output.Append(']');
        }
    }

    private static string GenericParameterDeclaration(Type argument)
    {
        GenericParameterAttributes variance =
            argument.GenericParameterAttributes & GenericParameterAttributes.VarianceMask;
        string prefix = variance switch
        {
            GenericParameterAttributes.Covariant => "out ",
            GenericParameterAttributes.Contravariant => "in ",
            _ => string.Empty,
        };
        return prefix + argument.Name;
    }

    private static void AppendLayout(StringBuilder output, Type type)
    {
        if (!type.IsValueType && !type.IsExplicitLayout && !type.IsLayoutSequential)
            return;

        StructLayoutAttribute? layout = type.StructLayoutAttribute;
        if (layout is null)
            return;

        output.Append(" layout[")
            .Append(layout.Value).Append(",pack=")
            .Append(layout.Pack.ToString(CultureInfo.InvariantCulture)).Append(",size=")
            .Append(layout.Size.ToString(CultureInfo.InvariantCulture)).Append(",charset=")
            .Append(layout.CharSet);

        InlineArrayAttribute? inlineArray = type.GetCustomAttribute<InlineArrayAttribute>();
        if (inlineArray is not null)
            output.Append(",inline-array=")
                .Append(inlineArray.Length.ToString(CultureInfo.InvariantCulture));
        output.Append(']');
    }

    private static void AppendLayoutFields(StringBuilder output, Type type)
    {
        if (!type.IsValueType && !type.IsExplicitLayout && !type.IsLayoutSequential)
            return;

        FieldInfo[] fields = type.GetFields(DeclaredInstanceFields)
            .Where(static field => !field.IsStatic)
            .OrderBy(static field => field.MetadataToken)
            .ToArray();
        for (int index = 0; index < fields.Length; index++)
        {
            FieldInfo field = fields[index];
            output.Append("  layout-field #")
                .Append(index.ToString(CultureInfo.InvariantCulture))
                .Append(" offset=").Append(LayoutOffset(type, field))
                .Append(" type=").Append(TypeName(field.FieldType))
                .Append(CustomModifiers(field));

            FixedBufferAttribute? fixedBuffer = field.GetCustomAttribute<FixedBufferAttribute>();
            if (fixedBuffer is not null)
            {
                output.Append(" fixed[element=").Append(TypeName(fixedBuffer.ElementType))
                    .Append(",length=")
                    .Append(fixedBuffer.Length.ToString(CultureInfo.InvariantCulture))
                    .Append(']');
            }

            if (IsExternallyVisible(field))
                output.Append(" member=").Append(MemberVisibility(field)).Append(':').Append(field.Name);
            else
                output.Append(" member=<nonpublic>");
            AppendContractAttributes(output, field.GetCustomAttributesData());
            output.Append('\n');
        }
    }

    private static string LayoutOffset(Type declaringType, FieldInfo field)
    {
        FieldOffsetAttribute? explicitOffset = field.GetCustomAttribute<FieldOffsetAttribute>();
        if (explicitOffset is not null)
            return explicitOffset.Value.ToString(CultureInfo.InvariantCulture);
        if (declaringType.ContainsGenericParameters || declaringType.IsByRefLike)
            return "unavailable";

        try
        {
            return Marshal.OffsetOf(declaringType, field.Name).ToInt64()
                .ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is ArgumentException or TypeLoadException or
                                           NotSupportedException or MarshalDirectiveException)
        {
            return "unavailable";
        }
    }

    private static string TypeKind(Type type) =>
        type.IsInterface ? "interface" :
        type.IsEnum ? "enum" :
        typeof(MulticastDelegate).IsAssignableFrom(type.BaseType) ? "delegate" :
        type.IsValueType ? "struct" :
        "class";

    private static string TypeModifiers(Type type)
    {
        var modifiers = new List<string>();
        if (type.IsAbstract && type.IsSealed)
            modifiers.Add("static");
        else
        {
            if (type.IsAbstract && !type.IsInterface)
                modifiers.Add("abstract");
            if (type.IsSealed && !type.IsValueType)
                modifiers.Add("sealed");
        }
        if (type.IsValueType && HasAttribute(type.GetCustomAttributesData(),
                typeof(IsReadOnlyAttribute).FullName!))
            modifiers.Add("readonly");
        if (type.IsByRefLike)
            modifiers.Add("ref");
        return modifiers.Count == 0 ? string.Empty : string.Join(' ', modifiers) + ' ';
    }

    private static string MethodModifiers(MethodInfo method)
    {
        var modifiers = new List<string>();
        if (method.IsStatic)
            modifiers.Add("static");
        if (method.IsAbstract)
            modifiers.Add("abstract");
        else if (method.IsVirtual && method.GetBaseDefinition() == method && !method.IsFinal)
            modifiers.Add("virtual");
        else if (method.IsVirtual && method.GetBaseDefinition() != method && !method.IsFinal)
            modifiers.Add("override");
        if (method.IsFinal && method.IsVirtual)
            modifiers.Add("sealed");
        return modifiers.Count == 0 ? string.Empty : string.Join(' ', modifiers) + ' ';
    }

    private static string ReturnSignature(
        ParameterInfo returnParameter,
        Type returnType,
        bool includeNullability = true)
    {
        if (!returnType.IsByRef)
            return TypeName(returnType) + CustomModifiers(returnParameter) +
                (includeNullability
                    ? NullabilitySignature(returnType, TryNullability(returnParameter))
                    : string.Empty);

        bool readOnly = returnParameter.IsIn ||
            HasAttribute(returnParameter.GetCustomAttributesData(), typeof(IsReadOnlyAttribute).FullName!) ||
            HasCustomModifier(returnParameter, typeof(IsReadOnlyAttribute)) ||
            HasCustomModifier(returnParameter, typeof(InAttribute));
        return (readOnly ? "ref readonly " : "ref ") +
            TypeName(returnType.GetElementType()!) + CustomModifiers(returnParameter) +
            (includeNullability
                ? NullabilitySignature(returnType.GetElementType()!, TryNullability(returnParameter))
                : string.Empty);
    }

    private static string ParameterSignature(ParameterInfo parameter)
    {
        string scoped = HasAttribute(parameter.GetCustomAttributesData(),
            "System.Runtime.CompilerServices.ScopedRefAttribute") ? "scoped " : string.Empty;
        string modifier = ParameterModifier(parameter);
        Type type = parameter.ParameterType.IsByRef
            ? parameter.ParameterType.GetElementType()!
            : parameter.ParameterType;
        string optional = parameter.HasDefaultValue
            ? "=" + DefaultValue(parameter.DefaultValue)
            : string.Empty;
        if (parameter.IsOptional)
            optional += " optional";
        string attributes = ContractAttributes(parameter.GetCustomAttributesData());
        return scoped + modifier + TypeName(type) + CustomModifiers(parameter) + " " +
            parameter.Name + NullabilitySignature(type, TryNullability(parameter)) + optional + attributes;
    }

    private static string ParameterModifier(ParameterInfo parameter)
    {
        if (parameter.GetCustomAttribute<ParamArrayAttribute>() is not null)
            return "params ";
        if (parameter.IsOut)
            return "out ";
        if (!parameter.ParameterType.IsByRef)
            return string.Empty;
        if (HasAttribute(parameter.GetCustomAttributesData(),
                "System.Runtime.CompilerServices.RequiresLocationAttribute") ||
            HasCustomModifier(parameter, "System.Runtime.CompilerServices.RequiresLocationAttribute"))
            return "ref readonly ";
        if (parameter.IsIn || HasCustomModifier(parameter, typeof(InAttribute)) ||
            HasCustomModifier(parameter, typeof(IsReadOnlyAttribute)))
            return "in ";
        return "ref ";
    }

    private static string TypeName(Type type)
    {
        if (type.IsByRef)
            return TypeName(type.GetElementType()!) + "&";
        if (type.IsPointer)
            return TypeName(type.GetElementType()!) + "*";
        if (type.IsFunctionPointer)
        {
            string conventions = string.Join(',', type.GetFunctionPointerCallingConventions()
                .Select(TypeName).Order(StringComparer.Ordinal));
            string parameters = string.Join(',', type.GetFunctionPointerParameterTypes().Select(TypeName));
            return "fnptr[" + conventions + "](" + parameters + ")->" +
                TypeName(type.GetFunctionPointerReturnType());
        }
        if (type.IsArray)
            return TypeName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (type.IsGenericParameter)
            return (type.DeclaringMethod is null ? "!" : "!!") +
                type.GenericParameterPosition.ToString(CultureInfo.InvariantCulture) + ":" + type.Name;
        if (!type.IsGenericType)
            return type.FullName ?? type.Name;

        Type definition = type.GetGenericTypeDefinition();
        string name = definition.FullName ?? definition.Name;
        return name + "<" + string.Join(',', type.GetGenericArguments().Select(TypeName)) + ">";
    }

    private static string CustomModifiers(ParameterInfo parameter) =>
        CustomModifiers(parameter.GetRequiredCustomModifiers(), parameter.GetOptionalCustomModifiers());

    private static string CustomModifiers(FieldInfo field) =>
        CustomModifiers(field.GetRequiredCustomModifiers(), field.GetOptionalCustomModifiers());

    private static string CustomModifiers(PropertyInfo property) =>
        CustomModifiers(property.GetRequiredCustomModifiers(), property.GetOptionalCustomModifiers());

    private static string CustomModifiers(Type[] required, Type[] optional)
    {
        if (required.Length == 0 && optional.Length == 0)
            return string.Empty;
        return " modifiers[required=" +
            string.Join(',', required.Select(TypeName)) +
            ";optional=" +
            string.Join(',', optional.Select(TypeName)) + "]";
    }

    private static bool HasCustomModifier(ParameterInfo parameter, Type modifier) =>
        parameter.GetRequiredCustomModifiers().Contains(modifier) ||
        parameter.GetOptionalCustomModifiers().Contains(modifier);

    private static bool HasCustomModifier(ParameterInfo parameter, string modifierName) =>
        parameter.GetRequiredCustomModifiers().Concat(parameter.GetOptionalCustomModifiers())
            .Any(modifier => string.Equals(modifier.FullName, modifierName, StringComparison.Ordinal));

    private static NullabilityInfo? TryNullability(ParameterInfo parameter)
    {
        try
        {
            return _nullability.Create(parameter);
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                           NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static NullabilityInfo? TryNullability(FieldInfo field)
    {
        try
        {
            return _nullability.Create(field);
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                           NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static NullabilityInfo? TryNullability(PropertyInfo property)
    {
        try
        {
            return _nullability.Create(property);
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                           NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static NullabilityInfo? TryNullability(EventInfo @event)
    {
        try
        {
            return _nullability.Create(@event);
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                           NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static string NullabilitySignature(Type type, NullabilityInfo? info)
    {
        if (!HasNullableShape(type))
            return string.Empty;
        return " nullability[read=" + NullabilityType(type, info, write: false) +
            ";write=" + NullabilityType(type, info, write: true) + "]";
    }

    private static bool HasNullableShape(Type type)
    {
        if (type.IsByRef || type.IsPointer || type.IsArray)
            return HasNullableShape(type.GetElementType()!) || type.IsArray;
        if (!type.IsValueType || type.IsGenericParameter)
            return true;
        return type.IsGenericType && type.GetGenericArguments().Any(HasNullableShape);
    }

    private static string NullabilityType(Type type, NullabilityInfo? info, bool write)
    {
        if (type.IsByRef || type.IsPointer)
            return NullabilityType(type.GetElementType()!, MatchingElementInfo(type, info), write) +
                (type.IsPointer ? "*" : "&");
        if (type.IsArray)
        {
            return NullabilityType(type.GetElementType()!, info?.ElementType, write) + "[" +
                new string(',', type.GetArrayRank() - 1) + "]" + NullabilityState(type, info, write);
        }
        if (type.IsFunctionPointer)
            return TypeName(type);
        if (type.IsGenericParameter)
            return TypeName(type) + NullabilityState(type, info, write);
        if (!type.IsGenericType)
            return TypeName(type) + NullabilityState(type, info, write);

        Type definition = type.GetGenericTypeDefinition();
        string name = definition.FullName ?? definition.Name;
        Type[] arguments = type.GetGenericArguments();
        NullabilityInfo[] nullabilityArguments = info?.GenericTypeArguments ?? [];
        string renderedArguments = string.Join(',', arguments.Select((argument, index) =>
            NullabilityType(argument,
                index < nullabilityArguments.Length ? nullabilityArguments[index] : null,
                write)));
        return name + "<" + renderedArguments + ">" + NullabilityState(type, info, write);
    }

    private static NullabilityInfo? MatchingElementInfo(Type type, NullabilityInfo? info) =>
        info?.Type == type && info.ElementType is not null ? info.ElementType : info;

    private static string NullabilityState(Type type, NullabilityInfo? info, bool write)
    {
        if (type.IsValueType && !type.IsGenericParameter)
            return string.Empty;
        NullabilityState state = info is null
            ? System.Reflection.NullabilityState.Unknown
            : write ? info.WriteState : info.ReadState;
        return state switch
        {
            System.Reflection.NullabilityState.Nullable => "{nullable}",
            System.Reflection.NullabilityState.NotNull => "{notnull}",
            _ => "{oblivious}",
        };
    }

    private static void AppendContractAttributes(
        StringBuilder output,
        IList<CustomAttributeData> attributes,
        string prefix = " attrs")
    {
        string value = ContractAttributes(attributes);
        if (value.Length > 0)
            output.Append(prefix).Append(value);
    }

    private static string ContractAttributes(IList<CustomAttributeData> attributes)
    {
        string[] values = attributes
            .Where(static attribute => IsContractAttribute(attribute.AttributeType))
            .Select(AttributeSignature)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return values.Length == 0 ? string.Empty : "[" + string.Join(',', values) + "]";
    }

    private static bool IsContractAttribute(Type attributeType)
    {
        string name = attributeType.FullName ?? attributeType.Name;
        return name is
            "System.AttributeUsageAttribute" or
            "System.ObsoleteAttribute" or
            "System.CLSCompliantAttribute" or
            "System.FlagsAttribute" or
            "System.Diagnostics.ConditionalAttribute" or
            "System.ParamArrayAttribute" or
            "System.ComponentModel.DefaultValueAttribute" or
            "System.ComponentModel.EditorBrowsableAttribute" or
            "System.Runtime.InteropServices.FieldOffsetAttribute" or
            "System.Runtime.InteropServices.InAttribute" or
            "System.Runtime.InteropServices.MarshalAsAttribute" or
            "System.Runtime.InteropServices.OptionalAttribute" or
            "System.Runtime.InteropServices.OutAttribute" or
            "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute" or
            "System.Runtime.CompilerServices.CallerArgumentExpressionAttribute" or
            "System.Runtime.CompilerServices.CallerFilePathAttribute" or
            "System.Runtime.CompilerServices.CallerLineNumberAttribute" or
            "System.Runtime.CompilerServices.CallerMemberNameAttribute" or
            "System.Runtime.CompilerServices.AsyncMethodBuilderAttribute" or
            "System.Runtime.CompilerServices.CollectionBuilderAttribute" or
            "System.Runtime.CompilerServices.CompilerFeatureRequiredAttribute" or
            "System.Runtime.CompilerServices.DateTimeConstantAttribute" or
            "System.Runtime.CompilerServices.DecimalConstantAttribute" or
            "System.Runtime.CompilerServices.DefaultInterpolatedStringHandlerAttribute" or
            "System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute" or
            "System.Runtime.CompilerServices.DynamicAttribute" or
            "System.Runtime.CompilerServices.ExtensionAttribute" or
            "System.Runtime.CompilerServices.FixedBufferAttribute" or
            "System.Runtime.CompilerServices.InlineArrayAttribute" or
            "System.Runtime.CompilerServices.InterpolatedStringHandlerAttribute" or
            "System.Runtime.CompilerServices.InterpolatedStringHandlerArgumentAttribute" or
            "System.Runtime.CompilerServices.IsByRefLikeAttribute" or
            "System.Runtime.CompilerServices.IsReadOnlyAttribute" or
            "System.Runtime.CompilerServices.IsUnmanagedAttribute" or
            "System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute" or
            "System.Runtime.CompilerServices.RefSafetyRulesAttribute" or
            "System.Runtime.CompilerServices.RequiredMemberAttribute" or
            "System.Runtime.CompilerServices.ScopedRefAttribute" or
            "System.Runtime.CompilerServices.SkipLocalsInitAttribute" or
            "System.Runtime.CompilerServices.TupleElementNamesAttribute" or
            "System.Reflection.DefaultMemberAttribute" or
            "System.Security.SuppressUnmanagedCodeSecurityAttribute" or
            "System.Security.UnverifiableCodeAttribute" or
            "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute" ||
            name.StartsWith("System.Diagnostics.CodeAnalysis.", StringComparison.Ordinal) ||
            name.StartsWith("System.Runtime.InteropServices.", StringComparison.Ordinal) ||
            name.StartsWith("System.Runtime.Serialization.", StringComparison.Ordinal) ||
            name.StartsWith("System.Runtime.Versioning.", StringComparison.Ordinal);
    }

    private static string AttributeSignature(CustomAttributeData attribute)
    {
        string constructor = string.Join(',', attribute.Constructor.GetParameters()
            .Select(static parameter => TypeName(parameter.ParameterType)));
        string positional = string.Join(',', attribute.ConstructorArguments
            .Select((argument, index) => "#" + index.ToString(CultureInfo.InvariantCulture) + "=" +
                AttributeValue(argument)));
        string named = string.Join(',', attribute.NamedArguments
            .OrderBy(static argument => argument.IsField ? 0 : 1)
            .ThenBy(static argument => argument.MemberName, StringComparer.Ordinal)
            .Select(static argument => (argument.IsField ? "field:" : "property:") +
                argument.MemberName + "=" + AttributeValue(argument.TypedValue)));
        return TypeName(attribute.AttributeType) + "(ctor[" + constructor + "]" +
            ";args[" + positional + "];named[" + named + "])";
    }

    private static string AttributeValue(CustomAttributeTypedArgument argument)
    {
        string type = TypeName(argument.ArgumentType);
        if (argument.Value is IEnumerable<CustomAttributeTypedArgument> values)
            return type + ":[" + string.Join(',', values.Select(AttributeValue)) + "]";
        if (argument.Value is Type valueType)
            return type + ":typeof(" + TypeName(valueType) + ")";
        if (argument.ArgumentType.IsEnum && argument.Value is not null)
            return type + ":" +
                Convert.ToString(argument.Value, CultureInfo.InvariantCulture);
        return type + ":" + DefaultValue(argument.Value);
    }

    private static bool HasAttribute(IList<CustomAttributeData> attributes, string fullName) =>
        attributes.Any(attribute => string.Equals(
            attribute.AttributeType.FullName,
            fullName,
            StringComparison.Ordinal));

    private static string DefaultValue(object? value) => value switch
    {
        null => "null",
        string text => "\"" + EscapeLiteral(text, '"') + "\"",
        char character => "'" + EscapeLiteral(character.ToString(), '\'') + "'",
        bool boolean => boolean ? "true" : "false",
        Missing => "missing",
        DBNull => "dbnull",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string EscapeLiteral(string value, char quote)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            escaped.Append(character switch
            {
                '\0' => "\\0",
                '\a' => "\\a",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\v' => "\\v",
                '\\' => "\\\\",
                _ when character == quote => "\\" + quote,
                _ when char.IsControl(character) || character is '\u2028' or '\u2029' =>
                    "\\u" + ((int)character).ToString("X4", CultureInfo.InvariantCulture),
                _ => character.ToString(),
            });
        }
        return escaped.ToString();
    }

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers()
            .Contains(typeof(IsExternalInit));
}
