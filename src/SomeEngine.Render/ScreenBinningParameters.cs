using System.Runtime.InteropServices;

namespace SomeEngine.Render;

[StructLayout(LayoutKind.Sequential, Size = SizeInBytes)]
internal readonly record struct ScreenBinningParameters(uint ItemCount, uint ItemCapacity,
    uint CellCount, uint ClearThreadCount, uint MaxItemsPerCell)
{
    internal const int SizeInBytes = 32;
}
