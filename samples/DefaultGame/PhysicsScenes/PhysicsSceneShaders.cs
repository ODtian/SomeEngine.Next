using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Physics;
using SomeEngine.Resources;

namespace DefaultGame;

internal sealed class PhysicsSceneShaders
{
    private readonly IGlobalSession _global;
    private readonly SlangCompileTarget _target;
    private readonly string _directory;

    internal PhysicsSceneShaders(SlangCompileTarget target, string directory)
    {
        _target = target; _directory = directory;
        Check(Slang.CreateGlobalSession(Slang.ApiVersion, out _global));
    }

    internal DeviceGridKernels Grid()
    {
        ComputePipelineDesc[] entries = Compile("scene_grid", "#include \"physics_grid.slang\"",
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
        void Update(uint3 id : SV_DispatchThreadID) { updateDeviceGeometry<SceneBoxUpdate>(id.x); }
        """, "Update")[0];

    internal ComputePipelineDesc Detection() => Compile("scene_box_detection", """
        #include "physics_geometry_references.slang"
        [shader("compute")][numthreads(64,1,1)]
        void Detect(uint3 id : SV_DispatchThreadID)
        { DeviceConvexCollision<DeviceBox,DeviceBox> collision; detectDeviceReferencedCollision(id.x,collision); }
        """, "Detect")[0];

    internal DeviceContactMatchKernels Matching()
    {
        ComputePipelineDesc[] entries = Compile("scene_contact_matching", "#include \"physics_contact_matching.slang\"",
            "ClearDeviceContactMatches", "IndexDeviceContactMatches", "MatchDeviceContacts");
        return new(entries[0], entries[1], entries[2]);
    }

    internal ComputePipelineDesc ContactGeometry() => Compile("scene_contact_geometry",
        "#include \"physics_contact_geometry.slang\"", "UpdateDeviceContactGeometry")[0];

    internal DeviceGraphKernels Graph(bool contactSupports)
    {
        ComputePipelineDesc[] entries = Compile("scene_contact_graph", SupportDefine(contactSupports) + """
            #include "physics_body_activity_types.slang"
            #define DEVICE_GRAPH_BODY_ACTIVE(body) deviceBodyActivityAwake(body)
            #include "physics_contact_data.slang"
            #include "physics_joint_bodies.slang"
            #include "physics_distance_range_bodies.slang"
            typedef DeviceConstraintBodyGroups<
                DeviceConstraintBodyGroups<DeviceContactBodies,DeviceJointBodies>,
                DeviceDistanceRangeBodies> SceneConstraintBodies;
            #if SCENE_CONTACT_SUPPORTS
            #include "physics_contact_support_bodies.slang"
            typedef DeviceConstraintBodyGroups<SceneConstraintBodies,DeviceSupportBodies> SceneGraphRows;
            #else
            typedef SceneConstraintBodies SceneGraphRows;
            #endif
            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id : SV_DispatchThreadID)
            { SceneGraphRows rows; writeDeviceConstraintGraphRows(id.x,rows); }
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

    internal DeviceAvbdKernels Avbd(bool contactSupports)
    {
        ComputePipelineDesc[] entries = Compile("scene_contact_avbd", SupportDefine(contactSupports) + """
            #include "physics_avbd_contact.slang"
            #include "physics_avbd_joint.slang"
            #include "physics_avbd_distance_range.slang"
            #include "physics_awake_avbd.slang"
            #if SCENE_CONTACT_SUPPORTS
            #include "physics_avbd_contact_support.slang"
            #endif
            struct SceneContactAvbdConstraints : IDeviceAvbdConstraints
            {
                DeviceContactPotentialAvbdConstraints inner;
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
                    float mobility=deviceContactWeight(point,point.normal,bodies);
                    float conditioning=1/(context.duration*context.duration*mobility);
                    DeviceAvbdContactState state=DeviceAvbdContactStates[row];
                    state.normalPenalty=clamp(max(conditioning,state.normalPenalty*context.retention),1,1e9);
                    state.tangentPenalty=clamp(max(conditioning,state.tangentPenalty*context.retention),1,1e9);
                    DeviceAvbdContactStates[row]=state;
                }
                void accumulate(uint row,uint bodyIndex,DeviceAvbdStep context,inout DeviceAvbdEquation equation)
                {
                    #if SCENE_CONTACT_SUPPORTS
                    bool supported=deviceConstraintSelected(DeviceContactsMetadata[row],DeviceContactSupportSelection[row]);
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
                    float gap=point.separation+dot(point.normal,deviceContactDelta(point,false)-point.bodyAnchorDelta);
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
                DeviceAvbdConstraintGroups<SceneContactAvbdConstraints,DeviceJointAvbdConstraints>,
                DeviceDistanceRangeAvbdConstraints> SceneAvbdConstraints;
            #if SCENE_CONTACT_SUPPORTS
            typedef DeviceAvbdConstraintGroups<SceneAvbdConstraints,DeviceContactSupportAvbdConstraints> SceneSolveRows;
            #else
            typedef SceneAvbdConstraints SceneSolveRows;
            #endif
            [shader("compute")][numthreads(64,1,1)]
            void ClearBodyIndices(uint3 id : SV_DispatchThreadID) { clearDeviceAvbdBodyIndices(id.x); }
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID)
            { DeviceAwakeAvbdConstraints<SceneSolveRows> rows; DeviceAwakeBodies bodies; initializeDeviceAvbdRows(id.x,rows,bodies); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateBody(uint3 id : SV_DispatchThreadID)
            { DeviceAwakeAvbdConstraints<SceneSolveRows> rows; updateDeviceAvbdBody(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateDuals(uint3 id : SV_DispatchThreadID)
            { DeviceAwakeAvbdConstraints<SceneSolveRows> rows; updateDeviceAvbdDuals(id.x,rows); }
            [shader("compute")][numthreads(1,1,1)]
            void InitializeSupports(uint3 id:SV_DispatchThreadID)
            {
                #if SCENE_CONTACT_SUPPORTS
                DeviceAwakeBodies bodies; initializeDeviceContactSupports(AvbdStep,bodies);
                #endif
            }
            [shader("compute")][numthreads(1,1,1)]
            void UpdateSupports(uint3 id:SV_DispatchThreadID)
            {
                #if SCENE_CONTACT_SUPPORTS
                DeviceAwakeBodies bodies; updateDeviceContactSupports(AvbdStep,bodies);
                #endif
            }
            """, "ClearBodyIndices", "Initialize", "UpdateBody", "UpdateDuals", "InitializeSupports", "UpdateSupports");
        return new(entries[1], entries[2], entries[3],
            contactSupports ? entries[4] : null, contactSupports ? entries[5] : null, entries[0]);
    }

    internal DeviceContactKernels Responses()
    {
        ComputePipelineDesc[] entries = Compile("scene_contact_responses", """
            #include "physics_awake_contact_actions.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID)
            { DeviceAwakeContactResponseActions<DeviceGraphBodies> rows; initializeDeviceContactResponses(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void Restore(uint3 id : SV_DispatchThreadID)
            { DeviceAwakeContactResponseActions<DeviceGraphBodies> rows; applyDeviceContactRestitution(id.x,rows); }
            """, "Initialize", "Restore");
        return new(entries[0], entries[1]);
    }

    internal DeviceActivityKernels Activity(bool contactSupports)
    {
        ComputePipelineDesc[] entries = Compile("scene_activity", SupportDefine(contactSupports) + """
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
                DeviceDistanceRangeActivity> SceneActivityRows;
            [shader("compute")][numthreads(64,1,1)]
            void ReadRows(uint3 id : SV_DispatchThreadID)
            { SceneActivityRows rows; readDeviceActivityRows(devicePhysicsThreadIndex(id,64),rows); }
            """, "ApplyDeviceActivityInputs", "ClearDeviceActivitySignals", "CollectDeviceActivityBodies", "ReadRows",
            "WakeDeviceBodies", "AdvanceDeviceBodyQuietTime", "FinishDeviceBodyActivity", "ReadDeviceBodyInputActivity");
        return new(entries[0], entries[1], entries[2], entries[3], entries[4], entries[5], entries[6], ReadBodyInputs: entries[7]);
    }

    private static string SupportDefine(bool enabled) => "#define SCENE_CONTACT_SUPPORTS " + (enabled ? "1\n" : "0\n");

    internal ComputePipelineDesc JointForces() => Compile("scene_joint_forces", """
        #include "physics_avbd_joint_forces.slang"
        [shader("compute")][numthreads(64,1,1)]
        void ReadForces(uint3 id:SV_DispatchThreadID)
        { DeviceJointAvbdForces rows; readDeviceConstraintForces(id.x,rows); }
        """, "ReadForces")[0];

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
                float separation=point.separation+dot(point.normal,deviceContactDelta(point,false)-point.bodyAnchorDelta);
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
                new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0))],
        }, out ISession session));
        IModule? unit = session.LoadModuleFromSource(name, Path.Combine(_directory, name + ".slang"),
            Slang.CreateBlob(source), out ISlangBlob? diagnostics);
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
