using Shouldly;
using Microsoft.Extensions.Logging.Abstractions;
using UniFlow.SrmExport.Models;
using UniFlow.SrmExport.Services;

namespace UniFlow.Tests.SrmExport.Services;

public class ExportFileServiceTests : IDisposable
{
    private readonly string _baseDir;
    private readonly ExportFileService _service;

    public ExportFileServiceTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _service = new ExportFileService(NullLogger<ExportFileService>.Instance, _baseDir);
    }

    [Fact]
    public async Task ExportAsync_CreatesFileWithCorrectContent()
    {
        var samples = new List<DisposedSample>
        {
            new() { Barcode = "SAMPLE001", Location = "&3-09-000001", UpdateTime = "20260726" },
            new() { Barcode = "SAMPLE002", Location = "&3-09-000002", UpdateTime = "20260726" }
        };

        var path = await _service.ExportAsync(samples);

        File.Exists(path).ShouldBeTrue();
        var content = await File.ReadAllTextAsync(path);
        content.ShouldContain("SAMPLE001");
        content.ShouldContain("SAMPLE002");
        content.ShouldContain("Total: 2");
    }

    [Fact]
    public async Task ExportAsync_FileNameContainsTimestampAndCount()
    {
        var samples = new List<DisposedSample>
        {
            new() { Barcode = "T1", Location = "L1", UpdateTime = "T" }
        };

        var path = await _service.ExportAsync(samples);

        var file = Path.GetFileName(path);
        file.ShouldMatch(@"^Dispose_\d{14}_1\.txt$");
    }

    [Fact]
    public async Task ExportAsync_EmptyList_CreatesFileWithZeroCount()
    {
        var path = await _service.ExportAsync(new List<DisposedSample>());

        var content = await File.ReadAllTextAsync(path);
        content.ShouldContain("Total: 0");
    }

    [Fact]
    public async Task ExportAsync_HeaderAndFooterFormatting()
    {
        var samples = new List<DisposedSample>
        {
            new() { Barcode = "B1", Location = "L1", UpdateTime = "T1" }
        };

        var path = await _service.ExportAsync(samples);
        var content = await File.ReadAllTextAsync(path);

        content.ShouldContain("Export Time:");
        content.ShouldContain("Barcode");
        content.ShouldContain("Location");
        content.ShouldContain("UpdateTime");
    }

    [Fact]
    public async Task CleanOld_RemovesFilesOlderThanRetention()
    {
        var disposeDir = Path.Combine(_baseDir, "DisposeFile");
        Directory.CreateDirectory(disposeDir);
        var oldFile = Path.Combine(disposeDir, "Dispose_20200101000000_1.txt");
        await File.WriteAllTextAsync(oldFile, "old data");
        File.SetLastWriteTime(oldFile, DateTime.Now.AddDays(-10));

        _service.CleanOld(5);

        File.Exists(oldFile).ShouldBeFalse();
    }

    [Fact]
    public async Task CleanOld_KeepsRecentFiles()
    {
        var disposeDir = Path.Combine(_baseDir, "DisposeFile");
        Directory.CreateDirectory(disposeDir);
        var recentFile = Path.Combine(disposeDir, "Dispose_20260101000000_1.txt");
        await File.WriteAllTextAsync(recentFile, "recent data");

        _service.CleanOld(5);

        File.Exists(recentFile).ShouldBeTrue();
    }

    [Fact]
    public void CleanOld_NoDirectory_DoesNotThrow()
    {
        Should.NotThrow(() => _service.CleanOld(5));
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, true); }
        catch { /* ignore */ }
    }
}
