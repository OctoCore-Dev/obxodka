using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ObxodkaWindows.Core
{
    public class UserSession
    {
        public string? Email { get; set; }
        public string? Password { get; set; }
        public string? JwtToken { get; set; }
        public bool IsLoggedIn { get; set; }

    }

    public static class AuthManager
    {
        private static readonly string _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Obxodka", "session.dat");

        private static readonly byte[] _entropy = Encoding.UTF8.GetBytes("OctoCore_Security_Salt_2026");

        public static void SaveSession(UserSession session)
        {
            try
            {
                ClearSession();

                string json = JsonSerializer.Serialize(session);
                byte[] data = Encoding.UTF8.GetBytes(json);

                byte[] encrypted = ProtectedData.Protect(data, _entropy, DataProtectionScope.CurrentUser);

                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                File.WriteAllBytes(_filePath, encrypted);
            }
            catch { }
        }

        public static UserSession LoadSession()
        {
            if (!File.Exists(_filePath)) return new UserSession { IsLoggedIn = false };

            try
            {
                byte[] encrypted = File.ReadAllBytes(_filePath);

                // Расшифровка
                byte[] decrypted = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser);

                string json = Encoding.UTF8.GetString(decrypted);
                var session = JsonSerializer.Deserialize<UserSession>(json);

                return session ?? new UserSession { IsLoggedIn = false };
            }
            catch
            {
                ClearSession();
                return new UserSession { IsLoggedIn = false };
            }
        }

        public static void ClearSession()
        {
            try { if (File.Exists(_filePath)) File.Delete(_filePath); } catch { }
        }
    }
}