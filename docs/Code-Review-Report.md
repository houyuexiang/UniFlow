# UniFlow Code Review Report

**Project**: UniFlow (.NET 10 Worker Service for Laboratory Automation)  
**Date**: 2026-07-27  
**Reviewer**: OpenCode AI  
**Scope**: All `.cs` files and `appsettings.json` in `src/UniFlow/`

---

## Summary

| Category | Count | Severity |
|---|---|---|
| **Critical** | 11 | Bugs that could cause crashes, data loss, or security breaches |
| **Warnings** | 14 | Potential issues needing attention |
| **Suggestions** | 16 | Improvements, not critical but recommended |
| **Config-Only entries** | 5 | Keys in config not consumed by any code |
| **Total** | 46 | |

---

## Config-Code Mapping

### Keys CONSUMED by code

| Config Key | Code Location | Status |
|---|---|---|
| `Logging:UniFlowFile:FileLoggingEnabled` | `Program.cs:22` | ✅ |
| `Logging:UniFlowFile:LogDirectory` | `Program.cs:23` | ✅ |
| `Logging:UniFlowFile:MaxFileSizeMb` | `Program.cs:24` | ✅ |
| `Logging:UniFlowFile:RetentionDays` | `Program.cs:25` | ✅ |
| `Logging:UniFlowFile:LogLevel` | `Program.cs:26` | ✅ |
| `Features:AptioAutoProcess` | `Program.cs:51` | ✅ |
| `Features:SrmExportEnabled` | `Program.cs:60` | ✅ |
| `Features:ImmuliteWorkOrderClean` | `Program.cs:63` | ✅ |
| `Features:DmsAutoOrder` | `Program.cs:86` | ✅ |
| `Aptio:Ip` | `DisposeSampleWorker.cs:46` | ✅ |
| `Aptio:Port` | `DisposeSampleWorker.cs:47` | ✅ |
| `Aptio:SrmNodeIds` | `SrmExportWorker.cs:67`, `DisposeSampleWorker.cs:228` | ✅ |
| `Aptio:Database:Host` | `DisposeDatabaseService.cs:22`, `ExportDatabaseService.cs:20` | ✅ |
| `Aptio:Database:Port` | Same as above | ✅ |
| `Aptio:Database:User` | Same as above | ✅ |
| `Aptio:Database:Password` | Same as above | ✅ |
| `Aptio:Database:Database` | Same as above | ✅ |
| `Centralink:Ip` | `Program.cs:36` (registered via `Configure<T>`, but never injected) | ⚠️ Registered only |
| `Centralink:Port` | Same as above | ⚠️ Registered only |
| `Centralink:Driver` | Same as above | ⚠️ Registered only |
| `AptioAutoProcess:Dispose:CommandType` | `DisposeSampleWorker.cs:241` | ✅ |
| `AptioAutoProcess:Dispose:CommandName` | `DisposeSampleWorker.cs:242` | ✅ |
| `AptioAutoProcess:Dispose:DiscardRunDate` | `DisposeSampleWorker.cs:49` | ✅ |
| `AptioAutoProcess:Dispose:DiscardTimeRange` | `DisposeSampleWorker.cs:50` | ✅ |
| `AptioAutoProcess:Dispose:LoopIntervalSeconds` | `DisposeSampleWorker.cs:63,74,81` | ✅ |
| `AptioAutoProcess:Dispose:MaxWaitDiscardCount` | `DisposeSampleWorker.cs:338` | ✅ |
| `AptioAutoProcess:Dispose:MaxOnetimeSelectDiscardCount` | `DisposeSampleWorker.cs:172,189,206,242` | ✅ |
| `AptioAutoProcess:Dispose:AllowSrmErrorCode` | `DisposeSampleWorker.cs:52-54` | ✅ |
| `AptioAutoProcess:Dispose:EnableTestNameDispose` | `DisposeSampleWorker.cs:166` | ✅ |
| `AptioAutoProcess:Dispose:DisposeTestName` | `DisposeSampleWorker.cs:166,206` | ✅ |
| `AptioAutoProcess:Deliver:Enabled` | `DisposeSampleWorker.cs:160` | ✅ |
| `AptioAutoProcess:Deliver:TestName` | `DisposeSampleWorker.cs:172` | ✅ |
| `AptioAutoProcess:Deliver:DeliveryListFilePath` | **NOT FOUND** | ❌ Config-only |
| `AptioAutoProcess:Priority:Enabled` | `DisposeSampleWorker.cs:163` | ✅ |
| `AptioAutoProcess:Priority:TestName` | `DisposeSampleWorker.cs:189` | ✅ |
| `AptioAutoProcess:Export:ExportTime` | `SrmExportWorker.cs:27,40,42` | ✅ |
| `AptioAutoProcess:Export:LoopIntervalSeconds` | `SrmExportWorker.cs:54` | ✅ |
| `AptioAutoProcess:Export:LogRetentionDays` | `SrmExportWorker.cs:80` | ✅ |
| `WorkListCleaners[*]:Enabled` | `Program.cs:71` | ✅ |
| `WorkListCleaners[*]:InstrumentName` | `ImmuliteCleaner.cs:13` | ✅ |
| `WorkListCleaners[*]:MdbFilePath` | `ImmuliteCleaner.cs:50,78,83,96,120` | ✅ |
| `WorkListCleaners[*]:CentralinkIp` | `ImmuliteCleaner.cs:38` | ✅ |
| `WorkListCleaners[*]:CentralinkPort` | `ImmuliteCleaner.cs:38` | ✅ |
| `WorkListCleaners[*]:CentralinkDriver` | `ImmuliteCleaner.cs:39` | ✅ |
| `WorkListCleaners[*]:EnableCentralink` | `ImmuliteCleaner.cs:36` | ✅ |
| `WorkListCleaners[*]:LoopIntervalSeconds` | **NOT FOUND** (hardcoded to 60 in `WorkListCleanerWorker.cs:17`) | ❌ Config-only |
| `WorkListCleaners[*]:TimeSpanMinutes` | **NOT FOUND** | ❌ Config-only |
| `WorkListCleaners[*]:Prefix` | `ImmuliteCleaner.cs:56,75-77,80-82` | ✅ |
| `WorkListCleaners[*]:NaPrefix` | `ImmuliteCleaner.cs:101,116` | ✅ |
| `WorkListCleaners[*]:SampleIdLenMin` | `ImmuliteCleaner.cs:59` | ✅ |
| `WorkListCleaners[*]:SampleIdLenMax` | `ImmuliteCleaner.cs:59` | ✅ |
| `WorkListCleaners[*]:CheckSampleIdLen` | `ImmuliteCleaner.cs:58` | ✅ |
| `WorkListCleaners[*]:NoSampleLasFlag` | `ImmuliteCleaner.cs:39` | ✅ |
| `WorkListCleaners[*]:NaResultLasFlag` | `ImmuliteCleaner.cs:39` | ✅ |
| `WorkListCleaners[*]:NaResultSqlWhere` | `ImmuliteCleaner.cs:93` | ✅ |
| `WebAdmin:Enabled` | `Program.cs:98,101,115` | ✅ |
| `WebAdmin:BindIp` | `Program.cs:110` | ✅ |
| `WebAdmin:Port` | `Program.cs:110` | ✅ |
| `WebAdmin:ErrorRetentionDays` | `Program.cs:104` | ✅ |
| `WebAdmin:HealthCheckIntervalSeconds` | `HealthCheckWorker.cs:20,33` | ✅ |
| `DMS:Enabled` | `Program.cs:86` | ✅ |
| `DMS:DbHost` | `DmsDatabaseService.cs:18` | ✅ |
| `DMS:DbPort` | Same as above | ✅ |
| `DMS:DbUser` | Same as above | ✅ |
| `DMS:DbPassword` | Same as above | ✅ |
| `DMS:DbName` | Same as above | ✅ |
| `DMS:LoopIntervalSeconds` | `DmsAutoOrderWorker.cs:40` | ✅ |
| `DMS:EnablePitStopMonitor` | `PitStopMonitorService.cs:24` | ✅ |
| `DMS:PitStopTimeoutMinutes` | `PitStopMonitorService.cs:51` | ✅ |
| `DMS:AutoModifyTestStatus` | `StatusCorrectionService.cs:29,31,43` | ✅ |
| `DMS:IgnoreFlagList` | `StatusCorrectionService.cs:59,70` | ✅ |
| `DMS:TestTriggerSampleDeletion` | `SampleCleanupService.cs:62` | ✅ |
| `DMS:DeleteTableList` | **NOT FOUND** | ❌ Config-only |
| `DMS:SendCancelMessageToAptio` | **NOT FOUND** | ❌ Config-only |
| `DMS:AptioConnectString` | **NOT FOUND** | ❌ Config-only |

