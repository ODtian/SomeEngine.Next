#include "physics_constraint_graph_parallel.slang"
#include "physics_contact_data.slang"

// ALM duals update distinct histories. Only velocity-writing contacts need row colors.
[shader("compute")][numthreads(64,1,1)]
void PrepareAvbdGraphColors(uint3 id:SV_DispatchThreadID)
{
    uint index=devicePhysicsThreadIndex(id,64);
    if(index<ConstraintGraphParameters.bodies&&ConstraintGraphActiveBodies[index]!=0)
        InterlockedAdd(ConstraintGraphBuildProgress[0],1);
    if(index>=ConstraintGraphParameters.rows||!deviceGraphRowActive(ConstraintGraphRows[index])) return;
    uint input=ConstraintGraphRows[index].w-1;
    uint contacts,stride; DeviceContacts.GetDimensions(contacts,stride);
    if(input<contacts&&DeviceContacts[input].restitution>0)
        InterlockedAdd(ConstraintGraphBuildProgress[1],1);
    else ConstraintGraphRowColors[index]=0;
}
