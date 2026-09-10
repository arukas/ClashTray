namespace ClashTray.Core;

/// <summary>Counts a short, uninterrupted sequence without persisting partial progress.</summary>
public sealed class LogoUnlockSequence
{
    public const int RequiredClicks = 5;
    public const long MaximumGapMilliseconds = 1500;
    private int _clicks;
    private long _lastClick;

    public int Click(long timestampMilliseconds)
    {
        if (_clicks > 0 && (timestampMilliseconds < _lastClick
            || timestampMilliseconds - _lastClick > MaximumGapMilliseconds))
        {
            Reset();
        }

        _lastClick = timestampMilliseconds;
        _clicks++;
        int remaining = RequiredClicks - _clicks;
        if (remaining == 0)
        {
            Reset();
        }

        return remaining;
    }

    public void Reset() => _clicks = 0;
}
