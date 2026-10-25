using System.Runtime.InteropServices;

namespace SomeEngine.Physics.Research;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BodyData
{
    public fixed double Inertia[6];
    public fixed double Difference[6];
    public fixed double Curvature[9];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ConstraintData
{
    public int BodyA, BodyB, Kind, NormalRow;
    public double Error, Force, Friction, Penalty;
    public fixed double JacobianA[6];
    public fixed double JacobianB[6];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BodyFrame
{
    public fixed double Position[3];
    public fixed double Rotation[4];
    public fixed double Size[3];
    public double Mass;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct InitialBodyData
{
    public fixed double Position[3], Rotation[4], Size[3];
    public double Mass;
    public fixed double Inertia[3];
    public double Friction;
    public fixed double Velocity[3], AngularVelocity[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct JointData
{
    public int BodyA, BodyB, AngularFixed, Source;
    public fixed double PointA[3], PointB[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SupportContact
{
    public int BodyA, BodyB, Source, Sticks;
    public fixed double PointA[3], PointB[3], Normal[3], Force[3];
    public double Friction;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DistanceRange
{
    public int BodyA, BodyB, Kind, LoadStart, LoadCount;
    public fixed double PointA[3], PointB[3];
    public double Minimum, Maximum;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ContactLoad
{
    public int Contact;
    public double Weight;
    public ContactLoad(int contact, double weight) { Contact = contact; Weight = weight; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SupportNode
{
    public int Contact, Direction, BelowStart, BelowCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CollisionPairData
{
    public int BodyA, BodyB;
}

internal sealed unsafe class DemoLibrary : IDisposable
{
    private readonly nint library;
    public readonly int Dimension;
    public readonly delegate* unmanaged[Cdecl]<int, int, nint> Create;
    public readonly delegate* unmanaged[Cdecl]<nint, void> Destroy;
    public readonly delegate* unmanaged[Cdecl]<nint, int> BodyCount;
    public readonly delegate* unmanaged[Cdecl]<nint, double*, void> StepMeasurements;
    public readonly delegate* unmanaged[Cdecl]<nint, int, void> SetPostStabilize;
    public readonly delegate* unmanaged[Cdecl]<nint, int, void> SetResearchExtensions;
    public readonly delegate* unmanaged[Cdecl]<nint, int, void> SetCompliantMaterials;
    public readonly delegate* unmanaged[Cdecl]<nint, int, float, float, float, void> AddVelocity;
    public readonly delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, void>, nint, int,
        delegate* unmanaged[Cdecl]<nint, int, nint, nint, void>, nint, int, void> Step;
    public readonly delegate* unmanaged[Cdecl]<nint, BodyFrame*, void> ReadFrame;
    public readonly delegate* unmanaged[Cdecl]<nint, BodyData*, ConstraintData**, float, int> Read;
    public readonly delegate* unmanaged[Cdecl]<nint, BodyData*, ConstraintData**, int> ReadContacts;
    public readonly delegate* unmanaged[Cdecl]<nint, double*, double*, int, void> Apply;
    public readonly delegate* unmanaged[Cdecl]<nint, JointData*, int> ReadJoints;
    public readonly delegate* unmanaged[Cdecl]<nint, SupportContact**, int> ReadSupportContacts;
    public readonly delegate* unmanaged[Cdecl]<nint, DistanceRange*, int, void> SetJointRanges;
    public readonly delegate* unmanaged[Cdecl]<nint, DistanceRange*, int, SupportNode*, int, ContactLoad*, int, void> SetSupportRanges;
    public readonly delegate* unmanaged[Cdecl]<nint, float> GetGravity;
    public readonly delegate* unmanaged[Cdecl]<nint, float, void> SetGravity;
    public readonly delegate* unmanaged[Cdecl]<nint, int, float, float, float, void> MoveBody;
    public readonly delegate* unmanaged[Cdecl]<nint, double*, void> ReadVelocities;
    public readonly delegate* unmanaged[Cdecl]<nint, int> ReadJointVersion;
    public readonly delegate* unmanaged[Cdecl]<nint, int> ReadIterations;
    public readonly delegate* unmanaged[Cdecl]<nint, int> ReadStabilizationIterations;
    public readonly delegate* unmanaged[Cdecl]<nint, int> ReadTreeIterations;
    public readonly delegate* unmanaged[Cdecl]<nint, InitialBodyData*, void> ReadInputBodies;
    public readonly delegate* unmanaged[Cdecl]<InitialBodyData*, int, JointData*, int, int, float, float, nint> CreateFromInput;
    public readonly delegate* unmanaged[Cdecl]<nint, float> ReadStepSeconds;
    public readonly delegate* unmanaged[Cdecl]<nint, float, float, float, void> SetMaterials;
    public readonly delegate* unmanaged[Cdecl]<nint, double*, void> ReadMaterials;
    public readonly delegate* unmanaged[Cdecl]<nint, float, void> SetContactDamping;
    public readonly delegate* unmanaged[Cdecl]<nint, int, void> RemoveJoint;
    public readonly delegate* unmanaged[Cdecl]<nint, void> BuildSupports;
    public readonly delegate* unmanaged[Cdecl]<nint, CollisionPairData*, int, void> SetCollisionExclusions;
    public readonly delegate* unmanaged[Cdecl]<nint, int*, void> ReadActivity;
    public readonly delegate* unmanaged[Cdecl]<nint, int, void> SetSleepEnabled;
    public readonly delegate* unmanaged[Cdecl]<nint, int> ReadBodyUpdates;
    public readonly delegate* unmanaged[Cdecl]<nint, int> ReadContactSolves;
    public readonly delegate* unmanaged[Cdecl]<nint, double> ReadGroundReaction;
    public readonly delegate* unmanaged[Cdecl]<nint, int> ReadCollisionPasses;
    public readonly delegate* unmanaged[Cdecl]<nint, int> GetContactInterval;
    public readonly delegate* unmanaged[Cdecl]<nint, int, void> SetContactInterval;
    public readonly delegate* unmanaged[Cdecl]<nint, int, void> SetContactHistory;
    public readonly delegate* unmanaged[Cdecl]<nint, int> GetContactHistory;
    public readonly delegate* unmanaged[Cdecl]<nint, float, void> SetStepSeconds;

    public DemoLibrary(int dimension, string? nativeDirectory = null)
    {
        Dimension = dimension;
        library = NativeLibrary.Load(Path.Combine(nativeDirectory ?? AppContext.BaseDirectory, $"Avbd{dimension}D.dll"));
        if (NativeLibrary.TryGetExport(library, "demo_step_measurements", out nint measurements))
            StepMeasurements = (delegate* unmanaged[Cdecl]<nint, double*, void>)measurements;
        ReadActivity = (delegate* unmanaged[Cdecl]<nint, int*, void>)NativeLibrary.GetExport(library, "demo_activity");
        SetSleepEnabled = (delegate* unmanaged[Cdecl]<nint, int, void>)NativeLibrary.GetExport(library, "demo_sleep_enabled");
        ReadBodyUpdates = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(library, "demo_body_updates");
        ReadContactSolves = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(library, "demo_contact_solves");
        ReadGroundReaction = (delegate* unmanaged[Cdecl]<nint, double>)NativeLibrary.GetExport(library, "demo_ground_reaction");
        ReadCollisionPasses = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(library, "demo_collision_passes");
        GetContactInterval = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(library, "demo_get_contact_interval");
        SetContactInterval = (delegate* unmanaged[Cdecl]<nint, int, void>)NativeLibrary.GetExport(library, "demo_set_contact_interval");
        SetContactHistory = (delegate* unmanaged[Cdecl]<nint, int, void>)NativeLibrary.GetExport(library, "demo_set_contact_history");
        GetContactHistory = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(library, "demo_get_contact_history");
        SetStepSeconds = (delegate* unmanaged[Cdecl]<nint, float, void>)NativeLibrary.GetExport(library, "demo_set_step_seconds");
        Create = (delegate* unmanaged[Cdecl]<int, int, nint>)NativeLibrary.GetExport(library, "demo_create");
        Destroy = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(library, "demo_destroy");
        BodyCount = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(library, "demo_body_count");
        SetPostStabilize = (delegate* unmanaged[Cdecl]<nint, int, void>)NativeLibrary.GetExport(library, "demo_post_stabilize");
        SetResearchExtensions = (delegate* unmanaged[Cdecl]<nint, int, void>)NativeLibrary.GetExport(library, "demo_research_extensions");
        SetCompliantMaterials = (delegate* unmanaged[Cdecl]<nint, int, void>)NativeLibrary.GetExport(library, "demo_compliant_materials");
        AddVelocity = (delegate* unmanaged[Cdecl]<nint, int, float, float, float, void>)NativeLibrary.GetExport(library, "demo_add_velocity");
        Step = (delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, void>, nint, int,
            delegate* unmanaged[Cdecl]<nint, int, nint, nint, void>, nint, int, void>)NativeLibrary.GetExport(library, "demo_step");
        ReadFrame = (delegate* unmanaged[Cdecl]<nint, BodyFrame*, void>)NativeLibrary.GetExport(library, "demo_frame");
        Read = (delegate* unmanaged[Cdecl]<nint, BodyData*, ConstraintData**, float, int>)NativeLibrary.GetExport(library, "demo_read");
        ReadContacts = (delegate* unmanaged[Cdecl]<nint, BodyData*, ConstraintData**, int>)NativeLibrary.GetExport(library, "demo_contacts");
        Apply = (delegate* unmanaged[Cdecl]<nint, double*, double*, int, void>)NativeLibrary.GetExport(library, "demo_apply");
        ReadJoints = (delegate* unmanaged[Cdecl]<nint, JointData*, int>)NativeLibrary.GetExport(library, "demo_joints");
        ReadSupportContacts = (delegate* unmanaged[Cdecl]<nint, SupportContact**, int>)NativeLibrary.GetExport(library, "demo_support_contacts");
        SetJointRanges = (delegate* unmanaged[Cdecl]<nint, DistanceRange*, int, void>)NativeLibrary.GetExport(library, "demo_lrc_joints");
        SetSupportRanges = (delegate* unmanaged[Cdecl]<nint, DistanceRange*, int, SupportNode*, int, ContactLoad*, int, void>)NativeLibrary.GetExport(library, "demo_lrc_supports");
        GetGravity = (delegate* unmanaged[Cdecl]<nint, float>)NativeLibrary.GetExport(library, "demo_get_gravity");
        SetGravity = (delegate* unmanaged[Cdecl]<nint, float, void>)NativeLibrary.GetExport(library, "demo_set_gravity");
        MoveBody = (delegate* unmanaged[Cdecl]<nint, int, float, float, float, void>)NativeLibrary.GetExport(library, "demo_move_body");
        ReadVelocities = (delegate* unmanaged[Cdecl]<nint, double*, void>)NativeLibrary.GetExport(library, "demo_velocities");
        ReadJointVersion = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(library, "demo_joint_version");
        ReadIterations = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(library, "demo_iterations");
        ReadStabilizationIterations = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(library, "demo_stabilization_iterations");
        ReadTreeIterations = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(library, "demo_tree_iterations");
        ReadInputBodies = (delegate* unmanaged[Cdecl]<nint, InitialBodyData*, void>)NativeLibrary.GetExport(library, "demo_input_bodies");
        CreateFromInput = (delegate* unmanaged[Cdecl]<InitialBodyData*, int, JointData*, int, int, float, float, nint>)NativeLibrary.GetExport(library, "demo_create_input");
        ReadStepSeconds = (delegate* unmanaged[Cdecl]<nint, float>)NativeLibrary.GetExport(library, "demo_step_seconds");
        SetMaterials = (delegate* unmanaged[Cdecl]<nint, float, float, float, void>)NativeLibrary.GetExport(library, "demo_materials");
        ReadMaterials = (delegate* unmanaged[Cdecl]<nint, double*, void>)NativeLibrary.GetExport(library, "demo_read_materials");
        SetContactDamping = (delegate* unmanaged[Cdecl]<nint, float, void>)NativeLibrary.GetExport(library, "demo_contact_damping");
        RemoveJoint = (delegate* unmanaged[Cdecl]<nint, int, void>)NativeLibrary.GetExport(library, "demo_remove_joint");
        BuildSupports = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(library, "demo_build_supports");
        SetCollisionExclusions = (delegate* unmanaged[Cdecl]<nint, CollisionPairData*, int, void>)NativeLibrary.GetExport(library, "demo_collision_exclusions");
    }

    public void Dispose() => NativeLibrary.Free(library);
}
