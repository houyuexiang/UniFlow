namespace UniFlow.SrmExport.Models;

public class SrmExportConfig
{
    public string SrmNodeId { get; set; } = "04";
    public string ExportTime { get; set; } = "08:30";
    public int LoopIntervalSeconds { get; set; } = 60;
    public int LogRetentionDays { get; set; } = 30;
}

public class DisposedSample
{
    public string? Barcode { get; set; }
    public string? Location { get; set; }
    public string? UpdateTime { get; set; }
}
