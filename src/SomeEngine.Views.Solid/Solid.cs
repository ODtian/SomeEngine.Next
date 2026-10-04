using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS.Entities;
using SomeEngine.Elements;
using TextData = SomeEngine.Elements.Text;

namespace SomeEngine.Views.Solid;

/// <summary>Calculations and element declarations for components built once.</summary>
public static class Solid
{
    /// <summary>Runs a calculation now and in the rebuild phase when its reads change.</summary>
    public static void Effect(Action run) => Scope.Compute(run);

    /// <summary>Creates a getter that refreshes its Memo and tracks the Memo's result.</summary>
    public static Func<T> Memo<T>(Func<T> compute)
    {
        Memo<T> memo = Scope.Memo(compute);
        return () => memo.Value;
    }

    /// <summary>Reads a value while the surrounding calculation suspends dependency tracking.</summary>
    public static T Untrack<T>(Func<T> read)
    {
        DevelopmentCheck.Require(read is not null, "Untrack requires a value getter.");
        using (Scope.Untrack())
            return read();
    }

    /// <summary>Creates a text node with its initial value.</summary>
    public static Entity Text(string value) => Scope.View(new TextData(value), 0);

    /// <summary>Creates a formatted text node, reusing equal text during declarations.</summary>
    public static Entity Text(int value, string format = "", IFormatProvider? provider = null) =>
        TextFunctions.DeclareText(0, value, format, provider);

    /// <summary>Creates a formatted text node using the supplied format provider.</summary>
    public static Entity Text<T>(T value, string format = "", IFormatProvider? provider = null)
        where T : struct, ISpanFormattable => TextFunctions.DeclareText(0, value, format, provider);

    /// <summary>Creates a text node and binds its value to the getter's reads.</summary>
    public static Entity Text(Func<string> value)
    {
        DevelopmentCheck.Require(value is not null, "Text requires a value getter.");
        Entity node = Text("");
        Scope.Compute(() => Scope.Write<TextData>(node).Value = value());
        return node;
    }

    /// <summary>Creates a text node and binds its formatted integer value.</summary>
    public static Entity Text(Func<int> value, string format = "", IFormatProvider? provider = null) =>
        Text<int>(value, format, provider);

    /// <summary>Binds formatted data, retaining the existing string and version when its characters match.</summary>
    public static Entity Text<T>(Func<T> value, string format = "", IFormatProvider? provider = null)
        where T : struct, ISpanFormattable
    {
        DevelopmentCheck.Require(value is not null, "Text requires a value getter.");
        Entity node = Text("");
        Scope.Compute(() => node.SetText(value(), format, provider));
        return node;
    }
}
