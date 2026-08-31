// Optional MaterialBindless shader contract. The renderer supplies materialBase;
// generated material code supplies its material-local slot constants.
StructuredBuffer<uint> MaterialBindlessValues;

uint getMaterialDescriptor(uint materialBase, uint materialSlot)
{
    return MaterialBindlessValues[materialBase + materialSlot];
}
