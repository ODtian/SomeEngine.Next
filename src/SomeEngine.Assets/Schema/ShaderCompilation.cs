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
    private readonly object _gate = new();
    private readonly Dictionary<string, ISlangBlob> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ShaderSourceFile> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, TrackedInput>? _trackedInputs;
    private Exception? _inputFailure;
    private bool _baseInputsFinished;

    public unsafe void* CastAs(Guid guid) => null;

    // Importer-only stable-input scope. Finish may clear a phase cache, but these
    // immutable owners and first hashes remain alive until every backend job has joined.
    internal IDisposable TrackInputVersions()
    {
        lock (_gate)
        {
            if (_trackedInputs is not null || _files.Count != 0 || _hashes.Count != 0)
                throw new InvalidOperationException("Input tracking must begin before source loading.");
            _trackedInputs = new(StringComparer.OrdinalIgnoreCase);
            _inputFailure = null;
            _baseInputsFinished = false;
            return new InputTracking(this);
        }
    }

    public SlangResult LoadFile(string path, out ISlangBlob outBlob)
    {
        lock (_gate)
        {
            if (_trackedInputs is null) return LoadUntrackedFile(path, out outBlob);
            string fullPath = Path.GetFullPath(path);
            if (_inputFailure is not null)
            {
                outBlob = null!;
                return SlangResult.SLANG_E_NOT_FOUND;
            }
            // Base receipts already determine the legacy first-path representative. For a
            // newly discovered path, racing case aliases have no recoverable variant order.
            bool caseAlias = _trackedInputs.TryGetValue(fullPath, out TrackedInput? representative)
                && !StringComparer.Ordinal.Equals(representative.Receipt.Path, fullPath);
            if (caseAlias && !representative!.HasBaseRepresentative)
            {
                _inputFailure ??= new IOException($"Shader input '{fullPath}' has ambiguous path casing during batched compilation.");
                outBlob = null!;
                return SlangResult.SLANG_E_NOT_FOUND;
            }
            // A differently spelled base alias must be read and hash-checked through the
            // actual source route, even when the case-insensitive phase cache has a hit.
            if (!caseAlias && _files.TryGetValue(fullPath, out outBlob!)) return SlangResult.SLANG_OK;
            try
            {
                ISlangBlob source;
                if (supplied is not null)
                {
                    SlangResult result = supplied.LoadFile(path, out source);
                    if (result.Failed)
                    {
                        if (_trackedInputs?.ContainsKey(fullPath) == true)
                            _inputFailure ??= new IOException($"Shader input '{fullPath}' could not be reopened.");
                        outBlob = null!;
                        return result;
                    }
                }
                else source = Slang.CreateBlob(File.ReadAllBytes(fullPath));

                // The default branch already creates an owned copy. Snapshot a supplied
                // blob before hashing so worker callbacks never observe mutable provider data.
                ISlangBlob owned;
                try
                {
                    owned = supplied is not null ? Slang.CreateBlob(source.Buffer) : source;
                }
                finally
                {
                    // The native-backed span alone does not keep its supplying COM owner alive.
                    GC.KeepAlive(source);
                }
                string hash;
                try
                {
                    hash = Convert.ToHexStringLower(SHA256.HashData(owned.Buffer));
                }
                finally
                {
                    // A same-content reopen can replace this temporary owner below.
                    GC.KeepAlive(owned);
                }
                ShaderSourceFile receipt = new(fullPath, hash);
                if (_trackedInputs is not null)
                {
                    if (_trackedInputs.TryGetValue(fullPath, out TrackedInput? first))
                    {
                        if (!StringComparer.Ordinal.Equals(first.Receipt.ContentHash, hash))
                        {
                            _inputFailure ??= new IOException($"Shader input '{fullPath}' changed during compilation.");
                            outBlob = null!;
                            return SlangResult.SLANG_E_NOT_FOUND;
                        }
                        owned = first.Blob;
                        receipt = first.Receipt;
                    }
                    else _trackedInputs.Add(fullPath, new(owned, receipt, !_baseInputsFinished));
                }
                // Commit neither phase map until the snapshot and its receipt are valid.
                _files[fullPath] = owned;
                _hashes[fullPath] = receipt;
                outBlob = owned;
                return SlangResult.SLANG_OK;
            }
            catch (IOException error)
            {
                if (_trackedInputs?.ContainsKey(fullPath) == true) _inputFailure ??= error;
                outBlob = null!;
                return SlangResult.SLANG_E_NOT_FOUND;
            }
            catch (UnauthorizedAccessException error)
            {
                if (_trackedInputs?.ContainsKey(fullPath) == true) _inputFailure ??= error;
                outBlob = null!;
                return SlangResult.SLANG_E_NOT_FOUND;
            }
            catch (Exception error) when (_trackedInputs is not null)
            {
                _inputFailure ??= error;
                outBlob = null!;
                return SlangResult.SLANG_E_NOT_FOUND;
            }
        }
    }

    private SlangResult LoadUntrackedFile(string path, out ISlangBlob outBlob)
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

    internal void ThrowIfInputChanged()
    {
        lock (_gate)
            if (_inputFailure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_inputFailure).Throw();
    }

    internal ShaderSourceFile[] Finish()
    {
        lock (_gate)
        {
            ThrowIfInputChanged();
            ShaderSourceFile[] files = _hashes.Values.OrderBy(static file => file.Path, StringComparer.Ordinal).ToArray();
            _files.Clear();
            _hashes.Clear();
            if (_trackedInputs is not null) _baseInputsFinished = true;
            return files;
        }
    }

    private void EndInputTracking()
    {
        lock (_gate)
        {
            _files.Clear();
            _hashes.Clear();
            _trackedInputs = null;
            _inputFailure = null;
            _baseInputsFinished = false;
        }
    }

    private sealed record TrackedInput(ISlangBlob Blob, ShaderSourceFile Receipt, bool HasBaseRepresentative);

    private sealed class InputTracking(ShaderSourceFiles owner) : IDisposable
    {
        private ShaderSourceFiles? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndInputTracking();
    }
}
