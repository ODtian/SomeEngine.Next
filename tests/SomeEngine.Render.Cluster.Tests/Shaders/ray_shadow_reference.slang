struct RayShadowInputs
{
    float4x4 ClipToWorld;
    float3 CameraPosition;
    uint Width;
    float3 LightDirection;
    uint Height;
    float3 LightPosition;
    uint LightKind;
    float LightRange;
    float NearPlane;
    float FarPlane;
    float OuterConeCos;
    float SourceAngleRadians;
    float SourceRadius;
    uint SampleCount;
    uint SamplePadding;
};
struct RayVertex
{
    float3 Position;
    float3 Normal;
    float4 Tangent;
    float2 TexCoord;
};
struct RayGeometry
{
    uint VertexStart;
    uint IndexStart;
    uint AlphaStart;
    uint AlphaWidth;
    uint AlphaHeight;
    uint NormalStart;
    uint NormalWidth;
    uint NormalHeight;
    float AlphaCutoff;
    float NormalScale;
    uint DoubleSided;
};
ConstantBuffer<RayShadowInputs> Inputs;
RaytracingAccelerationStructure Scene;
ByteAddressBuffer Vertices;
StructuredBuffer<uint> Indices;
StructuredBuffer<RayGeometry> Geometries;
ByteAddressBuffer Texels;
RWStructuredBuffer<uint> Visibility;

RayVertex ReadVertex(uint index)
{
    uint address = index * 48u;
    RayVertex result;
    result.Position = asfloat(Vertices.Load3(address));
    result.Normal = asfloat(Vertices.Load3(address + 12u));
    result.Tangent = asfloat(Vertices.Load4(address + 24u));
    result.TexCoord = asfloat(Vertices.Load2(address + 40u));
    return result;
}

float4 ReadTexel(uint start, uint width, uint height, int2 pixel)
{
    pixel = ((pixel % int2(width, height)) + int2(width, height)) % int2(width, height);
    uint packed = Texels.Load((start + uint(pixel.y) * width + uint(pixel.x)) * 4u);
    return float4(packed & 255u, (packed >> 8u) & 255u,
        (packed >> 16u) & 255u, packed >> 24u) / 255.0;
}

float4 SampleTexels(uint start, uint width, uint height, float2 uv)
{
    float2 position = uv * float2(width, height) - 0.5;
    int2 pixel = int2(floor(position));
    float2 fraction = frac(position);
    return lerp(lerp(ReadTexel(start, width, height, pixel),
                     ReadTexel(start, width, height, pixel + int2(1, 0)), fraction.x),
                lerp(ReadTexel(start, width, height, pixel + int2(0, 1)),
                     ReadTexel(start, width, height, pixel + int2(1, 1)), fraction.x), fraction.y);
}

void ReadTriangle(RayGeometry geometry, uint primitive, out RayVertex a, out RayVertex b, out RayVertex c)
{
    uint first = geometry.IndexStart + primitive * 3u;
    a = ReadVertex(geometry.VertexStart + Indices[first]);
    b = ReadVertex(geometry.VertexStart + Indices[first + 1u]);
    c = ReadVertex(geometry.VertexStart + Indices[first + 2u]);
}

bool AlphaAccepts(uint instance, uint geometryIndex, uint primitive, float2 bary)
{
    RayGeometry geometry = Geometries[instance + geometryIndex];
    if (geometry.AlphaCutoff < 0.0) return true;
    RayVertex a, b, c;
    ReadTriangle(geometry, primitive, a, b, c);
    float2 uv = a.TexCoord * (1.0 - bary.x - bary.y) + b.TexCoord * bary.x + c.TexCoord * bary.y;
    return SampleTexels(geometry.AlphaStart, geometry.AlphaWidth, geometry.AlphaHeight, uv).a >= geometry.AlphaCutoff;
}

bool MaterialSideAccepts(uint instance, uint geometryIndex, bool frontFace)
{
    return Geometries[instance + geometryIndex].DoubleSided != 0u || frontFace;
}

float3 TransformRayNormal(float3 normal, float3x3 worldToObject)
{
    return normalize(mul(normal, worldToObject));
}

float3 TransformRayTangent(float3 tangent, float3 normal, float3x3 objectToWorld)
{
    float3 transformed = mul(objectToWorld, tangent);
    return normalize(transformed - normal * dot(normal, transformed));
}

