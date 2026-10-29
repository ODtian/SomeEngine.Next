using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Physics;
using SomeEngine.Resources;

namespace DefaultGame;

internal sealed partial class PhysicsSceneShaders
{
    private readonly IGlobalSession _global;
    private readonly SlangCompileTarget _target;
    private readonly string _directory;

    internal PhysicsSceneShaders(SlangCompileTarget target, string directory)
    {
        _target = target; _directory = directory;
        _global = Shader.CreateSlangGlobalSession();
    }

    internal DeviceGridKernels Grid()
    {
        ComputePipelineDesc[] entries = Compile("scene_grid", """
            #include "physics_grid_common.slang"
            #include "physics_bodies.slang"
            """ + "\n" + SceneBodyPairs + "\n" + """
            [shader("compute")][numthreads(256,1,1)]
            void CountDevicePairs(uint3 id:SV_DispatchThreadID)
            { DevicePairAnd<DeviceCollisionRules,SceneAllowedPairs> rules; DeviceShapePairs types; countDevicePairs(devicePhysicsThreadIndex(id,256),rules,types); }
            [shader("compute")][numthreads(256,1,1)]
            void WriteDevicePairs(uint3 id:SV_DispatchThreadID)
            { DevicePairAnd<DeviceCollisionRules,SceneAllowedPairs> rules; DeviceShapePairs types; writeDevicePairs(devicePhysicsThreadIndex(id,256),rules,types); }
            """,
            "ClearDeviceGrid", "BuildDeviceGrid", "CountDevicePairs", "ScanDevicePairCounts",
            "OffsetDevicePairBlocks", "OffsetDevicePairBins", "WriteDevicePairs");
        return new(entries[0], entries[1], entries[2], entries[3], entries[4], entries[5], entries[6]);
    }

    internal DeviceBodyKernels Bodies()
    {
        ComputePipelineDesc[] entries = Compile("scene_bodies", "#include \"physics_body_activity_dynamics.slang\"",
            "PredictDeviceActiveBodies", "UpdateDeviceActiveBodyVelocities", "ApplyDeviceGyroscopicTorque", "ResumeDeviceActiveBodies");
        return new(entries[0], entries[1], entries[2], Resume: entries[3]);
    }

    internal DeviceBodyInputKernels BodyInputs()
    {
        ComputePipelineDesc[] entries = Compile("scene_body_inputs", "#include \"physics_body_inputs.slang\"",
            "ApplyDeviceBodyInputs", "ApplyDeviceKinematicTargets");
        return new(entries[0], entries[1]);
    }

    internal ComputePipelineDesc Geometry(float contactDistance) => Compile("scene_box_geometry",
        "static const float SceneContactDistance = " + contactDistance.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ";\n" + """
        #include "physics_geometry_update.slang"
        struct SceneBoxUpdate : IDeviceGeometryUpdate
        {
            DeviceGeometryBounds update(uint index, inout DeviceGeometryInput geometry)
            {
                DeviceBoxUpdate box;
                DeviceGeometryBounds bounds = box.update(index,geometry);
                bounds.min -= SceneContactDistance;
                bounds.max += SceneContactDistance;
                return bounds;
            }
        };
        [shader("compute")][numthreads(64,1,1)]
        void Update(uint3 id : SV_DispatchThreadID) { updateDeviceGeometry<SceneBoxUpdate>(devicePhysicsThreadIndex(id,64)); }
        """, "Update")[0];

    internal ComputePipelineDesc Detection() => Compile("scene_box_detection", """
        #include "physics_geometry_references.slang"
        [shader("compute")][numthreads(64,1,1)]
        void Detect(uint3 id : SV_DispatchThreadID)
        { DeviceConvexCollision<DeviceBox,DeviceBox> collision; detectDeviceReferencedCollision(devicePhysicsThreadIndex(id,64),collision); }
        """, "Detect")[0];

    internal DeviceContactMatchKernels Matching()
    {
        ComputePipelineDesc[] entries = Compile("scene_contact_matching", "#include \"physics_contact_matching.slang\"",
            "ClearDeviceContactMatches", "IndexDeviceContactMatches", "MatchDeviceContacts");
        return new(entries[0], entries[1], entries[2]);
    }

    internal ComputePipelineDesc ContactGeometry() => Compile("scene_contact_geometry",
        "#include \"physics_contact_geometry.slang\"", "UpdateDeviceContactGeometry")[0];

