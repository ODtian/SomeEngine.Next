using System.Globalization;
using SomeEngine.Graphics;

namespace DefaultGame;

internal sealed partial class PhysicsSceneShaders
{
    internal ComputePipelineDesc SiloGeometry(string type, float contactDistance) => Compile("silo_" + type,
        "static const float SiloContactDistance = " + contactDistance.ToString("R", CultureInfo.InvariantCulture) + ";\n" + $$"""
        #include "physics_hull_update.slang"
        struct SiloGeometryUpdate : IDeviceGeometryUpdate
        {
            DeviceGeometryBounds update(uint index, inout DeviceGeometryInput geometry)
            {
                {{type}} shape;
                DeviceGeometryBounds bounds = shape.update(index, geometry);
                bounds.min -= SiloContactDistance; bounds.max += SiloContactDistance;
                return bounds;
            }
        };
        [shader("compute")][numthreads(64,1,1)]
        void Update(uint3 id : SV_DispatchThreadID) { updateDeviceGeometry<SiloGeometryUpdate>(id.x); }
        """, "Update")[0];

    internal ComputePipelineDesc[] SiloDetection() =>
    [
        ShapePairDetection("silo_sphere_sphere", 0, "DeviceSphere", "DeviceSphere"),
        ShapePairDetection("silo_sphere_box", 0, "DeviceSphere", "DeviceBox"),
        ShapePairDetection("silo_box_box", 1, "DeviceBox", "DeviceBox"),
        ShapePairDetection("silo_sphere_hull", 0, "DeviceSphere", "DeviceHull", hull: true),
        ShapePairDetection("silo_box_hull", 1, "DeviceBox", "DeviceHull", hull: true),
        ShapePairDetection("silo_hull_hull", 2, "DeviceHull", "DeviceHull", hull: true),
    ];
}
