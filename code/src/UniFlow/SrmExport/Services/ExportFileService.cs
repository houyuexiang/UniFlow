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
        // 与原始版 SRM_ExportDisposeSample.exe 的导出格式保持一致：
        //   目录：exe 同目录下 DisposeFile 文件夹（原始程序启动时自动创建）
        //   文件名：Dispose_YYYYMMDDHHMMSS_DisposeCounts.txt（见随附 Doc.docx 使用说明）
        //   内容：首行 Tab 分隔表头「Barcode\tDispose time」，每行「条码\t14位处置时间」
        var dir = Path.Combine(_baseDir, "DisposeFile");
        Directory.CreateDirectory(dir);
        var ts = DateTime.Now.ToString("yyyyMMddHHmmss");
        var path = Path.Combine(dir, $"Dispose_{ts}_{samples.Count}.txt");

        var sb = new StringBuilder();
        // 原始版（Delphi/Windows）产物为 CRLF 行尾，固定写出以保证跨平台一致
        sb.Append("Barcode\tDispose time\r\n");
        foreach (var s in samples)
            sb.Append($"{s.Barcode}\t{s.UpdateTime}\r\n");

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
