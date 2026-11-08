#ifndef PHYSICS_CONSTRAINT_GRAPH
#define PHYSICS_CONSTRAINT_GRAPH
#include "physics_body_math.slang"
#include "physics_body_selection.slang"
#include "physics_constraint_selection.slang"
#include "physics_constraint_bodies.slang"
struct DeviceConstraintGraphParameters { uint bodies; uint rows; uint bodiesPerRow; uint activeWorkOnly; uint contactCapacity; uint contactRows; uint contactStart; };
ConstantBuffer<DeviceConstraintGraphParameters, ScalarDataLayout> ConstraintGraphParameters;
// Row = participant start/count, concrete query group, active.
RWStructuredBuffer<uint4, ScalarDataLayout> ConstraintGraphRows;
// Participant = body index, write role. Static/unselected bodies remain read inputs.
RWStructuredBuffer<uint2, ScalarDataLayout> ConstraintGraphParticipants;
globallycoherent RWStructuredBuffer<uint, ScalarDataLayout> ConstraintGraphParents;
RWStructuredBuffer<uint, ScalarDataLayout> ConstraintGraphActiveBodies;
globallycoherent RWStructuredBuffer<uint, ScalarDataLayout> ConstraintGraphBodyColors;
RWStructuredBuffer<uint, ScalarDataLayout> ConstraintGraphRowColors;
RWStructuredBuffer<uint, ScalarDataLayout> ConstraintGraphBodyOrder;
RWStructuredBuffer<uint, ScalarDataLayout> ConstraintGraphRowOrder;
RWStructuredBuffer<uint2, ScalarDataLayout> ConstraintGraphBodyRanges;
RWStructuredBuffer<uint2, ScalarDataLayout> ConstraintGraphRowRanges;
RWStructuredBuffer<uint, ScalarDataLayout> ConstraintGraphBodyOffsets;
RWStructuredBuffer<uint, ScalarDataLayout> ConstraintGraphAdjacency;
RWStructuredBuffer<uint, ScalarDataLayout> ConstraintGraphCounts;
RWStructuredBuffer<uint, ScalarDataLayout> ConstraintGraphScratch;
RWStructuredBuffer<uint, ScalarDataLayout> ConstraintGraphIslands;
struct DeviceGraphBodies : IDeviceBodySelection
{
    bool contains(uint body) { return ConstraintGraphActiveBodies[body]!=0; }
};
bool deviceGraphRowActive(uint4 row)
{
    if(row.w==0) return false;
    for(uint end=0;end<row.y;end++)
    {
        uint2 participant=ConstraintGraphParticipants[row.x+end];
        if(participant.y!=0&&ConstraintGraphActiveBodies[participant.x]!=0) return true;
    }
    return false;
}
#ifndef DEVICE_GRAPH_BODY_ACTIVE
#define DEVICE_GRAPH_BODY_ACTIVE(body) true
#endif
uint deviceGraphSourceRow(uint row)
{ return row < ConstraintGraphParameters.contactStart + ConstraintGraphParameters.contactRows ? row : row + ConstraintGraphParameters.contactCapacity - ConstraintGraphParameters.contactRows; }
void writeDeviceConstraintGraphRows<T : IDeviceConstraintBodies>(uint row, T source)
{
    if (row >= ConstraintGraphParameters.rows) return;
    uint input = deviceGraphSourceRow(row);
    uint first = row * ConstraintGraphParameters.bodiesPerRow;
    if (input >= source.count() || !source.active(input)) { ConstraintGraphRows[row] = uint4(first,0,0,0); return; }
    uint count = source.bodyCount(input);
    ConstraintGraphRows[row] = uint4(first, count, source.group(input), input + 1);
    for (uint end = 0; end < count; end++)
        ConstraintGraphParticipants[first + end] = uint2(source.body(input,end), source.writes(input,end) ? 1 : 0);
}
uint deviceGraphRoot(uint body)
{
    uint parent = ConstraintGraphParents[body];
    while (parent != body) { body = parent; parent = ConstraintGraphParents[body]; }
    return body;
}
bool deviceGraphFirstBody(uint4 row, uint end)
{
    uint body = ConstraintGraphParticipants[row.x + end].x;
    for (uint previous = 0; previous < end; previous++)
        if (ConstraintGraphParticipants[row.x + previous].x == body) return false;
    return true;
}
bool deviceGraphRowWrites(uint4 row, uint body)
{
    for (uint end = 0; end < row.y; end++)
    {
        uint2 participant = ConstraintGraphParticipants[row.x + end];
        if (participant.x == body && participant.y != 0) return true;
    }
    return false;
}
bool deviceGraphBodyColorUsed(uint body, uint color)
{
    for (uint entry = ConstraintGraphBodyOffsets[body]; entry < ConstraintGraphBodyOffsets[body + 1]; entry++)
    {
        uint4 row = ConstraintGraphRows[ConstraintGraphAdjacency[entry]];
        for (uint end = 0; end < row.y; end++)
        {
            uint other = ConstraintGraphParticipants[row.x + end].x;
            if (other != body && ConstraintGraphBodyColors[other] == color) return true;
        }
    }
    return false;
}
bool deviceGraphRowColorUsed(uint index, uint color)
{
    uint4 row = ConstraintGraphRows[index];
    for (uint end = 0; end < row.y; end++)
    {
        uint body = ConstraintGraphParticipants[row.x + end].x;
        if (ConstraintGraphParents[body] == 0xffffffff) continue;
        bool writes = deviceGraphRowWrites(row, body);
        for (uint entry = ConstraintGraphBodyOffsets[body]; entry < ConstraintGraphBodyOffsets[body + 1]; entry++)
        {
            uint other = ConstraintGraphAdjacency[entry];
            if (other == index || ConstraintGraphRows[other].z != row.z || ConstraintGraphRowColors[other] != color) continue;
            if (writes || deviceGraphRowWrites(ConstraintGraphRows[other], body)) return true;
        }
    }
    return false;
}

