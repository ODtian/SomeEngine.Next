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
        SiloDetection("sphere_sphere", 0, "DeviceSphere", "DeviceSphere"),
        SiloDetection("sphere_box", 0, "DeviceSphere", "DeviceBox"),
        SiloDetection("box_box", 1, "DeviceBox", "DeviceBox"),
        SiloDetection("sphere_hull", 0, "DeviceSphere", "DeviceHull"),
        SiloDetection("box_hull", 1, "DeviceBox", "DeviceHull"),
        SiloDetection("hull_hull", 2, "DeviceHull", "DeviceHull"),
    ];

    private ComputePipelineDesc SiloDetection(string name, int firstType, string first, string second) =>
        Compile("silo_" + name, $$"""
        #include "physics_collision_hull.slang"
        #include "physics_geometry_references.slang"
        struct SiloCollision : IDeviceCollisionNarrowPhase
        {
            DeviceGeometryDistance distance(uint work, DeviceGeometryInput first, DeviceGeometryInput second)
            {
                if (first.geometryType == {{firstType}})
                { DeviceConvexCollision<{{first}},{{second}}> shape; return shape.distance(work, first, second); }
                DeviceConvexCollision<{{second}},{{first}}> shape; return shape.distance(work, first, second);
            }
            uint manifold(uint work, DeviceGeometryInput first, DeviceGeometryInput second,
                DeviceGeometryDistance distance, out DeviceGeometryDistance points[4])
            {
                if (first.geometryType == {{firstType}})
                { DeviceConvexCollision<{{first}},{{second}}> shape; return shape.manifold(work, first, second, distance, points); }
                DeviceConvexCollision<{{second}},{{first}}> shape; return shape.manifold(work, first, second, distance, points);
            }
        };
        [shader("compute")][numthreads(64,1,1)]
        void Detect(uint3 id : SV_DispatchThreadID) { SiloCollision shape; detectDeviceReferencedCollision(id.x, shape); }
        """, "Detect")[0];
}
