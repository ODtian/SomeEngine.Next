#ifndef VSM_PREFILTER_MATH_SLANG
#define VSM_PREFILTER_MATH_SLANG

float VirtualShadowPrefilterHistory(float current, float previous, float historyWeight)
{
    float delta = (current - previous) * (1.0 - historyWeight);
    if (abs(delta) < 1.0 / 255.0) delta = sign(delta) / 255.0;
    return saturate(previous + delta);
}

float VirtualShadowPrefilterOcclusion(float reverseDepth, float reverseTestDepth)
{
    return reverseDepth > reverseTestDepth ? 0.0 : 1.0;
}

float VirtualShadowPrefilterFiveSamples(float minus2, float minus1, float center, float plus1, float plus2)
{
    return (minus2 + 4.0 * minus1 + 6.0 * center + 4.0 * plus1 + plus2) / 16.0;
}

#endif