// Connectivity and deterministic greedy order are derived on the device; numerical colors run in SIMT.
void buildDeviceConstraintGraph()
{
    uint bodies = ConstraintGraphParameters.bodies, rows = ConstraintGraphParameters.rows;
    for (uint body = 0; body < bodies; body++)
    {
        ConstraintGraphParents[body] = 0xffffffff; ConstraintGraphBodyColors[body] = 0xffffffff;
        ConstraintGraphActiveBodies[body] = 0;
        ConstraintGraphBodyOffsets[body] = 0; ConstraintGraphScratch[body] = 0;
        ConstraintGraphIslands[body] = 0xffffffff;
    }
    for (uint index = 0; index < BodyStep.count; index++)
    {
        uint body = DeviceBodyIndices[index];
        if (DeviceBodyStates[body].motion == 2)
        {
            ConstraintGraphParents[body] = body;
            ConstraintGraphActiveBodies[body] = DEVICE_GRAPH_BODY_ACTIVE(body)?1:0;
        }
    }
    uint groupCount = 0;
    for (uint index = 0; index < rows; index++)
    {
        ConstraintGraphRowColors[index] = 0xffffffff;
        uint4 row = ConstraintGraphRows[index];
        if (row.w == 0) continue;
        groupCount = max(groupCount, row.z + 1);
        uint first = 0xffffffff;
        for (uint end = 0; end < row.y; end++)
        {
            uint body = ConstraintGraphParticipants[row.x + end].x;
            if (ConstraintGraphParents[body] == 0xffffffff || !deviceGraphFirstBody(row,end)) continue;
            ConstraintGraphBodyOffsets[body]++;
            if (first == 0xffffffff) first = body;
            else
            {
                uint a = deviceGraphRoot(first), b = deviceGraphRoot(body);
                if (a != b) ConstraintGraphParents[max(a,b)] = min(a,b);
            }
        }
    }
    uint adjacencyCount = 0, islandCount = 0, activeBodies = 0;
    for (uint body = 0; body < bodies; body++)
    {
        uint count = ConstraintGraphBodyOffsets[body];
        ConstraintGraphBodyOffsets[body] = adjacencyCount; adjacencyCount += count;
        if (ConstraintGraphParents[body] != 0xffffffff)
        {
            uint root = deviceGraphRoot(body);
            ConstraintGraphIslands[body] = root; ConstraintGraphParents[body] = root;
            if (root == body) islandCount++;
            if(ConstraintGraphActiveBodies[body]!=0) activeBodies++;
        }
    }
    ConstraintGraphBodyOffsets[bodies] = adjacencyCount;
    for (uint index = 0; index < rows; index++)
    {
        uint4 row = ConstraintGraphRows[index];
        for (uint end = 0; end < row.y; end++)
        {
            uint body = ConstraintGraphParticipants[row.x + end].x;
            if (ConstraintGraphParents[body] == 0xffffffff || !deviceGraphFirstBody(row,end)) continue;
            uint offset = ConstraintGraphBodyOffsets[body] + ConstraintGraphScratch[body];
            ConstraintGraphScratch[body]++; ConstraintGraphAdjacency[offset] = index;
        }
    }
    uint bodyColors = 0;
    for (uint index = 0; index < BodyStep.count; index++)
    {
        uint body = DeviceBodyIndices[index];
        if (ConstraintGraphActiveBodies[body] == 0) continue;
        uint color = 0; while (deviceGraphBodyColorUsed(body,color)) color++;
        ConstraintGraphBodyColors[body] = color; bodyColors = max(bodyColors,color+1);
    }
    uint bodyPosition = 0, maximumBodyColorSize = 0;
    for (uint color = 0; color < bodyColors; color++)
    {
        uint first = bodyPosition;
        // Packing original entity order keeps each independent island's chosen local order.
        for (uint index = 0; index < BodyStep.count; index++)
        {
            uint body = DeviceBodyIndices[index];
            if (ConstraintGraphBodyColors[body] == color) ConstraintGraphBodyOrder[bodyPosition++] = body;
        }
        ConstraintGraphBodyRanges[color] = uint2(first,bodyPosition-first);
        maximumBodyColorSize = max(maximumBodyColorSize,bodyPosition-first);
    }
    uint rowPosition = 0, stages = 0;
    for (uint group = 0; group < groupCount; group++)
    {
        uint colors = 0;
        for (uint index = 0; index < rows; index++)
        {
            uint4 row = ConstraintGraphRows[index];
            if (!deviceGraphRowActive(row) || row.z != group) continue;
            uint color = 0; while (deviceGraphRowColorUsed(index,color)) color++;
            ConstraintGraphRowColors[index] = color; colors = max(colors,color+1);
        }
        for (uint color = 0; color < colors; color++)
        {
            uint first = rowPosition;
            for (uint index = 0; index < rows; index++)
                if (deviceGraphRowActive(ConstraintGraphRows[index]) && ConstraintGraphRows[index].z == group && ConstraintGraphRowColors[index] == color)
                    ConstraintGraphRowOrder[rowPosition++] = ConstraintGraphRows[index].w - 1;
            ConstraintGraphRowRanges[stages++] = uint2(first,rowPosition-first);
        }
    }
    ConstraintGraphCounts[0] = activeBodies; ConstraintGraphCounts[1] = bodyColors;
    ConstraintGraphCounts[2] = stages; ConstraintGraphCounts[3] = islandCount;
    ConstraintGraphCounts[4] = maximumBodyColorSize;
}
#endif
