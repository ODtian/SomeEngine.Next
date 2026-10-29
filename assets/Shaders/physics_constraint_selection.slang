#ifndef PHYSICS_CONSTRAINT_SELECTION
#define PHYSICS_CONSTRAINT_SELECTION
#include "physics_constraint_data.slang"
bool deviceConstraintSelected(DeviceConstraintMetadata metadata,uint2 selected)
{ return metadata.active!=0&&metadata.entityIndex==selected.x&&metadata.entityGeneration==selected.y; }
#endif
