namespace SomeEngine.Views.SwiftUI;

/// <summary>Stores a partial property's State on the view entity across builds.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class StateAttribute : Attribute;
