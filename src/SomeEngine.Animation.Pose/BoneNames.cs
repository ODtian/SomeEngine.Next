using System.Text;

namespace SomeEngine.Animation.Pose;

public static class BoneNames
{
    /// <summary>Hashes a case-sensitive UTF-8 name with 32-bit FNV-1a. Empty names have hash zero.</summary>
    public static uint Hash(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty) return 0;
        uint hash = 2166136261;
        Span<byte> bytes = stackalloc byte[4];
        foreach (Rune rune in name.EnumerateRunes())
        {
            int length = rune.EncodeToUtf8(bytes);
            foreach (byte value in bytes[..length]) hash = unchecked((hash ^ value) * 16777619);
        }
        return hash;
    }
}
