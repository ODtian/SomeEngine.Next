using System.Globalization;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Elements;

/// <summary>The current language on the World's single translation entity.</summary>
public struct Locale : IComponent
{
    public string Name;

    internal static Entity Find(World world, bool required = true)
    {
        var query = world.Query(world.QueryDefinition().All<Locale>());
        Entity found = Entity.Null;
        try
        {
            world.ExecuteQuery(query, ref found, static (rows, ref source) =>
            {
                foreach (var row in rows.Rows)
                {
                    DevelopmentCheck.Require(source == Entity.Null, "Text requires one Locale per World.");
                    source = row.Entity;
                }
            });
        }
        finally
        {
            world.ReleaseQuery(query);
        }
        if (required)
            DevelopmentCheck.Require(found != Entity.Null, "Localized text requires a Locale component.");
        return found;
    }

    internal static CultureInfo? ReadCulture(World world, bool track)
    {
        if (world.ComponentWriteVersion<Locale>() == 0)
            return null;
        Entity source = Find(world, required: false);
        if (source == Entity.Null)
            return null;
        string name = track ? Views.Scope.Read<Locale>(source).Name : world.Read<Locale>(source).Name;
        CheckName(name);
        return CultureInfo.GetCultureInfo(name);
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void CheckName(string name)
    {
        try
        {
            CultureInfo.GetCultureInfo(name);
        }
        catch (ArgumentException)
        {
            DevelopmentCheck.Fail($"Locale requires a supported culture name, received '{name}'.");
        }
    }
}
