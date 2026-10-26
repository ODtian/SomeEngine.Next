using SlangShaderSharp;
using SomeEngine.Physics;

namespace SomeEngine.Graphics.Direct3D12.Tests;

internal static class DeviceGraphTestKernels
{
    internal static DeviceGraphKernels CompileParallel(D3D12TestCompiler compiler, string name, string rowSource, string shaderDirectory)
    {
        D3D12TestEntry[] entries = [new("WriteRows", SlangStage.Compute), new("InitializeDeviceGraph", SlangStage.Compute),
            new("ConnectDeviceGraph", SlangStage.Compute), new("CompressDeviceGraph", SlangStage.Compute),
            new("ScanDeviceGraphDegrees", SlangStage.Compute), new("OffsetDeviceGraphBlocks", SlangStage.Compute),
            new("AddDeviceGraphDegreeOffsets", SlangStage.Compute), new("WriteDeviceGraphAdjacency", SlangStage.Compute), new("ResetDeviceGraphColorProgress", SlangStage.Compute),
            new("ProposeDeviceBodyColors", SlangStage.Compute), new("ResolveDeviceBodyColors", SlangStage.Compute),
            new("ProposeDeviceRowColors", SlangStage.Compute), new("ResolveDeviceRowColors", SlangStage.Compute),
            new("CountDeviceGraphColors", SlangStage.Compute), new("OffsetDeviceGraphGroups", SlangStage.Compute),
            new("CountDeviceGraphRows", SlangStage.Compute), new("OffsetDeviceGraphRanges", SlangStage.Compute), new("WriteDeviceGraphWork", SlangStage.Compute)];
        D3D12TestShader shader = compiler.CompileWithSearchPaths(name,
            rowSource + "\n#include \"physics_constraint_graph_parallel.slang\"", entries, "sm_6_0", shaderDirectory);
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), new DeviceGraphParallelKernels(Entry(1), Entry(2), Entry(3), Entry(4), Entry(5), Entry(6), Entry(7), Entry(8),
            Entry(9), Entry(10), Entry(11), Entry(12), Entry(13), Entry(14), Entry(15), Entry(16), Entry(17)));
    }
}
