namespace UniFlow.WebAdmin.Services;

// 全新默认配置（文件损坏/不存在时生成；结构与当前模型对齐）
public static class DefaultConfig
{
    private const string DefaultJson =
        """
        {
          "ConfigVersion": "1.0",
          "Features": {
            "Aptio": { "DisposeSample": true, "SrmExport": true, "Delivery": false, "DeliveryFile": false, "Priority": false, "TestNameDispose": false },
            "Immulite": { "WorkListCleaner": false },
            "Dms": { "PitStopMonitor": false, "StatusCorrection": false, "SampleCleanup": false, "EmptyResultCleanup": false }
          },
          "Aptio": {
            "Ip": "127.0.0.1", "Port": 2055, "SrmNodeIds": ["09", "16"],
            "Database": { "Host": "127.0.0.1", "Port": 3306, "User": "root", "Password": "root", "Database": "flexlab" },
            "DisposeSample": {
              "CommandType": 0, "CommandName": "view_overtimestoragesample",
              "DiscardRunDate": "1,2,3,4,5,6,7", "DiscardTimeRange": "01:30-05:45,3800;18:00-07:00,7500;",
              "RunDays": null, "TimeRanges": null, "AllowedSrmErrorCodes": null,
              "LoopIntervalSeconds": 3, "MaxWaitDiscardCount": 2, "MaxOnetimeSelectDiscardCount": 8,
              "AllowSrmErrorCode": "0000,0F0A,0E59,AAAA", "SkipOnUnknownNode": true
            },
            "SrmExport": { "ExportTime": "08:30", "LoopIntervalSeconds": 60, "LogRetentionDays": 30, "OutputPath": "DisposeFile" },
            "Delivery": { "TestName": "" },
            "DeliveryFile": { "DeliveryListFilePath": "", "LoopIntervalSeconds": 60 },
            "Priority": { "TestName": "" },
            "TestNameDispose": { "DisposeTestName": "" },
            "BatchScan": { "LoopIntervalSeconds": 60, "MaxOnetimeScanCount": 500 }
          },
          "WorkListCleaners": [],
          "WebAdmin": { "Enabled": true, "BindIp": "0.0.0.0", "Port": 5100, "ErrorRetentionDays": 30, "HealthCheckIntervalSeconds": 60 },
          "DMS": {
            "Enabled": false, "DbHost": "127.0.0.1", "DbPort": 3306, "DbUser": "root", "DbPassword": "", "DbName": "dms",
            "LoopIntervalSeconds": 60, "PitStop": { "TimeoutMinutes": 1 },
            "StatusCorrection": { "AutoModifyTestStatus": "1", "IgnoreFlags": null, "IgnoreFlagList": "" },
            "SampleCleanup": { "TriggerRules": null, "TestTriggerSampleDeletion": "", "SendCancelMessageToAptio": false }
          }
        }
        """;

    public static Dictionary<string, object?> Build()
        => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(DefaultJson) ?? new();
}
