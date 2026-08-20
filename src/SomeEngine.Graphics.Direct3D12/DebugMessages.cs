using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    private static string? AppendDebugMessages(
        D3D12Device? device,
        string? diagnostic)
    {
        string? messages = ReadDebugMessages(device);
        if (string.IsNullOrEmpty(messages))
            return diagnostic;
        return string.IsNullOrEmpty(diagnostic)
            ? messages
            : diagnostic + Environment.NewLine + messages;
    }

    private static string? ReadDebugMessages(D3D12Device? device)
    {
        if (device is null)
            return null;
        ID3D12Device10* native = device.Native;
        if (native is null)
            return null;

        ID3D12InfoQueue* queue = null;
        Guid iid = ID3D12InfoQueue.Guid;
        if (native->QueryInterface(&iid, (void**)&queue) < 0 || queue is null)
            return null;

        try
        {
            ulong count = queue->GetNumStoredMessagesAllowedByRetrievalFilter();
            if (count == 0)
                return null;
            ulong first = count > 16 ? count - 16 : 0;
            var result = new StringBuilder();
            for (ulong index = first; index < count; index++)
                AppendDebugMessage(queue, index, result);
            return result.Length == 0 ? null : result.ToString();
        }
        catch
        {
            return null;
        }
        finally
        {
            _ = queue->Release();
        }
    }

    private static void AppendDebugMessage(
        ID3D12InfoQueue* queue,
        ulong index,
        StringBuilder destination)
    {
        nuint byteCount = 0;
        if (queue->GetMessageA(index, null, &byteCount) < 0 || byteCount == 0)
            return;

        void* storage = NativeMemory.Alloc(byteCount);
        try
        {
            if (queue->GetMessageA(index, (Message*)storage, &byteCount) < 0)
                return;
            Message* message = (Message*)storage;
            int descriptionLength = GetDescriptionLength(message);
            string? description = descriptionLength == 0
                ? null
                : Marshal.PtrToStringAnsi(
                    (nint)message->PDescription,
                    descriptionLength);
            if (string.IsNullOrWhiteSpace(description))
                return;
            if (destination.Length != 0)
                destination.AppendLine();
            destination.Append("D3D12 ")
                .Append(message->Severity)
                .Append(" [")
                .Append(message->ID)
                .Append("]: ")
                .Append(description.TrimEnd());
        }
        finally
        {
            NativeMemory.Free(storage);
        }
    }

    private static int GetDescriptionLength(Message* message)
    {
        nuint length = message->DescriptionByteLength;
        if (length == 0 || message->PDescription is null)
            return 0;
        if (message->PDescription[length - 1] == 0)
            length--;
        return checked((int)length);
    }
}
