using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace SomeEngine.Physics.Research;

public enum PhysicsSolver
{
    Avbd,
    LongRangeConstraints,
    CoupledConstraints,
    ContactRecovery,
    ResearchHybrid
}

[JsonConverter(typeof(JsonStringEnumConverter<PhysicsMaterialModel>))]
public enum PhysicsMaterialModel
{
    RigidConstraints,
    CompliantMaterials
}

public readonly record struct PhysicsBody(Vector3 Position, Quaternion Rotation, Vector3 Size, double Mass, Vector3 Velocity);
public readonly record struct PhysicsActivity(int Island, bool Sleeping);

internal sealed record PhysicsEvent(int Frame, int? Body = null, double[]? Velocity = null, int? RemovedJoint = null, double[]? Position = null);
internal sealed record InputBody(double[] Position, double[] Rotation, double[] Size, double Mass, double[] Inertia,
    double Friction, double[] Velocity, double[] AngularVelocity);
internal sealed record InputJoint(int BodyA, int BodyB, double[] PointA, double[] PointB, bool AngularFixed, int Source);
internal sealed record InputCollisionPair(int BodyA, int BodyB);
internal sealed record InputDistanceRange(int BodyA, int BodyB, double[] PointA, double[] PointB, double Minimum, double Maximum);
internal sealed record InputMaterials(double ContactStiffness, double JointLinearStiffness, double JointAngularStiffness, double ContactDamping = 0);
internal sealed record PhysicsInput(double StepSeconds, double[] Gravity, InputBody[] Bodies, InputJoint[] Joints, PhysicsEvent[] Events,
    InputCollisionPair[] CollisionExclusions, InputDistanceRange[]? DistanceRanges = null, InputMaterials? Materials = null,
    int ContactDetectionInterval = 0, bool ContactHistoryEnabled = false, int Substeps = 1,
    PhysicsMaterialModel MaterialModel = PhysicsMaterialModel.RigidConstraints);

/// <summary>Runs the official rigid-body scenes with a selected solver extension.</summary>
public sealed unsafe class PhysicsDemo : IDisposable
{
    public const int DefaultRigidIterations = 8;
    private readonly DemoLibrary library;
    private readonly nint demo;
    private readonly PhysicsSolver solver;
    private readonly int iterations;
    private readonly BodyFrame[] frames;
    private readonly BodyData[] data;
    private readonly double[] velocities;
    private readonly GCHandle callback;
    private readonly CoupledCorrection? coupled;
    private readonly ContactSeparation? contactRecovery;
    private PhysicsEvent[] events;
    private readonly InputCollisionPair[] collisionExclusions;
    private readonly InputDistanceRange[]? distanceRanges;
    private int frame;
    private int nextEvent;
    private bool sleepingEnabled = true;
    private bool positionStabilizationEnabled;
    private readonly double frameStepSeconds;
    private int substeps = 1;
    private int frameIterations, frameStabilizationIterations, frameTreeIterations, frameBodyUpdates, frameContactSolves, frameCollisionPasses;
    public int Dimension { get; }
    public PhysicsMaterialModel MaterialModel { get; }
    public int BodyCount => frames.Length;
    public float Gravity { get => library.GetGravity(demo); set => library.SetGravity(demo, value); }
    /// <summary>Newton rigid collision schedule: zero selects step initialization; a positive value refreshes every N main iterations.</summary>
    public int ContactDetectionInterval { get => library.GetContactInterval(demo); set => library.SetContactInterval(demo, value); }
    public bool ContactHistoryEnabled { get => library.GetContactHistory(demo) != 0; set => library.SetContactHistory(demo, value ? 1 : 0); }
    public int Substeps
    {
        get => substeps;
        set { substeps = value; library.SetStepSeconds(demo, (float)(frameStepSeconds / value)); }
    }
    public bool SleepingEnabled
    {
        get => sleepingEnabled;
        set { sleepingEnabled = value; library.SetSleepEnabled(demo, value ? 1 : 0); }
    }

    /// <summary>AVBD alpha=1 main solve followed by a position-only alpha=0 solve.</summary>
    public bool PositionStabilizationEnabled
    {
        get => positionStabilizationEnabled;
        set { positionStabilizationEnabled = value; library.SetPostStabilize(demo, value ? 1 : 0); }
    }

    public PhysicsDemo(PhysicsSceneDescription scene, int? iterations = null,
        PhysicsSolver solver = PhysicsSolver.LongRangeConstraints,
        PhysicsMaterialModel materialModel = PhysicsMaterialModel.RigidConstraints)
        : this(scene.Dimension, scene.Scene, iterations, solver, materialModel)
    {
        if (scene.DisturbanceFrame >= 0)
            AddEvent(new(scene.DisturbanceFrame, BodyCount - 1,
                scene.Id == "2d-stack-disturbed" ? [2, 0, 0] : [0, 2, 0]));
    }

