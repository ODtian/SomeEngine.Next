// Optional MaterialBindless shader contract. The renderer supplies materialBase;
// generated material code supplies compact offsets owned by this extension.
StructuredBuffer<uint> MaterialBindlessValues;

uint getMaterialDescriptor(uint materialBase, uint valueOffset)
{
    return MaterialBindlessValues[materialBase + valueOffset];
}
