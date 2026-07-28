# UniFlow Code Review Report v2

**Project**: UniFlow (.NET 10 Worker Service for Laboratory Automation)
**Date**: 2026-07-28
**Reviewer**: OpenCode AI
**Scope**: Verification of previously reported issues and identification of new issues

---

## Summary

| Category | Count | Severity |
|---|---|---|
| **Critical (unfixed)** | 4 | Bugs that could cause crashes, data loss, or security breaches |
| **Critical (fixed)** | 7 | Previously reported criticals now resolved |
| **Warnings (unfixed)** | 6 | Potential issues not yet addressed |
| **Warnings (fixed)** | 6 | Previously reported warnings now resolved |
| **New issues found** | 3 | Introduced by or revealed by the fixes |

---

## Fixed Issues (Verified in Code)

### CRIT-1: SQL Injection in DisposeDatabaseService ✅ FIXED
- Uses `MySqlConnectionFactory` for per-operation connections
- All queries are parameterized via Dapper's `new { ... }` syntax
- `GetDisposeSamplesAsync` uses `SanitizeSqlName()` regex whitelist for table names
- `UpdateStatusAsync` uses a switch-based `setClause` restricted to 3 known strings
- `GetDeliverRecordsAsync`, `GetPriorityRecordsAsync`, `GetTestNameDisposeRecordsAsync` all use `@TestName` and `@Max` parameters

### CRIT-8: AptioSocketClient.Disconnect ✅ PARTIALLY FIXED
- Changed from `_lock.Wait()` (unbounded block) to `_lock.Wait(TimeSpan.FromSeconds(5))` with timeout
- Added fallback: force-disconnects if lock cannot be acquired within 5s
- Still uses synchronous `Wait()` instead of async, but the timeout mitigates thread pool starvation risk

### CRIT-9: NullReferenceException risk ✅ FIXED
- Null check for `_preBarcode` added at `DisposeSampleWorker.cs:300-304`

### CRIT-10: DMSAutoOrderWorker CancellationToken ✅ FIXED
- `ct` is now passed to all sub-services: `_pitStop.ExecuteAsync(ct)`, `_status.ExecuteAsync(ct)`, `_cleanup.ExecuteAsync(ct)`
- All sub-service `ExecuteAsync` methods accept `CancellationToken ct = default`

### CRIT-11: HealthStore async ✅ FIXED
- All SQLite operations use `ExecuteNonQueryAsync`, `ExecuteReaderAsync`, `ReadAsync`
- `RecordHealthAsync`, `RecordErrorAsync`, `GetHealthHistoryAsync`, `GetErrorsAsync`, `GetLatestModuleStatusAsync`, `CleanupOldAsync` are all async

### WARN-1: Duplicate IsWorkTime ✅ FIXED
- `DisposeSampleWorker.TickAsync` uses `new WorkTimeChecker().IsWorkTime(...)` instead of duplicated logic
- The `IsWorkTime` method is no longer defined in `DisposeSampleWorker`

### WARN-5: Connection string duplication ✅ FIXED
- `MySqlConnectionFactory` in `Common/Services/MySqlConnectionFactory.cs` centralizes connection string construction
- Used by `DisposeDatabaseService`, `ExportDatabaseService`, and `DmsDatabaseService`

### WARN-8: Cached connection ✅ FIXED
- `DisposeDatabaseService` no longer caches a single `MySqlConnection`
- All operations use `using var conn = NewConnection()` with `MySqlConnectionFactory.Create()`

### WARN-10: Password redaction in ApiEndpoints ✅ FIXED
- `RedactPasswords()` function at `ApiEndpoints.cs:57-67` recursively redacts keys containing "Password" or "Pwd"
- Called on `GET /api/config` at line 53

### WARN-11: PitStopMonitorService dictionary cleanup ✅ FIXED
- Lines 31-33: removes entries for tables that no longer exist in the database
- Lines 68-72: removes stale IDs within a table that are no longer in the current result set

### WARN-14: WorkListCleanerWorker loop interval ✅ FIXED
- Changed from hardcoded `60` to `configs.FirstOrDefault()?.LoopIntervalSeconds ?? 60` (line 19)

---

## Unfixed Issues (Remaining from v1 Report)

### CRIT-2: SQL Injection in ImmuliteCleaner ❌ NOT FIXED
**File**: `WorkListCleaner/Services/ImmuliteCleaner.cs`
**Lines**: 69-70, 73-74, 113-116

