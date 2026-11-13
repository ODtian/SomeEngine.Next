namespace SomeEngine.RenderGraph;

internal sealed partial class FrameConnections
{
    private void ConnectInputValueOrigins(GraphSchedule schedule)
    {
        var resources = _accesses.Where(access => access.Connected && IsInputAccess(access.Identity))
            .Select(access => (access.TargetKind, access.ResourceIndex)).Distinct();
        foreach (var resource in resources)
        {
            int[] accesses = _resourceAccessLists[resource]
                .Where(index => _passes[_accesses[index].PassIndex].Enabled).ToArray();
            var users = accesses.Select(index => _accesses[index].PassIndex).ToHashSet();
            var before = users.ToDictionary(pass => pass, _ => new HashSet<int>());
            foreach (int pass in users)
            {
                var visited = new HashSet<int>();
                var work = new Stack<int>(Predecessors(pass));
                while (work.TryPop(out int predecessor))
                {
                    if (!visited.Add(predecessor)) continue;
                    if (users.Contains(predecessor)) before[pass].Add(predecessor);
                    else foreach (int previous in Predecessors(predecessor)) work.Push(previous);
                }
            }
            int[] order = SortSection(users.ToArray(), before);
            var ranks = order.Select((pass, index) => (pass, index))
                .ToDictionary(value => value.pass, value => value.index);
            accesses = accesses.OrderBy(index => ranks[_accesses[index].PassIndex])
                .ThenBy(index => _accesses[index].PassAdjacencyIndex).ToArray();
            if (resource.TargetKind == GraphAccessTargetKind.Buffer)
            {
                int count = BuildBufferCoordinates(_buffers[resource.ResourceIndex], accesses.ToList());
                for (int segment = 0; segment + 1 < count; segment++)
                {
                    ulong first = _bufferCoordinateScratch[segment], end = _bufferCoordinateScratch[segment + 1];
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

        IEnumerable<int> Predecessors(int pass)
        {
            if (schedule.Passes.TryGetValue(_passes[pass].Identity, out GraphSchedulePass? persistent))
                foreach (GraphIdentity predecessor in persistent.Predecessors) yield return schedule.GetPassRow(predecessor);
            if (PredecessorAdditions.TryGetValue(pass, out HashSet<int>? additions))
                foreach (int predecessor in additions) yield return predecessor;
        }

        void Connect(IEnumerable<int> accesses)
        {
            var writers = new List<int>();
            foreach (int index in accesses)
            {
                FrameResourceAccess access = _accesses[index];
                if (access.Mode != GraphAccessMode.Write || access.Coverage == WriteCoverage.Partial)
                    foreach (int writer in writers) FrameEdge(writer, access.PassIndex, value: true);
                if (access.Mode == GraphAccessMode.Read) continue;
                if (access.UnorderedAccessHazards == UnorderedAccessHazardMode.Ordered) writers.Clear();
                writers.Add(access.PassIndex);
            }
        }
    }
}
