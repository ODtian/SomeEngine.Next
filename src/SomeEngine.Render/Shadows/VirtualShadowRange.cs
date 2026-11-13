using System.Runtime.InteropServices;

namespace SomeEngine.Render.Shadows;

/// <summary>One page-table range's effective entries and view in the current frame.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = SizeInBytes)]
public readonly record struct VirtualShadowRange(uint Offset, uint EntryCount, uint Generation, uint ViewIndex)
{
    public const int SizeInBytes = 16;
}
