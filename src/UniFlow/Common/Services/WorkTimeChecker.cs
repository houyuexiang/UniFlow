namespace UniFlow.Common.Services;

public record TimeRange(TimeOnly Start, TimeOnly End, int Threshold);

public class WorkTimeChecker
{
    public bool IsWorkTime(HashSet<int> runDays, List<TimeRange> ranges, out int threshold)
    {
        threshold = 0;
        var now = DateTime.Now;
        var dotNetDay = (int)now.DayOfWeek;
        var delphiDay = dotNetDay == 0 ? 7 : dotNetDay;
        if (!runDays.Contains(delphiDay)) return false;

        var nowTime = TimeOnly.FromDateTime(now);
        foreach (var r in ranges)
        {
            if (r.Start <= r.End)
            {
                if (nowTime >= r.Start && nowTime <= r.End)
                { threshold = r.Threshold; return true; }
            }
            else
            {
                if (nowTime >= r.Start || nowTime <= r.End)
                { threshold = r.Threshold; return true; }
            }
        }
        return false;
    }

    public static HashSet<int> ParseRunDays(string config)
    {
        var result = new HashSet<int>();
        foreach (var d in config.Split(',', StringSplitOptions.TrimEntries))
            if (int.TryParse(d, out var day) && day >= 1 && day <= 7)
                result.Add(day);
        return result;
    }

    public static List<TimeRange> ParseTimeRanges(string config)
    {
        var result = new List<TimeRange>();
        var ranges = config.Split(';', StringSplitOptions.TrimEntries);
        foreach (var r in ranges)
        {
            if (string.IsNullOrEmpty(r)) continue;
            var parts = r.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;
            var tp = parts[0].Split('-', StringSplitOptions.TrimEntries);
            if (tp.Length < 2) continue;
            if (TimeOnly.TryParse(tp[0], out var st) &&
                TimeOnly.TryParse(tp[1], out var et) &&
                int.TryParse(parts[1], out var th))
                result.Add(new TimeRange(st, et, th));
        }
        return result;
    }
}