### Config-Only Entries (not consumed by code)

1. **`AptioAutoProcess:Deliver:DeliveryListFilePath`** — defined in `ConfigShared.cs:65` as `AptioAutoDeliverConfig.DeliveryListFilePath`, never read.
2. **`WorkListCleaners[*]:LoopIntervalSeconds`** — defined in `CleanerConfig.cs:12`, but `WorkListCleanerWorker.cs:17` hardcodes `_loopInterval = 60`.
3. **`WorkListCleaners[*]:TimeSpanMinutes`** — defined in `CleanerConfig.cs:13`, never read by any code.
4. **`DMS:DeleteTableList`** — defined in `DmsOrderConfig.cs:19`, never read. `SampleCleanupService` dynamically discovers tables instead.
5. **`DMS:SendCancelMessageToAptio`** — defined in `DmsOrderConfig.cs:20`, never read.
6. **`DMS:AptioConnectString`** — defined in `DmsOrderConfig.cs:21`, never read.

---

## Critical Issues

### CRIT-1: SQL Injection in DisposeDatabaseService (multiple methods)

**Files**: `DisposeSample/Services/DisposeDatabaseService.cs`
**Lines**: 65, 163-169, 172-180, 183-191

**Problem**: Four methods use string interpolation (`$"..."`) to embed user-configurable values directly into SQL queries:

