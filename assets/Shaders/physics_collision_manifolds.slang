DeviceGeometryDistance reverseDeviceDistance(DeviceGeometryDistance value)
{
    DeviceGeometryDistance result;
    result.firstPoint = value.secondPoint;
    result.secondPoint = value.firstPoint;
    result.firstFeature = value.secondFeature;
    result.secondFeature = value.firstFeature;
    result.normal = -value.normal;
    result.separation = value.separation;
    return result;
}

DeviceFeaturePoint interpolateDeviceFeature(DeviceFeaturePoint first, DeviceFeaturePoint second, float parameter)
{
    DeviceFeaturePoint result;
    result.point = lerp(first.point, second.point, parameter);
    result.feature = first.feature;
    return result;
}

uint reduceDeviceManifold(uint work, uint count, out DeviceGeometryDistance points[4])
{
    uint start = work * (2 * DetectionParameters.faceCapacity + 4);
    if (count <= 4)
    {
        for (uint index = 0; index < count; index++) points[index] = ManifoldCandidates[start + index];
        return count;
    }
    uint selected[4];
    selected[0] = 0;
    for (uint index = 1; index < count; index++)
        if (ManifoldCandidates[start + index].separation < ManifoldCandidates[start + selected[0]].separation)
            selected[0] = index;
    for (uint point = 1; point < 4; point++)
    {
        float farthest = -1;
        for (uint index = 0; index < count; index++)
        {
            float score = 3.402823466e38f;
            for (uint previous = 0; previous < point; previous++)
            {
                float3 offset = ManifoldCandidates[start + index].firstPoint - ManifoldCandidates[start + selected[previous]].firstPoint;
                score = min(score, dot(offset, offset));
            }
            if (point == 2)
            {
                float3 area = cross(ManifoldCandidates[start + selected[1]].firstPoint - ManifoldCandidates[start + selected[0]].firstPoint,
                    ManifoldCandidates[start + index].firstPoint - ManifoldCandidates[start + selected[0]].firstPoint);
                score = dot(area, area);
            }
            if (score > farthest)
            {
                farthest = score;
                selected[point] = index;
            }
        }
    }
    for (uint index = 0; index < 4; index++) points[index] = ManifoldCandidates[start + selected[index]];
    return 4;
}

uint clipDeviceManifold(uint work, uint reference, uint referenceCount, uint incident, uint incidentCount,
    float3 normal, float manifoldDistance, out DeviceGeometryDistance points[4])
{
    uint polygon = work * (2 * DetectionParameters.faceCapacity + 4);
    for (uint index = 0; index < incidentCount; index++) ContactPolygon[polygon + index] = CollisionFaces[incident + index];
    uint count = incidentCount;
    float3 center = 0;
    for (uint index = 0; index < referenceCount; index++) center += CollisionFaces[reference + index].point;
    center /= float(referenceCount);
    float tolerance = DetectionParameters.tolerance;
    for (uint edge = 0; edge < referenceCount && count > 0; edge++)
    {
        float3 origin = CollisionFaces[reference + edge].point;
        float3 side = cross(normal, CollisionFaces[reference + (edge + 1) % referenceCount].point - origin);
        if (dot(side, center - origin) < 0) side = -side;
        if (incidentCount == 2)
        {
            float first = dot(side, ContactPolygon[polygon].point - origin);
            float second = dot(side, ContactPolygon[polygon + count - 1].point - origin);
            if (first < -tolerance && second < -tolerance) return 0;
            if (first < -tolerance)
                ContactPolygon[polygon] = interpolateDeviceFeature(ContactPolygon[polygon], ContactPolygon[polygon + count - 1], first / (first - second));
            else if (second < -tolerance)
                ContactPolygon[polygon + count - 1] = interpolateDeviceFeature(ContactPolygon[polygon], ContactPolygon[polygon + count - 1], first / (first - second));
            continue;
        }
        uint written = 0;
        DeviceFeaturePoint previous = ContactPolygon[polygon + count - 1];
        float previousDistance = dot(side, previous.point - origin);
        for (uint index = 0; index < count; index++)
        {
            DeviceFeaturePoint current = ContactPolygon[polygon + index];
            float currentDistance = dot(side, current.point - origin);
            bool previousInside = previousDistance >= -tolerance;
            bool currentInside = currentDistance >= -tolerance;
            if (previousInside != currentInside)
                ContactClip[polygon + written++] = interpolateDeviceFeature(previous, current, previousDistance / (previousDistance - currentDistance));
            if (currentInside) ContactClip[polygon + written++] = current;
            previous = current;
            previousDistance = currentDistance;
        }
        for (uint index = 0; index < written; index++) ContactPolygon[polygon + index] = ContactClip[polygon + index];
        count = written;
    }
    float3 faceNormal = normalize(cross(CollisionFaces[reference + 1].point - CollisionFaces[reference].point,
        CollisionFaces[reference + 2].point - CollisionFaces[reference].point));
    if (dot(faceNormal, normal) < 0) faceNormal = -faceNormal;
    float projection = dot(faceNormal, normal);
    uint contactCount = 0;
    for (uint index = 0; index < count; index++)
    {
        DeviceFeaturePoint point = ContactPolygon[polygon + index];
        float separation = dot(faceNormal, point.point - CollisionFaces[reference].point) / projection;
        if (separation > manifoldDistance + tolerance) continue;
        DeviceGeometryDistance contact;
        contact.firstPoint = point.point - normal * separation;
        contact.secondPoint = point.point;
        contact.normal = normal;
        contact.separation = separation;
        contact.firstFeature = CollisionFaces[reference].feature;
        contact.secondFeature = point.feature;
        ManifoldCandidates[polygon + contactCount++] = contact;
    }
    return reduceDeviceManifold(work, contactCount, points);
}

