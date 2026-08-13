using System.Security.Cryptography;
using System.Text;

namespace TarkovMonitor
{
    internal static class TokenStoreProtection
    {
        private const string Prefix = "TM-PROTECTED-V1:";
        private static readonly byte[] Entropy = SHA256.HashData(
            Encoding.UTF8.GetBytes("TarkovMonitor:TarkovTracker:settings:v1"));

        internal static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value) || value.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return value;
            }

            return Prefix + Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value),
                Entropy,
                DataProtectionScope.CurrentUser));
        }

        internal static string Unprotect(string value, out bool wasProtected)
        {
            if (string.IsNullOrEmpty(value))
            {
                wasProtected = true;
                return value;
            }

            if (!value.StartsWith(Prefix, StringComparison.Ordinal))
            {
                wasProtected = false;
                return value;
            }

            wasProtected = true;
            try
            {
                var encrypted = Convert.FromBase64String(value[Prefix.Length..]);
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    encrypted,
                    Entropy,
                    DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                throw new InvalidDataException("A protected TarkovTracker settings value could not be decrypted.", ex);
            }
        }
    }
}
