using System.Buffers;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Packs;

namespace SomeEngine.Assets.Importers;

/// <summary>Runs development asset commands using the project's selected importers and source formats.</summary>
public static class AssetCommands
{
    /// <summary>Identifies the engine's asset commands so applications can route their other startup modes.</summary>
    public static bool Matches(IReadOnlyList<string> arguments) => GetCommand(arguments) != AssetCommand.None;

    private static AssetCommand GetCommand(IReadOnlyList<string> arguments) => arguments.Count == 0 ? AssetCommand.None : arguments[0] switch
    {
        "import" => AssetCommand.Import,
        "build" => AssetCommand.Build,
        "list" => AssetCommand.List,
        "pack" => AssetCommand.Pack,
        "update-pack" => AssetCommand.UpdatePack,
        "refresh-shader" => AssetCommand.RefreshShader,
        "inspect-shader" => AssetCommand.InspectShader,
        _ => AssetCommand.None,
    };

    private enum AssetCommand : byte { None, Import, Build, List, Pack, UpdatePack, RefreshShader, InspectShader }

    public static async ValueTask<int> RunAsync(AssetProject project, IReadOnlyList<string> arguments,
        TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        AssetCommand command = GetCommand(arguments);
        if (command is AssetCommand.Pack or AssetCommand.UpdatePack)
        {
            await PackAsync(project.RootPath, arguments, output, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        if (command == AssetCommand.RefreshShader && arguments.Count is 2 or 3)
        {
            if (!Guid.TryParse(arguments[1], out Guid id) || id == Guid.Empty
                || arguments.Count == 3 && arguments[2] != "--dry-run")
                throw new ArgumentException("Usage: refresh-shader <shader GUID> [--dry-run]");
            bool dryRun = arguments.Count == 3;
            using ShaderBuildMeasurements? refreshMeasurements = dryRun ? null : ShaderBuildMeasurements.Start(output);
            await RefreshShaderAsync(project, id, output, dryRun, cancellationToken: cancellationToken).ConfigureAwait(false);
            return 0;
        }
        if (command == AssetCommand.InspectShader)
        {
            await RunShaderInspectionAsync(project.RootPath, arguments, output, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        using ShaderBuildMeasurements? measurements = arguments.Count > 1
            && (command is AssetCommand.Import or AssetCommand.Build)
                ? ShaderBuildMeasurements.Start(output) : null;
        if (arguments.Count > 1 && command == AssetCommand.Import)
        {
            for (int index = 1; index < arguments.Count; index++)
            {
                IReadOnlyList<Guid> ids = await project.ImportAsync(arguments[index], cancellationToken).ConfigureAwait(false);
                foreach (Guid id in ids)
                    await output.WriteLineAsync(id.ToString()).ConfigureAwait(false);
            }
            return 0;
        }

        if (arguments.Count > 1 && command == AssetCommand.Build)
        {
            for (int index = 1; index < arguments.Count; index++)
            {
                string input = Path.GetFullPath(arguments[index], project.RootPath);
                Guid id;
                if (input.EndsWith(Schema.ShaderAsset.PathSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    using Schema.ShaderAsset shader = await Schema.ShaderAsset.OpenAsync(input, cancellationToken).ConfigureAwait(false);
                    var slang = SlangShaderImporter.GlobalSession;
                    var settings = SlangShaderImporter.CreateSessionDescription(input, SlangShaderImporter.CreateDefaultTargets(slang));
                    await shader.CompileAsync(slang, settings, cancellationToken).ConfigureAwait(false);
                    id = project.RegisterAsset(await shader.SaveAsync(input, cancellationToken).ConfigureAwait(false));
                }
                else id = await project.BuildAsync(input, cancellationToken: cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(id.ToString()).ConfigureAwait(false);
            }
            return 0;
        }

        if (arguments.Count is 1 or 2 && command == AssetCommand.List)
        {
            foreach (AssetEntry asset in project.List(arguments.Count == 2 ? arguments[1] : null))
                await output.WriteLineAsync($"{asset.AssetGuid}\t{asset.AssetType}\t{asset.Path}").ConfigureAwait(false);
            return 0;
        }

        await output.WriteLineAsync("Usage: import <source>... | build <definition-or-shader-asset>... | list [asset-type] | pack <output> [--base <pack>] [--asset <guid> ...] [--dry-run] | update-pack <output> --asset <guid> ... [--dry-run] | refresh-shader <guid> [--dry-run] | inspect-shader <guid> <entry> [--arg <expression>] [--pack <pack>] [--target Dxil|Spirv] [--include-root <directory>]").ConfigureAwait(false);
        return 2;
    }

    /// <summary>Packs existing assets without importing or deploying project content.</summary>
    public static async ValueTask PackAsync(string rootPath, IReadOnlyList<string> arguments,
        TextWriter outputWriter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(outputWriter);
        cancellationToken.ThrowIfCancellationRequested();
        const string usage = "Usage: (pack|update-pack) <pack in the existing project root> [--base <base pack>] [--asset <guid> ...] [--dry-run]";
        if (arguments.Count < 2 || arguments[0] is not ("pack" or "update-pack")) throw new ArgumentException(usage);
        bool update = arguments[0] == "update-pack";
        string root = Path.GetFullPath(rootPath);
        string output = Path.GetFullPath(arguments[1], root);
        string? basePath = null;
        bool dryRun = false;
        var selectedAssets = new HashSet<Guid>();
        for (int i = 2; i < arguments.Count; i++)
        {
            string option = arguments[i];
            if (option == "--dry-run") dryRun = true;
            else if (option == "--base")
            {
                if (++i == arguments.Count) throw new ArgumentException(usage);
                basePath = Path.GetFullPath(arguments[i], root);
            }
            else if (option == "--asset")
            {
                if (++i == arguments.Count || !Guid.TryParse(arguments[i], out Guid asset) || asset == Guid.Empty)
                    throw new ArgumentException(usage);
                if (!selectedAssets.Add(asset) && update)
                    throw new ArgumentException($"Shader {asset} was selected more than once.");
            }
            else throw new ArgumentException(usage);
        }
        if (update && (basePath is not null || selectedAssets.Count == 0))
            throw new ArgumentException("--update-pack requires explicit shader IDs and does not accept --base.");
        if (update && !File.Exists(output))
            throw new FileNotFoundException("The pack to update must already exist.", output);
        if (!Directory.Exists(root) || !Directory.Exists(Path.GetDirectoryName(output)))
            throw new DirectoryNotFoundException("Packing uses existing content/output directories only.");
        RequireUnderRoot(output, root);
        StringComparison paths = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(basePath, output, paths))
            throw new ArgumentException("A delta must not replace its base pack.");
        string catalog = Path.Combine(root, "Library", "AssetManifest");
        if (!File.Exists(Path.Combine(catalog, SomeEngine.Assets.AssetManifest.CatalogFileName)))
            throw new FileNotFoundException("The selected content root has no asset catalog.", catalog);
        var manifest = SomeEngine.Assets.AssetManifest.Load(catalog);
        foreach (Guid id in selectedAssets)
            if (!manifest.Assets.ContainsKey(id))
                throw new ArgumentException($"Selected asset {id} is absent from the source catalog.");
        var sourceInputs = new List<(AssetEntry Entry, string Path, long Length, byte[]? Bytes)>();
        foreach (var group in manifest.List().Where(entry => selectedAssets.Count == 0 || selectedAssets.Contains(entry.AssetGuid))
            .GroupBy(entry => entry.PackPath, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            if (group.Key.Length == 0)
            {
                foreach (AssetEntry entry in group)
                {
                    string path = Path.GetFullPath(entry.Path, root);
                    RequireUnderRoot(path, root);
                    if (string.Equals(path, output, paths))
                        throw new ArgumentException("A pack cannot replace a source asset.");
                    sourceInputs.Add((entry, path, new FileInfo(path).Length, null));
                }
                continue;
            }
            string packPath = Path.GetFullPath(group.Key, root);
            RequireUnderRoot(packPath, root);
            // Parse each packed directory once, and close its ranges before publication.
            await using var pack = await AssetPack.OpenAsync(packPath, cancellationToken: cancellationToken);
            foreach (AssetEntry entry in group)
            {
                if (!pack.TryGetEntry(entry.AssetGuid, out var stored) ||
                    stored!.AssetType != entry.AssetType || stored.SchemaFingerprint != entry.SchemaFingerprint)
                    throw new InvalidDataException($"Packed asset {entry.AssetGuid} differs from its catalog entry.");
                await using var source = await pack.OpenAssetSourceAsync(entry.AssetGuid, cancellationToken);
                if (await BinaryDocument.ReadVersionAsync(source, cancellationToken) != entry.Version)
                    throw new IOException($"Packed asset {entry.AssetGuid} has a different document version.");
                byte[]? bytes = null;
                if (!dryRun)
                {
                    bytes = new byte[checked((int)source.Length)];
                    await source.ReadExactlyAsync(0, bytes, cancellationToken);
                    if (!await pack.ContentEqualsAsync(entry.AssetGuid, entry.AssetType, bytes, entry.SchemaFingerprint, cancellationToken))
                        throw new InvalidDataException($"Packed asset {entry.AssetGuid} failed its content check.");
                }
                sourceInputs.Add((entry, packPath, source.Length, bytes));
            }
        }
        var inputs = sourceInputs.ToArray();
        await using var existing = File.Exists(output)
            ? await AssetPack.OpenAsync(output, cancellationToken: cancellationToken).ConfigureAwait(false) : null;
        if (existing is { HasSignature: true })
            throw new ArgumentException("An unsigned command cannot replace a signed pack.");
        AssetPack? previous = update ? existing : null;
        if (previous is not null)
        {
            if (previous.HasSignature)
                throw new ArgumentException("Selected shader updates cannot replace a signed pack.");
            foreach (var input in inputs)
                if (input.Entry.AssetType != ShaderAsset.FileType ||
                    !previous.TryGetEntry(input.Entry.AssetGuid, out var entry) ||
                    entry!.AssetType != input.Entry.AssetType || entry.SchemaFingerprint != input.Entry.SchemaFingerprint)
                    throw new ArgumentException($"Shader {input.Entry.AssetGuid} must already exist with the same type and schema in the pack.");
        }
        long inputBytes = inputs.Sum(static item => item.Length);
        long budget = checked(inputBytes + inputs.LongLength * 4096 + 4 * 1024 * 1024 +
            (update ? new FileInfo(output).Length : 0));
        const long reserve = 256L * 1024 * 1024;
        var drive = new DriveInfo(Path.GetPathRoot(output)!);
        outputWriter.WriteLine($"Pack plan: assets={inputs.Length}, sourceBytes={inputBytes}, temporaryDiskBudget={budget}, freeBytes={drive.AvailableFreeSpace}, output={output}, base={basePath ?? "(none)"}");
        outputWriter.WriteLine(update
            ? $"Updating {inputs.Length} shaders; preserving {previous!.Count - inputs.Length} other entries from the existing pack. No import occurs."
            : "No import or resource deployment occurs. The existing builder retains selected asset bytes in memory. Delta packs override/add IDs; they do not remove base IDs.");
        cancellationToken.ThrowIfCancellationRequested();
        if (dryRun) return;
        for (int index = 0; index < inputs.Length; index++)
        {
            var input = inputs[index];
            byte[] bytes = input.Bytes ?? await File.ReadAllBytesAsync(input.Path, cancellationToken).ConfigureAwait(false);
            if (bytes.LongLength != input.Length)
                throw new IOException("A source asset changed while preparing the pack.");
            await using var source = new MemoryRangeSource(bytes);
            if (await BinaryDocument.ReadVersionAsync(source, cancellationToken).ConfigureAwait(false) != input.Entry.Version)
                throw new IOException($"Asset {input.Entry.AssetGuid} differs from its catalog version.");
            if (input.Entry.AssetType == ShaderAsset.FileType)
            {
                await using var document = await BinaryDocument.OpenAsync(new MemoryRangeSource(bytes),
                    ShaderAsset.WireType, ownsSource: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                var header = new AssetHeader();
                document.ReadRootInto(header);
                if (header.Id != input.Entry.AssetGuid || header.AssetType != input.Entry.AssetType)
                    throw new InvalidDataException("Source shader identity or type differs from its catalog entry.");
            }
            inputs[index] = (input.Entry, input.Path, input.Length, bytes);
        }
        if (previous is not null)
        {
            bool unchanged = true;
            for (int index = 0; index < inputs.Length; index++)
            {
                var input = inputs[index];
                byte[] bytes = input.Bytes!;
                previous.TryGetEntry(input.Entry.AssetGuid, out var stored);
                if (unchanged && (stored!.DocumentVersion != input.Entry.Version ||
                    !await previous.ContentEqualsAsync(input.Entry.AssetGuid,
                        input.Entry.AssetType, bytes, input.Entry.SchemaFingerprint, cancellationToken)))
                    unchanged = false;
            }
            if (unchanged)
            {
                outputWriter.WriteLine($"Selected shaders are unchanged: {output}; generation={previous.Generation}. No pack was written.");
                return;
            }
        }
        if (drive.AvailableFreeSpace < checked(budget + reserve))
            throw new IOException("Insufficient free space for atomic pack publication and the 256 MiB reserve.");
        await using var basePack = basePath is null ? null :
            await AssetPack.OpenAsync(basePath, cancellationToken: cancellationToken);
        var builder = new SomeEngine.Serialization.Packs.AssetPackBuilder(basePack);
        var contents = new Dictionary<Guid, (string Type, ulong Schema, byte[] Bytes)>();
        if (previous is not null)
            foreach (var entry in previous.Entries)
            {
                if (selectedAssets.Contains(entry.AssetId)) continue;
                byte[] bytes = await ReadVerifiedPackBytesAsync(previous, entry, cancellationToken);
                builder.AddAsset(entry.AssetId, entry.AssetType, bytes, entry.SchemaFingerprint);
                contents.Add(entry.AssetId, (entry.AssetType, entry.SchemaFingerprint, bytes));
            }
        foreach (var input in inputs)
        {
            byte[] bytes = input.Bytes ?? await File.ReadAllBytesAsync(input.Path, cancellationToken);
            if (bytes.LongLength != input.Length)
                throw new IOException("A source asset changed while preparing the pack.");
            contents.Add(input.Entry.AssetGuid, (input.Entry.AssetType, input.Entry.SchemaFingerprint, bytes));
            // Do not retain unchanged shared assets while producing a small delta.
            if (basePack is not null && await basePack.ContentEqualsAsync(input.Entry.AssetGuid,
                    input.Entry.AssetType, bytes, input.Entry.SchemaFingerprint, cancellationToken))
                continue;
            builder.AddAsset(input.Entry.AssetGuid, input.Entry.AssetType, bytes, input.Entry.SchemaFingerprint);
        }
        if (drive.AvailableFreeSpace < checked(budget + reserve))
            throw new IOException("Free space changed before pack publication.");
        await builder.PublishAsync(output, cancellationToken: cancellationToken);
        await using var published = await AssetPack.OpenAsync(output, cancellationToken: cancellationToken);
        if (published.Count != builder.ChangedAssetIds.Count)
            throw new InvalidDataException("Published pack entry count differs from the selected assets.");
        foreach (Guid id in builder.ChangedAssetIds)
        {
            var expectedContent = contents[id];
            if (!await published.ContentEqualsAsync(id, expectedContent.Type, expectedContent.Bytes, expectedContent.Schema,
                cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException($"Published pack entry {id} differs from the requested content.");
        }
        outputWriter.WriteLine($"Published and reopened {published.Count} assets in place: {output}; generation={published.Generation}; bytes={new FileInfo(output).Length}; freeBytes={drive.AvailableFreeSpace}");
    }

    private static void RequireUnderRoot(string path, string root)
    {
        string relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("The selected path must stay in its existing content root.");
    }


    private static async ValueTask<byte[]> ReadVerifiedPackBytesAsync(AssetPack pack, AssetPackEntry entry,
        CancellationToken cancellationToken)
    {
        await using IRangeSource source = await pack.OpenAssetSourceAsync(entry.AssetId, cancellationToken).ConfigureAwait(false);
        byte[] bytes = new byte[checked((int)source.Length)];
        await source.ReadExactlyAsync(0, bytes, cancellationToken).ConfigureAwait(false);
        if (!await pack.ContentEqualsAsync(entry.AssetId, entry.AssetType, bytes, entry.SchemaFingerprint,
            cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException($"Packed asset {entry.AssetId} failed its content check.");
        return bytes;
    }

    /// <summary>Refreshes one registered shader, preserving its identity and existing content boundaries.</summary>
    /// <remarks>A custom builder can prepare project-specific shared definitions; dry runs never invoke it.</remarks>
    public static async ValueTask RefreshShaderAsync(AssetProject project, Guid shaderId, TextWriter output,
        bool dryRun = false, string? sourceRoot = null,
        Func<string, string, CancellationToken, ValueTask<Guid>>? buildShader = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        if (shaderId == Guid.Empty) throw new ArgumentException("Select a nonempty shader ID.", nameof(shaderId));
        string root = project.RootPath;
        sourceRoot = Path.GetFullPath(sourceRoot ?? root);
        string catalog = Path.Combine(sourceRoot, "Library", "AssetManifest");
        if (!Directory.Exists(root) || !File.Exists(Path.Combine(catalog, AssetManifest.CatalogFileName))
            || !File.Exists(Path.Combine(root, "Library", "AssetManifest", AssetManifest.CatalogFileName)))
            throw new DirectoryNotFoundException("Shader refresh requires existing source and destination catalogs.");
        AssetManifest manifest = AssetManifest.Load(catalog);
        if (!manifest.TryGetAsset(shaderId, out AssetEntry source)
            || source.AssetType != ShaderAsset.FileType || source.DefinitionPath.Length == 0)
            throw new ArgumentException("The selected shader must have an existing native definition.");
        string definition = Path.GetFullPath(source.DefinitionPath, sourceRoot);
        RequireUnderRoot(definition, sourceRoot);
        if (!definition.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) || !File.Exists(definition))
            throw new InvalidDataException("The shader definition must be an existing native shader file.");
        using (ShaderAsset declared = await ShaderAsset.OpenSourceAsync(definition, cancellationToken).ConfigureAwait(false))
            if (declared.Id != shaderId)
                throw new InvalidDataException("The shader definition ID differs from the selected catalog entry.");
        AssetEntry destination = project.Manifest.TryGetAsset(shaderId, out AssetEntry registered) ? registered : source;
        if (destination.AssetType != source.AssetType)
            throw new InvalidDataException("The destination has a different asset type for the selected identity.");
        string localOutput = Path.GetFullPath(destination.Path, root);
        RequireUnderRoot(localOutput, root);
        if (!Directory.Exists(Path.GetDirectoryName(localOutput)))
            throw new DirectoryNotFoundException("The selected shader output directory must already exist.");
        bool shared = !string.Equals(root, sourceRoot,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (shared && buildShader is null)
            throw new ArgumentException("A shared definition requires the project's explicit shader preparation callback.");
        output.WriteLine($"Single shader: {shaderId}; source={definition}; destination={localOutput}; dryRun={dryRun}.");
        if (dryRun) return;
        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        if (drive.AvailableFreeSpace < 256L * 1024 * 1024)
            throw new IOException("At least 256 MiB of free space is required for this single-shader update.");
        Guid built = buildShader is null
            ? await project.BuildAsync(definition, localOutput, cancellationToken).ConfigureAwait(false)
            : await buildShader(definition, localOutput, cancellationToken).ConfigureAwait(false);
        if (built != shaderId || !project.Manifest.TryGetAsset(shaderId, out AssetEntry saved)
            || saved.AssetType != source.AssetType || saved.SchemaFingerprint != source.SchemaFingerprint
            || !string.Equals(Path.GetFullPath(saved.Path, root), localOutput,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("The single-shader build changed the selected asset identity or type.");
        output.WriteLine($"Refreshed shader {built}: {saved.Path}; version={saved.Version}; free={drive.AvailableFreeSpace} bytes.");
    }

    private static async ValueTask RunShaderInspectionAsync(string root, IReadOnlyList<string> arguments,
        TextWriter output, CancellationToken cancellationToken)
    {
        const string usage = "Usage: inspect-shader <shader GUID> <entry> [--arg <expression>] [--pack <pack>] [--target Dxil|Spirv] [--include-root <directory>]";
        if (arguments.Count < 3 || !Guid.TryParse(arguments[1], out Guid shaderId) || shaderId == Guid.Empty)
            throw new ArgumentException(usage);
        var values = new List<string>();
        var includes = new List<string>();
        string? pack = null;
        SlangCompileTarget target = SlangCompileTarget.Dxil;
        for (int index = 3; index < arguments.Count; index++)
        {
            string option = arguments[index];
            if (++index == arguments.Count) throw new ArgumentException(usage);
            switch (option)
            {
                case "--arg": values.Add(arguments[index]); break;
                case "--include-root": includes.Add(Path.GetFullPath(arguments[index], root)); break;
                case "--pack": pack = Path.GetFullPath(arguments[index], root); break;
                case "--target": target = arguments[index] switch
                {
                    "Dxil" => SlangCompileTarget.Dxil,
                    "Spirv" => SlangCompileTarget.Spirv,
                    _ => throw new ArgumentException(usage),
                }; break;
                default: throw new ArgumentException(usage);
            }
        }
        await InspectShaderAsync(root, shaderId, arguments[2], includes, output, values, pack, target,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Inspects stored shader code and reflection without compiling source code.</summary>
    /// <remarks>The caller owns include and optional output-directory policy; no engine checkout layout is assumed.</remarks>
    public static async ValueTask InspectShaderAsync(string contentRoot, Guid shaderId, string entryName,
        IReadOnlyList<string> includeDirectories, TextWriter outputWriter, IReadOnlyList<string>? arguments = null,
        string? packPath = null, SlangCompileTarget targetKind = SlangCompileTarget.Dxil,
        string? outputPath = null, string? outputRoot = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(includeDirectories);
        ArgumentNullException.ThrowIfNull(outputWriter);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
        cancellationToken.ThrowIfCancellationRequested();
        if (shaderId == Guid.Empty) throw new ArgumentException("Select a nonempty shader ID.", nameof(shaderId));
        if (targetKind is not (SlangCompileTarget.Dxil or SlangCompileTarget.Spirv))
            throw new ArgumentOutOfRangeException(nameof(targetKind));
        string root = Path.GetFullPath(contentRoot);
        string? output = outputPath is null ? null : Path.GetFullPath(outputPath);
        IReadOnlyList<string> values = arguments ?? [];
        string catalog = Path.Combine(root, "Library", "AssetManifest");
        if (!File.Exists(Path.Combine(catalog, AssetManifest.CatalogFileName)))
            throw new FileNotFoundException("The selected source catalog does not exist.", catalog);
        var manifest = AssetManifest.Load(catalog);
        if (!manifest.TryGetAsset(shaderId, out var selected) || selected.AssetType != ShaderAsset.FileType ||
            selected.DefinitionPath.Length == 0)
            throw new ArgumentException("The selected ID must have a saved shader and an existing definition.");
        string definition = Path.GetFullPath(selected.DefinitionPath, root);
        string assetPath = Path.GetFullPath(selected.Path, root);
        RequireUnderRoot(definition, root);
        RequireUnderRoot(assetPath, root);
        if (!File.Exists(definition) || (packPath is null && !File.Exists(assetPath)))
            throw new FileNotFoundException("The selected saved shader or its definition is missing.");
        if (output is not null)
        {
            if (targetKind != SlangCompileTarget.Dxil)
                throw new ArgumentException("File output is supported only for the Dxil target.");
            if (outputRoot is null) throw new ArgumentException("File output requires an explicit existing output root.");
            string obj = Path.GetFullPath(outputRoot);
            RequireUnderRoot(output, obj);
            if (!Directory.Exists(obj) || !Directory.Exists(Path.GetDirectoryName(output)) ||
                !output.EndsWith(".dxil", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("DXIL output must be a file in the selected existing output directory.");
        }
        await using AssetPackStorage? storage = packPath is null ? null :
            new AssetPackStorage([await AssetPack.OpenAsync(packPath, cancellationToken: cancellationToken)]);
        await using AssetLoader? loader = storage is null ? null : new AssetLoader(storage);
        using ShaderAsset asset = loader is null ? await ShaderAsset.OpenAsync(assetPath, cancellationToken) :
            await ShaderAsset.OpenAsync(loader, new AssetId<ShaderAsset>(shaderId), cancellationToken);
        if (asset.Id != shaderId) throw new InvalidDataException("The saved shader has a different ID.");
        var global = SlangShaderImporter.GlobalSession;
        var description = SlangShaderImporter.CreateSessionDescription(definition,
            SlangShaderImporter.CreateDefaultTargets(global),
            includeDirectories);
        var code = asset.GetBlock<ShaderBlock>();
        // This API includes every selected input block and throws instead of recompiling missing/stale code.
        Shader shader = await asset.CreateSavedAsync(global, description,
            values.Count == 0 ? null : entryName, values.Select(SpecializationArg.FromExpression).ToArray(),
            cancellationToken).ConfigureAwait(false);
        int[] matches = Enumerable.Range(0, shader.SlangEntryCount).Where(index =>
            shader.GetSlangEntryPoint(index).GetFunctionReflection().Name == entryName).ToArray();
        if (matches.Length != 1 || values.Count == 0 && shader.GetSlangEntryPoint(matches[0]).GetSpecializationParamCount() != 0)
            throw new InvalidDataException("Select one fully saved entry, with its declared specialization expressions if required.");
        int target = shader.RequireSlangTarget(targetKind);
        byte[] bytes = shader.GetSlangEntryBytes(target, matches[0]).ToArray();
        if (bytes.Length == 0) throw new InvalidDataException($"The stored {targetKind} entry is empty.");
        if (output is not null) await File.WriteAllBytesAsync(output, bytes, cancellationToken);
        // Inspect the saved evaluator and raster declarations used by depth invalidation.
        // This reads restored reflection only and never substitutes source-tree declarations.
        var depthEvaluators = new[] { "VertexEvaluate", "DomainEvaluate", "VisibilityEvaluate" }.Select(typeName =>
        {
            var type = shader.GetSlangReflection(target).FindTypeByName(typeName);
            return new
            {
                Name = typeName, Present = type is not null,
                Declarations = type is null ? null : Enumerable.Range(0, (int)type.Value.AttributeCount)
                    .Select(index => type.Value.GetAttribute((uint)index))
                    .Where(attribute => attribute.Name == "MaterialDepthInputs")
                    .Select(attribute => new { attribute.ArgumentCount,
                        Properties = attribute.ArgumentCount == 1 ? attribute.GetArgumentValueString(0) : null }).ToArray()
            };
        }).ToArray();
        var depthEntries = Enumerable.Range(0, shader.SlangEntryCount).Select(index =>
        {
            var function = shader.GetSlangEntryPoint(index).GetFunctionReflection();
            return new
            {
                Entry = function.Name,
                Declarations = Enumerable.Range(0, (int)function.AttributeCount)
                    .Select(attributeIndex => function.GetAttribute((uint)attributeIndex))
                    .Where(attribute => attribute.Name == "RasterMaterialInputs")
                    .Select(attribute => new { attribute.ArgumentCount,
                        Inputs = attribute.ArgumentCount == 1 ? attribute.GetArgumentValueString(0) : null }).ToArray()
            };
        }).Where(entry => entry.Entry.StartsWith("ClusterRaster", StringComparison.Ordinal)).ToArray();
        outputWriter.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            Asset = shaderId, Entry = entryName, Arguments = values, Target = targetKind.ToString(),
            EntryIndex = matches[0], Bytes = bytes.Length, CatalogVersion = selected.Version,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
            Source = packPath ?? assetPath, CatalogPath = selected.Path, CatalogPackPath = selected.PackPath,
            Output = output, StoredCodeOnly = true, StoredImportTrace = code.ImportTrace,
            MaterialDepthInputContract = new { Evaluators = depthEvaluators, RasterEntries = depthEntries }
        }));
    }

    /// <summary>Reads exactly the selected catalog version from loose or packed storage.</summary>
    /// <remarks>Packed bytes are hash-verified before callers copy or register them elsewhere.</remarks>
    public static async ValueTask<byte[]> ReadAssetBytesAsync(string contentRoot, AssetEntry entry,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(contentRoot);
        string path = Path.GetFullPath(entry.PackPath.Length == 0 ? entry.Path : entry.PackPath, root);
        RequireUnderRoot(path, root);
        byte[] bytes;
        if (entry.PackPath.Length == 0)
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        else
        {
            await using AssetPack pack = await AssetPack.OpenAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!pack.TryGetEntry(entry.AssetGuid, out AssetPackEntry? stored)
                || stored!.AssetType != entry.AssetType || stored.SchemaFingerprint != entry.SchemaFingerprint)
                throw new InvalidDataException($"Packed asset {entry.AssetGuid} differs from its catalog entry.");
            if (stored.DocumentVersion != entry.Version)
                throw new IOException($"Packed asset {entry.AssetGuid} differs from its catalog version.");
            bytes = await ReadVerifiedPackBytesAsync(pack, stored, cancellationToken).ConfigureAwait(false);
        }
        await using var source = new MemoryRangeSource(bytes);
        if (await BinaryDocument.ReadVersionAsync(source, cancellationToken).ConfigureAwait(false) != entry.Version)
            throw new IOException($"Asset {entry.AssetGuid} differs from its catalog version.");
        if (entry.AssetType == ShaderAsset.FileType)
        {
            await using var document = await BinaryDocument.OpenAsync(new MemoryRangeSource(bytes),
                ShaderAsset.WireType, ownsSource: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            var header = new AssetHeader();
            document.ReadRootInto(header);
            if (header.Id != entry.AssetGuid || header.AssetType != entry.AssetType)
                throw new InvalidDataException("Source shader identity or type differs from its catalog entry.");
        }
        return bytes;
    }
}
