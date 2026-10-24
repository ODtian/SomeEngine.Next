using System.Numerics;
using System.Runtime.InteropServices;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Xunit.Collection("Direct3D12 shader compilation")]
public sealed class ClusteredLightingTests(D3D12TestCompiler shaderCompiler)
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(3, false)]
    [InlineData(80, true)]
    [InlineData(80, false)]
    public void Clear_and_group_assignment_preserve_view_boundaries_and_bounded_indices(int lightCount, bool large)
    {
        const int cells = 8 * 4 * 4, capacity = 16, views = 2, directional = 2;
        uint sliceGroups = (uint)(4 + Math.Max(lightCount, 1) - 1) / (uint)Math.Max(lightCount, 1);
        const uint untouched = 0xdeadbeef;
        var parameters = new uint[48 * views];
        for (int view = 0; view < views; view++)
        {
            Span<uint> row = parameters.AsSpan(view * 48, 48);
            Matrix4x4 matrix = view == 0 ? Matrix4x4.Identity : Matrix4x4.CreateTranslation(0, 0, -1000);
            MemoryMarshal.Write(MemoryMarshal.AsBytes(row), in matrix);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, 1, 16);
            MemoryMarshal.Write(MemoryMarshal.AsBytes(row[16..]), in projection);
            row[32] = 16; row[33] = 16; row[34] = 8; row[35] = 4;
            row[36] = Float(1); row[37] = Float(16); row[38] = Float(1); row[39] = 0;
            row[40] = 4; row[41] = capacity; row[42] = directional; row[43] = (uint)lightCount;
            row[44] = 128; row[45] = 64; row[46] = cells;
            row[47] = sliceGroups;
        }
        var lights = new uint[Math.Max(directional + lightCount, 1) * 36];
        for (int index = directional; index < directional + lightCount; index++)
        {
            Span<uint> row = lights.AsSpan(index * 36, 36);
            row[0] = Float(large ? 0 : 0.375f); row[1] = Float(large ? 0 : 0.375f);
            row[2] = Float(large ? -8 : -3); row[3] = Float(large ? 32 : 0.01f);
        }
        uint[] grid = Enumerable.Repeat(untouched, cells * views * 2).ToArray();
        uint[] indices = Enumerable.Repeat(untouched, cells * views * capacity).ToArray();
        uint[][] cleared = Dispatch("ClearClusteredLightGrid", new(cells / 64, views, 1), grid, indices);
        Assert.All(cleared[0], value => Assert.Equal(0u, value));
        Assert.All(cleared[1], value => Assert.Equal(untouched, value));
        uint[][] assigned = lightCount == 0 ? cleared : Dispatch("BuildClusteredLightGrid",
            new((uint)lightCount, views, sliceGroups), cleared[0], cleared[1]);
        for (int cell = 0; cell < cells * views; cell++)
        {
            // Small spheres lie strictly within tile (4, 1), depth slice 1.
            bool covered = cell < cells && (large || cell == (1 * 4 + 1) * 8 + 4);
            uint count = covered ? (uint)lightCount : 0;
            Assert.Equal(0u, assigned[0][cell * 2]);
            Assert.Equal(count, assigned[0][cell * 2 + 1]);
            int valid = Math.Min((int)count, capacity);
            uint[] prefix = assigned[1].AsSpan(cell * capacity, valid).ToArray();
            Assert.Equal(valid, prefix.Distinct().Count());
            Assert.All(prefix, value => Assert.InRange(value, (uint)directional, (uint)(directional + lightCount - 1)));
            Assert.All(assigned[1].AsSpan(cell * capacity + valid, capacity - valid).ToArray(),
                value => Assert.Equal(untouched, value));
        }

        uint[][] Dispatch(string entry, DispatchArguments dispatch, uint[] counts, uint[] output) =>
            new D3D12TestCompute(shaderCompiler).Execute("clustered_light_grid.slang", entry, dispatch,
            [
                (parameters, ResourceBindingType.BufferSrv, 192), (lights, ResourceBindingType.BufferSrv, 144),
                (counts, ResourceBindingType.BufferUav, 8), (output, ResourceBindingType.BufferUav, 4),
            ]);
    }

    private static uint Float(float value) => BitConverter.SingleToUInt32Bits(value);
}
