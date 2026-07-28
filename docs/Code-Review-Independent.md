# UniFlow Independent Code Review

**Date:** 2026-07-28
**Scope:** All `.cs` files and `appsettings.json` in `/src/UniFlow/`
**Target Framework:** .NET 10 / Worker Service + WebApplication

---

## Executive Summary

| Severity | Count | Description |
|----------|-------|-------------|
| **Critical** | 8 | Compile-blocker (missing class), SQL injection, hardcoded credentials, path traversal, missing auth |
| **Warning** | 10 | Race conditions, thread safety, memory leaks, blocking I/O, incorrect logic |
| **Suggestion** | 6 | Code quality, maintainability, unused features |
| **Config-only** | 3 | Config keys not consumed by code |

---

## Critical Findings

### C1. Missing `AptioCommandService` class (project will not compile)

**Files:**
- `Program.cs:49` — registers the service
- `DisposeSample/Workers/DisposeSampleWorker.cs:14,152,173,183,207,266,305,366` — calls methods on it

```csharp
// Program.cs:49
builder.Services.AddSingleton<AptioCommandService>();
```

```csharp
// DisposeSampleWorker.cs:152
var ok = await _commands.SendDeliverAsync(r.Barcode!, ct);
// DisposeSampleWorker.cs:172
var ok = await _commands.SendStatPriorityAsync(r.Barcode!, ct);
// DisposeSampleWorker.cs:183
var ok = await _commands.SendDisposeAsync(r.Barcode!, ct);
// DisposeSampleWorker.cs:207
var ok = await _commands.SendDeliverAsync(sid, ct);
// DisposeSampleWorker.cs:266
var response = await _commands.SendStatusRequestAsync(ct);
// DisposeSampleWorker.cs:305
var acked = await _commands.SendDisposeAsync(_preBarcode, ct);
// DisposeSampleWorker.cs:366
var resp = await _commands.SendStatusRequestAsync(ct);
```

The class `AptioCommandService` is referenced in the `.DisposeSample.Services` namespace import (`Program.cs:4`) and registered in DI but **does not exist anywhere in the codebase**. This is a compile error that blocks the entire build. The methods it is expected to expose are:
- `SendDisposeAsync(string barcode, CancellationToken ct)`
- `SendStatusRequestAsync(CancellationToken ct)`
- `SendDeliverAsync(string barcode, CancellationToken ct)`
- `SendStatPriorityAsync(string barcode, CancellationToken ct)`

---

### C2. SQL injection in `ImmuliteCleaner.NaResultProcess`

**File:** `WorkListCleaner/Services/ImmuliteCleaner.cs:90-92`

```csharp
var where = string.IsNullOrEmpty(_config.NaResultSqlWhere) ? "" : $" where {_config.NaResultSqlWhere}";
var sql = $"SELECT Unique_Record_ID_Num, Accession_Num, Dilution_Factor, Errors, Test_Type, Result, CPS " +
          $"FROM [Result Information]{where}";
```

The `NaResultSqlWhere` configuration value comes from `appsettings.json:85,103` and is **concatenated directly into the SQL string** with no sanitization or parameterization. Anyone who can modify the config file or hit the unprotected `PUT /api/config` endpoint can inject arbitrary SQL into the Access database.

**Recommendation:** Use OleDb parameters for the WHERE clause, or strongly validate that `NaResultSqlWhere` only contains expected column/value patterns before injection.

---

### C3. SQL injection via unsafe table name interpolation in `DmsDatabaseService`

**File:** `DMSAutoOrder/Services/DmsDatabaseService.cs:43,58,64-68,101`

Multiple methods interpolate `_config.DbName` and `tableName` directly into SQL strings:

```csharp
// line 43 — tableName from DB query, unsanitized
var rows = await conn.QueryAsync(
    $"SELECT * FROM {_config.DbName}.{tableName} WHERE flgrunning <> ''");

// line 58
await conn.ExecuteAsync($"DELETE FROM {_config.DbName}.{tableName} WHERE flgrunning <> ''");

// lines 64-68
var sql = $"SELECT r.codsid, r.codoid FROM {_config.DbName}.reqtube r " +
          $"WHERE r.codoid IN (SELECT o.codoid FROM {_config.DbName}.orders o, {_config.DbName}.anagpatient a " +
          $"WHERE o.codaid = a.codaid AND a.codpid = 'UNKNOWN') " +
          $"AND r.codsid IN (SELECT t.codsid FROM {_config.DbName}.reqtest t WHERE t.codtest <> 'UNKN') " +
          $"OR r.codoid NOT IN (SELECT o.codoid FROM {_config.DbName}.orders o)";
```

While `_config.DbName` comes from the config file and `tableName` comes from `information_schema` queries, this pattern is dangerous. If an attacker can create a table in the DMS database with a malicious name (e.g., `pitstop%'; DROP TABLE ...;--`), it would execute. MySQL table names can contain backticks and other special characters.

**Recommendation:** Wrap all table/database identifiers in backticks: ``$"SELECT * FROM `{_config.DbName}`.`{tableName}`"``. Better yet, validate against known-safe table names.

---

### C4. SQL injection via delete templates in `SampleCleanupService`

**File:** `DMSAutoOrder/Services/SampleCleanupService.cs:69-83,101`

```csharp
// lines 69-70
foreach (var t in tables)
    templates.Add($"DELETE FROM {_config.DbName}.{t} WHERE codsid = '{{0}}'");

// lines 75-76
if (!templates.Any(s => s.Contains(t)))
    templates.Add($"DELETE FROM {_config.DbName}.{t} WHERE codoid = '{{1}}'");
```

The `t` variable comes from `information_schema.columns` — table names from a database that may be externally accessible. These templates then have user data (`sid`, `oid`) injected via `string.Format` in `DeleteSampleAsync`:

```csharp
// DmsDatabaseService.cs:101
var sql = string.Format(template, sid, oid);
await conn.ExecuteAsync(sql);
```

**Recommendation:** Use Dapper parameterized queries instead of `string.Format`. Never interpolate unvalidated identifiers into SQL.

---

### C5. Hardcoded database credentials in configuration

**File:** `appsettings.json:33-34`

```json
"Aptio": {
    "Database": {
        "Host": "127.0.0.1",
        "Port": 3306,
        "User": "root",
        "Password": "root",
        "Database": "flexlab"
    }
}
```

Plaintext `root/root` credentials stored in the config file. This is the MySQL root account.

**Recommendation:** Use environment variables, .NET User Secrets, or a secrets manager. Never commit credentials to version control.

---

### C6. WebAdmin config update endpoint has no authentication

**File:** `WebAdmin/ApiEndpoints.cs:69-73`

```csharp
api.MapPut("/config", (ConfigUpdateRequest req) =>
{
    var ok = config.Update(req.Path, req.Value);
    return ok ? Results.Ok(new { success = true }) : Results.Problem("Update failed");
});
```

The `PUT /api/config` endpoint allows arbitrary modification of `appsettings.json` with no authentication, authorization, or CORS restrictions. Combined with SQL injectable config values (C2), this is a severe vulnerability.

**Recommendation:** Add authentication middleware, or remove this endpoint entirely (runtime config modification of a worker service is unusual).

---

### C7. Path traversal in log viewer endpoint

**File:** `WebAdmin/ApiEndpoints.cs:96-106`

```csharp
api.MapGet("/logs/view", (string file, int? tail) =>
{
    var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
    var path = Path.Combine(logDir, Path.GetFileName(file));
    if (!File.Exists(path)) return Results.NotFound();
    var lines = File.ReadAllLines(path).ToList();
    ...
});
```

`Path.GetFileName(file)` strips directory separators but does **not** prevent traversal via:
- NTFS alternate data streams (e.g., `file.txt:stream`)
- Un-normalised Unicode paths
- `..` with trailing dots on some platforms
- The underlying `File.ReadAllLines` follows the resolved path regardless