    public PhysicsDemo(PhysicsScene scene, int? iterations = null, PhysicsSolver solver = PhysicsSolver.LongRangeConstraints,
        PhysicsMaterialModel materialModel = PhysicsMaterialModel.RigidConstraints)
        : this(3, (int)scene, iterations, solver, materialModel) { }

    public PhysicsDemo(int dimension, int scene, int? iterations = null, PhysicsSolver solver = PhysicsSolver.LongRangeConstraints,
        PhysicsMaterialModel materialModel = PhysicsMaterialModel.RigidConstraints)
        : this(dimension, scene, iterations ?? (solver == PhysicsSolver.ResearchHybrid ? 2
            : materialModel == PhysicsMaterialModel.RigidConstraints ? DefaultRigidIterations : 8), solver, null, materialModel) { }

    internal PhysicsDemo(int dimension, int scene, int iterations, PhysicsSolver solver, PhysicsInput? input,
        PhysicsMaterialModel materialModel = PhysicsMaterialModel.RigidConstraints, string? nativeDirectory = null)
    {
        int requestedSubsteps = input?.Substeps ?? 1;
        input ??= scene >= 200 ? PhysicsScenes.Create((PhysicsScene)scene) with { MaterialModel = materialModel } : null;
        Dimension = dimension; this.iterations = iterations; this.solver = solver;
        events = (input?.Events ?? (scene == 102 ? [new(90, RemovedJoint: 0)] : [])).OrderBy(eventData => eventData.Frame).ToArray();
        library = new DemoLibrary(dimension, nativeDirectory);
        demo = input is null ? library.Create(scene, iterations) : CreateFromInput(input);
        MaterialModel = input?.MaterialModel ?? materialModel;
        library.SetCompliantMaterials(demo, MaterialModel == PhysicsMaterialModel.CompliantMaterials ? 1 : 0);
        frameStepSeconds = library.ReadStepSeconds(demo);
        Substeps = requestedSubsteps;
        ContactHistoryEnabled = input?.ContactHistoryEnabled ?? false;
        collisionExclusions = input?.CollisionExclusions ?? ReadInitialCollisionExclusions();
        distanceRanges = input is null && scene == 101 ? PhysicsScenes.CreateManualDistances() : input?.DistanceRanges;
        SetCollisionExclusions();
        library.SetResearchExtensions(demo, solver == PhysicsSolver.ResearchHybrid ? 1 : 0);
        PositionStabilizationEnabled = solver == PhysicsSolver.ResearchHybrid;
        frames = new BodyFrame[library.BodyCount(demo)];
        data = new BodyData[frames.Length]; velocities = new double[frames.Length * 3];
        if (solver is PhysicsSolver.LongRangeConstraints or PhysicsSolver.ResearchHybrid) callback = GCHandle.Alloc(new LongRangeConstraints(library, demo, distanceRanges, MaterialModel));
        if (solver == PhysicsSolver.CoupledConstraints)
        {
            coupled = new CoupledCorrection(library, demo, 2); callback = GCHandle.Alloc(coupled);
        }
        if (solver is PhysicsSolver.CoupledConstraints or PhysicsSolver.ContactRecovery)
            contactRecovery = new ContactSeparation(library, demo);
    }

    public void Step()
    {
        for (; nextEvent < events.Length && events[nextEvent].Frame == frame; ++nextEvent)
        {
            PhysicsEvent input = events[nextEvent];
            if (input.RemovedJoint is int joint) library.RemoveJoint(demo, joint);
            if (input.Body is int body)
            {
                if (input.Position is { } position)
                    library.MoveBody(demo, body, (float)position[0], (float)position[1], (float)position[2]);
                if (input.Velocity is { } velocity)
                    library.AddVelocity(demo, body, (float)velocity[0], (float)velocity[1], (float)velocity[2]);
            }
        }
        ++frame;
        var correct = solver is PhysicsSolver.LongRangeConstraints or PhysicsSolver.ResearchHybrid ? &LongRangeConstraints.BuildSupports
            : solver == PhysicsSolver.CoupledConstraints ? &CoupledCorrection.Correct : (delegate* unmanaged[Cdecl]<nint, void>)null;
        int correctionIteration = solver is PhysicsSolver.LongRangeConstraints or PhysicsSolver.ResearchHybrid ? 0 : Math.Min(2, iterations - 1);
        frameIterations = frameStabilizationIterations = frameTreeIterations = frameBodyUpdates = frameContactSolves = frameCollisionPasses = 0;
        for (int substep = 0; substep < substeps; substep++)
        {
            library.Step(demo, correct, callback.IsAllocated ? GCHandle.ToIntPtr(callback) : 0, correctionIteration);
            frameIterations += library.ReadIterations(demo);
            frameStabilizationIterations += library.ReadStabilizationIterations(demo);
            frameTreeIterations += library.ReadTreeIterations(demo);
            frameBodyUpdates += library.ReadBodyUpdates(demo);
            frameContactSolves += library.ReadContactSolves(demo);
            frameCollisionPasses += library.ReadCollisionPasses(demo);
        }
        contactRecovery?.Separate();
    }