    internal DeviceGraphKernels Graph(bool contactSupports, bool jointBounds)
    {
        ComputePipelineDesc[] entries = Compile("scene_contact_graph", SceneDefines(contactSupports, jointBounds) + """
            #include "physics_body_activity_types.slang"
            #define DEVICE_GRAPH_BODY_ACTIVE(body) deviceBodyActivityAwake(body)
            #include "physics_contact_data.slang"
            #include "physics_joint_bodies.slang"
            #include "physics_distance_range_bodies.slang"
            typedef DeviceConstraintBodyGroups<
                DeviceConstraintBodyGroups<DeviceContactBodies,DeviceJointBodies>,
                DeviceDistanceRangeBodies> SceneOrdinaryBodies;
            #if SCENE_JOINT_BOUNDS
            #include "physics_joint_bound_bodies.slang"
            typedef DeviceConstraintBodyGroups<SceneOrdinaryBodies,DeviceJointBoundBodies> SceneConstraintBodies;
            #else
            typedef SceneOrdinaryBodies SceneConstraintBodies;
            #endif
            #if SCENE_CONTACT_SUPPORTS
            #include "physics_contact_support_bodies.slang"
            typedef DeviceConstraintBodyGroups<SceneConstraintBodies,DeviceSupportBodies> SceneGraphRows;
            #else
            typedef SceneConstraintBodies SceneGraphRows;
            #endif
            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id : SV_DispatchThreadID)
            { SceneGraphRows rows; writeDeviceConstraintGraphRows(devicePhysicsThreadIndex(id,64),rows); }
            #include "physics_constraint_graph_parallel.slang"
            """, "WriteRows", "InitializeDeviceGraph", "ConnectDeviceGraph", "CompressDeviceGraph",
            "ScanDeviceGraphDegrees", "OffsetDeviceGraphBlocks", "AddDeviceGraphDegreeOffsets",
            "WriteDeviceGraphAdjacency", "ResetDeviceGraphColorProgress", "ProposeDeviceBodyColors",
            "ResolveDeviceBodyColors", "ProposeDeviceRowColors", "ResolveDeviceRowColors",
            "CountDeviceGraphColors", "OffsetDeviceGraphGroups", "CountDeviceGraphRows",
            "OffsetDeviceGraphRanges", "WriteDeviceGraphWork");
        return new(entries[0], new DeviceGraphParallelKernels(entries[1], entries[2], entries[3], entries[4],
            entries[5], entries[6], entries[7], entries[8], entries[9], entries[10], entries[11], entries[12],
            entries[13], entries[14], entries[15], entries[16], entries[17]));
    }

