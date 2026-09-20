using System.Diagnostics;

namespace AreaRec.Core.Recording;

public interface IMonotonicClock
{
    TimeSpan Now { get; }
}

public sealed class StopwatchClock : IMonotonicClock
{
    private readonly long _origin = Stopwatch.GetTimestamp();

    public TimeSpan Now => Stopwatch.GetElapsedTime(_origin);
}

public sealed class FramePacer
{
    private readonly TimeSpan _period;
    private readonly IMonotonicClock _clock;
    private TimeSpan _nextDeadline;

    public FramePacer(int framesPerSecond, IMonotonicClock clock)
    {
        if (framesPerSecond is not (30 or 60))
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        _period = TimeSpan.FromSeconds(1d / framesPerSecond);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public void Start() => _nextDeadline = _clock.Now;

    public TimeSpan Advance()
    {
        _nextDeadline += _period;
        var delay = _nextDeadline - _clock.Now;
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }
}
