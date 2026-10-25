using System.Numerics;
using SomeEngine.Clothing.Demo;
using Xunit;
using Xunit.Abstractions;

namespace SomeEngine.Clothing.Tests;

public sealed class ClothingBehaviorTests(ITestOutputHelper output)
{
    [Fact]
    public void WornGarmentsKeepTheirVerticesOutsideTheBody()
    {
        var scene = new ClothingScene();
        foreach (ClothingSimulation garment in new[] { scene.Briefs, scene.Tights })
        {
            float minimum = garment.Positions.Min(point => scene.BodyGeometry.FindContact(point).MinimumSeparation(point));
            output.WriteLine($"{garment.Mesh.Name}: worn body separation {minimum * 1000:F2} mm");
            Assert.InRange(minimum, -.001f, .05f);
        }
    }

    [Fact]
    public void RunningMovesAttachedClothingAndKeepsTheCrotchConnected()
    {
        var scene = new ClothingScene();
        int vertex = Array.FindIndex(scene.Tights.Mesh.Vertices, point => point.Y is > .4f and < .55f && point.X > .14f);
        Vector3 initial = scene.Tights.Positions[vertex];
        float displacement = 0, briefsStretch = 1, tightsStretch = 1;
        for (int frame = 0; frame < 120; frame++)
        {
            scene.Advance(new DemoControls { Running = .65f, Spread = .07f });
            displacement = MathF.Max(displacement, Vector3.Distance(initial, scene.Tights.Positions[vertex]));
            briefsStretch = MathF.Max(briefsStretch, scene.Briefs.MaximumStretch);
            tightsStretch = MathF.Max(tightsStretch, scene.Tights.MaximumStretch);
        }
        output.WriteLine($"Running: maximum briefs stretch {briefsStretch:F3}; maximum tights stretch {tightsStretch:F3}");
        Assert.True(displacement > .03f);
        Assert.InRange(briefsStretch, 1, 2.5f);
        Assert.InRange(tightsStretch, 1, 2.5f);
    }

    [Fact]
    public void GarmentsFallOntoTheFloorAfterLeavingTheTrajectory()
    {
        var scene = new ClothingScene();
        for (int frame = 0; frame < 420; frame++)
            scene.Advance(new DemoControls { Briefs = 1, Tights = 1, Spread = 0 });
        Assert.All(scene.Briefs.SimulationWeights, weight => Assert.Equal(1, weight));
        Assert.All(scene.Tights.SimulationWeights, weight => Assert.Equal(1, weight));
        Assert.InRange(scene.Briefs.Positions.Average(point => point.Y), .0009f, .15f);
        Assert.InRange(scene.Tights.Positions.Average(point => point.Y), .0009f, .15f);
    }

    [Fact]
    public void ResetRestoresTheWornClothing()
    {
        var scene = new ClothingScene();
        for (int frame = 0; frame < 200; frame++)
            scene.Advance(new DemoControls { Briefs = 1, Tights = 1 });
        scene.Advance(new DemoControls { Reset = true, Spread = 0 });
        var worn = new ClothingScene();
        worn.Advance(new DemoControls { Spread = 0 });
        Assert.Equal(0, scene.Briefs.Progress);
        Assert.Equal(0, scene.Tights.Progress);
        Assert.InRange(scene.Briefs.Positions.Select((point, index) => Vector3.Distance(point, worn.Briefs.Positions[index])).Max(), 0, .00001f);
        Assert.InRange(scene.Tights.Positions.Select((point, index) => Vector3.Distance(point, worn.Tights.Positions[index])).Max(), 0, .00001f);
    }

    [Fact]
    public void ReversingTheProgressReturnsBothGarmentsToTheBody()
    {
        var scene = new ClothingScene();
        float briefsHeight = scene.Briefs.Positions.Average(point => point.Y);
        float tightsHeight = scene.Tights.Positions.Average(point => point.Y);
        for (int frame = 0; frame < 300; frame++)
            scene.Advance(new DemoControls { Briefs = 1, Tights = 1, Running = .65f, Spread = .07f });
        for (int frame = 0; frame < 300; frame++)
            scene.Advance(new DemoControls { Running = .65f, Spread = .07f });
        Assert.Equal(0, scene.Briefs.Progress);
        Assert.Equal(0, scene.Tights.Progress);
        Assert.InRange(scene.Briefs.Positions.Average(point => point.Y), briefsHeight - .12f, briefsHeight + .12f);
        Assert.InRange(scene.Tights.Positions.Average(point => point.Y), tightsHeight - .12f, tightsHeight + .12f);
        Assert.InRange(scene.Briefs.MaximumStretch, 1, 2.5f);
        Assert.InRange(scene.Tights.MaximumStretch, 1, 2.5f);
    }

