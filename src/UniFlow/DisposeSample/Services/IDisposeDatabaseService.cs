using UniFlow.DisposeSample.Models;

namespace UniFlow.DisposeSample.Services;

public interface IDisposeDatabaseService
{
    Task<bool> PingAsync();
    Task<int> CheckSrmSampleCountAsync(string nodeId);
    Task<List<SampleRecord>> GetDisposeSamplesAsync(int cmdType, string cmdName, int maxCount);
    Task<int> InsertDisposeRecordsAsync(int needCount, List<SampleRecord> samples);
    Task<DisposeStatus?> SelectOneSendRecordAsync();
    Task UpdateStatusAsync(string barcode, string field);
    Task<bool> CheckDisposedAsync(string barcode, string nodeId);
    Task<int> GetCheckCountAsync(string barcode);
    Task DeleteUnsendAsync();
    Task DeleteHistoryAsync();
    Task<List<SampleRecord>> GetDeliverRecordsAsync(string deliverTestName, int maxCount);
    Task<List<SampleRecord>> GetPriorityRecordsAsync(string priorityTestName, int maxCount);
    Task SetPriorityDoneAsync(string barcode);
}
