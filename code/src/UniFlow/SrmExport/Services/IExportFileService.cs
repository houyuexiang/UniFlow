using Microsoft.Extensions.Logging;

namespace UniFlow.SrmExport.Services;

public interface IExportFileService
{
    Task<string> ExportAsync(List<Models.DisposedSample> samples);
    void CleanOld(int retentionDays);
}
