using System.Diagnostics;

namespace SmartBOQ.App.ViewModels;

/// <summary>
/// Throttled IProgress wrapper that rate-limits progress dispatching to the WPF Dispatcher.
/// Prevents UI stutter and message pump saturation during high-speed parallel computations.
/// </summary>
public sealed class ThrottledProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;
    private readonly long _throttleIntervalTicks;
    private long _lastReportTicks;

    public ThrottledProgress(Action<T> handler, int throttleIntervalMs = 50)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _throttleIntervalTicks = Stopwatch.Frequency * throttleIntervalMs / 1000;
        _lastReportTicks = 0;
    }

    public void Report(T value)
    {
        long current = Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref _lastReportTicks);

        if (current - last >= _throttleIntervalTicks)
        {
            if (Interlocked.CompareExchange(ref _lastReportTicks, current, last) == last)
            {
                _handler(value);
            }
        }
    }

    /// <summary>
    /// Forces an immediate un-throttled report (e.g. for 0% start or 100% completion).
    /// </summary>
    public void ReportImmediate(T value)
    {
        Volatile.Write(ref _lastReportTicks, Stopwatch.GetTimestamp());
        _handler(value);
    }
}
