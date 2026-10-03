namespace SomeEngine.Views.Compose;

/// <summary>Declares a static method as a view with its own node and parameter component.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter)]
public sealed class ComposableAttribute : Attribute;