uint clipDeviceManifold(uint work, uint reference, uint referenceCount, uint incident, uint incidentCount,
    float3 normal, out DeviceGeometryDistance points[4])
{
    return clipDeviceManifold(work, reference, referenceCount, incident, incidentCount,
        normal, DetectionParameters.contactDistance, points);
}

uint lineDeviceManifold(uint first, uint second, float3 normal, out DeviceGeometryDistance points[4])
{
    float3 firstEdge = CollisionFaces[first + 1].point - CollisionFaces[first].point;
    float3 secondEdge = CollisionFaces[second + 1].point - CollisionFaces[second].point;
    float firstLength = length(firstEdge);
    float secondSquared = dot(secondEdge, secondEdge);
    float3 crossProduct = cross(firstEdge, secondEdge);
    float tolerance = DetectionParameters.tolerance;
    if (dot(crossProduct, crossProduct) > tolerance * tolerance * firstLength * firstLength * secondSquared) return 0;
    float3 axis = firstEdge / firstLength;
    float secondStart = dot(CollisionFaces[second].point - CollisionFaces[first].point, axis);
    float secondEnd = dot(CollisionFaces[second + 1].point - CollisionFaces[first].point, axis);
    float minimum = max(0, min(secondStart, secondEnd));
    float maximum = min(firstLength, max(secondStart, secondEnd));
    if (maximum < minimum) return 0;
    uint count = maximum - minimum <= tolerance ? 1 : 2;
    for (uint index = 0; index < count; index++)
    {
        float3 firstPoint = CollisionFaces[first].point + axis * (index == 0 ? minimum : maximum);
        float parameter = dot(firstPoint - CollisionFaces[second].point, secondEdge) / secondSquared;
        float3 secondPoint = CollisionFaces[second].point + secondEdge * parameter;
        DeviceGeometryDistance point;
        point.firstPoint = firstPoint;
        point.secondPoint = secondPoint;
        point.normal = normal;
        point.separation = dot(secondPoint - firstPoint, normal);
        point.firstFeature = CollisionFaces[first + index].feature;
        point.secondFeature = CollisionFaces[second + index].feature;
        points[index] = point;
    }
    return count;
}

uint manifoldDevicePair<TFirst : IDeviceConvex, TSecond : IDeviceConvex>(uint work,
    DeviceGeometryInput first, DeviceGeometryInput second, DeviceGeometryDistance distance,
    float manifoldDistance, out DeviceGeometryDistance points[4])
{
    TFirst a;
    TSecond b;
    uint firstFace = work * 2 * DetectionParameters.faceCapacity;
    uint secondFace = firstFace + DetectionParameters.faceCapacity;
    uint firstCount = a.face(first, distance.normal, firstFace);
    uint secondCount = b.face(second, -distance.normal, secondFace);
    uint count = 0;
    if (firstCount >= 3)
        count = clipDeviceManifold(work, firstFace, firstCount, secondFace, secondCount, distance.normal, manifoldDistance, points);
    else if (secondCount >= 3)
    {
        count = clipDeviceManifold(work, secondFace, secondCount, firstFace, firstCount, -distance.normal, manifoldDistance, points);
        for (uint index = 0; index < count; index++) points[index] = reverseDeviceDistance(points[index]);
    }
    else if (firstCount == 2 && secondCount == 2)
        count = lineDeviceManifold(firstFace, secondFace, distance.normal, points);
    if (count > 0) return count;
    points[0] = distance;
    return 1;
}

uint manifoldDevicePair<TFirst : IDeviceConvex, TSecond : IDeviceConvex>(uint work,
    DeviceGeometryInput first, DeviceGeometryInput second, DeviceGeometryDistance distance,
    out DeviceGeometryDistance points[4])
{
    return manifoldDevicePair<TFirst, TSecond>(work, first, second, distance, DetectionParameters.contactDistance, points);
}
