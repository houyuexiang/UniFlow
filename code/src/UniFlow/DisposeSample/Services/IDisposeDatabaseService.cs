using UniFlow.DisposeSample.Models;

namespace UniFlow.DisposeSample.Services;

public interface IDisposeDatabaseService
{
    Task<bool> PingAsync();
    Task EnsureSamDisposeTableAsync();
    Task<int> CheckSrmSampleCountAsync(string nodeId);
    Task<List<SampleRecord>> GetDisposeSamplesAsync(int cmdType, string cmdName, int maxCount);
    Task<int> InsertDisposeRecordsAsync(int needCount, List<SampleRecord> samples);
    Task<DisposeStatus?> SelectOneSendRecordAsync();
    /// <summary>
    /// 更新记录状态字段（send / skip / disposed / checkcount）。
    /// <para>
    /// 返回 <c>true</c> = 写入成功；<c>false</c> = 写入失败（异常已记录日志）。
    /// 调用方**必须**检查返回值：失败时数据库状态未推进（记录仍是 send=0），
    /// 若调用方静默忽略，会出现"状态机已进 Ack 但 DB 仍待发 → 下一轮重复下发"的破坏性不一致。
    /// </para>
    /// </summary>
    Task<bool> UpdateStatusAsync(string barcode, string field);
    Task<bool> CheckDisposedAsync(string barcode, string nodeId);
    Task<int> GetCheckCountAsync(string barcode);
    Task DeleteUnsendAsync();
    Task DeleteHistoryAsync();
    /// <summary>
    /// 后期补查：复查 send=1 &amp; disposed=0 的记录，若 t_sample 中条码位置已变为
    /// &amp;3-{node}-%（已丢弃）则置 disposed=1。
    /// <para>
    /// 轮转窗口：只查 ID &gt; afterId 的记录（ORDER BY ID LIMIT max），
    /// 返回 (命中数, 本批最大 ID)。调用方把 LastId 作为下一次的 afterId 传入即可覆盖全表；
    /// 返回 LastId=0 表示本轮查完（调用方应把游标归零，下一轮从头开始）。
    /// </para>
    /// </summary>
    Task<(int Hits, long LastId)> RecheckPendingDisposalsAsync(int max = 200, long afterId = 0);

    /// <summary>
    /// 重发计数字段（<c>sam_dispose_status.resend_count</c>）是否就绪。
    /// 建表成功或旧表迁移（ALTER ADD COLUMN）成功 → true；
    /// 旧表迁移失败 → false（此时依赖 resend_count 的查询都会失败，Worker 必须跳过重发/失败标记）。
    /// </summary>
    bool ResendColumnReady { get; }

    // ===== 队列查询 / 重发 / 失败处置（Web 管理页调用）=====

    /// <summary>待发数量（send=0）。</summary>
    Task<int> CountPendingSendAsync();
    /// <summary>待处理数量：send=0 或 (send=1 且 disposed=0)。</summary>
    Task<int> CountPendingAsync();
    /// <summary>待确认数量：send=1 且 disposed=0（已投递但未观测到丢弃）。</summary>
    Task<int> CountPendingConfirmAsync();
    /// <summary>失败数量：send=3 且 disposed=0（重发次数用尽，等待人工处置）。</summary>
    Task<int> CountFailedAsync();
    /// <summary>待处理明细：send IN (1,3) AND disposed=0 AND resend_count &gt;= minResendCount。</summary>
    Task<List<DisposeStatus>> GetPendingRecordsAsync(int minResendCount, int limit);
    /// <summary>重发候选：send=1 AND disposed=0 AND send_time &lt;= cutoff（maxResendCount&gt;0 时再限 resend_count &lt; max）。</summary>
    Task<List<DisposeStatus>> GetResendCandidatesAsync(string cutoff, int maxResendCount, int limit);
    /// <summary>重发次数已用尽（resend_count &gt;= maxResendCount）的失败候选；maxResendCount&lt;=0 返回空列表。</summary>
    Task<List<DisposeStatus>> GetExhaustedCandidatesAsync(int maxResendCount, int limit);
    /// <summary>
    /// 重排队：把「已投递但未确认丢弃」的记录放回待发队列（send=0）。
    /// <para>
    /// 只改 send：<b>不改 send_time</b>（保留原投递时间，供「已重发次数」判定与历史清理）、
    /// <b>不增 resend_count</b>（重发次数只在真正发送成功时 +1，由 UpdateStatusAsync("send") 负责）。
    /// </para>
    /// </summary>
    Task<int> RequeueForResendAsync(IReadOnlyCollection<string> barcodes);
    /// <summary>标记失败：send=3（不再自动重发）。返回受影响行数。</summary>
    Task<int> MarkFailedAsync(IReadOnlyCollection<string> barcodes);
    /// <summary>重新入队：send=0, resend_count=0, checkcount=0, send_time=NULL。返回受影响行数。</summary>
    Task<int> ResetRecordsAsync(IReadOnlyCollection<string> barcodes);
    /// <summary>删除记录。返回受影响行数。</summary>
    Task<int> DeleteRecordsAsync(IReadOnlyCollection<string> barcodes);
    /// <summary>复查单条：位置已是 &amp;3-{node}-% 则置 disposed=1 并返回 true。</summary>
    Task<bool> RecheckRecordAsync(string barcode);

    Task<List<SampleRecord>> GetAllScanableSamplesAsync(int maxCount, IReadOnlyCollection<string>? candidateTestNames = null);
}
