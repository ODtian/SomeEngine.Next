using SlangShaderSharp;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceReferencedGeometryTests
{
    [Theory]
    [InlineData("DeviceBox", "DeviceBoxUpdate", "physics_geometry_update.slang", "")]
    [InlineData("DeviceCapsule", "DeviceCapsuleUpdate", "physics_geometry_update.slang", "")]
    [InlineData("DeviceCylinder", "DeviceCylinderUpdate", "physics_geometry_update.slang", "")]
    [InlineData("DeviceHull", "DeviceHullUpdate", "physics_hull_update.slang", "#include \"physics_collision_hull.slang\"")]
    [InlineData("DeviceTriangle", "DeviceDynamicFaceUpdate", "physics_geometry_update.slang", "")]
    public void ConcreteDeviceSdfAndBoundsPipelinesCanBeCreatedWithoutThrowing(string shape, string update, string updateHeader, string shapeHeader)
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        ComputePipelineDesc distance = Compile("concrete_sdf_" + shape, $$"""
            #include "physics_sdf_collision.slang"
            #include "physics_sdf_grid.slang"
            {{shapeHeader}}
            [shader("compute")][numthreads(64,1,1)]
            void Run(uint3 id : SV_DispatchThreadID) {
                DeviceTypePairCollision<DeviceSdfCollision<{{shape}}, DeviceTrilinearSdfGrid>> kernel;
                kernel.firstType = 0;
                kernel.kernel.settings.cells = uint3(2,2,2);
                kernel.kernel.settings.rootIterations = 20;
                kernel.kernel.settings.supportIterations = 8;
                kernel.kernel.settings.tolerance = 1e-5f;
                detectDeviceReferencedCollision(id.x, kernel);
            }
            """);
        ComputePipelineDesc bounds = Compile("concrete_bounds_" + shape, $$"""
            #include "{{updateHeader}}"
            [shader("compute")][numthreads(64,1,1)]
            void Run(uint3 id : SV_DispatchThreadID) { updateDeviceGeometry<{{update}}>(id.x); }
            """);

        using Pipeline distancePipeline = backend.CreateComputePipeline(device, distance);
        using Pipeline boundsPipeline = backend.CreateComputePipeline(device, bounds);
    }
}
