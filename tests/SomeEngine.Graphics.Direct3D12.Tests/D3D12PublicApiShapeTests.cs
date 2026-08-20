using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class D3D12PublicApiShapeTests
{
    [Fact]
    public void Public_creation_surface_returns_only_the_graphics_backend_contract()
    {
        Assert.True(typeof(D3D12Backend).IsNotPublic);
        Assert.DoesNotContain(
            typeof(D3D12GraphicsBackend).Assembly.GetExportedTypes(),
            static type => type.Name == nameof(D3D12Backend));

        System.Reflection.MethodInfo create =
            typeof(D3D12GraphicsBackend).GetMethod(nameof(D3D12GraphicsBackend.Create))
            ?? throw new InvalidOperationException("The D3D12 backend factory is missing.");
        Assert.Equal(typeof(IGraphicsBackend), create.ReturnType);
    }
}