    public void AddVelocity(int body, Vector3 velocity) => library.AddVelocity(demo, body, velocity.X, velocity.Y, velocity.Z);
    public void MoveBody(int body, Vector3 position) => library.MoveBody(demo, body, position.X, position.Y, position.Z);
    public void RemoveJoint(int joint) => library.RemoveJoint(demo, joint);

    public PhysicsActivity[] ReadActivity()
    {
        int[] values = new int[BodyCount * 2];
        fixed (int* output = values) library.ReadActivity(demo, output);
        return Enumerable.Range(0, BodyCount).Select(body => new PhysicsActivity(values[body * 2], values[body * 2 + 1] != 0)).ToArray();
    }

    internal void AddEvent(PhysicsEvent input) => events = events.Append(input).OrderBy(eventData => eventData.Frame).ToArray();

    internal int ReadIterations() => frameIterations;
    internal int ReadStabilizationIterations() => frameStabilizationIterations;
    internal int ReadTreeIterations() => frameTreeIterations;
    internal int ReadBodyUpdates() => frameBodyUpdates;
    internal int ReadContactSolves() => frameContactSolves;
    internal int ReadCollisionPasses() => frameCollisionPasses;

    internal double[] ReadStepMeasurements()
    {
        if (library.StepMeasurements == null) return [];
        double[] values = new double[21];
        fixed (double* output = values) library.StepMeasurements(demo, output);
        return values;
    }

    private InputCollisionPair[] ReadInitialCollisionExclusions()
    {
        var joints = new JointData[library.ReadJoints(demo, null)];
        fixed (JointData* output = joints) library.ReadJoints(demo, output);
        return joints.Where(joint => joint.BodyA >= 0)
            .Select(joint => new InputCollisionPair(Math.Min(joint.BodyA, joint.BodyB), Math.Max(joint.BodyA, joint.BodyB)))
            .Distinct().ToArray();
    }

    private void SetCollisionExclusions()
    {
        CollisionPairData[] pairs = collisionExclusions.Select(pair => new CollisionPairData { BodyA = pair.BodyA, BodyB = pair.BodyB }).ToArray();
        fixed (CollisionPairData* data = pairs) library.SetCollisionExclusions(demo, data, pairs.Length);
    }

    private nint CreateFromInput(PhysicsInput input)
    {
        var bodies = new InitialBodyData[input.Bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            InputBody body = input.Bodies[i];
            InitialBodyData data = new() { Mass = body.Mass, Friction = body.Friction };
            body.Position.AsSpan().CopyTo(new Span<double>(data.Position, 3)); body.Rotation.AsSpan().CopyTo(new Span<double>(data.Rotation, 4));
            body.Size.AsSpan().CopyTo(new Span<double>(data.Size, 3)); body.Inertia.AsSpan().CopyTo(new Span<double>(data.Inertia, 3));
            body.Velocity.AsSpan().CopyTo(new Span<double>(data.Velocity, 3)); body.AngularVelocity.AsSpan().CopyTo(new Span<double>(data.AngularVelocity, 3));
            bodies[i] = data;
        }
        var joints = new JointData[input.Joints.Length];
        for (int i = 0; i < joints.Length; i++)
        {
            InputJoint joint = input.Joints[i];
            JointData data = new() { BodyA = joint.BodyA, BodyB = joint.BodyB, AngularFixed = joint.AngularFixed ? 1 : 0, Source = joint.Source };
            joint.PointA.AsSpan().CopyTo(new Span<double>(data.PointA, 3));
            joint.PointB.AsSpan().CopyTo(new Span<double>(data.PointB, 3));
            joints[i] = data;
        }
        nint created;
        fixed (InitialBodyData* bodyData = bodies)
        fixed (JointData* jointData = joints)
            created = library.CreateFromInput(bodyData, bodies.Length, jointData, joints.Length, iterations,
                (float)input.Gravity[Dimension - 1], (float)input.StepSeconds);
        if (input.Materials is InputMaterials materials)
        {
            library.SetMaterials(created, (float)materials.ContactStiffness, (float)materials.JointLinearStiffness, (float)materials.JointAngularStiffness);
            library.SetContactDamping(created, (float)materials.ContactDamping);
        }
        library.SetContactInterval(created, input.ContactDetectionInterval);
        return created;
    }

