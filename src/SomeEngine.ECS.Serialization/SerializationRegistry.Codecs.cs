using System.Collections.Immutable;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Serialization;

public sealed partial class SerializationRegistry
{
    internal static SerializationRegistry Bind(SerializationRegistry? source, ImmutableArray<ComponentCodec> codecs)
    {
        if (source is not null && codecs.IsDefaultOrEmpty) return source;
        var selected = new Dictionary<(SavedStorage Storage, int Type), SavedType>();
        var services = new Dictionary<int, SavedService>();
        var result = new SerializationRegistry();
        if (source is null)
        {
            for (int index = 0; index < ComponentTypes.Count; index++)
            {
                object? data = ComponentTypes.Get(index).SerializationDescription;
                ComponentCodec? description = data is ITopologySerializationType topology
                    ? ComponentCodec.Describe(topology) : data as ComponentCodec;
                if (description is not null)
                {
                    selected[(description.Value.Storage, description.Value.Type)] = description.Value;
                    foreach (SavedType related in description.Related) selected[(related.Storage, related.Type)] = related;
                    foreach (SavedService service in description.Services) services[service.Type.Type] = service;
                }
            }
        }
        else
        {
            foreach (SavedType type in source.Types)
                selected[(type.Storage, type.Type)] = type;
            foreach (SavedService service in source.Services) services[service.Type.Type] = service;
        }
        if (!codecs.IsDefaultOrEmpty)
            foreach (ComponentCodec codec in codecs)
            {
                selected[(codec.Value.Storage, codec.Value.Type)] = codec.Value;
                foreach (SavedType related in codec.Related) selected[(related.Storage, related.Type)] = related;
                foreach (SavedService service in codec.Services) services[service.Type.Type] = service;
            }
        IEnumerable<SavedType> values = source is null ? selected.Values.OrderBy(type => type.Key.StableId) : selected.Values;
        foreach (SavedType type in values)
        {
            if (type.Type == ComponentType<Destroyed>.TypeId && type.Storage == SavedStorage.Tag) continue;
            result.Add(type);
            result._entries.Add(type.Describe());
            if (type is SavedSparse sparse) result.Sparse.Add(type.Type, sparse);
            else if (type is SavedResource resource) result.Resources.Add(type.Type, resource);
        }
        foreach (SavedService service in services.Values)
        {
            result.Services.Add(service);
            result.ServicesByType.Add(service.Type.Type, service);
        }
        return result;
    }
}
