namespace TibiaSquare.HuntMonitor.Models;

public sealed class SessionScreenshot
{
    public long Id { get; init; }
    public required string SessionId { get; init; }
    public int SessionTimeSeconds { get; init; }
    public long? RawXpPerHour { get; init; }
    public required string FilePath { get; init; }
    public bool IsUploaded { get; init; }
}
