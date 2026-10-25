using System.Diagnostics;
using System.Numerics;

namespace SomeEngine.Clothing.Demo;

public sealed record DemoControls
{
    public bool ShellOnly { get; init; }
    public int FrameSteps { get; init; } = 1;
    public float Briefs { get; init; }
    public float Tights { get; init; }
    public float Running { get; init; }
    public float Spread { get; init; } = .07f;
    public float Wind { get; init; }
    public bool DualQuaternion { get; init; } = true;
    public bool Fine { get; init; }
    public bool Paused { get; init; }
    public bool Reset { get; init; }
}

public sealed class ClothingScene
{
    public const int Substeps = 2;
    public const int SolverIterations = 16;
    public const int ReferenceIterations = 64;
    public const float StepSeconds = 1f / 60;
    public CharacterAsset Character { get; } = new();
    public BodyMotion Body { get; }
    public BodyMesh BodyGeometry { get; }
    public TransportShell Shell { get; }
    public SurfaceDrawing BodyDrawing { get; }
    public TrajectoryField Trajectory { get; }
    public ClothingSimulation Briefs { get; }
    public ClothingSimulation Tights { get; }
    public float Seconds { get; private set; }
    public double SkinningMilliseconds { get; private set; }
    public double SimulationMilliseconds { get; private set; }
    public double SurfaceMilliseconds { get; private set; }
    public float[] Values { get; }
    public byte[] Bytes { get; }
    private float motionSeconds;
    private float running;
    private float spread;
    private int frame;
    public const int HeaderLength = 24;

    public ClothingScene()
    {
        Body = new(Character);
        BodyGeometry = new(Character);
        BodyDrawing = new(Character.Body, Character.BodyFaceVisibility);
        Shell = new(Character, BodyGeometry);
        Trajectory = new(Character);
        Briefs = new(ClothingMesh.Create(ClothingShape.Briefs, Character, Trajectory));
        Tights = new(ClothingMesh.Create(ClothingShape.Tights, Character, Trajectory));
        BodyGeometry.Update(Body, true, StepSeconds);
        BodyGeometry.ResetMotion();
        BodyGeometry.UpdateContactSurface(1);
        Trajectory.Update(Body, BodyGeometry);
        Shell.Update(Body);
        Briefs.Place(Trajectory, true);
        Tights.Place(Trajectory, true);
        int length = HeaderLength + BodyGeometry.Positions.Length * 6
            + (Briefs.Positions.Length + Tights.Positions.Length) * 5 + (Trajectory.Points.Length + Shell.Positions.Length) * 3;
        Values = new float[length];
        Bytes = new byte[length * sizeof(float)];
        Briefs.SampleTargets(Trajectory, 0, StepSeconds, true);
        Tights.SampleTargets(Trajectory, 0, StepSeconds, true);
        UpdateSurfaces();
        FillFrame();
    }

    public object Describe() => new
    {
        BodyVertices = BodyGeometry.Positions.Length,
        Shell = new { Vertices = Shell.Positions.Length, FaceCount = Shell.Faces.Length, Shell.Triangles, Shell.TransportEdges, Shell.SectionEdges,
            Shell.Rings, RingCount = Shell.Rings.Length / 2, RingProgress = Shell.Rings.Take(Shell.Rings.Length / 2).Select(ring => Shell.Points[ring[0]].Progress),
            Clearance = TransportShell.Clearance },
        BodyTriangles = BodyDrawing.Triangles,
        BodyVertexMap = BodyDrawing.SourceVertices,
        BodyTextureCoordinates = BodyDrawing.TextureCoordinates,
        BodyMaterials = BodyDrawing.Materials,
        Hair = new SurfaceDrawing(Character.Hair).Describe(Character.Hair),
        Eyes = new SurfaceDrawing(Character.Eyes).Describe(Character.Eyes),
        Briefs = new { Vertices = Briefs.Positions.Length, Particles = Briefs.Positions.Length, Briefs.Mesh.Triangles, Briefs.Mesh.Trim, Briefs.Mesh.BoundaryEdges },
        Tights = new { Vertices = Tights.Positions.Length, Particles = Tights.Positions.Length, Tights.Mesh.Triangles, Tights.Mesh.Trim, Tights.Mesh.BoundaryEdges },
        TrajectoryColumns = TrajectoryField.Columns,
        TrajectoryRows = TrajectoryField.Rows,
        TrajectorySurfaces = TrajectoryField.SurfaceCount,
        TrajectoryWeights = Trajectory.Points.Select(point => point.SimulationWeight).ToArray(),
        TrajectoryVertices = Trajectory.Points.Length,
        HeaderLength,
        Substeps, SolverIterations, ReferenceIterations,
    };

