using System.Data;
using Microsoft.Extensions.Logging;
using UniFlow.WorkListCleaner.Models;

namespace UniFlow.WorkListCleaner.Services;

public class AccessDatabaseService
{
    private readonly ILogger<AccessDatabaseService> _logger;

    public AccessDatabaseService(ILogger<AccessDatabaseService> logger)
    {
        _logger = logger;
    }

    public DataTable GetDataTable(string connectionString, string sql)
    {
        var dt = new DataTable();
        if (string.IsNullOrEmpty(connectionString)) return dt;
        try
        {
            using var conn = new System.Data.OleDb.OleDbConnection(connectionString);
            using var cmd = new System.Data.OleDb.OleDbCommand(sql, conn);
            conn.Open();
            using var adapter = new System.Data.OleDb.OleDbDataAdapter(cmd);
            adapter.Fill(dt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Access query failed: {Message}", ex.Message);
        }
        return dt;
    }

    public int UpdateRecord(string connectionString, string sql)
    {
        if (string.IsNullOrEmpty(connectionString)) return 0;
        try
        {
            using var conn = new System.Data.OleDb.OleDbConnection(connectionString);
            using var cmd = new System.Data.OleDb.OleDbCommand(sql, conn);
            conn.Open();
            return cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Access update failed: {Message}", ex.Message);
            return 0;
        }
    }
}
