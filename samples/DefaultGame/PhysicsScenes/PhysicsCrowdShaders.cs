using SomeEngine.Graphics;
using SomeEngine.Physics;

namespace DefaultGame;

internal sealed partial class PhysicsSceneShaders
{
    internal DeviceBodyKernels CrowdBodies()
    {
        ComputePipelineDesc[] entries = Compile("crowd_bodies", "#include \"physics_body_dynamics.slang\"",
            "PredictDeviceBodies", "UpdateDeviceBodyVelocities");
        return new(entries[0], entries[1]);
    }

    private const string CrowdBodyRows = """
        #include "physics_dispatch.slang"
        #include "physics_length_graph.slang"
        #include "physics_bend_graph.slang"
        #include "physics_motor_bodies.slang"
        #include "physics_contact_data.slang"
        typedef DeviceConstraintBodyGroups<DeviceLengthBodies,DeviceBendBodies> CrowdInternalBodies;
        typedef DeviceConstraintBodyGroups<CrowdInternalBodies,DeviceMotorBodies> CrowdDrivenBodies;
        #if CROWD_JOINT_NET
        #include "physics_joint_bodies.slang"
        typedef DeviceConstraintBodyGroups<CrowdDrivenBodies,DeviceJointBodies> CrowdConnectedBodies;
        #else
        typedef CrowdDrivenBodies CrowdConnectedBodies;
        #endif
        typedef DeviceConstraintBodyGroups<CrowdConnectedBodies,DeviceContactBodies> CrowdBodies;
        """;

