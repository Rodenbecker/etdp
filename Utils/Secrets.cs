using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ETD.Api.Utils
{
    public static class Secrets
    {
        public static string? GetGoogleKey()
        {
            var env = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
                ?? Environment.GetEnvironmentVariable("GOOGLE_SEARCH_KEY");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

            try
            {
                var currentDir = Directory.GetCurrentDirectory();
                var envCandidates = new[]
                {
                    Path.Combine(currentDir, "google.env"),
                    EtdpPaths.CombineProject("google.env"),
                    EtdpPaths.CombineProject("ETDP", "google.env"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ETDP", "google.env")
                }
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var envPath in envCandidates)
                {
                    if (!File.Exists(envPath)) continue;

                    var lines = File.ReadAllLines(envPath);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("GOOGLE_SEARCH_KEY=", StringComparison.OrdinalIgnoreCase))
                        {
                            var val = trimmed.Substring(18).Trim().Trim('"').Trim('\'');
                            if (!string.IsNullOrWhiteSpace(val)) return val;
                        }
                        if (trimmed.StartsWith("GEMINI_API_KEY=", StringComparison.OrdinalIgnoreCase))
                        {
                            var val = trimmed.Substring(15).Trim().Trim('"').Trim('\'');
                            if (!string.IsNullOrWhiteSpace(val)) return val;
                        }
                        if (trimmed.StartsWith("GOOGLE_API_KEY=", StringComparison.OrdinalIgnoreCase))
                        {
                            var val = trimmed.Substring(15).Trim().Trim('"').Trim('\'');
                            if (!string.IsNullOrWhiteSpace(val)) return val;
                        }
                    }
                }

                // Check Google/googlekeys.md
                var googleKeysDoc = EtdpPaths.CombineProject("Google", "googlekeys.md");
                if (File.Exists(googleKeysDoc))
                {
                    var lines = File.ReadAllLines(googleKeysDoc);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("Google API:", StringComparison.OrdinalIgnoreCase))
                        {
                            var val = trimmed.Substring(11).Trim();
                            if (!string.IsNullOrWhiteSpace(val)) return val;
                        }
                    }
                }
            }
            catch { }

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "ETDP");
            var plainPath = Path.Combine(dir, "google.key");
            var protectedPath = Path.Combine(dir, "google.protected");
            try
            {
                if (File.Exists(plainPath))
                {
                    return File.ReadAllText(plainPath).Trim();
                }
                if (File.Exists(protectedPath))
                {
                    if (OperatingSystem.IsWindows())
                    {
                        var bytes = File.ReadAllBytes(protectedPath);
                        var unprotected = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                        return Encoding.UTF8.GetString(unprotected).Trim();
                    }
                }
            }
            catch { }

            return null;
        }

        public static string? GetGeminiKey() => GetGoogleKey();

        public static string? GetOpenAIKey()
        {
            // For full Google/Gemini migration, check Google key first or fall back to OpenAI if configured
            var env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(env)) return env;

            try
            {
                var currentDir = Directory.GetCurrentDirectory();
                var envCandidates = new[]
                {
                    Path.Combine(currentDir, ".env"),
                    EtdpPaths.CombineProject(".env"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ETDP", ".env")
                }
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var envPath in envCandidates)
                {
                    if (!File.Exists(envPath)) continue;

                    var lines = File.ReadAllLines(envPath);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("OPENAI_API_KEY=", StringComparison.OrdinalIgnoreCase))
                        {
                            var val = trimmed.Substring(15).Trim().Trim('"').Trim('\'');
                            if (!string.IsNullOrWhiteSpace(val)) return val;
                        }
                    }
                }
            }
            catch { }

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "ETDP");
            var plainPath = Path.Combine(dir, "openai.key");
            var protectedPath = Path.Combine(dir, "openai.protected");
            try
            {
                if (File.Exists(plainPath))
                {
                    return File.ReadAllText(plainPath).Trim();
                }
                if (File.Exists(protectedPath))
                {
                    if (OperatingSystem.IsWindows())
                    {
                        var bytes = File.ReadAllBytes(protectedPath);
                        var unprotected = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                        return Encoding.UTF8.GetString(unprotected).Trim();
                    }
                }
            }
            catch
            {
                // ignore
            }
            return null;
        }
    }
}
