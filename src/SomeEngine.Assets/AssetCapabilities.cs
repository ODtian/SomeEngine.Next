using System.Text.Json;
using SomeEngine.Assets.Importing;
using Microsoft.Extensions.DependencyInjection;

namespace SomeEngine.Assets;

/// <summary>Generates registrations for the public asset extensions referenced by this project.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AssetCapabilitiesAttribute : Attribute;

/// <summary>A package's named group of importers for source defaults and pipeline selection.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AssetImporterSetAttribute(string id, params Type[] importers) : Attribute
{
    public string Id { get; } = id;
    public IReadOnlyList<Type> Importers { get; } = Array.AsReadOnly(importers);
    public bool Neutral { get; init; }
    public bool Default { get; init; }
}

public sealed record AssetImporterSetCapability(Guid Id, string Name, IReadOnlyList<Guid> Importers, bool Neutral, bool Default);

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
public abstract class AssetImporterCapability(
    Guid id,
    Type importerType,
    Type sourceType,
    Type settingsType,
    IReadOnlyList<AssetBlockCapability> producedBlocks,
    string implementationVersion = "")
{
    public Guid Id { get; } = id;
    public Type ImporterType { get; } = importerType;
    public Type SourceType { get; } = sourceType;
    public Type SettingsType { get; } = settingsType;
    public IReadOnlyList<AssetBlockCapability> ProducedBlocks { get; } = producedBlocks;
    public string ImplementationVersion { get; } = implementationVersion;
    public abstract object GetImporter(IServiceProvider services);
    public abstract JsonElement GetDefaultSettings();
    protected abstract object BindImporter(IServiceProvider services, JsonElement? settings);

    public IAssetImporter<TSource> Bind<TSource>(IServiceProvider services, JsonElement? settings = null)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(services);
        if (SourceType != typeof(TSource))
            throw new InvalidOperationException($"Importer {Id} consumes {SourceType}, not {typeof(TSource)}.");
        return (IAssetImporter<TSource>)BindImporter(services, settings);
    }
}

public sealed class AssetImporterCapability<TImporter, TSource, TSettings>(Guid id,
    IReadOnlyList<AssetBlockCapability> producedBlocks, IAssetImporterSettings<TSettings>? settings,
    string implementationVersion)
    : AssetImporterCapability(id, typeof(TImporter), typeof(TSource), typeof(TSettings), producedBlocks, implementationVersion)
    where TImporter : class, IAssetImporter<TSource, TSettings> where TSource : class where TSettings : notnull
{
    public override object GetImporter(IServiceProvider services) => services.GetRequiredService<TImporter>();
    public override JsonElement GetDefaultSettings() => settings is null ? default
        : JsonSerializer.SerializeToElement(settings.DefaultSettings, settings.TypeInfo);

    protected override object BindImporter(IServiceProvider services, JsonElement? value)
        => AssetImporter.Bind(Id, ProducedBlocks.Select(static block => block.Id).ToArray(), services.GetRequiredService<TImporter>(),
            settings ?? throw new InvalidOperationException($"Importer {Id} needs a settings codec."), value);
}

/// <summary>A generated editor choice. Persist Id; Value is local to the compiled project.</summary>
public sealed record AssetChoice<T>(T Value, string Label, Guid Id) where T : struct, Enum;
