using SlangShaderSharp;
using SomeEngine.Resources;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;

namespace SomeEngine.Assets.Schema;

public sealed record ShaderSourceFile(string Path, string ContentHash);

/// <summary>Compiled shader, serialized Slang inputs, and hashes of the bytes supplied to the compiler.</summary>
public sealed record ShaderCompilation(Shader Shader, IReadOnlyList<ReadOnlyMemory<byte>> SerializedSources,
    IReadOnlyList<ShaderSourceFile> Files)
{
    public IReadOnlyList<ShaderSpecialization> Specializations { get; init; } = [];
}

[GeneratedComClass]
internal partial class ShaderSourceFiles(ISlangFileSystem? supplied) : ISlangFileSystem
{
    private readonly Dictionary<string, ISlangBlob> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ShaderSourceFile> _hashes = new(StringComparer.OrdinalIgnoreCase);

    public unsafe void* CastAs(Guid guid) => null;

    public SlangResult LoadFile(string path, out ISlangBlob outBlob)
    {
        string fullPath = Path.GetFullPath(path);
        if (_files.TryGetValue(fullPath, out outBlob!)) return SlangResult.SLANG_OK;
        try
        {
            if (supplied is not null)
            {
                SlangResult result = supplied.LoadFile(path, out outBlob);
                if (result.Failed) return result;
            }
            else outBlob = Slang.CreateBlob(File.ReadAllBytes(fullPath));
            _files.Add(fullPath, outBlob);
            _hashes.Add(fullPath, new(fullPath, Convert.ToHexStringLower(SHA256.HashData(outBlob.Buffer))));
            return SlangResult.SLANG_OK;
        }
        catch (IOException) { outBlob = null!; return SlangResult.SLANG_E_NOT_FOUND; }
        catch (UnauthorizedAccessException) { outBlob = null!; return SlangResult.SLANG_E_NOT_FOUND; }
    }

    internal ShaderSourceFile[] Finish()
    {
        ShaderSourceFile[] files = _hashes.Values.OrderBy(static file => file.Path, StringComparer.Ordinal).ToArray();
        _files.Clear();
        _hashes.Clear();
        return files;
    }
}
