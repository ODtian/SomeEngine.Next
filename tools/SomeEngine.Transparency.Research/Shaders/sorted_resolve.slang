#include "comparison_common.slang"
StructuredBuffer<float4> Colors;
RWStructuredBuffer<uint> Heads;
RWStructuredBuffer<Fragment> Fragments;
RWTexture2DArray<float4> SceneColor;

#if SORT_LINKED_MERGE
uint SortFragments(uint head)
{
    uint width = 1;
    while (true)
    {
        uint left = head;
        head = 0xffffffff;
        uint tail = 0xffffffff;
        uint merges = 0;
        while (left != 0xffffffff)
        {
            merges++;
            uint right = left, leftCount = 0;
            for (uint index = 0; index < width && right != 0xffffffff; index++)
            {
                leftCount++;
                right = Fragments[right].NextIndex;
            }
            uint rightCount = width;
            while (leftCount > 0 || (rightCount > 0 && right != 0xffffffff))
            {
                uint fragment;
                if (leftCount == 0)
                {
                    fragment = right; right = Fragments[right].NextIndex; rightCount--;
                }
                else if (rightCount == 0 || right == 0xffffffff || Before(Fragments[left], Fragments[right]))
                {
                    fragment = left; left = Fragments[left].NextIndex; leftCount--;
                }
                else
                {
                    fragment = right; right = Fragments[right].NextIndex; rightCount--;
                }
                if (tail == 0xffffffff) head = fragment;
                else Fragments[tail].NextIndex = fragment;
                tail = fragment;
            }
            left = right;
        }
        Fragments[tail].NextIndex = 0xffffffff;
        if (merges <= 1) return head;
        width *= 2;
    }
}
#endif

[shader("compute")]
[numthreads(8, 8, 1)]
void ResolveSorted(uint3 threadId : SV_DispatchThreadID)
{
    uint2 pixel = threadId.xy;
    if (any(pixel >= uint2(Settings.Width, Settings.Height))) return;
    uint head = Heads[pixel.y * Settings.Width + pixel.x];
    if (head == 0xffffffff) return;
#if SORT_LINKED_MERGE
    head = SortFragments(head);
    float3 color = 0;
    float transmittance = 1;
    while (head != 0xffffffff)
    {
        Fragment fragment = Fragments[head];
        float4 surface = Colors[fragment.ColorIndex];
        color += transmittance * surface.a * surface.rgb;
        transmittance *= 1 - surface.a;
        head = fragment.NextIndex;
    }
#else
    Fragment sorted[SORTED_FRAGMENT_COUNT];
    uint count = 0;
    while (head != 0xffffffff)
    {
        Fragment fragment = Fragments[head];
        uint position = count;
        while (position > 0 && Before(fragment, sorted[position - 1]))
        {
            sorted[position] = sorted[position - 1];
            position--;
        }
        sorted[position] = fragment;
        count++;
        head = fragment.NextIndex;
    }
    float3 color = 0;
    float transmittance = 1;
    for (uint index = 0; index < count; index++)
    {
        float4 surface = Colors[sorted[index].ColorIndex];
        color += transmittance * surface.a * surface.rgb;
        transmittance *= 1 - surface.a;
    }
#endif
    float4 background = SceneColor[uint3(pixel, 0)];
    SceneColor[uint3(pixel, 0)] = float4(color + transmittance * background.rgb, background.a);
}
