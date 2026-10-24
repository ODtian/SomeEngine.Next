using System.Runtime.CompilerServices;

namespace SomeEngine.Layout;

public enum GridTrackKind : byte
{
    Fixed,
    Auto,
    Fraction,
}

/// <summary>A fixed length, content-sized track, or share of the remaining space.</summary>
public readonly record struct GridTrack(GridTrackKind Kind, float Value = 0)
{
    public static GridTrack Fixed(float size) => new(GridTrackKind.Fixed, size);
    public static GridTrack Auto => new(GridTrackKind.Auto);
    public static GridTrack Fraction(float weight = 1) => new(GridTrackKind.Fraction, weight);
}

/// <summary>An immutable, value-compared sequence of row or column definitions.</summary>
[CollectionBuilder(typeof(GridTracks), nameof(Create))]
public readonly struct GridTracks : IEquatable<GridTracks>
{
    private readonly GridTrack[]? _tracks;

    public GridTracks(params ReadOnlySpan<GridTrack> tracks)
    {
        GridValidation.CheckTracks(tracks);
        _tracks = tracks.ToArray();
    }

    public int Count => _tracks?.Length ?? 0;
    public GridTrack this[int index] => _tracks![index];
    public ReadOnlySpan<GridTrack>.Enumerator GetEnumerator() => ((ReadOnlySpan<GridTrack>)_tracks).GetEnumerator();
    public static GridTracks Create(ReadOnlySpan<GridTrack> tracks) => new(tracks);

    public bool Equals(GridTracks other) => _tracks.AsSpan().SequenceEqual(other._tracks);
    public override bool Equals(object? obj) => obj is GridTracks other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (GridTrack track in this)
            hash.Add(track);
        return hash.ToHashCode();
    }

    public static bool operator ==(GridTracks left, GridTracks right) => left.Equals(right);
    public static bool operator !=(GridTracks left, GridTracks right) => !left.Equals(right);
}
