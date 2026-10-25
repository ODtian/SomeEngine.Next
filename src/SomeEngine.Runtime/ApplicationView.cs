namespace SomeEngine.Runtime;

/// <summary>A diagnostic output size and material specialization; labels and UI layout belong to the application.</summary>
public sealed record ApplicationViewSettings
{
    public ApplicationViewSettings(uint mode, int width = 320, int height = 180)
    {
        if (mode is < 8 or > 11) throw new ArgumentOutOfRangeException(nameof(mode));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Mode = mode;
        Width = width;
        Height = height;
    }

    public uint Mode { get; }
    public int Width { get; }
    public int Height { get; }
}

/// <summary>A scene-local, nonowning image handle. Register or change it before drawing images in that frame.</summary>
public sealed class ApplicationView
{
    private ApplicationViewSettings _settings;

    internal ApplicationView(nint imageId, ApplicationViewSettings settings)
        => (ImageId, _settings) = (imageId, settings);

    public nint ImageId { get; }
    public ApplicationViewSettings Settings
    {
        get => _settings;
        set => _settings = value ?? throw new ArgumentNullException(nameof(value));
    }
}
