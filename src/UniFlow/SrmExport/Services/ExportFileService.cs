using System.Text;
using Microsoft.Extensions.Logging;

namespace UniFlow.SrmExport.Services;

public class ExportFileService : IExportFileService
{
    private readonly ILogger<ExportFileService> _logger;
    private readonly string _baseDir;

    public ExportFileService(ILogger<ExportFileService> logger, string? baseDir = null)
    {
        _logger = logger;
        _baseDir = baseDir ?? AppDomain.CurrentDomain.BaseDirectory;
    }

    public async Task<string> ExportAsync(List<Models.DisposedSample> samples)
    {
        var dir = Path.Combine(_baseDir, "DisposeFile");
        Directory.CreateDirectory(dir);
        var ts = DateTime.Now.ToString("yyyyMMddHHmmss");
        var path = Path.Combine(dir, $"Dispose_{ts}_{samples.Count}.txt");

        var sb = new StringBuilder();
        sb.AppendLine($"Export Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Total: {samples.Count}");
        sb.AppendLine(new string('-', 80));
        sb.AppendLine($"{"Barcode",-30} {"Location",-30} {"UpdateTime",-20}");
        sb.AppendLine(new string('-', 80));
        foreach (var s in samples)
            sb.AppendLine($"{s.Barcode,-30} {s.Location,-30} {s.UpdateTime,-20}");
        sb.AppendLine(new string('-', 80));

        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);
        _logger.LogInformation("Exported {Count} samples to {Path}", samples.Count, path);
        return path;
    }

    public void CleanOld(int retentionDays)
    {
        var dir = Path.Combine(_baseDir, "DisposeFile");
        if (!Directory.Exists(dir)) return;
        var cutoff = DateTime.Now.AddDays(-retentionDays);
        foreach (var f in Directory.GetFiles(dir, "Dispose_*.txt"))
        {
            var fi = new FileInfo(f);
            if (fi.LastWriteTime < cutoff)
            {
                fi.Delete();
                _logger.LogInformation("Deleted old: {File}", f);
            }
        }
    }
}
