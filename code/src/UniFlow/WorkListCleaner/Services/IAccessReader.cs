namespace UniFlow.WorkListCleaner.Services;

public interface IAccessReader
{
    System.Data.DataTable GetDataTable(string sql);
    int UpdateRecord(string sql, params (string name, object value)[] parameters);
}