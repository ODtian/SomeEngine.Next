using System.Runtime.InteropServices;

namespace SomeEngine.Assets;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct SourceGuid(Guid Value)
{
    public static readonly SourceGuid Empty = new(Guid.Empty);
    public static SourceGuid New() => new(Guid.NewGuid());
    public bool IsEmpty => Value == Guid.Empty;
    public string ToFlatString() => Value.ToString("D");
    public override string ToString() => ToFlatString();
    public static SourceGuid Parse(string value) => new(Guid.Parse(value));

    public static bool TryParse(string? value, out SourceGuid guid)
    {
        bool success = Guid.TryParse(value, out Guid parsed);
        guid = success ? new SourceGuid(parsed) : Empty;
        return success;
    }
}