- `GetDisposeSamplesAsync` (line 65): `$"SELECT * FROM {cmdName} LIMIT {maxCount}"` — `cmdName` comes from `appsettings.json`
- `GetDeliverRecordsAsync` (line 165-169): `$@"...test LIKE '%{deliverTestName}%'...LIMIT {maxCount}"`
- `GetPriorityRecordsAsync` (line 173-180): Same pattern
- `GetTestNameDisposeRecordsAsync` (line 183-191): Same pattern

**Risk**: `deliverTestName`, `priorityTestName`, `disposeTestName`, and `cmdName` are read from config files. If an attacker gains write access to `appsettings.json` (e.g., via the `/api/config` endpoint), they can inject arbitrary SQL. Even without that, `maxCount` is an integer but still embedded via interpolation.

**Fix**: Use parameterized queries with Dapper for all values. For `cmdName` (table name), validate against a whitelist.

---

### CRIT-2: SQL Injection in ImmuliteCleaner

**Files**: `WorkListCleaner/Services/ImmuliteCleaner.cs`
**Lines**: 75-83, 116-120

**Problem**: SQL UPDATE statements are built using string interpolation:

```csharp
var updateSql = $"update Worklist set Accession_Num = '{_config.Prefix}{u.AccessionNum}' " +
    $"where Unique_Record_ID_Num = {u.UniqueRecordIdNum} and Accession_Num = '{u.AccessionNum}' " +
    $"and Test_Type = '{u.TestType}'";
```

**Risk**: `u.AccessionNum`, `u.UniqueRecordIdNum`, and `u.TestType` come from the Access database (user-controlled data). This is classic SQL injection into the same database.

**Fix**: Use parameterized OleDb queries.

---

### CRIT-3: SQL Injection in StatusCorrectionService

**Files**: `DMSAutoOrder/Services/StatusCorrectionService.cs`
**Lines**: 85-90

**Problem**: Values from database are interpolated into SQL:

```csharp
await _db.ExecuteSqlAsync(
    $"UPDATE {_config.DbName}.reqtestresult SET flgstatus = 'V' " +
    $"WHERE codsid = '{sid}' AND codtest = '{test}'");
```

**Risk**: If `sid` or `test` contain SQL metacharacters, the query is altered.

**Fix**: Use parameterized queries.

---

### CRIT-4: SQL Injection in DmsDatabaseService

**Files**: `DMSAutoOrder/Services/DmsDatabaseService.cs`
**Lines**: 49, 64, 101

**Problem**: Table names and SQL templates are interpolated:

```csharp
var rows = await conn.QueryAsync(
    $"SELECT * FROM {_config.DbName}.{tableName} WHERE flgrunning <> ''");
```

**Risk**: `tableName` comes from `information_schema.tables`, which is less risky but still technically injection-prone. The `string.Format` on line 101 with user-provided SQL templates is high-risk.

**Fix**: Use `MySqlConnector`'s `MySqlCommand` with parameters for values. For table names, validate against a known set.

---

### CRIT-5: SQL Injection in SampleCleanupService

**Files**: `DMSAutoOrder/Services/SampleCleanupService.cs`
**Lines**: 42, 48, 53, 73, 86-90

**Problem**: Table names and column values are interpolated into SQL:

```csharp
templates.Add($"DELETE FROM {_config.DbName}.{t} WHERE codsid = '{{0}}'");
...
var sql = $"SELECT codsid, codoid FROM {_config.DbName}.reqtest " +
          $"WHERE codtest = '{testName}' AND TIMESTAMPDIFF(MINUTE, datrequest, NOW()) > {timeoutMin}";
```

**Risk**: `testName` and `timeoutMin` come from config (`TestTriggerSampleDeletion`), which is user-configurable. `t` comes from `information_schema` (lower risk).

**Fix**: Parameterize `testName` and `timeoutMin`. For table names, validate.

---

### CRIT-6: SrmStatusDecoder.Decode always returns true for first node

**Files**: `Common/Services/SrmStatusDecoder.cs`
**Lines**: 37-53

**Problem**: The `Decode` method **always returns `true`** after processing the first node, regardless of the `isReady` result:

