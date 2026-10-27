using SomeEngine.Graphics;

namespace DefaultGame;

internal sealed partial class PhysicsSceneShaders
{
    private const string SceneBodyPairs = """
        StructuredBuffer<uint2> SceneExcludedBodyPairs;
        struct SceneAllowedPairs : IDevicePairRule
        {
            bool matches(uint firstIndex,uint secondIndex,DeviceCollisionInput first,DeviceCollisionInput second)
            {
                if(DeviceBodyStates[firstIndex].motion==0&&DeviceBodyStates[secondIndex].motion==0) return false;
                uint2 pair=uint2(min(firstIndex,secondIndex),max(firstIndex,secondIndex));
                uint begin=0,end=SceneExcludedBodyPairs[0].x;
                while(begin<end)
                {
                    uint middle=begin+(end-begin)/2;
                    uint2 excluded=SceneExcludedBodyPairs[middle+1];
                    if(all(pair==excluded)) return false;
                    bool before=excluded.x<pair.x||(excluded.x==pair.x&&excluded.y<pair.y);
                    if(before) begin=middle+1; else end=middle;
                }
                return true;
            }
        };
        """;

    private ComputePipelineDesc ShapePairDetection(string name, int firstType, string first, string second, bool hull = false) =>
        Compile(name, (hull ? "#include \"physics_collision_hull.slang\"\n" : "") + $$"""
        #include "physics_geometry_references.slang"
        struct ShapePairCollision : IDeviceCollisionNarrowPhase
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
        void Detect(uint3 id : SV_DispatchThreadID) { ShapePairCollision shape; detectDeviceReferencedCollision(id.x, shape); }
        """, "Detect")[0];
}
