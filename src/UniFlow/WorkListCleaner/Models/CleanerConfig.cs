namespace UniFlow.WorkListCleaner.Models;

public class CleanerConfig
{
    public bool Enabled { get; set; }
    public string MdbFilePath { get; set; } = "";
    public string CentralinkIp { get; set; } = "";
    public int CentralinkPort { get; set; }
    public string CentralinkDriver { get; set; } = "AUTO";
    public bool EnableCentralink { get; set; }
    public int LoopIntervalSeconds { get; set; } = 60;
    public double TimeSpanMinutes { get; set; } = 5;
    public string Prefix { get; set; } = "ERR";
    public string NaPrefix { get; set; } = "NA";
    public int SampleIdLenMin { get; set; }
    public int SampleIdLenMax { get; set; }
    public bool CheckSampleIdLen { get; set; }
    public string NoSampleLasFlag { get; set; } = "NS";
    public string NaResultLasFlag { get; set; } = "NA";
    public string NaResultSqlWhere { get; set; } = "";
}