Still uses string interpolation for SQL:
```csharp
var updateSql = $"update Worklist set Accession_Num = '{_config.Prefix}{sid}' " +
    $"where Unique_Record_ID_Num = {row[0]} and Accession_Num = '{sid}' and Test_Type = '{test}'";
```
Values `sid`, `test`, `row[0]` come from the Access database (user-controlled data). Use `OleDbCommand` with parameters.

### CRIT-3: SQL Injection in StatusCorrectionService.ProcessIgnoreFlagsAsync ❌ NOT FIXED
**File**: `DMSAutoOrder/Services/StatusCorrectionService.cs`
**Lines**: 83-88

```csharp
await _db.ExecuteSqlAsync(
    $"UPDATE {_config.DbName}.reqtestresult SET flgstatus = 'V' " +
    $"WHERE codsid = '{sid}' AND codtest = '{test}'");
```
`sid` and `test` come from the database (via `ExecuteReaderAsync` on line 76) and are interpolated directly. Use parameterized queries.

### CRIT-5: SQL Injection in SampleCleanupService.ProcessTriggeredDeletionAsync ❌ NOT FIXED
**File**: `DMSAutoOrder/Services/SampleCleanupService.cs`
**Lines**: 99-100

```csharp
var sql = $"SELECT codsid, codoid FROM {_config.DbName}.reqtest " +
          $"WHERE codtest = '{testName}' AND TIMESTAMPDIFF(MINUTE, datrequest, NOW()) > {timeoutMin}";
```
`testName` and `timeoutMin` come from config (`TestTriggerSampleDeletion`) and are interpolated directly. Use parameters.

### CRIT-6: SrmStatusDecoder.Decode always returns true ❌ NOT FIXED
**File**: `Common/Services/SrmStatusDecoder.cs`
**Line**: 48

```csharp
public bool Decode(...)
{
    var nodes = DecodeAll(message);
    foreach (var node in nodes)
    {
        ...
        isReady = node.Mode == "ON" && ...;
        return true;  // Always returns true after first node
    }
    return false;
}
```
The method returns `true` after processing the first node regardless of `isReady`. The caller (`DisposeSampleWorker.HandleReadyAsync`) no longer uses this path (now uses `IsNodeReady`), but the method itself is still broken for any other caller.

### WARN-2: CentralinkConfig registered but never consumed ❌ NOT FIXED
**File**: `Program.cs:35`
`CentralinkConfig` is registered via `Configure<AptioAutoProcessConfig>` but `CentralinkService.SendLasFlag` takes individual `ip`, `port`, `driver` parameters from `CleanerConfig` instead.

### WARN-3: DmsOrderConfig dual registration ❌ NOT FIXED
**File**: `Program.cs:36,96`
- Line 36: `builder.Services.Configure<DmsOrderConfig>(...)` (IOptions)
- Line 96: `builder.Services.AddSingleton(dmsCfg)` (Singleton)
- `DmsAutoOrderWorker` injects `IOptions<DmsOrderConfig>` (line 16)
- All other DMS services inject `DmsOrderConfig` directly
- Different instances could exist

### WARN-4: SrmExportWorker time window ❌ NOT FIXED
**File**: `SrmExport/Workers/SrmExportWorker.cs:42`
```csharp
if (!_exportedToday && now.Hour == target.Hour && now.Minute == target.Minute)
```
Still uses exact-minute match. If the worker's loop interval skips over that exact minute, the export is missed for the day. Use range-based check: `now.TimeOfDay >= targetTime`.

### WARN-6: ImmuliteCleaner wraps synchronous I/O in Task.Run ❌ NOT FIXED
**File**: `WorkListCleaner/Services/ImmuliteCleaner.cs:29`
Still uses `await Task.Run(() => { ... }, ct)` which wastes a thread pool thread for I/O-bound operations.

### WARN-7: AstmConnection blocking I/O ❌ NOT FIXED
**File**: `Common/Services/ASTM.cs:105-201`
Still uses `TcpClient.Connect()` (synchronous), `NetworkStream.Read()` (synchronous), `Thread.Sleep(50)` polling, and `new Thread(ReceiveLoop)`.

### WARN-9: ConfigService writes to appsettings.json ❌ NOT FIXED
**File**: `WebAdmin/Services/ConfigService.cs:43`
Still writes to live `appsettings.json`. A crash during serialization could corrupt the config file.

