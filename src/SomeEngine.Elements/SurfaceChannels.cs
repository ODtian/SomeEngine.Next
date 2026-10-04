using SomeEngine.Animation;
using SomeEngine.Core.Math;

namespace SomeEngine.Elements;

public static class SurfaceChannels
{
    public static readonly Channel<Color> Fill = Channel<Color>.Of<Surface>(static (ref Surface value) => ref value.Fill);
}
