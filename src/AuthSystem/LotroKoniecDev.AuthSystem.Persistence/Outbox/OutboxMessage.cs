using LotroKoniecDev.SharedKernel.Guards;

namespace LotroKoniecDev.AuthSystem.Persistence.Outbox;

public sealed class OutboxMessage
{
    public const int TypeMaxLength = 500;
    public const int LastErrorMaxLength = 2000;

    public Guid Id { get; }
    public string Type { get; }
    public string Payload { get; }
    public DateTimeOffset OccurredOn { get; }
    public DateTimeOffset? ProcessedOn { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }

    public bool IsProcessed()
    {
        bool isProcessed = ProcessedOn is not null;
        return isProcessed;
    }
    
    public void MarkAsProcessed(DateTimeOffset processedOn)
    {
        if (IsProcessed())
        {
            return;
        }

        ProcessedOn = processedOn;
    }

    public void MarkFailed(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        error = error.Trim();
        if (error.Length > LastErrorMaxLength)
        {
            error = error[..LastErrorMaxLength];
        }

        Attempts++;
        LastError = error;
    }

    public static OutboxMessage Create(
        string type,
        string payload,
        DateTimeOffset occurredOn)
    {
        Ensure.NotEmpty(occurredOn);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(type.Length, TypeMaxLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        Guid id = Guid.CreateVersion7();
        OutboxMessage instance = new(id: id, type: type, payload: payload, occurredOn: occurredOn);
        return instance;
    }

    private OutboxMessage(
        Guid id,
        string type,
        string payload,
        DateTimeOffset occurredOn)
    {
        Id = id;
        Type = type;
        Payload = payload;
        OccurredOn = occurredOn;
    }

    private OutboxMessage()
    {
        Type = null!;
        Payload = "";
    }
}
