namespace TibiaSquare.HuntMonitor.Models;

/// <summary>
/// Rolling experience rates displayed by Tibia's XP Analyser widget.
/// These are independent of the Hunt Analyser session timer.
/// </summary>
public sealed record XpAnalyserRates
{
    public long? XpPerHour { get; init; }
    public long? RawXpPerHour { get; init; }
    public bool HasAnyRate => XpPerHour.HasValue || RawXpPerHour.HasValue;
}
