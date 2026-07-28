namespace UniFlow.DisposeSample.Models;

public enum SysStatus
{
    None,
    Ready,
    Ack,
    Dispose
}

public class DisposeStatus
{
    public string? Barcode { get; set; }
    public string? Patient { get; set; }
    public string? Stype { get; set; }
    public string? Location { get; set; }
    public string? UpdateTime { get; set; }
    public string? Res1 { get; set; }
    public string? Res2 { get; set; }
    public string? Rack { get; set; }
    public int Send { get; set; }
    public int Disposed { get; set; }
    public int Checkcount { get; set; }
}

public class SampleRecord
{
    public string? Barcode { get; set; }
    public string? Patient { get; set; }
    public string? Stype { get; set; }
    public string? Location { get; set; }
    public string? UpdateTime { get; set; }
    public string? Res1 { get; set; }
    public string? Res2 { get; set; }
}
