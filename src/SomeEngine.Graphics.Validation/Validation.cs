namespace SomeEngine.Graphics.Validation;

public enum ValidationMessageType : byte
{
    Information,
    Warning,
    Error,
}

public readonly record struct ValidationMessage(
    ValidationMessageType Type,
    string Area,
    string Text,
    string? Label = null);

public interface IValidationMessageSink
{
    void Report(in ValidationMessage message);
}

public sealed class DelegateValidationMessageSink : IValidationMessageSink
{
    private readonly Action<ValidationMessage> _report;

    public DelegateValidationMessageSink(Action<ValidationMessage> report)
    {
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    public void Report(in ValidationMessage message) => _report(message);
}

public readonly record struct ValidationOptions(
    IValidationMessageSink? MessageSink = null,
    bool ReportLiveObjectsOnDispose = true);
