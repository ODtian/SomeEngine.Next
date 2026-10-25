#ifndef PHYSICS_GRID_COMMON
#define PHYSICS_GRID_COMMON

struct DeviceCollisionInput
{
    float3 min;
    uint entityIndex;
    float3 max;
    uint entityGeneration;
    uint geometryType;
    uint geometryIndex;
    uint members;
    uint revision;
    uint2 key;
    uint2 categories;
    uint2 allowed;
    uint hasKey;
    uint userValue;
};

struct DeviceCollisionPair
{
    uint firstEntity;
    uint firstGeneration;
    uint secondEntity;
    uint secondGeneration;
    uint firstGeometry;
    uint secondGeometry;
    uint firstBody;
    uint secondBody;
};

struct DeviceGridCell
{
    int4 coordinate;
    uint body;
    uint next;
    uint2 padding;
};

struct DeviceGridParameters
{
    uint bodyCount;
    uint bucketCount;
    uint cellCapacity;
    uint binCount;
    float cellSize;
    uint levelCount;
    uint blockCount;
    uint revision;
    uint pairing;
    uint rules;
    uint2 padding;
};

ConstantBuffer<DeviceGridParameters> GridParameters;
RWStructuredBuffer<DeviceCollisionInput> CollisionBodies;
RWStructuredBuffer<uint> GridHeads;
RWStructuredBuffer<DeviceGridCell> GridCells;
RWStructuredBuffer<uint> GridCounters;
RWStructuredBuffer<uint> BodyLevels;
RWStructuredBuffer<uint> PairCounts;
RWStructuredBuffer<uint> PairOffsets;
RWStructuredBuffer<uint> BlockTotals;
RWStructuredBuffer<uint> BlockOffsets;
RWStructuredBuffer<uint> BinOffsets;
RWStructuredBuffer<uint> PairWritePositions;
RWStructuredBuffer<DeviceCollisionPair> CollisionPairs;

struct DevicePairClassification
{
    uint bin;
    uint firstGeometry;
    uint secondGeometry;
};

interface IDevicePairRule
{
    bool matches(uint firstIndex, uint secondIndex, DeviceCollisionInput first, DeviceCollisionInput second);
};

interface IDevicePairClassifier
{
    DevicePairClassification classify(uint firstIndex, uint secondIndex,
        DeviceCollisionInput first, DeviceCollisionInput second);
};

interface IDevicePairOutput
{
    void add(uint firstIndex, uint secondIndex, DevicePairClassification type);
};

struct DevicePairAnd<TFirst : IDevicePairRule, TSecond : IDevicePairRule> : IDevicePairRule
{
    TFirst first;
    TSecond second;
    bool matches(uint a, uint b, DeviceCollisionInput firstBody, DeviceCollisionInput secondBody)
    { return first.matches(a, b, firstBody, secondBody) && second.matches(a, b, firstBody, secondBody); }
};

struct DevicePairExcept<TRule : IDevicePairRule> : IDevicePairRule
{
    TRule rule;
    bool matches(uint a, uint b, DeviceCollisionInput first, DeviceCollisionInput second)
    { return !rule.matches(a, b, first, second); }
};

uint gridBucket(int4 cell)
{
    uint value = uint(cell.x) * 73856093u ^ uint(cell.y) * 19349663u ^ uint(cell.z) * 83492791u
        ^ uint(cell.w) * 2654435761u;
    return value % GridParameters.bucketCount;
}

bool entityPrecedes(DeviceCollisionInput first, DeviceCollisionInput second)
{
    return first.entityIndex < second.entityIndex ||
        (first.entityIndex == second.entityIndex && first.entityGeneration < second.entityGeneration);
}

float gridCellSize(uint level) { return GridParameters.cellSize * exp2(float(level)); }
int3 gridMinimum(DeviceCollisionInput body, uint level) { return int3(floor(body.min / gridCellSize(level))); }
int3 gridMaximum(DeviceCollisionInput body, uint level) { return int3(floor(body.max / gridCellSize(level))); }
bool isCurrentBody(DeviceCollisionInput body)
{
    return body.revision == GridParameters.revision && body.members != 0;
}

[shader("compute")]
[numthreads(256, 1, 1)]
void ClearDeviceGrid(uint3 id : SV_DispatchThreadID)
{
    if (id.x < GridParameters.bucketCount) GridHeads[id.x] = 0xffffffffu;
    if (id.x == 0) GridCounters[0] = 0;
}

