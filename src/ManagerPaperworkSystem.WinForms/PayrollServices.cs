using System.Security.Cryptography;
using System.Text;

namespace ManagerPaperworkSystem.WinForms;

internal static class PayrollSensitiveDataProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HISAB-KITAB-WORKS-PAYROLL-SENSITIVE-V1");

    public static byte[] Protect(byte[] clear)
        => ProtectedData.Protect(clear, Entropy, DataProtectionScope.LocalMachine);

    public static byte[] Unprotect(byte[] encrypted)
        => ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.LocalMachine);

    public static byte[] ProtectText(string value)
    {
        var clear = Encoding.UTF8.GetBytes(value ?? "");
        try { return Protect(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public static string UnprotectText(byte[] encrypted)
    {
        if (encrypted is null || encrypted.Length == 0) return "";
        var clear = Unprotect(encrypted);
        try { return Encoding.UTF8.GetString(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
}