    internal DeviceAvbdKernels Avbd(bool contactSupports, bool jointBounds, int bodyCount, int contactCapacity,
        bool hasJoints, bool hasDistanceRanges)
    {
        string[] names = ["ClearBodyIndices", "Initialize", "UpdateBody", "UpdateDuals", "InitializeSupports",
            "UpdateSupports", "ClearSupportBodies"];
        if (contactSupports) names = [.. names, "ClearDeviceSupportGroups", "CountDeviceSupportGroups",
            "ScanDeviceSupportGroups", "OffsetDeviceSupportGroupBlocks", "AddDeviceSupportGroupOffsets", "WriteDeviceSupportGroups",
            "MergeDeviceSupportContacts", "AdvanceDeviceSupportSort", "FinishDeviceSupportSort",
            "ClearDeviceSupportContactOrder", "WriteDeviceSupportContactOrder", "WriteDeviceSupportContactEnds", "WriteDeviceSupportDependencies",
            "InitializeDeviceSupportRebuild", "ClearDeviceSupportRebuild", "CheckDeviceSupportMotion", "InitializeDeviceSupportWarmStart",
            "PrepareDeviceSupportBodies", "PrepareDeviceSupportCandidates", "ClearDeviceSupportWorkQueue", "QueueDeviceSupportCandidates",
            "SeedDeviceSupportCandidates", "FinishDeviceSupportBodies", "ReleaseDeviceUnsupportedContacts", "ClearDeviceSupportIncoming",
            "WriteDeviceSupportIncoming", "QueueDeviceSupportLoads", "SeedDeviceSupportLoads"];
        string constraintDefines = "#define SCENE_HAS_JOINTS " + (hasJoints ? "1\n" : "0\n")
            + "#define SCENE_HAS_DISTANCE_RANGES " + (hasDistanceRanges ? "1\n" : "0\n");
        ComputePipelineDesc[] entries = Compile("scene_contact_avbd", SceneDefines(contactSupports, jointBounds) + constraintDefines + """
            #include "physics_dispatch.slang"
            #include "physics_avbd_contact.slang"
            #if SCENE_HAS_JOINTS
            #include "physics_avbd_joint.slang"
            #endif
            #if SCENE_HAS_DISTANCE_RANGES
            #include "physics_avbd_distance_range.slang"
            #endif
            #include "physics_awake_avbd.slang"
            // Empty scene arrays still reserve one row and one group in the graph.
            struct SceneEmptyAvbdConstraints : IDeviceAvbdConstraints
            {
                uint count() { return 1; }
                bool active(uint row) { return false; }
                uint bodyCount(uint row) { return 0; }
                uint body(uint row,uint end) { return 0; }
                bool writes(uint row,uint end) { return false; }
                uint groupCount() { return 1; }
                uint group(uint row) { return 0; }
                void initialize(uint row,DeviceAvbdStep context) { }
                void accumulate(uint row,uint bodyIndex,DeviceAvbdStep context,inout DeviceAvbdEquation equation) { }
                void updateDuals(uint row,DeviceAvbdStep context) { }
                override bool adjustsBody() { return false; }
                void adjust(uint bodyIndex,DeviceAvbdStep context,DeviceAvbdEquation equation,
                    inout float3 translation,inout float3 rotation) { }
            };
            #if SCENE_HAS_JOINTS
            typedef DeviceJointAvbdConstraints SceneJointAvbdConstraints;
            #else
            typedef SceneEmptyAvbdConstraints SceneJointAvbdConstraints;
            #endif
            #if SCENE_HAS_DISTANCE_RANGES
            typedef DeviceDistanceRangeAvbdConstraints SceneDistanceRangeAvbdConstraints;
            #else
            typedef SceneEmptyAvbdConstraints SceneDistanceRangeAvbdConstraints;
            #endif
            #if SCENE_CONTACT_SUPPORTS
            #include "physics_avbd_contact_support.slang"
            #include "physics_contact_support_parallel.slang"
            struct SceneContactSupportConstraints : IDeviceAvbdConstraints
            {
                DeviceContactSupportAvbdConstraints inner;
                override bool adjustsBody() { return inner.adjustsBody(); }
                uint count() { return inner.count(); }
                bool active(uint row) { return inner.active(row); }
                uint bodyCount(uint row) { return inner.bodyCount(row); }
                uint body(uint row,uint end) { return inner.body(row,end); }
                bool writes(uint row,uint end) { return inner.writes(row,end); }
                uint groupCount() { return inner.groupCount(); }
                uint group(uint row) { return inner.group(row); }
                void initialize(uint row,DeviceAvbdStep context) { }
                void accumulate(uint row,uint bodyIndex,DeviceAvbdStep context,inout DeviceAvbdEquation equation)
                { inner.accumulate(row,bodyIndex,context,equation); }
                void updateDuals(uint row,DeviceAvbdStep context) { inner.updateDuals(row,context); }
                void adjust(uint bodyIndex,DeviceAvbdStep context,DeviceAvbdEquation equation,
                    inout float3 translation,inout float3 rotation)
                { inner.adjust(bodyIndex,context,equation,translation,rotation); }
            };
            #endif
            struct SceneContactAvbdConstraints : IDeviceAvbdConstraints
            {
                DeviceContactPotentialAvbdConstraints inner;
                override bool adjustsBody() { return inner.adjustsBody(); }
                uint count() { return inner.count(); }
                bool active(uint row) { return inner.active(row); }
                uint bodyCount(uint row) { return inner.bodyCount(row); }
                uint body(uint row,uint end) { return inner.body(row,end); }
                bool writes(uint row,uint end) { return inner.writes(row,end); }
                uint groupCount() { return inner.groupCount(); }
                uint group(uint row) { return inner.group(row); }
                void initialize(uint row,DeviceAvbdStep context)
                {
                    inner.initialize(row,context);
                    DeviceContactConstraint point=DeviceContacts[row];
                    DeviceGraphBodies bodies;
                    float mobility=deviceContactWeight(point,point.normal,bodies,true);
                    float conditioning=1/(context.duration*context.duration*mobility);
                    DeviceAvbdContactState state=DeviceAvbdContactStates[row];
                    state.normalPenalty=clamp(max(conditioning,state.normalPenalty*context.retention),1,1e9);
                    state.tangentPenalty=clamp(max(conditioning,state.tangentPenalty*context.retention),1,1e9);
                    DeviceAvbdContactStates[row]=state;
                }
                void accumulate(uint row,uint bodyIndex,DeviceAvbdStep context,inout DeviceAvbdEquation equation)
                {
                    #if SCENE_CONTACT_SUPPORTS
                    bool supported=deviceConstraintSelected(DeviceContactsMetadata[row],DeviceContactSupportSelection[row])
                        &&DeviceSupportContactStates[row].enabled!=0;
                    deviceAccumulateAvbdContactPotential(row,bodyIndex,context,deviceContactSupportForce(row),supported,equation);
                    #else
                    inner.accumulate(row,bodyIndex,context,equation);
                    #endif
                }
                void updateDuals(uint row,DeviceAvbdStep context)
                {
                    #if SCENE_CONTACT_SUPPORTS
                    deviceUpdateAvbdContact(row,context,deviceContactSupportForce(row));
                    #else
                    inner.updateDuals(row,context);
                    #endif
                    DeviceContactConstraint point=DeviceContacts[row];
                    float gap=point.separation+dot(point.normal,deviceContactNormalDelta(point,false)-point.bodyAnchorDelta);
                    DeviceAvbdContactState state=DeviceAvbdContactStates[row];
                    float penalty=max(state.normalPenalty+100000*max(0,-gap),-state.normalMultiplier/.0005);
                    state.normalPenalty=clamp(penalty,1,1e9); state.tangentPenalty=state.normalPenalty;
                    DeviceAvbdContactStates[row]=state;
                }
                void adjust(uint bodyIndex,DeviceAvbdStep context,DeviceAvbdEquation equation,
                    inout float3 translation,inout float3 rotation)
                { inner.adjust(bodyIndex,context,equation,translation,rotation); }
            };
            typedef DeviceAvbdConstraintGroups<
                DeviceAvbdConstraintGroups<SceneContactAvbdConstraints,SceneJointAvbdConstraints>,
                SceneDistanceRangeAvbdConstraints> SceneOrdinaryConstraints;
            #if SCENE_JOINT_BOUNDS
            #include "physics_avbd_joint_bounds.slang"
            typedef DeviceAvbdConstraintGroups<SceneOrdinaryConstraints,DeviceJointBoundAvbdConstraints> SceneAvbdConstraints;
            #else
            typedef SceneOrdinaryConstraints SceneAvbdConstraints;
            #endif
            #if SCENE_CONTACT_SUPPORTS
            typedef DeviceAvbdConstraintGroups<SceneAvbdConstraints,SceneContactSupportConstraints> SceneSolveRows;
            #else
            typedef SceneAvbdConstraints SceneSolveRows;
            #endif
            [shader("compute")][numthreads(64,1,1)]
            void ClearBodyIndices(uint3 id : SV_DispatchThreadID) { clearDeviceAvbdBodyIndices(devicePhysicsThreadIndex(id,64)); }
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID)
            { DeviceAwakeAvbdConstraints<SceneSolveRows> rows; DeviceAwakeBodies bodies; initializeDeviceAvbdRows(devicePhysicsThreadIndex(id,64),rows,bodies); }
            [shader("compute")][numthreads(1,1,1)]
            void UpdateBody(uint3 id : SV_DispatchThreadID)
            { DeviceAwakeAvbdConstraints<SceneSolveRows> rows; updateDeviceAvbdBody(devicePhysicsThreadIndex(id,1),rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateDuals(uint3 id : SV_DispatchThreadID)
            { DeviceAwakeAvbdConstraints<SceneSolveRows> rows; updateDeviceAvbdDuals(devicePhysicsThreadIndex(id,64),rows); }
            [shader("compute")][numthreads(64,1,1)]
            void InitializeSupports(uint3 id:SV_DispatchThreadID)
            {
                #if SCENE_CONTACT_SUPPORTS
                buildDeviceSupportCandidatesParallel();
                #endif
            }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateSupports(uint3 id:SV_DispatchThreadID)
            {
                #if SCENE_CONTACT_SUPPORTS
                updateDeviceSupportLoadsParallel();
                #endif
            }
            [shader("compute")][numthreads(64,1,1)]
            void ClearSupportBodies(uint3 id:SV_DispatchThreadID)
            {
                #if SCENE_CONTACT_SUPPORTS
                uint bodyIndex=devicePhysicsThreadIndex(id,64);
                DeviceSupportBodies rows;
                if(bodyIndex<rows.count()&&rows.active(bodyIndex)&&deviceBodyActivityAwake(bodyIndex))
                    DeviceSupportBodyStates[bodyIndex]=(DeviceBodySupportState)0;
                #endif
            }
            """, names);
        DispatchArguments bodies = DevicePhysicsDispatch.CreateGroups((bodyCount + 63) / 64);
        DispatchArguments rows = DevicePhysicsDispatch.CreateGroups((Math.Max(bodyCount, contactCapacity) + 63) / 64);
        // Persistent consumers share one ready list; mapping kernels still cover every row.
        const int maximumSupportWorkerGroups = 128;
        DispatchArguments supportWorkers = DevicePhysicsDispatch.CreateGroups(Math.Min(maximumSupportWorkerGroups,
            (bodyCount + 63) / 64));
        var initialization = new List<DeviceAvbdGroupDispatch>();
        var updates = new List<DeviceAvbdGroupDispatch>();
        if (contactSupports)
        {
            initialization.AddRange([new(entries[6], bodies), new(entries[7], bodies), new(entries[8], rows),
                new(entries[9], DevicePhysicsDispatch.CreateGroups((bodyCount + 255) / 256)), new(entries[10], new(1, 1, 1)),
                new(entries[11], bodies), new(entries[12], rows)]);
            for (long width = 1; width < contactCapacity; width *= 2)
            {
                initialization.Add(new(entries[13], rows));
                initialization.Add(new(entries[14], new(1, 1, 1)));
            }
            initialization.Add(new(entries[15], rows));
            Add(initialization, "ClearDeviceSupportContactOrder", rows);
            Add(initialization, "WriteDeviceSupportContactOrder", rows);
            Add(initialization, "WriteDeviceSupportContactEnds", rows);
            Add(initialization, "WriteDeviceSupportDependencies", rows);
            Add(initialization, "InitializeDeviceSupportRebuild", bodies);
            Add(initialization, "InitializeDeviceSupportWarmStart", rows);
            Add(initialization, "CheckDeviceSupportMotion", bodies);
            AddBuild(initialization);

            Add(updates, "ClearDeviceSupportRebuild", bodies);
            Add(updates, "CheckDeviceSupportMotion", bodies);
            AddBuild(updates);
            Add(updates, "ClearDeviceSupportWorkQueue", new(1, 1, 1));
            Add(updates, "QueueDeviceSupportLoads", rows);
            Add(updates, "SeedDeviceSupportLoads", rows);
            Add(updates, "UpdateSupports", supportWorkers);
        }
        return new(entries[1], entries[2], entries[3], ClearBodyIndices: entries[0])
        { GroupInitializationDispatches = initialization.ToArray(), GroupUpdateDispatches = updates.ToArray() };

        void Add(List<DeviceAvbdGroupDispatch> dispatches, string name, DispatchArguments groups) =>
            dispatches.Add(new(entries[Array.IndexOf(names, name)], groups));

        void AddBuild(List<DeviceAvbdGroupDispatch> dispatches)
        {
            Add(dispatches, "PrepareDeviceSupportBodies", bodies);
            Add(dispatches, "PrepareDeviceSupportCandidates", rows);
            Add(dispatches, "ClearDeviceSupportWorkQueue", new(1, 1, 1));
            Add(dispatches, "QueueDeviceSupportCandidates", rows);
            Add(dispatches, "SeedDeviceSupportCandidates", rows);
            Add(dispatches, "InitializeSupports", supportWorkers);
            Add(dispatches, "FinishDeviceSupportBodies", bodies);
            Add(dispatches, "ReleaseDeviceUnsupportedContacts", rows);
            Add(dispatches, "ClearDeviceSupportIncoming", rows);
            Add(dispatches, "WriteDeviceSupportIncoming", rows);
        }
    }