float OffsetRayCoordinate(float position, float normal)
{
    // Ray Tracing Gems, chapter 6, section 6.2.2: geometric-normal ULP offset.
    // https://github.com/Apress/ray-tracing-gems/tree/master/Ch_06_A_Fast_and_Robust_Method_for_Avoiding_Self-Intersection
    if (abs(position) < 0.03125)
        return position + normal * 0.0000152587890625;
    int steps = int(normal * 256.0);
    return asfloat(asint(position) + (position < 0.0 ? -steps : steps));
}

float3 OffsetRayPosition(float3 position, float3 normal)
{
    return float3(OffsetRayCoordinate(position.x, normal.x),
        OffsetRayCoordinate(position.y, normal.y), OffsetRayCoordinate(position.z, normal.z));
}

uint PointFace(float3 fromLight)
{
    float3 magnitude = abs(fromLight);
    if (magnitude.x >= magnitude.y && magnitude.x >= magnitude.z) return fromLight.x >= 0.0 ? 0u : 1u;
    if (magnitude.y >= magnitude.z) return fromLight.y >= 0.0 ? 2u : 3u;
    return fromLight.z >= 0.0 ? 4u : 5u;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void TraceRayShadow(uint3 pixel : SV_DispatchThreadID)
{
    if (pixel.x >= Inputs.Width || pixel.y >= Inputs.Height) return;
    uint output = pixel.y * Inputs.Width + pixel.x;
    Visibility[output] = 0xffffffffu;
    float2 uv = (float2(pixel.xy) + 0.5) / float2(Inputs.Width, Inputs.Height);
    float4 nearWorld = mul(float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0), Inputs.ClipToWorld);
    RayDesc primary;
    primary.Origin = Inputs.CameraPosition;
    primary.Direction = normalize(nearWorld.xyz / nearWorld.w - primary.Origin);
    primary.TMin = length(nearWorld.xyz / nearWorld.w - primary.Origin);
    float4 farWorld = mul(float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 1.0, 1.0), Inputs.ClipToWorld);
    primary.TMax = length(farWorld.xyz / farWorld.w - primary.Origin);
    // Every source triangle goes through its authored material side and alpha rules.
    RayQuery<RAY_FLAG_FORCE_NON_OPAQUE> camera;
    camera.TraceRayInline(Scene, RAY_FLAG_NONE, 255u, primary);
    while (camera.Proceed())
        if (camera.CandidateType() == CANDIDATE_NON_OPAQUE_TRIANGLE &&
            MaterialSideAccepts(camera.CandidateInstanceID(), camera.CandidateGeometryIndex(),
                camera.CandidateTriangleFrontFace()) &&
            AlphaAccepts(camera.CandidateInstanceID(), camera.CandidateGeometryIndex(),
                camera.CandidatePrimitiveIndex(), camera.CandidateTriangleBarycentrics()))
            camera.CommitNonOpaqueTriangleHit();
    if (camera.CommittedStatus() != COMMITTED_TRIANGLE_HIT) return;

    RayGeometry geometry = Geometries[camera.CommittedInstanceID() + camera.CommittedGeometryIndex()];
    RayVertex a, b, c;
    ReadTriangle(geometry, camera.CommittedPrimitiveIndex(), a, b, c);
    float2 bary = camera.CommittedTriangleBarycentrics();
    float3 weights = float3(1.0 - bary.x - bary.y, bary);
    float3x3 worldToObject = (float3x3)camera.CommittedWorldToObject3x4();
    float3x3 objectToWorld = (float3x3)camera.CommittedObjectToWorld3x4();
    float3 position = mul(camera.CommittedObjectToWorld3x4(),
        float4(a.Position + bary.x * (b.Position - a.Position) + bary.y * (c.Position - a.Position), 1.0));
    float3 geometricNormal = normalize(cross(mul(objectToWorld, b.Position - a.Position),
        mul(objectToWorld, c.Position - a.Position)));
    if (dot(geometricNormal, -primary.Direction) < 0.0) geometricNormal = -geometricNormal;
    float3 normalA = TransformRayNormal(a.Normal, worldToObject);
    float3 normalB = TransformRayNormal(b.Normal, worldToObject);
    float3 normalC = TransformRayNormal(c.Normal, worldToObject);
    float3 normal = normalize(normalA * weights.x + normalB * weights.y + normalC * weights.z);
    float3 worldTangent = normalize(TransformRayTangent(a.Tangent.xyz, normalA, objectToWorld) * weights.x +
        TransformRayTangent(b.Tangent.xyz, normalB, objectToWorld) * weights.y +
        TransformRayTangent(c.Tangent.xyz, normalC, objectToWorld) * weights.z);
    worldTangent = normalize(worldTangent - normal * dot(normal, worldTangent));
    float handedness = a.Tangent.w * weights.x + b.Tangent.w * weights.y + c.Tangent.w * weights.z;
    handedness = (handedness < 0.0 ? -1.0 : 1.0) * (determinant(objectToWorld) < 0.0 ? -1.0 : 1.0);
    float2 texCoord = a.TexCoord * weights.x + b.TexCoord * weights.y + c.TexCoord * weights.z;
    float3 normalTs = SampleTexels(geometry.NormalStart, geometry.NormalWidth, geometry.NormalHeight, texCoord).rgb * 2.0 - 1.0;
    normalTs.xy *= geometry.NormalScale;
    normal = normalize(worldTangent * normalTs.x + cross(normal, worldTangent) * handedness * normalTs.y + normal * normalTs.z);
    float3 toLight = Inputs.LightKind == 0u ? -Inputs.LightDirection : Inputs.LightPosition - position;
    uint faceBits = Inputs.LightKind == 1u ? (PointFace(-toLight) + 1u) << 8u : 0u;
    uint shadingRejected = all(isfinite(normal)) && dot(normal, -primary.Direction) > 0.0 &&
        dot(normal, normalize(toLight)) >= 0.15 ? 0u : 4u;
    // Light eligibility uses source geometry and authored range/cone, never a mapped
    // normal, production depth bias or a shadow-map residency result.
    if (Inputs.LightKind != 0u && length(toLight) >= Inputs.LightRange)
    {
        Visibility[output] = 2u | shadingRejected | 16u | faceBits;
        return;
    }
    if (Inputs.LightKind == 2u && dot(-normalize(toLight), normalize(Inputs.LightDirection)) < Inputs.OuterConeCos)
    {
        Visibility[output] = 2u | shadingRejected | 32u | faceBits;
        return;
    }
    if (dot(geometricNormal, normalize(toLight)) <= 0.0)
    {
        Visibility[output] = 2u | shadingRejected | faceBits;
        return;
    }

    RayDesc shadow;
    shadow.Origin = OffsetRayPosition(position, geometricNormal);
    toLight = Inputs.LightKind == 0u ? -Inputs.LightDirection : Inputs.LightPosition - shadow.Origin;
    shadow.Direction = normalize(toLight);
    shadow.TMin = 0.0;
    shadow.TMax = Inputs.LightKind == 0u ? 1000000.0 : length(toLight);
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_NON_OPAQUE> ray;
    ray.TraceRayInline(Scene, RAY_FLAG_NONE, 255u, shadow);
    while (ray.Proceed())
        if (ray.CandidateType() == CANDIDATE_NON_OPAQUE_TRIANGLE &&
            // Receiver-to-light rays meet the back of a light-facing raster triangle.
            MaterialSideAccepts(ray.CandidateInstanceID(), ray.CandidateGeometryIndex(),
                !ray.CandidateTriangleFrontFace()) &&
            AlphaAccepts(ray.CandidateInstanceID(), ray.CandidateGeometryIndex(),
                ray.CandidatePrimitiveIndex(), ray.CandidateTriangleBarycentrics()))
            ray.CommitNonOpaqueTriangleHit();
    bool blocked = ray.CommittedStatus() == COMMITTED_TRIANGLE_HIT;
    // InstanceID carries the source mesh's geometry-table offset, shared by all
    // instances of that mesh. InstanceIndex distinguishes actual TLAS instances.
    bool selfHit = blocked && ray.CommittedInstanceIndex() == camera.CommittedInstanceIndex() &&
        ray.CommittedGeometryIndex() == camera.CommittedGeometryIndex() &&
        ray.CommittedPrimitiveIndex() == camera.CommittedPrimitiveIndex();
    Visibility[output] = (blocked ? 0u : 1u) | shadingRejected | (selfHit ? 8u : 0u) | faceBits;
}