**Recommendation:** Resolve the combined path and verify it starts with the resolved `logDir` path:

```csharp
var fullPath = Path.GetFullPath(Path.Combine(logDir, Path.GetFileName(file)));
if (!fullPath.StartsWith(Path.GetFullPath(logDir) + Path.DirectorySeparatorChar))
    return Results.Forbid();
```

---

### C8. `HealthStore` shares a single `SqliteConnection` across all async operations

**File:** `WebAdmin/Services/HealthStore.cs:15-16,44-183`

```csharp
public HealthStore(string dbPath, ILogger<HealthStore> logger, int retentionDays = 30)
{
    _conn = new SqliteConnection($"Data Source={dbPath}");
    _conn.Open();
    InitDatabase();
}
```

The same `SqliteConnection` instance (`_conn`) is reused in **every** async method (`RecordHealthAsync`, `RecordErrorAsync`, `GetHealthHistoryAsync`, `GetErrorsAsync`, `GetLatestModuleStatusAsync`, `CleanupOldAsync`). SQLite is not thread-safe in this mode by default. Multiple concurrent HTTP requests to the health API will cause `SQLiteException: database is locked` or data corruption.

**Recommendation:** Create and dispose a new `SqliteConnection` per operation, or use `Microsoft.Data.Sqlite` with connection pooling (`new SqliteConnection(...)` each time — the pool handles reuse).

---

## Warnings

### W1. Race condition in `AptioSocketClient.Disconnect` force path

**File:** `Common/Services/AptioSocketClient.cs:87-94`

```csharp
public void Disconnect()
{
    if (!_lock.Wait(TimeSpan.FromSeconds(5)))
    {
        _logger.LogWarning("Disconnect: could not acquire lock within 5s, force disconnecting");
        DisconnectInternal();
        return;
    }
    try { DisconnectInternal(); } finally { _lock.Release(); }
}
```

When the lock times out, `DisconnectInternal()` is called while another thread (in `SendAndReceiveAsync`) may still hold the lock and be reading from `_reader`/`_writer`. This means `_reader?.Dispose()` runs concurrently with `_reader.ReadLineAsync()`, causing an `ObjectDisposedException` that is silently caught by the I/O error handler.

**Recommendation:** Use a cooperative cancellation mechanism (e.g., `CancellationTokenSource`) to cancel outstanding I/O before disposing.

---

### W2. `AstmConnection` uses synchronous I/O on a background thread

**File:** `Common/Services/ASTM.cs:117-126,128-143,163,168`

```csharp
// Constructor — sync connect on caller thread
_tcp.Connect(host, port);

// Background thread — sync read
var len = _stream.Read(buffer, 0, buffer.Length);

// Send — sync write
_stream.Write(data, 0, data.Length);
_stream.WriteByte((byte)c);
```

- `Connect(host, port)` blocks the calling thread (from `CentralinkService` which runs in a `BackgroundService` loop).
- `_stream.Read()` on the background thread hangs until data arrives — there's no read timeout.
- `_stream.Write()` is synchronous.
- `ReadFrame` busy-waits with `Thread.Sleep(50)` instead of using async patterns.

**Recommendation:** Replace with `TcpClient.ConnectAsync`, `NetworkStream.ReadAsync`/`WriteAsync`, and async receive patterns, or use `AptioSocketClient` to centralize socket logic.

---

### W3. `ImmuliteCleaner.CleanAsync` wraps everything in `Task.Run`

**File:** `WorkListCleaner/Services/ImmuliteCleaner.cs:29`

```csharp
public async Task CleanAsync(CancellationToken ct)
{
    await Task.Run(() =>
    {
        var updated = new List<WorkListRecord>();
        ...
    }, ct);
}
```

The entire method body is wrapped in `Task.Run` even though the underlying calls (`GetDataTable`, `UpdateRecord`, `SendLasFlag`) are synchronous. This offloads work to the thread pool unnecessarily and blocks a pool thread with OleDb I/O. The caller (`WorkListCleanerWorker`) already expects an async method.

