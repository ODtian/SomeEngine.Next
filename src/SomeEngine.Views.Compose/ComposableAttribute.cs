namespace SomeEngine.Views.Compose;

/// <summary>Declares a static composition method or a delegate parameter that supplies view content.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter)]
public sealed class ComposableAttribute : Attribute;
