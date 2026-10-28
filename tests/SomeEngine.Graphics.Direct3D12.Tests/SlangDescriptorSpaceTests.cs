using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class SlangDescriptorSpaceTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void AComputeResourceUsesItsDeclaredRegisterSpace()
    {
        uint[][] output = new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            RWStructuredBuffer<uint> Output : register(u3, space2);
            [shader("compute")] [numthreads(1,1,1)]
            void WriteValue() { Output[0] = 17; }
            """, "WriteValue", new(1, 1, 1), [([0u], ResourceBindingType.BufferUav, 4u)]);
        Assert.Equal(17u, output[0][0]);
    }
}
