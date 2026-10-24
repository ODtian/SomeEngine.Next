using System.Text.Json;
using SomeEngine.Assets.Importing;

namespace SomeEngine.Assets;

/// <summary>Generates registrations for the public asset extensions referenced by this project.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AssetCapabilitiesAttribute(params string[] defaultImporterSets) : Attribute
{
    public IReadOnlyList<string> DefaultImporterSets { get; } = Array.AsReadOnly(defaultImporterSets);
}

/// <summary>Declares a package's group of importer choices.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class AssetImporterSetAttribute(string id, string name, params Type[] importers) : Attribute
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public IReadOnlyList<Type> Importers { get; } = Array.AsReadOnly(importers);
    public bool Neutral { get; init; }
}

/// <summary>A package's importer group and its default selection in the project.</summary>
public sealed record AssetImporterSetCapability(Guid Id, string Name, IReadOnlyList<Guid> Importers, bool Neutral)
{
    public bool Default { get; init; }
}

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
    string ImplementationVersion = "")
{
    private Func<IServiceProvider, JsonElement?, object>? BindImporter { get; init; }

    public AssetImporterCapability(Guid id, Type importerType, Type sourceType, Type settingsType,
        IReadOnlyList<AssetBlockCapability> producedBlocks, string implementationVersion,
        Func<IServiceProvider, JsonElement?, object> bindImporter)
        : this(id, importerType, sourceType, settingsType, producedBlocks, implementationVersion)
        => BindImporter = bindImporter;

    public IAssetImporter<TSource> Bind<TSource>(IServiceProvider services, JsonElement? settings = null)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(services);
        if (SourceType != typeof(TSource))
            throw new InvalidOperationException($"Importer {Id} consumes {SourceType}, not {typeof(TSource)}.");
        if (BindImporter is null)
            throw new InvalidOperationException($"Importer {Id} has no settings codec.");
        return (IAssetImporter<TSource>)BindImporter(services, settings);
    }
}

/// <summary>A generated editor choice. Persist Id; Value is local to the compiled project.</summary>
public sealed record AssetChoice<T>(T Value, string Label, Guid Id) where T : struct, Enum;
