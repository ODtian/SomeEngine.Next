#if STAGE_ClearSurface || STAGE_ClearBackground || STAGE_ShadeMesh || STAGE_ShadeSurface
bool HitBox(float3 origin, float3 direction, float3 center, float3 size, out float distance, out float3 normal)
{
    float3 a = (center - size - origin) / direction, b = (center + size - origin) / direction;
    float3 near = min(a, b), far = max(a, b);
    distance = max(near.x, max(near.y, near.z));
    float last = min(far.x, min(far.y, far.z));
    normal = near.x > near.y && near.x > near.z ? float3(-sign(direction.x), 0, 0) :
        near.y > near.z ? float3(0, -sign(direction.y), 0) : float3(0, 0, -sign(direction.z));
    return distance > .00001 && distance < last;
}
bool HitSphere(float3 origin, float3 direction, float3 center, float radius, out float distance, out float3 normal)
{
    float3 offset = origin - center;
    float b = dot(offset, direction), c = dot(offset, offset) - radius * radius;
    float discriminant = b * b - c;
    distance = -b - sqrt(max(0, discriminant));
    normal = (origin + direction * distance - center) / radius;
    return discriminant > 0 && distance > .00001;
}
uint RampContactSurface(float3 normal)
{
    if (abs(normal.y) > .5) return normal.y > 0 ? CONTACT_RampTop : CONTACT_RampBottom;
    if (abs(normal.x) > .5) return normal.x > 0 ? CONTACT_RampPositiveX : CONTACT_RampNegativeX;
    return normal.z > 0 ? CONTACT_RampPositiveZ : CONTACT_RampNegativeZ;
}
uint CurvedContactSurface(float3 normal)
{
    if (normal.y != 0) return normal.y > 0 ? CONTACT_CurvedTop : CONTACT_CurvedBottom;
    if (normal.x != 0) return normal.x > 0 ? CONTACT_CurvedPositiveX : CONTACT_CurvedNegativeX;
    return normal.z > 0 ? CONTACT_CurvedPositiveZ : CONTACT_CurvedNegativeZ;
}
void TraceScene(float3 origin, float3 direction, out float distance, out float3 normal, out uint material, out uint contactSurface)
{
    distance = direction.y < 0 ? (-.19 - origin.y) / direction.y : 100;
    normal = float3(0, 1, 0); material = 0; contactSurface = CONTACT_None;
    float hit; float3 hitNormal;
    if (HitBox(origin, direction, float3(0, -.035, 0), float3(Settings.Fluid.z + .055, .09, Settings.Fluid.w + .055), hit, hitNormal) && hit < distance)
    { distance = hit; normal = hitNormal; material = 1; contactSurface = hitNormal.y > .5 ? CONTACT_TrayFloor : CONTACT_None; }
    for (uint side = 0; side < 4; side++)
    {
        float3 center = side < 2 ? float3(0, .08, (side == 0 ? 1 : -1) * (Settings.Fluid.w + .025)) :
            float3((side == 2 ? 1 : -1) * (Settings.Fluid.z + .025), .08, 0);
        float3 size = side < 2 ? float3(Settings.Fluid.z + .055, .07, .025) : float3(.025, .07, Settings.Fluid.w + .025);
        if (HitBox(origin, direction, center, size, hit, hitNormal) && hit < distance)
        {
            distance = hit; normal = hitNormal; material = 2;
            bool inward = side < 2 ? hitNormal.z == (side == 0 ? -1 : 1) : hitNormal.x == (side == 2 ? -1 : 1);
            contactSurface = inward ? (side < 2 ? CONTACT_TrayZWall : CONTACT_TrayXWall) : CONTACT_None;
        }
    }
    LiquidObstacle ramp = Obstacles[0];
    float4 inverse = float4(-ramp.Rotation.xyz, ramp.Rotation.w);
    if (Settings.GridSize.x < .5 && HitBox(Rotate(inverse, origin - ramp.Center.xyz), Rotate(inverse, direction), 0, ramp.Size.xyz, hit, hitNormal) && hit < distance)
    { distance = hit; normal = Rotate(ramp.Rotation, hitNormal); material = 3; contactSurface = RampContactSurface(hitNormal); }
    LiquidObstacle sphere = Obstacles[1];
    if (Settings.GridSize.x < .5 && HitSphere(origin, direction, sphere.Center.xyz, sphere.Size.x, hit, hitNormal) && hit < distance)
    { distance = hit; normal = hitNormal; material = 4; contactSurface = CONTACT_Sphere; }
    float top = Settings.Profile.x + abs(Settings.Profile.y) * Settings.Ripple.y + abs(Settings.Profile.z) +
        abs(Settings.Profile.w) * Settings.Ripple.z * Settings.Ripple.z + abs(Settings.Ridges.z);
    float3 boundsCenter = float3(0, top * .5, 0), boundsSize = float3(Settings.Ripple.y, top * .5, Settings.Ripple.z);
    bool insideCurveBounds = all(abs(origin - boundsCenter) < boundsSize);
    bool curveBoundsHit = HitBox(origin, direction, boundsCenter, boundsSize, hit, hitNormal);
    if (Settings.GridSize.x > .5 && (insideCurveBounds || curveBoundsHit))
    {
        float travel = insideCurveBounds ? .0002 : hit;
        for (uint iteration = 0; iteration < 36; iteration++)
        {
            float3 p = origin + direction * travel;
            if (abs(p.x) > Settings.Ripple.y + .001 || abs(p.z) > Settings.Ripple.z + .001 || travel > distance || p.y > top + .001) break;
            float3 n;
            float d = CurvedDistance(p, n);
            if (d < .001)
            {
                for (uint refinement = 0; refinement < 2; refinement++)
                {
                    float slope = dot(n, direction);
                    if (abs(slope) > .05) travel -= clamp(d / slope, -.01, .01);
                    p = origin + direction * travel;
                    d = CurvedDistance(p, n);
                }
                if (abs(d) < .00002 && abs(p.x) <= Settings.Ripple.y + .00001 && abs(p.z) <= Settings.Ripple.z + .00001 && travel < distance)
                { distance = travel; normal = n; material = 5; contactSurface = CurvedContactSurface(n); break; }
            }
            travel += max(.00005, d * .7);
        }
    }
    if (HitBox(origin, direction, Settings.GridMinimum.xyz + float3(0,.12,0), float3(.08, .08, .08), hit, hitNormal) && hit < distance)
    { distance = hit; normal = hitNormal; material = 2; contactSurface = CONTACT_None; }
}
void TraceScene(float3 origin, float3 direction, out float distance, out float3 normal, out uint material)
{
    uint contactSurface;
    TraceScene(origin, direction, distance, normal, material, contactSurface);
}
float3 TraceOpaque(float3 origin, float3 direction, out float distance, out float3 normal, out uint contactSurface)
{
    uint material;
    TraceScene(origin, direction, distance, normal, material, contactSurface);
    float3 point = origin + direction * distance;
    float3 light = normalize(float3(-.4, .85, .3));
    float3 baseColor = float3(.09, .12, .15);
    if (material == 0)
    {
        float grid = max(1 - smoothstep(.012, .022, abs(frac(point.x * 2) - .5)),
            1 - smoothstep(.012, .022, abs(frac(point.z * 2) - .5)));
        baseColor = lerp(baseColor, float3(.15, .19, .23), grid * .65);
    }
    else if (material == 1)
    {
        float tile = fmod(floor(point.x * 5) + floor(point.z * 5) + 100, 2);
        baseColor = lerp(float3(.24, .31, .34), float3(.38, .45, .46), tile);
    }
    else if (material == 2) baseColor = float3(.63, .36, .12);
    else if (material == 3) baseColor = float3(.24, .29, .33);
    else if (material == 4) baseColor = float3(.65, .17, .045);
    else
    {
        float bands = .5 + .5 * sin(point.x * 14 + point.z * 8);
        baseColor = lerp(float3(.42,.34,.27), float3(.63,.54,.43), bands * .35);
    }
    float shadowDistance; float3 shadowNormal; uint shadowMaterial;
    TraceScene(point + normal * .003, light, shadowDistance, shadowNormal, shadowMaterial);
    float shadow = shadowDistance < 12 ? .24 : 1;
    float highlight = pow(saturate(dot(normal, normalize(light - direction))), material == 2 ? 90 : 48);
    float3 color = baseColor * (.35 + .85 * saturate(dot(normal, light)) * shadow) + highlight * shadow * .65;
    if (distance >= 99) color = Sky(direction);
    return color;
}
float3 TraceOpaque(float3 origin, float3 direction, out float distance)
{
    float3 normal; uint contactSurface;
    return TraceOpaque(origin, direction, distance, normal, contactSurface);
}