[shader("compute")]
[numthreads(256, 1, 1)]
void BuildDeviceGrid(uint3 id : SV_DispatchThreadID)
{
    uint bodyIndex = id.x;
    if (bodyIndex >= GridParameters.bodyCount) return;
    DeviceCollisionInput body = CollisionBodies[bodyIndex];
    if (!isCurrentBody(body)) return;
    float3 extent = body.max - body.min;
    float size = max(extent.x, max(extent.y, extent.z));
    uint level = uint(clamp(ceil(log2(max(1.0f, size / GridParameters.cellSize))), 0.0f,
        float(GridParameters.levelCount - 1)));
    BodyLevels[bodyIndex] = level;
    int3 minimum = gridMinimum(body, level);
    int3 maximum = gridMaximum(body, level);
    uint cells = uint(maximum.x - minimum.x + 1) * uint(maximum.y - minimum.y + 1) * uint(maximum.z - minimum.z + 1);
    uint start;
    InterlockedAdd(GridCounters[0], cells, start);
    uint index = start;
    for (int z = minimum.z; z <= maximum.z; z++)
    for (int y = minimum.y; y <= maximum.y; y++)
    for (int x = minimum.x; x <= maximum.x; x++)
    {
        int4 cell = int4(x, y, z, int(level));
        uint previous;
        InterlockedExchange(GridHeads[gridBucket(cell)], index, previous);
        DeviceGridCell entry;
        entry.coordinate = cell;
        entry.body = bodyIndex;
        entry.next = previous;
        entry.padding = uint2(0, 0);
        GridCells[index++] = entry;
    }
}

struct DeviceCollisionRules : IDevicePairRule
{
    bool matches(uint firstIndex, uint secondIndex, DeviceCollisionInput first, DeviceCollisionInput second)
    {
        uint rules = GridParameters.rules;
        if ((rules & 4u) != 0 && first.hasKey != 0 && second.hasKey != 0 && all(first.key == second.key))
            return false;
        if ((rules & 3u) != 0 && !any((first.allowed & second.categories) != 0)) return false;
        if ((rules & 1u) != 0 && !any((second.allowed & first.categories) != 0)) return false;
        return true;
    }
};

struct DeviceShapePairs : IDevicePairClassifier
{
    DevicePairClassification classify(uint firstIndex, uint secondIndex,
        DeviceCollisionInput first, DeviceCollisionInput second)
    {
        uint maximum = max(first.geometryType, second.geometryType);
        DevicePairClassification result;
        result.bin = maximum * (maximum + 1) / 2 + min(first.geometryType, second.geometryType);
        result.firstGeometry = first.geometryIndex;
        result.secondGeometry = second.geometryIndex;
        return result;
    }
};

void emitDevicePair<TRule : IDevicePairRule, TClassifier : IDevicePairClassifier, TOutput : IDevicePairOutput>(
    uint firstIndex, uint secondIndex, TRule rule, TClassifier classifier, TOutput output)
{
    DeviceCollisionInput first = CollisionBodies[firstIndex];
    DeviceCollisionInput second = CollisionBodies[secondIndex];
    if (rule.matches(firstIndex, secondIndex, first, second))
        output.add(firstIndex, secondIndex, classifier.classify(firstIndex, secondIndex, first, second));
}

void emitDeviceMembership<TRule : IDevicePairRule, TClassifier : IDevicePairClassifier, TOutput : IDevicePairOutput>(
    uint firstIndex, uint secondIndex, TRule rule, TClassifier classifier, TOutput output)
{
    uint firstMembers = CollisionBodies[firstIndex].members;
    uint secondMembers = CollisionBodies[secondIndex].members;
    uint pairing = GridParameters.pairing;
    if (pairing == 0)
    {
        if ((firstMembers & secondMembers & 1u) != 0)
            emitDevicePair(firstIndex, secondIndex, rule, classifier, output);
    }
    else if (pairing == 1)
    {
        if (((firstMembers & 1u) != 0 && (secondMembers & 2u) != 0) ||
            ((firstMembers & 2u) != 0 && (secondMembers & 1u) != 0))
            emitDevicePair(firstIndex, secondIndex, rule, classifier, output);
    }
    else
    {
        if ((firstMembers & 1u) != 0 && (secondMembers & 2u) != 0)
            emitDevicePair(firstIndex, secondIndex, rule, classifier, output);
        if (firstIndex != secondIndex && (firstMembers & 2u) != 0 && (secondMembers & 1u) != 0)
            emitDevicePair(secondIndex, firstIndex, rule, classifier, output);
    }
}

void enumerateDevicePairs<TRule : IDevicePairRule, TClassifier : IDevicePairClassifier, TOutput : IDevicePairOutput>(
    uint bodyIndex, TRule rule, TClassifier classifier, TOutput output)
{
    DeviceCollisionInput first = CollisionBodies[bodyIndex];
    if (!isCurrentBody(first)) return;
    uint firstLevel = BodyLevels[bodyIndex];
    if (GridParameters.pairing == 2)
        emitDeviceMembership(bodyIndex, bodyIndex, rule, classifier, output);
    for (uint level = firstLevel; level < GridParameters.levelCount; level++)
    {
        int3 minimum = gridMinimum(first, level);
        int3 maximum = gridMaximum(first, level);
        for (int z = minimum.z; z <= maximum.z; z++)
        for (int y = minimum.y; y <= maximum.y; y++)
        for (int x = minimum.x; x <= maximum.x; x++)
        {
            int4 cell = int4(x, y, z, int(level));
            for (uint node = GridHeads[gridBucket(cell)]; node != 0xffffffffu; node = GridCells[node].next)
            {
                DeviceGridCell candidate = GridCells[node];
                if (any(candidate.coordinate != cell) || candidate.body == bodyIndex) continue;
                DeviceCollisionInput second = CollisionBodies[candidate.body];
                if (level == firstLevel && !entityPrecedes(first, second)) continue;
                if (any(int3(x, y, z) != max(minimum, gridMinimum(second, level)))) continue;
                if (any(first.max < second.min) || any(second.max < first.min)) continue;
                if (entityPrecedes(first, second))
                    emitDeviceMembership(bodyIndex, candidate.body, rule, classifier, output);
                else
                    emitDeviceMembership(candidate.body, bodyIndex, rule, classifier, output);
            }
        }
    }
}

