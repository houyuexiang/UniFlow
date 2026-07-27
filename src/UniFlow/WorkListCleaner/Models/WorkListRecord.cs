namespace UniFlow.WorkListCleaner.Models;

public class WorkListRecord
{
    public string UniqueRecordIdNum { get; set; } = "";
    public string AccessionNum { get; set; } = "";
    public string TestType { get; set; } = "";
    public string DilutionFactor { get; set; } = "";
    public string Cps { get; set; } = "";
}