    [Fact]
    public void SlidingKeepsSupportedClothingOutsideTheBody()
    {
        var scene = new ClothingScene();
        for (int frame = 0; frame < 100; frame++)
            scene.Advance(new DemoControls { Briefs = .2f, Tights = .2f, Spread = .07f });
        float minimum = float.PositiveInfinity;
        foreach (ClothingSimulation garment in new[] { scene.Briefs, scene.Tights })
        for (int i = 0; i < garment.Positions.Length; i++)
        {
            if (garment.SimulationWeights[i] > .01f) continue;
            ClothingContact contact = scene.BodyGeometry.FindContact(garment.Positions[i]);
            minimum = MathF.Min(minimum, contact.MinimumSeparation(garment.Positions[i]));
        }
        output.WriteLine($"Sliding: closest supported separation {minimum * 1000:F2} mm");
        Assert.InRange(minimum, -.001f, .045f);
    }

    [Fact]
    public void SlidingKeepsSimulatedClothingOutsideTheMovingLegs()
    {
        var scene = new ClothingScene();
        for (int frame = 0; frame < 240; frame++)
            scene.Advance(new DemoControls { Briefs = .75f, Tights = .75f, Running = .65f, Spread = .07f });
        float minimum = float.PositiveInfinity;
        foreach (ClothingSimulation garment in new[] { scene.Briefs, scene.Tights })
        for (int i = 0; i < garment.Positions.Length; i++)
        {
            if (garment.SimulationWeights[i] == 0) continue;
            ClothingContact contact = scene.BodyGeometry.FindContact(garment.Positions[i]);
            minimum = MathF.Min(minimum, contact.MinimumSeparation(garment.Positions[i]));
        }
        output.WriteLine($"Sliding: closest simulated separation {minimum * 1000:F2} mm");
        Assert.InRange(minimum, -.001f, .045f);
    }

    [Fact]
    public void WornTightsEncloseTheFootSurface()
    {
        var scene = new ClothingScene();
        int crossings = CountSurfaceCrossings(scene, FootSurface(scene));
        output.WriteLine($"Worn foot surface crossings: {crossings}");
        Assert.Equal(0, crossings);
    }

