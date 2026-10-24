using SomeEngine.Assets.Importing;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets;

public sealed partial class AssetProject
{
    private async ValueTask<bool> CanReuseSourceAsync(IAssetSourceFormat format, string definitionPath,
        string outputPath, AssetEntry description, byte[]? recordBytes, CancellationToken token)
    {
        if (recordBytes is null) return false;
        SourceBuildRecord record = SourceBuildRecord.Read(recordBytes);
        if (record.Outputs.Count != 1) return false;
        SourceOutput output = record.Outputs[0];
        if (output.AssetGuid != description.AssetGuid || output.Version == Guid.Empty || output.ContentHash.Length == 0
            || !PathsEqual(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(definitionPath)!, output.AssetPath)), outputPath))
            return false;
        lock (_gate)
            if (!Manifest.TryGetAsset(output.AssetGuid, out AssetEntry entry) || entry.Version != output.Version
                || entry.AssetType != description.AssetType || !PathsEqual(FullPath(entry.Path), outputPath)
                || !PathsEqual(FullPath(entry.DefinitionPath), definitionPath)) return false;

        ImportInputs? inputs = await format.GetInputsAsync(_projectRoot, definitionPath, record.Inputs, token).ConfigureAwait(false);
        if (inputs is null || inputs.InputHash != record.InputHash || inputs.ImporterVersion != record.ImporterVersion)
            return false;
        try
        {
            if (await HashFileAsync(outputPath, token).ConfigureAwait(false) != output.ContentHash) return false;
            await using var source = FileRangeSource.Open(outputPath);
            return await BinaryDocument.ReadVersionAsync(source, token).ConfigureAwait(false) == output.Version;
        }
        catch (IOException) { return false; }
        catch (InvalidDataException) { return false; }
    }
}
