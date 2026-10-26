// Original scene creation values translated to the common ECS physics inputs.
// Source: codex/job331-380, PhysicsSceneCatalog.cs, References/{2d,3d}/source/scenes.h,
// References/{2d,3d}/source/rigid.cpp and Native/Bridge.cpp demo_create/demo_input_bodies.
// Body, constraint and event indices are resolved once by PhysicsScenes.CreateCpu.
// Copyright (c) 2025 Chris Giles.
// Copyright (c) 2026 Chris Giles. Permission to use, copy, modify, distribute and sell
// this software and its documentation for any purpose is hereby granted without fee,
// provided that the above copyright notice appear in all copies. The software is provided
// "as is" without express or implied warranty; the author makes no representations about
// its suitability for any purpose.
using System.Numerics;

namespace DefaultGame;

public static partial class PhysicsScenes
{
    internal static PhysicsSceneData Create(PhysicsSceneDescription description)
    {
        if (description.Scene >= 400) return PhysicsScaleScenes.Create(description);
        PhysicsSceneData input = description.Scene >= 200 ? Create((PhysicsScene)description.Scene)
            : CreateOriginal(description.Dimension, description.Scene);
        if (description.DisturbanceFrame >= 0)
        {
            double[] velocity = description.Id == "2d-stack-disturbed" ? [2, 0, 0] : [0, 2, 0];
            input = input with { Events = [.. input.Events,
                new(description.DisturbanceFrame, input.Bodies.Length - 1, velocity)] };
        }
        return input;
    }

    private static PhysicsSceneData CreateOriginal(int dimension, int scene)
    {
        var bodies = new OriginalSceneBodies(dimension);
        if (scene is 4 or 104) Pyramid(bodies, scene == 104);
        else if (dimension == 2 && scene == 11 || dimension == 3 && scene == 9) Stack(bodies);
        else if (scene is >= 100 and <= 102) HorizontalChain(bodies, scene);
        else if (scene == 103) BranchedChain(bodies);
        else HeavyChain(bodies, dimension == 2 && scene == 8);
        PhysicsSceneData input = bodies.Build();
        if (scene == 101) input = input with { DistanceRanges = CreateManualDistances() };
        if (scene == 102) input = input with { Events = [new(90, RemovedJoint: 0)] };
        return input;
    }

    private static void Stack(OriginalSceneBodies world)
    {
        world.Box(new(0, 0, 0), world.Dimension == 2 ? new(100, 1, .25f) : new(100, 100, 1), 0);
        int count = world.Dimension == 2 ? 20 : 10;
        for (int index = 0; index < count; index++)
        {
            Vector3 position = world.Dimension == 2 ? new(0, index * 2f + 1, 0) : new(0, 0, index * 1.5f + 1);
            world.Box(position, world.Dimension == 2 ? new(1, 1, .25f) : Vector3.One, 1);
        }
    }

    private static void Pyramid(OriginalSceneBodies world, bool resting)
    {
        int size = world.Dimension == 2 ? 20 : 16;
        Vector3 ground = world.Dimension == 2 ? new(0, -2, 0) : new(0, 0, -.5f);
        Vector3 groundSize = world.Dimension == 2 ? new(100, .5f, .25f) : new(100, 100, 1);
        world.Box(ground, groundSize, 0);
        for (int row = 0; row < size; row++)
            for (int column = 0; column < size - row; column++)
            {
                float x = column * (world.Dimension == 2 ? 1.1f : 1.01f) + row * .5f - size / 2f;
                float height = row * .85f + (world.Dimension == 2 ? 0 : .5f);
                if (resting) height = world.Dimension == 2 ? -1.75f + .5f * (row + .5f) : .5f * (row + .5f);
                Vector3 position = world.Dimension == 2 ? new(x, height, 0) : new(x, 0, height);
                world.Box(position, world.Dimension == 2 ? new(1, .5f, .25f) : new(1, .5f, .5f), 1);
            }
    }

    private static void HorizontalChain(OriginalSceneBodies world, int scene)
    {
        for (int index = 0; index < 20; index++)
        {
            Vector3 position = world.Dimension == 2 ? new(index, 10, 0) : new(index, 0, 10);
            int body = world.Box(position, world.Dimension == 2 ? new(1, .5f, .25f) : new(1, .5f, .5f),
                scene != 100 && index == 0 ? 0 : 1);
            if (index > 0) world.Link(body - 1, body, new(.5f, 0, 0), new(-.5f, 0, 0));
        }
    }

