using System.Reflection;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class DevelopmentValidationTests
{
    [Fact]
    public void CommandResourceLookupContainsValidationCallsOnlyInDevelopmentIl()
    {
        Type executor = typeof(RenderGraph).Assembly.GetType(
            "SomeEngine.RenderGraph.FrameExecutor",
            throwOnError: true)!;
        MethodInfo getBuffer = executor.GetMethod(
            "GetBuffer",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo ensureDeclared = executor.GetMethod(
            "EnsurePassUsesResource",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo require = typeof(DevelopmentCheck).GetMethod(
            nameof(DevelopmentCheck.Require),
            [typeof(bool), typeof(string)])!;

#if DEBUG
        Assert.True(Calls(getBuffer, ensureDeclared));
        Assert.True(Calls(getBuffer, require));
#else
        Assert.False(Calls(getBuffer, ensureDeclared));
        Assert.False(Calls(getBuffer, require));
#endif
    }

    [Fact]
    public void CommandBindingValidationCallIsAbsentFromShippingIl()
    {
        Type executor = typeof(RenderGraph).Assembly.GetType(
            "SomeEngine.RenderGraph.FrameExecutor",
            throwOnError: true)!;
        MethodInfo validateBindings = executor.GetMethod(
            "ValidateBindings",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo setBindings = typeof(RasterPassCommandScope).GetMethod(
            nameof(RasterPassCommandScope.SetTransientParameterBindings))!;

#if DEBUG
        Assert.True(Calls(setBindings, validateBindings));
#else
        Assert.False(Calls(setBindings, validateBindings));
#endif
    }

    private static bool Calls(MethodInfo caller, MethodInfo target)
    {
        byte[] bytes = caller.GetMethodBody()?.GetILAsByteArray() ??
            throw new InvalidOperationException($"{caller} has no method body.");
        for (int offset = 0; offset <= bytes.Length - 5; offset++)
        {
            if (bytes[offset] is not 0x28 and not 0x6F)
                continue;
            int token = BitConverter.ToInt32(bytes, offset + 1);
            MethodBase? resolved;
            try
            {
                resolved = caller.Module.ResolveMethod(token);
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (resolved?.Module == target.Module && resolved.MetadataToken == target.MetadataToken)
                return true;
        }
        return false;
    }
}
