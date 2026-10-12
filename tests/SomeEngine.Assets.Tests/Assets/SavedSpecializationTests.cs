using System.Buffers;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class SavedSpecializationTests
{
    [Fact]
    public async Task SavedEntryVariantsSurviveAssetDisposalAndShareTheirFirstRestoredShader()
    {
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        var description = new SessionDesc
        {
            Targets = [new TargetDesc { Format = SlangCompileTarget.Spirv, Profile = slang.FindProfile("spirv_1_5") }],
        };
        Assert.True(slang.CreateSession(description, out ISession session).Succeeded);
        IModule? unit = session.LoadModuleFromSource("SavedVariants", "SavedVariants.slang", Slang.CreateBlob(
            System.Text.Encoding.UTF8.GetBytes("""
                RWStructuredBuffer<uint> Left;
                RWStructuredBuffer<uint> Right;
                [shader("compute")][numthreads(1,1,1)]
                void Select<let left : bool>() { if (left) Left[0] = 7; else Right[0] = 9; }
                [shader("compute")][numthreads(1,1,1)]
                void Ordinary() { Left[0] = 3; }
                """)), out _);
        Assert.NotNull(unit);
        Assert.True(unit.Serialize(out ISlangBlob serialized).Succeeded);
        Shader saved;
        byte[] expectedCode;
        IReadOnlySet<SomeEngine.Graphics.SlangDescriptorLocation> expectedUsage;
        using (var source = new ShaderSourceBlock { SourceNames = ["SavedVariants.slang"] })
        using (var code = new ShaderBlock())
        {
            source.SetSerializedSource(0, serialized.Buffer);
            source.Specializations.Add(new ShaderSpecialization { EntryPoint = "Select", Arguments = ["false"] });
            source.Specializations.Add(new ShaderSpecialization { EntryPoint = "Select", Arguments = ["true"] });
            Shader compiled = await source.CreateAsync(slang, description, "saved variants");
            for (int entry = 0; entry < compiled.SlangEntryCount; entry++)
            {
                if (compiled.GetSlangEntryPoint(entry).GetSpecializationParamCount() != 0) continue;
                code.SetEntryCode(description, 0, entry, compiled.GetSlangEntryBlob(0, entry)!.Buffer);
                code.SetEntryUsage(description, 0, entry, compiled.GetSlangUsedParameters(0, entry));
            }
            foreach (string argument in new[] { "false", "true" })
            {
                Shader variant = compiled.RequireSpecialization("Select", [argument]);
                code.SetEntryCode(description, 0, 0, variant.GetSlangEntryBlob(0, 0)!.Buffer, [argument], "Select");
                code.SetEntryUsage(description, 0, 0, variant.GetSlangUsedParameters(0, 0), [argument], "Select");
            }
            Shader expected = compiled.RequireSpecialization("Select", ["true"]);
            expectedCode = expected.GetSlangEntryBlob(0, 0)!.Buffer.ToArray();
            expectedUsage = expected.GetSlangUsedParameters(0, 0);
            saved = await code.CreateAsync(source, slang, description, "saved variants");
        }

        Shader[] selected = await Task.WhenAll(
            Task.Run(() => saved.RequireSpecialization("Select", ["true"])),
            Task.Run(() => saved.RequireSpecialization("Select", ["true"])));
        Assert.Same(selected[0], selected[1]);
        Assert.True(saved.TryGetSpecialization("Select", ["true"], out Shader cached));
        Assert.Same(selected[0], cached);
        Assert.Equal(expectedCode, cached.GetSlangEntryBytes(0, 0).ToArray());
        Assert.True(expectedUsage.SetEquals(cached.GetSlangUsedParameters(0, 0)));
        Assert.NotSame(cached, saved.RequireSpecialization("Select", ["false"]));
        Assert.False(saved.HasSpecializations("Ordinary"));
        Assert.True(saved.TryGetSlangEntry(0, "Ordinary", out EntryPointReflection ordinary));
        Assert.False(saved.GetSlangEntryBytes(0, ordinary).IsEmpty);
        Assert.Throws<InvalidDataException>(() => saved.RequireSpecialization("Select", ["2"]));
    }
}
