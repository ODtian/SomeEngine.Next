// Dubins equations adapted from OMPL's DubinsStateSpace.cpp (Mark Moll), in the engine's XZ convention.
// https://github.com/ompl/ompl/blob/main/src/ompl/base/spaces/src/DubinsStateSpace.cpp
// Copyright (c) 2010, Rice University. All rights reserved.
// Redistribution and use in source and binary forms, with or without modification, are permitted provided that:
// * Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
// * Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following
//   disclaimer in the documentation and/or other materials provided with the distribution.
// * Neither the name of the Rice University nor the names of its contributors may be used to endorse or promote products
//   derived from this software without specific prior written permission.
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
// INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
// SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
// WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

using System.Numerics;

namespace SomeEngine.Navigation;

internal static class NavigationDubinsConnection
{
    internal static List<NavigationVehicleControl[]> Create(NavigationPose start, NavigationPose goal,
        NavigationVehicleModel model, NavigationProfile profile)
    {
        var result = new List<NavigationVehicleControl[]>();
        double radius = model.MinimumTurningRadius;
        Vector3 offset = goal.Position - start.Position;
        double bearing = Math.Atan2(offset.X, offset.Z), distance = Math.Sqrt(offset.X * offset.X + offset.Z * offset.Z) / radius;
        for (int direction = model.AllowReverse ? -1 : 1; direction <= 1; direction += 2)
        {
            double reverse = direction < 0 ? Math.PI : 0;
            double alpha = Wrap(start.Heading + reverse - bearing), beta = Wrap(goal.Heading + reverse - bearing);
            double sa = Math.Sin(alpha), sb = Math.Sin(beta), ca = Math.Cos(alpha), cb = Math.Cos(beta), ab = Math.Cos(alpha - beta);
            double squared = 2 + distance * distance - 2 * ab + 2 * distance * (sa - sb);
            if (squared >= 0)
            {
                double turn = Math.Atan2(cb - ca, distance + sa - sb);
                Add(1, 0, 1, Wrap(-alpha + turn), Math.Sqrt(squared), Wrap(beta - turn));
            }
            squared = 2 + distance * distance - 2 * ab + 2 * distance * (sb - sa);
            if (squared >= 0)
            {
                double turn = Math.Atan2(ca - cb, distance - sa + sb);
                Add(-1, 0, -1, Wrap(alpha - turn), Math.Sqrt(squared), Wrap(-beta + turn));
            }
            squared = distance * distance - 2 + 2 * ab - 2 * distance * (sa + sb);
            if (squared >= 0)
            {
                double straight = Math.Sqrt(squared), turn = Math.Atan2(ca + cb, distance - sa - sb) - Math.Atan2(2, straight);
                Add(-1, 0, 1, Wrap(alpha - turn), straight, Wrap(beta - turn));
            }
            squared = distance * distance - 2 + 2 * ab + 2 * distance * (sa + sb);
            if (squared >= 0)
            {
                double straight = Math.Sqrt(squared), turn = Math.Atan2(-ca - cb, distance + sa + sb) - Math.Atan2(-2, straight);
                Add(1, 0, -1, Wrap(-alpha + turn), straight, Wrap(-beta + turn));
            }
            double cosine = (6 - distance * distance + 2 * ab + 2 * distance * (sa - sb)) / 8;
            if (Math.Abs(cosine) <= 1)
            {
                double middle = Math.Tau - Math.Acos(cosine), first = Wrap(alpha - Math.Atan2(ca - cb, distance - sa + sb) + middle / 2);
                Add(-1, 1, -1, first, middle, Wrap(alpha - beta - first + middle));
            }
            cosine = (6 - distance * distance + 2 * ab - 2 * distance * (sa - sb)) / 8;
            if (Math.Abs(cosine) <= 1)
            {
                double middle = Math.Tau - Math.Acos(cosine), first = Wrap(-alpha + Math.Atan2(-ca + cb, distance + sa - sb) + middle / 2);
                Add(1, -1, 1, first, middle, Wrap(beta - alpha - first + middle));
            }

            void Add(int first, int middle, int last, double firstLength, double middleLength, double lastLength)
            {
                var controls = new List<NavigationVehicleControl>();
                Segment(first, firstLength); Segment(middle, middleLength); Segment(last, lastLength);
                result.Add(controls.ToArray());
                void Segment(int turn, double length)
                {
                    if (length <= 0.000001) return;
                    float curvature = direction * turn / model.MinimumTurningRadius;
                    float speed = profile.MaximumSpeed;
                    if (curvature != 0) speed = MathF.Min(speed, profile.MaximumAngularSpeed / MathF.Abs(curvature));
                    controls.Add(new(direction * speed, curvature, (float)(length * radius / speed)));
                }
            }
        }
        return result;
    }

    private static double Wrap(double angle)
    {
        double value = angle - Math.Tau * Math.Floor(angle / Math.Tau);
        return Math.Tau - value < 0.000001 ? 0 : value;
    }
}
