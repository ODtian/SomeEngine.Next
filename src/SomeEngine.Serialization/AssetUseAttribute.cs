namespace SomeEngine.Serialization;

/// <summary>Marks a use type or interface whose object graph must be independent of asset acquisition and editing.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = true)]
public sealed class AssetUseAttribute : Attribute;
