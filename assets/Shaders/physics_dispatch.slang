#ifndef PHYSICS_DISPATCH
#define PHYSICS_DISPATCH
static const uint DevicePhysicsGroupsPerRow=65535;
uint devicePhysicsThreadIndex(uint3 id,uint threadsPerGroup)
{
    return id.x+id.y*(DevicePhysicsGroupsPerRow*threadsPerGroup);
}
#endif