```csharp
public bool Decode(string message, HashSet<string> allowedErrors, out string statusMessage, out bool isReady)
{
    ...
    var nodes = DecodeAll(message);
    foreach (var node in nodes)
    {
        ...
        if (node.Mode == "ON" && (node.Status == "G" || node.Status == "Y") && errOk == "0000")
            isReady = true;
        return true;  // BUG: always returns true after first node
    }
    return false;
}
```

**Impact**: The caller in `DisposeSampleWorker.HandleReadyAsync` (line 288-290) discards the `out` parameters and uses the return value:
```csharp
var isReady = string.IsNullOrEmpty(nodeId)
    ? _statusDecoder.Decode(response, _allowErrors, out _, out _)  // always true if any node exists
    : _statusDecoder.IsNodeReady(response, nodeId, _allowErrors);
```

This means the non-node-specific path **always thinks the system is ready** as long as at least one SRM node is reported. The `isReady` out parameter is never checked.

**Fix**: Change the method to return `isReady` instead of hardcoded `true`, or restructure the logic to properly evaluate all nodes.

---

### CRIT-7: CRIT-6: SrmStatusDecoder.Decode always returns true for first node

**Files**: `Common/Services/SrmStatusDecoder.cs`
**Lines**: 37-53

**Problem**: The `Decode` method **always returns `true`** after processing the first node, regardless of the `isReady` result:

```csharp
public bool Decode(string message, HashSet<string> allowedErrors, out string statusMessage, out bool isReady)
{
    ...
    var nodes = DecodeAll(message);
    foreach (var node in nodes)
    {
        ...
        if (node.Mode == "ON" && (node.Status == "G" || node.Status == "Y") && errOk == "0000")
            isReady = true;
        return true;  // BUG: always returns true after first node
    }
    return false;
}
```

**Impact**: The caller in `DisposeSampleWorker.HandleReadyAsync` (line 288-290) discards the `out` parameters and uses the return value:
```csharp
var isReady = string.IsNullOrEmpty(nodeId)
    ? _statusDecoder.Decode(response, _allowErrors, out _, out _)  // always true if any node exists
    : _statusDecoder.IsNodeReady(response, nodeId, _allowErrors);
```

This means the non-node-specific path **always thinks the system is ready** as long as at least one SRM node is reported. The `isReady` out parameter is never checked.

**Fix**: Change the method to return `isReady` instead of hardcoded `true`, or restructure the logic to properly evaluate all nodes.

---

### CRIT-8: AptioSocketClient.Disconnect uses synchronous Wait() inside async code path

**Files**: `Common/Services/AptioSocketClient.cs`
**Lines**: 86-88

**Problem**: The `Disconnect()` method uses `_lock.Wait()` (synchronous semaphore wait) instead of `await _lock.WaitAsync()`:

```csharp
public void Disconnect()
{
    _lock.Wait();  // Blocking call on async code path
    try { DisconnectInternal(); } finally { _lock.Release(); }
}
```

**Risk**: If `Disconnect()` is called while `SendAndReceiveAsync` or `ConnectAsync` is holding the semaphore (which they do via `await _lock.WaitAsync(ct)`), this will block the calling thread. In an ASP.NET or async worker context, this can cause thread pool starvation and deadlocks.

**Fix**: Either make `Disconnect()` async, or use `Semaphore` instead of `SemaphoreSlim`.

---

### CRIT-9: NullReferenceException risk in DisposeSampleWorker.HandleReadyAsync

**Files**: `DisposeSample/Workers/DisposeSampleWorker.cs`
**Line**: 298

**Problem**: `record.Barcode!` uses the null-forgiving operator, but `record` is checked for null on line 272:

```csharp
var record = await _db.SelectOneSendRecordAsync();
if (record == null) { ... return; }
...
_preBarcode = record.Barcode;
var acked = await _commands.SendDisposeAsync(record.Barcode!, ct);
```

`record.Barcode` is `string?` per `DisposeStatus.Barcode`. If `SelectOneSendRecordAsync` returns a record where `Barcode` is null, `SendDisposeAsync` will receive null, and the downstream `AptioCommandService.SendDisposeAsync` (line 18) will format it as `$"COMMENT S002^{barcode}\\TRASH^S"` — producing a malformed command.

**Fix**: Add a null check or ensure `Barcode` is required in the query.

---

### CRIT-10: DMSAutoOrderWorker does not propagate CancellationToken to sub-services

**Files**: `DMSAutoOrder/Workers/DmsAutoOrderWorker.cs`
**Lines**: 33-35

**Problem**: The cancellation token from `ExecuteAsync(CancellationToken ct)` is not passed to any sub-service:

```csharp
await _pitStop.ExecuteAsync();     // no ct
await _status.ExecuteAsync();      // no ct
await _cleanup.ExecuteAsync();     // no ct
```

