DeviceSupportPoint supportDevicePair<TFirst : IDeviceConvex, TSecond : IDeviceConvex>(
    DeviceGeometryInput first, DeviceGeometryInput second, float3 direction)
{
    TFirst a;
    TSecond b;
    DeviceFeaturePoint firstPoint = a.support(first, direction);
    DeviceFeaturePoint secondPoint = b.support(second, -direction);
    DeviceSupportPoint result;
    result.first = firstPoint.point;
    result.second = secondPoint.point;
    result.firstFeature = firstPoint.feature;
    result.secondFeature = secondPoint.feature;
    return result;
}

float3 closestDeviceSegment(float3 a, float3 b, out float2 weights)
{
    float3 edge = b - a;
    float squared = dot(edge, edge);
    float parameter = squared > 0 ? saturate(-dot(a, edge) / squared) : 0;
    weights = float2(1 - parameter, parameter);
    return a + edge * parameter;
}

float3 closestDeviceTriangle(float3 a, float3 b, float3 c, out float3 weights)
{
    float3 ab = b - a;
    float3 ac = c - a;
    float3 ap = -a;
    float d1 = dot(ab, ap);
    float d2 = dot(ac, ap);
    if (d1 <= 0 && d2 <= 0) { weights = float3(1, 0, 0); return a; }
    float3 bp = -b;
    float d3 = dot(ab, bp);
    float d4 = dot(ac, bp);
    if (d3 >= 0 && d4 <= d3) { weights = float3(0, 1, 0); return b; }
    float vc = d1 * d4 - d3 * d2;
    if (vc <= 0 && d1 >= 0 && d3 <= 0)
    {
        float v = d1 / (d1 - d3);
        weights = float3(1 - v, v, 0);
        return a + ab * v;
    }
    float3 cp = -c;
    float d5 = dot(ab, cp);
    float d6 = dot(ac, cp);
    if (d6 >= 0 && d5 <= d6) { weights = float3(0, 0, 1); return c; }
    float vb = d5 * d2 - d1 * d6;
    if (vb <= 0 && d2 >= 0 && d6 <= 0)
    {
        float v = d2 / (d2 - d6);
        weights = float3(1 - v, 0, v);
        return a + ac * v;
    }
    float va = d3 * d6 - d5 * d4;
    if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
    {
        float v = (d4 - d3) / (d4 - d3 + d5 - d6);
        weights = float3(0, 1 - v, v);
        return b + (c - b) * v;
    }
    float inverse = 1 / (va + vb + vc);
    weights = float3(1 - (vb + vc) * inverse, vb * inverse, vc * inverse);
    return a + ab * weights.y + ac * weights.z;
}

float3 closestDeviceSimplex(DeviceSupportPoint points[4], int count, float3 target, out float4 weights)
{
    float3 a = points[0].difference() - target;
    if (count == 1) { weights = float4(1, 0, 0, 0); return a; }
    float3 b = points[1].difference() - target;
    if (count == 2)
    {
        float2 edgeWeights;
        float3 result = closestDeviceSegment(a, b, edgeWeights);
        weights = float4(edgeWeights, 0, 0);
        return result;
    }
    float3 c = points[2].difference() - target;
    float3 faceWeights;
    if (count == 3)
    {
        float3 result = closestDeviceTriangle(a, b, c, faceWeights);
        weights = float4(faceWeights, 0);
        return result;
    }
    float3 d = points[3].difference() - target;
    float3 first = b - a;
    float3 second = c - a;
    float3 third = d - a;
    float determinant = dot(first, cross(second, third));
    if (determinant != 0)
    {
        float u = dot(-a, cross(second, third)) / determinant;
        float v = dot(first, cross(-a, third)) / determinant;
        float w = dot(first, cross(second, -a)) / determinant;
        float4 candidate = float4(1 - u - v - w, u, v, w);
        if (all(candidate >= 0)) { weights = candidate; return float3(0); }
    }
    float3 closest = closestDeviceTriangle(a, b, c, faceWeights);
    weights = float4(faceWeights, 0);
    float3 candidate = closestDeviceTriangle(a, b, d, faceWeights);
    if (dot(candidate, candidate) < dot(closest, closest))
    { closest = candidate; weights = float4(faceWeights.xy, 0, faceWeights.z); }
    candidate = closestDeviceTriangle(a, c, d, faceWeights);
    if (dot(candidate, candidate) < dot(closest, closest))
    { closest = candidate; weights = float4(faceWeights.x, 0, faceWeights.yz); }
    candidate = closestDeviceTriangle(b, c, d, faceWeights);
    if (dot(candidate, candidate) < dot(closest, closest))
    { closest = candidate; weights = float4(0, faceWeights); }
    return closest;
}

float3 reduceDeviceSimplex(inout DeviceSupportPoint points[4], inout int count, out float4 weights)
{
    float4 selected;
    float3 closest = closestDeviceSimplex(points, count, float3(0), selected);
    int written = 0;
    weights = 0;
    for (int index = 0; index < count; index++)
    {
        if (selected[index] <= 0) continue;
        points[written] = points[index];
        weights[written++] = selected[index];
    }
    count = written;
    return closest;
}

DeviceGeometryDistance witnessDeviceSimplex(DeviceSupportPoint points[4], int count, float4 weights,
    float3 normal, float separation, float firstRadius, float secondRadius)
{
    float3 first = 0;
    float3 second = 0;
    int feature = 0;
    for (int index = 0; index < count; index++)
    {
        first += points[index].first * weights[index];
        second += points[index].second * weights[index];
        if (weights[index] > weights[feature]) feature = index;
    }
    DeviceGeometryDistance result;
    result.firstPoint = first + normal * firstRadius;
    result.secondPoint = second - normal * secondRadius;
    result.normal = normal;
    result.separation = separation - firstRadius - secondRadius;
    result.firstFeature = points[feature].firstFeature;
    result.secondFeature = points[feature].secondFeature;
    return result;
}
