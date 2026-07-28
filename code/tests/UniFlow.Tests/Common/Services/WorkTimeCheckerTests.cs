using UniFlow.Common.Services;
using Shouldly;

namespace UniFlow.Tests.Common.Services;

public class WorkTimeCheckerTests
{
    private readonly WorkTimeChecker _checker = new();

    [Fact]
    public void ParseRunDays_ValidInput_ParsesCorrectly()
    {
        var result = WorkTimeChecker.ParseRunDays("1,2,3,4,5,6,7");
        result.ShouldBe(new HashSet<int> { 1, 2, 3, 4, 5, 6, 7 });
    }

    [Fact]
    public void ParseRunDays_PartialWeek_ParsesCorrectly()
    {
        var result = WorkTimeChecker.ParseRunDays("1,2,3,4,5");
        result.ShouldBe(new HashSet<int> { 1, 2, 3, 4, 5 });
    }

    [Fact]
    public void ParseRunDays_WithSpaces_TrimsCorrectly()
    {
        var result = WorkTimeChecker.ParseRunDays("1, 2, 3");
        result.ShouldBe(new HashSet<int> { 1, 2, 3 });
    }

    [Fact]
    public void ParseRunDays_InvalidValues_Ignored()
    {
        var result = WorkTimeChecker.ParseRunDays("1,abc,8,0,2");
        result.ShouldBe(new HashSet<int> { 1, 2 });
    }

    [Fact]
    public void ParseRunDays_Empty_ReturnsEmpty()
    {
        WorkTimeChecker.ParseRunDays("").ShouldBeEmpty();
    }

    [Fact]
    public void ParseTimeRanges_SingleRange_ParsesCorrectly()
    {
        var result = WorkTimeChecker.ParseTimeRanges("07:30-15:30,9000;");
        result.Count.ShouldBe(1);
        result[0].Start.ShouldBe(new TimeOnly(7, 30));
        result[0].End.ShouldBe(new TimeOnly(15, 30));
        result[0].Threshold.ShouldBe(9000);
    }

    [Fact]
    public void ParseTimeRanges_MultipleRanges_ParsesCorrectly()
    {
        var result = WorkTimeChecker.ParseTimeRanges("07:30-15:30,9000;18:00-07:00,7500;");
        result.Count.ShouldBe(2);
        result[1].Start.ShouldBe(new TimeOnly(18, 0));
        result[1].Threshold.ShouldBe(7500);
    }

    [Fact]
    public void ParseTimeRanges_InvalidFormat_SkipsMalformed()
    {
        var result = WorkTimeChecker.ParseTimeRanges("invalid;07:30-15:30,9000;");
        result.Count.ShouldBe(1);
    }

    [Fact]
    public void ParseTimeRanges_Empty_ReturnsEmpty()
    {
        WorkTimeChecker.ParseTimeRanges("").ShouldBeEmpty();
    }

    [Fact]
    public void IsWorkTime_MatchingDayAndTime_ReturnsTrue()
    {
        var now = DateTime.Now;
        var dotNetDay = (int)now.DayOfWeek;
        var delphiDay = dotNetDay == 0 ? 7 : dotNetDay;
        var runDays = new HashSet<int> { delphiDay };
        var ranges = new List<TimeRange>
        {
            new(new TimeOnly(0, 0), new TimeOnly(23, 59), 5000)
        };

        _checker.IsWorkTime(runDays, ranges, out var threshold).ShouldBeTrue();
        threshold.ShouldBe(5000);
    }

    [Fact]
    public void IsWorkTime_WrongDay_ReturnsFalse()
    {
        var runDays = new HashSet<int> { 99 };
        var ranges = new List<TimeRange>
        {
            new(new TimeOnly(0, 0), new TimeOnly(23, 59), 5000)
        };

        _checker.IsWorkTime(runDays, ranges, out _).ShouldBeFalse();
    }

    [Fact]
    public void IsWorkTime_OutsideTimeRange_ReturnsFalse()
    {
        var now = DateTime.Now;
        var dotNetDay = (int)now.DayOfWeek;
        var delphiDay = dotNetDay == 0 ? 7 : dotNetDay;
        var runDays = new HashSet<int> { delphiDay };
        var futureStart = TimeOnly.FromDateTime(now.AddHours(2));
        var futureEnd = TimeOnly.FromDateTime(now.AddHours(3));
        var ranges = new List<TimeRange>
        {
            new(futureStart, futureEnd, 5000)
        };

        _checker.IsWorkTime(runDays, ranges, out _).ShouldBeFalse();
    }

    [Fact]
    public void IsWorkTime_EmptyRanges_ReturnsFalse()
    {
        var runDays = new HashSet<int> { 1 };
        _checker.IsWorkTime(runDays, new List<TimeRange>(), out _).ShouldBeFalse();
    }

    [Fact]
    public void IsWorkTime_MultipleRanges_FirstMatchingUsed()
    {
        var now = DateTime.Now;
        var dotNetDay = (int)now.DayOfWeek;
        var delphiDay = dotNetDay == 0 ? 7 : dotNetDay;
        var runDays = new HashSet<int> { delphiDay };
        var ranges = new List<TimeRange>
        {
            new(new TimeOnly(0, 0), new TimeOnly(23, 59), 100),
            new(new TimeOnly(0, 0), new TimeOnly(23, 59), 200)
        };

        _checker.IsWorkTime(runDays, ranges, out var threshold).ShouldBeTrue();
        threshold.ShouldBe(100);
    }

    [Fact]
    public void IsWorkTime_CrossMidnightRange_AfterMidnight_ReturnsTrue()
    {
        var runDays = new HashSet<int> { 1, 2, 3, 4, 5, 6, 7 };
        var ranges = new List<TimeRange>
        {
            new(new TimeOnly(22, 0), new TimeOnly(6, 0), 3000)
        };

        if (DateTime.Now.Hour >= 22 || DateTime.Now.Hour < 6)
            _checker.IsWorkTime(runDays, ranges, out _).ShouldBeTrue();
    }
}