    internal DeviceContactKernels Responses()
    {
        ComputePipelineDesc[] entries = Compile("scene_contact_responses", """
            #include "physics_awake_contact_actions.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID)
            { DeviceAwakeContactResponseActions<DeviceGraphBodies> rows; initializeDeviceContactResponses(devicePhysicsThreadIndex(id,64),rows); }
            [shader("compute")][numthreads(64,1,1)]
            void Restore(uint3 id : SV_DispatchThreadID)
            { DeviceAwakeContactResponseActions<DeviceGraphBodies> rows; applyDeviceContactRestitution(devicePhysicsThreadIndex(id,64),rows); }
            """, "Initialize", "Restore");
        return new(entries[0], entries[1]);
    }

    internal DeviceActivityKernels Activity(bool contactSupports, bool jointBounds)
    {
        ComputePipelineDesc[] entries = Compile("scene_activity", SceneDefines(contactSupports, jointBounds) + """
            #include "physics_contact_activity.slang"
            #include "physics_joint_activity.slang"
            #include "physics_distance_range_activity.slang"
            #include "physics_constraint_activity.slang"
            #include "physics_body_input_activity.slang"
            #if SCENE_CONTACT_SUPPORTS
            #include "physics_contact_support_activity.slang"
            typedef DeviceConstraintActivityGroups<DeviceSupportedContactActivity,DeviceSupportBodyActivity> SceneContactActivity;
            #else
            typedef DeviceContactActivity SceneContactActivity;
            #endif
            typedef DeviceConstraintActivityGroups<
                DeviceConstraintActivityGroups<SceneContactActivity,DeviceJointActivity>,
                DeviceDistanceRangeActivity> SceneOrdinaryActivityRows;
            #if SCENE_JOINT_BOUNDS
            #include "physics_joint_bound_activity.slang"
            typedef DeviceConstraintActivityGroups<SceneOrdinaryActivityRows,DeviceJointBoundActivity> SceneActivityRows;
            #else
            typedef SceneOrdinaryActivityRows SceneActivityRows;
            #endif
            [shader("compute")][numthreads(64,1,1)]
            void ReadRows(uint3 id : SV_DispatchThreadID)
            { SceneActivityRows rows; readDeviceActivityRows(devicePhysicsThreadIndex(id,64),rows); }
            """, "ApplyDeviceActivityInputs", "ClearDeviceActivitySignals", "CollectDeviceActivityBodies", "ReadRows",
            "WakeDeviceBodies", "AdvanceDeviceBodyQuietTime", "FinishDeviceBodyActivity", "ReadDeviceBodyInputActivity");
        return new(entries[0], entries[1], entries[2], entries[3], entries[4], entries[5], entries[6], ReadBodyInputs: entries[7]);
    }