**Recommendation:** If the underlying Access/OleDb calls must remain sync, mark the method as synchronous or use `async` for the ASTP/LIS calls separately. `Task.Run` is a code smell here.

---

### W4. Log file rotation template `{0}` never substituted

**File:** `Common/Services/Logging/UniFlowFileLogger.cs:117,142,154-158`

```csharp
// line 117 — called with template like "DisposeSample_{0}"
Rotate(state, $"{module}_{{0}}");

// line 142
OpenFile(state, BuildPath(nameTemplate, today));

// lines 154-158
private string BuildPath(string template, string date)
{
    var name = $"{template}_{date}.log";  // produces: DisposeSample_{0}_2026-07-28.log
    return Path.Combine(_baseDir, name);
}
```

The `{0}` placeholder in the template is never formatted with `state.FileIndex`. The file index is incremented but the filename always resolves to the same value. This means only **one file per module per day** is created — the rotation by size will overwrite the same file instead of creating `_0`, `_1`, etc.

**Recommendation:** Fix: `var name = string.Format(template, state.FileIndex) + $"_{date}.log";`

---

### W5. `PitStopMonitorService._seen` dictionary has unbounded growth

**File:** `DMSAutoOrder/Services/PitStopMonitorService.cs:10,32-33,47-48,68-72`

```csharp
private readonly Dictionary<string, Dictionary<string, DateTime>> _seen = new();
```

The `_seen` dictionary accumulates entries for every pitstop table and record ID seen. It is only cleaned when:
- A table no longer exists in the DB (line 32-33).
- An ID is no longer in the current batch (lines 68-72).

If pitstop table names and record IDs are continuously generated (e.g., with timestamps in names), the dictionary grows without bound. This is a potential memory leak.

**Recommendation:** Add a periodic cleanup (e.g., remove entries older than `PitStopTimeoutMinutes * 2`), or use a bounded `ConcurrentDictionary` with LRU eviction.

---

### W6. `Environment.TickCount` wrap-around timeout bug

**File:** `Common/Services/ASTM.cs:148`

```csharp
public string? ReadFrame(int timeoutMs = 5000)
{
    var start = Environment.TickCount;
    while (Environment.TickCount - start < timeoutMs)
    ...
}
```

