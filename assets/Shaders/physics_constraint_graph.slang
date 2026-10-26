#ifndef PHYSICS_CONSTRAINT_GRAPH
#define PHYSICS_CONSTRAINT_GRAPH
#include "physics_body_math.slang"
#include "physics_body_selection.slang"
#include "physics_constraint_selection.slang"
interface IDeviceConstraintBodies
{
    uint count(); bool active(uint row);
    uint bodyCount(uint row); uint body(uint row, uint end); bool writes(uint row, uint end);
    uint groupCount(); uint group(uint row);
};
struct DeviceConstraintBodyGroups<TFirst : IDeviceConstraintBodies, TSecond : IDeviceConstraintBodies> : IDeviceConstraintBodies
{
    TFirst first; TSecond second;
    uint count() { return first.count() + second.count(); }
    bool active(uint row) { return row < first.count() ? first.active(row) : second.active(row - first.count()); }
    uint bodyCount(uint row) { return row < first.count() ? first.bodyCount(row) : second.bodyCount(row - first.count()); }
    uint body(uint row, uint end) { return row < first.count() ? first.body(row, end) : second.body(row - first.count(), end); }
    bool writes(uint row, uint end) { return row < first.count() ? first.writes(row, end) : second.writes(row - first.count(), end); }
    uint groupCount() { return first.groupCount() + second.groupCount(); }
    uint group(uint row) { return row < first.count() ? first.group(row) : first.groupCount() + second.group(row - first.count()); }
};
struct DeviceConstraintGraphParameters { uint bodies; uint rows; uint bodiesPerRow; uint padding; };
ConstantBuffer<DeviceConstraintGraphParameters> ConstraintGraphParameters;
// Row = participant start/count, concrete query group, active.
RWStructuredBuffer<uint4> ConstraintGraphRows;
// Participant = body index, write role. Static/unselected bodies remain read inputs.
RWStructuredBuffer<uint2> ConstraintGraphParticipants;
RWStructuredBuffer<uint> ConstraintGraphParents;
RWStructuredBuffer<uint> ConstraintGraphBodyColors;
RWStructuredBuffer<uint> ConstraintGraphRowColors;
RWStructuredBuffer<uint> ConstraintGraphBodyOrder;
RWStructuredBuffer<uint> ConstraintGraphRowOrder;
RWStructuredBuffer<uint2> ConstraintGraphBodyRanges;
RWStructuredBuffer<uint2> ConstraintGraphRowRanges;
RWStructuredBuffer<uint> ConstraintGraphBodyOffsets;
RWStructuredBuffer<uint> ConstraintGraphAdjacency;
RWStructuredBuffer<uint> ConstraintGraphCounts;
RWStructuredBuffer<uint> ConstraintGraphScratch;
RWStructuredBuffer<uint> ConstraintGraphIslands;
struct DeviceGraphBodies : IDeviceBodySelection
{
    bool contains(uint body) { return ConstraintGraphParents[body]!=0xffffffff; }
};
void writeDeviceConstraintGraphRows<T : IDeviceConstraintBodies>(uint row, T source)
{
    if (row >= ConstraintGraphParameters.rows) return;
    uint first = row * ConstraintGraphParameters.bodiesPerRow;
    if (row >= source.count() || !source.active(row)) { ConstraintGraphRows[row] = uint4(first,0,0,0); return; }
    uint count = source.bodyCount(row);
    ConstraintGraphRows[row] = uint4(first, count, source.group(row), 1);
    for (uint end = 0; end < count; end++)
        ConstraintGraphParticipants[first + end] = uint2(source.body(row,end), source.writes(row,end) ? 1 : 0);
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
        ConstraintGraphBodyOffsets[body] = 0; ConstraintGraphScratch[body] = 0;
        ConstraintGraphIslands[body] = 0xffffffff;
    }
    for (uint index = 0; index < BodyStep.count; index++)
    {
        uint body = DeviceBodyIndices[index];
        if (DeviceBodyStates[body].motion == 2) ConstraintGraphParents[body] = body;
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
            activeBodies++;
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
        if (ConstraintGraphParents[body] == 0xffffffff) continue;
        uint color = 0; while (deviceGraphBodyColorUsed(body,color)) color++;
        ConstraintGraphBodyColors[body] = color; bodyColors = max(bodyColors,color+1);
    }
    uint bodyPosition = 0;
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
    }
    uint rowPosition = 0, stages = 0;
    for (uint group = 0; group < groupCount; group++)
    {
        uint colors = 0;
        for (uint index = 0; index < rows; index++)
        {
            uint4 row = ConstraintGraphRows[index];
            if (row.w == 0 || row.z != group) continue;
            uint color = 0; while (deviceGraphRowColorUsed(index,color)) color++;
            ConstraintGraphRowColors[index] = color; colors = max(colors,color+1);
        }
        for (uint color = 0; color < colors; color++)
        {
            uint first = rowPosition;
            for (uint index = 0; index < rows; index++)
                if (ConstraintGraphRows[index].w != 0 && ConstraintGraphRows[index].z == group && ConstraintGraphRowColors[index] == color)
                    ConstraintGraphRowOrder[rowPosition++] = index;
            ConstraintGraphRowRanges[stages++] = uint2(first,rowPosition-first);
        }
    }
    ConstraintGraphCounts[0] = activeBodies; ConstraintGraphCounts[1] = bodyColors;
    ConstraintGraphCounts[2] = stages; ConstraintGraphCounts[3] = islandCount;
}
#endif
