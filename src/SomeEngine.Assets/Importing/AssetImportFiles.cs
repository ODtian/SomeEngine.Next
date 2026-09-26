namespace SomeEngine.Assets.Importing;

/// <summary>Replaces related files together, restoring previous files when a replacement fails.</summary>
internal sealed class AssetImportFiles : IDisposable
{
    private readonly List<FileChange> _changes = [];
    private readonly HashSet<string> _paths = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private bool _committed;
    private Exception? _failure;

    public string CreateStagePath(string destination)
    {
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        return Path.Combine(Path.GetDirectoryName(destination)!, $".{Guid.NewGuid():N}.tmp.{Path.GetFileName(destination)}");
    }

    public void AddFile(string destination, string stagedFile)
        => Add(new FileChange(Path.GetFullPath(destination), stagedFile));

    public void AddWrite(string destination, byte[] bytes)
    {
        string stage = CreateStagePath(destination);
        Add(new FileChange(Path.GetFullPath(destination), stage, copyBackup: true));
        File.WriteAllBytes(stage, bytes);
    }

    public void AddDelete(string destination) => Add(new FileChange(Path.GetFullPath(destination), null));

    private void Add(FileChange change)
    {
        if (_committed) throw new InvalidOperationException("File changes have already been committed.");
        if (!_paths.Add(change.Destination)) throw new InvalidOperationException($"The import writes '{change.Destination}' more than once.");
        _changes.Add(change);
    }

    public void Commit(CancellationToken cancellationToken = default)
    {
        if (_committed) throw new InvalidOperationException("File changes have already been committed.");
        // Cancellation has one boundary. Once replacement begins, either all files commit or all are restored.
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            foreach (FileChange change in _changes)
            {
                if (File.Exists(change.Destination))
                {
                    string backup = CreateStagePath(change.Destination);
                    if (change.StagedFile is not null)
                    {
                        if (change.CopyBackup)
                        {
                            // Small serialized metadata needs one name change. A replacement that
                            // also renames the old file to a backup can briefly hide the catalog.
                            File.Copy(change.Destination, backup);
                            change.Backup = backup;
                            ImportFileRename.Replace(change.StagedFile, change.Destination);
                        }
                        else
                        {
                            File.Replace(change.StagedFile, change.Destination, backup);
                            change.Backup = backup;
                        }
                        change.Installed = true;
                        continue;
                    }
                    File.Move(change.Destination, backup);
                    change.Backup = backup;
                    change.OriginalMoved = true;
                }
                if (change.StagedFile is not null)
                {
                    File.Move(change.StagedFile, change.Destination);
                    change.Installed = true;
                }
            }
            _committed = true;
        }
        catch (Exception error)
        {
            List<Exception> failures = [error];
            foreach (FileChange change in _changes.AsEnumerable().Reverse())
            {
                try
                {
                    if (change.Installed && change.Backup is not null)
                    {
                        ImportFileRename.Replace(change.Backup, change.Destination);
                        change.Backup = null;
                    }
                    else
                    {
                        if (change.Installed) File.Delete(change.Destination);
                        if (change.OriginalMoved && change.Backup is not null) { File.Move(change.Backup, change.Destination); change.Backup = null; }
                    }
                }
                catch (Exception rollback) { failures.Add(rollback); change.KeepBackup = true; }
            }
            if (failures.Count > 1)
            {
                _failure = new AggregateException("Import failed and some original files could not be restored; their backup files were retained.", failures);
                throw _failure;
            }
            _failure = error;
            throw;
        }
    }

    public void Dispose()
    {
        List<Exception>? failures = null;
        foreach (FileChange change in _changes)
        {
            if (change.StagedFile is not null) Remove(change.StagedFile);
            if (!change.KeepBackup && change.Backup is not null) Remove(change.Backup);
        }
        if (failures is null) return;
        var cleanup = new AggregateException("Some import temporary files could not be removed.", failures);
        if (_failure is not null) _failure.Data["ImportFileCleanup"] = cleanup;
        else throw cleanup;

        void Remove(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
    }

    public void RecordFailure(Exception error) => _failure = error;

    private sealed class FileChange(string destination, string? stagedFile, bool copyBackup = false)
    {
        public string Destination { get; } = destination;
        public string? StagedFile { get; } = stagedFile;
        public bool CopyBackup { get; } = copyBackup;
        public string? Backup { get; set; }
        public bool OriginalMoved { get; set; }
        public bool Installed { get; set; }
        public bool KeepBackup { get; set; }
    }
}
