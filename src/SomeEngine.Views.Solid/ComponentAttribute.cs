namespace SomeEngine.Views.Solid;

/// <summary>Exports a Solid function as a view built once at Compose and view Build call sites.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ComponentAttribute : Attribute;
