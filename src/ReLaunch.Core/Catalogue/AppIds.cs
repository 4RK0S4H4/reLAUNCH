using System.Security.Cryptography;
using System.Text;

namespace ReLaunch.Core.Catalogue;

public static class AppIds
{
    /// <summary>Stable id: first 12 hex chars of SHA-256 over the lower-cased AUMID / parsing name.</summary>
    public static string From(string parsingName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(parsingName.Trim().ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }
}
