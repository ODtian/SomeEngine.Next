using Vortice.Direct3D12.Debug;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed class NativeDiagnosticQueue
{
    private const string Source = "D3D12 Debug Layer";

    private readonly ID3D12InfoQueue _queue;
    private readonly object _gate = new();
    private ulong _reportedDiscardedMessages;

    public NativeDiagnosticQueue(ID3D12InfoQueue queue)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));

        InfoQueueFilter storageFilter = new()
        {
            AllowList = new InfoQueueFilterDescription
            {
                Categories = [],
                Severities = [],
                Ids = [],
            },
            DenyList = new InfoQueueFilterDescription
            {
                Categories = [],
                Severities = [MessageSeverity.Info, MessageSeverity.Message],
                Ids = [],
            },
        };

        _queue.AddStorageFilterEntries(storageFilter);
        _queue.ClearStoredMessages();
    }

    public GraphicsDiagnostic[] Drain()
    {
        lock (_gate)
        {
            List<GraphicsDiagnostic> diagnostics = new();
            try
            {
                ulong discardedMessages = _queue.NumMessagesDiscardedByMessageCountLimit;
                if (discardedMessages > _reportedDiscardedMessages)
                {
                    diagnostics.Add(new GraphicsDiagnostic(
                        GraphicsDiagnosticSeverity.Warning,
                        Source,
                        $"The native information queue discarded {discardedMessages - _reportedDiscardedMessages} message(s) after reaching its storage limit."));
                }
                _reportedDiscardedMessages = discardedMessages;

                ulong messageCount = _queue.NumStoredMessages;
                for (ulong index = 0; index < messageCount; index++)
                {
                    Message message = _queue.GetMessage(index);
                    diagnostics.Add(new GraphicsDiagnostic(
                        MapSeverity(message.Severity),
                        Source,
                        $"[{message.Category}] {message.Description?.TrimEnd('\0')}",
                        (int)message.Id));
                }
            }
            catch (Exception exception)
            {
                diagnostics.Add(new GraphicsDiagnostic(
                    GraphicsDiagnosticSeverity.Error,
                    Source,
                    $"Failed to read the native information queue: {exception.Message}"));
            }
            finally
            {
                try
                {
                    _queue.ClearStoredMessages();
                }
                catch (Exception exception)
                {
                    diagnostics.Add(new GraphicsDiagnostic(
                        GraphicsDiagnosticSeverity.Error,
                        Source,
                        $"Failed to clear the native information queue: {exception.Message}"));
                }
            }

            return diagnostics.ToArray();
        }
    }

    private static GraphicsDiagnosticSeverity MapSeverity(MessageSeverity severity) => severity switch
    {
        MessageSeverity.Corruption => GraphicsDiagnosticSeverity.Corruption,
        MessageSeverity.Error => GraphicsDiagnosticSeverity.Error,
        MessageSeverity.Warning => GraphicsDiagnosticSeverity.Warning,
        MessageSeverity.Info or MessageSeverity.Message => GraphicsDiagnosticSeverity.Information,
        _ => GraphicsDiagnosticSeverity.Information,
    };
}
