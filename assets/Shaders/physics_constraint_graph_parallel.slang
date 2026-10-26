#ifndef PHYSICS_CONSTRAINT_GRAPH_PARALLEL
#define PHYSICS_CONSTRAINT_GRAPH_PARALLEL
#include "physics_constraint_graph.slang"
RWStructuredBuffer<uint> ConstraintGraphBlockTotals;
RWStructuredBuffer<uint> ConstraintGraphBlockOffsets;
RWStructuredBuffer<uint> ConstraintGraphRowProposals;
RWStructuredBuffer<uint> ConstraintGraphGroupColors;
RWStructuredBuffer<uint> ConstraintGraphGroupOffsets;
RWStructuredBuffer<uint> ConstraintGraphBuildProgress;
groupshared uint GraphDegreeScan[256];

uint deviceGraphPriority(uint index)
{
    uint value=index*2654435761u;
    value^=value>>16; value*=2246822519u; value^=value>>13;
    return value;
}
bool deviceGraphPrecedes(uint first,uint second)
{
    uint a=deviceGraphPriority(first),b=deviceGraphPriority(second);
    return a>b||(a==b&&first<second);
}
[shader("compute")][numthreads(64,1,1)]
void InitializeDeviceGraph(uint3 id:SV_DispatchThreadID)
{
    uint index=id.x;
    if(index<ConstraintGraphParameters.bodies)
    {
        DeviceSelectedBodies selection;
        ConstraintGraphParents[index]=selection.contains(index)&&DeviceBodyStates[index].motion==2?index:0xffffffff;
        ConstraintGraphActiveBodies[index]=ConstraintGraphParents[index]!=0xffffffff&&DEVICE_GRAPH_BODY_ACTIVE(index)?1:0;
        ConstraintGraphBodyColors[index]=0xffffffff; ConstraintGraphBodyOffsets[index]=0;
        ConstraintGraphScratch[index]=0; ConstraintGraphIslands[index]=0xffffffff; ConstraintGraphBodyRanges[index]=uint2(0);
    }
    if(index<ConstraintGraphParameters.rows)
    {
        ConstraintGraphRowColors[index]=0xffffffff; ConstraintGraphRowRanges[index]=uint2(0);
        ConstraintGraphGroupColors[index]=0; ConstraintGraphRowProposals[index]=0xffffffff;
    }
    if(index==0)
        for(uint component=0;component<4;component++)
        { ConstraintGraphCounts[component]=0; ConstraintGraphBuildProgress[component]=0; }
}
void deviceGraphJoin(uint first,uint second)
{
    uint a=deviceGraphRoot(first),b=deviceGraphRoot(second);
    while(a!=b)
    {
        uint lower=min(a,b),higher=max(a,b),previous;
        InterlockedMin(ConstraintGraphParents[higher],lower,previous);
        if(previous==higher) return;
        a=deviceGraphRoot(previous); b=deviceGraphRoot(lower);
    }
}
[shader("compute")][numthreads(64,1,1)]
void ConnectDeviceGraph(uint3 id:SV_DispatchThreadID)
{
    uint index=id.x;
    if(index>=ConstraintGraphParameters.rows) return;
    uint4 row=ConstraintGraphRows[index];
    if(row.w==0) return;
    InterlockedMax(ConstraintGraphBuildProgress[2],row.z+1);
    uint first=0xffffffff;
    for(uint end=0;end<row.y;end++)
    {
        uint body=ConstraintGraphParticipants[row.x+end].x;
        if(ConstraintGraphParents[body]==0xffffffff||!deviceGraphFirstBody(row,end)) continue;
        InterlockedAdd(ConstraintGraphBodyOffsets[body],1);
        if(first==0xffffffff) first=body;
        else deviceGraphJoin(first,body);
    }
}
[shader("compute")][numthreads(64,1,1)]
void CompressDeviceGraph(uint3 id:SV_DispatchThreadID)
{
    uint body=id.x;
    if(body>=ConstraintGraphParameters.bodies||ConstraintGraphParents[body]==0xffffffff) return;
    uint root=deviceGraphRoot(body);
    ConstraintGraphParents[body]=root; ConstraintGraphIslands[body]=root;
    if(ConstraintGraphActiveBodies[body]!=0) InterlockedAdd(ConstraintGraphCounts[0],1);
    if(root==body) InterlockedAdd(ConstraintGraphCounts[3],1);
}
[shader("compute")][numthreads(256,1,1)]
void ScanDeviceGraphDegrees(uint3 id:SV_DispatchThreadID,uint3 group:SV_GroupID,uint3 lane:SV_GroupThreadID)
{
    uint value=id.x<ConstraintGraphParameters.bodies?ConstraintGraphBodyOffsets[id.x]:0;
    GraphDegreeScan[lane.x]=value;
    GroupMemoryBarrierWithGroupSync();
    for(uint offset=1;offset<256;offset*=2)
    {
        uint left=lane.x>=offset?GraphDegreeScan[lane.x-offset]:0;
        GroupMemoryBarrierWithGroupSync(); GraphDegreeScan[lane.x]+=left; GroupMemoryBarrierWithGroupSync();
    }
    if(id.x<ConstraintGraphParameters.bodies) ConstraintGraphBodyOffsets[id.x]=GraphDegreeScan[lane.x]-value;
    if(lane.x==255) ConstraintGraphBlockTotals[group.x]=GraphDegreeScan[255];
}
[shader("compute")][numthreads(1,1,1)]
void OffsetDeviceGraphBlocks(uint3 id:SV_DispatchThreadID)
{
    uint count=(ConstraintGraphParameters.bodies+255)/256,sum=0;
    for(uint block=0;block<count;block++)
    { ConstraintGraphBlockOffsets[block]=sum; sum+=ConstraintGraphBlockTotals[block]; }
    ConstraintGraphBodyOffsets[ConstraintGraphParameters.bodies]=sum;
}
[shader("compute")][numthreads(64,1,1)]
void AddDeviceGraphDegreeOffsets(uint3 id:SV_DispatchThreadID)
{
    uint body=id.x;
    if(body<ConstraintGraphParameters.bodies) ConstraintGraphBodyOffsets[body]+=ConstraintGraphBlockOffsets[body/256];
}
[shader("compute")][numthreads(64,1,1)]
void WriteDeviceGraphAdjacency(uint3 id:SV_DispatchThreadID)
{
    uint rowIndex=id.x;
    if(rowIndex>=ConstraintGraphParameters.rows) return;
    uint4 row=ConstraintGraphRows[rowIndex];
    if(row.w==0) return;
    for(uint end=0;end<row.y;end++)
    {
        uint body=ConstraintGraphParticipants[row.x+end].x;
        if(ConstraintGraphParents[body]==0xffffffff||!deviceGraphFirstBody(row,end)) continue;
        uint position;
        InterlockedAdd(ConstraintGraphScratch[body],1,position);
        ConstraintGraphAdjacency[ConstraintGraphBodyOffsets[body]+position]=rowIndex;
    }
}
[shader("compute")][numthreads(1,1,1)]
void ResetDeviceGraphColorProgress(uint3 id:SV_DispatchThreadID)
{
    ConstraintGraphBuildProgress[0]=ConstraintGraphBuildProgress[1]=0;
}
[shader("compute")][numthreads(64,1,1)]
void ProposeDeviceBodyColors(uint3 id:SV_DispatchThreadID)
{
    uint body=id.x;
    if(body>=ConstraintGraphParameters.bodies) return;
    uint color=0xffffffff;
    if(ConstraintGraphActiveBodies[body]!=0&&ConstraintGraphBodyColors[body]==0xffffffff)
    { color=0; while(deviceGraphBodyColorUsed(body,color)) color++; }
    ConstraintGraphScratch[body]=color;
}
[shader("compute")][numthreads(64,1,1)]
void ResolveDeviceBodyColors(uint3 id:SV_DispatchThreadID)
{
    uint body=id.x;
    if(body>=ConstraintGraphParameters.bodies) return;
    uint color=ConstraintGraphScratch[body];
    if(color==0xffffffff) return;
    bool selected=true;
    for(uint entry=ConstraintGraphBodyOffsets[body];entry<ConstraintGraphBodyOffsets[body+1];entry++)
    {
        uint4 row=ConstraintGraphRows[ConstraintGraphAdjacency[entry]];
        for(uint end=0;end<row.y;end++)
        {
            uint other=ConstraintGraphParticipants[row.x+end].x;
            if(other!=body&&ConstraintGraphScratch[other]==color&&deviceGraphPrecedes(other,body)) selected=false;
        }
    }
    if(selected) { ConstraintGraphBodyColors[body]=color; InterlockedMax(ConstraintGraphCounts[1],color+1); }
    else InterlockedAdd(ConstraintGraphBuildProgress[0],1);
}
[shader("compute")][numthreads(64,1,1)]
void ProposeDeviceRowColors(uint3 id:SV_DispatchThreadID)
{
    uint row=id.x;
    if(row>=ConstraintGraphParameters.rows) return;
    uint color=0xffffffff;
    if(deviceGraphRowActive(ConstraintGraphRows[row])&&ConstraintGraphRowColors[row]==0xffffffff)
    { color=0; while(deviceGraphRowColorUsed(row,color)) color++; }
    ConstraintGraphRowProposals[row]=color;
}
[shader("compute")][numthreads(64,1,1)]
void ResolveDeviceRowColors(uint3 id:SV_DispatchThreadID)
{
    uint rowIndex=id.x;
    if(rowIndex>=ConstraintGraphParameters.rows) return;
    uint color=ConstraintGraphRowProposals[rowIndex];
    if(color==0xffffffff) return;
    uint4 row=ConstraintGraphRows[rowIndex]; bool selected=true;
    for(uint end=0;end<row.y;end++)
    {
        uint body=ConstraintGraphParticipants[row.x+end].x;
        if(ConstraintGraphParents[body]==0xffffffff) continue;
        bool writes=deviceGraphRowWrites(row,body);
        for(uint entry=ConstraintGraphBodyOffsets[body];entry<ConstraintGraphBodyOffsets[body+1];entry++)
        {
            uint other=ConstraintGraphAdjacency[entry];
            if(other==rowIndex||ConstraintGraphRows[other].z!=row.z||ConstraintGraphRowProposals[other]!=color) continue;
            if((writes||deviceGraphRowWrites(ConstraintGraphRows[other],body))&&deviceGraphPrecedes(other,rowIndex)) selected=false;
        }
    }
    if(selected) ConstraintGraphRowColors[rowIndex]=color;
    else InterlockedAdd(ConstraintGraphBuildProgress[1],1);
}
[shader("compute")][numthreads(64,1,1)]
void CountDeviceGraphColors(uint3 id:SV_DispatchThreadID)
{
    uint index=id.x;
    if(index<ConstraintGraphParameters.bodies&&ConstraintGraphActiveBodies[index]!=0)
        InterlockedAdd(ConstraintGraphBodyRanges[ConstraintGraphBodyColors[index]].y,1);
    if(index<ConstraintGraphParameters.rows&&deviceGraphRowActive(ConstraintGraphRows[index]))
        InterlockedMax(ConstraintGraphGroupColors[ConstraintGraphRows[index].z],ConstraintGraphRowColors[index]+1);
}
[shader("compute")][numthreads(1,1,1)]
void OffsetDeviceGraphGroups(uint3 id:SV_DispatchThreadID)
{
    uint sum=0,count=ConstraintGraphBuildProgress[2];
    for(uint group=0;group<count;group++)
    { ConstraintGraphGroupOffsets[group]=sum; sum+=ConstraintGraphGroupColors[group]; }
    ConstraintGraphGroupOffsets[count]=sum; ConstraintGraphCounts[2]=sum;
}
[shader("compute")][numthreads(64,1,1)]
void CountDeviceGraphRows(uint3 id:SV_DispatchThreadID)
{
    uint row=id.x;
    if(row>=ConstraintGraphParameters.rows||!deviceGraphRowActive(ConstraintGraphRows[row])) return;
    uint stage=ConstraintGraphGroupOffsets[ConstraintGraphRows[row].z]+ConstraintGraphRowColors[row];
    InterlockedAdd(ConstraintGraphRowRanges[stage].y,1);
}
[shader("compute")][numthreads(1,1,1)]
void OffsetDeviceGraphRanges(uint3 id:SV_DispatchThreadID)
{
    uint bodySum=0,rowSum=0;
    for(uint color=0;color<ConstraintGraphCounts[1];color++)
    {
        uint2 range=ConstraintGraphBodyRanges[color]; range.x=bodySum; bodySum+=range.y;
        ConstraintGraphBodyRanges[color]=range; ConstraintGraphScratch[color]=0;
    }
    for(uint stage=0;stage<ConstraintGraphCounts[2];stage++)
    {
        uint2 range=ConstraintGraphRowRanges[stage]; range.x=rowSum; rowSum+=range.y;
        ConstraintGraphRowRanges[stage]=range; ConstraintGraphRowProposals[stage]=0;
    }
}
[shader("compute")][numthreads(64,1,1)]
void WriteDeviceGraphWork(uint3 id:SV_DispatchThreadID)
{
    uint index=id.x;
    if(index<ConstraintGraphParameters.bodies&&ConstraintGraphActiveBodies[index]!=0)
    {
        uint color=ConstraintGraphBodyColors[index],position;
        InterlockedAdd(ConstraintGraphScratch[color],1,position);
        ConstraintGraphBodyOrder[ConstraintGraphBodyRanges[color].x+position]=index;
    }
    if(index<ConstraintGraphParameters.rows&&deviceGraphRowActive(ConstraintGraphRows[index]))
    {
        uint stage=ConstraintGraphGroupOffsets[ConstraintGraphRows[index].z]+ConstraintGraphRowColors[index],position;
        InterlockedAdd(ConstraintGraphRowProposals[stage],1,position);
        ConstraintGraphRowOrder[ConstraintGraphRowRanges[stage].x+position]=index;
    }
}
#endif
