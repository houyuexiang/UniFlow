namespace UniFlow.Common.Services;

public class SrmStatusDecoder
{
    public class NodeStatus
    {
        public string NodeId { get; set; } = "";
        public string Mode { get; set; } = "";
        public string Error { get; set; } = "";
        public string Status { get; set; } = "";
        public bool IsReady { get; set; }
    }

    public List<NodeStatus> DecodeAll(string message)
    {
        var results = new List<NodeStatus>();

        foreach (var seg in message.Split('\\'))
        {
            if (!seg.Contains("SRM")) continue;
            var fields = seg.Split('^');
            if (fields.Length < 6) continue;

            var node = new NodeStatus
            {
                NodeId = fields[0],
                Mode = fields[3],
                Error = fields[4],
                Status = fields[5]
            };
            results.Add(node);
        }

        return results;
    }

    /// <summary>
    /// 解析 STATUS 报文并给出首个 SRM 节点的就绪判定。
    /// <para>
    /// <b>生产主路径不使用本方法</b>（状态机与重排队都走 <see cref="IsNodeReady"/>，按节点号取）；
    /// 本方法保留给外部/诊断调用与单元测试，语义与 <see cref="IsNodeReady"/> 一致。
    /// </para>
    /// </summary>
    /// <param name="allowStandby">测试开关：true 时额外允许 Mode=STB 与 Status=P 参与判定。</param>
    public bool Decode(string message, HashSet<string> allowedErrors, out string statusMessage, out bool isReady, bool allowStandby = false)
    {
        statusMessage = "";
        isReady = false;

        var nodes = DecodeAll(message);
        foreach (var node in nodes)
        {
            statusMessage = $"Mode:{node.Mode} Status:{node.Status} Error:{node.Error}";
            isReady = EvaluateReady(node, allowedErrors, allowStandby);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 判断指定节点当前是否「可丢弃」。
    /// <para>
    /// 默认（<paramref name="allowStandby"/> = false）：<c>Mode == "ON"</c> 且 <c>Status ∈ {G,Y}</c> 且错误码在允许列表。
    /// 开启测试开关时：额外允许 <c>Mode == "STB"</c>（Stand-by）与 <c>Status == "P"</c>（Pink）。
    /// <c>GTO / PA / OFF / FR / SH / EXE</c> <b>始终不允许</b>（与开关无关）。
    /// </para>
    /// </summary>
    public bool IsNodeReady(string message, string nodeId, HashSet<string> allowedErrors, bool allowStandby = false)
    {
        var nodes = DecodeAll(message);
        foreach (var node in nodes)
        {
            if (node.NodeId != nodeId) continue;
            return EvaluateReady(node, allowedErrors, allowStandby);
        }
        return false;
    }

    // 就绪判定的唯一实现（Decode / IsNodeReady 共用，口径不漂移）
    //   Mode：ON 恒可；STB 仅在测试开关 AllowStandbyDispose=true 时可；GTO/PA/OFF/FR/SH/EXE 永不可
    //   Status：G/Y 恒可；P 仅在测试开关开启时可
    //   Error：必须在允许列表内（否则视为 0000 之外的错误码 → 不可丢弃）
    private static bool EvaluateReady(NodeStatus node, HashSet<string> allowedErrors, bool allowStandby)
    {
        var errOk = allowedErrors.Contains(node.Error) ? "0000" : node.Error;
        var modeOk = node.Mode == "ON" || (allowStandby && node.Mode == "STB");
        var statusOk = node.Status == "G" || node.Status == "Y" || (allowStandby && node.Status == "P");
        return modeOk && statusOk && errOk == "0000";
    }

    // ===== 只读诊断：不参与就绪判定 =====
    // 手册 Appendix F：Mode 取值含 ON（Automation Map ON，正常处理）/ STB（Stand-by）/
    // GTO / PA / OFF / FR / SH / EXE 等。其中 STB = carriers NOT processed、Automation Map OFF，
    // 即「待命/未处理」：默认（AllowStandbyDispose=false）不算可丢弃状态，IsNodeReady 返回 false；
    // 开启测试开关（AllowStandbyDispose=true）后 STB 与 Pink(P) 才参与丢弃判断。
    // GTO / PA / OFF / FR / SH / EXE 始终不允许。
    // 本节方法仅用于日志说明「为什么跳过」，不改变就绪判定本身。

    /// <summary>取指定节点的 Mode（ON/OFF/STB/GTO/PA/FR/SH/EXE…）；节点不存在返回空串。</summary>
    public string GetNodeMode(string message, string nodeId)
    {
        var node = FindNode(message, nodeId);
        return node?.Mode ?? "";
    }

    /// <summary>取指定节点的完整状态（Mode/Status/Error）；节点不存在返回 null。</summary>
    public NodeStatus? FindNode(string message, string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return null;
        foreach (var node in DecodeAll(message))
            if (node.NodeId == nodeId) return node;
        return null;
    }

    /// <summary>人类可读的节点状态描述，用于日志（如 "mode=STB status=G error=0000"）。</summary>
    public string DescribeNode(string message, string nodeId)
    {
        var node = FindNode(message, nodeId);
        if (node == null) return "node not found in STATUS";
        return $"mode={node.Mode} status={node.Status} error={node.Error}";
    }
}