namespace TibiaSquare.HuntMonitor.Auth;

public sealed class AuthTokens
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public required long ExpiresAt { get; init; }
    public string? UserName { get; init; }

    public bool IsExpired => DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= ExpiresAt - 60;
}