    private static string SceneDefines(bool contactSupports, bool jointBounds) =>
        "#define SCENE_CONTACT_SUPPORTS " + (contactSupports ? "1\n" : "0\n") +
        "#define SCENE_JOINT_BOUNDS " + (jointBounds ? "1\n" : "0\n");

    internal ComputePipelineDesc JointForces() => Compile("scene_joint_forces", """
        #include "physics_avbd_joint_forces.slang"
        [shader("compute")][numthreads(64,1,1)]
        void ReadForces(uint3 id:SV_DispatchThreadID)
        { DeviceJointAvbdForces rows; readDeviceConstraintForces(devicePhysicsThreadIndex(id,64),rows); }
        """, "ReadForces")[0];

    internal ComputePipelineDesc ContactSamples() => Compile("scene_contact_samples", """
        #include "physics_contact_response_samples.slang"
        [shader("compute")][numthreads(64,1,1)]
        void ReadSamples(uint3 id:SV_DispatchThreadID)
        { DeviceResponseEventForces forces; readDeviceContactSamples(devicePhysicsThreadIndex(id,64),forces); }
        """, "ReadSamples")[0];

    internal (ComputePipelineDesc Clear, ComputePipelineDesc Bodies, ComputePipelineDesc Contacts) Statistics()
    {
        ComputePipelineDesc[] entries = Compile("scene_statistics", """
            #include "physics_dispatch.slang"
            #include "physics_body_activity_types.slang"
            #include "physics_constraint_graph.slang"
            #include "physics_contact_geometry_math.slang"
            RWStructuredBuffer<uint> SceneStatistics;
            RWStructuredBuffer<uint> SceneIslandSizes;
            [shader("compute")][numthreads(64,1,1)]
            void Clear(uint3 id : SV_DispatchThreadID)
            {
                uint index=devicePhysicsThreadIndex(id,64);
                uint count,stride; SceneIslandSizes.GetDimensions(count,stride);
                if(index<count) SceneIslandSizes[index]=0;
                if(index<4) SceneStatistics[index]=0;
            }
            [shader("compute")][numthreads(64,1,1)]
            void Bodies(uint3 id : SV_DispatchThreadID)
            {
                uint index=devicePhysicsThreadIndex(id,64);
                uint count,stride; DeviceBodyStates.GetDimensions(count,stride);
                if(index>=count) return;
                uint root=ConstraintGraphParents[index];
                if(root==0xffffffff) return;
                if(deviceBodyActivityAwake(index)) InterlockedAdd(SceneStatistics[1],1);
                uint previous; InterlockedAdd(SceneIslandSizes[root],1,previous);
                InterlockedMax(SceneStatistics[3],previous+1);
            }
            [shader("compute")][numthreads(64,1,1)]
            void Contacts(uint3 id : SV_DispatchThreadID)
            {
                uint index=devicePhysicsThreadIndex(id,64);
                uint count,stride; DeviceContacts.GetDimensions(count,stride);
                if(index>=count||!deviceContactSelected(index)) return;
                InterlockedAdd(SceneStatistics[2],1);
                DeviceContactConstraint point=DeviceContacts[index];
                float separation=point.separation+dot(point.normal,deviceContactNormalDelta(point,false)-point.bodyAnchorDelta);
                if(separation<0) InterlockedMax(SceneStatistics[0],asuint(-separation));
            }
            """, "Clear", "Bodies", "Contacts");
        return (entries[0], entries[1], entries[2]);
    }

