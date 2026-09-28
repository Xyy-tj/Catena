using System.Security.Cryptography;
using System.Text;
using Catena.Contracts;

namespace Catena.Platform.Windows;
public sealed class WindowsSecretProtector : ISecretProtector
{
    public string Protect(string value)
    {
        if (value.Length == 0) return "";
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("API Key 加密存储当前仅支持 Windows。");
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public string Unprotect(string value)
    {
        if (value.Length == 0) return "";
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("API Key 解密当前仅支持 Windows。");
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
