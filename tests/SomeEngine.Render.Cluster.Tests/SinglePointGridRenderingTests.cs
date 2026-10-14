using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using SlangShaderSharp;
using SomeEngine.Render;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class SinglePointGridRenderingTests
{
    [Fact]
    [Trait("Category", "Hardware")]
    public async Task MovingSinglePointPreservesColorDepthAndShadowCoverageWithFinalGrid()
    {
        Shader original = LoadLighting(["ClearClusteredLightGrid", "BuildClusteredLightGrid", "CompactClusteredLightGrid"]);
        Shader candidate = LoadLighting(["ClearClusteredLightGrid", "BuildClusteredLightGrid", "CompactClusteredLightGrid",
            "PrepareSinglePointLightGrid", "FillSinglePointLightGrid"]);
        var options = new ClusterPipelineOptions { MaxCandidates = 4096, EnableFrameMetricsReadback = true };
        await using var expected = await FrameStageScene.CreateAsync("D3D12", true, options, lightingKernel: original);
        await using var actual = await FrameStageScene.CreateAsync("D3D12", true, options, lightingKernel: candidate);
        byte[]? firstColor = null, firstDepth = null;
        int changedColors = 0, changedDepths = 0;
        double maximumEnergy = 0;
        ulong shadowWork = 0, requestedPages = 0;
        for (int frame = 0; frame < 16; frame++)
        {
            float angle = frame * MathF.Tau / 16;
            Vector3 eye = new(.6f * MathF.Sin(angle), .25f * MathF.Cos(angle), .15f * MathF.Sin(angle * 2));
            var view = new RenderView(Matrix4x4.CreateLookAt(eye, new(0, 0, -7), Vector3.UnitY),
                Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100), 64, 64);
            var point = new PointLight(new(4 * MathF.Sin(angle), 2 + MathF.Cos(angle), -3 + .5f * MathF.Cos(angle * 2)),
                12 + 4 * MathF.Cos(angle), new(1, .8f, .6f), 30 + 5 * MathF.Sin(angle));
            expected.SetView(view);
            actual.SetView(view);
            expected.SetPointLight(point);
            actual.SetPointLight(point);
            var reference = await expected.ReadShadowQualityAsync();
            var result = await actual.ReadShadowQualityAsync();
            RasterImage.AssertEqual(reference.Image, result.Image,
                nameof(MovingSinglePointPreservesColorDepthAndShadowCoverageWithFinalGrid) + $"-{frame}");
            Assert.Equal(0u, reference.Quality.AllocationFailures);
            Assert.Equal(0u, result.Quality.AllocationFailures);
            Assert.False(reference.Quality.LightAssignmentOverflow);
            Assert.False(result.Quality.LightAssignmentOverflow);
            Assert.Equal(0u, (uint)reference.Quality.WorkOverflow);
            Assert.Equal(0u, (uint)result.Quality.WorkOverflow);
            requestedPages += result.Quality.RequestedPages;
            foreach (var item in result.Quality.Views) shadowWork += item.HardwareVisibleItems + item.SoftwareVisibleItems;
            Half[] channels = MemoryMarshal.Cast<byte, Half>(reference.Image.Color).ToArray();
            Assert.All(channels, value => Assert.True(Half.IsFinite(value)));
            double energy = 0;
            for (int i = 0; i < channels.Length; i++)
                if ((i & 3) != 3) energy += Math.Abs((float)channels[i]);
            maximumEnergy = Math.Max(maximumEnergy, energy);
            firstColor ??= reference.Image.Color;
            firstDepth ??= reference.Image.Depth;
            if (!firstColor.AsSpan().SequenceEqual(reference.Image.Color)) changedColors++;
            if (!firstDepth.AsSpan().SequenceEqual(reference.Image.Depth)) changedDepths++;
        }
        Assert.True(maximumEnergy > 100);
        Assert.True(changedColors > 8);
        Assert.True(changedDepths > 8);
        Assert.True(requestedPages > 0);
        Assert.True(shadowWork > 0);
    }

    [Fact]
    public void LinkedLightingKeepsLegacyEntriesWhenDirectPartnersAreOmitted()
    {
        LoadLighting(["ClearClusteredLightGrid", "BuildClusteredLightGrid", "CompactClusteredLightGrid"]);
        LoadLighting(["ClearClusteredLightGrid", "BuildClusteredLightGrid", "CompactClusteredLightGrid",
            "PrepareSinglePointLightGrid", "FillSinglePointLightGrid"]);
    }

    private static Shader LoadLighting(string[] entryNames)
    {
        string engine = FrameStageScene.FindRoot();
        string sourcePath = Path.Combine(engine, "assets", "Shaders", "clustered_light_grid.slang");
        var description = TestShaderLoading.CreateSessionDescription(SlangCompileTarget.Dxil);
        description.SearchPaths = [Path.Combine(engine, "assets", "Shaders")];
        IGlobalSession slang = TestShaderLoading.Slang;
        Assert.True(slang.CreateSession(description, out ISession session).Succeeded);
        IModule? unit = session.LoadModuleFromSource("SinglePointGrid", sourcePath,
            Slang.CreateBlob(File.ReadAllBytes(sourcePath)), out ISlangBlob? diagnostics);
        Assert.True(unit is not null, Message(diagnostics));
        var entries = new List<IEntryPoint>();
        foreach (string name in entryNames)
        {
            Assert.True(unit!.FindEntryPointByName(name, out IEntryPoint entry).Succeeded);
            entries.Add(entry);
        }
        Assert.True(session.CreateCompositeComponentType([unit!, .. entries], out IComponentType combined, out diagnostics).Succeeded,
            Message(diagnostics));
        Assert.True(combined.Link(out IComponentType linked, out diagnostics).Succeeded, Message(diagnostics));
        ShaderReflection reflection = linked.GetLayout(0, out diagnostics);
        Assert.Equal((uint)entryNames.Length, reflection.EntryPointCount);
        Assert.Equal(new[] { "LightingViews", "LightBuffer", "ClusteredLightGrid", "LightIndexList",
            "LightLinkList", "LightAllocationCounters", "LightAllocationStatus" },
            ResourceNames(reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null));
        for (uint i = 0; i < reflection.EntryPointCount; i++)
        {
            EntryPointReflection entry = reflection.GetEntryPointByIndex(i);
            Assert.Equal(entryNames[i], entry.Name);
            string? expectedRole = entry.Name switch
            {
                "BuildClusteredLightGrid" => "build-v2",
                "PrepareSinglePointLightGrid" => "prepare-v2",
                "FillSinglePointLightGrid" => "fill-v2",
                _ => null,
            };
            var roles = new List<string>();
            for (uint attributeIndex = 0; attributeIndex < entry.Function.AttributeCount; attributeIndex++)
            {
                var attribute = entry.Function.GetAttribute(attributeIndex);
                if (attribute.Name != "ClusteredSinglePointDirect") continue;
                Assert.Equal(1u, attribute.ArgumentCount);
                roles.Add(attribute.GetArgumentValueString(0));
            }
            Assert.Equal(expectedRole is null ? Array.Empty<string>() : new[] { expectedRole }, roles);
            Assert.Equal(i < 3 ? Array.Empty<string>() : new[] { "SinglePointBounds" }, ResourceNames(entry.VarLayout));
        }
        return Shader.CreateFromSlang("Single point grid", slang, session, [unit!], entries, linked, description.Targets);
    }

    private static string[] ResourceNames(VariableLayoutReflection variable)
    {
        TypeLayoutReflection layout = variable.TypeLayout.UnwrapArray();
        if (layout.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            layout = layout.ElementTypeLayout.UnwrapArray();
        var names = new List<string>();
        for (nint i = 0; i < layout.BindingRangeCount; i++)
            names.Add(layout.GetBindingRangeLeafVariable(i).Name);
        return names.ToArray();
    }

    private static string Message(ISlangBlob? value) => value is null ? "" : Encoding.UTF8.GetString(value.Buffer);
}
