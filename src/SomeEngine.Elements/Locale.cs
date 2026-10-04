using System.Globalization;
using System.Runtime.CompilerServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Elements;

/// <summary>The current language on the World's single translation entity.</summary>
public struct Locale : IComponent
{
    private static readonly ConditionalWeakTable<World, LocaleEntity> s_entities = new();
    public string Name;

    internal static Entity Find(World world, bool required = true)
    {
        LocaleEntity data = s_entities.GetValue(world, static _ => new LocaleEntity());
        uint version = world.ComponentWriteVersion<Locale>();
        if (!data.HasResult || data.WriteVersion != version)
        {
            data.Entity = FindEntity(world);
            data.WriteVersion = version;
            data.HasResult = true;
            data.Culture = null;
        }
        if (required)
            DevelopmentCheck.Require(data.Entity != Entity.Null, "Localized text requires a Locale component.");
        return data.Entity;
    }

    private static Entity FindEntity(World world)
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
        LocaleEntity data = s_entities.GetValue(world, static _ => new LocaleEntity());
        if (data.Culture is null)
        {
            CheckName(name);
            data.Culture = CultureInfo.GetCultureInfo(name);
        }
        return data.Culture;
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

    private sealed class LocaleEntity
    {
        internal Entity Entity;
        internal uint WriteVersion;
        internal bool HasResult;
        internal CultureInfo? Culture;
    }
}