`Environment.TickCount` is a 32-bit signed integer that wraps from `Int32.MaxValue` to `Int32.MinValue` approximately every 24.9 days. When the wrap occurs, `TickCount - start` produces an arithmetic overflow (default behavior is unchecked in C#, so it wraps negatively), breaking the timeout logic. The method will either return `null` immediately or loop forever.

**Recommendation:** Use `Environment.TickCount64` or `Stopwatch.GetTimestamp()` for timeout calculations.

---

### W7. `AccessDatabaseService.UpdateRecord` parameterized overload ignores parameter names

**File:** `WorkListCleaner/Services/AccessDatabaseService.cs:58-61`

```csharp
foreach (var (_, value) in parameters)
    cmd.Parameters.AddWithValue("@p", value ?? DBNull.Value);
```

All parameters are added with the same name `@p`, so each call to `AddWithValue("@p", ...)` **overwrites** the previous parameter. Only the last parameter value is actually sent to the database. The `name` from the tuple `(string name, object value)` is discarded (note the `_` discard for `name`).

The caller (`ImmuliteCleaner.cs:71-75`) passes 4 parameters expecting them all to be bound:

```csharp
_accessDb.UpdateRecord(_config.MdbFilePath, updateSql,
    ("", newVal), ("", id), ("", sid), ("", test));
```

But only `test` (the last one) is bound. The `newVal`, `id`, and `sid` values are lost. **This means the UPDATE queries in `ImmuliteCleaner` produce incorrect results — they match on the wrong values.**

**Recommendation:** Fix the parameter naming to use unique ordinal names or the caller-provided names:

```csharp
for (int i = 0; i < parameters.Length; i++)
    cmd.Parameters.AddWithValue($"@p{i}", parameters[i].value ?? DBNull.Value);
```
And update the SQL in the caller to use `@p0`, `@p1`, `@p2`, `@p3` in the correct positions (OleDb uses positional parameters, so order matters).

---

### W8. `SampleCleanupService._deleteSqlTemplates` null-forgiveness without guard

**File:** `DMSAutoOrder/Services/SampleCleanupService.cs:113`

```csharp
await _db.DeleteSampleAsync(sid, oid ?? "", _deleteSqlTemplates!);
```

The `!` null suppression operator assumes `InitDeleteSqlAsync()` has successfully populated `_deleteSqlTemplates`. But if `InitDeleteSqlAsync()` threw an exception (caught by the try/catch in `ExecuteAsync` at line 38-41), `_deleteSqlTemplates` remains `null`. On the next loop iteration, this line will throw a `NullReferenceException` that is caught as a generic error but may corrupt state silently.

**Recommendation:** Either make `_deleteSqlTemplates` non-nullable with a lazy-initialized pattern, or add an explicit null check before use.

---

### W9. `HealthCheckWorker` always reports "healthy" — no actual health check

**File:** `WebAdmin/HealthCheckWorker.cs:26-28`

```csharp
await _health.RecordHealthAsync("System", "healthy", "All modules running");
```

This writes a hardcoded "healthy" status every interval regardless of whether any module is actually running. The `DisposeSampleWorker`, `SrmExportWorker`, and other services are not monitored. The `/api/health` endpoint always returns "healthy" even if all workers crashed.

**Recommendation:** Inject the worker services or a shared health reporter and actually check their status (e.g., `BackgroundService.IsRunning` or a heartbeat pattern).

---

### W10. `SrmExportWorker` uses tight 1-minute polling with minute-granularity check

**File:** `SrmExport/Workers/SrmExportWorker.cs:40-49`

```csharp
var target = ParseTimeOnly(_ec.ExportTime);

if (!_exportedToday && now.Hour == target.Hour && now.Minute == target.Minute)
{
    await RunExportAsync(ct);
    _exportedToday = true;
}

if (now.Hour == 0 && now.Minute == 0)
    _exportedToday = false;
```

The export can be missed if the tick doesn't land exactly on the target minute (e.g., due to an in-flight operation blocking). The midnight reset also requires a tick exactly at 00:00. On busy systems, this can cause missed or duplicate exports.

**Recommendation:** Use a time-window approach: `if (!_exportedToday && now.TimeOfDay >= target.ToTimeSpan() && now.TimeOfDay < target.ToTimeSpan().Add(TimeSpan.FromSeconds(_ec.LoopIntervalSeconds)))`.

---

## Suggestions

### S1. Duplicate `DisposeDatabaseService` / `ExportDatabaseService` pattern

**Files:**
- `DisposeSample/Services/DisposeDatabaseService.cs` (212 lines)
- `SrmExport/Services/ExportDatabaseService.cs` (53 lines)

Both services:
- Depend on `MySqlConnectionFactory` via constructor injection
- Have identical `PingAsync()` method bodies
- Have identical `NewConnection()` methods
- Query the same `flexlab` database

**Recommendation:** Extract a common base class or shared `AptioDatabaseService` that both can use, using a single `MySqlConnectionFactory` instance instead of creating two factories (`Program.cs:43,51`).

---

### S2. `UniFlowLoggerConfig.FileLoggingEnabled` is never checked

**File:** `Common/Services/Logging/UniFlowLoggerConfig.cs:5`

```csharp
public bool FileLoggingEnabled { get; set; } = true;
```

This property is set from config (`Program.cs:22`) but **never read anywhere**. The `UniFlowFileLoggerProvider` always initializes and starts writing logs regardless. This means setting `"FileLoggingEnabled": false` has no effect — file logging still runs.

**Recommendation:** Check `config.FileLoggingEnabled` in `UniFlowFileLoggerProvider` constructor and skip file writing if `false`.

---

### S3. `CentralinkService.SendLasFlag` passes empty `instrumentId`

**File:** `WorkListCleaner/Services/ImmuliteCleaner.cs:39-41`

```csharp
_centralink.SendLasFlag(updated, _config.CentralinkIp, _config.CentralinkPort,
    _config.CentralinkDriver, _config.NoSampleLasFlag, _config.NaResultLasFlag, "");
```

The last argument (`instrumentId`) is always `""`. In `CentralinkService.SendLasFlag` (line 29), this empty string is prepended to the message: `sbMsg.Append("").Append(';').Append(r.TestType)`. This results in `";TestName"` — a leading semicolon that may not parse correctly on the Centralink side.

**Recommendation:** Either add an `InstrumentId` field to `CleanerConfig` and populate it, or remove the empty `instrumentId` prefix.

---

### S4. Duplicate time parsing in `SrmExportWorker` vs `WorkTimeChecker`

**File:** `SrmExport/Workers/SrmExportWorker.cs:83-84`

```csharp
private static TimeOnly ParseTimeOnly(string time) =>
    TimeOnly.TryParse(time, out var r) ? r : new TimeOnly(8, 30);
```

**File:** `Common/Services/WorkTimeChecker.cs:52-53`

```csharp
if (TimeOnly.TryParse(tp[0], out var st) &&
    TimeOnly.TryParse(tp[1], out var et) && ...)
```

Two different `TimeOnly` parsing patterns exist. `SrmExportWorker` has a fallback to `08:30`; `WorkTimeChecker` silently skips unparseable entries.

**Recommendation:** Centralize time parsing in a utility method with consistent error handling.

---

### S5. Unused `using` imports

**Files:**
- `SrmExport/Services/IExportFileService.cs:1` — `using Microsoft.Extensions.Logging;` (unused)
- `WebAdmin/ApiEndpoints.cs:1` — `using UniFlow.Common.Models;` (unused)

**Recommendation:** Remove unused imports.

---

### S6. `DisposeSampleWorker.ProcessTestNameDisposeAsync` is one-line method body

**File:** `DisposeSample/Workers/DisposeSampleWorker.cs:182`

```csharp
private async Task ProcessTestNameDisposeAsync(CancellationToken ct) { var records = await _db.GetTestNameDisposeRecordsAsync(_dc.DisposeTestName, _dc.MaxOnetimeSelectDiscardCount); foreach (var r in records) { if (ct.IsCancellationRequested) break; if (!_socket.Connected) break; var ok = await _commands.SendDisposeAsync(r.Barcode!, ct); if (ok) { await _db.UpdateStatusAsync(r.Barcode!, "send"); _logger.LogInformation("Test-name disposed {Barcode}", r.Barcode); } await Task.Delay(500, ct); } }
```

This 450+ character single-line method is unreadable and unmaintainable. It belongs in a code obfuscation contest, not a production codebase. It also **exactly duplicates** the logic of `ProcessDeliverAsync` (lines 148-163), differing only in the DB call and log message.

**Recommendation:** Break into multiple lines and extract common logic into a helper method.

---

## Config-Only Entries

These keys exist in `appsettings.json` but are not consumed by any code:

### CO1. `AptioAutoProcess.Dispose.DisposeTestName` (when `EnableTestNameDispose` is false)

**File:** `appsettings.json:48` → `Common/Models/ConfigShared.cs:50`

The value `""` is set but when `EnableTestNameDispose` is `false` (the default), the `HandleCommandsAsync` method in `DisposeSampleWorker.cs:140` skips this branch entirely. This is expected behavior but worth noting — the empty string default is fine but if someone populates it without enabling the feature, no warning is emitted.

### CO2. `AptioAutoProcess.Deliver` sub-section (when `Enabled` is false)

**File:** `appsettings.json:52-56`

The `TestName`, `DeliveryListFilePath` fields are not consumed when `Enabled` is `false`. Same for `AptioAutoProcess.Priority` at lines 57-60. This is by design via feature flags but no validation warns users if they set values for a disabled feature.

### CO3. `DMS.EnablePitStopMonitor`, `DMS.AutoModifyTestStatus`, etc. (when `DMS.Enabled` is false)

**File:** `appsettings.json:114-129`

All DMS settings are ignored when `Features.DmsAutoOrder` is `false` (`Program.cs:93`). No validation warns about this.

---

## Appendix A: File Inventory

| File | Lines | Module |
|------|-------|--------|
| `Program.cs` | 132 | Root |
| `appsettings.json` | 130 | Config |
| `Common/Models/ConfigShared.cs` | 82 | Common |
| `Common/Services/AptioSocketClient.cs` | 97 | Common |
| `Common/Services/ASTM.cs` | 202 | Common |
| `Common/Services/MySqlConnectionFactory.cs` | 23 | Common |
| `Common/Services/SrmStatusDecoder.cs` | 65 | Common |
| `Common/Services/WorkTimeChecker.cs` | 59 | Common |
| `Common/Services/Logging/UniFlowFileLogger.cs` | 208 | Common |
| `Common/Services/Logging/UniFlowLoggerConfig.cs` | 10 | Common |
| `DisposeSample/Services/DisposeDatabaseService.cs` | 212 | Dispose |
| `DisposeSample/Services/IDisposeDatabaseService.cs` | 21 | Dispose |
| `DisposeSample/Workers/DisposeSampleWorker.cs` | 373 | Dispose |
| `DisposeSample/Models/DisposeRecords.cs` | 35 | Dispose |
| `SrmExport/Services/ExportDatabaseService.cs` | 53 | Export |
| `SrmExport/Services/ExportFileService.cs` | 54 | Export |
| `SrmExport/Workers/SrmExportWorker.cs` | 85 | Export |
| `WorkListCleaner/Services/ImmuliteCleaner.cs` | 122 | Cleaner |
| `WorkListCleaner/Services/AccessDatabaseService.cs` | 70 | Cleaner |
| `WorkListCleaner/Services/CentralinkService.cs` | 69 | Cleaner |
| `WorkListCleaner/Workers/WorkListCleanerWorker.cs` | 60 | Cleaner |
| `DMSAutoOrder/Services/DmsDatabaseService.cs` | 105 | DMS |
| `DMSAutoOrder/Services/SampleCleanupService.cs` | 125 | DMS |
| `DMSAutoOrder/Services/StatusCorrectionService.cs` | 92 | DMS |
| `DMSAutoOrder/Services/PitStopMonitorService.cs` | 80 | DMS |
| `DMSAutoOrder/Workers/DmsAutoOrderWorker.cs` | 43 | DMS |
| `WebAdmin/ApiEndpoints.cs` | 110 | Web |
| `WebAdmin/HealthCheckWorker.cs` | 36 | Web |
| `WebAdmin/Services/HealthStore.cs` | 189 | Web |
| `WebAdmin/Services/ConfigService.cs` | 74 | Web |

**Total:** 30 files, ~2,925 lines of source code (excluding generated/model-only files).

---

## Appendix B: Pull Request Checklist

Before merging, ensure the following:

- [ ] **C1**: Implement `AptioCommandService` or remove its registration
- [ ] **C2**: Parameterize `NaResultSqlWhere` usage in SQL
- [ ] **C3,C4**: Apply identifier sanitization (backtick-wrapping) for all table/db name interpolations
- [ ] **C5**: Remove hardcoded credentials; use environment variables or secrets manager
- [ ] **C6**: Add authentication to `PUT /api/config` or remove the endpoint
- [ ] **C7**: Fix path traversal in `/logs/view` endpoint
- [ ] **C8**: Fix SQLite connection thread safety (create new connections per operation)
- [ ] **W7**: Fix `UpdateRecord` parameter naming (currently overwriting all params as `@p`)
- [ ] **W4**: Fix log rotation template `{0}` not being substituted
- [ ] **W1**: Add cooperative cancellation to `AptioSocketClient.Disconnect`
- [ ] **S2**: Wire up `FileLoggingEnabled` flag check
