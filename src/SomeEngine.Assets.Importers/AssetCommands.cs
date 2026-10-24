namespace SomeEngine.Assets.Importers;

/// <summary>Runs development asset commands using the project's selected importers and source formats.</summary>
public static class AssetCommands
{
    /// <summary>Identifies the engine's asset commands so applications can route their other startup modes.</summary>
    public static bool Matches(IReadOnlyList<string> arguments) => GetCommand(arguments) != AssetCommand.None;

    private static AssetCommand GetCommand(IReadOnlyList<string> arguments) => arguments.Count == 0 ? AssetCommand.None : arguments[0] switch
    {
        "import" => AssetCommand.Import,
        "build" => AssetCommand.Build,
        "list" => AssetCommand.List,
        _ => AssetCommand.None,
    };

    private enum AssetCommand : byte { None, Import, Build, List }

    public static async ValueTask<int> RunAsync(AssetProject project, IReadOnlyList<string> arguments,
        TextWriter output, CancellationToken cancellationToken = default)
    {
        AssetCommand command = GetCommand(arguments);
        if (arguments.Count > 1 && command == AssetCommand.Import)
        {
            for (int index = 1; index < arguments.Count; index++)
            {
                IReadOnlyList<Guid> ids = await project.ImportAsync(arguments[index], cancellationToken).ConfigureAwait(false);
                foreach (Guid id in ids)
                    await output.WriteLineAsync(id.ToString()).ConfigureAwait(false);
            }
            return 0;
        }

        if (arguments.Count > 1 && command == AssetCommand.Build)
        {
            for (int index = 1; index < arguments.Count; index++)
            {
                Guid id = await project.BuildAsync(arguments[index], cancellationToken: cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(id.ToString()).ConfigureAwait(false);
            }
            return 0;
        }

        if (arguments.Count is 1 or 2 && command == AssetCommand.List)
        {
            foreach (AssetEntry asset in project.List(arguments.Count == 2 ? arguments[1] : null))
                await output.WriteLineAsync($"{asset.AssetGuid}\t{asset.AssetType}\t{asset.Path}").ConfigureAwait(false);
            return 0;
        }

        await output.WriteLineAsync("Usage: import <source>... | build <definition>... | list [asset-type]").ConfigureAwait(false);
        return 2;
    }
}
