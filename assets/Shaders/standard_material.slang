#ifndef STANDARD_SLANG
#define STANDARD_SLANG

__exported import material_interfaces;
__exported import triangle_surface;
__exported import coating_material;
__exported import material_lighting;

// C# imports this marked struct through Slang reflection and writes the material
// scalar payload using Slang's own field offsets and type size.
[MaterialScalars]
struct StandardScalars
{
    float4 BaseColorTint;
    float MetallicFactor;
    float Roughness;
    float3 EmissiveFactor;
};

struct StandardPixelContext
{
    uint3 pixelCoord;
    uint instanceID;
    float3 surfacePosition;
    float3 worldNormal;
    float4 worldTangent;
    float2 uv0;
};

StandardPixelContext BuildStandardPixelContext(
    uint3 pixelCoord,
    uint instanceID,
    SurfaceEval surface)
{
    StandardPixelContext ctx;
    ctx.pixelCoord = pixelCoord;
    ctx.instanceID = instanceID;
    ctx.surfacePosition = surface.Position;
    ctx.worldNormal = surface.Normal;
    ctx.worldTangent = surface.Tangent;
    ctx.uv0 = surface.TexCoord;
    return ctx;
}

struct StandardMaterial : ISurfaceEvaluate<SurfaceEval, Triangle>
{
    // Textures
    Texture2D AlbedoMap;
    Texture2D NormalMap;
    Texture2D ARMMap;
    SamplerState MaterialSampler;

