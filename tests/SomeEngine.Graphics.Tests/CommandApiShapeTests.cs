using System.Reflection;
using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class CommandApiShapeTests
{
    [Theory]
    [InlineData(nameof(IGraphicsBackend.Draw), typeof(DrawArguments))]
    [InlineData(nameof(IGraphicsBackend.DrawIndexed), typeof(DrawIndexedArguments))]
    [InlineData(nameof(IGraphicsBackend.Dispatch), typeof(DispatchArguments))]
    public void CommandReceiverExposesTheApplicationCallGranularity(
        string methodName,
        Type argumentType)
    {
        MethodInfo[] candidates = typeof(IGraphicsBackend)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
            .ToArray();

        MethodInfo method = Assert.Single(candidates);
        ParameterInfo[] parameters = method.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(CommandContext), parameters[0].ParameterType);
        Assert.True(parameters[1].ParameterType.IsByRef);
        Assert.Equal(argumentType, parameters[1].ParameterType.GetElementType());
        Assert.True(parameters[1].IsIn);
    }
}
