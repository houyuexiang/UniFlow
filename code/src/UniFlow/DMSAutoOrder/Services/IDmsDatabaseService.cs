namespace UniFlow.DMSAutoOrder.Services;

/// <summary>
/// DMS 数据访问接口（PitStop 监控 + 触发式样本清理共用）。
/// <para>
/// 抽出接口只为让 <see cref="PitStopMonitorService"/> / <see cref="SampleCleanupService"/> 可被单元测试替身驱动
/// （DmsDatabaseService 依赖真实 MySQL 连接，无法直接 mock）。
/// 生产实现见 <see cref="DmsDatabaseService"/>；本接口已在 DI 中注册（Program.cs）。
/// </para>
/// </summary>
public interface IDmsDatabaseService
{
    // ===== PitStop 监控 =====

    /// <summary>库中所有 pitstop* 表名（来自 information_schema 白名单）。</summary>
    Task<List<string>> GetPitStopTablesAsync();

    /// <summary>某张 pitstop 表里"运行中"（flgrunning 非空）的记录。</summary>
    Task<List<Dictionary<string, object>>> GetRunningPitstopRecordsAsync(string tableName);

    /// <summary>
    /// 删除**指定 id** 的运行中记录（只删这些 id，不清整表）。
    /// </summary>
    Task<int> DeletePitstopRecordsAsync(string tableName, IReadOnlyCollection<string> ids);

    // ===== 触发式样本清理 =====

    /// <summary>含指定列的所有表名（用于动态生成删除 SQL 模板）。</summary>
    Task<List<string>> GetTableColumnsAsync(string columnName);

    /// <summary>某个测试名下的全部样本 sid（去重、非空）。</summary>
    Task<List<string>> GetTriggerSampleIdsAsync(string testName);

    /// <summary>取 sid 对应的 oid（reqtube）；取不到返回空串。</summary>
    Task<string> GetOidBySidAsync(string sid);

    /// <summary>取某样本在 reqtest 中的全部测试名（去重、非空）。</summary>
    Task<List<string>> GetTestsBySidAsync(string sid);

    /// <summary>
    /// 删除样本在所有关联表里的记录（整组删除在**同一事务**内；失败整体回滚并返回 0）。
    /// </summary>
    Task<int> DeleteSampleAsync(string sid, string oid, List<string> deleteTableSqlTemplates);
}
