namespace SomeEngine.Views.Compose;

/// <summary>Declares a static method as a view with its own node and parameter component.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ComposableAttribute : Attribute;