uint HashRaySample(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    return value ^ (value >> 16u);
}

float TraceEmitterVisibility(float3 position, float3 geometricNormal, uint2 pixel,
    uint samples, uint sampleSet, uint instanceIndex, uint geometryIndex, uint primitiveIndex,
    out bool selfHit)
{
    float3 origin = OffsetRayPosition(position, geometricNormal);
    float3 toLight = Inputs.LightKind == 0u ? -Inputs.LightDirection : Inputs.LightPosition - origin;
    float distance = length(toLight);
    float3 axis = distance > 0.0 ? toLight / distance : geometricNormal;
    bool insideEmitter = Inputs.LightKind != 0u && distance <= Inputs.SourceRadius;
    float sourceSine = Inputs.LightKind == 0u ? sin(Inputs.SourceAngleRadians * 0.5) : Inputs.SourceRadius / distance;
    float cosineLimit = sqrt(max(1.0 - sourceSine * sourceSine, 0.0));
    float cosineSpan = insideEmitter ? 2.0 : sourceSine * sourceSine / (1.0 + cosineLimit);
    float3 tangent = normalize(cross(abs(axis.z) < 0.999 ? float3(0, 0, 1) : float3(0, 1, 0), axis));
    float3 bitangent = cross(axis, tangent);
    uint seed = HashRaySample(pixel.x + pixel.y * Inputs.Width + sampleSet * 0x9e3779b9u);
    float radialShift = float(HashRaySample(seed)) * 2.3283064365386963e-10;
    float angularShift = float(HashRaySample(seed ^ 0xa511e9b3u)) * 2.3283064365386963e-10;
    uint visible = 0u;
    selfHit = false;
    for (uint sample = 0u; sample < samples; sample++)
    {
        // Integrate constant emitter radiance over its visible solid angle.
        // These shifted low-discrepancy sets use no production shadow-map data.
        float u = frac((float(sample) + 0.5) / float(samples) + radialShift);
        float v = frac(float(reversebits(sample)) * 2.3283064365386963e-10 + angularShift);
        float cosineDelta = cosineSpan * u;
        float cosine = 1.0 - cosineDelta;
        float sine = sqrt(max(cosineDelta * (2.0 - cosineDelta), 0.0));
        float angle = 6.283185307179586 * v;
        float3 direction = axis * cosine + (tangent * cos(angle) + bitangent * sin(angle)) * sine;
        if (dot(geometricNormal, direction) <= 0.0) continue;
        RayDesc shadow;
        shadow.Origin = origin;
        shadow.Direction = direction;
        shadow.TMin = 0.0;
        float surfaceDistance = sqrt(max(Inputs.SourceRadius * Inputs.SourceRadius -
            distance * distance * sine * sine, 0.0));
        shadow.TMax = Inputs.LightKind == 0u ? 1000000.0 : Inputs.SourceRadius > 0.0 ?
            distance * cosine + (insideEmitter ? surfaceDistance : -surfaceDistance) : distance;
        RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_NON_OPAQUE> ray;
        ray.TraceRayInline(Scene, RAY_FLAG_NONE, 255u, shadow);
        while (ray.Proceed())
            if (ray.CandidateType() == CANDIDATE_NON_OPAQUE_TRIANGLE &&
                MaterialSideAccepts(ray.CandidateInstanceID(), ray.CandidateGeometryIndex(),
                    !ray.CandidateTriangleFrontFace()) &&
                AlphaAccepts(ray.CandidateInstanceID(), ray.CandidateGeometryIndex(),
                    ray.CandidatePrimitiveIndex(), ray.CandidateTriangleBarycentrics()))
                ray.CommitNonOpaqueTriangleHit();
        bool blocked = ray.CommittedStatus() == COMMITTED_TRIANGLE_HIT;
        visible += blocked ? 0u : 1u;
        selfHit = selfHit || (blocked && ray.CommittedInstanceIndex() == instanceIndex &&
            ray.CommittedGeometryIndex() == geometryIndex && ray.CommittedPrimitiveIndex() == primitiveIndex);
    }
    return float(visible) / float(samples);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void TraceSoftRayShadow(uint3 pixel : SV_DispatchThreadID)
{
    if (pixel.x >= Inputs.Width || pixel.y >= Inputs.Height) return;
    uint output = (pixel.y * Inputs.Width + pixel.x) * 2u;
    Visibility[output] = asuint(0.0);
    Visibility[output + 1u] = 0xffffffffu;
    float2 uv = (float2(pixel.xy) + 0.5) / float2(Inputs.Width, Inputs.Height);
    float4 nearWorld = mul(float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0), Inputs.ClipToWorld);
    RayDesc primary;
    primary.Origin = Inputs.CameraPosition;
    primary.Direction = normalize(nearWorld.xyz / nearWorld.w - primary.Origin);
    primary.TMin = length(nearWorld.xyz / nearWorld.w - primary.Origin);
    float4 farWorld = mul(float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 1.0, 1.0), Inputs.ClipToWorld);
    primary.TMax = length(farWorld.xyz / farWorld.w - primary.Origin);
    RayQuery<RAY_FLAG_FORCE_NON_OPAQUE> camera;
    camera.TraceRayInline(Scene, RAY_FLAG_NONE, 255u, primary);
    while (camera.Proceed())
        if (camera.CandidateType() == CANDIDATE_NON_OPAQUE_TRIANGLE &&
            MaterialSideAccepts(camera.CandidateInstanceID(), camera.CandidateGeometryIndex(),
                camera.CandidateTriangleFrontFace()) &&
            AlphaAccepts(camera.CandidateInstanceID(), camera.CandidateGeometryIndex(),
                camera.CandidatePrimitiveIndex(), camera.CandidateTriangleBarycentrics()))
            camera.CommitNonOpaqueTriangleHit();
    if (camera.CommittedStatus() != COMMITTED_TRIANGLE_HIT) return;

    RayGeometry geometry = Geometries[camera.CommittedInstanceID() + camera.CommittedGeometryIndex()];
    RayVertex a, b, c;
    ReadTriangle(geometry, camera.CommittedPrimitiveIndex(), a, b, c);
    float2 bary = camera.CommittedTriangleBarycentrics();
    float3 weights = float3(1.0 - bary.x - bary.y, bary);
    float3x3 worldToObject = (float3x3)camera.CommittedWorldToObject3x4();
    float3x3 objectToWorld = (float3x3)camera.CommittedObjectToWorld3x4();
    float3 position = mul(camera.CommittedObjectToWorld3x4(),
        float4(a.Position + bary.x * (b.Position - a.Position) + bary.y * (c.Position - a.Position), 1.0));
    float3 geometricNormal = normalize(cross(mul(objectToWorld, b.Position - a.Position),
        mul(objectToWorld, c.Position - a.Position)));
    if (dot(geometricNormal, -primary.Direction) < 0.0) geometricNormal = -geometricNormal;
    float3 normalA = TransformRayNormal(a.Normal, worldToObject);
    float3 normalB = TransformRayNormal(b.Normal, worldToObject);
    float3 normalC = TransformRayNormal(c.Normal, worldToObject);
    float3 normal = normalize(normalA * weights.x + normalB * weights.y + normalC * weights.z);
    float3 worldTangent = normalize(TransformRayTangent(a.Tangent.xyz, normalA, objectToWorld) * weights.x +
        TransformRayTangent(b.Tangent.xyz, normalB, objectToWorld) * weights.y +
        TransformRayTangent(c.Tangent.xyz, normalC, objectToWorld) * weights.z);
    worldTangent = normalize(worldTangent - normal * dot(normal, worldTangent));
    float handedness = a.Tangent.w * weights.x + b.Tangent.w * weights.y + c.Tangent.w * weights.z;
    handedness = (handedness < 0.0 ? -1.0 : 1.0) * (determinant(objectToWorld) < 0.0 ? -1.0 : 1.0);
    float2 texCoord = a.TexCoord * weights.x + b.TexCoord * weights.y + c.TexCoord * weights.z;
    float3 normalTs = SampleTexels(geometry.NormalStart, geometry.NormalWidth, geometry.NormalHeight, texCoord).rgb * 2.0 - 1.0;
    normalTs.xy *= geometry.NormalScale;
    normal = normalize(worldTangent * normalTs.x + cross(normal, worldTangent) * handedness * normalTs.y + normal * normalTs.z);
    float3 toLight = Inputs.LightKind == 0u ? -Inputs.LightDirection : Inputs.LightPosition - position;
    uint faceBits = Inputs.LightKind == 1u ? (PointFace(-toLight) + 1u) << 8u : 0u;
    uint flags = all(isfinite(normal)) && dot(normal, -primary.Direction) > 0.0 &&
        dot(normal, normalize(toLight)) >= 0.15 ? faceBits : faceBits | 4u;
    if (Inputs.LightKind != 0u && length(toLight) >= Inputs.LightRange)
    {
        Visibility[output + 1u] = flags | 2u | 16u;
        return;
    }
    if (Inputs.LightKind == 2u && dot(-normalize(toLight), normalize(Inputs.LightDirection)) < Inputs.OuterConeCos)
    {
        Visibility[output + 1u] = flags | 2u | 32u;
        return;
    }
    if (dot(geometricNormal, normalize(toLight)) <= 0.0)
    {
        Visibility[output + 1u] = flags | 2u;
        return;
    }
    bool selfHit;
    float value = TraceEmitterVisibility(position, geometricNormal, pixel.xy, Inputs.SampleCount, 0u,
        camera.CommittedInstanceIndex(), camera.CommittedGeometryIndex(), camera.CommittedPrimitiveIndex(), selfHit);
    Visibility[output] = asuint(value);
    Visibility[output + 1u] = flags | (value == 1.0 ? 1u : 0u) | (selfHit ? 8u : 0u);
}
