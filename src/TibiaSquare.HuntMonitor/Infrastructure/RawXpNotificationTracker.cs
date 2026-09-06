namespace TibiaSquare.HuntMonitor.Infrastructure;

internal sealed record MissingRawXpSources(
    bool HuntAnalyser,
    bool XpAnalyser);

internal sealed class RawXpNotificationTracker
{
    internal const int ConfirmationFrames = 10;

    private int _consecutiveHuntAnalyserMisses;
    private int _consecutiveXpAnalyserMisses;
    private bool _huntAnalyserReported;
    private bool _xpAnalyserReported;

    public MissingRawXpSources? Observe(
        bool huntAnalyserMissing,
        bool xpAnalyserMissing)
    {
        _consecutiveHuntAnalyserMisses = huntAnalyserMissing
            ? _consecutiveHuntAnalyserMisses + 1
            : 0;
        _consecutiveXpAnalyserMisses = xpAnalyserMissing
            ? _consecutiveXpAnalyserMisses + 1
            : 0;

        bool reportHuntAnalyser = !_huntAnalyserReported
            && _consecutiveHuntAnalyserMisses >= ConfirmationFrames;
        bool reportXpAnalyser = !_xpAnalyserReported
            && _consecutiveXpAnalyserMisses >= ConfirmationFrames;
        if (!reportHuntAnalyser && !reportXpAnalyser)
            return null;

        _huntAnalyserReported |= reportHuntAnalyser;
        _xpAnalyserReported |= reportXpAnalyser;
        return new MissingRawXpSources(reportHuntAnalyser, reportXpAnalyser);
    }

    public void Reset()
    {
        _consecutiveHuntAnalyserMisses = 0;
        _consecutiveXpAnalyserMisses = 0;
        _huntAnalyserReported = false;
        _xpAnalyserReported = false;
    }
}