### WARN-13: DisposeSampleWorker public methods ❌ NOT FIXED
**File**: `DisposeSample/Workers/DisposeSampleWorker.cs`
`TickAsync`, `HandleCommandsAsync`, `HandleNoneAsync`, `HandleReadyAsync`, `HandleAckAsync`, `HandleDisposeAsync` are all still `public`. They should be `private` — they are only called from within the class.

---

## New Issues Found

### NEW-1: SampleCleanupService bypasses MySqlConnectionFactory
**File**: `DMSAutoOrder/Services/SampleCleanupService.cs:105-107`
```csharp
using var conn = new MySqlConnector.MySqlConnection(
    $"Server={_config.DbHost};Port={_config.DbPort};Database={_config.DbName};" +
    $"User={_config.DbUser};Password={_config.DbPassword};");
```
Creates a raw MySQL connection with password in the connection string, bypassing the `MySqlConnectionFactory` that was introduced to centralize connection management. This also duplicates the connection string format.

### NEW-2: StatusCorrectionService.ProcessIgnoreFlagsAsync uses raw ADO.NET instead of Dapper
**File**: `DMSAutoOrder/Services/StatusCorrectionService.cs:73-76`
```csharp
using var conn = _db.NewConnection();
await conn.OpenAsync();
using var cmd = new MySqlConnector.MySqlCommand(sql, conn);
```
Uses raw `MySqlCommand` + `ExecuteReaderAsync` instead of Dapper's `QueryAsync`, which is inconsistent with the rest of the codebase. The `sql` variable on line 71-72 also interpolates `_config.DbName` (low risk, but inconsistent).

### NEW-3: DisposeSampleWorker still uses null-forgiving operator on record.Barcode
**File**: `DisposeSample/Workers/DisposeSampleWorker.cs:155,158,172,175,182`
Multiple calls use `r.Barcode!` (null-forgiving) without null check. While `SelectOneSendRecordAsync` (line 273-274) now has a null check for `record`, the batch methods (`ProcessDeliverAsync`, `ProcessPriorityAsync`, `ProcessTestNameDisposeAsync`) do not validate that `Barcode` is non-null before passing to `SendDeliverAsync`/`SendStatPriorityAsync`/`SendDisposeAsync`.

---

## Recommendations

### Priority 1 — Fix remaining SQL injection vectors
1. **ImmuliteCleaner.cs:69-75, 113-116** — Convert to `OleDbCommand` with parameters
2. **StatusCorrectionService.cs:83-88** — Use `Dapper` with `new { sid, test }` parameters
3. **SampleCleanupService.cs:99-100** — Use `@TestName` and `@Timeout` parameters

### Priority 2 — Fix SrmStatusDecoder.Decode return value
**SrmStatusDecoder.cs:48** — Change `return true;` to track whether any node is ready across all nodes, then return `isReady` after the loop. Currently the method is unused by the main code path, but it's a ticking time bomb for any future caller.

### Priority 3 — Fix SrmExportWorker time window
**SrmExportWorker.cs:42** — Change to `if (!_exportedToday && now.TimeOfDay >= targetTime)` to prevent missed exports.

### Priority 4 — Eliminate MySqlConnectionFactory bypass
**SampleCleanupService.cs:105-107** — Use `_db.NewConnection()` or inject the factory directly instead of constructing a raw connection string.

### Priority 5 — Fix DmsOrderConfig dual registration
**Program.cs:36,96** — Remove the `Configure<DmsOrderConfig>()` on line 36 if all consumers use direct injection, or remove the singleton on line 96 and make all consumers use `IOptions<DmsOrderConfig>`.

### Priority 6 — Make DisposeSampleWorker state machine methods private
**DisposeSampleWorker.cs:106,132,224,264,319,364** — Change `public` to `private` for `TickAsync`, `HandleCommandsAsync`, `HandleNoneAsync`, `HandleReadyAsync`, `HandleAckAsync`, `HandleDisposeAsync`.

### Priority 7 — Address warnings
- **WARN-2/CentralinkConfig**: Remove dead config registration or wire it to `CentralinkService`
- **WARN-6**: Remove `Task.Run` wrapper in `ImmuliteCleaner.CleanAsync`
- **WARN-7**: Consider async rewrite of `AstmConnection`
- **WARN-9**: Write config updates to a separate override file instead of `appsettings.json`
- **NEW-3**: Add null checks for `Barcode` in batch processing methods