**Risk**: When the application shuts down, these operations will continue running. Database operations could be interrupted mid-execution, leading to inconsistent state. The `OperationCanceledException` catch on line 37 will never trigger for these calls.

**Fix**: Pass `ct` to all sub-service methods.

---

### CRIT-11: HealthStore uses synchronous SQLite operations blocking ASP.NET threads

**Files**: `WebAdmin/Services/HealthStore.cs`
**Lines**: 41, 54, 72, 93-104, 123-135, 150-160, 170-173, 183-185

**Problem**: All SQLite operations use synchronous `ExecuteNonQuery()`, `ExecuteReader()`, etc. These are called from ASP.NET Minimal API endpoints via `ApiEndpoints.cs`, which blocks the request thread.

**Impact**: Under load, this degrades throughput and can cause thread pool starvation.

**Fix**: Use the async equivalents (`ExecuteNonQueryAsync`, `ExecuteReaderAsync`, etc.) from `Microsoft.Data.Sqlite`.

---

## Warnings

### WARN-1: Duplicate IsWorkTime logic

**Files**: 
- `DisposeSample/Workers/DisposeSampleWorker.cs:131-154`
- `Common/Services/WorkTimeChecker.cs:7-30`

**Problem**: The `IsWorkTime` method in `DisposeSampleWorker` is an exact duplicate of `WorkTimeChecker.IsWorkTime`. The `WorkTimeChecker` class exists in the Common layer but is not used by the worker it was designed for.

**Fix**: Replace `DisposeSampleWorker.IsWorkTime` with `WorkTimeChecker.IsWorkTime`.

---

### WARN-2: CentralinkConfig registered but never consumed

**Files**: `Program.cs:36`

**Problem**: `builder.Services.Configure<CentralinkConfig>(...)` registers the config section, but the `CentralinkConfig` class is never injected into any service. The `CentralinkService.SendLasFlag` takes `ip`, `port`, `driver` as individual method parameters that come from `CleanerConfig` instead.

**Impact**: The top-level `Centralink` config section is dead code. If it was intended as a global default, it's not being used.

---

### WARN-3: DmsOrderConfig registered as both IOptions and Singleton

**Files**: 
- `Program.cs:37` — `Configure<DmsOrderConfig>(...)` (IOptions pattern)
- `Program.cs:89` — `builder.Services.AddSingleton(dmsCfg)` (Singleton instance)
- `DmsAutoOrderWorker.cs:16` — injects `IOptions<DmsOrderConfig>`
- `DmsDatabaseService.cs:12`, `PitStopMonitorService.cs:15`, `StatusCorrectionService.cs:14`, `SampleCleanupService.cs:15` — inject `DmsOrderConfig` directly

**Problem**: Two different registration mechanisms exist for the same config type. The `IOptions` registration in `Program.cs:37` is redundant because `Program.cs:88-89` already creates a singleton instance. The `DmsAutoOrderWorker` uses `IOptions<DmsOrderConfig>` while all other DMS services inject `DmsOrderConfig` directly — different instances could exist.

---

### WARN-4: SrmExportWorker time window may be missed

**Files**: `SrmExport/Workers/SrmExportWorker.cs:42-49`

**Problem**: The export triggers only when `now.Hour == target.Hour && now.Minute == target.Minute`. If the worker's loop interval (default 60 seconds) skips over that exact minute, the export is missed for the entire day. The reset at midnight also requires exact match on `now.Hour == 0 && now.Minute == 0`.

**Fix**: Use a range-based check: `now.TimeOfDay >= targetTime && !_exportedToday`.

---

### WARN-5: Connection string duplication across 4 files

**Files**:
- `DisposeSample/Services/DisposeDatabaseService.cs:22-24`
- `SrmExport/Services/ExportDatabaseService.cs:20-22`
- `DMSAutoOrder/Services/DmsDatabaseService.cs:18-20`
- `DMSAutoOrder/Services/StatusCorrectionService.cs:73-75`

**Problem**: The same MySQL connection string format is duplicated in 4 places. Any change (e.g., adding `SslMode=Required`) requires updating all copies.

**Fix**: Extract a shared `ConnectionStringFactory` or `DbConnectionFactory` in the Common layer.

---

### WARN-6: ImmuliteCleaner wraps synchronous I/O in Task.Run

**Files**: `WorkListCleaner/Services/ImmuliteCleaner.cs:29`

**Problem**:
```csharp
public async Task CleanAsync(CancellationToken ct)
{
    await Task.Run(() => { ... }, ct);
}
```

All Access DB operations are synchronous (OleDbDataAdapter.Fill, ExecuteNonQuery). Wrapping in `Task.Run` offloads blocking to a thread pool thread, but doesn't make it truly async. This wastes a thread pool thread.

