namespace SomeEngine.Views.Compose;

/// <summary>Runs a composable method in the caller's view with a call-site key prefix.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class InlineAttribute : Attribute;
