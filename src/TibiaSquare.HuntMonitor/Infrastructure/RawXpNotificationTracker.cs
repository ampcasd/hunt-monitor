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

    /// <summary>
    /// A parsed zero is still proof that the Raw XP row is visible. Only a missing
    /// nullable value means OCR did not find the row.
    /// </summary>
    internal static bool IsRawXpMissing(
        long? xpValue,
        long? rawXpValue,
        long? alternateRawXpValue = null)
    {
        return xpValue.HasValue
            && !rawXpValue.HasValue
            && !alternateRawXpValue.HasValue;
    }

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
        DiscardPendingObservations();
        _huntAnalyserReported = false;
        _xpAnalyserReported = false;
    }

    public void DiscardPendingObservations()
    {
        _consecutiveHuntAnalyserMisses = 0;
        _consecutiveXpAnalyserMisses = 0;
    }
}