    private static void HeavyChain(OriginalSceneBodies world, bool hanging)
    {
        if (world.Dimension == 3) world.Box(new(0, 0, -20), new(100, 100, 1), 0);
        int count = hanging ? 50 : 20;
        float size = world.Dimension == 3 ? 5 : hanging ? 10 : 30;
        Vector3 linkSize = hanging ? new(.5f, 1, .25f)
            : world.Dimension == 2 ? new(1, .5f, .25f) : new(1, .5f, .5f);
        Vector3 lastSize = new(size, size, world.Dimension == 2 ? .25f : size);
        Vector3 direction = hanging ? -Vector3.UnitY : Vector3.UnitX;
        Vector3 start = world.Dimension == 2 ? new(0, 10, 0) : new(0, 0, 10);
        for (int index = 0; index < count; index++)
        {
            bool last = index == count - 1;
            Vector3 position = start + direction * (index + (last ? size / 2 : 0));
            int body = world.Box(position, last ? lastSize : linkSize, index == 0 ? 0 : 1);
            if (index > 0) world.Link(body - 1, body, direction * .5f, -direction * (last ? size / 2 : .5f));
        }
        world.AlignAnchors();
    }

    private static void BranchedChain(OriginalSceneBodies world)
    {
        for (int index = 0; index < 8; index++)
        {
            int body = world.Box(new(index, 0, 10), new(1, .3f, .3f), index == 0 ? 0 : 1);
            if (index > 0) world.Link(body - 1, body, new(.5f, 0, 0), new(-.5f, 0, 0));
        }
        int previous = 3;
        for (int index = 0; index < 4; index++)
        {
            int body = world.Box(new(3, .65f + index, 10), new(.3f, 1, .3f), 1);
            world.Link(previous, body, index == 0 ? new(0, .15f, 0) : new(0, .5f, 0), new(0, -.5f, 0));
            previous = body;
        }
    }

    private sealed class OriginalSceneBodies(int dimension)
    {
        private readonly List<OriginalSceneBody> _bodies = [];
        private readonly List<SceneJointData> _joints = [];
        internal int Dimension => dimension;

        internal int Box(Vector3 position, Vector3 size, float density)
        {
            float mass = dimension == 2 ? size.X * size.Y * density : size.X * size.Y * size.Z * density;
            Vector3 inertia = dimension == 2 ? new(0, 0, mass * (size.X * size.X + size.Y * size.Y) / 12f)
                : new((size.Y * size.Y + size.Z * size.Z) / 12f * mass,
                    (size.X * size.X + size.Z * size.Z) / 12f * mass,
                    (size.X * size.X + size.Y * size.Y) / 12f * mass);
            _bodies.Add(new(position, size, mass, inertia));
            return _bodies.Count - 1;
        }

        internal void Link(int first, int second, Vector3 firstAnchor, Vector3 secondAnchor) =>
            _joints.Add(new(first, second, [firstAnchor.X, firstAnchor.Y, firstAnchor.Z],
                [secondAnchor.X, secondAnchor.Y, secondAnchor.Z], false, _joints.Count));

        internal void AlignAnchors()
        {
            foreach (SceneJointData joint in _joints)
            {
                OriginalSceneBody first = _bodies[joint.BodyA], second = _bodies[joint.BodyB];
                Vector3 firstAnchor = new((float)joint.PointA[0], (float)joint.PointA[1], (float)joint.PointA[2]);
                Vector3 secondAnchor = new((float)joint.PointB[0], (float)joint.PointB[1], (float)joint.PointB[2]);
                Vector3 firstPoint = first.Position + firstAnchor, secondPoint = second.Position + secondAnchor;
                _bodies[joint.BodyB] = second with { Position = second.Position + (firstPoint - secondPoint) };
            }
        }

        internal PhysicsSceneData Build() => new(1f / 60f, dimension == 2 ? [0, -10, 0] : [0, 0, -10],
            _bodies.Select(body => new SceneBodyData([body.Position.X, body.Position.Y, body.Position.Z], [0, 0, 0, 1],
                [body.Size.X, body.Size.Y, body.Size.Z], body.Mass, [body.Inertia.X, body.Inertia.Y, body.Inertia.Z],
                .5, [0, 0, 0], [0, 0, 0])).ToArray(), _joints.ToArray(), [],
            _joints.Select(joint => new SceneCollisionPair(joint.BodyA, joint.BodyB)).ToArray());
    }

    private readonly record struct OriginalSceneBody(Vector3 Position, Vector3 Size, float Mass, Vector3 Inertia);
}