    internal DeviceGraphKernels CrowdGraph(bool jointNet = false)
    {
        ComputePipelineDesc[] entries = Compile("crowd_graph", (jointNet ? "#define CROWD_JOINT_NET 1\n" : "") + CrowdBodyRows + """

            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id:SV_DispatchThreadID)
            { CrowdBodies rows; writeDeviceConstraintGraphRows(devicePhysicsThreadIndex(id,64),rows); }
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

    internal DeviceAvbdKernels CrowdAvbd(bool jointNet, int bodyCapacity)
    {
        ComputePipelineDesc[] entries = Compile("crowd_avbd", (jointNet ? "#define CROWD_JOINT_NET 1\n" : "") + """
            #include "physics_dispatch.slang"
            #include "physics_xpbd_length_forces.slang"
            #include "physics_xpbd_bend_forces.slang"
            #include "physics_avbd_motor.slang"
            #include "physics_avbd_contact.slang"
            typedef DeviceAvbdConstraintGroups<DeviceXpbdLengthForces,DeviceXpbdBendForces> CrowdInternalForces;
            typedef DeviceAvbdConstraintGroups<CrowdInternalForces,DeviceMotorAvbdConstraints> CrowdDrivenForces;
            #if CROWD_JOINT_NET
            #include "physics_avbd_joint.slang"
            typedef DeviceAvbdConstraintGroups<CrowdDrivenForces,DeviceJointAvbdConstraints> CrowdConnectedForces;
            #else
            typedef CrowdDrivenForces CrowdConnectedForces;
            #endif
            typedef DeviceAvbdConstraintGroups<CrowdConnectedForces,DeviceContactPotentialAvbdConstraints> CrowdForces;
            [shader("compute")][numthreads(64,1,1)]
            void ClearBodyIndices(uint3 id:SV_DispatchThreadID) { clearDeviceAvbdBodyIndices(devicePhysicsThreadIndex(id,64)); }
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id:SV_DispatchThreadID) { CrowdForces rows; initializeDeviceAvbdRows(devicePhysicsThreadIndex(id,64),rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateBody(uint3 id:SV_DispatchThreadID) { CrowdForces rows; updateDeviceAvbdBody(devicePhysicsThreadIndex(id,64),rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateDuals(uint3 id:SV_DispatchThreadID) { CrowdForces rows; updateDeviceAvbdDuals(devicePhysicsThreadIndex(id,64),rows); }
            [shader("compute")][numthreads(64,1,1)]
            void RestoreBodyStarts(uint3 id:SV_DispatchThreadID) { restoreDeviceAvbdBodyStartPose(devicePhysicsThreadIndex(id,64)); }
            """, "Initialize", "UpdateBody", "UpdateDuals", "ClearBodyIndices", "RestoreBodyStarts");
        return new(entries[0], entries[1], entries[2], ClearBodyIndices: entries[3])
        {
            GroupInitializationDispatches = new DeviceAvbdGroupDispatch[]
            {
                new(entries[4], DevicePhysicsDispatch.CreateGroups((bodyCapacity + 63) / 64)),
            },
        };
    }

    internal DeviceXpbdKernels CrowdXpbd(bool indirect = false, bool jointNet = false)
    {
        string source = (indirect ? "#define PHYSICS_XPBD_INDIRECT_DISPATCH\n" : "")
            + (jointNet ? "#define CROWD_JOINT_NET 1\n" : "") + CrowdBodyRows + """

            #include "physics_xpbd_graph.slang"
            #include "physics_xpbd_length.slang"
            #include "physics_xpbd_bend.slang"
            struct CrowdDrivenBody : IDeviceXpbdProjection
            {
                bool active(uint row) { return true; }
                void initialize(uint row,DeviceXpbdStep context) { }
                void project(uint row,DeviceXpbdStep context) { }
            };
            typedef DeviceXpbdConstraintRows<DeviceLengthBodies,DeviceXpbdLengthProjection<DeviceGraphBodies>> LengthRows;
            typedef DeviceXpbdConstraintRows<DeviceBendBodies,DeviceXpbdBendProjection<DeviceGraphBodies>> BendRows;
            typedef DeviceXpbdConstraintGroups<LengthRows,BendRows> InternalRows;
            typedef DeviceXpbdConstraintRows<DeviceMotorBodies,CrowdDrivenBody> DrivenRows;
            typedef DeviceXpbdConstraintGroups<InternalRows,DrivenRows> DrivenBodyRows;
            #if CROWD_JOINT_NET
            typedef DeviceXpbdConstraintRows<DeviceJointBodies,CrowdDrivenBody> JointRows;
            typedef DeviceXpbdConstraintGroups<DrivenBodyRows,JointRows> ConnectedBodyRows;
            #else
            typedef DrivenBodyRows ConnectedBodyRows;
            #endif
            typedef DeviceXpbdConstraintRows<DeviceContactBodies,CrowdDrivenBody> ContactRows;
            typedef DeviceXpbdConstraintGroups<ConnectedBodyRows,ContactRows> CrowdRows;
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 group:SV_GroupID,uint3 thread:SV_GroupThreadID)
            { CrowdRows rows; initializeDeviceXpbdGraph(devicePhysicsGroupIndex(group),thread.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void Project(uint3 group:SV_GroupID,uint3 thread:SV_GroupThreadID)
            { CrowdRows rows; projectDeviceXpbdGraph(devicePhysicsGroupIndex(group),thread.x,rows); }
            #ifdef PHYSICS_XPBD_INDIRECT_DISPATCH
            [shader("compute")][numthreads(64,1,1)]
            void WriteDispatchArguments(uint3 id:SV_DispatchThreadID)
            { writeDeviceXpbdDispatchArguments(devicePhysicsThreadIndex(id,64)); }
            #endif
            """;
        string[] names = indirect ? ["Initialize", "Project", "WriteDispatchArguments"] : ["Initialize", "Project"];
        ComputePipelineDesc[] entries = Compile(indirect ? "crowd_xpbd_rows" : "crowd_xpbd", source, names);
        return new(entries[0], entries[1]) { WriteDispatchArguments = indirect ? entries[2] : null };
    }

    internal DeviceGridKernels CrowdGrid(bool jointNet = false)
    {
        ComputePipelineDesc[] entries = Compile("crowd_grid", (jointNet ? "#define CROWD_JOINT_NET 1\n" : "") + """
            #include "physics_dispatch.slang"
            #include "physics_grid_common.slang"
            """ + "\n" + (jointNet ? "#include \"physics_geometry_reference_data.slang\"\n" + SceneBodyPairs + "\n" : "") + """
            struct CrowdCapsuleFacePairs : IDevicePairRule
            {
                bool matches(uint a,uint b,DeviceCollisionInput first,DeviceCollisionInput second)
                {
                    #if CROWD_JOINT_NET
                    if(first.geometryType==0&&second.geometryType==0)
                    {
                        SceneAllowedPairs allowed;
                        return allowed.matches(DeviceGeometryReferences[first.geometryIndex].firstBody,
                            DeviceGeometryReferences[second.geometryIndex].firstBody,first,second);
                    }
                    if(min(first.geometryType,second.geometryType)==0) return true;
                    #endif
                    return min(first.geometryType,second.geometryType)==1&&max(first.geometryType,second.geometryType)==2;
                }
            };
            [shader("compute")][numthreads(256,1,1)]
            void CountPairs(uint3 id:SV_DispatchThreadID)
            { DevicePairAnd<DeviceCollisionRules,CrowdCapsuleFacePairs> rules; DeviceShapePairs types; countDevicePairs(devicePhysicsThreadIndex(id,256),rules,types); }
            [shader("compute")][numthreads(256,1,1)]
            void WritePairs(uint3 id:SV_DispatchThreadID)
            { DevicePairAnd<DeviceCollisionRules,CrowdCapsuleFacePairs> rules; DeviceShapePairs types; writeDevicePairs(devicePhysicsThreadIndex(id,256),rules,types); }
            """, "ClearDeviceGrid", "BuildDeviceGrid", "CountPairs", "ScanDevicePairCounts",
            "OffsetDevicePairBlocks", "OffsetDevicePairBins", "WritePairs");
        return new(entries[0], entries[1], entries[2], entries[3], entries[4], entries[5], entries[6]);
    }

    internal ComputePipelineDesc CrowdGeometry(string type) => Compile("crowd_geometry_" + type, $$"""
        #include "physics_dispatch.slang"
        #include "physics_geometry_update.slang"
        struct CrowdGeometryUpdate : IDeviceGeometryUpdate
        {
            DeviceGeometryBounds update(uint index,inout DeviceGeometryInput geometry)
            {
                {{type}} shape;
                DeviceGeometryBounds bounds=shape.update(index,geometry);
                bounds.min-=.02; bounds.max+=.02;
                return bounds;
            }
        };
        [shader("compute")][numthreads(64,1,1)]
        void Update(uint3 id:SV_DispatchThreadID) { updateDeviceGeometry<CrowdGeometryUpdate>(devicePhysicsThreadIndex(id,64)); }
        """, "Update")[0];

    internal ComputePipelineDesc CrowdCapsuleFaces() => Compile("crowd_capsule_faces", """
        #include "physics_geometry_references.slang"
        struct CrowdCapsuleFaceCollision : IDeviceCollisionNarrowPhase
        {
            DeviceGeometryDistance distance(uint work,DeviceGeometryInput first,DeviceGeometryInput second)
            {
                if(first.geometryType==1)
                { DeviceConvexCollision<DeviceCapsule,DeviceTriangle> shapes; return shapes.distance(work,first,second); }
                DeviceConvexCollision<DeviceTriangle,DeviceCapsule> shapes; return shapes.distance(work,first,second);
            }
            uint manifold(uint work,DeviceGeometryInput first,DeviceGeometryInput second,
                DeviceGeometryDistance distance,out DeviceGeometryDistance points[4])
            {
                if(first.geometryType==1)
                { DeviceConvexCollision<DeviceCapsule,DeviceTriangle> shapes; return shapes.manifold(work,first,second,distance,points); }
                DeviceConvexCollision<DeviceTriangle,DeviceCapsule> shapes; return shapes.manifold(work,first,second,distance,points);
            }
        };
        [shader("compute")][numthreads(64,1,1)]
        void Detect(uint3 id:SV_DispatchThreadID)
        { CrowdCapsuleFaceCollision shapes; detectDeviceReferencedCollision(id.x,shapes); }
        """, "Detect")[0];

    internal ComputePipelineDesc[] CrowdDetection() =>
    [
        ShapePairDetection("crowd_box_box", 0, "DeviceBox", "DeviceBox"),
        ShapePairDetection("crowd_box_capsule", 0, "DeviceBox", "DeviceCapsule"),
        ShapePairDetection("crowd_capsule_capsule", 1, "DeviceCapsule", "DeviceCapsule"),
        ShapePairDetection("crowd_box_face", 0, "DeviceBox", "DeviceTriangle"),
        CrowdCapsuleFaces(),
    ];

    internal DeviceContactKernels CrowdResponses(bool jointNet = false)
    {
        ComputePipelineDesc[] entries = Compile("crowd_contact_responses", (jointNet ? "#define CROWD_JOINT_NET 1\n" : "") + CrowdBodyRows + """

            #include "physics_contact_actions.slang"
            typedef DeviceNoContactResponses<CrowdConnectedBodies> InternalResponses;
            typedef DeviceContactResponseGroups<InternalResponses,DeviceContactResponseActions<DeviceGraphBodies>> Responses;
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id:SV_DispatchThreadID)
            { Responses rows; initializeDeviceContactResponses(devicePhysicsThreadIndex(id,64),rows); }
            [shader("compute")][numthreads(64,1,1)]
            void Restore(uint3 id:SV_DispatchThreadID)
            { Responses rows; applyDeviceContactRestitution(devicePhysicsThreadIndex(id,64),rows); }
            """, "Initialize", "Restore");
        return new(entries[0], entries[1]);
    }
}
