void addDevicePenetrationEdge(uint start, inout int count, int first, int second)
{
    for (int index = 0; index < count; index++)
    {
        int2 edge = PenetrationEdges[start + index];
        if (edge.x != second || edge.y != first) continue;
        PenetrationEdges[start + index] = PenetrationEdges[start + --count];
        return;
    }
    PenetrationEdges[start + count++] = int2(first, second);
}

void addDevicePenetrationFace(uint vertices, uint faces, inout int count,
    int first, int second, int third, float3 interior)
{
    float3 a = PenetrationVertices[vertices + first].difference();
    float3 normal = cross(PenetrationVertices[vertices + second].difference() - a,
        PenetrationVertices[vertices + third].difference() - a);
    float squared = dot(normal, normal);
    if (squared == 0) return;
    normal /= sqrt(squared);
    if (dot(normal, interior - a) > 0)
    {
        normal = -normal;
        int previous = second;
        second = third;
        third = previous;
    }
    DevicePenetrationFace face;
    face.first = first;
    face.second = second;
    face.third = third;
    face.normal = normal;
    face.distance = max(0, dot(normal, a));
    PenetrationFaces[faces + count++] = face;
}

DeviceGeometryDistance roundedDeviceCore<TFirst : IDeviceConvex, TSecond : IDeviceConvex>(
    DeviceGeometryInput first, DeviceGeometryInput second, DeviceSupportPoint simplex[4], int count, float3 normal)
{
    float4 weights;
    closestDeviceSimplex(simplex, count, float3(0), weights);
    TFirst a;
    TSecond b;
    return witnessDeviceSimplex(simplex, count, weights, normal, 0, a.radius(first), b.radius(second));
}

