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

    public bool Decode(string message, HashSet<string> allowedErrors, out string statusMessage, out bool isReady)
    {
        statusMessage = "";
        isReady = false;

        var nodes = DecodeAll(message);
        foreach (var node in nodes)
        {
            statusMessage = $"Mode:{node.Mode} Status:{node.Status} Error:{node.Error}";
            var errOk = allowedErrors.Contains(node.Error) ? "0000" : node.Error;
            isReady = node.Mode == "ON" && (node.Status == "G" || node.Status == "Y") && errOk == "0000";
            return true;
        }

        return false;
    }

    public bool IsNodeReady(string message, string nodeId, HashSet<string> allowedErrors)
    {
        var nodes = DecodeAll(message);
        foreach (var node in nodes)
        {
            if (node.NodeId != nodeId) continue;
            var errOk = allowedErrors.Contains(node.Error) ? "0000" : node.Error;
            return node.Mode == "ON" && (node.Status == "G" || node.Status == "Y") && errOk == "0000";
        }
        return false;
    }
}