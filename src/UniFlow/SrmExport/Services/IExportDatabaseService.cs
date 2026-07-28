using UniFlow.SrmExport.Models;

namespace UniFlow.SrmExport.Services;

public interface IExportDatabaseService
{
    Task<bool> PingAsync();
    Task<List<DisposedSample>> GetDisposedSamplesAsync(string nodeId);
}
