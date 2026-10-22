// Adapted from the RVO2 C# Library, Agent.cs (8e7cf2f9bdc331fe0802cdfb10d6c19fb5b20745).
// SPDX-FileCopyrightText: 2008 University of North Carolina at Chapel Hill
// SPDX-License-Identifier: Apache-2.0
// Authors: Jur van den Berg, Stephen J. Guy, Jamie Snape, Ming C. Lin, Dinesh Manocha.
// Changes: System.Numerics, ordinary sampled data, per-boundary baked radius, weighted pair responsibility,
// and reusable per-actor constraint buffers. See ThirdParty/RVO2/NOTICE and LICENSE.

using System.Numerics;

namespace SomeEngine.Navigation;

internal static class NavigationVelocitySolver
{
    internal static Vector2 Solve(List<NavigationVelocityConstraint> lines, int boundaryCount, float maximumSpeed,
        Vector2 preferredVelocity, List<NavigationVelocityConstraint> projected) =>
        Solve(lines, boundaryCount, new NavigationVelocityDisks(maximumSpeed), preferredVelocity, projected);

    internal static Vector2 Solve(List<NavigationVelocityConstraint> lines, int boundaryCount, NavigationVelocityDisks disks,
        Vector2 preferredVelocity, List<NavigationVelocityConstraint> projected)
    {
        int failed = SolveVelocity(lines, disks, preferredVelocity, false, out Vector2 result);
        if (failed < lines.Count) SolveProjected(lines, boundaryCount, failed, disks, projected, ref result);
        return result;
    }

    private static bool SolveLine(IList<NavigationVelocityConstraint> lines, int lineNo, NavigationVelocityDisks disks,
        Vector2 optVelocity, bool directionOpt, ref Vector2 result)
    {
        if (!disks.ClipLine(lines[lineNo].Point, lines[lineNo].Direction, out float tLeft, out float tRight)) return false;

        for (int i = 0; i < lineNo; ++i)
        {
            float denominator = Cross(lines[lineNo].Direction, lines[i].Direction);
            float numerator = Cross(lines[i].Direction, lines[lineNo].Point - lines[i].Point);

            if (MathF.Abs(denominator) <= 0.00001f)
            {
                /* Lines lineNo and i are (almost) parallel. */
                if (numerator < 0.0f)
                {
                    return false;
                }

                continue;
            }

            float t = numerator / denominator;

            if (denominator >= 0.0f)
            {
                /* NavigationVelocityConstraint i bounds line lineNo on the right. */
                tRight = Math.Min(tRight, t);
            }
            else
            {
                /* NavigationVelocityConstraint i bounds line lineNo on the left. */
                tLeft = Math.Max(tLeft, t);
            }

            if (tLeft > tRight)
            {
                return false;
            }
        }

        if (directionOpt)
        {
            /* Optimize direction. */
            if (Vector2.Dot(optVelocity, lines[lineNo].Direction) > 0.0f)
            {
                /* Take right extreme. */
                result = lines[lineNo].Point + tRight * lines[lineNo].Direction;
            }
            else
            {
                /* Take left extreme. */
                result = lines[lineNo].Point + tLeft * lines[lineNo].Direction;
            }
        }
        else
        {
            /* Optimize closest point. */
            float t = Vector2.Dot(lines[lineNo].Direction, (optVelocity - lines[lineNo].Point));

            if (t < tLeft)
            {
                result = lines[lineNo].Point + tLeft * lines[lineNo].Direction;
            }
            else if (t > tRight)
            {
                result = lines[lineNo].Point + tRight * lines[lineNo].Direction;
            }
            else
            {
                result = lines[lineNo].Point + t * lines[lineNo].Direction;
            }
        }

        return true;
    }

    private static int SolveVelocity(IList<NavigationVelocityConstraint> lines, NavigationVelocityDisks disks,
        Vector2 optVelocity, bool directionOpt, out Vector2 result)
    {
        result = disks.Project(optVelocity, directionOpt);

        for (int i = 0; i < lines.Count; ++i)
        {
            if (Cross(lines[i].Direction, lines[i].Point - result) > 0.0f)
            {
                /* Result does not satisfy constraint i. Compute new optimal result. */
                Vector2 tempResult = result;
                if (!SolveLine(lines, i, disks, optVelocity, directionOpt, ref result))
                {
                    result = tempResult;

                    return i;
                }
            }
        }

        return lines.Count;
    }

    private static void SolveProjected(IList<NavigationVelocityConstraint> lines, int numObstLines, int beginLine,
        NavigationVelocityDisks disks, List<NavigationVelocityConstraint> projected, ref Vector2 result)
    {
        float distance = 0.0f;

        for (int i = beginLine; i < lines.Count; ++i)
        {
            if (Cross(lines[i].Direction, lines[i].Point - result) > distance)
            {
                /* Result does not satisfy constraint of line i. */
                List<NavigationVelocityConstraint> projLines = projected;
                projLines.Clear();
                for (int ii = 0; ii < numObstLines; ++ii)
                {
                    projLines.Add(lines[ii]);
                }

                for (int j = numObstLines; j < i; ++j)
                {
                    NavigationVelocityConstraint line;

                    float determinant = Cross(lines[i].Direction, lines[j].Direction);

                    if (MathF.Abs(determinant) <= 0.00001f)
                    {
                        /* NavigationVelocityConstraint i and line j are parallel. */
                        if (Vector2.Dot(lines[i].Direction, lines[j].Direction) > 0.0f)
                        {
                            /* NavigationVelocityConstraint i and line j point in the same direction. */
                            continue;
                        }
                        else
                        {
                            /* NavigationVelocityConstraint i and line j point in opposite direction. */
                            line.Point = 0.5f * (lines[i].Point + lines[j].Point);
                        }
                    }
                    else
                    {
                        line.Point = lines[i].Point + (Cross(lines[j].Direction, lines[i].Point - lines[j].Point) / determinant) * lines[i].Direction;
                    }

                    line.Direction = Vector2.Normalize(lines[j].Direction - lines[i].Direction);
                    projLines.Add(line);
                }

                Vector2 tempResult = result;
                if (SolveVelocity(projLines, disks, new Vector2(-lines[i].Direction.Y, lines[i].Direction.X), true, out result) < projLines.Count)
                {
                    /*
                     * This should in principle not happen. The result is by
                     * definition already in the feasible region of this
                     * linear program. If it fails, it is due to small
                     * floating point error, and the current result is kept.
                     */
                    result = tempResult;
                }

                distance = Cross(lines[i].Direction, lines[i].Point - result);
            }
        }
    }
    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

}
