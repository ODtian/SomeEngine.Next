namespace SomeEngine.Views.Compose;

/// <summary>Declares a static method or a delegate parameter as a view with its own node and parameters.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter)]
public sealed class ComposableAttribute : Attribute;
