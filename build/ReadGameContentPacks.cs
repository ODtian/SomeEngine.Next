using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

public sealed class ReadGameContentPacks : Task
{
    [Required] public string ProjectRoot { get; set; } = string.Empty;
    [Required] public string IntermediateDirectory { get; set; } = string.Empty;
    [Output] public ITaskItem[] ContentFiles { get; set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        string root = Path.GetFullPath(ProjectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        StringComparison comparison = Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var files = new Dictionary<string, ITaskItem>(Path.DirectorySeparatorChar == '\\'
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        string AddPack(string path)
        {
            string source = Path.GetFullPath(Path.Combine(root, path));
            if (files.TryGetValue(source, out ITaskItem existing)) return existing.GetMetadata("Link");
            string target = source.StartsWith(root, comparison) ? source.Substring(root.Length).Replace('\\', '/')
                : "Library/AssetPacks/" + files.Count + "/" + Path.GetFileName(source);
            files.Add(source, ContentFile(source, target));
            return target;
        }

        string pathsFile = Path.Combine(root, "Library/AssetPacks/packs.json");
        if (File.Exists(pathsFile))
        {
            string[] paths;
            var serializer = new DataContractJsonSerializer(typeof(string[]));
            using (Stream input = File.OpenRead(pathsFile)) paths = (string[])serializer.ReadObject(input);
            for (int index = 0; index < paths.Length; index++) paths[index] = AddPack(paths[index]);
            string generated = Path.GetFullPath(Path.Combine(IntermediateDirectory, "packs.json"));
            string text;
            using (var output = new MemoryStream())
            {
                serializer.WriteObject(output, paths);
                text = Encoding.UTF8.GetString(output.ToArray());
            }
            Directory.CreateDirectory(Path.GetDirectoryName(generated));
            if (!File.Exists(generated) || File.ReadAllText(generated) != text) File.WriteAllText(generated, text);
            files.Add(generated, ContentFile(generated, "Library/AssetPacks/packs.json"));
        }

        string catalog = Path.Combine(root, "Library/AssetManifest/catalog.json");
        if (File.Exists(catalog))
        {
            using (Stream input = File.OpenRead(catalog))
                foreach (AssetEntry asset in ((AssetCatalog)new DataContractJsonSerializer(typeof(AssetCatalog)).ReadObject(input)).Assets.Assets)
                    if (!string.IsNullOrEmpty(asset.PackPath)) AddPack(asset.PackPath);
        }
        ContentFiles = new List<ITaskItem>(files.Values).ToArray();
        return true;
    }

    private static ITaskItem ContentFile(string source, string target)
    {
        var item = new TaskItem(source);
        item.SetMetadata("Link", target);
        item.SetMetadata("CopyToOutputDirectory", "PreserveNewest");
        item.SetMetadata("CopyToPublishDirectory", "PreserveNewest");
        return item;
    }

    [DataContract]
    private sealed class AssetCatalog
    {
        [DataMember(Name = "assets")] public AssetRows Assets { get; set; }
    }

    [DataContract]
    private sealed class AssetRows
    {
        [DataMember(Name = "assets")] public AssetEntry[] Assets { get; set; }
    }

    [DataContract]
    private sealed class AssetEntry
    {
        [DataMember(Name = "pack_path")] public string PackPath { get; set; }
    }
}
