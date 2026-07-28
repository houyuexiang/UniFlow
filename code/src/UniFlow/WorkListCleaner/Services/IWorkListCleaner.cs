namespace UniFlow.WorkListCleaner.Services;

public interface IWorkListCleaner
{
    string Name { get; }
    Task CleanAsync(CancellationToken ct);
}
