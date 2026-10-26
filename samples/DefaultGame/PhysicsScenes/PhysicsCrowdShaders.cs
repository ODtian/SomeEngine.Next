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
        #include "physics_length_graph.slang"
        #include "physics_bend_graph.slang"
        #include "physics_motor_bodies.slang"
        typedef DeviceConstraintBodyGroups<DeviceLengthBodies,DeviceBendBodies> CrowdInternalBodies;
        typedef DeviceConstraintBodyGroups<CrowdInternalBodies,DeviceMotorBodies> CrowdBodies;
        """;

    internal DeviceGraphKernels CrowdGraph()
    {
        ComputePipelineDesc[] entries = Compile("crowd_graph", CrowdBodyRows + """

            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id:SV_DispatchThreadID)
            { CrowdBodies rows; writeDeviceConstraintGraphRows(id.x,rows); }
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

    internal DeviceAvbdKernels CrowdAvbd()
    {
        ComputePipelineDesc[] entries = Compile("crowd_avbd", """
            #include "physics_xpbd_length_forces.slang"
            #include "physics_xpbd_bend_forces.slang"
            #include "physics_avbd_motor.slang"
            typedef DeviceAvbdConstraintGroups<DeviceXpbdLengthForces,DeviceXpbdBendForces> CrowdInternalForces;
            typedef DeviceAvbdConstraintGroups<CrowdInternalForces,DeviceMotorAvbdConstraints> CrowdForces;
            [shader("compute")][numthreads(64,1,1)]
            void ClearBodyIndices(uint3 id:SV_DispatchThreadID) { clearDeviceAvbdBodyIndices(id.x); }
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id:SV_DispatchThreadID) { CrowdForces rows; initializeDeviceAvbdRows(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateBody(uint3 id:SV_DispatchThreadID) { CrowdForces rows; updateDeviceAvbdBody(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateDuals(uint3 id:SV_DispatchThreadID) { CrowdForces rows; updateDeviceAvbdDuals(id.x,rows); }
            """, "Initialize", "UpdateBody", "UpdateDuals", "ClearBodyIndices");
        return new(entries[0], entries[1], entries[2], ClearBodyIndices: entries[3]);
    }

    internal DeviceXpbdKernels CrowdXpbd()
    {
        ComputePipelineDesc[] entries = Compile("crowd_xpbd", CrowdBodyRows + """

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
            typedef DeviceXpbdConstraintGroups<InternalRows,DrivenRows> CrowdRows;
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 group:SV_GroupID,uint3 thread:SV_GroupThreadID)
            { CrowdRows rows; initializeDeviceXpbdGraph(group.x,thread.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void Project(uint3 group:SV_GroupID,uint3 thread:SV_GroupThreadID)
            { CrowdRows rows; projectDeviceXpbdGraph(group.x,thread.x,rows); }
            """, "Initialize", "Project");
        return new(entries[0], entries[1]);
    }
}
