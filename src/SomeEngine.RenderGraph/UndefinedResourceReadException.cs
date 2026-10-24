namespace SomeEngine.RenderGraph;

/// <summary>A pass reads resource contents that its producers or frame bindings have left undefined.</summary>
public sealed class UndefinedResourceReadException(string message) : InvalidOperationException(message);
