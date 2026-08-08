using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS;

[assembly: TypeForwardedTo(typeof(World))]
[module: SkipLocalsInit]
[module: DefaultCharSet(CharSet.Unicode)]

namespace SomeEngine.Testing;

public sealed class PublicApiSurfaceTests
{
    private static readonly Lazy<string> Surface = new(
        () => PublicApiSurface.Build(typeof(PublicApiSurfaceTests).Assembly));

    [Fact]
    public void CapturesExternallyDerivableTypesMembersAndCompleteAccessors()
    {
        string surface = Surface.Value;

        Assert.Contains(
            "type protected class SomeEngine.Testing.PublicApiVisibilityFixture+ProtectedNested`1<",
            surface,
            StringComparison.Ordinal);
        Assert.Contains("ctor protected SomeEngine.Testing.PublicApiVisibilityFixture(", surface,
            StringComparison.Ordinal);
        Assert.Contains("method protected internal virtual", surface, StringComparison.Ordinal);
        Assert.Contains("VisibilityContract", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateProtectedMustNotAppear", surface, StringComparison.Ordinal);

        string property = LineContaining(surface, "property ", " VisibilityValue");
        Assert.Contains("public get(", property, StringComparison.Ordinal);
        Assert.Contains("protected init(", property, StringComparison.Ordinal);

        string @event = LineContaining(surface, "event ", " VisibilityChanged");
        Assert.Contains("protected add(", @event, StringComparison.Ordinal);
        Assert.Contains("protected remove(", @event, StringComparison.Ordinal);
    }

    [Fact]
    public void DistinguishesNestedAndMethodGenericParametersAndAllowsRefLike()
    {
        string surface = Surface.Value;

        Assert.Contains("allows ref struct", surface, StringComparison.Ordinal);
        Assert.Contains(
            "PublicApiOuter`1+Inner`1<!0:TOuter,!1:TInner>",
            surface,
            StringComparison.Ordinal);
        string method = LineContaining(surface, "method ", " GenericMix<");
        Assert.Contains("!0:TOuter", method, StringComparison.Ordinal);
        Assert.Contains("!1:TInner", method, StringComparison.Ordinal);
        Assert.Contains("!!0:TMethod", method, StringComparison.Ordinal);
    }

    [Fact]
    public void CapturesRefReadonlyRequiresLocationAndScopedRef()
    {
        string surface = Surface.Value;

        Assert.Contains("ref readonly System.Int32", LineContaining(surface, "method ", " RefReadonlyReturn("),
            StringComparison.Ordinal);
        Assert.Contains("ref readonly System.Int32", LineContaining(surface, "method ", " AcceptLocation("),
            StringComparison.Ordinal);
        Assert.Contains("in System.Int32", LineContaining(surface, "method ", " AcceptIn("),
            StringComparison.Ordinal);
        Assert.Contains("scoped ref System.Int32", LineContaining(surface, "method ", " AcceptScoped("),
            StringComparison.Ordinal);
    }

    [Fact]
    public void CapturesLayoutOrderOffsetsFixedBuffersAndInlineArrays()
    {
        string surface = Surface.Value;

        Assert.Contains("PublicApiSequentialLayoutFixture layout[Sequential,pack=2", surface,
            StringComparison.Ordinal);
        Assert.Contains("layout-field #0 offset=0 type=System.Byte member=<nonpublic>", surface,
            StringComparison.Ordinal);
        Assert.Contains("layout-field #1 offset=2 type=System.Int32 member=public:Value", surface,
            StringComparison.Ordinal);
        Assert.Contains("fixed[element=System.Byte,length=7]", surface, StringComparison.Ordinal);
        Assert.Contains("inline-array=4",
            LineContaining(surface, "type ", "PublicApiInlineArrayFixture"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizesNullabilityAndCapturesContractAttributeShapes()
    {
        string surface = Surface.Value;

        Assert.Contains("{nullable}", LineContaining(surface, "field ", " NullableField"),
            StringComparison.Ordinal);
        Assert.Contains("{notnull}", LineContaining(surface, "field ", " NonNullField"),
            StringComparison.Ordinal);
        string allowNull = LineContaining(surface, "property ", " AllowNullValue");
        Assert.Contains("read=System.String{notnull}", allowNull, StringComparison.Ordinal);
        Assert.Contains("write=System.String{nullable}", allowNull, StringComparison.Ordinal);
        Assert.DoesNotContain("NullableContextAttribute", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("NullableAttribute", surface, StringComparison.Ordinal);

        Assert.Contains("System.AttributeUsageAttribute(ctor[System.AttributeTargets]", surface,
            StringComparison.Ordinal);
        string obsolete = LineContaining(surface, "method ", " OldContract(");
        Assert.Contains("System.ObsoleteAttribute(ctor[System.String,System.Boolean]", obsolete,
            StringComparison.Ordinal);
        Assert.Contains("line\\n\\\"quoted\\\"\\\\", obsolete, StringComparison.Ordinal);
        Assert.Contains("System.Runtime.InteropServices.OptionalAttribute", surface,
            StringComparison.Ordinal);
        Assert.Contains(" optional", LineContaining(surface, "method ", " OptionalContract("),
            StringComparison.Ordinal);
        Assert.Contains("= '\\n'", LineContaining(surface, "field ", " NewLine"),
            StringComparison.Ordinal);
        Assert.Contains("= \"line\\n\\\"quoted\\\"\\\\\"", LineContaining(surface, "field ", " Escaped"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void CapturesForwardersModuleAttributesAndIsDeterministic()
    {
        string first = Surface.Value;
        string second = PublicApiSurface.Build(typeof(PublicApiSurfaceTests).Assembly);

        Assert.Contains("forwarded-type SomeEngine.ECS.World", first, StringComparison.Ordinal);
        Assert.Contains("module-attrs[", first, StringComparison.Ordinal);
        Assert.Contains("System.Runtime.InteropServices.DefaultCharSetAttribute", first,
            StringComparison.Ordinal);
        Assert.Equal(first, second);
        Assert.Equal(PublicApiSurface.Sha256(first), PublicApiSurface.Sha256(second));
    }

    private static string LineContaining(string surface, string first, string second) =>
        surface.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains(first, StringComparison.Ordinal) &&
                            line.Contains(second, StringComparison.Ordinal));
}

public class PublicApiVisibilityFixture
{
    protected PublicApiVisibilityFixture(int value) => _ = value;

    public string? VisibilityValue { get; protected init; }

    protected event EventHandler? VisibilityChanged
    {
        add { }
        remove { }
    }

    protected internal virtual string? VisibilityContract(string? value) => value;

    private protected void PrivateProtectedMustNotAppear()
    {
    }

    protected class ProtectedNested<T>
    {
        protected T? Value { get; init; }
    }
}

public static class PublicApiGenericFixture
{
    public static void AllowsRefLike<T>() where T : allows ref struct
    {
    }
}

public class PublicApiOuter<TOuter>
{
    public class Inner<TInner>
    {
        public void GenericMix<TMethod>(TOuter outer, TInner inner, TMethod method)
        {
            _ = outer;
            _ = inner;
            _ = method;
        }
    }
}

public static class PublicApiRefFixture
{
    public static ref readonly int RefReadonlyReturn(ref int value) => ref value;

    public static void AcceptLocation(ref readonly int value) => _ = value;

    public static void AcceptIn(in int value) => _ = value;

    public static void AcceptScoped(scoped ref int value) => _ = value;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct PublicApiSequentialLayoutFixture
{
#pragma warning disable CS0169
    private byte _prefix;
#pragma warning restore CS0169
    public int Value;
}

[StructLayout(LayoutKind.Explicit, Size = 8)]
public struct PublicApiExplicitLayoutFixture
{
#pragma warning disable CS0169
    [FieldOffset(0)] private int _first;
#pragma warning restore CS0169
    [FieldOffset(4)] public int Second;
}

public unsafe struct PublicApiFixedBufferFixture
{
    public fixed byte Bytes[7];
}

[InlineArray(4)]
public struct PublicApiInlineArrayFixture
{
    private int _element0;
}

public sealed class PublicApiNullabilityFixture
{
    public string NonNullField = string.Empty;
    public string? NullableField;

    [AllowNull]
    public string AllowNullValue { get; set; } = string.Empty;

    public event EventHandler? NullableChanged
    {
        add { }
        remove { }
    }

    [return: MaybeNull]
    public string Transform([DisallowNull] string? value) => value;
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class PublicApiAttributeFixtureAttribute : Attribute;

public static class PublicApiAttributeFixture
{
    public const char NewLine = '\n';
    public const string Escaped = "line\n\"quoted\"\\";

    [Obsolete("line\n\"quoted\"\\", true, DiagnosticId = "API001", UrlFormat = "https://example/{0}")]
    public static void OldContract()
    {
    }

    [Conditional("PUBLIC_API_FEATURE")]
    public static void ConditionalContract()
    {
    }

    [DefaultValue('\n')]
    public static char Separator { get; set; }

    public static void OptionalContract(
        [Optional, MarshalAs(UnmanagedType.LPWStr)] string text) => _ = text;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
public delegate int PublicApiInteropDelegate(IntPtr value);
