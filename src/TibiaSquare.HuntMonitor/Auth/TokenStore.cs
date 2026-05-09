using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TibiaSquare.HuntMonitor.Auth;

public sealed class TokenStore
{
    private static readonly string TokenFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TibiaSquare", "auth.dat");

    public AuthTokens? Load()
    {
        try
        {
            if (!File.Exists(TokenFilePath))
                return null;

            var encrypted = File.ReadAllBytes(TokenFilePath);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(decrypted);
            return JsonSerializer.Deserialize<AuthTokens>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Save(AuthTokens tokens)
    {
        var json = JsonSerializer.Serialize(tokens);
        var bytes = Encoding.UTF8.GetBytes(json);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);

        var dir = Path.GetDirectoryName(TokenFilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(TokenFilePath, encrypted);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(TokenFilePath))
                File.Delete(TokenFilePath);
        }
        catch
        {
            // Best effort
        }
    }
}
