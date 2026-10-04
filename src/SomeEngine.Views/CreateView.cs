namespace SomeEngine.Views;

/// <summary>Creates view parameters from caller data and the matching node's previous parameters.</summary>
public delegate T CreateView<T, TValue>(in TValue value, in T previous)
    where T : struct, IView, IEquatable<T>;