    [Fact]
    public void SupportWeightsRespondToThePoseAtTheSameDressingProgress()
    {
        var scene = new ClothingScene();
        float travel = .25f * scene.Trajectory.TravelDistance;
        float[] initial = scene.Tights.Mesh.Bindings.Select(binding => scene.Trajectory.Sample(binding, travel, true).SimulationWeight).ToArray();
        scene.Body.Update(.28f, 1, .35f);
        scene.BodyGeometry.Update(scene.Body, true, ClothingScene.StepSeconds);
        scene.Trajectory.Update(scene.Body, scene.BodyGeometry);
        float change = scene.Tights.Mesh.Bindings.Select((binding, index) =>
            MathF.Abs(scene.Trajectory.Sample(binding, travel, true).SimulationWeight - initial[index])).Sum();
        output.WriteLine($"Support weight change at fixed progress: {change:F3}");
        Assert.True(change > 1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SlidingKeepsTightsSeparatedFromTheFeet(bool dualQuaternion)
    {
        var scene = new ClothingScene();
        var body = FootSurface(scene);
        output.WriteLine($"Clothing vertices: {scene.Briefs.Positions.Length} + {scene.Tights.Positions.Length}");
        for (int frame = 0; frame < 320; frame++)
        {
            scene.Advance(new DemoControls { Briefs = 1, Tights = 1, Running = .65f, Spread = .07f, DualQuaternion = dualQuaternion });
            Assert.True(float.IsFinite(scene.Tights.MaximumStretch), $"Finite cloth at frame {frame}");
            int footCrossings = CountSurfaceCrossings(scene, body);
            Assert.True(footCrossings == 0, $"Frame {frame}: foot surface crossings {footCrossings}");
        }
    }

    [Fact]
    public void RunningAtPartialProgressKeepsClothTrianglesOutsideTheBody()
    {
        var scene = new ClothingScene();
        for (int frame = 0; frame < 240; frame++)
        {
            scene.Advance(new DemoControls { Briefs = .35f, Tights = .35f, Running = .65f, Spread = .07f });
            int crossings = CountSurfaceCrossings(scene, scene.BodyGeometry.Surface);
            Assert.True(crossings == 0, $"Frame {frame}: body surface crossings {crossings}");
        }
    }

    private static SurfaceTriangles FootSurface(ClothingScene scene)
    {
        int[] triangles = Enumerable.Range(0, scene.BodyGeometry.Triangles.Length / 3)
            .Where(triangle => Enumerable.Range(0, 3).All(corner =>
                scene.Character.Body.Positions[scene.BodyGeometry.Triangles[triangle * 3 + corner]].Y < .12f))
            .SelectMany(triangle => scene.BodyGeometry.Triangles.Skip(triangle * 3).Take(3)).ToArray();
        return new(scene.BodyGeometry.Positions, triangles);
    }

    private static int CountSurfaceCrossings(ClothingScene scene, SurfaceTriangles body)
    {
        body.Update();
        List<SurfaceCrossing> crossings = [];
        int count = 0;
        ClothingSimulation tights = scene.Tights;
        for (int triangle = 0; triangle < tights.Mesh.Triangles.Length; triangle += 3)
        {
            int[] vertices = tights.Mesh.Triangles;
            crossings.Clear();
            body.FindCrossings(tights.Positions[vertices[triangle]], tights.Positions[vertices[triangle + 1]],
                tights.Positions[vertices[triangle + 2]], crossings);
            count += crossings.Count;
        }
        return count;
    }

    [Fact]
    public void RunningCrotchShapeAndMeanStretchStayCloseToTheHigherIterationResult()
    {
        var realtime = new ClothingScene();
        var reference = new ClothingScene();
        int[] crotch = Enumerable.Range(0, realtime.Tights.Positions.Length).Where(index =>
        {
            Vector3 point = realtime.Tights.Mesh.Vertices[index];
            return MathF.Abs(point.X) < .08f && point.Y > realtime.Character.CrotchHeight - .08f
                && point.Y < realtime.Character.CrotchHeight + .12f;
        }).ToArray();
        HashSet<int> crotchVertices = crotch.ToHashSet();
        (int A, int B)[] crotchEdges = realtime.Tights.Mesh.Edges
            .Where(edge => crotchVertices.Contains(edge.A) && crotchVertices.Contains(edge.B)).ToArray();
        float rms = 0, maximum = 0;
        float stretchRms = 0, maximumStretchDifference = 0;
        for (int frame = 0; frame < 120; frame++)
        {
            realtime.Advance(new DemoControls { Running = .65f, Spread = .07f });
            reference.Advance(new DemoControls { Running = .65f, Spread = .07f, Fine = true });
            if (frame < 30) continue;
            rms = MathF.Max(rms, MathF.Sqrt(crotch.Average(index => Vector3.DistanceSquared(realtime.Tights.Positions[index], reference.Tights.Positions[index]))));
            foreach (int index in crotch)
            {
                float difference = Vector3.Distance(realtime.Tights.Positions[index], reference.Tights.Positions[index]);
                maximum = MathF.Max(maximum, difference);
            }
            float squaredStretchDifference = 0;
            foreach ((int a, int b) in crotchEdges)
            {
                float restLength = Vector3.Distance(realtime.Tights.Mesh.Vertices[a], realtime.Tights.Mesh.Vertices[b]);
                float realtimeLength = Vector3.Distance(realtime.Tights.Positions[a], realtime.Tights.Positions[b]);
                float referenceLength = Vector3.Distance(reference.Tights.Positions[a], reference.Tights.Positions[b]);
                float difference = MathF.Abs(MathF.Max(1, realtimeLength / restLength) - MathF.Max(1, referenceLength / restLength));
                squaredStretchDifference += difference * difference;
                maximumStretchDifference = MathF.Max(maximumStretchDifference, difference);
            }
            stretchRms = MathF.Max(stretchRms, MathF.Sqrt(squaredStretchDifference / crotchEdges.Length));
        }
        output.WriteLine($"Crotch, {ClothingScene.SolverIterations} versus {ClothingScene.ReferenceIterations} iterations: maximum frame RMS {rms * 1000:F2} mm; maximum point error {maximum * 1000:F2} mm");
        output.WriteLine($"Crotch stretch difference: maximum frame RMS {stretchRms:P2}; maximum edge {maximumStretchDifference:P2}");
        // Contact can produce different folds; compare the position and strain RMS over the whole crotch.
        Assert.InRange(rms, 0, .008f);
        Assert.InRange(stretchRms, 0, .06f);
    }
}
