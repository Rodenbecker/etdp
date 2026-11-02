using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ETD.Api.Utils
{
    public static class GoogleGeminiClient
    {
        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

        public static async Task<string?> GenerateContentAsync(
            string prompt,
            string? model = null,
            string? systemInstruction = null,
            CancellationToken ct = default)
        {
            var apiKey = Secrets.GetGoogleKey();
            if (string.IsNullOrWhiteSpace(apiKey)) return null;

            var chosenModel = string.IsNullOrWhiteSpace(model) ? AiRuntime.GetGeminiModel("gemini-3.8-flash") : model.Trim();
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(chosenModel)}:generateContent?key={Uri.EscapeDataString(apiKey)}";

            var contents = new List<object>
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = prompt }
                    }
                }
            };

            object payload;
            if (!string.IsNullOrWhiteSpace(systemInstruction))
            {
                payload = new
                {
                    contents,
                    system_instruction = new
                    {
                        parts = new object[]
                        {
                            new { text = systemInstruction }
                        }
                    },
                    generationConfig = new
                    {
                        temperature = 0.4,
                        maxOutputTokens = 4096
                    }
                };
            }
            else
            {
                payload = new
                {
                    contents,
                    generationConfig = new
                    {
                        temperature = 0.4,
                        maxOutputTokens = 4096
                    }
                };
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            try
            {
                using var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var jsonStr = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(jsonStr);
                if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
                    candidates.GetArrayLength() > 0)
                {
                    var first = candidates[0];
                    if (first.TryGetProperty("content", out var content) &&
                        content.TryGetProperty("parts", out var parts) &&
                        parts.GetArrayLength() > 0)
                    {
                        var text = parts[0].GetProperty("text").GetString();
                        return text?.Trim();
                    }
                }
            }
            catch
            {
                // return null on network/API failure to allow safe local fallback
            }

            return null;
        }

        public static async Task<byte[]?> GenerateImageAsync(string prompt, CancellationToken ct = default)
        {
            var apiKey = Secrets.GetGoogleKey();
            if (string.IsNullOrWhiteSpace(apiKey)) return null;

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/imagen-3.0-generate-002:predict?key={Uri.EscapeDataString(apiKey)}";
            var payload = new
            {
                instances = new object[]
                {
                    new { prompt = prompt }
                },
                parameters = new
                {
                    sampleCount = 1,
                    aspectRatio = "16:9"
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            try
            {
                using var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return null;

                var jsonStr = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(jsonStr);
                if (doc.RootElement.TryGetProperty("predictions", out var predictions) &&
                    predictions.GetArrayLength() > 0)
                {
                    var first = predictions[0];
                    if (first.TryGetProperty("bytesBase64Encoded", out var b64))
                    {
                        var base64 = b64.GetString();
                        if (!string.IsNullOrWhiteSpace(base64))
                        {
                            return Convert.FromBase64String(base64);
                        }
                    }
                }
            }
            catch { }

            return null;
        }
    }
}
