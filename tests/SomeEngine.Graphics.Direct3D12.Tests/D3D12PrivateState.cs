using System.Reflection;
using System.Runtime.ExceptionServices;
using SomeEngine.Graphics.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12.Tests;

/// <summary>
/// Test-owned inspection of private D3D12 state. Product assemblies intentionally expose no
/// failure-injection callbacks, retirement snapshots, or test-only counters.
/// </summary>
internal static unsafe partial class D3D12PrivateState
{
    private const BindingFlags InstanceFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags =
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    internal static object InvokeStatic(string name, params object?[] arguments) =>
        InvokeMethod(typeof(D3D12Backend), null, name, StaticFlags, arguments);

    internal static object? Invoke(object value, string name, params object?[] arguments) =>
        InvokeMethod(value.GetType(), value, name, InstanceFlags, arguments);

    internal static FieldInfo GetField(object value, string name) =>
        GetField(value.GetType(), name);

    internal static FieldInfo GetField(Type type, string name)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            FieldInfo? field = current.GetField(name, InstanceFlags | StaticFlags);
            if (field is not null)
                return field;
        }
        throw new MissingFieldException(type.FullName, name);
    }

    internal static PropertyInfo GetProperty(object value, string name)
    {
        for (Type? current = value.GetType(); current is not null; current = current.BaseType)
        {
            PropertyInfo? property = current.GetProperty(name, InstanceFlags);
            if (property is not null)
                return property;
        }
        throw new MissingMemberException(value.GetType().FullName, name);
    }

    private static object InvokeMethod(
        Type type,
        object? receiver,
        string name,
        BindingFlags flags,
        object?[] arguments)
    {
        MethodInfo method = type.GetMethods(flags)
            .Single(candidate =>
                candidate.Name == name &&
                candidate.GetParameters().Length == arguments.Length);
        try
        {
            return method.Invoke(receiver, arguments)!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static FieldInfo? TryGetField(Type type, string name)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            FieldInfo? field = current.GetField(name, InstanceFlags);
            if (field is not null)
                return field;
        }
        return null;
    }

    private static unsafe bool PointerPropertyIsNonZero(object value, string name)
    {
        object? pointer = GetProperty(value, name).GetValue(value);
        return pointer is Pointer boxed && Pointer.Unbox(boxed) is not null;
    }

}
