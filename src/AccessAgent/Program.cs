using System.Net;
using System.Text.Json;
using System.Runtime.InteropServices;

if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    Console.Error.WriteLine("AccessAgent requires Windows (OleDb)");
    return 1;
}

var config = LoadConfig();
Console.WriteLine($"AccessAgent starting on http://{config.BindIp}:{config.Port}");
Console.WriteLine($"Database: {config.MdbFilePath}");

var listener = new HttpListener();
listener.Prefixes.Add($"http://{config.BindIp}:{config.Port}/");
listener.Start();

while (true)
{
    try
    {
        var ctx = await listener.GetContextAsync();
        _ = HandleRequestAsync(ctx, config);
    }
    catch (HttpListenerException) { break; }
    catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); }
}

return 0;

static Config LoadConfig()
{
    var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
    if (!File.Exists(path)) return new Config();
    var json = File.ReadAllText(path);
    return JsonSerializer.Deserialize<Config>(json) ?? new Config();
}

static async Task HandleRequestAsync(HttpListenerContext ctx, Config config)
{
    var req = ctx.Request;
    var res = ctx.Response;
    res.ContentType = "application/json; charset=utf-8";

    try
    {
        var path = req.Url?.AbsolutePath?.Trim('/') ?? "";
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        switch (req.HttpMethod)
        {
            case "GET" when parts.Length == 1 && parts[0] == "health":
                await WriteJson(res, new { status = "ok", db = File.Exists(config.MdbFilePath) });
                break;

            case "GET" when parts.Length >= 2 && parts[0] == "records":
                await HandleGetRecords(res, req, config, parts[1]);
                break;

            case "POST" when parts.Length >= 2 && parts[0] == "update":
                await HandleUpdate(res, req, config);
                break;

            default:
                res.StatusCode = 404;
                await WriteJson(res, new { error = "Not Found" });
                break;
        }
    }
    catch (Exception ex)
    {
        res.StatusCode = 500;
        await WriteJson(res, new { error = ex.Message });
    }
    finally
    {
        res.Close();
    }
}

static async Task HandleGetRecords(HttpListenerResponse res, HttpListenerRequest req, Config config, string table)
{
    if (!File.Exists(config.MdbFilePath))
    {
        res.StatusCode = 400;
        await WriteJson(res, new { error = "Database not found" });
        return;
    }

    var where = req.QueryString["where"] ?? "";
    var sql = $"SELECT * FROM [{table}]";
    if (!string.IsNullOrEmpty(where)) sql += $" WHERE {where}";

    var dt = new System.Data.DataTable();
    using var conn = new System.Data.OleDb.OleDbConnection($"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={config.MdbFilePath}");
    using var cmd = new System.Data.OleDb.OleDbCommand(sql, conn);
    conn.Open();
    using var adapter = new System.Data.OleDb.OleDbDataAdapter(cmd);
    adapter.Fill(dt);

    var rows = new List<Dictionary<string, object?>>();
    foreach (System.Data.DataRow row in dt.Rows)
    {
        var dict = new Dictionary<string, object?>();
        foreach (System.Data.DataColumn col in dt.Columns)
            dict[col.ColumnName] = row[col] == DBNull.Value ? null : row[col];
        rows.Add(dict);
    }

    await WriteJson(res, new { table, count = rows.Count, rows });
}

static async Task HandleUpdate(HttpListenerResponse res, HttpListenerRequest req, Config config)
{
    if (!File.Exists(config.MdbFilePath))
    {
        res.StatusCode = 400;
        await WriteJson(res, new { error = "Database not found" });
        return;
    }

    using var reader = new System.IO.StreamReader(req.InputStream);
    var body = await reader.ReadToEndAsync();
    var request = JsonSerializer.Deserialize<UpdateRequest>(body);
    if (request == null || string.IsNullOrEmpty(request.Sql))
    {
        res.StatusCode = 400;
        await WriteJson(res, new { error = "Invalid request" });
        return;
    }

    using var conn = new System.Data.OleDb.OleDbConnection($"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={config.MdbFilePath}");
    using var cmd = new System.Data.OleDb.OleDbCommand(request.Sql, conn);
    conn.Open();

    if (request.Parameters != null)
    {
        foreach (var p in request.Parameters)
            cmd.Parameters.AddWithValue("@" + p.Key, p.Value ?? DBNull.Value);
    }

    var affected = await cmd.ExecuteNonQueryAsync();
    await WriteJson(res, new { affected });
}

static Task WriteJson(HttpListenerResponse res, object data)
{
    var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    var buffer = System.Text.Encoding.UTF8.GetBytes(json);
    res.ContentLength64 = buffer.Length;
    return res.OutputStream.WriteAsync(buffer, 0, buffer.Length);
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