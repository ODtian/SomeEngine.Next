#include "skin_joint_transforms.slang"
struct CrowdSkin : IVertexEvaluate
{
    typedef StandardVertex Vertex;
    void setSurface(inout Vertex result, float3 position, float3 normal)
    {
        result.position = position;
        result.basePosition = position;
        result.worldNormal = normal;
        float3 tangent = normalize(abs(normal.y) < .9 ? cross(float3(0,1,0), normal) : cross(float3(1,0,0), normal));
        result.worldTangent = float4(tangent, 1);
    }
    precise Vertex evaluate(VertexEvalContext ctx)
    {
        uint footer = ctx.pageHeap.Load(ctx.attributeBlobAddress + ctx.attributeBlobSize - 4u);
        uint address = ctx.attributeBlobAddress + footer + ctx.localVertexIndex * 12u;
        uint3 packed = ctx.pageHeap.Load3(address);
        uint4 joints = uint4(packed.x & 65535u, packed.x >> 16u, packed.y & 65535u, packed.y >> 16u);
        float4 weights = float4(packed.z & 255u, (packed.z >> 8u) & 255u, (packed.z >> 16u) & 255u, packed.z >> 24u) / 255.0;
        uint2 binding = LoadSkinBinding(ctx.instanceID);
        precise float3 position = EvalObjectPosition(ctx);
        uint normalStream = ctx.pageHeap.Load(ctx.attributeBlobAddress + 4u);
        precise float3 normal = float3(ReadStandardComponent(ctx, normalStream, 0u, 0),
            ReadStandardComponent(ctx, normalStream, 1u, 1), ReadStandardComponent(ctx, normalStream, 2u, 0));
        precise float3 localPosition = 0;
        precise float3 localNormal = 0;
        [unroll] for (uint influence = 0; influence < 4u; influence++)
        {
            precise float4x4 transform = LoadSkinJointTransform(binding, joints[influence], ctx.IsPreviousFrame != 0u);
            precise float3 transformedPosition = mul(float4(position, 1), transform).xyz;
            precise float3 transformedNormal = mul(float4(normal, 0), transform).xyz;
            localPosition += transformedPosition * weights[influence];
            localNormal += transformedNormal * weights[influence];
        }
        StaticVertexEval source;
        precise Vertex result = source.evaluate(ctx);
        precise float3 worldPosition = TransformInstanceLocalPosition(localPosition, ctx.transform);
        precise float3 worldNormal = normalize(RotateByTransform(localNormal, ctx.transform));
        setSurface(result, worldPosition, worldNormal);
        return result;
    }
    precise float3 getPosition(Vertex value) { return value.position; }
    precise Vertex readLane(Vertex value, uint lane) { StaticVertexEval source; return source.readLane(value, lane); }
};
