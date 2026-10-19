namespace WinCfApiWrapper.Mounting;

// One instance belongs to one sequential operation. Suppressed updates never
// reach IProgress.Report, so Progress<T> cannot queue a UI callback for them.
internal sealed class ProgressThrottle(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _lastReport;
    private bool _reported;

    public bool ShouldReport(long completed, long total)
    {
        long now = _time.GetTimestamp();
        if (
            _reported
            && completed != 0
            && completed != total
            && _time.GetElapsedTime(_lastReport, now) < Interval
        )
            return false;
        _lastReport = now;
        _reported = true;
        return true;
    }
}
