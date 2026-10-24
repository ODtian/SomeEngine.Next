namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer
{
    internal TBackend RequireNativeBackend<TBackend>() where TBackend : class, IGraphicsBackend =>
        Backend as TBackend ?? throw new InvalidOperationException($"Validation does not wrap {typeof(TBackend).Name}.");

    internal void RequireNativeQueue(Queue queue) => RequireQueue(queue);
    internal void RejectNativeAccess(string area, string message) => Reject(area, message);

    internal void RequireNativeDevice(Device device) => RequireDevice(device);

    internal void RequireNativeResource<T>(T resource)
        where T : DeviceResource =>
        Require(resource);

    internal void RequireNativePipeline(Pipeline pipeline, bool stateObject)
    {
        Require(pipeline);
        bool isStateObject = pipeline.Type is PipelineType.RayTracing or PipelineType.WorkGraph;
        if (isStateObject != stateObject)
        {
            Reject(
                "NativeAccess",
                stateObject
                    ? "GetNativeStateObject requires a RayTracing or WorkGraph Pipeline."
                    : "GetNativePipelineState requires a Graphics, Compute or Mesh Pipeline.",
                pipeline.Label);
        }
    }

    internal Buffer ImportNativeBuffer(Device device, Func<Buffer> create)
    {
        RequireDevice(device);
        RequireCapability<ExternalResources>(device);
        var validationState = new ResourceValidationState(buffer: true);
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _resourceStates.EnsureAdditionalCapacity();
            Buffer? result = null;
            bool objectAdded = false;
            bool stateAdded = false;
            try
            {
                result = create();
                validationState.Bind(result);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _resourceStates.Add(result, validationState);
                stateAdded = true;
                return result;
            }
            catch
            {
                if (stateAdded)
                    _resourceStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    internal Texture ImportNativeTexture(Device device, in TextureDesc description, Func<TextureDesc, Texture> create)
    {
        RequireDevice(device);
        RequireCapability<ExternalResources>(device);
        var validationState = new ResourceValidationState(buffer: false);
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _resourceStates.EnsureAdditionalCapacity();
            Texture? result = null;
            bool objectAdded = false;
            bool stateAdded = false;
            try
            {
                result = create(description);
                validationState.Bind(result);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _resourceStates.Add(result, validationState);
                stateAdded = true;
                return result;
            }
            catch
            {
                if (stateAdded)
                    _resourceStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    internal Heap ImportNativeHeap(Device device, in HeapDesc desc, Func<Heap> create)
    {
        RequireDevice(device);
        RequireCapability<ExternalResources>(device);
        var metadata = new HeapValidationState(desc.VisibleNodeMask);
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _heapStates.EnsureAdditionalCapacity();
            Heap? result = null;
            bool objectAdded = false;
            bool stateAdded = false;
            try
            {
                result = create();
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _heapStates.Add(result, metadata);
                stateAdded = true;
                return result;
            }
            catch
            {
                if (stateAdded)
                    _heapStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    internal void ValidateNativeCommands(CommandContext context, ReadOnlySpan<Resource> retainedResources)
    {
        ContextValidationState state = RequireRecording(context);
        foreach (Resource resource in retainedResources)
            RequireOnDevice(context.Device, resource, "native command-list retained Resource");

        lock (state)
        {
            CommandMutationCapacity capacity = default;
            foreach (Resource resource in retainedResources)
                PrepareCommandDependencyCore(state, resource, ref capacity);
            ReserveCommandMutation(state, capacity);
        }

    }

    internal void RecordNativeCommands(CommandContext context, ReadOnlySpan<Resource> retainedResources)
    {
        ContextValidationState state = RequireRecording(context);

        lock (state)
        {
            foreach (Resource resource in retainedResources)
                RecordCommandDependencyCore(state, resource);
            state.Pipeline = null;
            state.PipelineType = null;
            state.WorkGraphBound = false;
            state.QueryPhases.Clear();
            state.ResourceStates.Clear();
        }
    }

}