    internal PhysicsInput ReadInput()
    {
        var initial = new InitialBodyData[BodyCount];
        fixed (InitialBodyData* output = initial) library.ReadInputBodies(demo, output);
        var inputBodies = new InputBody[BodyCount];
        for (int i = 0; i < inputBodies.Length; i++)
        {
            InitialBodyData body = initial[i];
            inputBodies[i] = new(new ReadOnlySpan<double>(body.Position, 3).ToArray(), new ReadOnlySpan<double>(body.Rotation, 4).ToArray(),
                new ReadOnlySpan<double>(body.Size, 3).ToArray(), body.Mass, new ReadOnlySpan<double>(body.Inertia, 3).ToArray(), body.Friction,
                new ReadOnlySpan<double>(body.Velocity, 3).ToArray(), new ReadOnlySpan<double>(body.AngularVelocity, 3).ToArray());
        }
        var joints = new JointData[library.ReadJoints(demo, null)];
        fixed (JointData* output = joints) library.ReadJoints(demo, output);
        var inputJoints = new InputJoint[joints.Length];
        for (int i = 0; i < joints.Length; i++)
        {
            JointData joint = joints[i];
            inputJoints[i] = new(joint.BodyA, joint.BodyB, new ReadOnlySpan<double>(joint.PointA, 3).ToArray(),
                new ReadOnlySpan<double>(joint.PointB, 3).ToArray(), joint.AngularFixed != 0, joint.Source);
        }
        double[] gravity = Dimension == 2 ? [0, Gravity, 0] : [0, 0, Gravity];
        double* materials = stackalloc double[4];
        library.ReadMaterials(demo, materials);
        return new(frameStepSeconds, gravity, inputBodies, inputJoints, events, collisionExclusions, distanceRanges,
            MaterialModel == PhysicsMaterialModel.CompliantMaterials ? new(materials[0], materials[1], materials[2], materials[3]) : null,
            ContactDetectionInterval, ContactHistoryEnabled, Substeps, MaterialModel);
    }

    public PhysicsBody[] ReadBodies()
    {
        ReadFrames(frames);
        fixed (double* output = velocities) library.ReadVelocities(demo, output);
        var result = new PhysicsBody[frames.Length];
        for (int i = 0; i < result.Length; i++)
        {
            BodyFrame frame = frames[i];
            result[i] = new(BodyGeometry.ReadPosition(frame), new((float)frame.Rotation[0], (float)frame.Rotation[1], (float)frame.Rotation[2], (float)frame.Rotation[3]),
                BodyGeometry.Read(frame.Size), frame.Mass, new((float)velocities[i * 3], (float)velocities[i * 3 + 1], (float)velocities[i * 3 + 2]));
        }
        return result;
    }

    public double ReadJointError()
    {
        double error = 0;
        fixed (BodyData* output = data)
        {
            ConstraintData* rows;
            int count = library.Read(demo, output, &rows, 0);
            for (int i = 0; i < count; i++) if (rows[i].Kind == 0) error = Math.Max(error, Math.Abs(rows[i].Error));
        }
        return error;
    }

    public double ReadResidual()
    {
        double error = 0;
        fixed (BodyData* output = data)
        {
            ConstraintData* rows;
            int count = library.Read(demo, output, &rows, 0);
            for (int i = 0; i < count; i++)
            {
                double value = rows[i].Kind == 0 ? Math.Abs(rows[i].Error) : rows[i].Kind == 1 ? Math.Max(0, -rows[i].Error) : 0;
                error = Math.Max(error, value);
            }
        }
        return error;
    }

    public double ReadPenetration()
    {
        double penetration = 0;
        fixed (BodyData* output = data)
        {
            ConstraintData* rows;
            int count = library.ReadContacts(demo, output, &rows);
            for (int i = 0; i < count; i++) penetration = Math.Max(penetration, -rows[i].Error);
        }
        return penetration;
    }

    public double ReadGroundReaction()
    {
        return library.ReadGroundReaction(demo);
    }

    internal void ReadFrames(BodyFrame[] output)
    {
        fixed (BodyFrame* target = output) library.ReadFrame(demo, target);
    }

    public void Dispose()
    {
        if (callback.IsAllocated) callback.Free();
        coupled?.Dispose(); contactRecovery?.Dispose();
        library.Destroy(demo); library.Dispose();
    }
}
