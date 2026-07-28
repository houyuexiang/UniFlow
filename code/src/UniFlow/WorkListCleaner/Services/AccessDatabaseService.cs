using System.Data;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using UniFlow.WorkListCleaner.Models;

namespace UniFlow.WorkListCleaner.Services;

public class AccessDatabaseService
{
    private readonly ILogger<AccessDatabaseService> _logger;

    public AccessDatabaseService(ILogger<AccessDatabaseService> logger)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("WorkListCleaner requires MS Access (OleDb), which is only available on Windows.");
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

    public int UpdateRecord(string connectionString, string sql, params (string name, object value)[] parameters)
    {
        if (string.IsNullOrEmpty(connectionString)) return 0;
        try
        {
            using var conn = new System.Data.OleDb.OleDbConnection(connectionString);
            using var cmd = new System.Data.OleDb.OleDbCommand(sql, conn);
            for (int i = 0; i < parameters.Length; i++)
                cmd.Parameters.AddWithValue($"@p{i}", parameters[i].value ?? DBNull.Value);
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
