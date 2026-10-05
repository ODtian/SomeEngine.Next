using System.Buffers;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Vulkan.Tests;

// Slang's native global session outlives all sessions created by this collection.
// Each compilation creates its own session, source and component data.
public sealed class VulkanTestCompiler : IDisposable
{
    private readonly IGlobalSession _globalSession = CreateGlobalSession();

    public void Dispose()
    {
        // Release the collection's COM compilation data while its native parent remains alive.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.KeepAlive(_globalSession);
    }

    internal VulkanTestShader Compile(
        string source,
        params (string Name, SlangStage Stage)[] entries)
    {
        IGlobalSession global = _globalSession;
        SessionDesc description = new()
        {
            Targets =
            [
                new TargetDesc
                {
                    Format = SlangCompileTarget.Spirv,
                    Profile = global.FindProfile("glsl_460"),
                    Flags = SlangTargetFlags.GenerateSpirvDirectly,
                    CompilerOptionEntries =
                    [
                        new(
                            CompilerOptionName.VulkanUseEntryPointName,
                            CompilerOptionValue.FromInt(1)),
                    ],
                },
            ],
            DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
            CompilerOptionEntries =
            [
                new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1)),
                new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0, 0)),
            ],
        };
        Require(global.CreateSession(description, out ISession session), "session", null);
        ISlangBlob sourceBlob = Slang.CreateBlob(source);
        IModule? sourceUnit = session.LoadModuleFromSource(
            "vulkan_test",
            Path.Combine(AppContext.BaseDirectory, "vulkan_test.slang"),
            sourceBlob,
            out ISlangBlob? moduleDiagnostics);
        if (sourceUnit is null)
            throw new InvalidOperationException(Failure("module", moduleDiagnostics));

        var slangEntries = new IEntryPoint[entries.Length];
        IComponentType[] components = new IComponentType[entries.Length + 1];
        components[0] = sourceUnit;
        for (int index = 0; index < entries.Length; index++)
        {
            Require(
                sourceUnit.FindEntryPointByName(entries[index].Name, out IEntryPoint entry),
                $"entry {entries[index].Name}",
                null);
            slangEntries[index] = entry;
            components[index + 1] = entry;
        }
        Require(
            session.CreateCompositeComponentType(
                components,
                out IComponentType combinedCode,
                out ISlangBlob? compositionDiagnostics),
            "composition",
            compositionDiagnostics);
        Require(
            combinedCode.Link(out IComponentType linkedCode, out ISlangBlob? linkDiagnostics),
            "link",
            linkDiagnostics);
        ShaderReflection reflection = linkedCode.GetLayout(0, out ISlangBlob? layoutDiagnostics);
        if (reflection == ShaderReflection.Null)
            throw new InvalidOperationException(Failure("layout", layoutDiagnostics));
        var reflectedEntries = new EntryPointReflection[entries.Length];
        for (int index = 0; index < entries.Length; index++)
        {
            reflectedEntries[index] = reflection.GetEntryPointByIndex(checked((uint)index));
            if (reflectedEntries[index] == EntryPointReflection.Null ||
                reflectedEntries[index].Stage != entries[index].Stage)
            {
                throw new InvalidOperationException($"Unexpected reflection for {entries[index].Name}.");
            }
        }
        var entryCode = new byte[entries.Length][];
        for (int index = 0; index < entryCode.Length; index++)
            entryCode[index] = ReadEntryCode(linkedCode, index);
        byte[] targetCode = ReadTargetCode(linkedCode);
        return new VulkanTestShader(
            session,
            sourceUnit,
            slangEntries,
            combinedCode,
            linkedCode,
            reflection,
            reflectedEntries,
            entryCode,
            targetCode);
    }

    private static IGlobalSession CreateGlobalSession()
    {
        Require(Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession session), "global session", null);
        return session;
    }

    internal static void Require(SlangResult result, string operation, ISlangBlob? diagnostics)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(Failure(operation, diagnostics));
    }

    private static string Failure(string operation, ISlangBlob? diagnostics) =>
        string.IsNullOrWhiteSpace(diagnostics?.AsString)
            ? operation
            : $"{operation}: {diagnostics.AsString}";

    private static byte[] ReadEntryCode(IComponentType slangCode, int index)
    {
        SlangResult result = slangCode.GetEntryPointCode(
            index,
            0,
            out ISlangBlob code,
            out ISlangBlob? diagnostics);
        Require(result, $"entry code {index}", diagnostics);
        return code.Buffer.ToArray();
    }

    private static byte[] ReadTargetCode(IComponentType slangCode)
    {
        SlangResult result = slangCode.GetTargetCode(
            0,
            out ISlangBlob code,
            out ISlangBlob? diagnostics);
        Require(result, "target code", diagnostics);
        return code.Buffer.ToArray();
    }
}