    public void Advance(DemoControls controls)
    {
        if (controls.Reset)
        {
            Seconds = motionSeconds = running = spread = 0;
        }
        if (controls.Paused && !controls.Reset) return;
        Seconds += StepSeconds;
        if (controls.Running > 0) motionSeconds += StepSeconds;
        running = float.Lerp(running, controls.Running, .08f);
        spread = float.Lerp(spread, controls.Spread, .08f);
        Body.Update(motionSeconds, running, spread);
        BodyGeometry.Update(Body, controls.DualQuaternion, StepSeconds);
        Shell.Update(Body, controls.DualQuaternion);
        if (controls.ShellOnly)
        {
            FillFrame();
            return;
        }
        if (controls.Reset) BodyGeometry.ResetMotion();
        long start = Stopwatch.GetTimestamp();
        Trajectory.Update(Body, BodyGeometry, controls.DualQuaternion);
        if (controls.Reset)
        {
            Briefs.Place(Trajectory, controls.DualQuaternion);
            Tights.Place(Trajectory, controls.DualQuaternion);
        }
        Briefs.SampleTargets(Trajectory, controls.Briefs, StepSeconds, controls.DualQuaternion);
        Tights.SampleTargets(Trajectory, controls.Tights, StepSeconds, controls.DualQuaternion);
        SkinningMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        start = Stopwatch.GetTimestamp();
        for (int substep = 0; substep < Substeps; substep++)
        {
            float blend = (float)(substep + 1) / Substeps;
            int iterations = controls.Fine ? ReferenceIterations : SolverIterations;
            BodyGeometry.UpdateContactSurface(blend);
            Parallel.Invoke(
                () => Briefs.Step(BodyGeometry, Seconds, StepSeconds / Substeps, iterations, controls.Wind, blend),
                () => Tights.Step(BodyGeometry, Seconds, StepSeconds / Substeps, iterations, controls.Wind, blend));
        }
        SimulationMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        start = Stopwatch.GetTimestamp();
        Briefs.FitFeet(BodyGeometry);
        Tights.FitFeet(BodyGeometry);
        UpdateSurfaces();
        SurfaceMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        FillFrame();
    }

    private void UpdateSurfaces()
    {
        Briefs.MeasureStretch(); Tights.MeasureStretch();
    }

    private void FillFrame()
    {
        Values[0] = Seconds;
        Values[1] = Briefs.Progress;
        Values[2] = Tights.Progress;
        Values[3] = (float)SkinningMilliseconds;
        Values[4] = (float)SimulationMilliseconds;
        Values[5] = Briefs.SimulatedVertices;
        Values[6] = Tights.SimulatedVertices;
        Values[7] = Briefs.ReleasedVertices;
        Values[8] = Tights.ReleasedVertices;
        Values[9] = Briefs.MaximumStretch;
        Values[10] = Tights.MaximumStretch;
        Values[11] = frame++;
        DualQuaternion head = Body.Bones[Character.HeadBone];
        Quaternion rotation = head.Rotation;
        Vector3 translation = head.Translation;
        Values[12] = rotation.X; Values[13] = rotation.Y; Values[14] = rotation.Z; Values[15] = rotation.W;
        Values[16] = translation.X; Values[17] = translation.Y; Values[18] = translation.Z;
        Values[19] = (float)SurfaceMilliseconds;
        int offset = HeaderLength;
        WritePositions(BodyGeometry.Positions, ref offset);
        WritePositions(BodyGeometry.Normals, ref offset);
        WritePositions(Briefs.Positions, ref offset);
        WritePositions(Tights.Positions, ref offset);
        Briefs.SimulationWeights.CopyTo(Values, offset); offset += Briefs.Positions.Length;
        Tights.SimulationWeights.CopyTo(Values, offset); offset += Tights.Positions.Length;
        Briefs.Stretch.CopyTo(Values, offset); offset += Briefs.Positions.Length;
        Tights.Stretch.CopyTo(Values, offset); offset += Tights.Positions.Length;
        WritePositions(Trajectory.Positions, ref offset);
        WritePositions(Shell.Positions, ref offset);
        Buffer.BlockCopy(Values, 0, Bytes, 0, Bytes.Length);
    }

    private void WritePositions(Vector3[] positions, ref int offset)
    {
        foreach (Vector3 position in positions)
        {
            Values[offset++] = position.X; Values[offset++] = position.Y; Values[offset++] = position.Z;
        }
    }
}