struct DevicePairCountOutput : IDevicePairOutput
{
    uint partition;
    void add(uint firstIndex, uint secondIndex, DevicePairClassification type)
    {
        PairCounts[partition * GridParameters.binCount + type.bin]++;
    }
};

struct DevicePairWriteOutput : IDevicePairOutput
{
    uint partition;
    void add(uint firstIndex, uint secondIndex, DevicePairClassification type)
    {
        uint index = PairWritePositions[partition * GridParameters.binCount + type.bin]++;
        DeviceCollisionInput first = CollisionBodies[firstIndex];
        DeviceCollisionInput second = CollisionBodies[secondIndex];
        DeviceCollisionPair pair;
        pair.firstEntity = first.entityIndex;
        pair.firstGeneration = first.entityGeneration;
        pair.secondEntity = second.entityIndex;
        pair.secondGeneration = second.entityGeneration;
        pair.firstGeometry = type.firstGeometry;
        pair.secondGeometry = type.secondGeometry;
        pair.firstBody = firstIndex;
        pair.secondBody = secondIndex;
        CollisionPairs[index] = pair;
    }
};

void countDevicePairs<TRule : IDevicePairRule, TClassifier : IDevicePairClassifier>(
    uint bodyIndex, TRule rule, TClassifier classifier)
{
    if (bodyIndex >= GridParameters.bodyCount) return;
    for (uint bin = 0; bin < GridParameters.binCount; bin++)
        PairCounts[bodyIndex * GridParameters.binCount + bin] = 0;
    DevicePairCountOutput output;
    output.partition = bodyIndex;
    enumerateDevicePairs(bodyIndex, rule, classifier, output);
}

void writeDevicePairs<TRule : IDevicePairRule, TClassifier : IDevicePairClassifier>(
    uint bodyIndex, TRule rule, TClassifier classifier)
{
    if (bodyIndex >= GridParameters.bodyCount) return;
    uint block = bodyIndex / 256;
    for (uint bin = 0; bin < GridParameters.binCount; bin++)
        PairWritePositions[bodyIndex * GridParameters.binCount + bin] = BinOffsets[bin]
            + BlockOffsets[bin * GridParameters.blockCount + block]
            + PairOffsets[bodyIndex * GridParameters.binCount + bin];
    DevicePairWriteOutput output;
    output.partition = bodyIndex;
    enumerateDevicePairs(bodyIndex, rule, classifier, output);
}

groupshared uint ScanValues[256];

[shader("compute")]
[numthreads(256, 1, 1)]
void ScanDevicePairCounts(uint3 id : SV_DispatchThreadID, uint3 group : SV_GroupID, uint3 lane : SV_GroupThreadID)
{
    uint bin = group.y;
    uint value = id.x < GridParameters.bodyCount ? PairCounts[id.x * GridParameters.binCount + bin] : 0;
    ScanValues[lane.x] = value;
    GroupMemoryBarrierWithGroupSync();
    for (uint offset = 1; offset < 256; offset *= 2)
    {
        uint left = lane.x >= offset ? ScanValues[lane.x - offset] : 0;
        GroupMemoryBarrierWithGroupSync();
        ScanValues[lane.x] += left;
        GroupMemoryBarrierWithGroupSync();
    }
    if (id.x < GridParameters.bodyCount) PairOffsets[id.x * GridParameters.binCount + bin] = ScanValues[lane.x] - value;
    if (lane.x == 255) BlockTotals[bin * GridParameters.blockCount + group.x] = ScanValues[255];
}

[shader("compute")]
[numthreads(1, 1, 1)]
void OffsetDevicePairBlocks(uint3 id : SV_DispatchThreadID)
{
    uint bin = id.x;
    uint sum = 0;
    for (uint block = 0; block < GridParameters.blockCount; block++)
    {
        uint index = bin * GridParameters.blockCount + block;
        BlockOffsets[index] = sum;
        sum += BlockTotals[index];
    }
    BinOffsets[bin] = sum;
}

[shader("compute")]
[numthreads(1, 1, 1)]
void OffsetDevicePairBins(uint3 id : SV_DispatchThreadID)
{
    uint sum = 0;
    for (uint bin = 0; bin < GridParameters.binCount; bin++)
    {
        uint count = BinOffsets[bin];
        BinOffsets[bin] = sum;
        sum += count;
    }
    BinOffsets[GridParameters.binCount] = sum;
    GridCounters[1] = sum;
}

#endif
