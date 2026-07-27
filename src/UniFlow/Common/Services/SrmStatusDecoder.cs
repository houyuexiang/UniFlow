namespace UniFlow.Common.Services;

public class SrmStatusDecoder
{
    public bool Decode(string message, HashSet<string> allowedErrors,
        out string statusMessage, out bool isReady)
    {
        statusMessage = "";
        isReady = false;

        foreach (var seg in message.Split('\\'))
        {
            if (!seg.Contains("SRM")) continue;
            var fields = seg.Split('^');
            if (fields.Length < 6) return false;

            var mode = fields[3];
            var err = fields[4];
            var st = fields[5];
            statusMessage = $"Mode:{mode} Status:{st} Error:{err}";

            var errOk = allowedErrors.Contains(err) ? "0000" : err;
            if (mode == "ON" && (st == "G" || st == "Y") && errOk == "0000")
                isReady = true;
            return true;
        }
        return false;
    }

    public bool Decode(string message, HashSet<string> allowedErrors, out bool isReady)
        => Decode(message, allowedErrors, out _, out isReady);
}
