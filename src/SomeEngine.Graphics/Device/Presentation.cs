namespace SomeEngine.Graphics;

public enum SwapchainPresentMode : byte
{
    Immediate,
    Mailbox,
    Fifo,
}

public enum SwapchainColorSpace : byte
{
    Srgb,
    Hdr10,
}

public readonly record struct SwapchainDesc(
    nint WindowHandle,
    int Width,
    int Height,
    Format Format,
    int BufferCount = 2,
    SwapchainPresentMode PresentMode = SwapchainPresentMode.Fifo,
    SwapchainColorSpace ColorSpace = SwapchainColorSpace.Srgb,
    bool AllowTearing = false,
    string? Name = null)
{
    public void Validate(bool requireWindowHandle)
    {
        if (requireWindowHandle && WindowHandle == 0) throw new ArgumentException("A native window handle is required.", nameof(WindowHandle));
        if (Width <= 0 || Height <= 0) throw new ArgumentOutOfRangeException(nameof(Width));
        if (BufferCount < 2) throw new ArgumentOutOfRangeException(nameof(BufferCount));
        if (!Enum.IsDefined(Format) || Format == Format.Unknown) throw new ArgumentOutOfRangeException(nameof(Format));
        if (!Enum.IsDefined(PresentMode)) throw new ArgumentOutOfRangeException(nameof(PresentMode));
        if (!Enum.IsDefined(ColorSpace)) throw new ArgumentOutOfRangeException(nameof(ColorSpace));
        if (PresentMode == SwapchainPresentMode.Fifo && AllowTearing)
            throw new ArgumentException("FIFO presentation cannot enable tearing.", nameof(AllowTearing));
    }
}

public readonly record struct SwapchainImage(TextureHandle Texture, uint ImageIndex);

public readonly record struct PresentOptions(bool VSync = true, bool AllowTearing = false);

public enum PresentStatus : byte
{
    Success,
    Occluded,
    DeviceLost,
}

public readonly record struct PresentResult(PresentStatus Status, DeviceError Error = default)
{
    public bool Succeeded => Status == PresentStatus.Success;
}
