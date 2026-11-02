using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ETD.Api.Utils
{
    public static class AiRuntime
    {
        public const string ModeOffline = "offline";
        public const string ModeHybrid = "hybrid";
        public const string ModeCloud = "cloud";

        public static string GetMode()
        {
            var raw = FirstNonEmpty(
                LoadRuntimeSettings().AiMode,
                Environment.GetEnvironmentVariable("AI_MODE"))?.Trim().ToLowerInvariant() ?? string.Empty;
            if (raw == ModeHybrid || raw == ModeCloud || raw == ModeOffline)
            {
                return raw;
            }

            // Default to offline to keep ETDP independent from cloud services.
            return ModeOffline;
        }

        public static bool IsOfflineMode() => string.Equals(GetMode(), ModeOffline, StringComparison.OrdinalIgnoreCase);
        public static bool IsHybridMode() => string.Equals(GetMode(), ModeHybrid, StringComparison.OrdinalIgnoreCase);
        public static bool IsCloudMode() => string.Equals(GetMode(), ModeCloud, StringComparison.OrdinalIgnoreCase);

        public static bool AllowCloudProviders() => !IsOfflineMode();
        // Microsoft Foundry is intentionally disabled to keep ETDP independent of Azure resources.
        public static bool AllowFoundry() => false;
        public static bool AllowGoogle() => IsHybridMode() || IsCloudMode();
        public static bool AllowOpenAi() => AllowGoogle();
        public static bool PreferLocalFirst() => !IsCloudMode();

        public static string GetGeminiModel(string fallback = "gemini-3.8-flash")
        {
            return FirstNonEmpty(
                Environment.GetEnvironmentVariable("GEMINI_MODEL"),
                Environment.GetEnvironmentVariable("GOOGLE_MODEL"),
                LoadRuntimeSettings().GeminiModel,
                fallback) ?? fallback;
        }

        public static string GetGoogleApiKey()
        {
            var key = FirstNonEmpty(
                Environment.GetEnvironmentVariable("GEMINI_API_KEY"),
                Environment.GetEnvironmentVariable("GOOGLE_API_KEY"),
                Environment.GetEnvironmentVariable("GOOGLE_SEARCH_KEY"),
                Secrets.GetGoogleKey());
            return key ?? string.Empty;
        }

        public static string GetOpenAiModel(string fallback = "gemini-3.8-flash")
        {
            return GetGeminiModel(fallback);
        }

        public static string GetOpenAiApiKey()
        {
            var googleKey = GetGoogleApiKey();
            if (!string.IsNullOrWhiteSpace(googleKey)) return googleKey;

            var envKey = FirstNonEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
            if (!string.IsNullOrWhiteSpace(envKey))
            {
                return envKey;
            }

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "ETDP");
            var protectedPath = Path.Combine(dir, "openai.protected");
            if (OperatingSystem.IsWindows() && File.Exists(protectedPath))
            {
                try
                {
                    var protectedBytes = File.ReadAllBytes(protectedPath);
                    var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(bytes).Trim();
                }
                catch
                {
                    // Fall back to the plain key file below when protected storage is unavailable.
                }
            }

            var plainPath = Path.Combine(dir, "openai.key");
            if (File.Exists(plainPath))
            {
                try
                {
                    return File.ReadAllText(plainPath).Trim();
                }
                catch
                {
                    return string.Empty;
                }
            }

            return string.Empty;
        }

        public static string GetLocalLlmEndpoint()
        {
            var endpoint = FirstNonEmpty(
                Environment.GetEnvironmentVariable("LOCAL_LLM_ENDPOINT"),
                Environment.GetEnvironmentVariable("LOCAL_CHAT_COMPLETIONS_ENDPOINT"),
                Environment.GetEnvironmentVariable("PARAPHRASE_WRAPPER_ENDPOINT"),
                Environment.GetEnvironmentVariable("LOCAL_PARAPHRASE_ENDPOINT"),
                LoadRuntimeSettings().LocalLlmEndpoint);

            return endpoint ?? string.Empty;
        }

        public static IReadOnlyList<string> GetLocalLlmEndpointCandidates()
        {
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("LOCAL_LLM_ENDPOINT"),
                Environment.GetEnvironmentVariable("LOCAL_CHAT_COMPLETIONS_ENDPOINT"),
                Environment.GetEnvironmentVariable("PARAPHRASE_WRAPPER_ENDPOINT"),
                Environment.GetEnvironmentVariable("LOCAL_PARAPHRASE_ENDPOINT"),
                LoadRuntimeSettings().LocalLlmEndpoint,
                "http://127.0.0.1:8099/api/chat",
                "http://127.0.0.1:11434/api/chat",
                "http://127.0.0.1:5000/api/chat"
            };

            return candidates
                .Select(value => (value ?? string.Empty).Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string GetLocalLlmModel()
        {
            var model = FirstNonEmpty(
                Environment.GetEnvironmentVariable("LOCAL_LLM_MODEL"),
                Environment.GetEnvironmentVariable("GEMMA_MODEL"),
                Environment.GetEnvironmentVariable("PARAPHRASE_WRAPPER_MODEL"),
                LoadRuntimeSettings().LocalLlmModel);

            return string.IsNullOrWhiteSpace(model) ? "gemma4:26b" : model;
        }

        public static IReadOnlyList<string> GetLocalLlmModelCandidates()
        {
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("LOCAL_LLM_MODEL"),
                Environment.GetEnvironmentVariable("GEMMA_MODEL"),
                Environment.GetEnvironmentVariable("PARAPHRASE_WRAPPER_MODEL"),
                LoadRuntimeSettings().LocalLlmModel,
                "gemma4:26b",
                "gemma-4-26b-a4b-it",
                "gemma-4-31b-it"
            };

            return candidates
                .Select(value => (value ?? string.Empty).Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string GetLocalLlmApiKey()
        {
            return FirstNonEmpty(
                Environment.GetEnvironmentVariable("LOCAL_LLM_API_KEY"),
                Environment.GetEnvironmentVariable("PARAPHRASE_WRAPPER_API_KEY"),
                LoadRuntimeSettings().LocalLlmApiKey) ?? string.Empty;
        }

        public static string GetLocalLibraryPath()
        {
            var explicitPath = FirstNonEmpty(
                Environment.GetEnvironmentVariable("LOCAL_LIBRARY_PATH"),
                Environment.GetEnvironmentVariable("ETDP_IMPORTS_PATH"));
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                return Path.GetFullPath(explicitPath);
            }

            return EtdpPaths.GetImportsRoot();
        }

        public static RuntimeSettings LoadRuntimeSettings()
        {
            try
            {
                var path = GetRuntimeSettingsPath();
                if (!File.Exists(path))
                {
                    return new RuntimeSettings();
                }

                return JsonSerializer.Deserialize<RuntimeSettings>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new RuntimeSettings();
            }
            catch
            {
                return new RuntimeSettings();
            }
        }

        public static RuntimeSettings SaveRuntimeSettings(RuntimeSettings settings)
        {
            var normalized = new RuntimeSettings
            {
                AiMode = NormalizeMode(settings.AiMode),
                LocalLlmEndpoint = (settings.LocalLlmEndpoint ?? string.Empty).Trim(),
                LocalLlmModel = (settings.LocalLlmModel ?? string.Empty).Trim(),
                LocalLlmApiKey = (settings.LocalLlmApiKey ?? string.Empty).Trim(),
                GeminiModel = (settings.GeminiModel ?? settings.OpenAiModel ?? string.Empty).Trim(),
                OpenAiModel = (settings.GeminiModel ?? settings.OpenAiModel ?? string.Empty).Trim(),
                GoogleApiKey = (settings.GoogleApiKey ?? string.Empty).Trim(),
                UpdatedAtUtc = DateTime.UtcNow
            };

            var path = GetRuntimeSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true }));
            return normalized;
        }

        public static string GetRuntimeSettingsPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ETDP", "runtime-settings.json");
        }

        private static string NormalizeMode(string? value)
        {
            var raw = (value ?? string.Empty).Trim().ToLowerInvariant();
            return raw is ModeHybrid or ModeCloud or ModeOffline ? raw : ModeOffline;
        }

        private static string? FirstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
            {
                var normalized = (value ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    return normalized;
                }
            }

            return null;
        }

        public sealed class RuntimeSettings
        {
            public string AiMode { get; set; } = string.Empty;
            public string LocalLlmEndpoint { get; set; } = string.Empty;
            public string LocalLlmModel { get; set; } = string.Empty;
            public string LocalLlmApiKey { get; set; } = string.Empty;
            public string GeminiModel { get; set; } = "gemini-3.8-flash";
            public string OpenAiModel { get; set; } = "gemini-3.8-flash";
            public string GoogleApiKey { get; set; } = string.Empty;
            public DateTime UpdatedAtUtc { get; set; }
        }
    }
}