    float4 evaluateColor(
        SurfacePixelInput input,
        SurfaceEval surface,
        float3 bary,
        Triangle triangle)
    {
        StandardPixelContext ctx = BuildStandardPixelContext(
            input.PixelCoord,
            input.InstanceID,
            surface);

        if (input.VisibilityDepth == 2u)
            ctx.surfacePosition = input.PositionFromDepth;
        float2 uvDx = triangle.TexCoord0 * input.BarycentricDx.x + triangle.TexCoord1 * input.BarycentricDx.y + triangle.TexCoord2 * input.BarycentricDx.z;
        float2 uvDy = triangle.TexCoord0 * input.BarycentricDy.x + triangle.TexCoord1 * input.BarycentricDy.y + triangle.TexCoord2 * input.BarycentricDy.z;

        // Sample material textures
        float4 albedoSample = AlbedoMap.SampleGrad(MaterialSampler, ctx.uv0, uvDx, uvDy);
        float3 armSample    = ARMMap.SampleGrad(MaterialSampler, ctx.uv0, uvDx, uvDy).rgb;
        float3 normalTS     = NormalMap.SampleGrad(MaterialSampler, ctx.uv0, uvDx, uvDy).rgb * 2.0 - 1.0;
        StandardScalars scalars = LoadMaterialScalars<StandardScalars>(input.MaterialIndex);

        // TBN matrix: transform normal from tangent space to world space
        float3 N_geom = normalize(ctx.worldNormal);
        float3 T = normalize(ctx.worldTangent.xyz - N_geom * dot(N_geom, ctx.worldTangent.xyz)); // Gram-Schmidt
        float3 B = cross(N_geom, T) * ctx.worldTangent.w; // handedness
        float3 mappedNormal = normalize(T * normalTS.x + B * normalTS.y + N_geom * normalTS.z);

        ClusterViewUniforms cameraView = MaterialViews[input.PixelCoord.z];
        float3 positionDx = StandardInstanceData::TransformFirstPersonWorldPosition(MaterialInstanceData, MaterialInstanceProperties,
            input.InstanceID, cameraView, ctx.surfacePosition + input.PositionDx);
        float3 positionDy = StandardInstanceData::TransformFirstPersonWorldPosition(MaterialInstanceData, MaterialInstanceProperties,
            input.InstanceID, cameraView, ctx.surfacePosition + input.PositionDy);
        ctx.surfacePosition = StandardInstanceData::TransformFirstPersonWorldPosition(MaterialInstanceData, MaterialInstanceProperties,
            input.InstanceID, cameraView, ctx.surfacePosition);
        positionDx -= ctx.surfacePosition;
        positionDy -= ctx.surfacePosition;
        mappedNormal = StandardInstanceData::TransformFirstPersonWorldNormal(MaterialInstanceData, MaterialInstanceProperties,
            input.InstanceID, cameraView, mappedNormal);
        N_geom = StandardInstanceData::TransformFirstPersonWorldNormal(MaterialInstanceData, MaterialInstanceProperties,
            input.InstanceID, cameraView, N_geom);
        float3 baseColor = albedoSample.rgb * scalars.BaseColorTint.rgb;
        float roughness = saturate(armSample.g * scalars.Roughness);
        float metallic = saturate(armSample.b * scalars.MetallicFactor);
        roughness = max(roughness, 0.04);
        MaterialDecalIterator decals = BeginMaterialDecals(input.PixelCoord, ctx.surfacePosition);
        MaterialDecalInput decal;
        while (NextMaterialDecal(decals, ctx.surfacePosition, positionDx, positionDy, decal))
        {
            CoatingLayer layer = EvaluateCoating(decal);
            float alpha = saturate(layer.BaseColor.a);
            baseColor = lerp(baseColor, layer.BaseColor.rgb, alpha);
            roughness = lerp(roughness, layer.Roughness, alpha);
            mappedNormal = SafeNormalize(lerp(mappedNormal, layer.Normal, alpha), N_geom);
        }
        float opacity = albedoSample.a * scalars.BaseColorTint.a;
        ClusteredLightGridParameters lighting = LightingViews[ctx.pixelCoord.z];
        float3 viewPosition = mul(float4(ctx.surfacePosition, 1.0), lighting.View).xyz;
        float viewDepth = max(-viewPosition.z, lighting.ZParams.x);
        SurfaceReceiver receiver = (SurfaceReceiver)0;
        receiver.Position = ctx.surfacePosition;
        receiver.ShadingNormal = mappedNormal;
        float3 geometricNormal = SafeNormalize(cross(triangle.Position1 - triangle.Position0,
            triangle.Position2 - triangle.Position0), surface.Normal);
        if (dot(geometricNormal, surface.Normal) < 0.0) geometricNormal = -geometricNormal;
        receiver.GeometricNormal = StandardInstanceData::TransformFirstPersonWorldNormal(MaterialInstanceData, MaterialInstanceProperties,
            input.InstanceID, cameraView, geometricNormal);
        receiver.Flags = SURFACE_RECEIVER_PRESENT | SURFACE_RECEIVER_LIGHTING;
        if ((StandardInstanceData::LoadInstanceFirstPerson(MaterialInstanceData, MaterialInstanceProperties, input.InstanceID).x & 3u) == 1u)
            receiver.Flags |= SURFACE_RECEIVER_FIRST_PERSON;
        receiver.Depth = input.Depth;
        ClusterViewUniforms surfaceView = StandardInstanceData::ApplyFirstPersonViewTransform(MaterialInstanceData, MaterialInstanceProperties,
            input.InstanceID, cameraView);
        float4 sourceClip = mul(float4(ClusterPositionForView(surface.Position, surfaceView), 1.0), surfaceView.ViewProj);
        receiver.DepthOffset = input.VisibilityDepth == 2u ? input.Depth - sourceClip.z / sourceClip.w : 0.0;
        float3 V = normalize(cameraView.CameraPos - ctx.surfacePosition);
        float3 finalColor = baseColor * armSample.r * 0.03 + scalars.EmissiveFactor;

        if (dot(mappedNormal, V) <= 0.0)
        {
            return float4(finalColor, opacity);
        }

        for (uint lightIndex = 0u; lightIndex < lighting.DirectionalCount; lightIndex++)
        {
            GPULight light = LightBuffer[lightIndex];
            if (dot(mappedNormal, -light.Direction) <= 0.0)
                continue;
            float shadow = EvaluateVirtualShadowForwardVisibility(input.PixelCoord, lightIndex, light, receiver,
                uint3(cameraView.ScreenWidth, cameraView.ScreenHeight, 1u), cameraView, MaterialDepth);
            finalColor += EvaluateDirectionalLight(
                baseColor,
                metallic,
                roughness,
                mappedNormal,
                V,
                -normalize(light.Direction),
                light.Color * (light.Intensity * shadow),
                EvaluateLightingCookie(light, ctx.surfacePosition));
        }

        uint cell = ClusteredLightCellIndex(
            lighting,
            ctx.pixelCoord.z,
            ctx.pixelCoord.xy,
            viewDepth);
        uint2 cellLights = ClusteredLightGrid[cell];
        uint localCount = cellLights.y;
        uint firstIndex = cellLights.x;
        for (uint localIndex = 0u; localIndex < localCount; localIndex++)
        {
            uint lightIndex = LightIndexList[firstIndex + localIndex];
            GPULight light = LightBuffer[lightIndex];
            float3 toLight = light.Position - ctx.surfacePosition;
            float distanceSq = dot(toLight, toLight);
            if (light.Range <= 0.0 || distanceSq >= light.Range * light.Range || dot(mappedNormal, toLight) <= 0.0)
                continue;
            bool spotLight = dot(light.Direction, light.Direction) > 1e-8 && light.OuterConeCos > 0.0;
            if (spotLight && EvaluateSpotCone(toLight * rsqrt(max(distanceSq, MIN_DISTANCE_SQ)),
                    light.Direction, light.InnerConeCos, light.OuterConeCos) <= 0.0)
                continue;
            float shadow = EvaluateVirtualShadowForwardVisibility(input.PixelCoord, lightIndex, light, receiver,
                uint3(cameraView.ScreenWidth, cameraView.ScreenHeight, 1u), cameraView, MaterialDepth);
            float3 radiance = light.Color * (light.Intensity * shadow);
            if (spotLight)
            {
                finalColor += EvaluateSpotLight(
                    baseColor,
                    metallic,
                    roughness,
                    mappedNormal,
                    V,
                    ctx.surfacePosition,
                    light.Position,
                    light.Range,
                    light.Direction,
                    light.InnerConeCos,
                    light.OuterConeCos,
                    radiance,
                    EvaluateLightingCookie(light, ctx.surfacePosition));
            }
            else
            {
                finalColor += EvaluatePointLight(
                    baseColor,
                    metallic,
                    roughness,
                    mappedNormal,
                    V,
                    ctx.surfacePosition,
                    light.Position,
                    light.Range,
                    radiance,
                    EvaluateLightingCookie(light, ctx.surfacePosition));
            }
        }

        return float4(finalColor, opacity);
    }