**Fix**: The synchronous pattern is acceptable for legacy Access DB, but consider using `Task.Run` only for the CPU-bound data processing, not for the I/O portions.

---

### WARN-7: AstmConnection uses blocking I/O and polling

**Files**: `Common/Services/ASTM.cs:105-201`

**Problem**: The `AstmConnection` class uses:
- `TcpClient.Connect()` (synchronous, line 122)
- `NetworkStream.Read()` (synchronous, line 136)
- `Thread.Sleep(50)` polling loop (line 155)
- `new Thread(ReceiveLoop)` (line 124)

This is inconsistent with the rest of the codebase which uses `async/await` throughout. The synchronous `Connect()` can block the constructor for seconds.

**Fix**: Consider making `AstmConnection` async or at least use `Task.Run` for the connection.

---

### WARN-8: DisposeDatabaseService caches a single MySqlConnection (not thread-safe)

**Files**: `DisposeSample/Services/DisposeDatabaseService.cs:14,30-32`

**Problem**: A single `MySqlConnection` is cached and reused:
```csharp
private MySqlConnection? _connection;
...
_connection ??= new MySqlConnection(ConnectionString);
if (_connection.State != ConnectionState.Open) await _connection.OpenAsync();
```

This is not thread-safe if `PingAsync` is called from multiple worker iterations concurrently. Contrast with `ExportDatabaseService` which creates a new connection per operation.

**Fix**: Create a new connection per operation (like `ExportDatabaseService`), relying on `MySqlConnector`'s built-in connection pooling.

---

### WARN-9: ConfigService writes to appsettings.json at runtime

**Files**: `WebAdmin/Services/ConfigService.cs:43`

**Problem**: The `/api/config` endpoint allows writing to the live `appsettings.json` file. A malformed write (e.g., partial write, crash during serialization) could corrupt the configuration file, crashing the application on next restart.

**Fix**: Write to a separate override file, or use a database-backed configuration store.

---

### WARN-10: ApiEndpoints expose database passwords via /api/config

**Files**: `WebAdmin/ApiEndpoints.cs:50-54`

**Problem**: The `GET /api/config` endpoint returns the entire `appsettings.json` content, including database passwords under `Aptio:Database:Password` and `DMS:DbPassword`.

**Fix**: Redact sensitive values (e.g., replace with `"***"`) before returning.

---

### WARN-11: PitStopMonitorService dictionary has unbounded memory growth

**Files**: `DMSAutoOrder/Services/PitStopMonitorService.cs:10`

**Problem**: The `_seen` dictionary is never cleaned up for tables that no longer exist in the database. Over weeks/months, if pitstop tables are created and dropped, the dictionary will grow unboundedly.

**Fix**: Remove entries for tables not returned by `GetPitStopTablesAsync()`.

---

### WARN-12: Hardcoded credentials in appsettings.json

**Files**: `appsettings.json:34`

**Problem**: Default credentials are shipped in the config:
```json
"Aptio": { "Database": { "Password": "root" } }
```

**Risk**: If this config is deployed to production without changing the password, the database is exposed.

**Fix**: Use `User Secrets` in development, environment variables or a secrets manager in production.

---

### WARN-13: DisposeSampleWorker exposes all state machine methods as public

**Files**: `DisposeSample/Workers/DisposeSampleWorker.cs:106,158,223,263,313,358`

**Problem**: `TickAsync`, `HandleCommandsAsync`, `HandleNoneAsync`, `HandleReadyAsync`, `HandleAckAsync`, `HandleDisposeAsync` are all `public`. They should be `private` — they are only called from within the class.

**Fix**: Change to `private`.

---

### WARN-14: WorkListCleanerWorker hardcodes loop interval

**Files**: `WorkListCleaner/Workers/WorkListCleanerWorker.cs:17`

**Problem**: `_loopInterval = 60` is hardcoded. The `CleanerConfig.LoopIntervalSeconds` is defined in the config model but never used by the worker.

