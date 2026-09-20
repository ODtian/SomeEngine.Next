namespace SomeEngine.Assets;

/// <summary>Generates registrations for the public asset extensions referenced by this project.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AssetCapabilitiesAttribute : Attribute;

/// <summary>Declares the persistent identity and data produced by an importer.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AssetImporterAttribute(string id, params Type[] producedBlocks) : Attribute
{
    public string Id { get; } = id;
    public IReadOnlyList<Type> ProducedBlocks { get; } = Array.AsReadOnly(producedBlocks);
    /// <summary>A public parameterless IAssetImporterSettings&lt;TSettings&gt; implementation for source configuration.</summary>
    public Type? SettingsProvider { get; init; }
    /// <summary>Additional directly used implementation assemblies whose build versions affect this conversion.</summary>
    public Type[] VersionInputs { get; init; } = [];
}

/// <summary>Editor information about a strongly typed block available to a project.</summary>
public sealed record AssetBlockCapability(Guid Id, Type BlockType);

/// <summary>Editor information about an available importer; it does not enable an import.</summary>
public sealed record AssetImporterCapability(
    Guid Id,
    Type ImporterType,
    Type SourceType,
    Type SettingsType,
    IReadOnlyList<AssetBlockCapability> ProducedBlocks,
    string ImplementationVersion = "");

/// <summary>A generated editor choice. Persist Id; Value is local to the compiled project.</summary>
public sealed record AssetChoice<T>(T Value, string Label, Guid Id) where T : struct, Enum;
