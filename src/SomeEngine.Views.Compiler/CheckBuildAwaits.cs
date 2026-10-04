using Microsoft.Build.Framework;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace SomeEngine.Views.Compiler;

/// <summary>Adds the build cancellation check at the compiler's await result consumption points.</summary>
public sealed class CheckBuildAwaits : Microsoft.Build.Utilities.Task
{
    [Required]
    public string AssemblyPath { get; set; } = "";
    public ITaskItem[] References { get; set; } = [];

    public override bool Execute()
    {
        try
        {
            BuildAwaitChecks.Apply(AssemblyPath, References.Select(reference => reference.ItemSpec));
            return true;
        }
        catch (Exception error)
        {
            Log.LogErrorFromException(error, showStackTrace: true);
            return false;
        }
    }
}

/// <summary>Completes compilation of BuildTask methods before managed or native execution.</summary>
public static class BuildAwaitChecks
{
    public static void Apply(string assemblyPath, IEnumerable<string>? references = null)
    {
        bool symbols = File.Exists(Path.ChangeExtension(assemblyPath, ".pdb"));
        using var resolver = ResolveReferences(references);
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath,
            new ReaderParameters { ReadWrite = true, ReadSymbols = symbols, AssemblyResolver = resolver });
        if (Apply(assembly))
            assembly.Write(new WriterParameters { WriteSymbols = symbols });
    }

    public static void Apply(Stream assemblyImage, IEnumerable<string>? references = null)
    {
        assemblyImage.Position = 0;
        using var resolver = ResolveReferences(references);
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyImage,
            new ReaderParameters { ReadingMode = ReadingMode.Immediate, AssemblyResolver = resolver });
        if (!Apply(assembly))
            return;
        using var result = new MemoryStream();
        assembly.Write(result);
        assemblyImage.SetLength(0);
        result.Position = 0;
        result.CopyTo(assemblyImage);
    }

    private static DefaultAssemblyResolver ResolveReferences(IEnumerable<string>? references)
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (string directory in (references ?? []).Select(Path.GetDirectoryName).Distinct())
            if (!string.IsNullOrEmpty(directory))
                resolver.AddSearchDirectory(directory);
        resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location));
        return resolver;
    }

    private static bool Apply(AssemblyDefinition assembly)
    {
        var code = assembly.MainModule;
        IMetadataScope? views = code.Assembly.Name.Name == "SomeEngine.Views" ? code :
            code.AssemblyReferences.FirstOrDefault(reference => reference.Name == "SomeEngine.Views");
        if (views is null)
            return false;
        var builder = new TypeReference("SomeEngine.Views", "BuildTaskBuilder", code, views, valueType: true);
        var check = new MethodReference("CheckCancellation", code.TypeSystem.Void, builder) { HasThis = true };
        var begin = new MethodReference("BeginAwait", code.TypeSystem.Void, builder) { HasThis = true };
        bool changed = false;
        foreach (var type in Types(code.Types))
        {
            if (!type.Fields.Any(field => field.FieldType.FullName == builder.FullName) ||
                !type.Interfaces.Any(value => value.InterfaceType.FullName == "System.Runtime.CompilerServices.IAsyncStateMachine"))
                continue;
            var resume = type.Methods.First(method => method.Name == "MoveNext");
            // Keep the compiler's generic state machine arguments on the field reference.
            var fields = resume.Body.Instructions.Select(instruction => instruction.Operand)
                .OfType<FieldReference>().Where(field => field.FieldType.FullName == builder.FullName).ToArray();
            var build = fields.FirstOrDefault(field => field.DeclaringType is GenericInstanceType) ?? fields[0];
            var results = resume.Body.Instructions.Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt &&
                instruction.Operand is MethodReference { Name: "GetResult", Parameters.Count: 0 } method &&
                type.Fields.Any(field => field.FieldType.FullName == method.DeclaringType.FullName)).ToArray();
            if (results.Length == 0)
                continue;
            resume.Body.SimplifyMacros();
            foreach (var result in results)
            {
                if (result.Next?.Next?.Next?.Operand is MethodReference next && next.FullName == check.FullName)
                {
                    // An incremental compile can reuse bytes written by the earlier compiler task.
                    foreach (var existingAddress in new[] { result.Previous?.Previous, result.Next.Next })
                        if (existingAddress?.Operand is FieldReference previous && previous.FullName != build.FullName)
                        {
                            existingAddress.Operand = build;
                            changed = true;
                        }
                    continue;
                }
                // Observe cancellation even when GetResult itself throws, so awaits in finally can finish.
                // Consume the result before delivering cancellation, including pooled ValueTask sources.
                var instructions = resume.Body.GetILProcessor();
                var first = Instruction.Create(OpCodes.Ldarg_0);
                instructions.InsertBefore(result, first);
                instructions.InsertBefore(result, Instruction.Create(OpCodes.Ldflda, build));
                instructions.InsertBefore(result, Instruction.Create(OpCodes.Call, begin));
                var receiver = Instruction.Create(OpCodes.Ldarg_0);
                var address = Instruction.Create(OpCodes.Ldflda, build);
                instructions.InsertAfter(result, receiver);
                instructions.InsertAfter(receiver, address);
                instructions.InsertAfter(address, Instruction.Create(OpCodes.Call, check));
                changed = true;
            }
            resume.Body.OptimizeMacros();
        }
        return changed;
    }

    private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in Types(type.NestedTypes))
                yield return nested;
        }
    }
}
