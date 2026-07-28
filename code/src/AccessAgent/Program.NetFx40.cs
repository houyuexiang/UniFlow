// AccessAgent - .NET Framework 4.0 版本（Windows 7+）
// 编译：csc Program.cs /r:System.Data.dll
// 或使用 Visual Studio 打开 AccessAgent.NetFx40.csproj

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AccessAgent;

class Program
{
    static void Main()
    {
        var config = LoadConfig();
        Console.WriteLine($"AccessAgent (NetFx40) starting on http://{config.BindIp}:{config.Port}");
        Console.WriteLine($"Database: {config.MdbFilePath}");

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{config.BindIp}:{config.Port}/");
        listener.Start();

        while (true)
        {
            try
            {
                var ctx = listener.GetContext();
                HandleRequest(ctx, config);
            }
            catch (HttpListenerException) { break; }
            catch (Exception ex) { Console.Error.WriteLine("Error: " + ex.Message); }
        }
    }

    static Config LoadConfig()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) return new Config();
        return JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) ?? new Config();
    }

    static void HandleRequest(HttpListenerContext ctx, Config config)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.ContentType = "application/json; charset=utf-8";

        try
        {
            var path = req.Url.AbsolutePath.Trim('/');
            var parts = path.Split('/');

            if (req.HttpMethod == "GET" && path == "health")
            {
                WriteJson(res, new { status = "ok", db = File.Exists(config.MdbFilePath) });
            }
            else if (req.HttpMethod == "GET" && parts.Length >= 2 && parts[0] == "records")
            {
                HandleGetRecords(res, req, config, parts[1]);
            }
            else if (req.HttpMethod == "POST" && parts.Length >= 1 && parts[0] == "update")
            {
                HandleUpdate(res, req, config);
            }
            else
            {
                res.StatusCode = 404;
                WriteJson(res, new { error = "Not Found" });
            }
        }
        catch (Exception ex)
        {
            res.StatusCode = 500;
            WriteJson(res, new { error = ex.Message });
        }
        finally
        {
            res.Close();
        }
    }

    static void HandleGetRecords(HttpListenerResponse res, HttpListenerRequest req, Config config, string table)
    {
        if (!File.Exists(config.MdbFilePath))
        {
            res.StatusCode = 400;
            WriteJson(res, new { error = "Database not found" });
            return;
        }

        var where = req.QueryString["where"] ?? "";
        var sql = "SELECT * FROM [" + table + "]";
        if (!string.IsNullOrEmpty(where)) sql += " WHERE " + where;

        var dt = new DataTable();
        using var conn = new OleDbConnection("Provider=Microsoft.ACE.OLEDB.12.0;Data Source=" + config.MdbFilePath);
        using var cmd = new OleDbCommand(sql, conn);
        conn.Open();
        using var adapter = new OleDbDataAdapter(cmd);
        adapter.Fill(dt);

        var rows = new List<Dictionary<string, object>>();
        foreach (DataRow row in dt.Rows)
        {
            var dict = new Dictionary<string, object>();
            foreach (DataColumn col in dt.Columns)
                dict[col.ColumnName] = row[col] == DBNull.Value ? null : row[col];
            rows.Add(dict);
        }

        WriteJson(res, new { table, count = rows.Count, rows });
    }

    static void HandleUpdate(HttpListenerResponse res, HttpListenerRequest req, Config config)
    {
        if (!File.Exists(config.MdbFilePath))
        {
            res.StatusCode = 400;
            WriteJson(res, new { error = "Database not found" });
            return;
        }

        using var reader = new StreamReader(req.InputStream);
        var body = reader.ReadToEnd();
        var request = JsonSerializer.Deserialize<UpdateRequest>(body);
        if (request == null || string.IsNullOrEmpty(request.Sql))
        {
            res.StatusCode = 400;
            WriteJson(res, new { error = "Invalid request" });
            return;
        }

        using var conn = new OleDbConnection("Provider=Microsoft.ACE.OLEDB.12.0;Data Source=" + config.MdbFilePath);
        using var cmd = new OleDbCommand(request.Sql, conn);
        conn.Open();

        if (request.Parameters != null)
        {
            foreach (var p in request.Parameters)
                cmd.Parameters.AddWithValue("@" + p.Key, p.Value ?? DBNull.Value);
        }

        var affected = cmd.ExecuteNonQuery();
        WriteJson(res, new { affected });
    }

    static void WriteJson(HttpListenerResponse res, object data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        var buffer = Encoding.UTF8.GetBytes(json);
        res.ContentLength64 = buffer.Length;
        res.OutputStream.Write(buffer, 0, buffer.Length);
    }
}

class Config
{
    public string BindIp { get; set; } = "localhost";
    public int Port { get; set; } = 5101;
    public string MdbFilePath { get; set; } = "";
}

class UpdateRequest
{
    public string? Sql { get; set; }
    public Dictionary<string, object?>? Parameters { get; set; }
}