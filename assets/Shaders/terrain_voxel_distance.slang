struct DistanceParameters
{
    int PageSize;
    int SourcePageX;
    int SourcePageY;
    int SourcePageZ;
    int SourcePageWidth;
    int SourcePageHeight;
    int ReadMinX;
    int ReadMinY;
    int ReadMinZ;
    int ReadWidth;
    int ReadHeight;
    int ReadDepth;
    int MinX;
    int MinY;
    int MinZ;
    int Width;
    int Height;
    int Depth;
    int PageX;
    int PageY;
    int PageZ;
    int MaxSubdivisions;
    float InitialValue;
    float SampleSpacing;
    float DistanceTolerance;
};

struct DistanceCell
{
    float Corner0;
    float Corner1;
    float Corner2;
    float Corner3;
    float Corner4;
    float Corner5;
    float Corner6;
    float Corner7;
    uint HasZero;
};

ConstantBuffer<DistanceParameters> Parameters;
StructuredBuffer<float> SourceSamples;
StructuredBuffer<int> SourceOffsets;
RWStructuredBuffer<DistanceCell> Cells;
RWStructuredBuffer<float> Distances;
RWStructuredBuffer<float> Errors;
RWStructuredBuffer<uint> Accuracy;
RWStructuredBuffer<float> Output;

float Infinity() { return asfloat(0x7f800000u); }
int3 ReadMinimum() { return int3(Parameters.ReadMinX, Parameters.ReadMinY, Parameters.ReadMinZ); }
int3 RangeMinimum() { return int3(Parameters.MinX, Parameters.MinY, Parameters.MinZ); }
int3 RangeSize() { return int3(Parameters.Width, Parameters.Height, Parameters.Depth); }
int CellCount() { return (Parameters.ReadWidth - 1) * (Parameters.ReadHeight - 1) * (Parameters.ReadDepth - 1); }
int SampleCount() { return Parameters.Width * Parameters.Height * Parameters.Depth; }
int3 Corner(int index) { return int3(index & 1, (index >> 1) & 1, index >> 2); }

int PageCoordinate(int sample)
{
    return sample >= 0 ? sample / Parameters.PageSize : (sample + 1) / Parameters.PageSize - 1;
}

float ReadSource(int3 position)
{
    int size = Parameters.PageSize;
    int3 page = int3(PageCoordinate(position.x), PageCoordinate(position.y), PageCoordinate(position.z));
    int3 sourcePage = page - int3(Parameters.SourcePageX, Parameters.SourcePageY, Parameters.SourcePageZ);
    int offset = SourceOffsets[(sourcePage.z * Parameters.SourcePageHeight + sourcePage.y) * Parameters.SourcePageWidth + sourcePage.x];
    int3 local = position - page * size;
    return offset < 0 ? Parameters.InitialValue : SourceSamples[offset + (local.z * size + local.y) * size + local.x];
}

float SampleCell(DistanceCell cell, float3 position)
{
    return lerp(lerp(lerp(cell.Corner0, cell.Corner1, position.x), lerp(cell.Corner2, cell.Corner3, position.x), position.y),
        lerp(lerp(cell.Corner4, cell.Corner5, position.x), lerp(cell.Corner6, cell.Corner7, position.x), position.y), position.z);
}

int3 CellPosition(int index)
{
    int width = Parameters.ReadWidth - 1, height = Parameters.ReadHeight - 1;
    return int3(index % width, index / width % height, index / (width * height)) + ReadMinimum();
}

int3 SamplePosition(int index)
{
    return int3(index % Parameters.Width, index / Parameters.Width % Parameters.Height,
        index / (Parameters.Width * Parameters.Height)) + RangeMinimum();
}

[shader("compute")]
[numthreads(64, 1, 1)]
void PrepareDistanceCells(uint3 threadId : SV_DispatchThreadID)
{
    int index = threadId.x;
    if (index == 0) { Accuracy[0] = 0; Accuracy[1] = 0; }
    if (index >= CellCount()) return;
    int3 position = CellPosition(index);
    DistanceCell cell;
    cell.Corner0 = ReadSource(position + Corner(0));
    cell.Corner1 = ReadSource(position + Corner(1));
    cell.Corner2 = ReadSource(position + Corner(2));
    cell.Corner3 = ReadSource(position + Corner(3));
    cell.Corner4 = ReadSource(position + Corner(4));
    cell.Corner5 = ReadSource(position + Corner(5));
    cell.Corner6 = ReadSource(position + Corner(6));
    cell.Corner7 = ReadSource(position + Corner(7));
    float low = min(min(min(cell.Corner0, cell.Corner1), min(cell.Corner2, cell.Corner3)),
        min(min(cell.Corner4, cell.Corner5), min(cell.Corner6, cell.Corner7)));
    float high = max(max(max(cell.Corner0, cell.Corner1), max(cell.Corner2, cell.Corner3)),
        max(max(cell.Corner4, cell.Corner5), max(cell.Corner6, cell.Corner7)));
    cell.HasZero = low <= 0.0f && high >= 0.0f ? 1u : 0u;
    Cells[index] = cell;
}

