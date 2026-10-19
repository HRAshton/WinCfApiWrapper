using WinCfApiWrapper.Mounting;
using Xunit;

namespace WinCfApiWrapper.Tests.Mounting;

public sealed class ProgressThrottleTests
{
    [Fact]
    public void LargeCatalog_SuppressesIntermediateReports_AndPreservesCompletion()
    {
        var clock = new ManualClock();
        var throttle = new ProgressThrottle(clock);
        int reports = 0;
        for (int completed = 0; completed <= 200_000; completed++)
        {
            // Model one second of startup work without a wall-clock delay.
            clock.Timestamp = completed * 50L;
            if (throttle.ShouldReport(completed, 200_000))
                reports++;
        }

        Assert.Equal(11, reports);
    }

    [Fact]
    public void Boundaries_ReportImmediately_AndRestartTheInterval()
    {
        var clock = new ManualClock();
        var throttle = new ProgressThrottle(clock);
        Assert.True(throttle.ShouldReport(0, 100));
        clock.Timestamp = TimeSpan.FromMilliseconds(99).Ticks;
        Assert.False(throttle.ShouldReport(1, 100));
        Assert.True(throttle.ShouldReport(100, 100));
        Assert.True(throttle.ShouldReport(0, 0));
        clock.Timestamp += TimeSpan.FromMilliseconds(99).Ticks;
        Assert.False(throttle.ShouldReport(1, 100));
        clock.Timestamp += TimeSpan.FromMilliseconds(1).Ticks;
        Assert.True(throttle.ShouldReport(2, 100));
    }

    private sealed class ManualClock : TimeProvider
    {
        public long Timestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Timestamp;
    }
}
