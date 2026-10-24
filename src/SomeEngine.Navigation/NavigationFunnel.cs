// Adapted from Mikko Mononen's funnel algorithm for NavigationMesh's XZ portal convention.
// https://github.com/recastnavigation/recastnavigation/blob/main/Detour/Source/DetourNavMeshQuery.cpp
// Copyright (c) 2009-2010 Mikko Mononen memon@inside.org
//
// This software is provided 'as-is', without any express or implied warranty. In no event will the
// authors be held liable for any damages arising from the use of this software.
// Permission is granted to anyone to use this software for any purpose, including commercial
// applications, and to alter it and redistribute it freely, subject to the following restrictions:
// 1. The origin of this software must not be misrepresented; you must not claim that you wrote the
// original software. If you use this software in a product, an acknowledgment in the product
// documentation would be appreciated but is not required.
// 2. Altered source versions must be plainly marked as such, and must not be misrepresented as
// being the original software.
// 3. This notice may not be removed or altered from any source distribution.

using System.Numerics;

namespace SomeEngine.Navigation;

internal static class NavigationFunnel
{
    internal static Vector3[] Pull(NavigationMesh mesh, ReadOnlySpan<int> portalIndices,
        ReadOnlySpan<int> corridor, Vector3 start, Vector3 goal)
    {
        var corners = new List<Vector3> { start };
        Vector3 apex = start, left = start, right = start;
        int leftIndex = 0, rightIndex = 0;
        for (int index = 1; index <= portalIndices.Length + 1; index++)
        {
            Vector3 nextLeft = goal, nextRight = goal;
            if (index <= portalIndices.Length)
            {
                NavigationPortal portal = mesh.Portals.Span[portalIndices[index - 1]];
                nextLeft = portal.Left;
                nextRight = portal.Right;
            }

            if (AreaXZ(apex, right, nextRight) >= 0)
            {
                if (SameXZ(apex, right) || AreaXZ(apex, left, nextRight) < 0)
                {
                    right = nextRight;
                    rightIndex = index;
                }
                else
                {
                    Append(mesh, corridor[Math.Min(leftIndex, corridor.Length - 1)], left, corners);
                    apex = left;
                    index = leftIndex;
                    left = right = apex;
                    rightIndex = leftIndex;
                    continue;
                }
            }

            if (AreaXZ(apex, left, nextLeft) <= 0)
            {
                if (SameXZ(apex, left) || AreaXZ(apex, right, nextLeft) > 0)
                {
                    left = nextLeft;
                    leftIndex = index;
                }
                else
                {
                    Append(mesh, corridor[Math.Min(rightIndex, corridor.Length - 1)], right, corners);
                    apex = right;
                    index = rightIndex;
                    left = right = apex;
                    leftIndex = rightIndex;
                }
            }
        }
        Append(mesh, corridor[^1], goal, corners);
        return corners.ToArray();
    }

    private static void Append(NavigationMesh mesh, int polygon, Vector3 point, List<Vector3> corners)
    {
        if (mesh.TrySampleHeight(polygon, point, out float height))
            point.Y = height;
        if (Vector3.DistanceSquared(corners[^1], point) > 0.00000001f)
            corners.Add(point);
    }

    private static float AreaXZ(Vector3 apex, Vector3 first, Vector3 second) =>
        (first.X - apex.X) * (second.Z - apex.Z) - (first.Z - apex.Z) * (second.X - apex.X);

    private static bool SameXZ(Vector3 first, Vector3 second)
    {
        float x = first.X - second.X, z = first.Z - second.Z;
        return x * x + z * z <= 0.00000001f;
    }
}