**Fix**: Read from config (e.g., first cleaner's interval, or add a global setting).

---

## Suggestions

### SUG-1: Use parameterized SQL across all data access layers

**Files**: Multiple (see CRIT-1 through CRIT-5)

**Suggestion**: The project uses Dapper and raw ADO.NET. Dapper's `QueryAsync<T>(sql, new { Param = value })` pattern is used in some places but not consistently. Refactor all string-interpolated SQL to use parameterized queries. This is the single most impactful security fix.

---

### SUG-2: Extract shared connection string builder

**Files**: `DisposeSample/Services/DisposeDatabaseService.cs`, `SrmExport/Services/ExportDatabaseService.cs`, `DMSAutoOrder/Services/DmsDatabaseService.cs`, `DMSAutoOrder/Services/StatusCorrectionService.cs`

**Suggestion**: Create a `MySqlConnectionFactory` in the `Common` layer:
```csharp
public class MySqlConnectionFactory
{
    private readonly string _connectionString;
    public MySqlConnection CreateConnection() => new(_connectionString);
}
```

Register per-database with named options.

---

### SUG-3: Use records for read-only models

**Files**: `SrmExport/Models/ExportModels.cs`, `DisposeSample/Models/DisposeRecords.cs`, `WorkListCleaner/Models/WorkListRecord.cs`

**Suggestion**: These are DTOs without behavior. Consider using `record` types for immutability and value equality:
```csharp
public record DisposedSample(string? Barcode, string? Location, string? UpdateTime);
```

---

### SUG-4: Add structured logging with context

**Files**: `Common/Services/Logging/UniFlowFileLogger.cs`

**Suggestion**: The custom logger writes plain text strings. Consider using structured logging (e.g., Serilog or the built-in `BeginScope`) to include correlation IDs, module names, etc., for better log analysis.

---

### SUG-5: Add retry policy for Aptio socket connection

**Files**: `DisposeSample/Workers/DisposeSampleWorker.cs:88-104`

**Suggestion**: The current retry logic only logs a warning every 6 attempts. Consider adding exponential backoff and a maximum retry limit to prevent indefinite retry loops.

---

### SUG-6: Make HealthStore operations async

**Files**: `WebAdmin/Services/HealthStore.cs`

**Suggestion**: `Microsoft.Data.Sqlite` has async methods. Use `ExecuteNonQueryAsync()`, `ExecuteReaderAsync()`, etc. to avoid blocking ASP.NET request threads.

---

### SUG-7: Use enum for StatusCorrectionService mode

**Files**: `DMSAutoOrder/Services/StatusCorrectionService.cs`, `DMSAutoOrder/Models/DmsOrderConfig.cs`

**Suggestion**: `AutoModifyTestStatus` is compared as strings `"1"`, `"2"`, `"3"`. Use an enum:
```csharp
public enum AutoModifyMode { Disabled, Mode1, Mode2, Mode3 }
```

---

### SUG-8: Remove unused using directives

**Files**:
- `SrmExport/Services/IExportFileService.cs:1` — imports `Microsoft.Extensions.Logging` but doesn't use it.

**Suggestion**: Clean up unused imports.

---

### SUG-9: Inconsistent nullability patterns

**Files**: Various

**Suggestion**: Some models use `string?` (e.g., `DisposedSample.Barcode`), others use `string = ""` (e.g., `CleanerConfig.InstrumentName`). Pick one convention and apply consistently. With `<Nullable>enable</Nullable>`, `string?` is more explicit about the absence of a value.

---

### SUG-10: SrmExportWorker should use TimeOnly comparison

**Files**: `SrmExport/Workers/SrmExportWorker.cs:42-49`

**Suggestion**: Use `TimeOnly` for cleaner time-of-day comparisons:
```csharp
var nowTime = TimeOnly.FromDateTime(DateTime.Now);
if (!_exportedToday && nowTime >= target && nowTime < target.AddMinutes(1))
```

---

### SUG-11: Add health check for Aptio connection

**Files**: `WebAdmin/HealthCheckWorker.cs:26`

**Suggestion**: The `HealthCheckWorker` always records "healthy" for "System". Consider adding actual health checks for the Aptio socket connection and database connectivity, then reporting the real status.

---

### SUG-12: Rename SysStatus.Ack to SysStatus.Acknowledged

**Files**: `DisposeSample/Models/DisposeRecords.cs:3-9`

**Suggestion**: Avoid abbreviations in enum member names for clarity.

---

### SUG-13: Use constants for field names in DisposeDatabaseService

**Files**: `DisposeSample/Services/DisposeDatabaseService.cs:113-118`

**Suggestion**: The string values `"send"`, `"disposed"`, `"checkcount"` used in a switch statement should be `private const` fields or a static dictionary.

---

### SUG-14: DisposeSampleWorker should use WorkTimeChecker instead of duplicating logic

**Files**: `DisposeSample/Workers/DisposeSampleWorker.cs:131-154`

**Suggestion**: Replace the duplicated `IsWorkTime` method with `WorkTimeChecker.IsWorkTime`, which already exists in the `Common` layer.

---

### SUG-15: Add cancellation token support to DMS sub-services

**Files**: `DMSAutoOrder/Services/PitStopMonitorService.cs`, `DMSAutoOrder/Services/StatusCorrectionService.cs`, `DMSAutoOrder/Services/SampleCleanupService.cs`

**Suggestion**: Accept `CancellationToken` in `ExecuteAsync()` methods and pass it to database operations.

---

### SUG-16: Add XML documentation for public API

**Files**: Various

**Suggestion**: Public interfaces and classes (e.g., `IAptioSocketClient`, `IWorkListCleaner`, `IDisposeDatabaseService`) would benefit from XML doc comments explaining their contract, especially since this is a laboratory automation system where correctness is critical.

---

## Config-Only Entry Details

| # | Config Key | Defined In | Expected Behavior | Current Status |
|---|---|---|---|---|
| 1 | `AptioAutoProcess:Deliver:DeliveryListFilePath` | `ConfigShared.cs:65` | Should specify a file path for delivery list | Never read by any code |
| 2 | `WorkListCleaners[*]:LoopIntervalSeconds` | `CleanerConfig.cs:12` | Should control worker loop interval | Hardcoded to 60 in `WorkListCleanerWorker.cs:17` |
| 3 | `WorkListCleaners[*]:TimeSpanMinutes` | `CleanerConfig.cs:13` | Unknown purpose | Never read by any code |
| 4 | `DMS:DeleteTableList` | `DmsOrderConfig.cs:19` | Should specify tables for deletion | Never read; `SampleCleanupService` uses dynamic discovery |
| 5 | `DMS:SendCancelMessageToAptio` | `DmsOrderConfig.cs:20` | Should enable cancel message sending | Never read |
| 6 | `DMS:AptioConnectString` | `DmsOrderConfig.cs:21` | Should provide Aptio connection string | Never read |

---

## Files Reviewed

| File | Lines | Status |
|---|---|---|
| `Program.cs` | 125 | Reviewed |
| `Common/Models/ConfigShared.cs` | 88 | Reviewed |
| `Common/Services/WorkTimeChecker.cs` | 59 | Reviewed |
| `Common/Services/AptioSocketClient.cs` | 92 | Reviewed |
| `Common/Services/IAptioSocketClient.cs` | 11 | Reviewed |
| `Common/Services/ASTM.cs` | 202 | Reviewed |
| `Common/Services/SrmStatusDecoder.cs` | 66 | Reviewed |
| `Common/Services/Logging/UniFlowFileLogger.cs` | 208 | Reviewed |
| `Common/Services/Logging/UniFlowLoggerConfig.cs` | 10 | Reviewed |
| `DisposeSample/Models/DisposeRecords.cs` | 35 | Reviewed |
| `DisposeSample/Services/IDisposeDatabaseService.cs` | 21 | Reviewed |
| `DisposeSample/Services/DisposeDatabaseService.cs` | 209 | Reviewed |
| `DisposeSample/Services/AptioCommandService.cs` | 52 | Reviewed |
| `DisposeSample/Workers/DisposeSampleWorker.cs` | 367 | Reviewed |
| `SrmExport/Models/ExportModels.cs` | 8 | Reviewed |
| `SrmExport/Services/IExportDatabaseService.cs` | 9 | Reviewed |
| `SrmExport/Services/ExportDatabaseService.cs` | 55 | Reviewed |
| `SrmExport/Services/IExportFileService.cs` | 9 | Reviewed |
| `SrmExport/Services/ExportFileService.cs` | 54 | Reviewed |
| `SrmExport/Workers/SrmExportWorker.cs` | 85 | Reviewed |
| `WorkListCleaner/Models/CleanerConfig.cs` | 22 | Reviewed |
| `WorkListCleaner/Models/WorkListRecord.cs` | 10 | Reviewed |
| `WorkListCleaner/Services/IWorkListCleaner.cs` | 7 | Reviewed |
| `WorkListCleaner/Services/AccessDatabaseService.cs` | 51 | Reviewed |
| `WorkListCleaner/Services/CentralinkService.cs` | 69 | Reviewed |
| `WorkListCleaner/Services/ImmuliteCleaner.cs` | 126 | Reviewed |
| `WorkListCleaner/Workers/WorkListCleanerWorker.cs` | 58 | Reviewed |
| `DMSAutoOrder/Models/DmsOrderConfig.cs` | 22 | Reviewed |
| `DMSAutoOrder/Services/DmsDatabaseService.cs` | 105 | Reviewed |
| `DMSAutoOrder/Services/PitStopMonitorService.cs` | 74 | Reviewed |
| `DMSAutoOrder/Services/StatusCorrectionService.cs` | 94 | Reviewed |
| `DMSAutoOrder/Services/SampleCleanupService.cs` | 101 | Reviewed |
| `DMSAutoOrder/Workers/DmsAutoOrderWorker.cs` | 43 | Reviewed |
| `WebAdmin/ApiEndpoints.cs` | 97 | Reviewed |
| `WebAdmin/HealthCheckWorker.cs` | 36 | Reviewed |
| `WebAdmin/Services/ConfigService.cs` | 74 | Reviewed |
| `WebAdmin/Services/HealthStore.cs` | 189 | Reviewed |
| `appsettings.json` | 135 | Reviewed |
| `appsettings.Development.json` | 8 | Reviewed |
| **Total** | **~3,270** | |