DeviceGeometryDistance penetrationDevicePair<TFirst : IDeviceConvex, TSecond : IDeviceConvex>(
    uint work, DeviceGeometryInput first, DeviceGeometryInput second, DeviceSupportPoint simplex[4], int count)
{
    uint capacity = 4 + DetectionParameters.penetrationIterations;
    uint vertices = work * capacity;
    uint faces = work * capacity * 2;
    uint edges = work * capacity * 6;
    for (int index = 0; index < count; index++) PenetrationVertices[vertices + index] = simplex[index];
    int originalCount = count;
    float tolerance = DetectionParameters.tolerance;
    float squaredTolerance = tolerance * tolerance;
    if (count == 1)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            float3 direction = 0;
            direction[axis] = 1;
            PenetrationVertices[vertices + count] = supportDevicePair<TFirst, TSecond>(first, second, direction);
            float3 difference = PenetrationVertices[vertices + count].difference() - PenetrationVertices[vertices].difference();
            if (dot(difference, difference) > squaredTolerance) break;
            PenetrationVertices[vertices + count] = supportDevicePair<TFirst, TSecond>(first, second, -direction);
            difference = PenetrationVertices[vertices + count].difference() - PenetrationVertices[vertices].difference();
            if (dot(difference, difference) > squaredTolerance) break;
        }
        float3 edge = PenetrationVertices[vertices + count].difference() - PenetrationVertices[vertices].difference();
        if (dot(edge, edge) <= squaredTolerance)
            return roundedDeviceCore<TFirst, TSecond>(first, second, simplex, originalCount, float3(1, 0, 0));
        count++;
    }
    if (count == 2)
    {
        float3 edge = PenetrationVertices[vertices + 1].difference() - PenetrationVertices[vertices].difference();
        float3 axis = abs(edge.x) < abs(edge.y) ? float3(1, 0, 0) : float3(0, 1, 0);
        float3 normal = cross(edge, axis);
        float areaTolerance = squaredTolerance * dot(edge, edge);
        float3 alternate = cross(edge, normal);
        for (int direction = 0; direction < 4; direction++)
        {
            float3 search = direction < 2 ? normal : alternate;
            if ((direction & 1) != 0) search = -search;
            PenetrationVertices[vertices + count] = supportDevicePair<TFirst, TSecond>(first, second, search);
            float3 area = cross(edge, PenetrationVertices[vertices + count].difference() - PenetrationVertices[vertices].difference());
            if (dot(area, area) > areaTolerance) break;
        }
        float3 area = cross(edge, PenetrationVertices[vertices + count].difference() - PenetrationVertices[vertices].difference());
        if (dot(area, area) <= areaTolerance)
            return roundedDeviceCore<TFirst, TSecond>(first, second, simplex, originalCount, normalize(normal));
        count++;
    }
    if (count == 3)
    {
        float3 normal = normalize(cross(PenetrationVertices[vertices + 1].difference() - PenetrationVertices[vertices].difference(),
            PenetrationVertices[vertices + 2].difference() - PenetrationVertices[vertices].difference()));
        PenetrationVertices[vertices + count] = supportDevicePair<TFirst, TSecond>(first, second, normal);
        if (abs(dot(normal, PenetrationVertices[vertices + count].difference() - PenetrationVertices[vertices].difference())) <= tolerance)
            PenetrationVertices[vertices + count] = supportDevicePair<TFirst, TSecond>(first, second, -normal);
        if (abs(dot(normal, PenetrationVertices[vertices + count].difference() - PenetrationVertices[vertices].difference())) <= tolerance)
            return roundedDeviceCore<TFirst, TSecond>(first, second, simplex, originalCount, normal);
        count++;
    }
    float3 interior = (PenetrationVertices[vertices].difference() + PenetrationVertices[vertices + 1].difference()
        + PenetrationVertices[vertices + 2].difference() + PenetrationVertices[vertices + 3].difference()) * 0.25f;
    int faceCount = 0;
    addDevicePenetrationFace(vertices, faces, faceCount, 0, 1, 2, interior);
    addDevicePenetrationFace(vertices, faces, faceCount, 0, 3, 1, interior);
    addDevicePenetrationFace(vertices, faces, faceCount, 0, 2, 3, interior);
    addDevicePenetrationFace(vertices, faces, faceCount, 1, 3, 2, interior);
    DevicePenetrationFace nearest;
    [loop] for (uint iteration = 0; iteration < DetectionParameters.penetrationIterations; iteration++)
    {
        int selected = 0;
        for (int index = 1; index < faceCount; index++)
            if (PenetrationFaces[faces + index].distance < PenetrationFaces[faces + selected].distance) selected = index;
        nearest = PenetrationFaces[faces + selected];
        DeviceSupportPoint support = supportDevicePair<TFirst, TSecond>(first, second, nearest.normal);
        if (dot(nearest.normal, support.difference()) - nearest.distance <= tolerance) break;
        int edgeCount = 0;
        int kept = 0;
        for (int index = 0; index < faceCount; index++)
        {
            DevicePenetrationFace face = PenetrationFaces[faces + index];
            if (dot(face.normal, support.difference() - PenetrationVertices[vertices + face.first].difference()) > 0)
            {
                addDevicePenetrationEdge(edges, edgeCount, face.first, face.second);
                addDevicePenetrationEdge(edges, edgeCount, face.second, face.third);
                addDevicePenetrationEdge(edges, edgeCount, face.third, face.first);
            }
            else PenetrationFaces[faces + kept++] = face;
        }
        faceCount = kept;
        int vertex = count;
        PenetrationVertices[vertices + count++] = support;
        for (int index = 0; index < edgeCount; index++)
        {
            int2 edge = PenetrationEdges[edges + index];
            addDevicePenetrationFace(vertices, faces, faceCount, edge.x, edge.y, vertex, interior);
        }
    }
    DeviceSupportPoint triangle[4];
    triangle[0] = PenetrationVertices[vertices + nearest.first];
    triangle[1] = PenetrationVertices[vertices + nearest.second];
    triangle[2] = PenetrationVertices[vertices + nearest.third];
    float4 weights;
    closestDeviceSimplex(triangle, 3, nearest.normal * nearest.distance, weights);
    TFirst a;
    TSecond b;
    return witnessDeviceSimplex(triangle, 3, weights, nearest.normal, -nearest.distance, a.radius(first), b.radius(second));
}