    private ComputePipelineDesc[] Compile(string name, string source, params string[] entryNames)
    {
        var target = new TargetDesc
        {
            Format = _target,
            Profile = _global.FindProfile(_target == SlangCompileTarget.Dxil ? "sm_6_0" : "glsl_460"),
            Flags = _target == SlangCompileTarget.Spirv ? SlangTargetFlags.GenerateSpirvDirectly : 0,
        };
        Check(_global.CreateSession(new SessionDesc
        {
            Targets = [target], SearchPaths = [_directory], DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
            CompilerOptionEntries = [new(CompilerOptionName.VulkanUseEntryPointName, CompilerOptionValue.FromInt(1)),
                new(CompilerOptionName.Optimization, CompilerOptionValue.FromInt((int)SlangOptimizationLevel.Maximal)),
                new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0))],
        }, out ISession session));
        IModule? unit = session.LoadModuleFromSource(name, Path.Combine(_directory, name + ".slang"),
            Slang.CreateBlob("#include \"physics_dispatch.slang\"\n" + source), out ISlangBlob? diagnostics);
        if (unit is null) throw new InvalidDataException(diagnostics?.AsString);
        var entries = new IEntryPoint[entryNames.Length];
        var components = new IComponentType[entryNames.Length + 1];
        components[0] = unit;
        for (int index = 0; index < entries.Length; index++)
        {
            Check(unit.FindEntryPointByName(entryNames[index], out entries[index]));
            components[index + 1] = entries[index];
        }
        Check(session.CreateCompositeComponentType(components, out IComponentType combined, out diagnostics), diagnostics);
        Check(combined.Link(out IComponentType linked, out diagnostics), diagnostics);
        Shader shader = Shader.CreateFromSlang(name, _global, session, [unit], entries, linked, [target], generateLibraryCode: false);
        return entryNames.Select(entryName =>
        {
            EntryPointReflection entry = shader.RequireSlangEntry(0, entryName, SlangStage.Compute);
            return new ComputePipelineDesc(shader.SlangCode, shader.GetSlangReflection(0), entry,
                shader.GetSlangEntryBytes(0, entry), entryName, CodeBlob: shader.GetSlangEntryBlob(0, entry));
        }).ToArray();
    }

    private static void Check(SlangResult result, ISlangBlob? diagnostics = null)
    {
        if (result.Failed) throw new InvalidDataException($"Physics shader compilation failed: {result}. {diagnostics?.AsString}");
    }
}
