using System;
using System.Security.Cryptography;
using System.Text;

namespace Atlas.Playnite
{
    /// <summary>Keeps the read token off disk in clear text (CONTRACT-C15 8.4 "token at rest").</summary>
    public interface ITokenProtector
    {
        string Protect(string token);
        /// <summary>Null when the value is empty, damaged, or was protected for another Windows user.</summary>
        string Unprotect(string stored);
    }

    /// <summary>Windows DPAPI, current user. The result is base64 and only the same Windows user on the same machine can read it.</summary>
    public sealed class DpapiTokenProtector : ITokenProtector
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("atlas-playnite-token-v1");

        public string Protect(string token)
        {
            if (string.IsNullOrEmpty(token)) return string.Empty;
            var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(blob);
        }

        public string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            try
            {
                var plain = ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (CryptographicException) { return null; }
            catch (FormatException) { return null; }
        }
    }
}
