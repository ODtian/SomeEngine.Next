using System.Buffers;
using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Packs;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class PackedMaterialShaderTests
{
    [Fact]
    public async Task PackedMaterialEntryRestorationMatchesLooseEntries()
    {
        string content = Path.Combine(FrameStageScene.FindRoot(), "samples", "DefaultGame");
        AssetManifest manifest = AssetManifest.Load(Path.Combine(content, "Library", "AssetManifest"));
        await using var loose = new AssetLoader(new LooseAssetStorage(content, manifest));
        var builder = new AssetPackBuilder();
        foreach (string name in new[] { "Standard", "Unlit" })
        {
            AssetEntry materialEntry = manifest.List(MaterialAsset.FileType).Single(item => item.Name == name);
            await using MaterialAsset material = await MaterialAsset.OpenAsync(loose, new(materialEntry.AssetGuid));
            AssetEntry shaderEntry = manifest.Assets[material.GetBlock<MaterialBlock>().Shader.Value];
            foreach (AssetEntry entry in new[] { materialEntry, shaderEntry })
                builder.AddAsset(entry.AssetGuid, entry.AssetType,
                    await AssetCommands.ReadAssetBytesAsync(content, entry), entry.SchemaFingerprint);
        }
        Assert.Equal(4, builder.Count);
        using var packBytes = new MemoryStream();
        await builder.WriteAsync(packBytes);
        // This loader contains only the in-memory pack; it has no loose-file fallback.
        await using var storage = new AssetPackStorage(
            [await AssetPack.OpenAsync(new MemoryRangeSource(packBytes.ToArray()), ownsSource: true)]);
        await using var packed = new AssetLoader(storage);
        var shaderIds = new HashSet<AssetId<ShaderAsset>>();
        var loaded = new List<(string Name, Shader Loose, Shader Packed,
            Shader[] LooseEntries, Shader[] PackedEntries)>();

        foreach (string name in new[] { "Standard", "Unlit" })
        {
            var materialId = new AssetId<MaterialAsset>(
                manifest.List(MaterialAsset.FileType).Single(item => item.Name == name).AssetGuid);
            await using MaterialAsset looseMaterial = await MaterialAsset.OpenAsync(loose, materialId);
            await using MaterialAsset packedMaterial = await MaterialAsset.OpenAsync(packed, materialId);
            MaterialBlock expectedMaterial = looseMaterial.GetBlock<MaterialBlock>();
            MaterialBlock actualMaterial = packedMaterial.GetBlock<MaterialBlock>();
            Assert.True(expectedMaterial.Parent.IsEmpty);
            Assert.True(actualMaterial.Parent.IsEmpty);
            Assert.False(expectedMaterial.Shader.IsEmpty);
            Assert.Equal(expectedMaterial.Shader, actualMaterial.Shader);
            Assert.True(shaderIds.Add(expectedMaterial.Shader));
            await using ShaderAsset looseFile = await ShaderAsset.OpenAsync(loose, expectedMaterial.Shader);
            await using ShaderAsset packedFile = await ShaderAsset.OpenAsync(packed, actualMaterial.Shader);
            SessionDesc description = SlangShaderImporter.CreateSessionDescription(
                Path.Combine(content, manifest.Assets[expectedMaterial.Shader.Value].Path),
                SlangShaderImporter.CreateDefaultTargets(TestShaderLoading.Slang));
            Shader looseDefault = await looseFile.CreateSavedAsync(TestShaderLoading.Slang, description);
            Shader packedDefault = await packedFile.CreateSavedAsync(TestShaderLoading.Slang, description);
            var looseEntries = new Shader[2];
            var packedEntries = new Shader[2];
            int entryIndex = 0;

            foreach ((string entryName, string[] expressions) in new[]
            {
                ("ClusterShadowRequests", new[] { "0", "0" }),
                ("ClusterShadeMode", new[] { "0" }),
            })
            {
                SpecializationArg[] arguments = expressions.Select(SpecializationArg.FromExpression).ToArray();
                Shader expected = await looseFile.CreateSavedAsync(
                    TestShaderLoading.Slang, description, entryName, arguments);
                Shader actual = await packedFile.CreateSavedAsync(
                    TestShaderLoading.Slang, description, entryName, arguments);
                looseEntries[entryIndex] = expected;
                packedEntries[entryIndex++] = actual;
                AssertShaderEntry(expected, actual, entryName, name);
            }
            loaded.Add((name, looseDefault, packedDefault, looseEntries, packedEntries));
        }

        // Match serial material loading followed by lazy stage selection after asset disposal.
        var selected = new List<(Shader Expected, Shader Actual, string EntryName, string Name)>();
        foreach (var material in loaded)
            foreach ((int index, string entryName, string[] arguments) in new (int, string, string[])[]
            {
                (1, "ClusterShadeMode", ["0"]),
                (0, "ClusterShadowRequests", ["0", "0"]),
            })
            {
                Assert.True(material.Loose.TryGetSpecialization(entryName, arguments, out Shader looseLazy));
                Assert.True(material.Packed.TryGetSpecialization(entryName, arguments, out Shader packedLazy));
                selected.Add((material.LooseEntries[index], looseLazy, entryName,
                    material.Name));
                selected.Add((material.PackedEntries[index], packedLazy, entryName,
                    material.Name));
            }
        foreach (var entry in selected)
            AssertShaderEntry(entry.Expected, entry.Actual, entry.EntryName, entry.Name);
    }

    private static void AssertShaderEntry(Shader expected, Shader actual, string entryName,
        string name)
    {
        EntryPointReflection expectedEntry = expected.RequireSlangEntry(0, entryName, SlangStage.Compute);
        EntryPointReflection actualEntry = actual.RequireSlangEntry(0, entryName, SlangStage.Compute);
        Assert.Equal(1, expected.SlangEntryCount);
        Assert.Equal(1, actual.SlangEntryCount);
        Assert.False(expected.GetSlangEntryBytes(0, expectedEntry).IsEmpty);
        Assert.Equal(expected.GetSlangEntryBytes(0, expectedEntry).ToArray(),
            actual.GetSlangEntryBytes(0, actualEntry).ToArray());
        Assert.True(expected.GetSlangUsedParameters(0, 0).SetEquals(actual.GetSlangUsedParameters(0, 0)));
        ShaderReflection expectedReflection = expected.GetSlangReflection(0);
        ShaderReflection actualReflection = actual.GetSlangReflection(0);
        AssertVariables(expectedReflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null,
            actualReflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
        AssertVariables(expectedEntry.VarLayout, actualEntry.VarLayout);
        // Compare the live direct-request resources as well as their complete reflected layouts.
        if (entryName == "ClusterShadowRequests" && name == "Standard")
        {
            ShaderParameterResource[] resources = actual.GetSlangResourceLayout(
                SlangCompileTarget.Dxil, entryName).Resources.ToArray();
            Assert.Contains(resources, resource => resource.IsUsed && resource.Name == "RequestedPageFlags" &&
                resource.Type == ResourceBindingType.BufferUav);
        }
        TypeLayoutReflection expectedLayout = MaterialLayout(expectedReflection, name + "Scalars");
        TypeLayoutReflection actualLayout = MaterialLayout(actualReflection, name + "Scalars");
        AssertLayouts(expectedLayout, actualLayout);
    }

    private static TypeLayoutReflection MaterialLayout(ShaderReflection reflection, string typeName)
    {
        TypeReflection type = reflection.FindTypeByName(typeName)
            ?? throw new InvalidDataException(typeName);
        return reflection.GetTypeLayout(type, LayoutRules.DefaultStructuredBuffer)
            ?? throw new InvalidDataException(typeName);
    }

    private static void AssertVariables(VariableLayoutReflection expected, VariableLayoutReflection actual)
    {
        Assert.Equal(expected == VariableLayoutReflection.Null, actual == VariableLayoutReflection.Null);
        if (expected == VariableLayoutReflection.Null) return;
        TypeLayoutReflection expectedLayout = DataLayout(expected);
        TypeLayoutReflection actualLayout = DataLayout(actual);
        AssertLayouts(expectedLayout, actualLayout);
        for (nint range = 0; range < expectedLayout.BindingRangeCount; range++)
        {
            nint count = expectedLayout.GetBindingRangeDescriptorRangeCount(range);
            if (count == 0) continue;
            nint set = expectedLayout.GetBindingRangeDescriptorSetIndex(range);
            nint first = expectedLayout.GetBindingRangeFirstDescriptorRangeIndex(range);
            for (nint index = first; index < first + count; index++)
            {
                SlangParameterCategory category = expectedLayout.GetDescriptorSetDescriptorRangeCategory(set, index);
                Assert.Equal(expected.GetOffset(category), actual.GetOffset(category));
                Assert.Equal(expected.GetBindingSpace(category), actual.GetBindingSpace(category));
            }
        }
    }

    private static TypeLayoutReflection DataLayout(VariableLayoutReflection variable)
    {
        TypeLayoutReflection result = variable.TypeLayout.UnwrapArray();
        return result.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock
            ? result.ElementTypeLayout.UnwrapArray() : result;
    }

    private static void AssertLayouts(TypeLayoutReflection expected, TypeLayoutReflection actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.GetSize(SlangParameterCategory.Uniform), actual.GetSize(SlangParameterCategory.Uniform));
        Assert.Equal(expected.FieldCount, actual.FieldCount);
        for (uint field = 0; field < expected.FieldCount; field++)
        {
            VariableLayoutReflection expectedField = expected.GetFieldByIndex(field);
            VariableLayoutReflection actualField = actual.GetFieldByIndex(field);
            Assert.Equal(expectedField.Name, actualField.Name);
            Assert.Equal(expectedField.GetOffset(SlangParameterCategory.Uniform),
                actualField.GetOffset(SlangParameterCategory.Uniform));
            Assert.Equal(expectedField.TypeLayout.Kind, actualField.TypeLayout.Kind);
            Assert.Equal(expectedField.TypeLayout.GetSize(SlangParameterCategory.Uniform),
                actualField.TypeLayout.GetSize(SlangParameterCategory.Uniform));
        }
        Assert.Equal(expected.BindingRangeCount, actual.BindingRangeCount);
        for (nint range = 0; range < expected.BindingRangeCount; range++)
        {
            Assert.Equal(expected.GetBindingRangeType(range), actual.GetBindingRangeType(range));
            Assert.Equal(expected.GetBindingRangeBindingCount(range), actual.GetBindingRangeBindingCount(range));
            nint count = expected.GetBindingRangeDescriptorRangeCount(range);
            Assert.Equal(count, actual.GetBindingRangeDescriptorRangeCount(range));
            if (count == 0) continue;
            nint set = expected.GetBindingRangeDescriptorSetIndex(range);
            nint first = expected.GetBindingRangeFirstDescriptorRangeIndex(range);
            Assert.Equal(set, actual.GetBindingRangeDescriptorSetIndex(range));
            Assert.Equal(first, actual.GetBindingRangeFirstDescriptorRangeIndex(range));
            Assert.Equal(expected.GetDescriptorSetSpaceOffset(set), actual.GetDescriptorSetSpaceOffset(set));
            Assert.Equal(expected.GetBindingRangeLeafVariable(range).Name, actual.GetBindingRangeLeafVariable(range).Name);
            for (nint index = first; index < first + count; index++)
            {
                Assert.Equal(expected.GetDescriptorSetDescriptorRangeType(set, index),
                    actual.GetDescriptorSetDescriptorRangeType(set, index));
                Assert.Equal(expected.GetDescriptorSetDescriptorRangeCategory(set, index),
                    actual.GetDescriptorSetDescriptorRangeCategory(set, index));
                Assert.Equal(expected.GetDescriptorSetDescriptorRangeIndexOffset(set, index),
                    actual.GetDescriptorSetDescriptorRangeIndexOffset(set, index));
                Assert.Equal(expected.GetDescriptorSetDescriptorRangeDescriptorCount(set, index),
                    actual.GetDescriptorSetDescriptorRangeDescriptorCount(set, index));
            }
        }
    }
}
