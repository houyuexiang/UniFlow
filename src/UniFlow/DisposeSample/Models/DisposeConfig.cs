namespace UniFlow.DisposeSample.Models;

public class DisposeConfig
{
    public string SrmNodeId { get; set; } = "09";
    public int CommandType { get; set; } = 0;
    public string CommandName { get; set; } = "view_overtimestoragesample";
    public string DiscardRunDate { get; set; } = "1,2,3,4,5,6,7";
    public string DiscardTimeRange { get; set; } = "01:30-05:45,3800;";
    public int LoopIntervalSeconds { get; set; } = 3;
    public int MaxWaitDiscardCount { get; set; } = 2;
    public int MaxOnetimeSelectDiscardCount { get; set; } = 8;
    public string AllowSrmErrorCode { get; set; } = "0000,0F0A,0E59,AAAA";
    public int LogRetentionDays { get; set; } = 30;

    public bool EnableDeliver { get; set; }
    public string DeliverTestName { get; set; } = "";
    public bool EnableUpdatePriority { get; set; }
    public string PriorityTestName { get; set; } = "";
    public string DeliveryListFilePath { get; set; } = "";
}