void Refine(DistanceCell cell, float3 origin, float tolerance, int maxDepth, inout float lower, inout float upper)
{
    static const int2 Edges[12] = { int2(0, 1), int2(2, 3), int2(4, 5), int2(6, 7),
        int2(0, 2), int2(1, 3), int2(4, 6), int2(5, 7), int2(0, 4), int2(1, 5), int2(2, 6), int2(3, 7) };
    float values[8];
    int level = 0;
    int3 node = int3(0, 0, 0);
    while (true)
    {
        float width = 1.0f / (1u << level);
        float3 fraction = float3(node) * width;
        float3 minimum = origin + fraction;
        float3 maximum = minimum + width;
        float bound = length(max(max(minimum, -maximum), 0.0f));
        bool split = false;
        if (bound >= upper - tolerance)
            lower = min(lower, bound);
        else
        {
            float low = Infinity(), high = -Infinity();
            for (int corner = 0; corner < 8; corner++)
            {
                float value = SampleCell(cell, fraction + float3(Corner(corner)) * width);
                values[corner] = value;
                low = min(low, value);
                high = max(high, value);
            }
            if (low <= 0.0f && high >= 0.0f)
            {
                for (int edge = 0; edge < 12; edge++)
                {
                    int first = Edges[edge].x, second = Edges[edge].y;
                    float a = values[first], b = values[second];
                    float3 start = minimum + float3(Corner(first)) * width;
                    if (a == 0.0f) upper = min(upper, length(start));
                    if (b == 0.0f) upper = min(upper, length(minimum + float3(Corner(second)) * width));
                    if ((a < 0.0f && b > 0.0f) || (a > 0.0f && b < 0.0f))
                    {
                        float3 point = start + float3(Corner(second) - Corner(first)) * (width * a / (a - b));
                        upper = min(upper, length(point));
                    }
                }
                split = bound < upper - tolerance && level < maxDepth;
                if (!split) lower = min(lower, bound);
            }
        }
        if (split)
        {
            level++;
            node *= 2;
            continue;
        }
        while (level > 0 && ((node.x & 1) | ((node.y & 1) << 1) | ((node.z & 1) << 2)) == 7)
        {
            node >>= 1;
            level--;
        }
        if (level == 0) break;
        int next = ((node.x & 1) | ((node.y & 1) << 1) | ((node.z & 1) << 2)) + 1;
        node = (node & ~int3(1, 1, 1)) | int3(next & 1, (next >> 1) & 1, (next >> 2) & 1);
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void RebuildDistances(uint3 threadId : SV_DispatchThreadID)
{
    int index = threadId.x;
    if (index >= SampleCount()) return;
    int3 position = SamplePosition(index);
    float value = ReadSource(position);
    if (value == 0.0f) { Distances[index] = 0.0f; Errors[index] = 0.0f; return; }
    float lower = Infinity(), upper = Infinity();
    float tolerance = Parameters.DistanceTolerance / Parameters.SampleSpacing;
    for (int cellIndex = 0; cellIndex < CellCount(); cellIndex++)
    {
        DistanceCell cell = Cells[cellIndex];
        if (cell.HasZero != 0)
            Refine(cell, float3(CellPosition(cellIndex) - position), tolerance, min(Parameters.MaxSubdivisions, 20), lower, upper);
    }
    Distances[index] = (value < 0.0f ? -upper : upper) * Parameters.SampleSpacing;
    Errors[index] = isfinite(upper) ? max(0.0f, upper - min(lower, upper)) * Parameters.SampleSpacing : Infinity();
}

float CrossingShift(float first, float second, float resultFirst, float resultSecond)
{
    return ((first < 0.0f && second > 0.0f) || (first > 0.0f && second < 0.0f))
        ? abs(first / (first - second) - resultFirst / (resultFirst - resultSecond)) : 0.0f;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CheckDistanceAccuracy(uint3 threadId : SV_DispatchThreadID)
{
    int index = threadId.x;
    if (index >= SampleCount()) return;
    int3 position = SamplePosition(index);
    int3 last = RangeMinimum() + RangeSize() - 1;
    float previous = ReadSource(position), current = Distances[index];
    float shift = 0.0f;
    if (!isfinite(current) || sign(previous) != sign(current)) shift = Infinity();
    else
    {
        if (position.x < last.x)
            shift = max(shift, CrossingShift(previous, ReadSource(position + int3(1, 0, 0)), current, Distances[index + 1]));
        if (position.y < last.y)
            shift = max(shift, CrossingShift(previous, ReadSource(position + int3(0, 1, 0)), current, Distances[index + Parameters.Width]));
        if (position.z < last.z)
            shift = max(shift, CrossingShift(previous, ReadSource(position + int3(0, 0, 1)), current,
                Distances[index + Parameters.Width * Parameters.Height]));
    }
    InterlockedMax(Accuracy[0], asuint(Errors[index]));
    InterlockedMax(Accuracy[1], asuint(shift * Parameters.SampleSpacing));
}

[shader("compute")]
[numthreads(4, 4, 4)]
void WriteDistancePage(uint3 threadId : SV_DispatchThreadID)
{
    int size = Parameters.PageSize;
    if (any(threadId >= size)) return;
    int3 position = int3(Parameters.PageX, Parameters.PageY, Parameters.PageZ) + int3(threadId);
    int3 local = position - RangeMinimum();
    float value = all(local >= 0) && all(local < RangeSize())
        ? Distances[(local.z * Parameters.Height + local.y) * Parameters.Width + local.x] : ReadSource(position);
    Output[(threadId.z * size + threadId.y) * size + threadId.x] = value;
}
