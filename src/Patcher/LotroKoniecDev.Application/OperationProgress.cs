namespace LotroKoniecDev.Application;

public sealed record OperationProgress(int Current, int Total, string? Message = null)
{
    public bool IsCompleted => Current >= Total;
}
