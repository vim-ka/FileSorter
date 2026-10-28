using FileSorter.Models;
using FileSorter.Services;

namespace FileSorter.Core.Tests;

public class NotificationThrottleTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly NotificationThrottle _throttle = new(Interval);
    private readonly List<(string Folder, IReadOnlyList<OrganizeOperation> Ops)> _shown = [];

    private void Flush(string folder, IReadOnlyList<OrganizeOperation> ops) => _shown.Add((folder, ops));

    [Fact]
    public void FirstRun_ShownImmediately()
    {
        var op = new OrganizeOperation();

        _throttle.Add("Downloads", op, T0, Flush);

        var shown = Assert.Single(_shown);
        Assert.Equal("Downloads", shown.Folder);
        Assert.Same(op, Assert.Single(shown.Ops));
        Assert.False(_throttle.HasPending);
    }

    [Fact]
    public void RunsWithinInterval_AccumulatedAndShownOnceByTick()
    {
        _throttle.Add("Downloads", new OrganizeOperation(), T0, Flush);
        var second = new OrganizeOperation();
        var third = new OrganizeOperation();

        _throttle.Add("Downloads", second, T0.AddSeconds(5), Flush);
        _throttle.Add("Downloads", third, T0.AddSeconds(10), Flush);
        _throttle.Tick(T0.AddSeconds(20), Flush);

        Assert.Single(_shown);
        Assert.True(_throttle.HasPending);

        _throttle.Tick(T0.AddSeconds(30), Flush);

        Assert.Equal(2, _shown.Count);
        Assert.Equal([second, third], _shown[1].Ops);
        Assert.False(_throttle.HasPending);

        _throttle.Tick(T0.AddSeconds(90), Flush);
        Assert.Equal(2, _shown.Count);
    }

    [Fact]
    public void RunAfterInterval_ShownImmediately()
    {
        _throttle.Add("Downloads", new OrganizeOperation(), T0, Flush);
        _throttle.Add("Downloads", new OrganizeOperation(), T0.AddSeconds(31), Flush);

        Assert.Equal(2, _shown.Count);
    }

    [Fact]
    public void RunAfterIntervalWithAccumulated_ShownTogether()
    {
        _throttle.Add("Downloads", new OrganizeOperation(), T0, Flush);
        var waiting = new OrganizeOperation();
        _throttle.Add("Downloads", waiting, T0.AddSeconds(5), Flush);
        var late = new OrganizeOperation();

        _throttle.Add("Downloads", late, T0.AddSeconds(40), Flush);

        Assert.Equal(2, _shown.Count);
        Assert.Equal([waiting, late], _shown[1].Ops);
    }

    [Fact]
    public void Folders_ThrottledIndependently()
    {
        _throttle.Add("Downloads", new OrganizeOperation(), T0, Flush);
        _throttle.Add("Desktop", new OrganizeOperation(), T0.AddSeconds(1), Flush);
        _throttle.Add("Downloads", new OrganizeOperation(), T0.AddSeconds(2), Flush);

        Assert.Equal(["Downloads", "Desktop"], _shown.Select(s => s.Folder));
    }
}
