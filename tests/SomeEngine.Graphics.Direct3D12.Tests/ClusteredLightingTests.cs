using System.Numerics;
using System.Runtime.InteropServices;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class ClusteredLightingTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(80, true)]
    [InlineData(80, false)]
    public void Clear_and_group_assignment_preserve_view_boundaries_and_bounded_indices(int lightCount, bool large)
    {
        const int cells = 8 * 4 * 4, capacity = 16, views = 2, directional = 2;
        const uint untouched = 0xdeadbeef;
        var parameters = new uint[36 * views];
        for (int view = 0; view < views; view++)
        {
            Span<uint> row = parameters.AsSpan(view * 36, 36);
            Matrix4x4 matrix = view == 0 ? Matrix4x4.Identity : Matrix4x4.CreateTranslation(0, 0, -1000);
            MemoryMarshal.Write(MemoryMarshal.AsBytes(row), in matrix);
            row[16] = 16; row[17] = 16; row[18] = 8; row[19] = 4;
            row[20] = Float(1); row[21] = Float(16); row[22] = Float(1); row[23] = 0;
            row[24] = 4; row[25] = capacity; row[26] = directional; row[27] = (uint)lightCount;
            row[28] = Float(1); row[29] = Float(1); row[30] = 128; row[31] = 64; row[32] = cells;
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
            new((uint)lightCount, views, 1), cleared[0], cleared[1]);
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
            Execute("clustered_light_grid.slang", entry, dispatch,
            [
                (parameters, ResourceBindingType.BufferSrv, 144), (lights, ResourceBindingType.BufferSrv, 144),
                (counts, ResourceBindingType.BufferUav, 8), (output, ResourceBindingType.BufferUav, 4),
            ]);
    }

    private static uint Float(float value) => BitConverter.SingleToUInt32Bits(value);
}
