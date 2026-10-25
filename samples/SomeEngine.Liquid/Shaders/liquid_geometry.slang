float CurvedHeight(float x, float z, out float3 normal)
{
    float4 p = Settings.Profile, r = Settings.Ridges, w = Settings.Ripple;
    float ridge = exp(-z * z * r.y);
    float height = p.x + p.y*x + p.z*sin(r.x*x)*ridge + p.w*z*z + r.z*sin(r.w*z+w.x*x);
    float dx = p.y + p.z*r.x*cos(r.x*x)*ridge + r.z*w.x*cos(r.w*z+w.x*x);
    float dz = -2*r.y*z*p.z*sin(r.x*x)*ridge + 2*p.w*z + r.z*r.w*cos(r.w*z+w.x*x);
    normal = normalize(float3(-dx, 1, -dz));
    return height;
}
float CurvedDistance(float3 position, out float3 normal)
{
    float height = CurvedHeight(position.x, position.z, normal);
    float separation = (position.y - height) * normal.y + Settings.Ripple.w * .5;
    if (separation < 0) normal = -normal;
    float distance = abs(separation) - Settings.Ripple.w * .5;
    float edge = abs(position.x) - Settings.Ripple.y;
    if (edge > distance) { distance = edge; normal = float3(sign(position.x),0,0); }
    edge = abs(position.z) - Settings.Ripple.z;
    if (edge > distance) { distance = edge; normal = float3(0,0,sign(position.z)); }
    return distance;
}
float SolidDistance(float3 position, LiquidObstacle obstacle, out float3 normal)
{
    float3 offset = position - obstacle.Center.xyz;
    if (obstacle.Size.w < .5)
    {
        float distance = length(offset);
        normal = offset / max(distance, .00001);
        return distance - obstacle.Size.x;
    }
    float3 local = Rotate(float4(-obstacle.Rotation.xyz, obstacle.Rotation.w), offset);
    float3 delta = abs(local) - obstacle.Size.xyz, outside = max(delta, 0);
    float distance = length(outside) + min(max(delta.x, max(delta.y, delta.z)), 0);
    float3 localNormal = dot(outside, outside) > 0 ? normalize(outside) * sign(local) :
        delta.x > delta.y && delta.x > delta.z ? float3(sign(local.x), 0, 0) :
        delta.y > delta.z ? float3(0, sign(local.y), 0) : float3(0, 0, sign(local.z));
    normal = Rotate(obstacle.Rotation, localNormal);
    return distance;
}
float SceneDistance(float3 position, out float3 normal)
{
    float distance = position.y - Settings.Fluid.y;
    normal = float3(0, 1, 0);
    float wall = Settings.Fluid.z - abs(position.x);
    if (wall < distance) { distance = wall; normal = float3(-sign(position.x), 0, 0); }
    wall = Settings.Fluid.w - abs(position.z);
    if (wall < distance) { distance = wall; normal = float3(0, 0, -sign(position.z)); }
    if (Settings.GridSize.x > .5)
    {
        if (abs(position.x) < Settings.Ripple.y + .003 && abs(position.z) < Settings.Ripple.z + .003)
        {
            float3 n;
            float d = CurvedDistance(position, n);
            if (d < distance) { distance = d; normal = n; }
        }
    }
    else for (uint obstacle = 0; obstacle < 2; obstacle++)
    {
        float3 n;
        float d = SolidDistance(position, Obstacles[obstacle], n);
        if (d < distance) { distance = d; normal = n; }
    }
    return distance;
}

float ContactDistance(float3 position, uint surface, out float3 normal)
{
    if (surface == CONTACT_TrayFloor) { normal = float3(0,1,0); return position.y - Settings.Fluid.y; }
    if (surface == CONTACT_TrayXWall) { normal = float3(-sign(position.x),0,0); return Settings.Fluid.z - abs(position.x); }
    if (surface == CONTACT_TrayZWall) { normal = float3(0,0,-sign(position.z)); return Settings.Fluid.w - abs(position.z); }
    if (surface == CONTACT_Sphere) return SolidDistance(position, Obstacles[1], normal);
    if (surface == CONTACT_CurvedPositiveX || surface == CONTACT_CurvedNegativeX)
    { normal = float3(surface == CONTACT_CurvedPositiveX ? 1 : -1,0,0); return dot(position,normal) - Settings.Ripple.y; }
    if (surface == CONTACT_CurvedPositiveZ || surface == CONTACT_CurvedNegativeZ)
    { normal = float3(0,0,surface == CONTACT_CurvedPositiveZ ? 1 : -1); return dot(position,normal) - Settings.Ripple.z; }
    if (surface == CONTACT_RampTop || surface >= CONTACT_RampBottom && surface <= CONTACT_RampNegativeZ)
    {
        float3 face = surface == CONTACT_RampTop ? float3(0,1,0) :
            surface == CONTACT_RampBottom ? float3(0,-1,0) :
            surface == CONTACT_RampPositiveX ? float3(1,0,0) :
            surface == CONTACT_RampNegativeX ? float3(-1,0,0) :
            surface == CONTACT_RampPositiveZ ? float3(0,0,1) : float3(0,0,-1);
        LiquidObstacle ramp = Obstacles[0];
        float3 local = Rotate(float4(-ramp.Rotation.xyz, ramp.Rotation.w), position - ramp.Center.xyz);
        normal = Rotate(ramp.Rotation, face);
        return dot(local, face) - dot(ramp.Size.xyz, abs(face));
    }
    float height = CurvedHeight(position.x, position.z, normal);
    float distance = (position.y - height) * normal.y;
    if (surface == CONTACT_CurvedBottom) { normal = -normal; distance = -distance - Settings.Ripple.w; }
    return distance;
}

float3 ProjectContact(float3 position, uint surface, float height, out float3 normal)
{
    float distance = ContactDistance(position, surface, normal);
    position += normal * (height - distance);
    if (surface == CONTACT_CurvedTop || surface == CONTACT_CurvedBottom)
    {
        distance = ContactDistance(position, surface, normal);
        position += normal * (height - distance);
    }
    return position;
}
