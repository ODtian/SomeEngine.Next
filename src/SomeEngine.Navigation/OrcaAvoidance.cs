// Adapted from the RVO2 C# Library, Agent.cs (8e7cf2f9bdc331fe0802cdfb10d6c19fb5b20745).
// SPDX-FileCopyrightText: 2008 University of North Carolina at Chapel Hill
// SPDX-License-Identifier: Apache-2.0
// Authors: Jur van den Berg, Stephen J. Guy, Jamie Snape, Ming C. Lin, Dinesh Manocha.
// Changes: System.Numerics, ordinary sampled data, per-boundary baked radius, weighted pair responsibility,
// and reusable per-actor constraint buffers. See ThirdParty/RVO2/NOTICE and LICENSE.

using System.Numerics;

namespace SomeEngine.Navigation;

/// <summary>Finite-horizon reciprocal velocity obstacles for XZ disks and directed static boundary segments.</summary>
public readonly struct OrcaAvoidance : INavigationAvoidanceSolver
{
    public static Vector2 Solve(in NavigationAvoidanceInput input, NavigationAvoidanceWorkspace workspace)
    {
        List<NavigationVelocityConstraint> lines = workspace.Constraints;
        lines.Clear();
        Vector2 position = Xz(input.Self.Motion.Position);
        Vector2 velocity = Xz(input.Self.Motion.Velocity);

        float invTimeHorizonObst = 1.0f / input.Self.Avoidance.BoundaryTimeHorizon;

        /* Create obstacle ORCA lines. */
        for (int i = 0; i < input.Boundaries.Length; ++i)
        {

            NavigationBoundarySegment boundary = input.Boundaries[i];
            Vector2 direction = Vector2.Normalize(Xz(boundary.End - boundary.Start));
            var obstacle1 = new Vertex(Xz(boundary.Start), direction, -direction);
            var obstacle2 = new Vertex(Xz(boundary.End), -direction, direction);
            float radius = MathF.Max(0, input.Self.Radius - boundary.BakedRadius);

            Vector2 relativePosition1 = obstacle1.Point - position;
            Vector2 relativePosition2 = obstacle2.Point - position;

            /*
             * Check if velocity obstacle of obstacle is already taken care
             * of by previously constructed obstacle ORCA lines.
             */
            bool alreadyCovered = false;

            for (int j = 0; j < lines.Count; ++j)
            {
                if (Cross(invTimeHorizonObst * relativePosition1 - lines[j].Point, lines[j].Direction) - invTimeHorizonObst * radius >= -0.00001f && Cross(invTimeHorizonObst * relativePosition2 - lines[j].Point, lines[j].Direction) - invTimeHorizonObst * radius >= -0.00001f)
                {
                    alreadyCovered = true;

                    break;
                }
            }

            if (alreadyCovered)
            {
                continue;
            }

            /* Not yet covered. Check for collisions. */
            float distSq1 = LengthSquared(relativePosition1);
            float distSq2 = LengthSquared(relativePosition2);

            float radiusSq = radius * radius;

            Vector2 obstacleVector = obstacle2.Point - obstacle1.Point;
            float s = (Vector2.Dot(-relativePosition1, obstacleVector)) / LengthSquared(obstacleVector);
            float distSqLine = LengthSquared(-relativePosition1 - s * obstacleVector);

            NavigationVelocityConstraint line;

            if (s < 0.0f && distSq1 <= radiusSq)
            {
                /* Collision with left vertex. Ignore if non-convex. */
                if (obstacle1.Convex)
                {
                    line.Point = new Vector2(0.0f, 0.0f);
                    line.Direction = Vector2.Normalize(new Vector2(-relativePosition1.Y, relativePosition1.X));
                    lines.Add(line);
                }

                continue;
            }
            else if (s > 1.0f && distSq2 <= radiusSq)
            {
                /*
                 * Collision with right vertex. Ignore if non-convex or if
                 * it will be taken care of by neighboring obstacle.
                 */
                if (obstacle2.Convex && Cross(relativePosition2, obstacle2.Direction) >= 0.0f)
                {
                    line.Point = new Vector2(0.0f, 0.0f);
                    line.Direction = Vector2.Normalize(new Vector2(-relativePosition2.Y, relativePosition2.X));
                    lines.Add(line);
                }

                continue;
            }
            else if (s >= 0.0f && s <= 1.0f && distSqLine <= radiusSq)
            {
                /* Collision with obstacle segment. */
                line.Point = new Vector2(0.0f, 0.0f);
                line.Direction = -obstacle1.Direction;
                lines.Add(line);

                continue;
            }

            /*
             * No collision. Compute legs. When obliquely viewed, both legs
             * can come from a single vertex. Legs extend cut-off line when
             * non-convex vertex.
             */

            Vector2 leftLegDirection, rightLegDirection;

            if (s < 0.0f && distSqLine <= radiusSq)
            {
                /*
                 * Obstacle viewed obliquely so that left vertex
                 * defines velocity obstacle.
                 */
                if (!obstacle1.Convex)
                {
                    /* Ignore obstacle. */
                    continue;
                }

                obstacle2 = obstacle1;

                float leg1 = MathF.Sqrt(distSq1 - radiusSq);
                leftLegDirection = new Vector2(relativePosition1.X * leg1 - relativePosition1.Y * radius, relativePosition1.X * radius + relativePosition1.Y * leg1) / distSq1;
                rightLegDirection = new Vector2(relativePosition1.X * leg1 + relativePosition1.Y * radius, -relativePosition1.X * radius + relativePosition1.Y * leg1) / distSq1;
            }
            else if (s > 1.0f && distSqLine <= radiusSq)
            {
                /*
                 * Obstacle viewed obliquely so that
                 * right vertex defines velocity obstacle.
                 */
                if (!obstacle2.Convex)
                {
                    /* Ignore obstacle. */
                    continue;
                }

                obstacle1 = obstacle2;

                float leg2 = MathF.Sqrt(distSq2 - radiusSq);
                leftLegDirection = new Vector2(relativePosition2.X * leg2 - relativePosition2.Y * radius, relativePosition2.X * radius + relativePosition2.Y * leg2) / distSq2;
                rightLegDirection = new Vector2(relativePosition2.X * leg2 + relativePosition2.Y * radius, -relativePosition2.X * radius + relativePosition2.Y * leg2) / distSq2;
            }
            else
            {
                /* Usual situation. */
                if (obstacle1.Convex)
                {
                    float leg1 = MathF.Sqrt(distSq1 - radiusSq);
                    leftLegDirection = new Vector2(relativePosition1.X * leg1 - relativePosition1.Y * radius, relativePosition1.X * radius + relativePosition1.Y * leg1) / distSq1;
                }
                else
                {
                    /* Left vertex non-convex; left leg extends cut-off line. */
                    leftLegDirection = -obstacle1.Direction;
                }

                if (obstacle2.Convex)
                {
                    float leg2 = MathF.Sqrt(distSq2 - radiusSq);
                    rightLegDirection = new Vector2(relativePosition2.X * leg2 + relativePosition2.Y * radius, -relativePosition2.X * radius + relativePosition2.Y * leg2) / distSq2;
                }
                else
                {
                    /* Right vertex non-convex; right leg extends cut-off line. */
                    rightLegDirection = obstacle1.Direction;
                }
            }

            /*
             * Legs can never point into neighboring edge when convex
             * vertex, take cutoff-line of neighboring edge instead. If
             * velocity projected on "foreign" leg, no constraint is added.
             */

            Vector2 leftNeighborDirection = obstacle1.PreviousDirection;

            bool isLeftLegForeign = false;
            bool isRightLegForeign = false;

            if (obstacle1.Convex && Cross(leftLegDirection, -leftNeighborDirection) >= 0.0f)
            {
                /* Left leg points into obstacle. */
                leftLegDirection = -leftNeighborDirection;
                isLeftLegForeign = true;
            }

            if (obstacle2.Convex && Cross(rightLegDirection, obstacle2.Direction) <= 0.0f)
            {
                /* Right leg points into obstacle. */
                rightLegDirection = obstacle2.Direction;
                isRightLegForeign = true;
            }

            /* Compute cut-off centers. */
            Vector2 leftCutOff = invTimeHorizonObst * (obstacle1.Point - position);
            Vector2 rightCutOff = invTimeHorizonObst * (obstacle2.Point - position);
            Vector2 cutOffVector = rightCutOff - leftCutOff;

            /* Project current velocity on velocity obstacle. */

            /* Check if current velocity is projected on cutoff circles. */
            float t = obstacle1 == obstacle2 ? 0.5f : (Vector2.Dot((velocity - leftCutOff), cutOffVector)) / LengthSquared(cutOffVector);
            float tLeft = Vector2.Dot((velocity - leftCutOff), leftLegDirection);
            float tRight = Vector2.Dot((velocity - rightCutOff), rightLegDirection);

            if ((t < 0.0f && tLeft < 0.0f) || (obstacle1 == obstacle2 && tLeft < 0.0f && tRight < 0.0f))
            {
                /* Project on left cut-off circle. */
                Vector2 unitW = Vector2.Normalize(velocity - leftCutOff);

                line.Direction = new Vector2(unitW.Y, -unitW.X);
                line.Point = leftCutOff + radius * invTimeHorizonObst * unitW;
                lines.Add(line);

                continue;
            }
            else if (t > 1.0f && tRight < 0.0f)
            {
                /* Project on right cut-off circle. */
                Vector2 unitW = Vector2.Normalize(velocity - rightCutOff);

                line.Direction = new Vector2(unitW.Y, -unitW.X);
                line.Point = rightCutOff + radius * invTimeHorizonObst * unitW;
                lines.Add(line);

                continue;
            }

            /*
             * Project on left leg, right leg, or cut-off line, whichever is
             * closest to velocity.
             */
            float distSqCutoff = (t < 0.0f || t > 1.0f || obstacle1 == obstacle2) ? float.PositiveInfinity : LengthSquared(velocity - (leftCutOff + t * cutOffVector));
            float distSqLeft = tLeft < 0.0f ? float.PositiveInfinity : LengthSquared(velocity - (leftCutOff + tLeft * leftLegDirection));
            float distSqRight = tRight < 0.0f ? float.PositiveInfinity : LengthSquared(velocity - (rightCutOff + tRight * rightLegDirection));

            if (distSqCutoff <= distSqLeft && distSqCutoff <= distSqRight)
            {
                /* Project on cut-off line. */
                line.Direction = -obstacle1.Direction;
                line.Point = leftCutOff + radius * invTimeHorizonObst * new Vector2(-line.Direction.Y, line.Direction.X);
                lines.Add(line);

                continue;
            }

            if (distSqLeft <= distSqRight)
            {
                /* Project on left leg. */
                if (isLeftLegForeign)
                {
                    continue;
                }

                line.Direction = leftLegDirection;
                line.Point = leftCutOff + radius * invTimeHorizonObst * new Vector2(-line.Direction.Y, line.Direction.X);
                lines.Add(line);

                continue;
            }

            /* Project on right leg. */
            if (isRightLegForeign)
            {
                continue;
            }

            line.Direction = -rightLegDirection;
            line.Point = rightCutOff + radius * invTimeHorizonObst * new Vector2(-line.Direction.Y, line.Direction.X);
            lines.Add(line);
        }

        int numObstLines = lines.Count;

        float invTimeHorizon = 1.0f / input.Self.Avoidance.TimeHorizon;

        /* Create agent ORCA lines. */
        for (int i = 0; i < input.Neighbors.Length; ++i)
        {
            NavigationNeighborAgent other = input.Agents[input.Neighbors[i]];

            Vector2 relativePosition = Xz(other.Motion.Position) - position;
            Vector2 relativeVelocity = velocity - Xz(other.Motion.Velocity);
            float distSq = LengthSquared(relativePosition);
            float combinedRadius = input.Self.Radius + other.Radius;
            float combinedRadiusSq = combinedRadius * combinedRadius;

            NavigationVelocityConstraint line;
            Vector2 u;

            if (distSq > combinedRadiusSq)
            {
                /* No collision. */
                Vector2 w = relativeVelocity - invTimeHorizon * relativePosition;

                /* Vector from cutoff center to relative velocity. */
                float wLengthSq = LengthSquared(w);
                float dotProduct1 = Vector2.Dot(w, relativePosition);

                if (dotProduct1 < 0.0f && dotProduct1 * dotProduct1 > combinedRadiusSq * wLengthSq)
                {
                    /* Project on cut-off circle. */
                    float wLength = MathF.Sqrt(wLengthSq);
                    Vector2 unitW = w / wLength;

                    line.Direction = new Vector2(unitW.Y, -unitW.X);
                    u = (combinedRadius * invTimeHorizon - wLength) * unitW;
                }
                else
                {
                    /* Project on legs. */
                    float leg = MathF.Sqrt(distSq - combinedRadiusSq);

                    if (Cross(relativePosition, w) > 0.0f)
                    {
                        /* Project on left leg. */
                        line.Direction = new Vector2(relativePosition.X * leg - relativePosition.Y * combinedRadius, relativePosition.X * combinedRadius + relativePosition.Y * leg) / distSq;
                    }
                    else
                    {
                        /* Project on right leg. */
                        line.Direction = -new Vector2(relativePosition.X * leg + relativePosition.Y * combinedRadius, -relativePosition.X * combinedRadius + relativePosition.Y * leg) / distSq;
                    }

                    float dotProduct2 = Vector2.Dot(relativeVelocity, line.Direction);
                    u = dotProduct2 * line.Direction - relativeVelocity;
                }
            }
            else
            {
                /* Collision. Project on cut-off circle of time timeStep. */
                float invTimeStep = 1.0f / input.DeltaTime;

                /* Vector from cutoff center to relative velocity. */
                Vector2 w = relativeVelocity - invTimeStep * relativePosition;

                float wLength = Length(w);
                Vector2 unitW = w / wLength;

                line.Direction = new Vector2(unitW.Y, -unitW.X);
                u = (combinedRadius * invTimeStep - wLength) * unitW;
            }

            line.Point = velocity + input.Self.Avoidance.Responsibility / (input.Self.Avoidance.Responsibility + other.Avoidance.Responsibility) * u;
            lines.Add(line);
        }

        return NavigationVelocitySolver.Solve(lines, numObstLines, input.Self.Profile.MaximumSpeed, input.PreferredVelocity, workspace.ProjectedConstraints);
    }
    private static float LengthSquared(Vector2 value) => value.LengthSquared();
    private static float Length(Vector2 value) => value.Length();
    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    private static Vector2 Xz(Vector3 value) => new(value.X, value.Z);
    private readonly record struct Vertex(Vector2 Point, Vector2 Direction, Vector2 PreviousDirection)
    {
        public bool Convex => true;
    }
}