    void evaluateSurface<TOutput : ISurfaceOutput>(
        SurfacePixelInput input, SurfaceEval surface, float3 bary,
        Triangle triangle, TOutput output)
    {
        float4 color = evaluateColor(input, surface, bary, triangle);
        output.Write(input.PixelCoord, color);
    }

    float4 evaluateDebugColor(uint debugMode, SurfacePixelInput input,
        SurfaceEval surface, float3 bary, Triangle triangle)
    {
        if (debugMode == 8u) return float4(bary, 1.0);
        if (debugMode == 9u) return float4(surface.Normal * .5 + .5, 1.0);
        if (debugMode == 10u)
        {
            float checker = step(.5, frac(surface.TexCoord.x * 8.0)) !=
                step(.5, frac(surface.TexCoord.y * 8.0)) ? .8 : .2;
            return float4(surface.TexCoord, checker, 1.0);
        }
        if (debugMode == 11u)
            return float4(abs(surface.Position - EvalLinearPosition(triangle, bary)) * 3.0, 1.0);
        return float4(1.0, 0.0, 1.0, 1.0);
    }

    void evaluateDebug<TOutput : ISurfaceOutput>(
        uint debugMode, SurfacePixelInput input, SurfaceEval surface, float3 bary,
        Triangle triangle, TOutput output)
    {
        output.Write(input.PixelCoord, evaluateDebugColor(debugMode, input, surface, bary, triangle));
    }


};

#endif