float3 ShadeLiquid(float3 position, float3 direction, float3 normal, float thickness, float foam, float variance)
{
    float3 transmittedDirection = refract(direction, normal, 1 / 1.333);
    float3 reflectedDirection = reflect(direction, normal);
    float transmittedDistance, reflectedDistance;
    float3 transmitted = TraceOpaque(position + transmittedDirection * .00005, transmittedDirection, transmittedDistance);
    float3 reflected = TraceOpaque(position + normal * .002, reflectedDirection, reflectedDistance);
    float3 light = normalize(float3(-.4, .85, .3));
    float wetting = exp(-transmittedDistance * 45);
    transmitted *= 1 - .22 * wetting;
    float3 absorption = exp(-Settings.Extinction.xyz * thickness);
    float scatteringLight = .45 + .55 * saturate(dot(normal, light));
    transmitted = transmitted * absorption + Settings.Scattering.xyz * (1 - absorption) * Settings.Extinction.w * scatteringLight;
    float fresnel = .02 + .98 * pow(1 - saturate(dot(-direction, normal)), 5);
    float3 color = lerp(transmitted, reflected, fresnel);
    float power = 2 / (Settings.Scattering.w + variance * 4) - 2;
    power = max(2, power);
    color += pow(saturate(dot(normal, normalize(light - direction))), power) * ((power + 2) / 162) * float3(2.4, 2.6, 2.8);
    return lerp(color, float3(.8, .9, .94), foam);
}
#endif
