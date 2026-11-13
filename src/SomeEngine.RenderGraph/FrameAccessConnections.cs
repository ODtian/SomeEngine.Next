namespace SomeEngine.RenderGraph;

internal sealed partial class FrameConnections
{
    // Frame additions connect only the resources they touch to the shared persistent schedule.
    private void ConnectFrameAccesses(List<int> order)
    {
        var ranks = order.Select((pass, index) => (pass, index)).ToDictionary(value => value.pass, value => value.index);
        var resources = _accesses.Where(access => access.Identity.ScopeId == _frame.Identity || IsInputAccess(access.Identity))
            .Select(access => (access.TargetKind, access.ResourceIndex)).Distinct();
        foreach (var resource in resources)
        {
            int[] accesses = _resourceAccessLists[resource].Where(index =>
                    _passes[_accesses[index].PassIndex].Enabled && ranks.ContainsKey(_accesses[index].PassIndex))
                .OrderBy(index => ranks[_accesses[index].PassIndex]).ThenBy(index => index).ToArray();
            if (resource.TargetKind == GraphAccessTargetKind.Buffer)
            {
                int count = BuildBufferCoordinates(_buffers[resource.ResourceIndex], accesses.ToList());
                ulong[] points = _bufferCoordinateScratch.AsSpan(0, count).ToArray();
                for (int segment = 0; segment + 1 < points.Length; segment++)
                {
                    ulong first = points[segment], end = points[segment + 1];
                    Connect(accesses.Where(index => CoversBuffer(index, first, end)));
                }
            }
            else if (resource.TargetKind == GraphAccessTargetKind.Texture)
            {
                FrameTexture texture = _textures[resource.ResourceIndex];
                int count = BuildTextureCoordinates(texture, accesses.ToList());
                for (int cell = 0; cell < count; cell++)
                {
                    int selected = cell;
                    Connect(accesses.Where(index => CoversTexture(index, texture, selected)));
                }
            }
            else Connect(accesses);
        }

        bool CoversBuffer(int index, ulong first, ulong end)
        {
            foreach (BufferRange range in _accesses[index].BufferRanges)
                if (range.Offset <= first && range.Offset + range.Size >= end) return true;
            return false;
        }
        bool CoversTexture(int index, FrameTexture texture, int selected)
        {
            foreach (int cell in TextureRegions(texture, _accesses[index].TextureRange))
                if (cell == selected) return true;
            return false;
        }

        void Connect(IEnumerable<int> accesses)
        {
            var writers = new List<int>();
            var readers = new List<int>();
            TextureLayout? layout = null;
            foreach (int index in accesses)
            {
                FrameResourceAccess access = _accesses[index];
                int pass = access.PassIndex;
                if (access.Mode != GraphAccessMode.Write || access.Coverage == WriteCoverage.Partial)
                    foreach (int writer in writers) FrameEdge(writer, pass, value: true);
                if (access.TargetKind == GraphAccessTargetKind.Texture && layout is { } previous && previous != access.TextureLayout)
                    foreach (int reader in readers) FrameEdge(reader, pass, value: false);
                layout = access.TextureLayout;
                if (access.Mode == GraphAccessMode.Read) { readers.Add(pass); continue; }
                foreach (int writer in writers) FrameEdge(writer, pass, value: false);
                foreach (int reader in readers) FrameEdge(reader, pass, value: false);
                readers.Clear();
                if (access.UnorderedAccessHazards == UnorderedAccessHazardMode.Ordered) writers.Clear();
                writers.Add(pass);
            }
        }
    }

    private bool IsInputAccess(GraphIdentity identity)
    {
        if (!identity.IsValid || identity.ScopeId != _frame.Graph.Identity) return false;
        PassResourceAccess access = _frame.StructureIndex.Structure.Accesses.Get(identity);
        return access.BufferInput is not null || access.TextureInput is not null || access.ParameterInput is not null;
    }
}
