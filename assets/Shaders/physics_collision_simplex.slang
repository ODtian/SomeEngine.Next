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

double deviceCollisionDot(double3 first, double3 second)
{
    return first.x * second.x + first.y * second.y + first.z * second.z;
}

double3 deviceCollisionCross(double3 first, double3 second)
{
    return double3(first.y * second.z - first.z * second.y,
        first.z * second.x - first.x * second.z,
        first.x * second.y - first.y * second.x);
}

float3 closestDeviceSegment(float3 a, float3 b, out float2 weights)
{
    double3 first = double3(a), edge = double3(b) - first;
    double squared = deviceCollisionDot(edge, edge);
    double parameter = squared > 0 ? clamp(-deviceCollisionDot(first, edge) / squared, 0.0, 1.0) : 0;
    weights = float2(1 - parameter, parameter);
    return float3(first + edge * parameter);
}

float3 closestDeviceTriangle(float3 a, float3 b, float3 c, out float3 weights)
{
    double3 first = double3(a), ab = double3(b) - first, ac = double3(c) - first;
    double3 normal = deviceCollisionCross(ab, ac);
    double squared = deviceCollisionDot(normal, normal);
    if (squared > 0)
    {
        double second = deviceCollisionDot(deviceCollisionCross(-first, ac), normal) / squared;
        double third = deviceCollisionDot(deviceCollisionCross(ab, -first), normal) / squared;
        weights = float3(1 - second - third, second, third);
        // Face projection keeps the closest vector in the normal direction at small separations.
        if (second >= 0 && third >= 0 && 1 - second - third >= 0)
            return float3(normal * (deviceCollisionDot(first, normal) / squared));
    }
    float2 edgeWeights;
    float3 closest = closestDeviceSegment(a, b, edgeWeights);
    weights = float3(edgeWeights, 0);
    float3 candidate = closestDeviceSegment(a, c, edgeWeights);
    if (dot(candidate, candidate) < dot(closest, closest))
    {
        closest = candidate; weights = float3(edgeWeights.x, 0, edgeWeights.y);
    }
    candidate = closestDeviceSegment(b, c, edgeWeights);
    if (dot(candidate, candidate) < dot(closest, closest))
    {
        closest = candidate; weights = float3(0, edgeWeights);
    }
    return closest;
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
    double3 origin = double3(a);
    double3 first = double3(b) - origin;
    double3 second = double3(c) - origin;
    double3 third = double3(d) - origin;
    double determinant = deviceCollisionDot(first, deviceCollisionCross(second, third));
    if (determinant != 0)
    {
        double u = deviceCollisionDot(-origin, deviceCollisionCross(second, third)) / determinant;
        double v = deviceCollisionDot(first, deviceCollisionCross(-origin, third)) / determinant;
        double w = deviceCollisionDot(first, deviceCollisionCross(second, -origin)) / determinant;
        double4 candidate = double4(1 - u - v - w, u, v, w);
        if (all(candidate >= 0)) { weights = float4(candidate); return float3(0); }
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
