#ifndef HIZ_COMMON_SLANG
#define HIZ_COMMON_SLANG

uint CoveredEnd(uint destination, uint destinationSize, uint sourceSize)
{
    return destination == destinationSize - 1u ? sourceSize - 1u : destination * 2u + 1u;
}

#endif
