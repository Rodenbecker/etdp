using Microsoft.AspNetCore.Mvc;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Tasks;
using ETD.Api.Data;
using ETD.Api.Models;
using System;
using System.IO;
using ETD.Api.Utils;
using ETD.Api.Services;
using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using System.Xml.Linq;

namespace ETD.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ContentController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly KnowledgeHierarchyService _knowledgeHierarchyService;
        private readonly OcrExtractionService _ocrExtractionService;
        private readonly PdfVisualExtractionService _pdfVisualExtractionService;
        private static readonly HttpClient _http = new HttpClient();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.SemaphoreSlim> QualificationFolderImportLocks = new(StringComparer.OrdinalIgnoreCase);
        private const string CurriculumBenchmarkMarker = "__CURRICULUM_BENCHMARK__";
        private const string CurriculumBenchmarkAssessmentMarker = "__WORKFLOW_CANONICAL__";
        private const string DefaultModeratorResponsesEndpoint = "";
        private static string DefaultGoogleEnvPath => EtdpPaths.CombineProject("google.env");
        private static readonly HashSet<string> GitHubDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".pdf", ".docx", ".pptx", ".csv", ".html", ".htm", ".json", ".jsonl", ".xml", ".yml", ".yaml",
            ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".svg"
        };
        private static readonly HashSet<string> GitHubCodeExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".js", ".jsx", ".ts", ".tsx", ".css", ".scss", ".less", ".cs", ".py", ".java", ".go", ".rs",
            ".c", ".cpp", ".h", ".hpp", ".sql", ".ps1", ".sh", ".bat"
        };
        private static readonly HashSet<string> KnowledgeUploadExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".docx", ".pdf", ".pptx", ".csv", ".json", ".jsonl", ".xml", ".yml", ".yaml", ".html", ".htm",
            ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".svg"
        };
        private static readonly HashSet<string> KnowledgeImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".svg"
        };
        private static readonly HashSet<string> KnowledgeTextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".csv", ".json", ".jsonl", ".xml", ".yml", ".yaml", ".html", ".htm"
        };
        private static readonly HashSet<string> SlideVisualImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff"
        };

        public ContentController(
            ApplicationDbContext context,
            KnowledgeHierarchyService knowledgeHierarchyService,
            OcrExtractionService ocrExtractionService,
            PdfVisualExtractionService pdfVisualExtractionService)
        {
            _context = context;
            _knowledgeHierarchyService = knowledgeHierarchyService;
            _ocrExtractionService = ocrExtractionService;
            _pdfVisualExtractionService = pdfVisualExtractionService;
        }

        [HttpGet("key-present")]
        public IActionResult KeyPresent()
        {
            var key = Secrets.GetOpenAIKey();
            return Ok(new { present = !string.IsNullOrWhiteSpace(key) });
        }

        [HttpGet("runtime-config")]
        public IActionResult RuntimeConfig()
        {
            var localLibraryPath = AiRuntime.GetLocalLibraryPath();
            var localLlmEndpoint = AiRuntime.GetLocalLlmEndpoint();
            var persistedSettings = AiRuntime.LoadRuntimeSettings();
            return Ok(new
            {
                aiMode = AiRuntime.GetMode(),
                offlineMode = AiRuntime.IsOfflineMode(),
                cloudProvidersEnabled = AiRuntime.AllowCloudProviders(),
                foundryEnabled = AiRuntime.AllowFoundry(),
                openAiEnabled = AiRuntime.AllowOpenAi(),
                localFirstDefault = AiRuntime.PreferLocalFirst(),
                localLibraryPath,
                localLibraryExists = Directory.Exists(localLibraryPath),
                localLlmConfigured = !string.IsNullOrWhiteSpace(localLlmEndpoint),
                localLlmEndpoint,
                localLlmModel = AiRuntime.GetLocalLlmModel(),
                openAiConfigured = AiRuntime.AllowOpenAi() && !string.IsNullOrWhiteSpace(Secrets.GetOpenAIKey()),
                openAiModel = AiRuntime.GetOpenAiModel(),
                runtimeSettingsPath = AiRuntime.GetRuntimeSettingsPath(),
                persisted = new
                {
                    aiMode = persistedSettings.AiMode,
                    localLlmEndpoint = persistedSettings.LocalLlmEndpoint,
                    localLlmModel = persistedSettings.LocalLlmModel,
                    openAiModel = persistedSettings.OpenAiModel,
                    updatedAtUtc = persistedSettings.UpdatedAtUtc
                }
            });
        }

        public class RuntimeConfigRequest
        {
            public string? AiMode { get; set; }
            public string? OpenAiApiKey { get; set; }
            public string? OpenAiModel { get; set; }
            public string? LocalLlmEndpoint { get; set; }
            public string? LocalLlmModel { get; set; }
            public string? LocalLlmApiKey { get; set; }
            public bool? ProtectOpenAiKey { get; set; }
        }

        [HttpPut("runtime-config")]
        public IActionResult SaveRuntimeConfig([FromBody] RuntimeConfigRequest req)
        {
            req ??= new RuntimeConfigRequest();
            var mode = (req.AiMode ?? string.Empty).Trim().ToLowerInvariant();
            if (mode is not ("offline" or "hybrid" or "cloud"))
            {
                return BadRequest(new { error = "AI mode must be offline, hybrid, or cloud." });
            }

            var saved = AiRuntime.SaveRuntimeSettings(new AiRuntime.RuntimeSettings
            {
                AiMode = mode,
                OpenAiModel = req.OpenAiModel ?? string.Empty,
                LocalLlmEndpoint = req.LocalLlmEndpoint ?? string.Empty,
                LocalLlmModel = req.LocalLlmModel ?? string.Empty,
                LocalLlmApiKey = req.LocalLlmApiKey ?? string.Empty
            });

            if (!string.IsNullOrWhiteSpace(req.OpenAiApiKey))
            {
                StoreOpenAiKey(req.OpenAiApiKey.Trim(), req.ProtectOpenAiKey ?? true);
            }

            return Ok(new
            {
                saved = true,
                aiMode = AiRuntime.GetMode(),
                openAiEnabled = AiRuntime.AllowOpenAi(),
                openAiConfigured = AiRuntime.AllowOpenAi() && !string.IsNullOrWhiteSpace(Secrets.GetOpenAIKey()),
                openAiModel = AiRuntime.GetOpenAiModel(),
                localLlmEndpoint = AiRuntime.GetLocalLlmEndpoint(),
                localLlmModel = AiRuntime.GetLocalLlmModel(),
                runtimeSettingsPath = AiRuntime.GetRuntimeSettingsPath(),
                updatedAtUtc = saved.UpdatedAtUtc
            });
        }

        private static string GetConfiguredImportBasePath()
        {
            return AiRuntime.GetLocalLibraryPath();
        }

        private static IEnumerable<string> GetImportBasePathCandidates()
        {
            var configured = GetConfiguredImportBasePath();
            var defaultImportRoot = EtdpPaths.CombineProject("Imports");
            return new[] { configured, defaultImportRoot }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string GetProjectRootPath()
        {
            return Path.GetFullPath(EtdpPaths.GetProjectRoot());
        }

        private static IEnumerable<string> GetAllowedImportRoots()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddRoot(string? root)
            {
                if (string.IsNullOrWhiteSpace(root)) return;
                try
                {
                    var full = Path.GetFullPath(root.Trim());
                    if (!string.IsNullOrWhiteSpace(full))
                    {
                        roots.Add(full);
                    }
                }
                catch
                {
                    // Ignore invalid candidate roots.
                }
            }

            var projectRoot = GetProjectRootPath();
            AddRoot(projectRoot);
            AddRoot(GetConfiguredImportBasePath());

            var projectParent = Directory.GetParent(projectRoot)?.FullName;
            if (!string.IsNullOrWhiteSpace(projectParent))
            {
                AddRoot(Path.Combine(projectParent, "VocationalLLM"));
            }

            var extraRoots = (Environment.GetEnvironmentVariable("ETDP_ALLOWED_IMPORT_ROOTS") ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(extraRoots))
            {
                var parts = extraRoots
                    .Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x));
                foreach (var part in parts)
                {
                    AddRoot(part);
                }
            }

            return roots;
        }

        private static bool TryResolveProjectScopedPath(string requestedPath, out string resolvedPath, out string error)
        {
            resolvedPath = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                error = "RootPath is required.";
                return false;
            }

            var projectRoot = GetProjectRootPath();
            var allowedRoots = GetAllowedImportRoots().ToList();
            var trimmed = requestedPath.Trim();
            var candidate = Path.IsPathRooted(trimmed)
                ? trimmed
                : Path.Combine(projectRoot, trimmed);

            try
            {
                resolvedPath = Path.GetFullPath(candidate);
            }
            catch
            {
                error = $"Invalid path: {requestedPath}";
                return false;
            }

            var resolvedCandidate = resolvedPath;
            if (!allowedRoots.Any(root => IsPathInsideRoot(resolvedCandidate, root)))
            {
                var rootsText = string.Join("; ", allowedRoots);
                error = $"Path must be within allowed roots: {rootsText}";
                return false;
            }

            return true;
        }

        private static bool IsPathInsideRoot(string candidatePath, string rootPath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(rootPath))
                return false;

            var normalizedCandidate = Path.GetFullPath(candidatePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedRoot = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return true;

            var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
            return normalizedCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }

        public class StoreKeyRequest
        {
            public string Key { get; set; } = string.Empty;
            public bool Protect { get; set; } = true;
        }

        [HttpPost("store-key")]
        public IActionResult StoreKey([FromBody] StoreKeyRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Key)) return BadRequest("Key required");
            StoreOpenAiKey(req.Key.Trim(), req.Protect);
            return Ok(new { saved = true });
        }

        private static void StoreOpenAiKey(string key, bool protect)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "ETDP");
            Directory.CreateDirectory(dir);
            if (protect)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(key);
                if (OperatingSystem.IsWindows())
                {
                    var protectedBytes = System.Security.Cryptography.ProtectedData.Protect(bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                    System.IO.File.WriteAllBytes(Path.Combine(dir, "openai.protected"), protectedBytes);
                }
                else
                {
                    System.IO.File.WriteAllText(Path.Combine(dir, "openai.key"), key);
                }
            }
            else
            {
                System.IO.File.WriteAllText(Path.Combine(dir, "openai.key"), key);
            }
        }

        public class SearchRequest
        {
            public string? Query { get; set; }
            public string? QualificationCode { get; set; }
            public string? QualificationDescription { get; set; }
            public string? SubjectName { get; set; }
            public string? SubjectDescription { get; set; }
            public string? TopicDescription { get; set; }
            public string? TopicPurpose { get; set; }
            public string? LessonPlanDescription { get; set; }
            public string? AssessmentCriteriaDescription { get; set; }
            public string? KnowledgeSourceType { get; set; }
            public string? Provider { get; set; } // google | wikipedia | searx | openai
            public bool? UseOpenAI { get; set; }
        }

        public class LocalSearchRequest
        {
            public string? Query { get; set; }
            public int Limit { get; set; } = 20;
            public int SnippetLength { get; set; } = 360;
            public string? QualificationCode { get; set; }
            public string? QualificationDescription { get; set; }
            public string? SubjectDescription { get; set; }
            public string? SubjectCode { get; set; }
            public string? TopicDescription { get; set; }
            public string? AssessmentCriteriaDescription { get; set; }
            public string? KnowledgePool { get; set; }
            public string? KnowledgeSourceType { get; set; }
            public bool RemoveBoilerplate { get; set; } = true;
        }

        public class ParagraphSearchRequest
        {
            public string? Query { get; set; }
            public int Limit { get; set; } = 20;
            public int SnippetLength { get; set; } = 420;
            public string? QualificationCode { get; set; }
            public string? QualificationDescription { get; set; }
            public string? SubjectDescription { get; set; }
            public string? SubjectCode { get; set; }
            public string? TopicDescription { get; set; }
            public string? AssessmentCriteriaDescription { get; set; }
            public string? KnowledgePool { get; set; }
            public string? KnowledgeSourceType { get; set; }
            public bool RemoveBoilerplate { get; set; } = true;
        }

        public class AutoMapSourcesRequest
        {
            public string? QualificationCode { get; set; }
            public string? QualificationDescription { get; set; }
            public string? SubjectCode { get; set; }
            public string? SubjectDescription { get; set; }
            public string? TopicDescription { get; set; }
            public string? AssessmentCriteriaDescription { get; set; }
            public int Limit { get; set; } = 20;
            public int SnippetLength { get; set; } = 420;
            public bool IncludeDeveloperKnowledgeBase { get; set; } = true;
            public bool IncludeLocalUploads { get; set; } = true;
            public bool IncludeOtherLocalPools { get; set; } = true;
            public bool RemoveBoilerplate { get; set; } = true;
        }

        public class KnowledgeFlatExportRequest
        {
            public int? QualificationId { get; set; }
            public string? QualificationCode { get; set; }
            public string? QualificationDescription { get; set; }
            public string? KnowledgePool { get; set; }
            public int MaxMaterials { get; set; } = 2000;
            public int MaxParagraphs { get; set; } = 25000;
            public bool RemoveBoilerplate { get; set; } = true;
        }

        private sealed class UnifiedSearchResult
        {
            public string Title { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public string Snippet { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public double Score { get; set; }
        }

        private sealed class LocalParagraphSearchHit
        {
            public int MaterialId { get; set; }
            public string Title { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public string Snippet { get; set; } = string.Empty;
            public int Score { get; set; }
            public int ParagraphIndex { get; set; }
            public string KnowledgePool { get; set; } = string.Empty;
            public string KnowledgeSourceType { get; set; } = string.Empty;
            public int? KnowledgeNumber { get; set; }
            public string QualificationCode { get; set; } = string.Empty;
        }

        private sealed class FlatParagraphRecord
        {
            public string Id { get; set; } = string.Empty;
            public int MaterialId { get; set; }
            public int ParagraphIndex { get; set; }
            public string Title { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public string KnowledgePool { get; set; } = string.Empty;
            public string KnowledgeSourceType { get; set; } = string.Empty;
            public int? KnowledgeNumber { get; set; }
            public string QualificationCode { get; set; } = string.Empty;
            public string QualificationDescription { get; set; } = string.Empty;
            public string SubjectDescription { get; set; } = string.Empty;
            public string TopicDescription { get; set; } = string.Empty;
            public string AssessmentCriteriaDescription { get; set; } = string.Empty;
            public int Priority { get; set; }
            public string Text { get; set; } = string.Empty;
        }

        [HttpPost("search")]
        public async Task<IActionResult> Search([FromBody] SearchRequest req)
        {
            var provider = (req.Provider ?? Environment.GetEnvironmentVariable("SEARCH_PROVIDER") ?? "google").ToLowerInvariant();
            var query = string.IsNullOrWhiteSpace(req.Query)
                ? string.Join(" ", new[] { req.QualificationCode, req.QualificationDescription, req.SubjectName, req.SubjectDescription, req.TopicDescription, req.TopicPurpose, req.LessonPlanDescription, req.AssessmentCriteriaDescription })
                : req.Query;
            query = Regex.Replace(query ?? string.Empty, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(query)) return BadRequest("Query is empty");

            var aiMode = AiRuntime.GetMode();
            var offlineMode = AiRuntime.IsOfflineMode();

            if (provider == "none")
            {
                return Ok(new { provider = "none", query, results = new List<object>(), aiMode, warning = "Web provider disabled." });
            }

            if (offlineMode)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    error = "Cloud provider search is disabled in offline mode. Use /api/Content/search-local.",
                    aiMode
                });
            }

            if (provider == "google")
            {
                var key = GetGoogleKey();
                var cx = GetGoogleCx();
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(cx))
                {
                    var fallback = await BuildSearchFallbackAsync(
                        query,
                        $"Google search not configured. Set GOOGLE_SEARCH_KEY and GOOGLE_SEARCH_CX, or add both to {DefaultGoogleEnvPath}.");
                    return Ok(new
                    {
                        provider = fallback.provider,
                        requestedProvider = "google",
                        query,
                        results = fallback.results,
                        warning = fallback.warning
                    });
                }
                var url = $"https://www.googleapis.com/customsearch/v1?key={Uri.EscapeDataString(key)}&cx={Uri.EscapeDataString(cx)}&q={Uri.EscapeDataString(query)}";
                using var reqMsg = new HttpRequestMessage(HttpMethod.Get, url);
                reqMsg.Headers.UserAgent.Add(new ProductInfoHeaderValue("ETDP", "1.0"));
                reqMsg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var resp = await _http.SendAsync(reqMsg);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    var fallback = await BuildSearchFallbackAsync(
                        query,
                        $"Google search failed: HTTP {(int)resp.StatusCode}.");
                    return Ok(new
                    {
                        provider = fallback.provider,
                        requestedProvider = "google",
                        query,
                        results = fallback.results,
                        warning = fallback.warning
                    });
                }
                using var doc = JsonDocument.Parse(body);
                var items = doc.RootElement.TryGetProperty("items", out var arr) ? arr : default;
                var results = new System.Collections.Generic.List<object>();
                if (items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in items.EnumerateArray())
                    {
                        results.Add(new
                        {
                            title = it.TryGetProperty("title", out var t) ? t.GetString() : "",
                            url = it.TryGetProperty("link", out var l) ? l.GetString() : "",
                            snippet = it.TryGetProperty("snippet", out var s) ? s.GetString() : ""
                        });
                    }
                }
                if (results.Count == 0)
                {
                    var fallback = await BuildSearchFallbackAsync(query, "Google search returned no results.");
                    return Ok(new
                    {
                        provider = fallback.provider,
                        requestedProvider = "google",
                        query,
                        results = fallback.results,
                        warning = fallback.warning
                    });
                }
                return Ok(new { provider = "google", query, results });
            }
            else if (provider == "bing")
            {
                return BadRequest("Bing search is no longer available in Engine.");
            }
            else if (provider == "wikipedia")
            {
                var url = $"https://en.wikipedia.org/w/api.php?action=query&list=search&srsearch={Uri.EscapeDataString(query)}&utf8=&format=json&srlimit=10";
                using var reqMsg = new HttpRequestMessage(HttpMethod.Get, url);
                reqMsg.Headers.UserAgent.Add(new ProductInfoHeaderValue("ETDP", "1.0"));
                reqMsg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                reqMsg.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));
                var resp = await _http.SendAsync(reqMsg);
                if (!resp.IsSuccessStatusCode)
                    return StatusCode((int)resp.StatusCode, $"Wikipedia request failed: {resp.StatusCode}");
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var queryObj = doc.RootElement.TryGetProperty("query", out var q) ? q : default;
                var searchArr = queryObj.ValueKind == JsonValueKind.Object && queryObj.TryGetProperty("search", out var s) ? s : default;
                var results = new System.Collections.Generic.List<object>();
                if (searchArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in searchArr.EnumerateArray())
                    {
                        var title = it.TryGetProperty("title", out var t) ? t.GetString() : "";
                        var pageid = it.TryGetProperty("pageid", out var pid) ? pid.GetInt32() : 0;
                        var snippet = it.TryGetProperty("snippet", out var sn) ? sn.GetString() : "";
                        results.Add(new
                        {
                            title,
                            url = $"https://en.wikipedia.org/?curid={pageid}",
                            snippet = Regex.Replace(snippet ?? "", "<[^>]+>", " ")
                        });
                    }
                }
                return Ok(new { provider = "wikipedia", query, results });
            }
            else if (provider == "searx")
            {
                var searx = GetSearxUrl();
                if (string.IsNullOrEmpty(searx)) return BadRequest("Searx URL not configured");
                var url = $"{searx.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&format=json";
                using var reqMsg = new HttpRequestMessage(HttpMethod.Get, url);
                reqMsg.Headers.UserAgent.Add(new ProductInfoHeaderValue("ETDP", "1.0"));
                reqMsg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var resp = await _http.SendAsync(reqMsg);
                if (!resp.IsSuccessStatusCode)
                    return StatusCode((int)resp.StatusCode, $"Searx request failed: {resp.StatusCode}");
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var resultsArr = doc.RootElement.TryGetProperty("results", out var arr) ? arr : default;
                var results = new System.Collections.Generic.List<object>();
                if (resultsArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in resultsArr.EnumerateArray())
                    {
                        results.Add(new
                        {
                            title = it.TryGetProperty("title", out var t) ? t.GetString() : "",
                            url = it.TryGetProperty("url", out var l) ? l.GetString() : "",
                            snippet = it.TryGetProperty("content", out var s) ? s.GetString() : ""
                        });
                    }
                }
                return Ok(new { provider = "searx", query, results });
            }
            else if (provider == "openaip" || provider == "figshare" || provider == "oai")
            {
                return StatusCode(StatusCodes.Status403Forbidden, "OpenAIP/Figshare source is disabled by policy.");
            }
            else if (provider == "openai" || (req.UseOpenAI ?? false))
            {
                if (!AiRuntime.AllowOpenAi())
                {
                    return StatusCode(StatusCodes.Status403Forbidden, "OpenAI search is disabled by AI_MODE.");
                }
                var key = ETD.Api.Utils.Secrets.GetOpenAIKey();
                if (string.IsNullOrWhiteSpace(key))
                {
                    var fallback = await BuildSearchFallbackAsync(query, "OpenAI key not stored. Falling back to alternate internet providers.");
                    return Ok(new
                    {
                        provider = fallback.provider,
                        requestedProvider = "openai",
                        query,
                        results = fallback.results,
                        warning = fallback.warning
                    });
                }
                var systemPrompt = "You are a search assistant. Given a query, produce up to 5 relevant resource suggestions with a title and 1-2 sentence snippet. Return plain text lines.";
                var payload = new
                {
                    model = "gpt-4o-mini",
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = $"Query: {query}\nReturn up to 5 items as lines: Title — Snippet" }
                    },
                    temperature = 0.2
                };
                using var msg = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                msg.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
                var resp = await _http.SendAsync(msg);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    var fallback = await BuildSearchFallbackAsync(query, $"OpenAI search failed: HTTP {(int)resp.StatusCode}. Falling back to alternate internet providers.");
                    return Ok(new
                    {
                        provider = fallback.provider,
                        requestedProvider = "openai",
                        query,
                        results = fallback.results,
                        warning = fallback.warning
                    });
                }
                using var doc = JsonDocument.Parse(body);
                var choices = doc.RootElement.TryGetProperty("choices", out var ch) ? ch : default;
                var text = "";
                if (choices.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in choices.EnumerateArray())
                    {
                        var m = c.TryGetProperty("message", out var mm) ? mm : default;
                        text = m.TryGetProperty("content", out var cc) ? cc.GetString() ?? "" : "";
                        if (!string.IsNullOrWhiteSpace(text)) break;
                    }
                }
                var lines = (text ?? "").Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith("- ")).Take(5).ToList();
                var results = new System.Collections.Generic.List<object>();
                foreach (var line in lines)
                {
                    var parts = line.Split(new[] { "—", "-" }, 2, StringSplitOptions.RemoveEmptyEntries);
                    var title = parts.Length > 0 ? parts[0].Trim() : line;
                    var snippet = parts.Length > 1 ? parts[1].Trim() : "";
                    results.Add(new { title, url = "", snippet });
                }
                if (results.Count == 0)
                {
                    var fallback = await BuildSearchFallbackAsync(query, "OpenAI search returned no results.");
                    return Ok(new
                    {
                        provider = fallback.provider,
                        requestedProvider = "openai",
                        query,
                        results = fallback.results,
                        warning = fallback.warning
                    });
                }
                return Ok(new { provider = "openai", query, results });
            }
            else
            {
                return BadRequest("Unknown provider");
            }
        }

        [HttpPost("search-azure")]
        [HttpPost("search-unified")]
        public async Task<IActionResult> SearchAzure([FromBody] SearchRequest req)
        {
            var query = string.IsNullOrWhiteSpace(req?.Query)
                ? string.Join(" ", new[] { req?.QualificationCode, req?.QualificationDescription, req?.SubjectName, req?.SubjectDescription, req?.TopicDescription, req?.TopicPurpose, req?.LessonPlanDescription, req?.AssessmentCriteriaDescription })
                : req!.Query;
            query = Regex.Replace(query ?? string.Empty, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(query)) return BadRequest("Query is empty");

            var aiMode = AiRuntime.GetMode();
            var cloudEnabled = AiRuntime.AllowCloudProviders();
            var warnings = new List<string>();
            var sourcesAttempted = new List<string>();
            var combined = new List<UnifiedSearchResult>();

            try
            {
                var local = BuildUnifiedLocalResults(query, req, limit: 8);
                if (local.Count > 0)
                {
                    combined.AddRange(local);
                }
                sourcesAttempted.Add("local_materials");
            }
            catch (Exception ex)
            {
                warnings.Add($"Local search failed: {ex.Message}");
            }

            if (cloudEnabled)
            {
                warnings.Add("Microsoft/Azure cloud search providers are disabled by policy; using local and non-Microsoft sources.");
                var googleKey = GetGoogleKey();
                var googleCx = GetGoogleCx();
                if (!string.IsNullOrWhiteSpace(googleKey) && !string.IsNullOrWhiteSpace(googleCx))
                {
                    try
                    {
                        var google = await SearchGoogleUnifiedAsync(query, googleKey, googleCx, top: 6);
                        combined.AddRange(google);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Google search failed: {ex.Message}");
                    }
                    sourcesAttempted.Add("google_custom");
                }
                else
                {
                    warnings.Add("Google search skipped (missing GOOGLE_SEARCH_KEY or GOOGLE_SEARCH_CX).");
                }

                if (GetOpenAipSearchEnabled())
                {
                    try
                    {
                        var openAip = await SearchOpenAipUnifiedAsync(query, top: 6);
                        combined.AddRange(openAip);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"OpenAIP search failed: {ex.Message}");
                    }
                    sourcesAttempted.Add("open_aip_figshare");
                }

                try
                {
                    var wikipedia = await SearchWikipediaUnifiedAsync(query, top: 6);
                    combined.AddRange(wikipedia);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Wikipedia search failed: {ex.Message}");
                }
                sourcesAttempted.Add("wikipedia");
            }
            else
            {
                warnings.Add("Offline mode active: cloud search sources skipped.");
            }

            var deduped = DeduplicateAndSort(combined, maxResults: 20);
            return Ok(new
            {
                provider = cloudEnabled ? "unified-search" : "local-only",
                aiMode,
                query,
                results = deduped.Select(r => new
                {
                    title = r.Title,
                    url = r.Url,
                    snippet = r.Snippet,
                    source = r.Source,
                    score = Math.Round(r.Score, 3)
                }).ToList(),
                sourcesAttempted,
                warnings
            });
        }

        [HttpPost("search-local")]
        public IActionResult SearchLocal([FromBody] LocalSearchRequest? req)
        {
            if (req == null) return BadRequest("Request body is required.");
            var request = req;
            var query = Regex.Replace(request.Query ?? string.Empty, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(query)) return BadRequest("Query is empty");

            var resolvedQualificationCode = (request.QualificationCode ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(resolvedQualificationCode))
            {
                resolvedQualificationCode = ResolveQualificationCode(request.QualificationDescription);
            }
            if (string.IsNullOrWhiteSpace(resolvedQualificationCode))
            {
                return BadRequest("QualificationCode (or resolvable QualificationDescription) is required for curriculum-scoped library search.");
            }
            request.QualificationCode = resolvedQualificationCode;

            TryAutoSyncKnowledgeHierarchy(request.QualificationCode, request.QualificationDescription);

            var limit = request.Limit;
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100;
            var snippetLength = request.SnippetLength;
            if (snippetLength < 120) snippetLength = 120;
            if (snippetLength > 1200) snippetLength = 1200;

            var hits = BuildLocalParagraphHits(query, request, limit, snippetLength);
            var results = hits
                .Select(x => new
                {
                    materialId = x.MaterialId,
                    title = x.Title,
                    snippet = x.Snippet,
                    score = x.Score,
                    paragraphIndex = x.ParagraphIndex,
                    knowledgePool = x.KnowledgePool,
                    knowledgeSourceType = x.KnowledgeSourceType,
                    knowledgeNumber = x.KnowledgeNumber,
                    qualificationCode = x.QualificationCode,
                    url = x.Url
                })
                .ToList();

            return Ok(new { query, results });
        }

        [HttpPost("search-paragraphs")]
        public IActionResult SearchParagraphs([FromBody] ParagraphSearchRequest req)
        {
            var localReq = new LocalSearchRequest
            {
                Query = req?.Query,
                Limit = req?.Limit ?? 20,
                SnippetLength = req?.SnippetLength ?? 420,
                QualificationCode = req?.QualificationCode,
                QualificationDescription = req?.QualificationDescription,
                SubjectDescription = req?.SubjectDescription,
                SubjectCode = req?.SubjectCode,
                TopicDescription = req?.TopicDescription,
                AssessmentCriteriaDescription = req?.AssessmentCriteriaDescription,
                KnowledgePool = req?.KnowledgePool,
                KnowledgeSourceType = req?.KnowledgeSourceType,
                RemoveBoilerplate = req?.RemoveBoilerplate ?? true
            };
            return SearchLocal(localReq);
        }

        [HttpPost("auto-map-sources")]
        public IActionResult AutoMapSources([FromBody] AutoMapSourcesRequest? req)
        {
            if (req == null) return BadRequest("Request body is required.");
            var request = req;
            var query = BuildAutoMapQuery(request);
            if (string.IsNullOrWhiteSpace(query))
            {
                return BadRequest("Auto-map requires at least one context field (subject/topic/criteria/qualification).");
            }

            var resolvedQualificationCode = (request.QualificationCode ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(resolvedQualificationCode))
            {
                resolvedQualificationCode = ResolveQualificationCode(request.QualificationDescription);
            }
            if (string.IsNullOrWhiteSpace(resolvedQualificationCode))
            {
                return BadRequest("QualificationCode (or resolvable QualificationDescription) is required for curriculum-scoped auto-mapping.");
            }
            request.QualificationCode = resolvedQualificationCode;

            TryAutoSyncKnowledgeHierarchy(request.QualificationCode, request.QualificationDescription);

            var limit = request.Limit;
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100;

            var snippetLength = request.SnippetLength;
            if (snippetLength < 120) snippetLength = 120;
            if (snippetLength > 1500) snippetLength = 1500;

            var baseReq = new LocalSearchRequest
            {
                Query = query,
                Limit = limit,
                SnippetLength = snippetLength,
                QualificationCode = request.QualificationCode,
                QualificationDescription = request.QualificationDescription,
                SubjectCode = request.SubjectCode,
                SubjectDescription = request.SubjectDescription,
                TopicDescription = request.TopicDescription,
                AssessmentCriteriaDescription = request.AssessmentCriteriaDescription,
                RemoveBoilerplate = request.RemoveBoilerplate
            };

            var combined = new List<LocalParagraphSearchHit>();
            if (request.IncludeDeveloperKnowledgeBase)
            {
                var developerReq = CloneForSourceType(baseReq, "developer_knowledge_base", limit);
                combined.AddRange(BuildLocalParagraphHits(query, developerReq, developerReq.Limit, snippetLength));
            }

            var remainingAfterDeveloper = Math.Max(0, limit - combined.Count);
            if (request.IncludeLocalUploads && remainingAfterDeveloper > 0)
            {
                var uploadReq = CloneForSourceType(baseReq, "local_source_upload", remainingAfterDeveloper);
                combined.AddRange(BuildLocalParagraphHits(query, uploadReq, uploadReq.Limit, snippetLength));
            }

            var remainingAfterPrimary = Math.Max(0, limit - combined.Count);
            if (request.IncludeOtherLocalPools && remainingAfterPrimary > 0)
            {
                var fallbackReq = CloneForSourceType(baseReq, null, remainingAfterPrimary);
                combined.AddRange(BuildLocalParagraphHits(query, fallbackReq, fallbackReq.Limit, snippetLength));
            }

            var hits = DeduplicateLocalHits(combined)
                .OrderByDescending(x => x.Score)
                .Take(limit)
                .ToList();

            var results = hits
                .Select(x => new
                {
                    materialId = x.MaterialId,
                    title = x.Title,
                    snippet = x.Snippet,
                    score = x.Score,
                    paragraphIndex = x.ParagraphIndex,
                    knowledgePool = x.KnowledgePool,
                    knowledgeSourceType = x.KnowledgeSourceType,
                    knowledgeNumber = x.KnowledgeNumber,
                    qualificationCode = x.QualificationCode,
                    url = x.Url
                })
                .ToList();

            var sourceTypeSummary = hits
                .GroupBy(h => string.IsNullOrWhiteSpace(h.KnowledgeSourceType) ? "unknown" : h.KnowledgeSourceType)
                .Select(g => new { knowledgeSourceType = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count)
                .ToList();

            return Ok(new
            {
                query,
                results,
                summary = new
                {
                    total = results.Count,
                    developerKnowledgeBase = hits.Count(h => string.Equals(h.KnowledgeSourceType, "developer_knowledge_base", StringComparison.OrdinalIgnoreCase)),
                    localSourceUpload = hits.Count(h => string.Equals(h.KnowledgeSourceType, "local_source_upload", StringComparison.OrdinalIgnoreCase)),
                    other = hits.Count(h =>
                        !string.Equals(h.KnowledgeSourceType, "developer_knowledge_base", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(h.KnowledgeSourceType, "local_source_upload", StringComparison.OrdinalIgnoreCase)),
                    sourceTypes = sourceTypeSummary
                }
            });
        }

        private static string BuildAutoMapQuery(AutoMapSourcesRequest? req)
        {
            var parts = new List<string>();

            void Add(string? value)
            {
                var cleaned = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    parts.Add(cleaned);
                }
            }

            Add(req?.QualificationCode);
            Add(req?.QualificationDescription);
            Add(req?.SubjectCode);
            Add(req?.SubjectDescription);
            Add(req?.TopicDescription);
            Add(req?.AssessmentCriteriaDescription);

            return Regex.Replace(string.Join(" ", parts), @"\s+", " ").Trim();
        }

        private static LocalSearchRequest CloneForSourceType(LocalSearchRequest source, string? knowledgeSourceType, int limit)
        {
            return new LocalSearchRequest
            {
                Query = source.Query,
                Limit = Math.Max(1, limit),
                SnippetLength = source.SnippetLength,
                QualificationCode = source.QualificationCode,
                QualificationDescription = source.QualificationDescription,
                SubjectDescription = source.SubjectDescription,
                SubjectCode = source.SubjectCode,
                TopicDescription = source.TopicDescription,
                AssessmentCriteriaDescription = source.AssessmentCriteriaDescription,
                KnowledgePool = source.KnowledgePool,
                KnowledgeSourceType = knowledgeSourceType,
                RemoveBoilerplate = source.RemoveBoilerplate
            };
        }

        private static List<LocalParagraphSearchHit> DeduplicateLocalHits(IEnumerable<LocalParagraphSearchHit> hits)
        {
            var map = new Dictionary<string, LocalParagraphSearchHit>(StringComparer.OrdinalIgnoreCase);
            foreach (var hit in hits ?? Enumerable.Empty<LocalParagraphSearchHit>())
            {
                if (hit == null) continue;
                var key = $"{hit.MaterialId}|{hit.ParagraphIndex}|{(hit.Url ?? string.Empty).Trim().ToLowerInvariant()}";
                if (map.TryGetValue(key, out var existing))
                {
                    if (hit.Score > existing.Score)
                    {
                        map[key] = hit;
                    }
                    continue;
                }
                map[key] = hit;
            }
            return map.Values.ToList();
        }

        [HttpGet("knowledge-pools")]
        public IActionResult KnowledgePools([FromQuery] int? qualificationId = null, [FromQuery] string? qualificationCode = null, [FromQuery] string? qualificationDescription = null)
        {
            var resolvedQualificationCode = (qualificationCode ?? string.Empty).Trim();
            var resolvedQualificationDescription = (qualificationDescription ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(resolvedQualificationCode))
            {
                resolvedQualificationCode = ResolveQualificationCode(resolvedQualificationDescription, qualificationId);
            }
            if (string.IsNullOrWhiteSpace(resolvedQualificationCode))
            {
                return BadRequest("QualificationId or QualificationCode is required for curriculum-scoped library content.");
            }
            if (string.IsNullOrWhiteSpace(resolvedQualificationDescription))
            {
                var q = qualificationId.HasValue && qualificationId.Value > 0
                    ? _context.Qualifications.Find(qualificationId.Value)
                    : _context.Qualifications.FirstOrDefault(x => x.QualificationNumber == resolvedQualificationCode);
                if (q != null)
                {
                    resolvedQualificationDescription = q.QualificationDescription ?? string.Empty;
                }
            }

            var materials = _context.SourceMaterials
                .Where(s => (s.QualificationCode ?? string.Empty) == resolvedQualificationCode)
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => new
                {
                    s.Id,
                    s.Title,
                    s.SubjectDescription,
                    s.Url,
                    s.QualificationCode,
                    s.KnowledgeSourceType,
                    s.KnowledgeNumber,
                    s.CreatedAt
                })
                .ToList();

            var pools = materials
                .GroupBy(m => ResolveKnowledgePool(m.SubjectDescription, m.Url))
                .Select(g => new
                {
                    pool = g.Key,
                    count = g.Count(),
                    latestCreatedAtUtc = g.Max(x => x.CreatedAt),
                    sampleTitles = g.Select(x => x.Title).Where(x => !string.IsNullOrWhiteSpace(x)).Take(3).ToList()
                })
                .OrderByDescending(x => x.count)
                .ToList();

            var sourceTypes = materials
                .GroupBy(m => NormalizeKnowledgeSourceType(m.KnowledgeSourceType))
                .Select(g => new
                {
                    sourceType = string.IsNullOrWhiteSpace(g.Key) ? "local_source_upload" : g.Key,
                    count = g.Count(),
                    latestCreatedAtUtc = g.Max(x => x.CreatedAt)
                })
                .OrderByDescending(x => x.count)
                .ToList();

            return Ok(new
            {
                qualificationCode = resolvedQualificationCode,
                qualificationDescription = resolvedQualificationDescription,
                totalMaterials = materials.Count,
                pools,
                sourceTypes,
                supportedPoolFilters = new[]
                {
                    "local_any",
                    "local_upload",
                    "developer_knowledge",
                    "local_folder",
                    "github_repo",
                    "oai_pmh",
                    "open_aip",
                    "engineering_seed",
                    "web_import",
                    "all"
                }
            });
        }

        [HttpPost("export-knowledge-flat")]
        public IActionResult ExportKnowledgeFlat([FromBody] KnowledgeFlatExportRequest req)
        {
            var resolvedQualificationCode = (req?.QualificationCode ?? string.Empty).Trim();
            var resolvedQualificationDescription = (req?.QualificationDescription ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(resolvedQualificationCode))
            {
                resolvedQualificationCode = ResolveQualificationCode(resolvedQualificationDescription, req?.QualificationId);
            }
            if (string.IsNullOrWhiteSpace(resolvedQualificationCode))
            {
                return BadRequest("QualificationId or QualificationCode is required for curriculum-scoped library export.");
            }

            var maxMaterials = req?.MaxMaterials ?? 2000;
            if (maxMaterials < 1) maxMaterials = 1;
            if (maxMaterials > 10000) maxMaterials = 10000;

            var maxParagraphs = req?.MaxParagraphs ?? 25000;
            if (maxParagraphs < 100) maxParagraphs = 100;
            if (maxParagraphs > 200000) maxParagraphs = 200000;

            var requestedPool = NormalizeKnowledgePool(req?.KnowledgePool);
            var removeBoilerplate = req?.RemoveBoilerplate ?? true;

            var materials = _context.SourceMaterials
                .Where(s => (s.QualificationCode ?? string.Empty) == resolvedQualificationCode)
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => new
                {
                    s.Id,
                    s.Title,
                    s.Url,
                    s.ExtractedText,
                    s.QualificationCode,
                    s.QualificationDescription,
                    s.SubjectDescription,
                    s.TopicDescription,
                    s.AssessmentCriteriaDescription,
                    s.KnowledgeSourceType,
                    s.KnowledgeNumber
                })
                .Take(maxMaterials)
                .ToList();

            var rows = new List<FlatParagraphRecord>();
            foreach (var material in materials)
            {
                var pool = ResolveKnowledgePool(material.SubjectDescription, material.Url);
                if (!MatchesKnowledgePool(pool, requestedPool)) continue;

                var paragraphs = SplitIntoSearchParagraphs(material.ExtractedText ?? string.Empty, removeBoilerplate);
                var paragraphIndex = 0;
                foreach (var paragraph in paragraphs)
                {
                    paragraphIndex++;
                    var text = Regex.Replace(paragraph ?? "", @"\s+", " ").Trim();
                    if (text.Length < 50) continue;

                    rows.Add(new FlatParagraphRecord
                    {
                        Id = $"{material.Id}:{paragraphIndex}",
                        MaterialId = material.Id,
                        ParagraphIndex = paragraphIndex,
                        Title = material.Title ?? string.Empty,
                        Url = material.Url ?? string.Empty,
                        KnowledgePool = pool,
                        KnowledgeSourceType = NormalizeKnowledgeSourceType(material.KnowledgeSourceType),
                        KnowledgeNumber = material.KnowledgeNumber,
                        QualificationCode = material.QualificationCode ?? string.Empty,
                        QualificationDescription = material.QualificationDescription ?? string.Empty,
                        SubjectDescription = material.SubjectDescription ?? string.Empty,
                        TopicDescription = material.TopicDescription ?? string.Empty,
                        AssessmentCriteriaDescription = material.AssessmentCriteriaDescription ?? string.Empty,
                        Priority = ComputeKnowledgeParagraphPriority(
                            text,
                            material.Title ?? string.Empty,
                            pool,
                            material.TopicDescription ?? string.Empty,
                            material.AssessmentCriteriaDescription ?? string.Empty),
                        Text = text
                    });
                }
            }

            var prioritized = rows
                .OrderByDescending(x => x.Priority)
                .ThenByDescending(x => x.MaterialId)
                .Take(maxParagraphs)
                .ToList();

            var outDir = Path.Combine(@"C:\ETDP\ETDP", "Exports", "KnowledgeFlat");
            Directory.CreateDirectory(outDir);
            var poolTag = string.IsNullOrWhiteSpace(requestedPool) ? "all" : requestedPool;
            var exportFileName = $"knowledge_flat_{poolTag}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.jsonl";
            var outPath = Path.Combine(outDir, exportFileName);
            using (var writer = new StreamWriter(outPath, false, System.Text.Encoding.UTF8))
            {
                foreach (var row in prioritized)
                {
                    writer.WriteLine(JsonSerializer.Serialize(row));
                }
            }

            string? myDocumentsPath = null;
            try
            {
                var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var qualificationFolder = MakeSafeFilePart(resolvedQualificationCode, "Qualification");
                var myDocumentsExportDir = Path.Combine(myDocuments, qualificationFolder, "KnowledgeFlatExports");
                Directory.CreateDirectory(myDocumentsExportDir);
                myDocumentsPath = Path.Combine(myDocumentsExportDir, exportFileName);
                System.IO.File.Copy(outPath, myDocumentsPath, true);
            }
            catch
            {
                myDocumentsPath = null;
            }

            return Ok(new
            {
                path = outPath,
                myDocumentsPath,
                qualificationCode = resolvedQualificationCode,
                qualificationDescription = resolvedQualificationDescription,
                pool = string.IsNullOrWhiteSpace(requestedPool) ? "all" : requestedPool,
                scannedMaterials = materials.Count,
                exportedParagraphs = prioritized.Count,
                removeBoilerplate
            });
        }

        private List<UnifiedSearchResult> BuildUnifiedLocalResults(string query, SearchRequest? req, int limit)
        {
            var localReq = new LocalSearchRequest
            {
                Query = query,
                Limit = Math.Max(1, limit * 4),
                SnippetLength = 340,
                QualificationCode = req?.QualificationCode,
                QualificationDescription = req?.QualificationDescription,
                SubjectDescription = req?.SubjectDescription,
                SubjectCode = req?.SubjectName,
                TopicDescription = req?.TopicDescription,
                AssessmentCriteriaDescription = req?.AssessmentCriteriaDescription,
                KnowledgeSourceType = req?.KnowledgeSourceType,
                RemoveBoilerplate = true
            };

            var hits = BuildLocalParagraphHits(query, localReq, localReq.Limit, localReq.SnippetLength);
            return hits
                .Select(x => new UnifiedSearchResult
                {
                    Title = string.IsNullOrWhiteSpace(x.Title) ? "Local material" : x.Title,
                    Url = x.Url ?? string.Empty,
                    Snippet = x.Snippet,
                    Source = string.IsNullOrWhiteSpace(x.KnowledgeSourceType)
                        ? $"local_{x.KnowledgePool}"
                        : $"local_{x.KnowledgePool}_{x.KnowledgeSourceType}",
                    Score = x.Score + 15 + (string.Equals(x.KnowledgeSourceType, "developer_knowledge_base", StringComparison.OrdinalIgnoreCase) ? 6 : 0)
                })
                .OrderByDescending(x => x.Score)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        private List<LocalParagraphSearchHit> BuildLocalParagraphHits(string query, LocalSearchRequest req, int limit, int snippetLength)
        {
            var terms = TokenizeQuery(query);
            if (terms.Count == 0) terms.Add(query.ToLowerInvariant());

            var resolvedQualificationCode = (req?.QualificationCode ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(resolvedQualificationCode))
            {
                resolvedQualificationCode = ResolveQualificationCode(req?.QualificationDescription);
            }

            var ctxQualificationCode = NormalizeForMatch(resolvedQualificationCode);
            if (string.IsNullOrWhiteSpace(ctxQualificationCode))
            {
                // Curriculum library isolation requires qualification scoping.
                return new List<LocalParagraphSearchHit>();
            }

            var ctxQualification = NormalizeForMatch(req?.QualificationDescription);
            var ctxSubject = NormalizeForMatch(req?.SubjectDescription);
            var ctxSubjectCode = NormalizeForMatch(req?.SubjectCode);
            var ctxTopic = NormalizeForMatch(req?.TopicDescription);
            var ctxCriteria = NormalizeForMatch(req?.AssessmentCriteriaDescription);
            var requestedPool = NormalizeKnowledgePool(req?.KnowledgePool);
            var requestedSourceType = NormalizeKnowledgeSourceType(req?.KnowledgeSourceType);
            var removeBoilerplate = req?.RemoveBoilerplate ?? true;
            var normalizedQualificationCodeLower = resolvedQualificationCode.ToLowerInvariant();

            var candidates = _context.SourceMaterials
                .Where(s => (s.QualificationCode ?? string.Empty).ToLower() == normalizedQualificationCodeLower)
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => new
                {
                    s.Id,
                    s.Title,
                    s.Url,
                    s.ExtractedText,
                    s.QualificationCode,
                    s.QualificationDescription,
                    s.SubjectDescription,
                    s.TopicDescription,
                    s.AssessmentCriteriaDescription,
                    s.KnowledgeSourceType,
                    s.KnowledgeNumber
                })
                .Take(2000)
                .ToList();

            var hits = new List<LocalParagraphSearchHit>();
            foreach (var c in candidates)
            {
                var pool = ResolveKnowledgePool(c.SubjectDescription, c.Url);
                if (!MatchesKnowledgePool(pool, requestedPool)) continue;
                var sourceType = NormalizeKnowledgeSourceType(c.KnowledgeSourceType);
                if (!MatchesKnowledgeSourceType(sourceType, requestedSourceType)) continue;

                var text = c.ExtractedText ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text)) continue;

                var paragraphs = SplitIntoSearchParagraphs(text, removeBoilerplate);
                if (paragraphs.Count == 0) paragraphs.Add(CleanExtractedText(text));

                var paragraphIndex = 0;
                foreach (var paragraph in paragraphs)
                {
                    paragraphIndex++;
                    if (string.IsNullOrWhiteSpace(paragraph)) continue;

                    var textScore = ScoreText(query, terms, paragraph);
                    if (textScore <= 0) continue;

                    var score = textScore + ScoreContext(
                        title: c.Title ?? string.Empty,
                        qualificationCode: c.QualificationCode ?? string.Empty,
                        qualification: c.QualificationDescription ?? string.Empty,
                        subject: c.SubjectDescription ?? string.Empty,
                        topic: c.TopicDescription ?? string.Empty,
                        criteria: c.AssessmentCriteriaDescription ?? string.Empty,
                        ctxQualificationCode: ctxQualificationCode,
                        ctxQualification: ctxQualification,
                        ctxSubject: ctxSubject,
                        ctxSubjectCode: ctxSubjectCode,
                        ctxTopic: ctxTopic,
                        ctxCriteria: ctxCriteria
                    );

                    var nCandidateQualificationCode = NormalizeForMatch(c.QualificationCode);
                    var nCandidateQualification = NormalizeForMatch(c.QualificationDescription);
                    var qualificationMatched =
                        (!string.IsNullOrWhiteSpace(ctxQualificationCode) && ContainsLoose(nCandidateQualificationCode, ctxQualificationCode)) ||
                        (!string.IsNullOrWhiteSpace(ctxQualification) && ContainsLoose(nCandidateQualification, ctxQualification));
                    if (string.Equals(sourceType, "developer_knowledge_base", StringComparison.OrdinalIgnoreCase))
                    {
                        score += qualificationMatched ? 12 : 4;
                    }

                    if (paragraph.Length >= 160 && paragraph.Length <= 1400) score += 2;
                    if (ContainsLoose(NormalizeForMatch(paragraph), NormalizeForMatch(query))) score += 2;

                    hits.Add(new LocalParagraphSearchHit
                    {
                        MaterialId = c.Id,
                        Title = string.IsNullOrWhiteSpace(c.Title) ? $"Material {c.Id}" : c.Title,
                        Url = c.Url ?? string.Empty,
                        Snippet = BuildSnippet(paragraph, query, terms, snippetLength),
                        Score = score,
                        ParagraphIndex = paragraphIndex,
                        KnowledgePool = pool,
                        KnowledgeSourceType = sourceType,
                        KnowledgeNumber = c.KnowledgeNumber,
                        QualificationCode = c.QualificationCode ?? string.Empty
                    });
                }
            }

            return hits
                .OrderByDescending(x => string.Equals(x.KnowledgeSourceType, "developer_knowledge_base", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.Score)
                .ThenBy(x => x.MaterialId)
                .ThenBy(x => x.ParagraphIndex)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        private static string ResolveKnowledgePool(string? subjectDescription, string? url)
        {
            var subject = (subjectDescription ?? "").Trim();
            var link = (url ?? "").Trim();
            if (subject.StartsWith("KnowledgeBase:", StringComparison.OrdinalIgnoreCase) ||
                link.StartsWith("knowledge://", StringComparison.OrdinalIgnoreCase))
            {
                if (subject.Contains("developer", StringComparison.OrdinalIgnoreCase) ||
                    link.Contains("/developer_knowledge_base/", StringComparison.OrdinalIgnoreCase))
                {
                    return "developer_knowledge";
                }
                return "local_upload";
            }
            if (subject.StartsWith("GitHub:", StringComparison.OrdinalIgnoreCase)) return "github_repo";
            if (subject.StartsWith("OAI-PMH:", StringComparison.OrdinalIgnoreCase)) return "oai_pmh";
            if (subject.StartsWith("LocalFolder:", StringComparison.OrdinalIgnoreCase)) return "local_folder";
            if (subject.StartsWith("EngineeringSeed:", StringComparison.OrdinalIgnoreCase) ||
                link.StartsWith("seed://engineering", StringComparison.OrdinalIgnoreCase))
            {
                return "engineering_seed";
            }
            if (link.Contains("figshare.com", StringComparison.OrdinalIgnoreCase)) return "open_aip";
            if (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return "web_import";
            }
            return "local_upload";
        }

        private static string NormalizeKnowledgePool(string? pool)
        {
            var p = (pool ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(p) || p == "all" || p == "*") return string.Empty;
            return p switch
            {
                "developer" => "developer_knowledge",
                "developer_kb" => "developer_knowledge",
                "knowledge_base" => "developer_knowledge",
                "github" => "github_repo",
                "repo" => "github_repo",
                "oai" => "oai_pmh",
                "openaip" => "open_aip",
                "figshare" => "open_aip",
                "seed" => "engineering_seed",
                "engineering" => "engineering_seed",
                "folder" => "local_folder",
                "localfolder" => "local_folder",
                "local" => "local_any",
                _ => p
            };
        }

        private static bool MatchesKnowledgePool(string actualPool, string requestedPool)
        {
            if (string.IsNullOrWhiteSpace(requestedPool)) return true;
            if (string.Equals(actualPool, requestedPool, StringComparison.OrdinalIgnoreCase)) return true;

            if (string.Equals(requestedPool, "developer_knowledge", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(actualPool, "developer_knowledge", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(requestedPool, "open_aip", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(actualPool, "oai_pmh", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(actualPool, "open_aip", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (string.Equals(requestedPool, "oai_pmh", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(actualPool, "oai_pmh", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(actualPool, "open_aip", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (string.Equals(requestedPool, "local_any", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(actualPool, "local_upload", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(actualPool, "local_folder", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }

        private static string NormalizeKnowledgeSourceType(string? sourceType)
        {
            var s = (sourceType ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            return s switch
            {
                "local" => "local_source_upload",
                "local_source" => "local_source_upload",
                "local_upload" => "local_source_upload",
                "developer" => "developer_knowledge_base",
                "developer_kb" => "developer_knowledge_base",
                "knowledge_base" => "developer_knowledge_base",
                "kb" => "developer_knowledge_base",
                "shared_agent" => "agent_shared",
                "agent_shared_knowledge" => "agent_shared",
                "mira_agent" => "agent_mira",
                "agent_mira_knowledge" => "agent_mira",
                "gemma_agent" => "agent_gemma",
                "agent_gemma_knowledge" => "agent_gemma",
                "qwen_agent" => "agent_gemma",
                "agent_qwen_knowledge" => "agent_qwen",
                _ => s
            };
        }

        private static bool MatchesKnowledgeSourceType(string actualType, string requestedType)
        {
            if (string.IsNullOrWhiteSpace(requestedType)) return true;
            if (string.Equals(actualType, requestedType, StringComparison.OrdinalIgnoreCase)) return true;

            if (string.Equals(requestedType, "local_source_upload", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(actualType))
            {
                return true;
            }

            return false;
        }

        private void TryAutoSyncKnowledgeHierarchy(string? qualificationCode, string? qualificationDescription)
        {
            try
            {
                _knowledgeHierarchyService.SyncKnowledgeHierarchy(new KnowledgeHierarchyService.SyncOptions
                {
                    QualificationCode = qualificationCode,
                    QualificationDescription = qualificationDescription,
                    IncludeLocalSourceUploads = true,
                    IncludeDeveloperKnowledgeBase = true,
                    MaxFilesPerInbox = 200,
                    RebuildUploadReadme = true
                });
            }
            catch
            {
                // Keep search flow resilient even when sync fails.
            }
        }

        private static List<string> SplitIntoSearchParagraphs(string text, bool removeBoilerplate)
        {
            var cleaned = CleanExtractedText(text ?? string.Empty);
            if (string.IsNullOrWhiteSpace(cleaned)) return new List<string>();

            cleaned = Regex.Replace(cleaned, @"\[(?:Page|PAGE)\s+\d+\]", "\n", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");

            var rawParagraphs = Regex.Split(cleaned, @"\n{2,}")
                .Select(p => Regex.Replace(p ?? "", @"\s+", " ").Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            if (rawParagraphs.Count <= 1)
            {
                var lineParagraphs = new List<string>();
                var buffer = new System.Text.StringBuilder();
                foreach (var line in cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var l = Regex.Replace(line ?? "", @"\s+", " ").Trim();
                    if (string.IsNullOrWhiteSpace(l)) continue;
                    if (buffer.Length > 0) buffer.Append(' ');
                    buffer.Append(l);
                    if (buffer.Length >= 800)
                    {
                        lineParagraphs.Add(buffer.ToString());
                        buffer.Clear();
                    }
                }
                if (buffer.Length > 0) lineParagraphs.Add(buffer.ToString());
                if (lineParagraphs.Count > 0) rawParagraphs = lineParagraphs;
            }

            var paragraphs = new List<string>();
            foreach (var paragraph in rawParagraphs)
            {
                var p = Regex.Replace(paragraph ?? "", @"\s+", " ").Trim();
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (removeBoilerplate && IsBoilerplateParagraph(p)) continue;
                if (p.Length < 50) continue;

                if (p.Length > 1800)
                {
                    var sentenceParts = Regex.Split(p, @"(?<=[\.\!\?])\s+(?=[A-Z0-9])")
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList();
                    if (sentenceParts.Count == 0)
                    {
                        sentenceParts = new List<string> { p };
                    }

                    var chunk = new System.Text.StringBuilder();
                    foreach (var sentence in sentenceParts)
                    {
                        var s = sentence.Trim();
                        if (s.Length == 0) continue;
                        if (chunk.Length + s.Length + 1 > 900)
                        {
                            var candidate = chunk.ToString().Trim();
                            if (candidate.Length >= 50 && (!removeBoilerplate || !IsBoilerplateParagraph(candidate)))
                            {
                                paragraphs.Add(candidate);
                            }
                            chunk.Clear();
                        }
                        if (chunk.Length > 0) chunk.Append(' ');
                        chunk.Append(s);
                    }
                    var tail = chunk.ToString().Trim();
                    if (tail.Length >= 50 && (!removeBoilerplate || !IsBoilerplateParagraph(tail)))
                    {
                        paragraphs.Add(tail);
                    }
                }
                else
                {
                    paragraphs.Add(p);
                }
            }

            return paragraphs;
        }

        private static bool IsBoilerplateParagraph(string paragraph)
        {
            return DocumentTextCleaner.IsLikelyBoilerplateParagraph(paragraph);
        }

        private static int CountWords(string text)
        {
            return DocumentTextCleaner.WordCount(text);
        }

        private static bool ShouldSkipPdfPageText(int pageNumber, int totalPages, string pageText)
        {
            var p = Regex.Replace(pageText ?? string.Empty, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(p)) return true;
            if (DocumentTextCleaner.IsLikelyBoilerplateParagraph(p) && CountWords(p) < 260) return true;

            var normalized = p.ToLowerInvariant();
            var words = CountWords(p);
            var dotLeaderCount = Regex.Matches(p, @"\.{3,}\s*\d{1,4}").Count;

            var tocLike = Regex.IsMatch(normalized, @"\b(table of contents?|contents|list of figures|list of tables)\b");
            if (tocLike && (dotLeaderCount >= 1 || words < 220)) return true;
            if (dotLeaderCount >= 3) return true;

            var indexLike = Regex.IsMatch(normalized, @"\bindex\b");
            if (indexLike && dotLeaderCount >= 1 && words < 220) return true;

            var coverLike = Regex.IsMatch(normalized, @"\b(copyright|all rights reserved|isbn|published by|edition|version|draft)\b");
            if (pageNumber <= 2 && coverLike && words < 260) return true;

            if (pageNumber == 1 && words < 80) return true;
            if (pageNumber == totalPages && words < 25) return true;

            return false;
        }

        private static string ApplyCognitiveDocumentScanClean(string text, string? ext)
        {
            var cleaned = CleanExtractedText(text ?? string.Empty);
            if (string.IsNullOrWhiteSpace(cleaned)) return string.Empty;

            var normalizedExt = (ext ?? string.Empty).Trim().ToLowerInvariant();
            if (normalizedExt == ".pdf" || normalizedExt == "pdf")
            {
                cleaned = TrimPdfFrontAndBackMatter(cleaned);
            }

            cleaned = RemoveLowValueMatterBlocks(cleaned);
            return CleanExtractedText(cleaned);
        }

        private static string RemoveLowValueMatterBlocks(string text)
        {
            var blocks = Regex.Split(text ?? string.Empty, @"\n{2,}")
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (blocks.Count <= 1) return text ?? string.Empty;

            var kept = new List<string>(blocks.Count);
            foreach (var block in blocks)
            {
                var normalized = Regex.Replace(block, @"\s+", " ").Trim().ToLowerInvariant();
                var words = CountWords(block);
                var dotLeaders = Regex.Matches(block, @"\.{3,}\s*\d{1,4}").Count;

                var frontMatter = IsLikelyFrontMatterPage(normalized, words, dotLeaders);
                var backMatter = IsLikelyBackMatterPage(normalized, words);
                if (frontMatter && words < 220)
                {
                    continue;
                }

                if (backMatter && words < 90)
                {
                    continue;
                }

                kept.Add(block);
            }

            return kept.Count == 0 ? (text ?? string.Empty) : string.Join("\n\n", kept);
        }

        private static string TrimPdfFrontAndBackMatter(string text)
        {
            var pages = ParsePdfPageBlocks(text);
            if (pages.Count < 3) return text;

            var firstContentIndex = 0;
            var probeLimit = Math.Min(pages.Count, 24);
            for (var i = 0; i < probeLimit; i++)
            {
                if (IsLikelyInstructionalPage(pages[i].Text))
                {
                    firstContentIndex = i;
                    break;
                }
            }

            if (firstContentIndex > 0)
            {
                var frontMatterHits = 0;
                for (var i = 0; i < firstContentIndex && i < 20; i++)
                {
                    var p = pages[i];
                    var normalized = Regex.Replace(p.Text, @"\s+", " ").Trim().ToLowerInvariant();
                    var words = CountWords(p.Text);
                    var dotLeaders = Regex.Matches(p.Text, @"\.{3,}\s*\d{1,4}").Count;
                    if (IsLikelyFrontMatterPage(normalized, words, dotLeaders))
                    {
                        frontMatterHits++;
                    }
                }

                var requiredHits = Math.Max(2, (int)Math.Ceiling(firstContentIndex * 0.45));
                if (frontMatterHits < requiredHits)
                {
                    firstContentIndex = 0;
                }
            }

            var endExclusive = pages.Count;
            var trailingBackMatter = 0;
            for (var i = pages.Count - 1; i >= Math.Max(firstContentIndex, pages.Count - 20); i--)
            {
                var p = pages[i];
                var normalized = Regex.Replace(p.Text, @"\s+", " ").Trim().ToLowerInvariant();
                var words = CountWords(p.Text);
                if (IsLikelyBackMatterPage(normalized, words))
                {
                    trailingBackMatter++;
                    continue;
                }
                break;
            }
            if (trailingBackMatter >= 2)
            {
                endExclusive = Math.Max(firstContentIndex + 1, pages.Count - trailingBackMatter);
            }

            if (firstContentIndex <= 0 && endExclusive >= pages.Count)
            {
                return text;
            }

            var sb = new System.Text.StringBuilder();
            for (var i = firstContentIndex; i < endExclusive; i++)
            {
                var p = pages[i];
                if (string.IsNullOrWhiteSpace(p.Text)) continue;
                sb.AppendLine($"[Page {p.Number}]");
                sb.AppendLine(p.Text.Trim());
                sb.AppendLine();
            }

            var rebuilt = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(rebuilt) ? text : rebuilt;
        }

        private static List<(int Number, string Text)> ParsePdfPageBlocks(string text)
        {
            var pages = new List<(int Number, string Text)>();
            var src = text ?? string.Empty;
            var matches = Regex.Matches(src, @"(?m)^\[Page\s+(?<n>\d+)\]\s*$");
            if (matches.Count == 0) return pages;

            for (var i = 0; i < matches.Count; i++)
            {
                var current = matches[i];
                var nextIndex = i + 1 < matches.Count ? matches[i + 1].Index : src.Length;
                var start = current.Index + current.Length;
                if (start >= nextIndex) continue;

                var pageText = src.Substring(start, nextIndex - start).Trim();
                if (string.IsNullOrWhiteSpace(pageText)) continue;

                if (!int.TryParse(current.Groups["n"].Value, out var pageNumber))
                {
                    pageNumber = i + 1;
                }
                pages.Add((pageNumber, pageText));
            }

            return pages;
        }

        private static bool IsLikelyFrontMatterPage(string normalized, int words, int dotLeaders)
        {
            if (string.IsNullOrWhiteSpace(normalized)) return true;
            if (dotLeaders >= 2) return true;

            if (Regex.IsMatch(normalized, @"\b(table of contents?|contents|list of figures|list of tables|foreword|preface|acknowledgements|dedication)\b"))
            {
                return true;
            }

            if (Regex.IsMatch(normalized, @"\b(copyright|all rights reserved|isbn|published by|edition|print(ed)? by|copyright notice)\b"))
            {
                return true;
            }

            var curriculumCodeHits = Regex.Matches(normalized, @"\b\d{6,}\s*[-–—]\s*[a-z]{2,}\s*[-–—]\s*\d{2}\b").Count;
            if (curriculumCodeHits >= 2 && words < 280 &&
                Regex.IsMatch(normalized, @"\b(nqf level|credits?)\b"))
            {
                return true;
            }

            if (words < 320 &&
                Regex.IsMatch(normalized, @"\b(total number of credits|credits for)\b") &&
                Regex.IsMatch(normalized, @"\b(modules?|knowledge modules?|practical skill modules?)\b"))
            {
                return true;
            }

            if (words < 70 && Regex.IsMatch(normalized, @"\b(introduction|overview|about this (book|manual)|disclaimer)\b"))
            {
                return true;
            }

            return false;
        }

        private static bool IsLikelyInstructionalPage(string pageText)
        {
            var normalized = Regex.Replace(pageText ?? string.Empty, @"\s+", " ").Trim().ToLowerInvariant();
            var words = CountWords(pageText ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalized) || words < 60) return false;

            var score = 0;
            if (words >= 120) score += 1;
            if (Regex.IsMatch(normalized, @"\b(topic|topics|unit|chapter|module|lesson)\b")) score += 1;
            if (Regex.IsMatch(normalized, @"\b(learning outcome|outcomes|objective|objectives)\b")) score += 2;
            if (Regex.IsMatch(normalized, @"\b(assessment criteria|assessment|criteria)\b")) score += 2;
            if (Regex.IsMatch(normalized, @"\b(procedure|steps|practical|exercise|activity)\b")) score += 1;
            if (Regex.IsMatch(normalized, @"\b(explain|demonstrate|evaluate|apply|describe)\b")) score += 1;

            if (Regex.IsMatch(normalized, @"\b(table of contents?|preface|foreword|acknowledgements|copyright|isbn)\b"))
            {
                score -= 3;
            }

            var curriculumCodeHits = Regex.Matches(normalized, @"\b\d{6,}\s*[-–—]\s*[a-z]{2,}\s*[-–—]\s*\d{2}\b").Count;
            if (curriculumCodeHits >= 3 && Regex.IsMatch(normalized, @"\b(nqf level|credits?)\b"))
            {
                score -= 4;
            }

            if (curriculumCodeHits >= 2 &&
                Regex.IsMatch(normalized, @"\b(total number of credits|credits for)\b") &&
                Regex.IsMatch(normalized, @"\b(modules?|knowledge modules?|practical skill modules?)\b"))
            {
                score -= 4;
            }

            return score >= 2;
        }

        private static bool IsLikelyBackMatterPage(string normalized, int words)
        {
            if (string.IsNullOrWhiteSpace(normalized)) return true;
            if (Regex.IsMatch(normalized, @"\b(index|bibliography|references|glossary|further reading|appendix|appendices|answer key)\b"))
            {
                return true;
            }

            if (words < 70 && Regex.IsMatch(normalized, @"\b(contact|website|www\.|support|customer service|notes)\b"))
            {
                return true;
            }

            return false;
        }

        private static int ComputeKnowledgeParagraphPriority(
            string text,
            string title,
            string pool,
            string topic,
            string criteria)
        {
            var score = 20;
            var normalized = NormalizeForMatch(text);
            var normalizedTitle = NormalizeForMatch(title);
            var normalizedTopic = NormalizeForMatch(topic);
            var normalizedCriteria = NormalizeForMatch(criteria);

            if (text.Length >= 220 && text.Length <= 1400) score += 6;
            if (CountWords(text) >= 40) score += 4;

            if (ContainsLoose(normalized, "assessment criteria")) score += 7;
            if (ContainsLoose(normalized, "learning outcome")) score += 6;
            if (ContainsLoose(normalized, "unit standard")) score += 5;
            if (ContainsLoose(normalized, "competency")) score += 5;
            if (ContainsLoose(normalized, "evidence")) score += 4;
            if (ContainsLoose(normalized, normalizedTopic) && !string.IsNullOrWhiteSpace(normalizedTopic)) score += 6;
            if (ContainsLoose(normalized, normalizedCriteria) && !string.IsNullOrWhiteSpace(normalizedCriteria)) score += 6;
            if (ContainsLoose(normalized, normalizedTitle) && !string.IsNullOrWhiteSpace(normalizedTitle)) score += 3;

            if (string.Equals(pool, "local_upload", StringComparison.OrdinalIgnoreCase)) score += 3;
            if (string.Equals(pool, "github_repo", StringComparison.OrdinalIgnoreCase)) score += 2;
            if (string.Equals(pool, "oai_pmh", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pool, "open_aip", StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }

            return score;
        }

        private static List<UnifiedSearchResult> DeduplicateAndSort(List<UnifiedSearchResult> input, int maxResults)
        {
            var map = new Dictionary<string, UnifiedSearchResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in input.Where(x => x != null))
            {
                var title = (item.Title ?? string.Empty).Trim();
                var url = (item.Url ?? string.Empty).Trim();
                var snippet = (item.Snippet ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(snippet)) continue;
                var key = !string.IsNullOrWhiteSpace(url)
                    ? url.ToLowerInvariant()
                    : $"{title.ToLowerInvariant()}::{(snippet.Length > 120 ? snippet.Substring(0, 120) : snippet).ToLowerInvariant()}";
                if (map.TryGetValue(key, out var existing))
                {
                    if (item.Score > existing.Score) map[key] = item;
                    continue;
                }
                map[key] = item;
            }

            return map.Values
                .OrderByDescending(x => x.Score)
                .Take(Math.Max(1, maxResults))
                .ToList();
        }

        private async Task<List<UnifiedSearchResult>> SearchAzureAiSearchAsync(
            string query,
            string endpoint,
            string indexName,
            string apiKey,
            string? qualificationCode,
            string? qualificationDescription,
            string? subjectDescription,
            string? topicDescription,
            string? criteriaDescription,
            int top)
        {
            var apiVersion = Environment.GetEnvironmentVariable("AZURE_AI_SEARCH_API_VERSION");
            if (string.IsNullOrWhiteSpace(apiVersion)) apiVersion = "2023-11-01";

            var url = $"{endpoint.Trim().TrimEnd('/')}/indexes/{Uri.EscapeDataString(indexName.Trim())}/docs/search?api-version={Uri.EscapeDataString(apiVersion)}";
            var payload = new
            {
                search = query,
                top = Math.Max(1, Math.Min(20, top)),
                queryType = "simple"
            };

            using var msg = new HttpRequestMessage(HttpMethod.Post, url);
            msg.Headers.Add("api-key", apiKey.Trim());
            msg.Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
            var resp = await _http.SendAsync(msg);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body}");
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return new List<UnifiedSearchResult>();
            }

            var ctxQualificationCode = NormalizeForMatch(qualificationCode);
            var ctxQualification = NormalizeForMatch(qualificationDescription);
            var ctxSubject = NormalizeForMatch(subjectDescription);
            var ctxTopic = NormalizeForMatch(topicDescription);
            var ctxCriteria = NormalizeForMatch(criteriaDescription);
            var terms = TokenizeQuery(query);
            if (terms.Count == 0) terms.Add(query.ToLowerInvariant());

            var results = new List<UnifiedSearchResult>();
            foreach (var item in arr.EnumerateArray())
            {
                var title = GetJsonString(item, "title", "name", "heading", "document_title", "fileName");
                var urlValue = GetJsonString(item, "url", "uri", "sourceUrl", "path", "blobUrl");
                var fullText = GetJsonString(item, "content", "chunk", "text", "description", "body");
                var snippet = BuildSnippet(fullText, query, terms, 360);
                var baseScore = 50.0;
                if (item.TryGetProperty("@search.score", out var sProp) && sProp.ValueKind == JsonValueKind.Number)
                {
                    baseScore = sProp.GetDouble() * 10.0;
                }
                var contextBoost = ScoreContext(
                    title,
                    qualificationCode: GetJsonString(item, "qualificationCode", "qualification_number", "qualificationId"),
                    qualification: GetJsonString(item, "qualificationDescription", "qualification", "qualification_name"),
                    subject: GetJsonString(item, "subjectDescription", "subject", "subject_name"),
                    topic: GetJsonString(item, "topicDescription", "topic", "topic_name"),
                    criteria: GetJsonString(item, "assessmentCriteriaDescription", "assessment", "criteria"),
                    ctxQualificationCode: ctxQualificationCode,
                    ctxQualification: ctxQualification,
                    ctxSubject: ctxSubject,
                    ctxSubjectCode: "",
                    ctxTopic: ctxTopic,
                    ctxCriteria: ctxCriteria);

                results.Add(new UnifiedSearchResult
                {
                    Title = string.IsNullOrWhiteSpace(title) ? "Azure AI Search result" : title,
                    Url = urlValue ?? string.Empty,
                    Snippet = snippet,
                    Source = "azure_ai_search",
                    Score = baseScore + contextBoost
                });
            }
            return results;
        }

        private async Task<List<UnifiedSearchResult>> SearchBingCustomAsync(string query, string key, string customConfigId, int top)
        {
            var endpoint = GetBingCustomSearchEndpoint();
            var url = $"{endpoint}?q={Uri.EscapeDataString(query)}&customconfig={Uri.EscapeDataString(customConfigId)}&count={Math.Max(1, Math.Min(50, top))}";
            using var msg = new HttpRequestMessage(HttpMethod.Get, url);
            msg.Headers.Add("Ocp-Apim-Subscription-Key", key);
            var resp = await _http.SendAsync(msg);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body}");
            }
            return ParseBingResults(body, "bing_custom", 40);
        }

        private async Task<List<UnifiedSearchResult>> SearchBingWebAsync(string query, string key, int top)
        {
            var endpoint = GetBingSearchEndpoint();
            var url = $"{endpoint}?q={Uri.EscapeDataString(query)}&count={Math.Max(1, Math.Min(50, top))}";
            using var msg = new HttpRequestMessage(HttpMethod.Get, url);
            msg.Headers.Add("Ocp-Apim-Subscription-Key", key);
            var resp = await _http.SendAsync(msg);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body}");
            }
            return ParseBingResults(body, "bing_web", 35);
        }

        private static List<UnifiedSearchResult> ParseBingResults(string json, string source, double baseScore)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("webPages", out var webPages) ||
                webPages.ValueKind != JsonValueKind.Object ||
                !webPages.TryGetProperty("value", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                return new List<UnifiedSearchResult>();
            }

            var list = new List<UnifiedSearchResult>();
            var rank = 0;
            foreach (var item in arr.EnumerateArray())
            {
                var title = GetJsonString(item, "name");
                var url = GetJsonString(item, "url");
                var snippet = GetJsonString(item, "snippet");
                list.Add(new UnifiedSearchResult
                {
                    Title = title,
                    Url = url,
                    Snippet = snippet,
                    Source = source,
                    Score = baseScore - rank
                });
                rank++;
            }
            return list;
        }

        private async Task<List<UnifiedSearchResult>> SearchGoogleUnifiedAsync(string query, string key, string cx, int top)
        {
            var url = $"https://www.googleapis.com/customsearch/v1?key={Uri.EscapeDataString(key)}&cx={Uri.EscapeDataString(cx)}&q={Uri.EscapeDataString(query)}&num={Math.Max(1, Math.Min(10, top))}";
            using var msg = new HttpRequestMessage(HttpMethod.Get, url);
            msg.Headers.UserAgent.Add(new ProductInfoHeaderValue("ETDP", "1.0"));
            var resp = await _http.SendAsync(msg);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body}");
            }
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return new List<UnifiedSearchResult>();
            }

            var list = new List<UnifiedSearchResult>();
            var rank = 0;
            foreach (var item in arr.EnumerateArray())
            {
                var title = GetJsonString(item, "title");
                var urlValue = GetJsonString(item, "link");
                var snippet = GetJsonString(item, "snippet");
                list.Add(new UnifiedSearchResult
                {
                    Title = title,
                    Url = urlValue,
                    Snippet = snippet,
                    Source = "google_custom",
                    Score = 30 - rank
                });
                rank++;
            }
            return list;
        }

        private async Task<List<UnifiedSearchResult>> SearchOpenAipUnifiedAsync(string query, int top)
        {
            var normalizedQuery = Regex.Replace(query ?? string.Empty, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(normalizedQuery) || normalizedQuery.Length < 3)
            {
                return new List<UnifiedSearchResult>();
            }

            var count = Math.Max(1, Math.Min(20, top));
            var endpoint = $"{GetOpenAipApiBaseUrl().TrimEnd('/')}/articles/search";
            var payload = new
            {
                search_for = normalizedQuery,
                page_size = count,
                order = "published_date",
                order_direction = "desc"
            };

            using var msg = new HttpRequestMessage(HttpMethod.Post, endpoint);
            msg.Headers.UserAgent.Add(new ProductInfoHeaderValue("ETDP", "1.0"));
            msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var token = GetOpenAipToken();
            if (!string.IsNullOrWhiteSpace(token))
            {
                msg.Headers.TryAddWithoutValidation("Authorization", $"token {token.Trim()}");
            }
            msg.Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

            var resp = await _http.SendAsync(msg);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body}");
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new List<UnifiedSearchResult>();
            }

            var list = new List<UnifiedSearchResult>();
            var rank = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (rank >= count) break;

                var title = GetJsonString(item, "title");
                var url = GetJsonString(item, "url_public_html", "figshare_url", "url");
                var apiUrl = GetJsonString(item, "url_public_api", "url");
                var snippet = BuildOpenAipSnippet(item);

                if (string.IsNullOrWhiteSpace(snippet) && !string.IsNullOrWhiteSpace(apiUrl) && rank < 3)
                {
                    var detailSnippet = await FetchOpenAipDescriptionAsync(apiUrl, token);
                    if (!string.IsNullOrWhiteSpace(detailSnippet))
                    {
                        snippet = detailSnippet;
                    }
                }

                list.Add(new UnifiedSearchResult
                {
                    Title = string.IsNullOrWhiteSpace(title) ? "OpenAIP result" : title,
                    Url = url,
                    Snippet = snippet,
                    Source = "open_aip_figshare",
                    Score = 28 - rank
                });
                rank++;
            }

            return list;
        }

        private async Task<List<UnifiedSearchResult>> SearchWikipediaUnifiedAsync(string query, int top)
        {
            var count = Math.Max(1, Math.Min(20, top));
            var url = $"https://en.wikipedia.org/w/api.php?action=query&list=search&srsearch={Uri.EscapeDataString(query)}&utf8=&format=json&srlimit={count}";
            using var reqMsg = new HttpRequestMessage(HttpMethod.Get, url);
            reqMsg.Headers.UserAgent.Add(new ProductInfoHeaderValue("ETDP", "1.0"));
            reqMsg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            reqMsg.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));

            var resp = await _http.SendAsync(reqMsg);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body}");
            }

            using var doc = JsonDocument.Parse(body);
            var queryObj = doc.RootElement.TryGetProperty("query", out var q) ? q : default;
            var searchArr = queryObj.ValueKind == JsonValueKind.Object && queryObj.TryGetProperty("search", out var s) ? s : default;
            if (searchArr.ValueKind != JsonValueKind.Array)
            {
                return new List<UnifiedSearchResult>();
            }

            var list = new List<UnifiedSearchResult>();
            var rank = 0;
            foreach (var it in searchArr.EnumerateArray())
            {
                var title = it.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                var pageid = it.TryGetProperty("pageid", out var pid) ? pid.GetInt32() : 0;
                var snippet = it.TryGetProperty("snippet", out var sn) ? sn.GetString() ?? string.Empty : string.Empty;
                list.Add(new UnifiedSearchResult
                {
                    Title = string.IsNullOrWhiteSpace(title) ? "Wikipedia result" : title,
                    Url = pageid > 0 ? $"https://en.wikipedia.org/?curid={pageid}" : string.Empty,
                    Snippet = Regex.Replace(snippet, "<[^>]+>", " ").Trim(),
                    Source = "wikipedia",
                    Score = 22 - rank
                });
                rank++;
                if (rank >= count) break;
            }

            return list;
        }

        private static List<object> MapUnifiedResultsToObjects(IEnumerable<UnifiedSearchResult> results)
        {
            return results
                .Select(r => (object)new
                {
                    title = r.Title,
                    url = r.Url,
                    snippet = r.Snippet,
                    source = r.Source,
                    score = Math.Round(r.Score, 3)
                })
                .ToList();
        }

        private async Task<(string provider, List<object> results, string warning)> BuildSearchFallbackAsync(string query, string warningPrefix)
        {
            if (GetOpenAipSearchEnabled())
            {
                try
                {
                    var openAip = await SearchOpenAipUnifiedAsync(query, top: 8);
                    if (openAip.Count > 0)
                    {
                        return ("openaip_fallback", MapUnifiedResultsToObjects(openAip), warningPrefix);
                    }
                }
                catch
                {
                    // Ignore fallback provider errors and continue.
                }
            }

            try
            {
                var wiki = await SearchWikipediaUnifiedAsync(query, top: 8);
                if (wiki.Count > 0)
                {
                    return ("wikipedia_fallback", MapUnifiedResultsToObjects(wiki), warningPrefix);
                }
            }
            catch
            {
                // Ignore fallback provider errors and continue.
            }

            return ("none", new List<object>(), $"{warningPrefix} No fallback internet results were returned.");
        }

        private async Task<string> FetchOpenAipDescriptionAsync(string detailApiUrl, string token)
        {
            if (!Uri.TryCreate(detailApiUrl?.Trim(), UriKind.Absolute, out var _))
            {
                return string.Empty;
            }

            using var msg = new HttpRequestMessage(HttpMethod.Get, detailApiUrl);
            msg.Headers.UserAgent.Add(new ProductInfoHeaderValue("ETDP", "1.0"));
            msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(token))
            {
                msg.Headers.TryAddWithoutValidation("Authorization", $"token {token.Trim()}");
            }

            var resp = await _http.SendAsync(msg);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return string.Empty;

            using var doc = JsonDocument.Parse(body);
            var description = GetJsonString(doc.RootElement, "description");
            if (string.IsNullOrWhiteSpace(description)) return string.Empty;

            var cleaned = Regex.Replace(description, "<[^>]+>", " ");
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
            if (cleaned.Length > 460) cleaned = cleaned.Substring(0, 460) + "...";
            return cleaned;
        }

        private static string BuildOpenAipSnippet(JsonElement item)
        {
            var description = GetJsonString(item, "description");
            if (!string.IsNullOrWhiteSpace(description))
            {
                var cleaned = Regex.Replace(description, "<[^>]+>", " ");
                cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
                if (cleaned.Length > 460) cleaned = cleaned.Substring(0, 460) + "...";
                return cleaned;
            }

            var type = GetJsonString(item, "defined_type_name");
            var published = GetJsonString(item, "published_date");
            var doi = GetJsonString(item, "doi");
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(type)) parts.Add(type);
            if (!string.IsNullOrWhiteSpace(published)) parts.Add($"published {published}");
            if (!string.IsNullOrWhiteSpace(doi)) parts.Add($"DOI {doi}");
            return string.Join(" | ", parts);
        }

        private static string GetJsonString(JsonElement element, params string[] names)
        {
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out var prop))
                {
                    if (prop.ValueKind == JsonValueKind.String) return prop.GetString() ?? string.Empty;
                    if (prop.ValueKind == JsonValueKind.Number) return prop.ToString();
                    if (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False) return prop.ToString();
                }
            }
            return string.Empty;
        }

        private static List<string> TokenizeQuery(string query)
        {
            var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the","and","for","with","from","into","that","this","your","you","are","was","were","have","has","had",
                "not","yet","done","plan","lesson","content","subject","topic","criteria","assessment","about","what",
                "when","where","how","why","all","any","can","will","would","should","use","using"
            };
            var parts = Regex.Split(query ?? string.Empty, @"[^A-Za-z0-9]+")
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim().ToLowerInvariant())
                .Where(p => p.Length >= 3 && !stop.Contains(p))
                .Distinct()
                .ToList();
            return parts;
        }

        private static int ScoreText(string query, List<string> terms, string text)
        {
            var source = (text ?? string.Empty).ToLowerInvariant();
            var q = (query ?? string.Empty).ToLowerInvariant();
            if (source.Length == 0) return 0;

            var score = 0;
            var phraseIdx = source.IndexOf(q, StringComparison.Ordinal);
            if (phraseIdx >= 0) score += 30;

            foreach (var t in terms)
            {
                if (source.Contains(t, StringComparison.Ordinal)) score += 8;
            }

            if (q.Length >= 8 && source.Contains(q.Replace(" ", ""), StringComparison.Ordinal))
            {
                score += 5;
            }

            return score;
        }

        private static int ScoreContext(
            string title,
            string qualificationCode,
            string qualification,
            string subject,
            string topic,
            string criteria,
            string ctxQualificationCode,
            string ctxQualification,
            string ctxSubject,
            string ctxSubjectCode,
            string ctxTopic,
            string ctxCriteria)
        {
            var score = 0;
            var nTitle = NormalizeForMatch(title);
            var nQualificationCode = NormalizeForMatch(qualificationCode);
            var nQualification = NormalizeForMatch(qualification);
            var nSubject = NormalizeForMatch(subject);
            var nTopic = NormalizeForMatch(topic);
            var nCriteria = NormalizeForMatch(criteria);

            if (!string.IsNullOrWhiteSpace(ctxQualificationCode) && ContainsLoose(nQualificationCode, ctxQualificationCode))
            {
                score += 16;
            }

            if (!string.IsNullOrWhiteSpace(ctxQualification) && ContainsLoose(nQualification, ctxQualification))
            {
                score += 14;
            }

            if (!string.IsNullOrWhiteSpace(ctxSubject))
            {
                if (ContainsLoose(nSubject, ctxSubject)) score += 12;
                if (ContainsLoose(nTitle, ctxSubject)) score += 4;
            }

            if (!string.IsNullOrWhiteSpace(ctxSubjectCode) && ContainsLoose(nTitle, ctxSubjectCode))
            {
                score += 8;
            }

            if (!string.IsNullOrWhiteSpace(ctxTopic))
            {
                if (ContainsLoose(nTopic, ctxTopic)) score += 12;
                if (ContainsLoose(nTitle, ctxTopic)) score += 4;
            }

            if (!string.IsNullOrWhiteSpace(ctxCriteria) && ContainsLoose(nCriteria, ctxCriteria))
            {
                score += 12;
            }

            return score;
        }

        private static string NormalizeForMatch(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var v = value.Trim().ToLowerInvariant();
            v = Regex.Replace(v, @"\s+", " ");
            return v;
        }

        private static bool ContainsLoose(string source, string needle)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(needle)) return false;
            if (source.Contains(needle, StringComparison.Ordinal)) return true;
            var parts = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var matches = 0;
            foreach (var p in parts)
            {
                if (p.Length < 3) continue;
                if (source.Contains(p, StringComparison.Ordinal)) matches++;
            }
            return matches >= Math.Max(1, parts.Length / 2);
        }

        private static (int score, bool blocked) ScoreHierarchicalCascade(
            string title,
            string text,
            string qualification,
            string subject,
            string topic,
            string criteria,
            string ctxQualification,
            string ctxSubject,
            string ctxSubjectCode,
            string ctxTopic,
            string ctxLesson,
            string ctxCriteria)
        {
            var nTitle = NormalizeForMatch(title);
            var nText = NormalizeForMatch(text);
            var nQualification = NormalizeForMatch(qualification);
            var nSubject = NormalizeForMatch(subject);
            var nTopic = NormalizeForMatch(topic);
            var nCriteria = NormalizeForMatch(criteria);
            var score = 0;

            // 1) Qualification
            if (!string.IsNullOrWhiteSpace(ctxQualification))
            {
                var qMatch = ContainsLoose(nQualification, ctxQualification) || ContainsLoose(nTitle, ctxQualification) || ContainsLoose(nText, ctxQualification);
                if (!qMatch) return (-200, true);
                score += 40;
            }

            // 2) Subject
            if (!string.IsNullOrWhiteSpace(ctxSubject) || !string.IsNullOrWhiteSpace(ctxSubjectCode))
            {
                var sMatch = false;
                if (!string.IsNullOrWhiteSpace(ctxSubject))
                {
                    sMatch = ContainsLoose(nSubject, ctxSubject) || ContainsLoose(nTitle, ctxSubject) || ContainsLoose(nText, ctxSubject);
                }
                if (!sMatch && !string.IsNullOrWhiteSpace(ctxSubjectCode))
                {
                    sMatch = ContainsLoose(nTitle, ctxSubjectCode) || ContainsLoose(nText, ctxSubjectCode);
                }
                if (!sMatch) return (-170, true);
                score += 30;
            }

            // 3) Topic
            if (!string.IsNullOrWhiteSpace(ctxTopic))
            {
                var tMatch = ContainsLoose(nTopic, ctxTopic) || ContainsLoose(nTitle, ctxTopic) || ContainsLoose(nText, ctxTopic);
                if (!tMatch) return (-140, true);
                score += 25;
            }

            // 4) Lesson plan (soft gate to tolerate phrasing variance)
            if (!string.IsNullOrWhiteSpace(ctxLesson))
            {
                var lMatch = ContainsLoose(nText, ctxLesson) || ContainsLoose(nTitle, ctxLesson);
                if (lMatch)
                {
                    score += 20;
                }
                else
                {
                    score -= 12;
                }
            }

            // 5) Assessment criteria (soft boost)
            if (!string.IsNullOrWhiteSpace(ctxCriteria))
            {
                var cMatch = ContainsLoose(nCriteria, ctxCriteria) || ContainsLoose(nText, ctxCriteria) || ContainsLoose(nTitle, ctxCriteria);
                if (cMatch) score += 10;
            }

            return (score, false);
        }

        private static string BuildSnippet(string text, string query, List<string> terms, int snippetLength)
        {
            var source = text ?? string.Empty;
            if (source.Length <= snippetLength) return CleanExtractedText(source);
            var lower = source.ToLowerInvariant();
            var q = (query ?? string.Empty).ToLowerInvariant();

            var idx = -1;
            if (!string.IsNullOrWhiteSpace(q))
            {
                idx = lower.IndexOf(q, StringComparison.Ordinal);
            }

            if (idx < 0)
            {
                foreach (var t in terms)
                {
                    idx = lower.IndexOf(t, StringComparison.Ordinal);
                    if (idx >= 0) break;
                }
            }

            if (idx < 0) idx = 0;
            var half = snippetLength / 2;
            var start = Math.Max(0, idx - half);
            var end = Math.Min(source.Length, start + snippetLength);
            var slice = source.Substring(start, Math.Max(0, end - start));
            return CleanExtractedText(slice);
        }

        public class ModeratorInsertRequest
        {
            public int LecturerToolkitEntryId { get; set; }
            public string? Query { get; set; }
            public string? QualificationCode { get; set; }
            public string? QualificationDescription { get; set; }
            public string? SubjectDescription { get; set; }
            public string? SubjectCode { get; set; }
            public string? TopicDescription { get; set; }
            public string? AssessmentCriteriaDescription { get; set; }
            public string? LessonPlanDescription { get; set; }
            public bool Cite { get; set; }
            public bool Holistic { get; set; }
            public int CandidateLimit { get; set; } = 8;
            public int SnippetLength { get; set; } = 1800;
            public bool DryRun { get; set; }
            public bool UseHierarchicalCascade { get; set; } = true;
        }

        private sealed class RankedMaterial
        {
            public int MaterialId { get; set; }
            public string Title { get; set; } = string.Empty;
            public string Snippet { get; set; } = string.Empty;
            public string FullText { get; set; } = string.Empty;
            public int Score { get; set; }
        }

        [HttpPost("moderator-insert-best-context")]
        public async Task<IActionResult> ModeratorInsertBestContext([FromBody] ModeratorInsertRequest req)
        {
            if (req == null) return BadRequest("Request body is required");
            if (req.LecturerToolkitEntryId <= 0) return BadRequest("LecturerToolkitEntryId is required");

            var entry = _context.LecturerToolkitEntries.Find(req.LecturerToolkitEntryId);
            if (entry == null) return NotFound("Toolkit entry not found");

            var query = string.IsNullOrWhiteSpace(req.Query)
                ? string.Join(" ", new[]
                {
                    req.QualificationCode,
                    req.QualificationDescription,
                    req.SubjectDescription,
                    req.SubjectCode,
                    req.TopicDescription,
                    req.AssessmentCriteriaDescription,
                    req.LessonPlanDescription
                })
                : req.Query;
            query = Regex.Replace(query ?? string.Empty, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(query)) return BadRequest("Query is empty");

            var limit = req.CandidateLimit <= 0 ? 8 : Math.Min(req.CandidateLimit, 12);
            var snippetLength = req.SnippetLength <= 0 ? (req.Holistic ? 12000 : 1800) : Math.Min(Math.Max(req.SnippetLength, 500), req.Holistic ? 24000 : 3000);
            var terms = TokenizeQuery(query);
            if (terms.Count == 0) terms.Add(query.ToLowerInvariant());

            var ctxQualificationCode = NormalizeForMatch(req.QualificationCode);
            var ctxQualification = NormalizeForMatch(req.QualificationDescription);
            var ctxSubject = NormalizeForMatch(req.SubjectDescription);
            var ctxSubjectCode = NormalizeForMatch(req.SubjectCode);
            var ctxTopic = NormalizeForMatch(req.TopicDescription);
            var ctxLesson = NormalizeForMatch(req.LessonPlanDescription);
            var ctxCriteria = NormalizeForMatch(req.AssessmentCriteriaDescription);

            var candidates = _context.SourceMaterials
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => new
                {
                    s.Id,
                    s.Title,
                    s.ExtractedText,
                    s.QualificationCode,
                    s.QualificationDescription,
                    s.SubjectDescription,
                    s.TopicDescription,
                    s.AssessmentCriteriaDescription
                })
                .ToList();

            var ranked = new List<RankedMaterial>();
            foreach (var c in candidates)
            {
                var text = c.ExtractedText ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text)) continue;
                var textScore = ScoreText(query, terms, text);
                if (textScore <= 0) continue;

                var score = textScore + ScoreContext(
                    title: c.Title ?? string.Empty,
                    qualificationCode: c.QualificationCode ?? string.Empty,
                    qualification: c.QualificationDescription ?? string.Empty,
                    subject: c.SubjectDescription ?? string.Empty,
                    topic: c.TopicDescription ?? string.Empty,
                    criteria: c.AssessmentCriteriaDescription ?? string.Empty,
                    ctxQualificationCode: ctxQualificationCode,
                    ctxQualification: ctxQualification,
                    ctxSubject: ctxSubject,
                    ctxSubjectCode: ctxSubjectCode,
                    ctxTopic: ctxTopic,
                    ctxCriteria: ctxCriteria
                );

                if (req.UseHierarchicalCascade)
                {
                    var h = ScoreHierarchicalCascade(
                        title: c.Title ?? string.Empty,
                        text: text,
                        qualification: c.QualificationDescription ?? string.Empty,
                        subject: c.SubjectDescription ?? string.Empty,
                        topic: c.TopicDescription ?? string.Empty,
                        criteria: c.AssessmentCriteriaDescription ?? string.Empty,
                        ctxQualification: ctxQualification,
                        ctxSubject: ctxSubject,
                        ctxSubjectCode: ctxSubjectCode,
                        ctxTopic: ctxTopic,
                        ctxLesson: ctxLesson,
                        ctxCriteria: ctxCriteria
                    );
                    if (h.blocked) continue;
                    score += h.score;
                }

                ranked.Add(new RankedMaterial
                {
                    MaterialId = c.Id,
                    Title = string.IsNullOrWhiteSpace(c.Title) ? $"Material {c.Id}" : c.Title,
                    Snippet = BuildSnippet(text, query, terms, 700),
                    FullText = text,
                    Score = score
                });
            }

            ranked = ranked
                .OrderByDescending(x => x.Score)
                .Take(limit)
                .ToList();

            if (ranked.Count == 0)
                return NotFound("No matching local context found.");

            var aiMode = AiRuntime.GetMode();
            var selectedIndex = 0;
            var selectionBackend = "local_rank";
            var moderatorRaw = string.Empty;

            var localSelected = await SelectBestCandidateWithLocalLlmAsync(
                query,
                req.QualificationDescription ?? string.Empty,
                req.SubjectDescription ?? string.Empty,
                req.SubjectCode ?? string.Empty,
                req.TopicDescription ?? string.Empty,
                req.AssessmentCriteriaDescription ?? string.Empty,
                req.LessonPlanDescription ?? string.Empty,
                ranked);
            if (localSelected.index >= 0 && localSelected.index < ranked.Count)
            {
                selectedIndex = localSelected.index;
                selectionBackend = "local_llm_selector";
                moderatorRaw = localSelected.rawText;
            }

            var chosen = ranked[selectedIndex];
            string incoming;
            if (req.Holistic) {
                incoming = await ExtractHolisticContentWithLocalLlmAsync(chosen.FullText, query, terms, snippetLength);
            } else {
                incoming = BuildSnippet(chosen.FullText, query, terms, snippetLength).Trim();
            }
            
            if (string.IsNullOrWhiteSpace(incoming)) incoming = chosen.Snippet.Trim();
            if (string.IsNullOrWhiteSpace(incoming))
                return BadRequest("Selected context is empty.");

            if (req.Cite) incoming = $"{incoming}\n\n[CITE]";

            if (req.DryRun)
            {
                return Ok(new
                {
                    saved = false,
                    appended = false,
                    dryRun = true,
                    proposedContent = incoming,
                    selectedMaterialId = chosen.MaterialId,
                    selectedTitle = chosen.Title,
                    selectedScore = chosen.Score,
                    candidateCount = ranked.Count,
                    selectionBackend,
                    aiMode,
                    moderatorRaw
                });
            }

            var existing = (entry.LessonPlanContent ?? "").Trim();
            if (string.Equals(existing, incoming, StringComparison.Ordinal))
            {
                return Ok(new
                {
                    saved = true,
                    appended = false,
                    reason = "duplicate_exact",
                    selectedMaterialId = chosen.MaterialId,
                    selectedTitle = chosen.Title,
                    selectedScore = chosen.Score,
                    candidateCount = ranked.Count,
                    selectionBackend,
                    aiMode,
                    moderatorRaw
                });
            }
            if (!string.IsNullOrEmpty(existing) && existing.Contains(incoming, StringComparison.Ordinal))
            {
                return Ok(new
                {
                    saved = true,
                    appended = false,
                    reason = "duplicate_segment",
                    selectedMaterialId = chosen.MaterialId,
                    selectedTitle = chosen.Title,
                    selectedScore = chosen.Score,
                    candidateCount = ranked.Count,
                    selectionBackend,
                    aiMode,
                    moderatorRaw
                });
            }

            entry.LessonPlanContent = string.IsNullOrEmpty(existing)
                ? incoming
                : $"{existing}\n\n{incoming}";
            _context.SaveChanges();

            return Ok(new
            {
                saved = true,
                appended = true,
                selectedMaterialId = chosen.MaterialId,
                selectedTitle = chosen.Title,
                selectedScore = chosen.Score,
                candidateCount = ranked.Count,
                selectionBackend,
                aiMode,
                moderatorRaw
            });
        }

        private async Task<(int index, string rawText)> SelectBestCandidateWithLocalLlmAsync(
            string query,
            string qualificationDescription,
            string subjectDescription,
            string subjectCode,
            string topicDescription,
            string criteriaDescription,
            string lessonPlanDescription,
            List<RankedMaterial> ranked)
        {
            var endpoint = AiRuntime.GetLocalLlmEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint)) return (-1, string.Empty);

            var systemPrompt =
                "You choose the single best teaching context candidate for lesson planning.\n" +
                "Output only one integer index from 1..N. No words.";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Select one best candidate by index.");
            sb.AppendLine($"Query: {query}");
            if (!string.IsNullOrWhiteSpace(qualificationDescription)) sb.AppendLine($"Qualification: {qualificationDescription}");
            if (!string.IsNullOrWhiteSpace(subjectDescription)) sb.AppendLine($"Subject: {subjectDescription}");
            if (!string.IsNullOrWhiteSpace(subjectCode)) sb.AppendLine($"SubjectCode: {subjectCode}");
            if (!string.IsNullOrWhiteSpace(topicDescription)) sb.AppendLine($"Topic: {topicDescription}");
            if (!string.IsNullOrWhiteSpace(criteriaDescription)) sb.AppendLine($"AssessmentCriteria: {criteriaDescription}");
            if (!string.IsNullOrWhiteSpace(lessonPlanDescription)) sb.AppendLine($"LessonPlan: {lessonPlanDescription}");
            sb.AppendLine("Candidates:");
            for (var i = 0; i < ranked.Count; i++)
            {
                var c = ranked[i];
                sb.AppendLine($"[{i + 1}] {c.Title}");
                sb.AppendLine($"Score: {c.Score}");
                sb.AppendLine($"Snippet: {c.Snippet}");
            }

            var payload = new
            {
                model = AiRuntime.GetLocalLlmModel(),
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = sb.ToString() }
                },
                temperature = 0.1
            };

            using var msg = new HttpRequestMessage(HttpMethod.Post, endpoint.Trim());
            var localApiKey = AiRuntime.GetLocalLlmApiKey();
            if (!string.IsNullOrWhiteSpace(localApiKey))
            {
                var token = localApiKey.Trim();
                if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    token = token.Substring(7).Trim();
                if (!string.IsNullOrWhiteSpace(token))
                    msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            msg.Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

            try
            {
                var resp = await _http.SendAsync(msg);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) return (-1, string.Empty);

                var text = TryExtractChatCompletionText(body) ?? TryExtractResponseOutputTextForModerator(body);
                if (string.IsNullOrWhiteSpace(text)) return (-1, string.Empty);
                var m = Regex.Match(text, @"\b([1-9]\d{0,2})\b");
                if (!m.Success) return (-1, text.Trim());
                if (!int.TryParse(m.Groups[1].Value, out var oneBased)) return (-1, text.Trim());
                return (oneBased - 1, text.Trim());
            }
            catch
            {
                return (-1, string.Empty);
            }
        }

        private async Task<(int index, string rawText)> SelectBestCandidateWithModeratorAsync(
            string responsesEndpoint,
            string apimSubscriptionKey,
            string foundryApiKey,
            string bearerToken,
            string query,
            string qualificationDescription,
            string subjectDescription,
            string subjectCode,
            string topicDescription,
            string criteriaDescription,
            string lessonPlanDescription,
            List<RankedMaterial> ranked)
        {
            var systemPrompt =
                "You choose the single best teaching context candidate for lesson planning.\n" +
                "Output only one integer index from 1..N. No words.";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Select one best candidate by index.");
            sb.AppendLine($"Query: {query}");
            if (!string.IsNullOrWhiteSpace(qualificationDescription)) sb.AppendLine($"Qualification: {qualificationDescription}");
            if (!string.IsNullOrWhiteSpace(subjectDescription)) sb.AppendLine($"Subject: {subjectDescription}");
            if (!string.IsNullOrWhiteSpace(subjectCode)) sb.AppendLine($"SubjectCode: {subjectCode}");
            if (!string.IsNullOrWhiteSpace(topicDescription)) sb.AppendLine($"Topic: {topicDescription}");
            if (!string.IsNullOrWhiteSpace(criteriaDescription)) sb.AppendLine($"AssessmentCriteria: {criteriaDescription}");
            if (!string.IsNullOrWhiteSpace(lessonPlanDescription)) sb.AppendLine($"LessonPlan: {lessonPlanDescription}");
            sb.AppendLine("Candidates:");
            for (var i = 0; i < ranked.Count; i++)
            {
                var c = ranked[i];
                sb.AppendLine($"[{i + 1}] {c.Title}");
                sb.AppendLine($"Score: {c.Score}");
                sb.AppendLine($"Snippet: {c.Snippet}");
            }

            var text = await SendModeratorSelectionRequestAsync(
                responsesEndpoint,
                apimSubscriptionKey,
                foundryApiKey,
                bearerToken,
                sb.ToString(),
                systemPrompt);

            if (string.IsNullOrWhiteSpace(text)) return (-1, string.Empty);

            var m = Regex.Match(text, @"\b([1-9]\d{0,2})\b");
            if (!m.Success) return (-1, text.Trim());
            if (!int.TryParse(m.Groups[1].Value, out var oneBased)) return (-1, text.Trim());
            return (oneBased - 1, text.Trim());
        }

        private async Task<string?> SendModeratorSelectionRequestAsync(
            string responsesEndpoint,
            string apimSubscriptionKey,
            string foundryApiKey,
            string bearerToken,
            string userContent,
            string systemPrompt)
        {
            var url = responsesEndpoint.Trim();
            var payload = new
            {
                input = userContent,
                instructions = systemPrompt
            };

            using var msg = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrWhiteSpace(bearerToken))
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            if (!string.IsNullOrWhiteSpace(apimSubscriptionKey))
                msg.Headers.Add("Ocp-Apim-Subscription-Key", apimSubscriptionKey);
            if (!string.IsNullOrWhiteSpace(foundryApiKey))
                msg.Headers.Add("api-key", foundryApiKey);
            msg.Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

            var resp = await _http.SendAsync(msg);
            var json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return null;
            return TryExtractResponseOutputTextForModerator(json);
        }

        private static string? TryExtractResponseOutputTextForModerator(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var itemType) || itemType.GetString() != "message")
                    continue;

                if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var partType) &&
                        partType.GetString() == "output_text" &&
                        part.TryGetProperty("text", out var textProp))
                    {
                        var text = textProp.GetString();
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                }
            }
            return null;
        }

        private async Task<string?> GetFoundryBearerTokenForModeratorAsync()
        {
            var direct = Environment.GetEnvironmentVariable("FOUNDRY_BEARER_TOKEN");
            if (!string.IsNullOrWhiteSpace(direct)) return direct.Trim();

            var tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
            var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
            var clientSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");
            if (!string.IsNullOrWhiteSpace(tenantId) &&
                !string.IsNullOrWhiteSpace(clientId) &&
                !string.IsNullOrWhiteSpace(clientSecret))
            {
                var tokenUrl = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
                using var form = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("client_id", clientId),
                    new KeyValuePair<string, string>("client_secret", clientSecret),
                    new KeyValuePair<string, string>("grant_type", "client_credentials"),
                    new KeyValuePair<string, string>("scope", "https://ml.azure.com/.default")
                });

                using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl) { Content = form };
                using var resp = await _http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var tokenJson = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(tokenJson);
                    if (doc.RootElement.TryGetProperty("access_token", out var at))
                    {
                        var token = at.GetString();
                        if (!string.IsNullOrWhiteSpace(token)) return token;
                    }
                }
            }

            return await TryGetTokenFromAzureCliForModeratorAsync();
        }

        private static async Task<string?> TryGetTokenFromAzureCliForModeratorAsync()
        {
            var azPath = Environment.GetEnvironmentVariable("AZ_CLI_PATH");
            if (string.IsNullOrWhiteSpace(azPath))
            {
                azPath = @"C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd";
            }

            if (!System.IO.File.Exists(azPath)) return null;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = azPath,
                Arguments = "account get-access-token --resource https://ml.azure.com/ --query accessToken -o tsv",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0) return null;

            var token = output.Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }

        private static string GetModeratorResponsesEndpoint()
        {
            var explicitResponses = Environment.GetEnvironmentVariable("FOUNDRY_RESPONSES_ENDPOINT");
            if (!string.IsNullOrWhiteSpace(explicitResponses)) return explicitResponses.Trim();

            var projectEndpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT");
            if (!string.IsNullOrWhiteSpace(projectEndpoint))
            {
                var appName = Environment.GetEnvironmentVariable("FOUNDRY_APPLICATION_NAME");
                if (string.IsNullOrWhiteSpace(appName)) appName = "Moderator";
                var apiVersion = Environment.GetEnvironmentVariable("FOUNDRY_API_VERSION");
                if (string.IsNullOrWhiteSpace(apiVersion)) apiVersion = "2025-11-15-preview";
                return $"{projectEndpoint.Trim().TrimEnd('/')}/applications/{Uri.EscapeDataString(appName)}/protocols/openai/responses?api-version={Uri.EscapeDataString(apiVersion)}";
            }

            var apimEndpoint = Environment.GetEnvironmentVariable("FOUNDRY_APIM_ENDPOINT");
            if (!string.IsNullOrWhiteSpace(apimEndpoint))
                return apimEndpoint.Trim().TrimEnd('/') + "/openai/responses";

            return DefaultModeratorResponsesEndpoint;
        }

        private static Dictionary<string, string> ReadGoogleEnvMap()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = Environment.GetEnvironmentVariable("GOOGLE_ENV_PATH");
                if (string.IsNullOrWhiteSpace(path)) path = DefaultGoogleEnvPath;
                if (!System.IO.File.Exists(path)) return result;

                var lines = System.IO.File.ReadAllLines(path);
                string? firstRawValue = null;
                foreach (var raw in lines)
                {
                    var line = (raw ?? "").Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    var idx = line.IndexOf('=');
                    if (idx > 0)
                    {
                        var key = line.Substring(0, idx).Trim();
                        var val = line.Substring(idx + 1).Trim().Trim('"');
                        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(val))
                            result[key] = val;
                    }
                    else if (firstRawValue == null)
                    {
                        firstRawValue = line.Trim('"');
                    }
                }

                if (!string.IsNullOrWhiteSpace(firstRawValue))
                    result["__RAW__"] = firstRawValue;
            }
            catch { }
            return result;
        }

        [HttpPost("store-searx-url")]
        public IActionResult StoreSearxUrl([FromBody] StoreSearxUrlRequest req)
        {
            var url = (req?.Url ?? "").Trim();
            if (string.IsNullOrEmpty(url)) return BadRequest("Url required");
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "ETDP");
            Directory.CreateDirectory(dir);
            var cfgPath = Path.Combine(dir, "config.json");
            var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (System.IO.File.Exists(cfgPath))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(cfgPath);
                    var doc = JsonDocument.Parse(json);
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        cfg[p.Name] = p.Value.GetString() ?? "";
                    }
                }
                catch { }
            }
            cfg["searx_url"] = url;
            var outJson = JsonSerializer.Serialize(cfg);
            System.IO.File.WriteAllText(cfgPath, outJson);
            return Ok(new { saved = true, searx_url = url });
        }

        private static string GetSearxUrl()
        {
            var env = Environment.GetEnvironmentVariable("SEARX_URL");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var cfgPath = Path.Combine(appData, "ETDP", "config.json");
                if (System.IO.File.Exists(cfgPath))
                {
                    var json = System.IO.File.ReadAllText(cfgPath);
                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("searx_url", out var v))
                    {
                        var s = v.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
            }
            catch { }
            return "";
        }

        public class StoreSearxUrlRequest
        {
            public string Url { get; set; } = "";
        }

        [HttpPost("store-google")]
        public IActionResult StoreGoogle([FromBody] StoreGoogleRequest req)
        {
            var cx = (req?.Cx ?? "").Trim();
            var key = (req?.Key ?? "").Trim();
            if (string.IsNullOrEmpty(cx) && string.IsNullOrEmpty(key)) return BadRequest("Cx or Key required");
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "ETDP");
            Directory.CreateDirectory(dir);
            var cfgPath = Path.Combine(dir, "config.json");
            var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (System.IO.File.Exists(cfgPath))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(cfgPath);
                    var doc = JsonDocument.Parse(json);
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        cfg[p.Name] = p.Value.GetString() ?? "";
                    }
                }
                catch { }
            }
            if (!string.IsNullOrEmpty(cx)) cfg["google_cx"] = cx;
            if (!string.IsNullOrEmpty(key)) cfg["google_key"] = key;
            var outJson = JsonSerializer.Serialize(cfg);
            System.IO.File.WriteAllText(cfgPath, outJson);
            return Ok(new { saved = true, google_cx = cx, google_key = string.IsNullOrEmpty(key) ? null : "***" });
        }

        [HttpGet("google-config")]
        public IActionResult GetGoogleConfig()
        {
            var cx = GetGoogleCx();
            var key = GetGoogleKey();
            return Ok(new
            {
                google_cx = string.IsNullOrWhiteSpace(cx) ? null : cx,
                google_key_present = !string.IsNullOrWhiteSpace(key)
            });
        }

        private static string GetGoogleCx()
        {
            var env = Environment.GetEnvironmentVariable("GOOGLE_SEARCH_CX");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var cfgPath = Path.Combine(appData, "ETDP", "config.json");
                if (System.IO.File.Exists(cfgPath))
                {
                    var json = System.IO.File.ReadAllText(cfgPath);
                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("google_cx", out var v))
                    {
                        var s = v.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
            }
            catch { }
            var map = ReadGoogleEnvMap();
            if (map.TryGetValue("GOOGLE_SEARCH_CX", out var cx) && !string.IsNullOrWhiteSpace(cx)) return cx;
            if (map.TryGetValue("GOOGLE_CX", out cx) && !string.IsNullOrWhiteSpace(cx)) return cx;
            if (map.TryGetValue("CX", out cx) && !string.IsNullOrWhiteSpace(cx)) return cx;
            return "";
        }

        private static string GetGoogleKey()
        {
            var env = Environment.GetEnvironmentVariable("GOOGLE_SEARCH_KEY");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var cfgPath = Path.Combine(appData, "ETDP", "config.json");
                if (System.IO.File.Exists(cfgPath))
                {
                    var json = System.IO.File.ReadAllText(cfgPath);
                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("google_key", out var v))
                    {
                        var s = v.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
            }
            catch { }
            var map = ReadGoogleEnvMap();
            if (map.TryGetValue("GOOGLE_SEARCH_KEY", out var key) && !string.IsNullOrWhiteSpace(key)) return key;
            if (map.TryGetValue("GOOGLE_API_KEY", out key) && !string.IsNullOrWhiteSpace(key)) return key;
            if (map.TryGetValue("GOOGLE_KEY", out key) && !string.IsNullOrWhiteSpace(key)) return key;
            if (map.TryGetValue("API_KEY", out key) && !string.IsNullOrWhiteSpace(key)) return key;
            if (map.TryGetValue("__RAW__", out key) && !string.IsNullOrWhiteSpace(key)) return key;
            return "";
        }

        public class StoreGoogleRequest
        {
            public string Cx { get; set; } = "";
            public string Key { get; set; } = "";
        }

        [HttpPost("store-bing")]
        public IActionResult StoreBing([FromBody] StoreBingRequest req)
        {
            var key = (req?.Key ?? "").Trim();
            if (string.IsNullOrEmpty(key)) return BadRequest("Key required");
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "ETDP");
            Directory.CreateDirectory(dir);
            var cfgPath = Path.Combine(dir, "config.json");
            var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (System.IO.File.Exists(cfgPath))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(cfgPath);
                    var doc = JsonDocument.Parse(json);
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        cfg[p.Name] = p.Value.GetString() ?? "";
                    }
                }
                catch { }
            }
            cfg["bing_key"] = key;
            var outJson = JsonSerializer.Serialize(cfg);
            System.IO.File.WriteAllText(cfgPath, outJson);
            return Ok(new { saved = true });
        }

        private static string GetBingKey()
        {
            var env = Environment.GetEnvironmentVariable("BING_SEARCH_KEY");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var cfgPath = Path.Combine(appData, "ETDP", "config.json");
                if (System.IO.File.Exists(cfgPath))
                {
                    var json = System.IO.File.ReadAllText(cfgPath);
                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("bing_key", out var v))
                    {
                        var s = v.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
            }
            catch { }
            return "";
        }

        private static string GetAzureAiSearchEndpoint()
        {
            var env = Environment.GetEnvironmentVariable("AZURE_AI_SEARCH_ENDPOINT");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            env = Environment.GetEnvironmentVariable("AZURE_SEARCH_ENDPOINT");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            return GetConfigValue("azure_ai_search_endpoint");
        }

        private static string GetAzureAiSearchIndex()
        {
            var env = Environment.GetEnvironmentVariable("AZURE_AI_SEARCH_INDEX");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            env = Environment.GetEnvironmentVariable("AZURE_SEARCH_INDEX");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            return GetConfigValue("azure_ai_search_index");
        }

        private static string GetAzureAiSearchKey()
        {
            var env = Environment.GetEnvironmentVariable("AZURE_AI_SEARCH_KEY");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            env = Environment.GetEnvironmentVariable("AZURE_SEARCH_KEY");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            return GetConfigValue("azure_ai_search_key");
        }

        private static string GetBingCustomConfigId()
        {
            var env = Environment.GetEnvironmentVariable("BING_CUSTOM_CONFIG_ID");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            env = Environment.GetEnvironmentVariable("BING_CUSTOM_SEARCH_CONFIG");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            env = Environment.GetEnvironmentVariable("BING_CUSTOM_SEARCH_CUSTOM_CONFIG");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            return GetConfigValue("bing_custom_config_id");
        }

        private static string GetBingCustomSearchEndpoint()
        {
            var env = Environment.GetEnvironmentVariable("BING_CUSTOM_SEARCH_ENDPOINT");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            return "https://api.bing.microsoft.com/v7.0/custom/search";
        }

        private static string GetBingSearchEndpoint()
        {
            var env = Environment.GetEnvironmentVariable("BING_SEARCH_ENDPOINT");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            return "https://api.bing.microsoft.com/v7.0/search";
        }

        private static bool GetOpenAipSearchEnabled()
        {
            // Disabled explicitly: this external source is not trusted for this deployment.
            return false;
        }

        private static string GetOpenAipApiBaseUrl()
        {
            var env = Environment.GetEnvironmentVariable("OPENAIP_API_BASE");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

            env = Environment.GetEnvironmentVariable("FIGSHARE_API_BASE");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

            var cfg = GetConfigValue("openaip_api_base");
            if (!string.IsNullOrWhiteSpace(cfg)) return cfg.Trim();

            cfg = GetConfigValue("figshare_api_base");
            if (!string.IsNullOrWhiteSpace(cfg)) return cfg.Trim();

            return "https://api.figshare.com/v2";
        }

        private static string GetOpenAipToken()
        {
            var env = Environment.GetEnvironmentVariable("OPENAIP_TOKEN");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

            env = Environment.GetEnvironmentVariable("FIGSHARE_TOKEN");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

            var cfg = GetConfigValue("openaip_token");
            if (!string.IsNullOrWhiteSpace(cfg)) return cfg.Trim();

            cfg = GetConfigValue("figshare_token");
            if (!string.IsNullOrWhiteSpace(cfg)) return cfg.Trim();

            return string.Empty;
        }

        private static string GetConfigValue(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "";
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var cfgPath = Path.Combine(appData, "ETDP", "config.json");
                if (!System.IO.File.Exists(cfgPath)) return "";
                var json = System.IO.File.ReadAllText(cfgPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty(key, out var v))
                {
                    var s = v.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                }
            }
            catch { }
            return "";
        }

        public class StoreBingRequest
        {
            public string Key { get; set; } = "";
        }

        public class StoreEnvFileRequest
        {
            public string Path { get; set; } = "";
        }

        [HttpPost("store-env-file")]
        public IActionResult StoreEnvFile([FromBody] StoreEnvFileRequest req)
        {
            var path = (req?.Path ?? "").Trim();
            if (string.IsNullOrEmpty(path)) return BadRequest("Path required");
            if (!System.IO.File.Exists(path)) return NotFound("Env file not found: " + path);
            var lines = System.IO.File.ReadAllLines(path);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in lines)
            {
                var line = (raw ?? "").Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var idx = line.IndexOf('=');
                if (idx <= 0) continue;
                var key = line.Substring(0, idx).Trim();
                var val = line.Substring(idx + 1).Trim().Trim('"');
                map[key] = val;
            }
            var savedKeys = new List<string>();
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "ETDP");
            Directory.CreateDirectory(dir);
            var cfgPath = Path.Combine(dir, "config.json");
            var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (System.IO.File.Exists(cfgPath))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(cfgPath);
                    var doc = JsonDocument.Parse(json);
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        cfg[p.Name] = p.Value.GetString() ?? "";
                    }
                }
                catch { }
            }
            void SaveIf(string envKey, string cfgKey)
            {
                if (map.TryGetValue(envKey, out var v) && !string.IsNullOrWhiteSpace(v))
                {
                    cfg[cfgKey] = v.Trim();
                    savedKeys.Add(cfgKey);
                }
            }
            SaveIf("GOOGLE_SEARCH_KEY", "google_key");
            SaveIf("GOOGLE_SEARCH_CX", "google_cx");
            SaveIf("BING_SEARCH_KEY", "bing_key");
            SaveIf("SEARX_URL", "searx_url");
            SaveIf("AZURE_PROJECT_ENDPOINT", "azure_project_endpoint");
            SaveIf("AZURE_AGENT_NAME", "azure_agent_name");
            SaveIf("OPENAIP_TOKEN", "openaip_token");
            SaveIf("OPENAIP_API_BASE", "openaip_api_base");
            SaveIf("OPENAIP_SEARCH_ENABLED", "openaip_search_enabled");
            var outJson = JsonSerializer.Serialize(cfg);
            System.IO.File.WriteAllText(cfgPath, outJson);
            return Ok(new { saved = true, keys = savedKeys });
        }

        public class FetchUrlRequest { public string Url { get; set; } = string.Empty; }

        [HttpPost("fetch-url")]
        public async Task<IActionResult> FetchUrl([FromBody] FetchUrlRequest req)
        {
            if (!AiRuntime.AllowCloudProviders())
            {
                return StatusCode(StatusCodes.Status403Forbidden, "URL fetch is disabled in offline mode.");
            }
            if (string.IsNullOrWhiteSpace(req.Url)) return BadRequest("Url is required");
            using var reqMsg = new HttpRequestMessage(HttpMethod.Get, req.Url);
            reqMsg.Headers.UserAgent.Add(new ProductInfoHeaderValue("ETDP", "1.0"));
            reqMsg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            var resp = await _http.SendAsync(reqMsg);
            if (!resp.IsSuccessStatusCode)
                return StatusCode((int)resp.StatusCode, $"Fetch failed: {resp.StatusCode}");
            var html = await resp.Content.ReadAsStringAsync();
            html = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
            var text = Regex.Replace(html, "<[^>]+>", " ");
            text = System.Web.HttpUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return Ok(new { text });
        }

        [HttpPost("upload-source")]
        public async Task<IActionResult> UploadSource([FromForm] IFormFile file)
        {
            if (file == null || file.Length == 0) return BadRequest("No file");
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!KnowledgeUploadExtensions.Contains(ext))
                return BadRequest("Supported: .txt, .md, .docx, .pdf, .pptx, .csv, .json, .jsonl, .xml, .yml, .yaml, .html, .htm, .png, .jpg, .jpeg, .webp, .gif, .bmp, .tif, .tiff, .svg");

            var tmpPath = Path.Combine(Path.GetTempPath(), $"etdp_upload_source_{Guid.NewGuid():N}{ext}");
            string text = string.Empty;
            try
            {
                await using (var outFs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await file.CopyToAsync(outFs);
                }

                if (KnowledgeImageExtensions.Contains(ext))
                {
                    text = ExtractTextFromImageFile(tmpPath);
                }
                else
                {
                    await using var stream = new FileStream(tmpPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    text = await ExtractTextFromFileStreamAsync(stream, ext);
                }

                text = await ApplyOcrEnhancementAsync(tmpPath, ext, text);
            }
            finally
            {
                try
                {
                    if (System.IO.File.Exists(tmpPath)) System.IO.File.Delete(tmpPath);
                }
                catch { }
            }
            return Ok(new { text });
        }

        private async Task<string> ApplyOcrEnhancementAsync(string filePath, string ext, string? extractedText, bool runCognitiveClean = true)
        {
            var text = extractedText ?? string.Empty;
            try
            {
                text = await _ocrExtractionService.EnhanceExtractedTextAsync(filePath, ext, text);
            }
            catch
            {
                // best effort: keep extracted text fallback
            }

            return runCognitiveClean
                ? ApplyCognitiveDocumentScanClean(text, ext)
                : CleanExtractedText(text);
        }

        public class UploadMaterialRequest
        {
            public string? Title { get; set; }
            public int? QualificationId { get; set; }
            public string? QualificationDescription { get; set; }
            public string? SubjectDescription { get; set; }
            public string? TopicDescription { get; set; }
            public string? AssessmentCriteriaDescription { get; set; }
            public bool RunCognitiveClean { get; set; } = true;
        }

        public class UploadMaterialToBlobResponse
        {
            public int Id { get; set; }
            public string Title { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
            public string BlobUrl { get; set; } = string.Empty;
            public string QualificationCode { get; set; } = string.Empty;
            public string QualificationDescription { get; set; } = string.Empty;
            public string SourceType { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public string? Reason { get; set; }
            public int? KnowledgeNumber { get; set; }
            public string? ArchivePath { get; set; }
            public string? QualificationRootPath { get; set; }
            public string? CurriculumLibraryPath { get; set; }
            public string? LocalInboxPath { get; set; }
            public string? LocalArchivePath { get; set; }
        }

        public class CurriculumBenchmarkSummaryRequest
        {
            public int? QualificationId { get; set; }
            public string? QualificationDescription { get; set; }
        }

        public class ImportFolderRequest
        {
            public string QualificationNumber { get; set; } = string.Empty;
        }

        public class GitHubRepoImportRequest
        {
            public string RepoUrl { get; set; } = string.Empty;
            public string? Branch { get; set; }
            public int? QualificationId { get; set; }
            public string? QualificationDescription { get; set; }
            public int MaxFiles { get; set; } = 200;
            public int MaxFileSizeKb { get; set; } = 20480;
            public bool IncludeCodeFiles { get; set; } = false;
        }

        public class OaiPmhHarvestRequest
        {
            public string BaseUrl { get; set; } = string.Empty;
            public string MetadataPrefix { get; set; } = "oai_dc";
            public string? Set { get; set; }
            public string? FromUtc { get; set; }
            public string? UntilUtc { get; set; }
            public int? QualificationId { get; set; }
            public string? QualificationDescription { get; set; }
            public int MaxRecords { get; set; } = 500;
            public bool IncludeDeleted { get; set; } = false;
            public string? ApiKey { get; set; }
            public string? ApiKeyHeaderName { get; set; }
            public string? ApiKeyQueryParam { get; set; }
        }

        public class EngineeringSeedImportRequest
        {
            public string? RootPath { get; set; }
            public int? QualificationId { get; set; }
            public string? QualificationDescription { get; set; }
            public int MaxFiles { get; set; } = 5000;
        }

        public class LocalFolderImportRequest
        {
            public string RootPath { get; set; } = string.Empty;
            public string? KnowledgePool { get; set; }
            public int? QualificationId { get; set; }
            public string? QualificationDescription { get; set; }
            public int MaxFiles { get; set; } = 1000;
            public int MaxFileSizeKb { get; set; } = 20480;
            public bool IncludeCodeFiles { get; set; } = true;
        }

        public class KnowledgeHierarchyScaffoldRequest
        {
            public int? QualificationId { get; set; }
            public string? QualificationCode { get; set; }
            public string? QualificationDescription { get; set; }
        }

        public class SyncKnowledgeHierarchyRequest
        {
            public string? QualificationCode { get; set; }
            public string? QualificationDescription { get; set; }
            public bool IncludeLocalSourceUploads { get; set; } = true;
            public bool IncludeDeveloperKnowledgeBase { get; set; } = true;
            public int MaxFilesPerInbox { get; set; } = 1000;
            public bool RebuildUploadReadme { get; set; } = true;
            public bool ConsolidateLegacyFolders { get; set; } = true;
        }

        public class SyncAgentKnowledgeRequest
        {
            public string? AgentMode { get; set; }
            public bool IncludeSharedKnowledge { get; set; } = true;
            public int MaxFilesPerInbox { get; set; } = 1000;
            public bool RebuildReadme { get; set; } = true;
        }

        public class ConsolidateKnowledgeHierarchyRequest
        {
            public string? QualificationCode { get; set; }
            public bool RebuildUploadReadme { get; set; } = true;
            public bool RemoveEmptyLegacyFolders { get; set; } = true;
        }

        public class IndexQualificationKnowledgeRequest
        {
            public string RootPath { get; set; } = string.Empty;
            public int? QualificationId { get; set; }
            public string? QualificationCode { get; set; }
            public string? QualificationDescription { get; set; }
            public string SourceType { get; set; } = "developer_knowledge_base";
            public int MaxFiles { get; set; } = 1000;
            public bool Recursive { get; set; } = true;
            public int? StartingKnowledgeNumber { get; set; }
        }

        public class LaunchKnowledgeFocusRequest
        {
            public int? QualificationId { get; set; }
            public string? QualificationCode { get; set; }
            public string? QualificationDescription { get; set; }
            public string EducationRootPath { get; set; } = @"E:\ETDP\VocationalLLM\data\knowledge_taxonomy\scientific_fields\education\higher-education-and-vocational-pedagogy";
            public string EngineeringRootPath { get; set; } = @"E:\ETDP\VocationalLLM\data\knowledge_taxonomy\scientific_fields\engineering";
            public int EducationMaxFiles { get; set; } = 12000;
            public int EngineeringMaxFiles { get; set; } = 12000;
            public int LessonPlanMaxRows { get; set; } = 0;
            public bool Recursive { get; set; } = true;
            public bool IncludeLessonPlanContent { get; set; } = true;
            public bool StopOnStageFailure { get; set; } = true;
        }

        private static string NormalizeMaterialIdentity(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
        }

        private static string BuildFileIdentityKey(string? fileName, string? fileType)
        {
            var normalizedFileName = NormalizeMaterialIdentity(fileName);
            var normalizedFileType = NormalizeMaterialIdentity(fileType);
            if (string.IsNullOrWhiteSpace(normalizedFileName) || string.IsNullOrWhiteSpace(normalizedFileType))
                return string.Empty;
            return $"{normalizedFileName}|{normalizedFileType}";
        }

        private static string EnsureUniqueFilePath(string path)
        {
            if (!System.IO.File.Exists(path))
                return path;

            var directory = Path.GetDirectoryName(path) ?? string.Empty;
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            var index = 2;

            while (true)
            {
                var candidate = Path.Combine(directory, $"{stem}_{index}{ext}");
                if (!System.IO.File.Exists(candidate))
                    return candidate;
                index++;
            }
        }

        private sealed class IndexedKnowledgeUploadResult
        {
            public SourceMaterial? Material { get; set; }
            public KnowledgeHierarchyService.StructureInfo? Structure { get; set; }
            public KnowledgeHierarchyService.SyncDetail? Detail { get; set; }
            public string QualificationCode { get; set; } = string.Empty;
            public string QualificationDescription { get; set; } = string.Empty;
            public string SourceType { get; set; } = "local_source_upload";
            public string StagedFileName { get; set; } = string.Empty;
        }

        private sealed class StagedKnowledgeUpload
        {
            public string OriginalFileName { get; set; } = string.Empty;
            public string StagedFileName { get; set; } = string.Empty;
            public string FileType { get; set; } = string.Empty;
        }

        private (Qualification? qualification, string qualificationCode, string qualificationDescription) ResolveKnowledgeUploadQualification(UploadMaterialRequest? meta)
        {
            Qualification? qualification = null;
            if (meta?.QualificationId.HasValue == true && meta.QualificationId.Value > 0)
            {
                qualification = _context.Qualifications.Find(meta.QualificationId.Value);
            }

            var qualificationDescription = ResolveQualificationDescription(meta);
            if (qualification == null && !string.IsNullOrWhiteSpace(qualificationDescription))
            {
                qualification = _context.Qualifications.FirstOrDefault(q => q.QualificationDescription == qualificationDescription)
                    ?? _context.Qualifications.FirstOrDefault(q => q.QualificationDescription.Contains(qualificationDescription));
            }

            if (qualification == null && string.IsNullOrWhiteSpace(qualificationDescription))
            {
                return (null, string.Empty, string.Empty);
            }

            var qualificationCode = !string.IsNullOrWhiteSpace(qualification?.QualificationNumber)
                ? qualification!.QualificationNumber.Trim()
                : ResolveQualificationCode(qualificationDescription, meta?.QualificationId);
            if (string.IsNullOrWhiteSpace(qualificationDescription))
            {
                qualificationDescription = qualification?.QualificationDescription?.Trim() ?? string.Empty;
            }

            return (qualification, qualificationCode, qualificationDescription);
        }

        private static string ResolveKnowledgeSourceRootPath(KnowledgeHierarchyService.StructureInfo structure, string sourceType)
        {
            return string.Equals(sourceType, "developer_knowledge_base", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(structure.QualificationRootPath, "developer_knowledge_base")
                : Path.Combine(structure.QualificationRootPath, "local_source_upload");
        }

        private static string ComposeKnowledgeSubjectDescription(string sourceType, string? subjectDescription)
        {
            var baseValue = $"KnowledgeBase:{sourceType}";
            var subject = (subjectDescription ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(subject)
                ? baseValue
                : $"{baseValue} | Subject:{subject}";
        }

        private static string ComposeKnowledgeTopicDescription(int? knowledgeNumber, string? topicDescription)
        {
            var parts = new List<string>();
            if (knowledgeNumber.HasValue && knowledgeNumber.Value > 0)
            {
                parts.Add($"KnowledgeNumber:{knowledgeNumber.Value:D4}");
            }

            var topic = (topicDescription ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(topic))
            {
                parts.Add($"Topic:{topic}");
            }

            return string.Join(" | ", parts);
        }

        private static string ComposeKnowledgeCriteriaDescription(string? existingValue, string? assessmentCriteriaDescription)
        {
            var parts = new List<string>();
            var existing = (existingValue ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                parts.Add(existing);
            }

            var criteria = (assessmentCriteriaDescription ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(criteria))
            {
                parts.Add($"Criteria:{criteria}");
            }

            return string.Join(" | ", parts
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private void ApplyIndexedUploadMetadata(SourceMaterial material, UploadMaterialRequest? meta, string qualificationCode, string qualificationDescription, string sourceType)
        {
            if (material == null) return;

            material.QualificationCode = qualificationCode;
            material.QualificationDescription = qualificationDescription;
            if (!string.IsNullOrWhiteSpace(meta?.Title))
            {
                material.Title = meta!.Title!.Trim();
            }

            material.SubjectDescription = ComposeKnowledgeSubjectDescription(sourceType, meta?.SubjectDescription);
            material.TopicDescription = ComposeKnowledgeTopicDescription(material.KnowledgeNumber, meta?.TopicDescription);
            material.AssessmentCriteriaDescription = ComposeKnowledgeCriteriaDescription(material.AssessmentCriteriaDescription, meta?.AssessmentCriteriaDescription);
            material.KnowledgeLabel = BuildKnowledgeLabel(
                string.IsNullOrWhiteSpace(meta?.Title) ? material.Title : meta!.Title,
                material.FileName,
                material.KnowledgeNumber);

            if (string.Equals(sourceType, "local_source_upload", StringComparison.OrdinalIgnoreCase) &&
                material.Id > 0 &&
                string.IsNullOrWhiteSpace(material.Url))
            {
                material.Url = BuildLocalMaterialExportUrl(material.Id);
            }
        }

        private KnowledgeHierarchyService.SyncDetail? FindIndexedUploadDetail(
            KnowledgeHierarchyService.SyncResult? sync,
            string qualificationCode,
            string sourceType,
            string stagedFileName)
        {
            if (sync == null || string.IsNullOrWhiteSpace(stagedFileName))
            {
                return null;
            }

            return sync.Details
                .Where(d =>
                    string.Equals(d.QualificationCode, qualificationCode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(d.SourceType, sourceType, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(d.FileName, stagedFileName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => string.Equals(d.Status, "created", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(d => d.KnowledgeNumber ?? 0)
                .FirstOrDefault();
        }

        private SourceMaterial? FindIndexedSourceMaterial(
            string qualificationCode,
            string sourceType,
            string stagedFileName,
            KnowledgeHierarchyService.SyncDetail? detail = null)
        {
            var baseQuery = _context.SourceMaterials.Where(s =>
                (s.QualificationCode ?? string.Empty) == qualificationCode &&
                (s.KnowledgeSourceType ?? string.Empty) == sourceType);

            if (detail?.KnowledgeNumber is int knowledgeNumber)
            {
                var materialByKnowledgeNumber = baseQuery.FirstOrDefault(s => s.KnowledgeNumber == knowledgeNumber);
                if (materialByKnowledgeNumber != null)
                {
                    return materialByKnowledgeNumber;
                }
            }

            var normalizedArchivedPath = NormalizeMaterialIdentity(detail?.ArchivedPath);
            if (!string.IsNullOrWhiteSpace(normalizedArchivedPath))
            {
                var materialByArchivedPath = baseQuery.FirstOrDefault(s =>
                    (s.FilePath ?? string.Empty).Trim().ToLower() == normalizedArchivedPath);
                if (materialByArchivedPath != null)
                {
                    return materialByArchivedPath;
                }
            }

            if (string.IsNullOrWhiteSpace(stagedFileName))
            {
                return null;
            }

            return baseQuery
                .Where(s =>
                    !((s.AssessmentCriteriaDescription ?? string.Empty).Contains("DerivedFromSource:")) &&
                    (((s.AssessmentCriteriaDescription ?? string.Empty).StartsWith($"Source:{stagedFileName}")) ||
                     ((s.AssessmentCriteriaDescription ?? string.Empty).Contains($";Source:{stagedFileName}"))))
                .OrderByDescending(s => s.KnowledgeUploadedAtUtc ?? s.CreatedAt)
                .FirstOrDefault();
        }

        private IActionResult? BuildIndexedUploadFailureResult(IndexedKnowledgeUploadResult result)
        {
            if (result.Detail != null && string.Equals(result.Detail.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    qualificationCode = result.QualificationCode,
                    qualificationDescription = result.QualificationDescription,
                    sourceType = result.SourceType,
                    status = result.Detail.Status,
                    reason = result.Detail.Reason,
                    knowledgeNumber = result.Detail.KnowledgeNumber,
                    archivePath = result.Detail.ArchivedPath,
                    localInboxPath = result.Structure?.LocalInboxPath,
                    localArchivePath = result.Structure?.LocalArchivePath,
                    qualificationRootPath = result.Structure?.QualificationRootPath,
                    curriculumLibraryPath = result.Structure?.CurriculumLibraryPath
                });
            }

            if (result.Material == null)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    qualificationCode = result.QualificationCode,
                    qualificationDescription = result.QualificationDescription,
                    sourceType = result.SourceType,
                    status = result.Detail?.Status ?? "failed",
                    reason = result.Detail?.Reason ?? "No indexed material record was created for the staged upload.",
                    knowledgeNumber = result.Detail?.KnowledgeNumber,
                    archivePath = result.Detail?.ArchivedPath,
                    localInboxPath = result.Structure?.LocalInboxPath,
                    localArchivePath = result.Structure?.LocalArchivePath,
                    qualificationRootPath = result.Structure?.QualificationRootPath,
                    curriculumLibraryPath = result.Structure?.CurriculumLibraryPath
                });
            }

            return null;
        }

        private UploadMaterialToBlobResponse BuildIndexedUploadResponse(
            IndexedKnowledgeUploadResult result,
            string fallbackTitle,
            string fallbackFileType)
        {
            var material = result.Material;
            var blobUrl = material?.Url ?? string.Empty;
            if (string.IsNullOrWhiteSpace(blobUrl) && material?.Id > 0)
            {
                blobUrl = BuildLocalMaterialExportUrl(material.Id);
            }

            return new UploadMaterialToBlobResponse
            {
                Id = material?.Id ?? 0,
                Title = material?.Title ?? fallbackTitle,
                Type = material?.FileType ?? fallbackFileType,
                BlobUrl = blobUrl,
                QualificationCode = result.QualificationCode,
                QualificationDescription = result.QualificationDescription,
                SourceType = result.SourceType,
                Status = result.Detail?.Status ?? (material != null ? "created" : "failed"),
                Reason = result.Detail?.Reason,
                KnowledgeNumber = result.Detail?.KnowledgeNumber ?? material?.KnowledgeNumber,
                ArchivePath = result.Detail?.ArchivedPath ?? material?.FilePath,
                QualificationRootPath = result.Structure?.QualificationRootPath,
                CurriculumLibraryPath = result.Structure?.CurriculumLibraryPath,
                LocalInboxPath = result.Structure?.LocalInboxPath,
                LocalArchivePath = result.Structure?.LocalArchivePath
            };
        }

        private async Task<IndexedKnowledgeUploadResult> UploadLocalKnowledgeViaHierarchyAsync(IFormFile file, UploadMaterialRequest? meta)
        {
            var (_, qualificationCode, qualificationDescription) = ResolveKnowledgeUploadQualification(meta);
            if (string.IsNullOrWhiteSpace(qualificationCode) || string.IsNullOrWhiteSpace(qualificationDescription))
            {
                throw new InvalidOperationException("Qualification selection is required for curriculum-aligned local knowledge upload.");
            }

            var structure = _knowledgeHierarchyService.EnsureQualificationStructure(qualificationCode, qualificationDescription);
            var safeName = Path.GetFileName(file.FileName);
            var inboxPath = EnsureUniqueFilePath(Path.Combine(structure.LocalInboxPath, safeName));

            await using (var fs = new FileStream(inboxPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.CopyToAsync(fs);
            }

            var sync = _knowledgeHierarchyService.SyncKnowledgeHierarchy(new KnowledgeHierarchyService.SyncOptions
            {
                QualificationCode = qualificationCode,
                QualificationDescription = qualificationDescription,
                IncludeLocalSourceUploads = true,
                IncludeDeveloperKnowledgeBase = false,
                MaxFilesPerInbox = 2000,
                RebuildUploadReadme = false,
                ConsolidateLegacyFolders = false
            });

            var indexedFileName = Path.GetFileName(inboxPath);
            var detail = FindIndexedUploadDetail(sync, qualificationCode, "local_source_upload", indexedFileName);
            var material = FindIndexedSourceMaterial(qualificationCode, "local_source_upload", indexedFileName, detail);

            if (material != null)
            {
                ApplyIndexedUploadMetadata(material, meta, qualificationCode, qualificationDescription, "local_source_upload");
                _context.SaveChanges();
            }

            return new IndexedKnowledgeUploadResult
            {
                Material = material,
                Structure = structure,
                Detail = detail,
                QualificationCode = qualificationCode,
                QualificationDescription = qualificationDescription,
                SourceType = "local_source_upload",
                StagedFileName = indexedFileName
            };
        }

        private bool SourceMaterialExistsByPath(string? filePath)
        {
            var normalizedPath = NormalizeMaterialIdentity(filePath);
            if (string.IsNullOrWhiteSpace(normalizedPath)) return false;
            return _context.SourceMaterials.Any(s => (s.FilePath ?? string.Empty).Trim().ToLower() == normalizedPath);
        }

        private bool SourceMaterialExistsByUrl(string? url)
        {
            var normalizedUrl = NormalizeMaterialIdentity(url);
            if (string.IsNullOrWhiteSpace(normalizedUrl)) return false;
            return _context.SourceMaterials.Any(s => (s.Url ?? string.Empty).Trim().ToLower() == normalizedUrl);
        }

        private bool SourceMaterialExistsByFileIdentity(
            string? fileName,
            string? fileType,
            string? qualificationCode = null,
            string? qualificationDescription = null)
        {
            var normalizedFileName = NormalizeMaterialIdentity(fileName);
            var normalizedFileType = NormalizeMaterialIdentity(fileType);
            if (string.IsNullOrWhiteSpace(normalizedFileName) || string.IsNullOrWhiteSpace(normalizedFileType))
                return false;

            var normalizedQualificationCode = NormalizeMaterialIdentity(qualificationCode);
            var normalizedQualificationDescription = NormalizeMaterialIdentity(qualificationDescription);

            var query = _context.SourceMaterials.Where(s =>
                (s.FileName ?? string.Empty).Trim().ToLower() == normalizedFileName &&
                (s.FileType ?? string.Empty).Trim().ToLower() == normalizedFileType);

            if (!string.IsNullOrWhiteSpace(normalizedQualificationCode))
            {
                query = query.Where(s => (s.QualificationCode ?? string.Empty).Trim().ToLower() == normalizedQualificationCode);
            }
            else if (!string.IsNullOrWhiteSpace(normalizedQualificationDescription))
            {
                query = query.Where(s => (s.QualificationDescription ?? string.Empty).Trim().ToLower() == normalizedQualificationDescription);
            }

            return query.Any();
        }

        private bool SourceMaterialExistsForCurriculum(string? fileName, string? fileType, string? qualificationCode, string? qualificationDescription)
        {
            var normalizedFileName = NormalizeMaterialIdentity(fileName);
            var normalizedFileType = NormalizeMaterialIdentity(fileType);
            if (string.IsNullOrWhiteSpace(normalizedFileName) || string.IsNullOrWhiteSpace(normalizedFileType))
                return false;

            var normalizedQualificationCode = NormalizeMaterialIdentity(qualificationCode);
            var normalizedQualificationDescription = NormalizeMaterialIdentity(qualificationDescription);

            var query = _context.SourceMaterials.Where(s =>
                (s.TopicDescription ?? string.Empty) == CurriculumBenchmarkMarker &&
                (s.FileName ?? string.Empty).Trim().ToLower() == normalizedFileName &&
                (s.FileType ?? string.Empty).Trim().ToLower() == normalizedFileType);

            if (!string.IsNullOrWhiteSpace(normalizedQualificationCode))
            {
                query = query.Where(s => (s.QualificationCode ?? string.Empty).Trim().ToLower() == normalizedQualificationCode);
            }
            if (!string.IsNullOrWhiteSpace(normalizedQualificationDescription))
            {
                query = query.Where(s => (s.QualificationDescription ?? string.Empty).Trim().ToLower() == normalizedQualificationDescription);
            }

            return query.Any();
        }

        [HttpPost("upload-material")]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public async Task<IActionResult> UploadMaterial([FromForm] IFormFile file, [FromForm] UploadMaterialRequest meta)
        {
            if (file == null || file.Length == 0) return BadRequest("No file");
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!KnowledgeUploadExtensions.Contains(ext)) return BadRequest("Unsupported file type");
            var (_, qualificationCode, qualificationDescription) = ResolveKnowledgeUploadQualification(meta);
            if (string.IsNullOrWhiteSpace(qualificationCode) || string.IsNullOrWhiteSpace(qualificationDescription))
            {
                return BadRequest("Qualification selection is required for curriculum-aligned local knowledge upload.");
            }
            var safeName = Path.GetFileName(file.FileName);
            var fileType = ext.TrimStart('.');
            if (SourceMaterialExistsByFileIdentity(safeName, fileType, qualificationCode, qualificationDescription))
            {
                return Conflict($"File '{safeName}' already uploaded.");
            }

            var result = await UploadLocalKnowledgeViaHierarchyAsync(file, meta);
            var failure = BuildIndexedUploadFailureResult(result);
            if (failure != null)
            {
                return failure;
            }

            return Ok(BuildIndexedUploadResponse(result, meta?.Title ?? safeName, fileType));
        }

        [HttpPost("upload-developer-knowledge")]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public async Task<IActionResult> UploadDeveloperKnowledge([FromForm] IFormFile file, [FromForm] UploadMaterialRequest meta)
        {
            if (file == null || file.Length == 0) return BadRequest("No file");
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!KnowledgeUploadExtensions.Contains(ext)) return BadRequest("Unsupported file type");

            var qualificationDescription = ResolveQualificationDescription(meta);
            if (string.IsNullOrWhiteSpace(qualificationDescription))
                return BadRequest("Qualification selection is required for Developer Knowledge Base upload.");

            var qualificationCode = ResolveQualificationCode(qualificationDescription, meta?.QualificationId);
            if (string.IsNullOrWhiteSpace(qualificationCode))
                return BadRequest("Unable to resolve qualification code for Developer Knowledge Base upload.");

            var structure = _knowledgeHierarchyService.EnsureQualificationStructure(qualificationCode, qualificationDescription);
            var safeName = Path.GetFileName(file.FileName);
            var inboxPath = EnsureUniqueFilePath(Path.Combine(structure.DeveloperInboxPath, safeName));

            await using (var fs = new FileStream(inboxPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.CopyToAsync(fs);
            }

            var sync = _knowledgeHierarchyService.SyncKnowledgeHierarchy(new KnowledgeHierarchyService.SyncOptions
            {
                QualificationCode = qualificationCode,
                QualificationDescription = qualificationDescription,
                IncludeLocalSourceUploads = false,
                IncludeDeveloperKnowledgeBase = true,
                MaxFilesPerInbox = 2000,
                RebuildUploadReadme = false,
                ConsolidateLegacyFolders = false
            });

            var indexedFileName = Path.GetFileName(inboxPath);
            var detail = sync.Details
                .Where(d =>
                    string.Equals(d.QualificationCode, qualificationCode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(d.SourceType, "developer_knowledge_base", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(d.FileName, indexedFileName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => string.Equals(d.Status, "created", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(d => d.KnowledgeNumber ?? 0)
                .FirstOrDefault();

            var material = FindIndexedSourceMaterial(qualificationCode, "developer_knowledge_base", indexedFileName, detail);

            if (detail != null && string.Equals(detail.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    qualificationCode,
                    qualificationDescription,
                    sourceType = "developer_knowledge_base",
                    status = detail.Status,
                    reason = detail.Reason,
                    knowledgeNumber = detail.KnowledgeNumber,
                    archivePath = detail.ArchivedPath,
                    developerInboxPath = structure.DeveloperInboxPath,
                    developerArchivePath = structure.DeveloperArchivePath
                });
            }

            return Ok(new
            {
                id = material?.Id,
                title = material?.Title ?? (meta?.Title ?? indexedFileName),
                type = ext.TrimStart('.'),
                qualificationCode,
                qualificationDescription,
                sourceType = "developer_knowledge_base",
                status = detail?.Status ?? "created",
                reason = detail?.Reason,
                knowledgeNumber = detail?.KnowledgeNumber ?? material?.KnowledgeNumber,
                archivePath = detail?.ArchivedPath ?? material?.FilePath,
                developerInboxPath = structure.DeveloperInboxPath,
                developerArchivePath = structure.DeveloperArchivePath,
                qualificationRootPath = structure.QualificationRootPath
            });
        }

        [HttpPost("upload-material-to-blob")]
        [HttpPost("upload-material-local")]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public async Task<IActionResult> UploadMaterialToBlob([FromForm] IFormFile file, [FromForm] UploadMaterialRequest meta)
        {
            if (file == null || file.Length == 0) return BadRequest("No file");
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!KnowledgeUploadExtensions.Contains(ext)) return BadRequest("Unsupported file type");
            var (_, qualificationCode, qualificationDescription) = ResolveKnowledgeUploadQualification(meta);
            if (string.IsNullOrWhiteSpace(qualificationCode) || string.IsNullOrWhiteSpace(qualificationDescription))
            {
                return BadRequest("Qualification selection is required for curriculum-aligned local knowledge upload.");
            }
            var safeName = Path.GetFileName(file.FileName);
            var fileType = ext.TrimStart('.');
            if (SourceMaterialExistsByFileIdentity(safeName, fileType, qualificationCode, qualificationDescription))
            {
                return Conflict($"File '{safeName}' already uploaded.");
            }

            var result = await UploadLocalKnowledgeViaHierarchyAsync(file, meta);
            var failure = BuildIndexedUploadFailureResult(result);
            if (failure != null)
            {
                return failure;
            }

            return Ok(BuildIndexedUploadResponse(result, meta?.Title ?? safeName, fileType));
        }

        [HttpPost("upload-curriculum-to-blob")]
        [HttpPost("upload-curriculum-local")]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public async Task<IActionResult> UploadCurriculumToBlob([FromForm] IFormFile file, [FromForm] UploadMaterialRequest meta)
        {
            if (file == null || file.Length == 0) return BadRequest("No file");
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!KnowledgeUploadExtensions.Contains(ext)) return BadRequest("Unsupported file type");

            var qualificationDescription = ResolveQualificationDescription(meta);
            if (string.IsNullOrWhiteSpace(qualificationDescription))
                return BadRequest("Qualification selection is required for curriculum benchmark upload.");
            var qualificationCode = ResolveQualificationCode(qualificationDescription, meta?.QualificationId);
            var structure = _knowledgeHierarchyService.EnsureQualificationStructure(qualificationCode, qualificationDescription);

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "ETDP", "Sources");
            Directory.CreateDirectory(dir);
            var safeName = Path.GetFileName(file.FileName);
            var fileType = ext.TrimStart('.');
            var duplicateTitle = $"[CURRICULUM] {qualificationDescription} :: {safeName}";
            if (SourceMaterialExistsForCurriculum(safeName, fileType, qualificationCode, qualificationDescription))
            {
                return Conflict($"Curriculum benchmark '{safeName}' already uploaded for '{qualificationDescription}'.");
            }

            var path = Path.Combine(dir, $"{Guid.NewGuid()}_{safeName}");
            using (var fs = new FileStream(path, FileMode.CreateNew))
            {
                await file.CopyToAsync(fs);
            }

            string text = "";
            if (KnowledgeImageExtensions.Contains(ext))
            {
                text = ExtractTextFromImageFile(path);
            }
            else
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                text = await ExtractTextFromFileStreamAsync(fs, ext);
            }
            text = await ApplyOcrEnhancementAsync(path, ext, text, meta?.RunCognitiveClean ?? true);

            var material = new SourceMaterial
            {
                Title = duplicateTitle,
                FileName = safeName,
                FilePath = path,
                FileType = fileType,
                Url = string.Empty,
                ExtractedText = text,
                QualificationCode = qualificationCode,
                QualificationDescription = qualificationDescription,
                SubjectDescription = meta?.SubjectDescription,
                TopicDescription = CurriculumBenchmarkMarker,
                AssessmentCriteriaDescription = CurriculumBenchmarkAssessmentMarker
            };
            ApplyKnowledgeMetadata(material, "local_source_upload", qualificationCode);

            _context.SourceMaterials.Add(material);
            _context.SaveChanges();
            var localMaterialUrl = BuildLocalMaterialExportUrl(material.Id);
            material.Url = localMaterialUrl;
            _context.SaveChanges();

            var curriculumBenchmarkPath = EnsureUniqueFilePath(Path.Combine(structure.CurriculumLibraryPath, safeName));
            System.IO.File.Copy(path, curriculumBenchmarkPath, overwrite: false);

            return Ok(new UploadMaterialToBlobResponse
            {
                Id = material.Id,
                Title = material.Title,
                Type = material.FileType,
                BlobUrl = localMaterialUrl,
                QualificationCode = qualificationCode,
                QualificationDescription = qualificationDescription,
                SourceType = "local_source_upload",
                Status = "created",
                KnowledgeNumber = material.KnowledgeNumber,
                ArchivePath = material.FilePath,
                QualificationRootPath = structure.QualificationRootPath,
                CurriculumLibraryPath = structure.CurriculumLibraryPath,
                LocalInboxPath = structure.LocalInboxPath,
                LocalArchivePath = structure.LocalArchivePath
            });
        }

        [HttpGet("curriculum-benchmark-summary")]
        public IActionResult CurriculumBenchmarkSummary([FromQuery] int? qualificationId = null, [FromQuery] string? qualificationDescription = null)
        {
            string qdesc = qualificationDescription ?? string.Empty;
            if (qualificationId.HasValue && qualificationId.Value > 0)
            {
                var q = _context.Qualifications.Find(qualificationId.Value);
                if (q != null && !string.IsNullOrWhiteSpace(q.QualificationDescription))
                    qdesc = q.QualificationDescription!;
            }
            qdesc = (qdesc ?? string.Empty).Trim();

            var benchmarksQuery = _context.SourceMaterials.Where(s => s.TopicDescription == CurriculumBenchmarkMarker);
            if (!string.IsNullOrWhiteSpace(qdesc))
            {
                benchmarksQuery = benchmarksQuery.Where(s => s.QualificationDescription == qdesc);
            }
            var benchmarks = benchmarksQuery
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => new
                {
                    s.Id,
                    s.Title,
                    s.FileName,
                    s.Url,
                    s.QualificationDescription,
                    s.CreatedAt
                })
                .ToList();

            var qualification = !string.IsNullOrWhiteSpace(qdesc)
                ? _context.Qualifications.FirstOrDefault(q => q.QualificationDescription == qdesc)
                : null;
            var qid = qualification?.Id ?? 0;

            int subjectsCount = 0, topicsCount = 0, criteriaCount = 0, lessonPlansCount = 0;
            if (qid > 0)
            {
                subjectsCount = _context.Subjects.Count(s => s.QualificationId == qid);
                var subjectIds = _context.Subjects.Where(s => s.QualificationId == qid).Select(s => s.Id).ToList();
                topicsCount = _context.Topics.Count(t => subjectIds.Contains(t.SubjectId));
                var topicIds = _context.Topics.Where(t => subjectIds.Contains(t.SubjectId)).Select(t => t.Id).ToList();
                criteriaCount = _context.AssessmentCriteria.Count(c => topicIds.Contains(c.TopicId));
                var criteriaIds = _context.AssessmentCriteria.Where(c => topicIds.Contains(c.TopicId)).Select(c => c.Id).ToList();
                lessonPlansCount = _context.LessonPlans.Count(lp => criteriaIds.Contains(lp.AssessmentCriteriaId));
            }

            return Ok(new
            {
                qualificationDescription = qdesc,
                hasBenchmark = benchmarks.Count > 0,
                benchmarks,
                mapped = new
                {
                    subjects = subjectsCount,
                    topics = topicsCount,
                    assessmentCriteria = criteriaCount,
                    lessonPlans = lessonPlansCount
                }
            });
        }

        [HttpPost("upload-materials-bulk")]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public async Task<IActionResult> UploadMaterialsBulk([FromForm] List<IFormFile> files, [FromForm] UploadMaterialRequest meta)
        {
            if (files == null || files.Count == 0) return BadRequest("No files");
            var (_, qualificationCode, qualificationDescription) = ResolveKnowledgeUploadQualification(meta);
            if (string.IsNullOrWhiteSpace(qualificationCode) || string.IsNullOrWhiteSpace(qualificationDescription))
            {
                return BadRequest("Qualification selection is required for curriculum-aligned local knowledge upload.");
            }

            var structure = _knowledgeHierarchyService.EnsureQualificationStructure(qualificationCode, qualificationDescription);

            int created = 0, failed = 0, skipped = 0;
            var items = new List<object>();
            var stagedUploads = new List<StagedKnowledgeUpload>();
            var existingFileKeys = _context.SourceMaterials
                .Select(s => new { s.FileName, s.FileType })
                .ToList()
                .Select(s => BuildFileIdentityKey(s.FileName, s.FileType))
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var file in files)
            {
                try
                {
                    if (file == null || file.Length == 0) { failed++; continue; }
                    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                    if (!KnowledgeUploadExtensions.Contains(ext)) { skipped++; continue; }
                    var safeName = Path.GetFileName(file.FileName);
                    var fileType = ext.TrimStart('.');
                    var fileKey = BuildFileIdentityKey(safeName, fileType);
                    if (string.IsNullOrWhiteSpace(fileKey) || existingFileKeys.Contains(fileKey)) { skipped++; continue; }
                    var inboxPath = EnsureUniqueFilePath(Path.Combine(structure.LocalInboxPath, safeName));
                    using (var fs = new FileStream(inboxPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        await file.CopyToAsync(fs);
                    }
                    stagedUploads.Add(new StagedKnowledgeUpload
                    {
                        OriginalFileName = safeName,
                        StagedFileName = Path.GetFileName(inboxPath),
                        FileType = fileType
                    });
                    existingFileKeys.Add(fileKey);
                }
                catch
                {
                    failed++;
                }
            }

            KnowledgeHierarchyService.SyncResult? sync = null;
            if (stagedUploads.Count > 0)
            {
                sync = _knowledgeHierarchyService.SyncKnowledgeHierarchy(new KnowledgeHierarchyService.SyncOptions
                {
                    QualificationCode = qualificationCode,
                    QualificationDescription = qualificationDescription,
                    IncludeLocalSourceUploads = true,
                    IncludeDeveloperKnowledgeBase = false,
                    MaxFilesPerInbox = 2000,
                    RebuildUploadReadme = false,
                    ConsolidateLegacyFolders = false
                });
            }

            foreach (var staged in stagedUploads)
            {
                var detail = FindIndexedUploadDetail(sync, qualificationCode, "local_source_upload", staged.StagedFileName);
                var material = FindIndexedSourceMaterial(qualificationCode, "local_source_upload", staged.StagedFileName, detail);
                if (material != null)
                {
                    ApplyIndexedUploadMetadata(material, meta, qualificationCode, qualificationDescription, "local_source_upload");
                }

                var status = detail?.Status ?? (material != null ? "created" : "failed");
                var reason = detail?.Reason;
                if (!string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) && material == null)
                {
                    status = "failed";
                    reason = string.IsNullOrWhiteSpace(reason) ? "indexed_material_not_found" : reason;
                }

                if (string.Equals(status, "created", StringComparison.OrdinalIgnoreCase))
                {
                    created++;
                }
                else if (string.Equals(status, "skipped", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                }
                else
                {
                    failed++;
                }

                items.Add(new
                {
                    file = staged.OriginalFileName,
                    id = material?.Id,
                    type = material?.FileType ?? staged.FileType,
                    status,
                    reason,
                    knowledgeNumber = detail?.KnowledgeNumber ?? material?.KnowledgeNumber,
                    archivePath = detail?.ArchivedPath ?? material?.FilePath
                });
            }

            if (stagedUploads.Count > 0)
            {
                _context.SaveChanges();
            }

            return Ok(new
            {
                created,
                failed,
                skipped,
                items,
                qualificationCode,
                qualificationDescription,
                sourceType = "local_source_upload",
                localInboxPath = structure.LocalInboxPath,
                localArchivePath = structure.LocalArchivePath,
                qualificationRootPath = structure.QualificationRootPath,
                curriculumLibraryPath = structure.CurriculumLibraryPath
            });
        }

        private static string CleanExtractedText(string text)
        {
            return DocumentTextCleaner.Clean(text, preservePdfPageMarkers: true);
        }

        private static async Task<string?> UploadFileToBlobViaSasAsync(
            string containerSasUrl,
            string localFilePath,
            string originalFileName,
            string? qualificationDescription)
        {
            if (string.IsNullOrWhiteSpace(containerSasUrl)) return null;
            if (!System.IO.File.Exists(localFilePath)) return null;

            var uri = new Uri(containerSasUrl);
            var baseUrl = $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}".TrimEnd('/');
            var sas = uri.Query.TrimStart('?');
            if (string.IsNullOrWhiteSpace(sas)) return null;

            var folder = Regex.Replace(qualificationDescription ?? "General", @"[^\w\- ]+", "").Trim().Replace(" ", "_");
            if (string.IsNullOrWhiteSpace(folder)) folder = "General";

            var blobName = $"{folder}/{Guid.NewGuid()}_{Path.GetFileName(originalFileName)}";
            var escapedBlobName = string.Join("/", blobName.Split('/').Select(Uri.EscapeDataString));
            var uploadUrl = $"{baseUrl}/{escapedBlobName}?{sas}";

            await using var fs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var content = new StreamContent(fs);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var req = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
            req.Headers.Add("x-ms-blob-type", "BlockBlob");
            req.Headers.Add("x-ms-version", "2023-11-03");
            req.Content = content;

            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Blob upload failed: {(int)resp.StatusCode} {err}");
            }

            return $"{baseUrl}/{escapedBlobName}";
        }

        private static string BuildLocalMaterialExportUrl(int materialId)
        {
            if (materialId <= 0) return string.Empty;
            return $"/api/Content/materials/{materialId}/export/txt";
        }

        private string ResolveQualificationDescription(UploadMaterialRequest? meta)
        {
            if (meta == null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(meta.QualificationDescription))
                return meta.QualificationDescription.Trim();
            if (meta.QualificationId.HasValue && meta.QualificationId.Value > 0)
            {
                var q = _context.Qualifications.Find(meta.QualificationId.Value);
                if (q != null && !string.IsNullOrWhiteSpace(q.QualificationDescription))
                    return q.QualificationDescription.Trim();
            }
            return string.Empty;
        }

        private string ResolveQualificationCode(string? qualificationDescription, int? qualificationId = null)
        {
            if (qualificationId.HasValue && qualificationId.Value > 0)
            {
                var byId = _context.Qualifications.Find(qualificationId.Value);
                if (byId != null && !string.IsNullOrWhiteSpace(byId.QualificationNumber))
                    return byId.QualificationNumber.Trim();
            }

            if (!string.IsNullOrWhiteSpace(qualificationDescription))
            {
                var desc = qualificationDescription.Trim();
                var exact = _context.Qualifications.FirstOrDefault(q => q.QualificationDescription == desc);
                if (exact != null && !string.IsNullOrWhiteSpace(exact.QualificationNumber))
                    return exact.QualificationNumber.Trim();

                var contains = _context.Qualifications.FirstOrDefault(q => q.QualificationDescription.Contains(desc));
                if (contains != null && !string.IsNullOrWhiteSpace(contains.QualificationNumber))
                    return contains.QualificationNumber.Trim();
            }

            return string.Empty;
        }

        private static int? ParseKnowledgeNumber(string? value)
        {
            var source = value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(source)) return null;

            var fromKnowledgeLabel = Regex.Match(source, @"(?:knowledge|kb)\D{0,8}(\d{1,5})", RegexOptions.IgnoreCase);
            if (fromKnowledgeLabel.Success && int.TryParse(fromKnowledgeLabel.Groups[1].Value, out var n1))
                return n1;

            var fromAnyToken = Regex.Match(Path.GetFileNameWithoutExtension(source), @"(?:^|[^0-9])(\d{1,5})(?:[^0-9]|$)");
            if (fromAnyToken.Success && int.TryParse(fromAnyToken.Groups[1].Value, out var n2))
                return n2;

            return null;
        }

        private static string BuildKnowledgeLabel(string? title, string? fileName, int? knowledgeNumber)
        {
            var preferred = string.IsNullOrWhiteSpace(title) ? fileName : title;
            var baseLabel = string.IsNullOrWhiteSpace(preferred) ? "Knowledge" : preferred!.Trim();
            if (knowledgeNumber.HasValue && knowledgeNumber.Value > 0)
            {
                return $"Knowledge Base {knowledgeNumber.Value}: {baseLabel}";
            }
            return baseLabel;
        }

        private string BuildKnowledgeRootPath(string? qualificationCode, string? qualificationDescription, string? knowledgeSourceType)
        {
            var qCode = string.IsNullOrWhiteSpace(qualificationCode) ? "UNASSIGNED" : qualificationCode.Trim();
            var qDesc = string.IsNullOrWhiteSpace(qualificationDescription) ? qCode : qualificationDescription.Trim();
            var structure = _knowledgeHierarchyService.EnsureQualificationStructure(qCode, qDesc);
            return ResolveKnowledgeSourceRootPath(structure, NormalizeKnowledgeSourceType(knowledgeSourceType));
        }

        private void ApplyKnowledgeMetadata(
            SourceMaterial material,
            string? knowledgeSourceType,
            string? qualificationCode,
            int? knowledgeNumber = null,
            DateTime? uploadedAtUtc = null,
            string? knowledgeRootPath = null,
            string? knowledgeLabel = null)
        {
            if (material == null) return;

            var sourceType = NormalizeKnowledgeSourceType(knowledgeSourceType);
            if (string.IsNullOrWhiteSpace(sourceType))
                sourceType = "local_source_upload";

            material.KnowledgeSourceType = sourceType;
            material.QualificationCode = string.IsNullOrWhiteSpace(qualificationCode) ? material.QualificationCode : qualificationCode.Trim();
            material.KnowledgeNumber = knowledgeNumber ?? ParseKnowledgeNumber(material.FileName) ?? ParseKnowledgeNumber(material.Title);
            material.KnowledgeUploadedAtUtc = uploadedAtUtc ?? DateTime.UtcNow;
            material.KnowledgeRootPath = string.IsNullOrWhiteSpace(knowledgeRootPath)
                ? BuildKnowledgeRootPath(material.QualificationCode, material.QualificationDescription, sourceType)
                : knowledgeRootPath.Trim();
            material.KnowledgeLabel = string.IsNullOrWhiteSpace(knowledgeLabel)
                ? BuildKnowledgeLabel(material.Title, material.FileName, material.KnowledgeNumber)
                : knowledgeLabel.Trim();
        }

        private sealed class DerivedPdfVisualImportResult
        {
            public List<SourceMaterial> Materials { get; } = new();
            public string SummaryText { get; set; } = string.Empty;
        }

        private DerivedPdfVisualImportResult ExtractDerivedPdfVisualMaterials(
            string archivedPdfPath,
            string originalName,
            string sourceType,
            string qualificationCode,
            string qualificationDescription,
            string sourceRootPath,
            string parentKnowledgeUrl,
            ref int nextKnowledgeNumber)
        {
            var result = new DerivedPdfVisualImportResult();
            try
            {
                var outputFolderName = MakeSafeFilePart(Path.GetFileNameWithoutExtension(archivedPdfPath), "pdf_visuals");
                var outputDirectory = Path.Combine(sourceRootPath, "visual_archive", outputFolderName);
                var extracted = _pdfVisualExtractionService.ExtractAndPersist(archivedPdfPath, new PdfVisualExtractionService.PersistOptions
                {
                    OutputDirectory = outputDirectory,
                    OutputNamePrefix = "visual",
                    SourceDocumentName = originalName
                });
                result.SummaryText = extracted.SummaryText;

                foreach (var visual in extracted.Visuals)
                {
                    var visualFileName = Path.GetFileName(visual.FilePath);
                    var imageText = ExtractTextFromImageFile(visual.FilePath, FindImageSidecarPaths(visual.FilePath));
                    imageText = _ocrExtractionService.EnhanceExtractedText(visual.FilePath, Path.GetExtension(visual.FilePath), imageText);

                    var material = new SourceMaterial
                    {
                        Title = BuildDerivedPdfVisualTitle(originalName, visual.Caption, visual.PageNumber, nextKnowledgeNumber),
                        FileName = visualFileName,
                        FilePath = visual.FilePath,
                        FileType = visual.FileType,
                        Url = $"knowledge://{Uri.EscapeDataString(qualificationCode)}/{Uri.EscapeDataString(sourceType)}/kb-{nextKnowledgeNumber:D4}/{Uri.EscapeDataString(visualFileName)}",
                        QualificationCode = qualificationCode,
                        QualificationDescription = qualificationDescription,
                        SubjectDescription = $"KnowledgeBase:{sourceType}",
                        TopicDescription = $"KnowledgeNumber:{nextKnowledgeNumber:D4};DerivedVisualPage:{visual.PageNumber}",
                        AssessmentCriteriaDescription = BuildDerivedPdfVisualAssessmentNote(
                            originalName,
                            archivedPdfPath,
                            parentKnowledgeUrl,
                            visual.PageNumber,
                            visual.PlaceholderTag,
                            visual.Caption),
                        ExtractedText = imageText
                    };

                    ApplyKnowledgeMetadata(
                        material,
                        sourceType,
                        qualificationCode,
                        knowledgeNumber: nextKnowledgeNumber,
                        uploadedAtUtc: DateTime.UtcNow,
                        knowledgeRootPath: sourceRootPath,
                        knowledgeLabel: BuildDerivedPdfVisualLabel(originalName, visual.PageNumber, nextKnowledgeNumber));

                    result.Materials.Add(material);
                    nextKnowledgeNumber++;
                }
            }
            catch
            {
                // PDF visual extraction is best-effort and must not block the parent document import.
            }

            return result;
        }

        private static string AppendPdfVisualSummary(string extractedText, string summaryText)
        {
            if (string.IsNullOrWhiteSpace(summaryText))
            {
                return extractedText;
            }

            if (string.IsNullOrWhiteSpace(extractedText))
            {
                return CleanExtractedText(summaryText);
            }

            return CleanExtractedText($"{extractedText}\n\n[PDF_VISUAL_REFERENCES]\n{summaryText}");
        }

        private static string BuildDerivedPdfVisualTitle(string originalName, string caption, int pageNumber, int knowledgeNumber)
        {
            var preferredCaption = string.IsNullOrWhiteSpace(caption)
                ? $"Visual from {Path.GetFileNameWithoutExtension(originalName)}"
                : caption.Trim();
            return LimitMetadataValue($"{preferredCaption} (page {pageNumber})", 240);
        }

        private static string BuildDerivedPdfVisualLabel(string originalName, int pageNumber, int knowledgeNumber)
        {
            return $"Knowledge Base {knowledgeNumber}: Visual from {originalName} page {pageNumber}";
        }

        private static string BuildDerivedPdfVisualAssessmentNote(
            string originalName,
            string archivedPdfPath,
            string parentKnowledgeUrl,
            int pageNumber,
            string placeholderTag,
            string caption)
        {
            return $"DerivedFromSource:{originalName};DerivedFromPath:{archivedPdfPath};DerivedFromUrl:{parentKnowledgeUrl};Page:{pageNumber};Placeholder:{placeholderTag};Caption:{LimitMetadataValue(caption, 180)}";
        }

        private static string LimitMetadataValue(string? value, int maxLen)
        {
            var cleaned = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
            if (cleaned.Length <= maxLen) return cleaned;
            return cleaned.Substring(0, maxLen).Trim();
        }

        private static async Task<(bool deleted, string message)> DeleteBlobViaSasAsync(string blobUrl)
        {
            if (string.IsNullOrWhiteSpace(blobUrl))
                return (true, "No remote blob URL to delete.");

            if (!Uri.TryCreate(blobUrl, UriKind.Absolute, out var blobUri))
            {
                return blobUrl.StartsWith("/", StringComparison.OrdinalIgnoreCase)
                    ? (true, "Local material URL; no remote blob delete required.")
                    : (true, "Non-absolute material URL; no remote blob delete required.");
            }

            var isHttp = blobUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                         blobUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            if (!isHttp)
                return (true, "Non-HTTP material URL; no remote blob delete required.");

            if (!blobUri.Host.Contains("blob.core.windows.net", StringComparison.OrdinalIgnoreCase))
                return (true, "Material URL is not Azure Blob Storage; no remote blob delete required.");

            var containerSasUrl = Environment.GetEnvironmentVariable("AZURE_BLOB_CONTAINER_SAS_URL");
            if (string.IsNullOrWhiteSpace(containerSasUrl))
                return (true, "AZURE_BLOB_CONTAINER_SAS_URL not configured; remote blob delete skipped.");

            if (!Uri.TryCreate(containerSasUrl, UriKind.Absolute, out var containerUri))
                return (false, "Invalid container SAS URL");

            var containerBasePath = containerUri.AbsolutePath.TrimEnd('/');
            var blobPath = blobUri.AbsolutePath;
            if (!blobPath.StartsWith(containerBasePath, StringComparison.OrdinalIgnoreCase))
                return (false, "Blob URL is outside configured container");

            var sas = containerUri.Query.TrimStart('?');
            if (string.IsNullOrWhiteSpace(sas))
                return (false, "Container SAS token missing");

            var deleteUrl = $"{blobUri.Scheme}://{blobUri.Host}{blobUri.AbsolutePath}?{sas}";
            using var req = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
            req.Headers.Add("x-ms-version", "2023-11-03");

            using var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode) return (true, "deleted");

            var body = await resp.Content.ReadAsStringAsync();
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return (true, "blob not found in storage; treated as deleted");

            return (false, $"Blob delete failed: {(int)resp.StatusCode} {body}");
        }

        [HttpPost("import-folder")]
        public async Task<IActionResult> ImportFolder([FromBody] ImportFolderRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.QualificationNumber))
                return BadRequest("Provide QualificationNumber");
            var normalizedQualificationNumber = req.QualificationNumber.Trim();
            var importGate = QualificationFolderImportLocks.GetOrAdd(
                normalizedQualificationNumber,
                _ => new System.Threading.SemaphoreSlim(1, 1));

            await importGate.WaitAsync();
            try
            {
                var baseDir = GetConfiguredImportBasePath();
                var safeFolder = Regex.Replace(req.QualificationNumber, @"[^\w\- ]+", "").Trim().Replace(" ", "_");
                var dir = Path.Combine(baseDir, safeFolder);
                if (!Directory.Exists(dir)) return NotFound($"Folder not found: {dir}");

                var qual = _context.Qualifications.FirstOrDefault(q => q.QualificationNumber == req.QualificationNumber)
                           ?? _context.Qualifications.FirstOrDefault(q => (q.QualificationDescription ?? "").Contains(req.QualificationNumber));
                var qualificationCode = !string.IsNullOrWhiteSpace(qual?.QualificationNumber)
                    ? qual!.QualificationNumber.Trim()
                    : req.QualificationNumber.Trim();
                var qualificationDescription = qual?.QualificationDescription;

                int created = 0, skipped = 0;
                var scopedMaterials = _context.SourceMaterials
                    .Where(s =>
                        (s.QualificationCode ?? string.Empty) == qualificationCode ||
                        (!string.IsNullOrWhiteSpace(qualificationDescription) &&
                         (s.QualificationDescription ?? string.Empty) == qualificationDescription))
                    .Select(s => new { s.FileName, s.FileType, s.FilePath })
                    .ToList();

                var existingFileKeys = scopedMaterials
                    .Select(s => BuildFileIdentityKey(s.FileName, s.FileType))
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .ToHashSet(StringComparer.Ordinal);
                var existingPathKeys = scopedMaterials
                    .Select(s => NormalizeMaterialIdentity(s.FilePath))
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var path in Directory.GetFiles(dir))
                {
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    if (!KnowledgeUploadExtensions.Contains(ext) || IsImageSidecarFile(path)) { skipped++; continue; }

                    var safeName = Path.GetFileName(path);
                    var fileType = ext.TrimStart('.');
                    var normalizedPath = NormalizeMaterialIdentity(path);
                    var fileKey = BuildFileIdentityKey(safeName, fileType);
                    if (existingPathKeys.Contains(normalizedPath) ||
                        (!string.IsNullOrWhiteSpace(fileKey) && existingFileKeys.Contains(fileKey)) ||
                        SourceMaterialExistsByPath(path) ||
                        SourceMaterialExistsByFileIdentity(safeName, fileType, qualificationCode, qualificationDescription))
                    {
                        skipped++;
                        continue;
                    }

                    string text = "";
                    try
                    {
                        if (KnowledgeImageExtensions.Contains(ext))
                        {
                            text = ExtractTextFromImageFile(path, FindImageSidecarPaths(path));
                        }
                        else
                        {
                            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                            text = await ExtractTextFromFileStreamAsync(fs, ext);
                        }
                        text = await ApplyOcrEnhancementAsync(path, ext, text);
                    }
                    catch { }

                    var material = new SourceMaterial
                    {
                        Title = safeName,
                        FileName = safeName,
                        FilePath = path,
                        FileType = fileType,
                        ExtractedText = text ?? "",
                        QualificationCode = qualificationCode,
                        QualificationDescription = qualificationDescription
                    };
                    ApplyKnowledgeMetadata(material, "local_source_upload", qualificationCode);
                    _context.SourceMaterials.Add(material);
                    if (!string.IsNullOrWhiteSpace(fileKey)) existingFileKeys.Add(fileKey);
                    if (!string.IsNullOrWhiteSpace(normalizedPath)) existingPathKeys.Add(normalizedPath);
                    created++;
                }

                _context.SaveChanges();
                return Ok(new { created, skipped });
            }
            finally
            {
                importGate.Release();
            }
        }

        [HttpPost("import-github-repo")]
        public async Task<IActionResult> ImportGitHubRepo([FromBody] GitHubRepoImportRequest req)
        {
            if (!AiRuntime.AllowCloudProviders())
                return StatusCode(StatusCodes.Status403Forbidden, "GitHub import is disabled in offline mode.");
            if (req == null || string.IsNullOrWhiteSpace(req.RepoUrl))
                return BadRequest("RepoUrl is required.");

            if (!TryParseGitHubRepository(req.RepoUrl, out var owner, out var repo, out var branchFromUrl))
                return BadRequest("Unsupported GitHub repository URL. Use https://github.com/<owner>/<repo>.");

            var requestedBranch = (req.Branch ?? "").Trim();
            var branch = !string.IsNullOrWhiteSpace(requestedBranch) ? requestedBranch : branchFromUrl;
            var maxFiles = req.MaxFiles <= 0 ? 200 : Math.Min(req.MaxFiles, 1000);
            var maxFileSizeKb = req.MaxFileSizeKb <= 0 ? 20480 : Math.Min(req.MaxFileSizeKb, 102400);
            var maxFileSizeBytes = (long)maxFileSizeKb * 1024L;

            using var repoReq = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}");
            repoReq.Headers.UserAgent.Add(new ProductInfoHeaderValue("ETDP", "1.0"));
            repoReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            var repoResp = await _http.SendAsync(repoReq);
            var repoBody = await repoResp.Content.ReadAsStringAsync();
            if (!repoResp.IsSuccessStatusCode)
                return StatusCode((int)repoResp.StatusCode, $"GitHub repo lookup failed: {repoBody}");

            string defaultBranch = "main";
            try
            {
                using var repoDoc = JsonDocument.Parse(repoBody);
                if (repoDoc.RootElement.TryGetProperty("default_branch", out var db) && db.ValueKind == JsonValueKind.String)
                {
                    defaultBranch = db.GetString() ?? "main";
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(branch))
                branch = defaultBranch;

            string treeBody;
            HttpResponseMessage treeResp;
            using (var treeReq = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/git/trees/{Uri.EscapeDataString(branch)}?recursive=1"))
            {
                treeReq.Headers.UserAgent.Add(new ProductInfoHeaderValue("ETDP", "1.0"));
                treeReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                treeResp = await _http.SendAsync(treeReq);
            }

            if (!treeResp.IsSuccessStatusCode && !string.Equals(branch, defaultBranch, StringComparison.OrdinalIgnoreCase))
            {
                treeResp.Dispose();
                branch = defaultBranch;
                using var fallbackTreeReq = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/git/trees/{Uri.EscapeDataString(branch)}?recursive=1");
                fallbackTreeReq.Headers.UserAgent.Add(new ProductInfoHeaderValue("ETDP", "1.0"));
                fallbackTreeReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                treeResp = await _http.SendAsync(fallbackTreeReq);
            }

            using var treeRespScope = treeResp;
            treeBody = await treeRespScope.Content.ReadAsStringAsync();
            if (!treeRespScope.IsSuccessStatusCode)
                return StatusCode((int)treeRespScope.StatusCode, $"GitHub tree lookup failed: {treeBody}");

            var qualificationDescription = ResolveQualificationDescription(new UploadMaterialRequest
            {
                QualificationId = req.QualificationId,
                QualificationDescription = req.QualificationDescription
            });
            var qualificationCode = ResolveQualificationCode(qualificationDescription, req.QualificationId);

            var candidates = new List<(string Path, string Ext, long Size)>();
            try
            {
                using var treeDoc = JsonDocument.Parse(treeBody);
                var root = treeDoc.RootElement;
                if (!root.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
                    return BadRequest("GitHub tree response did not include files.");

                foreach (var node in tree.EnumerateArray())
                {
                    var type = node.TryGetProperty("type", out var typeNode) ? (typeNode.GetString() ?? "") : "";
                    if (!string.Equals(type, "blob", StringComparison.OrdinalIgnoreCase)) continue;

                    var path = node.TryGetProperty("path", out var pathNode) ? (pathNode.GetString() ?? "") : "";
                    path = (path ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    if (!IsSupportedGitHubImportExtension(ext, req.IncludeCodeFiles)) continue;

                    long size = 0;
                    if (node.TryGetProperty("size", out var sizeNode))
                    {
                        sizeNode.TryGetInt64(out size);
                    }
                    if (size > maxFileSizeBytes) continue;

                    candidates.Add((path, ext, size));
                }
            }
            catch (Exception ex)
            {
                return BadRequest($"Failed to parse GitHub tree: {ex.Message}");
            }

            var selected = candidates
                .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .Take(maxFiles)
                .ToList();

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var srcDir = Path.Combine(appData, "ETDP", "Sources");
            Directory.CreateDirectory(srcDir);
            var existingUrlSet = _context.SourceMaterials
                .Where(s => !string.IsNullOrWhiteSpace(s.Url))
                .Select(s => s.Url!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            int created = 0, skipped = 0, failed = 0;
            var details = new List<object>();
            foreach (var candidate in selected)
            {
                var rawUrl = BuildGitHubRawFileUrl(owner, repo, branch!, candidate.Path);
                if (existingUrlSet.Contains(rawUrl))
                {
                    skipped++;
                    details.Add(new { path = candidate.Path, status = "skipped", reason = "already_imported" });
                    continue;
                }

                var safeName = BuildGitHubMaterialFileName(owner, repo, candidate.Path);
                var localPath = Path.Combine(srcDir, $"{Guid.NewGuid()}_{safeName}");
                try
                {
                    using var fileReq = new HttpRequestMessage(HttpMethod.Get, rawUrl);
                    fileReq.Headers.UserAgent.Add(new ProductInfoHeaderValue("ETDP", "1.0"));
                    using var fileResp = await _http.SendAsync(fileReq, HttpCompletionOption.ResponseHeadersRead);
                    if (!fileResp.IsSuccessStatusCode)
                    {
                        failed++;
                        details.Add(new { path = candidate.Path, status = "failed", reason = $"download_http_{(int)fileResp.StatusCode}" });
                        continue;
                    }

                    await using (var outFs = new FileStream(localPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        await fileResp.Content.CopyToAsync(outFs);
                    }

                    string text;
                    await using (var readFs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (IsGitHubTextExtension(candidate.Ext))
                        {
                            using var reader = new StreamReader(readFs, System.Text.Encoding.UTF8, true, 1024, true);
                            text = CleanExtractedText(await reader.ReadToEndAsync());
                        }
                        else
                        {
                            text = await ExtractTextFromFileStreamAsync(readFs, candidate.Ext);
                        }
                    }
                    text = await ApplyOcrEnhancementAsync(localPath, candidate.Ext, text);

                    var material = new SourceMaterial
                    {
                        Title = $"[GITHUB] {owner}/{repo} :: {candidate.Path}",
                        FileName = safeName,
                        FilePath = localPath,
                        FileType = candidate.Ext.TrimStart('.'),
                        Url = rawUrl,
                        ExtractedText = text ?? "",
                        QualificationCode = qualificationCode,
                        QualificationDescription = qualificationDescription,
                        SubjectDescription = $"GitHub:{owner}/{repo}",
                        TopicDescription = $"GitHubBranch:{branch}",
                        AssessmentCriteriaDescription = $"GitHubPath:{candidate.Path}"
                    };
                    ApplyKnowledgeMetadata(material, "developer_knowledge_base", qualificationCode);
                    _context.SourceMaterials.Add(material);
                    existingUrlSet.Add(rawUrl);
                    created++;
                    details.Add(new { path = candidate.Path, status = "created" });
                    TryMirrorImportedMaterial(localPath, safeName, qualificationDescription);
                }
                catch (Exception ex)
                {
                    failed++;
                    details.Add(new { path = candidate.Path, status = "failed", reason = ex.Message });
                }
            }

            _context.SaveChanges();
            return Ok(new
            {
                owner,
                repo,
                branch,
                totalCandidates = candidates.Count,
                selected = selected.Count,
                created,
                skipped,
                failed,
                details = details.Take(100).ToList()
            });
        }

        [HttpPost("import-oai-pmh")]
        public async Task<IActionResult> ImportOaiPmh([FromBody] OaiPmhHarvestRequest req)
        {
            if (!AiRuntime.AllowCloudProviders())
                return StatusCode(StatusCodes.Status403Forbidden, "OAI-PMH import is disabled in offline mode.");
            if (req == null || string.IsNullOrWhiteSpace(req.BaseUrl))
                return BadRequest("BaseUrl is required.");

            if (!Uri.TryCreate(req.BaseUrl.Trim(), UriKind.Absolute, out var baseUri))
                return BadRequest("BaseUrl is invalid.");

            var metadataPrefix = string.IsNullOrWhiteSpace(req.MetadataPrefix) ? "oai_dc" : req.MetadataPrefix.Trim();
            var setSpec = string.IsNullOrWhiteSpace(req.Set) ? "" : req.Set.Trim();
            var fromUtc = string.IsNullOrWhiteSpace(req.FromUtc) ? "" : req.FromUtc.Trim();
            var untilUtc = string.IsNullOrWhiteSpace(req.UntilUtc) ? "" : req.UntilUtc.Trim();
            var maxRecords = req.MaxRecords <= 0 ? 500 : Math.Min(req.MaxRecords, 5000);
            var includeDeleted = req.IncludeDeleted;

            var qualificationDescription = ResolveQualificationDescription(new UploadMaterialRequest
            {
                QualificationId = req.QualificationId,
                QualificationDescription = req.QualificationDescription
            });
            var qualificationCode = ResolveQualificationCode(qualificationDescription, req.QualificationId);

            var sourceTag = $"OAI-PMH:{baseUri.Host}";
            var existingUrls = _context.SourceMaterials
                .Where(s => !string.IsNullOrWhiteSpace(s.Url))
                .Select(s => s.Url!)
                .ToList();
            var existingSet = new HashSet<string>(existingUrls, StringComparer.OrdinalIgnoreCase);

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var srcDir = Path.Combine(appData, "ETDP", "Sources");
            Directory.CreateDirectory(srcDir);

            int created = 0, skipped = 0, failed = 0, deletedSkipped = 0;
            int pages = 0;
            var details = new List<object>();
            var currentResumptionToken = "";
            var stop = false;

            while (!stop && (created + skipped + failed) < maxRecords)
            {
                var requestUri = BuildOaiPmhRequestUri(
                    baseUri.ToString(),
                    metadataPrefix,
                    setSpec,
                    fromUtc,
                    untilUtc,
                    currentResumptionToken,
                    req.ApiKeyQueryParam,
                    req.ApiKey);

                string xmlBody;
                HttpResponseMessage response;
                try
                {
                    using var oaiReq = new HttpRequestMessage(HttpMethod.Get, requestUri);
                    oaiReq.Headers.UserAgent.Add(new ProductInfoHeaderValue("ETDP", "1.0"));
                    oaiReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
                    oaiReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));
                    if (!string.IsNullOrWhiteSpace(req.ApiKey) && !string.IsNullOrWhiteSpace(req.ApiKeyHeaderName))
                    {
                        oaiReq.Headers.TryAddWithoutValidation(req.ApiKeyHeaderName.Trim(), req.ApiKey.Trim());
                    }
                    response = await _http.SendAsync(oaiReq);
                }
                catch (Exception ex)
                {
                    return BadRequest($"OAI request failed: {ex.Message}");
                }

                using var responseScope = response;
                xmlBody = await responseScope.Content.ReadAsStringAsync();
                if (!responseScope.IsSuccessStatusCode)
                    return StatusCode((int)responseScope.StatusCode, $"OAI request failed: {(int)responseScope.StatusCode} {xmlBody}");

                pages++;
                XDocument doc;
                try
                {
                    doc = XDocument.Parse(xmlBody, LoadOptions.PreserveWhitespace);
                }
                catch (Exception ex)
                {
                    return BadRequest($"Invalid XML response from OAI provider: {ex.Message}");
                }

                var errorNode = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "error");
                if (errorNode != null)
                {
                    var code = errorNode.Attributes().FirstOrDefault(a => a.Name.LocalName == "code")?.Value ?? "unknown";
                    var message = (errorNode.Value ?? "").Trim();
                    return BadRequest(new
                    {
                        error = "oai_error",
                        code,
                        message,
                        pages,
                        created,
                        skipped,
                        failed
                    });
                }

                var records = doc.Descendants().Where(x => x.Name.LocalName == "record").ToList();
                if (records.Count == 0 && string.IsNullOrWhiteSpace(currentResumptionToken))
                {
                    var tokenOnly = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "resumptionToken");
                    if (tokenOnly == null || string.IsNullOrWhiteSpace((tokenOnly.Value ?? "").Trim()))
                    {
                        break;
                    }
                }

                foreach (var record in records)
                {
                    if ((created + skipped + failed) >= maxRecords)
                    {
                        stop = true;
                        break;
                    }

                    var header = record.Elements().FirstOrDefault(x => x.Name.LocalName == "header");
                    if (header == null)
                    {
                        failed++;
                        details.Add(new { status = "failed", reason = "missing_header" });
                        continue;
                    }

                    var identifier = (header.Elements().FirstOrDefault(x => x.Name.LocalName == "identifier")?.Value ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(identifier))
                    {
                        failed++;
                        details.Add(new { status = "failed", reason = "missing_identifier" });
                        continue;
                    }

                    var datestamp = (header.Elements().FirstOrDefault(x => x.Name.LocalName == "datestamp")?.Value ?? "").Trim();
                    var isDeleted = string.Equals(header.Attribute("status")?.Value, "deleted", StringComparison.OrdinalIgnoreCase);
                    if (isDeleted && !includeDeleted)
                    {
                        deletedSkipped++;
                        continue;
                    }

                    var setSpecs = header.Elements()
                        .Where(x => x.Name.LocalName == "setSpec")
                        .Select(x => (x.Value ?? "").Trim())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var materialUrl = BuildOaiMaterialUrl(baseUri, identifier, metadataPrefix);
                    if (existingSet.Contains(materialUrl))
                    {
                        skipped++;
                        details.Add(new { identifier, status = "skipped", reason = "already_imported" });
                        continue;
                    }

                    var metadataNode = record.Elements().FirstOrDefault(x => x.Name.LocalName == "metadata");
                    var title = "";
                    var lines = new List<string>
                    {
                        $"OAI Base URL: {baseUri}",
                        $"Identifier: {identifier}",
                        $"Metadata Prefix: {metadataPrefix}",
                        $"Datestamp: {datestamp}"
                    };
                    if (isDeleted) lines.Add("Record Status: deleted");
                    if (setSpecs.Count > 0) lines.Add("SetSpec: " + string.Join(", ", setSpecs));

                    if (metadataNode != null)
                    {
                        var leafFields = metadataNode
                            .Descendants()
                            .Where(x => !x.HasElements)
                            .Select(x => new
                            {
                                Name = x.Name.LocalName,
                                Value = (x.Value ?? "").Trim()
                            })
                            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                            .ToList();

                        foreach (var field in leafFields)
                        {
                            lines.Add($"{field.Name}: {field.Value}");
                        }

                        title = leafFields.FirstOrDefault(x => string.Equals(x.Name, "title", StringComparison.OrdinalIgnoreCase))?.Value
                            ?? leafFields.FirstOrDefault(x => string.Equals(x.Name, "identifier", StringComparison.OrdinalIgnoreCase))?.Value
                            ?? "";
                    }

                    if (string.IsNullOrWhiteSpace(title)) title = identifier;
                    var safeTitle = title.Length > 200 ? title.Substring(0, 200) : title;
                    var extractedText = CleanExtractedText(string.Join("\n", lines));

                    var fileName = BuildOaiMaterialFileName(baseUri.Host, identifier, metadataPrefix);
                    var filePath = Path.Combine(srcDir, $"{Guid.NewGuid()}_{fileName}");
                    await System.IO.File.WriteAllTextAsync(filePath, extractedText);

                    var material = new SourceMaterial
                    {
                        Title = $"[OAI] {safeTitle}",
                        FileName = fileName,
                        FilePath = filePath,
                        FileType = "txt",
                        Url = materialUrl,
                        ExtractedText = extractedText,
                        QualificationCode = qualificationCode,
                        QualificationDescription = qualificationDescription,
                        SubjectDescription = sourceTag,
                        TopicDescription = string.IsNullOrWhiteSpace(setSpec) ? $"Prefix:{metadataPrefix}" : $"Prefix:{metadataPrefix};Set:{setSpec}",
                        AssessmentCriteriaDescription = $"Identifier:{identifier}"
                    };
                    ApplyKnowledgeMetadata(material, "developer_knowledge_base", qualificationCode);

                    _context.SourceMaterials.Add(material);
                    existingSet.Add(materialUrl);
                    created++;
                    details.Add(new { identifier, status = "created" });
                    TryMirrorImportedMaterial(filePath, fileName, qualificationDescription);
                }

                var tokenNode = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "resumptionToken");
                currentResumptionToken = (tokenNode?.Value ?? "").Trim();
                if (string.IsNullOrWhiteSpace(currentResumptionToken))
                {
                    stop = true;
                }
            }

            _context.SaveChanges();
            return Ok(new
            {
                baseUrl = baseUri.ToString(),
                metadataPrefix,
                set = setSpec,
                fromUtc,
                untilUtc,
                pages,
                created,
                skipped,
                failed,
                deletedSkipped,
                details = details.Take(100).ToList()
            });
        }

        [HttpPost("import-engineering-seed")]
        public IActionResult ImportEngineeringSeed([FromBody] EngineeringSeedImportRequest req)
        {
            if (!GetOpenAipSearchEnabled())
                return StatusCode(StatusCodes.Status403Forbidden, "Engineering seed source is disabled by policy.");

            var defaultSeedRoot = Path.Combine(EtdpPaths.CombineProject("Imports"), "Open AIP", "EngineeringSeed");
            var requestedRoot = string.IsNullOrWhiteSpace(req?.RootPath)
                ? defaultSeedRoot
                : req.RootPath!.Trim();
            if (!TryResolveProjectScopedPath(requestedRoot, out var rootPath, out var pathError))
                return BadRequest(pathError);
            if (!Directory.Exists(rootPath))
                return NotFound($"Engineering seed folder not found: {rootPath}");

            var maxFiles = req?.MaxFiles <= 0 ? 5000 : Math.Min(req!.MaxFiles, 50000);
            var qualificationDescription = ResolveQualificationDescription(new UploadMaterialRequest
            {
                QualificationId = req?.QualificationId,
                QualificationDescription = req?.QualificationDescription
            });
            var qualificationCode = ResolveQualificationCode(qualificationDescription, req?.QualificationId);

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var srcDir = Path.Combine(appData, "ETDP", "Sources", "EngineeringSeedMeta");
            Directory.CreateDirectory(srcDir);

            var existingUrls = _context.SourceMaterials
                .Where(s => !string.IsNullOrWhiteSpace(s.Url))
                .Select(s => s.Url!)
                .ToList();
            var existingSet = new HashSet<string>(existingUrls, StringComparer.OrdinalIgnoreCase);

            var labelMaps = LoadEngineeringSeedLabelMaps(rootPath);
            var pklFiles = Directory
                .GetFiles(rootPath, "*.pkl", SearchOption.AllDirectories)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var totalCandidates = pklFiles.Count;
            int created = 0, skipped = 0, failed = 0;
            var details = new List<object>();

            foreach (var filePath in pklFiles)
            {
                if ((created + skipped + failed) >= maxFiles) break;
                var relPath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
                var sourceUrl = BuildEngineeringSeedUrl(relPath);
                if (existingSet.Contains(sourceUrl))
                {
                    skipped++;
                    details.Add(new { file = relPath, status = "skipped", reason = "already_imported" });
                    continue;
                }

                try
                {
                    var fileInfo = new FileInfo(filePath);
                    var dataset = relPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Unknown";
                    var fileName = Path.GetFileName(filePath);
                    var label = ResolveEngineeringSeedLabel(labelMaps, dataset, fileName);

                    var text = BuildEngineeringSeedMetadataText(
                        rootPath,
                        relPath,
                        fileName,
                        dataset,
                        fileInfo.Length,
                        label);

                    var safeName = BuildEngineeringSeedMetaFileName(dataset, fileName);
                    var metaFilePath = Path.Combine(srcDir, $"{Guid.NewGuid()}_{safeName}");
                    System.IO.File.WriteAllText(metaFilePath, text);

                    var material = new SourceMaterial
                    {
                        Title = $"[SEED] {relPath}",
                        FileName = safeName,
                        FilePath = metaFilePath,
                        FileType = "txt",
                        Url = sourceUrl,
                        ExtractedText = text,
                        QualificationCode = qualificationCode,
                        QualificationDescription = qualificationDescription,
                        SubjectDescription = "EngineeringSeed",
                        TopicDescription = $"Dataset:{dataset}",
                        AssessmentCriteriaDescription = string.IsNullOrWhiteSpace(label) ? null : $"Label:{label}"
                    };
                    ApplyKnowledgeMetadata(material, "developer_knowledge_base", qualificationCode);

                    _context.SourceMaterials.Add(material);
                    existingSet.Add(sourceUrl);
                    created++;
                }
                catch (Exception ex)
                {
                    failed++;
                    details.Add(new { file = relPath, status = "failed", reason = ex.Message });
                }
            }

            _context.SaveChanges();
            return Ok(new
            {
                rootPath,
                totalCandidates,
                created,
                skipped,
                failed,
                maxFiles,
                note = "Imported as metadata index only. Pickle binaries were not deserialized.",
                details = details.Take(100).ToList()
            });
        }

        [HttpPost("import-local-folder")]
        public async Task<IActionResult> ImportLocalFolder([FromBody] LocalFolderImportRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.RootPath))
                return BadRequest("RootPath is required.");

            if (!TryResolveProjectScopedPath(req.RootPath, out var rootPath, out var pathError))
                return BadRequest(pathError);
            if (!Directory.Exists(rootPath))
                return NotFound($"Folder not found: {rootPath}");

            var maxFiles = req.MaxFiles <= 0 ? 1000 : Math.Min(req.MaxFiles, 10000);
            var maxFileSizeBytes = (req.MaxFileSizeKb <= 0 ? 20480 : Math.Min(req.MaxFileSizeKb, 102400)) * 1024L;
            var includeCodeFiles = req.IncludeCodeFiles;
            var poolName = string.IsNullOrWhiteSpace(req.KnowledgePool)
                ? Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : req.KnowledgePool!.Trim();
            if (string.IsNullOrWhiteSpace(poolName)) poolName = "LocalFolder";

            var qualificationDescription = ResolveQualificationDescription(new UploadMaterialRequest
            {
                QualificationId = req.QualificationId,
                QualificationDescription = req.QualificationDescription
            });
            var qualificationCode = ResolveQualificationCode(qualificationDescription, req.QualificationId);

            var sourceTag = $"LocalFolder:{poolName}";
            var existingUrls = _context.SourceMaterials
                .Where(s => !string.IsNullOrWhiteSpace(s.Url))
                .Select(s => s.Url!)
                .ToList();
            var existingSet = new HashSet<string>(existingUrls, StringComparer.OrdinalIgnoreCase);

            var candidates = Directory
                .GetFiles(rootPath, "*", SearchOption.AllDirectories)
                .Select(path =>
                {
                    var ext = Path.GetExtension(path);
                    var relPath = Path.GetRelativePath(rootPath, path).Replace('\\', '/');
                    var info = new FileInfo(path);
                    return new { Path = path, Ext = ext, RelPath = relPath, Size = info.Length };
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Ext))
                .Where(x => IsSupportedGitHubImportExtension(x.Ext, includeCodeFiles))
                .Where(x => x.Size > 0 && x.Size <= maxFileSizeBytes)
                .OrderBy(x => x.RelPath, StringComparer.OrdinalIgnoreCase)
                .Take(maxFiles)
                .ToList();

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var srcDir = Path.Combine(appData, "ETDP", "Sources", "LocalFolderImports");
            Directory.CreateDirectory(srcDir);

            var created = 0;
            var skipped = 0;
            var failed = 0;
            var details = new List<object>();

            foreach (var candidate in candidates)
            {
                var sourceUrl = BuildLocalFolderMaterialUrl(rootPath, candidate.RelPath);
                if (existingSet.Contains(sourceUrl))
                {
                    skipped++;
                    details.Add(new { path = candidate.RelPath, status = "skipped", reason = "already_imported" });
                    continue;
                }

                try
                {
                    var safeName = BuildLocalFolderMaterialFileName(poolName, candidate.RelPath);
                    var localPath = Path.Combine(srcDir, $"{Guid.NewGuid()}_{safeName}");
                    System.IO.File.Copy(candidate.Path, localPath, true);

                    string text;
                    await using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (IsGitHubTextExtension(candidate.Ext))
                        {
                            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, true, 1024, true);
                            text = CleanExtractedText(await reader.ReadToEndAsync());
                        }
                        else
                        {
                            text = await ExtractTextFromFileStreamAsync(fs, candidate.Ext);
                        }
                    }
                    text = await ApplyOcrEnhancementAsync(localPath, candidate.Ext, text);

                    var material = new SourceMaterial
                    {
                        Title = $"[LOCAL] {poolName} :: {candidate.RelPath}",
                        FileName = safeName,
                        FilePath = localPath,
                        FileType = candidate.Ext.TrimStart('.'),
                        Url = sourceUrl,
                        ExtractedText = text ?? string.Empty,
                        QualificationCode = qualificationCode,
                        QualificationDescription = qualificationDescription,
                        SubjectDescription = sourceTag,
                        TopicDescription = $"Root:{rootPath}",
                        AssessmentCriteriaDescription = $"Path:{candidate.RelPath}"
                    };
                    ApplyKnowledgeMetadata(material, "local_source_upload", qualificationCode);
                    _context.SourceMaterials.Add(material);
                    existingSet.Add(sourceUrl);
                    created++;
                }
                catch (Exception ex)
                {
                    failed++;
                    details.Add(new { path = candidate.RelPath, status = "failed", reason = ex.Message });
                }
            }

            _context.SaveChanges();
            return Ok(new
            {
                rootPath,
                poolName,
                selected = candidates.Count,
                created,
                skipped,
                failed,
                maxFiles,
                includeCodeFiles,
                details = details.Take(100).ToList()
            });
        }

        [HttpPost("scaffold-knowledge-hierarchy")]
        public IActionResult ScaffoldKnowledgeHierarchy([FromBody] KnowledgeHierarchyScaffoldRequest req)
        {
            if (req == null) return BadRequest("Request body is required.");

            var qualificationCode = (req.QualificationCode ?? string.Empty).Trim();
            var qualificationDescription = (req.QualificationDescription ?? string.Empty).Trim();

            Qualification? qualification = null;
            if (req.QualificationId.HasValue && req.QualificationId.Value > 0)
            {
                qualification = _context.Qualifications.Find(req.QualificationId.Value);
            }
            if (qualification == null && !string.IsNullOrWhiteSpace(qualificationCode))
            {
                qualification = _context.Qualifications.FirstOrDefault(q => q.QualificationNumber == qualificationCode);
            }
            if (qualification == null && !string.IsNullOrWhiteSpace(qualificationDescription))
            {
                qualification = _context.Qualifications.FirstOrDefault(q => q.QualificationDescription == qualificationDescription);
            }

            if (qualification != null)
            {
                if (string.IsNullOrWhiteSpace(qualificationCode))
                    qualificationCode = qualification.QualificationNumber ?? string.Empty;
                if (string.IsNullOrWhiteSpace(qualificationDescription))
                    qualificationDescription = qualification.QualificationDescription ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(qualificationCode) && !string.IsNullOrWhiteSpace(qualificationDescription))
            {
                qualificationCode = ResolveQualificationCode(qualificationDescription);
            }
            if (string.IsNullOrWhiteSpace(qualificationDescription))
            {
                qualificationDescription = qualificationCode;
            }

            if (string.IsNullOrWhiteSpace(qualificationCode) || string.IsNullOrWhiteSpace(qualificationDescription))
            {
                return BadRequest("QualificationCode or QualificationDescription is required.");
            }

            var structure = _knowledgeHierarchyService.EnsureQualificationStructure(qualificationCode, qualificationDescription);
            return Ok(new
            {
                qualificationId = qualification?.Id,
                qualificationCode = structure.QualificationCode,
                qualificationDescription = structure.QualificationDescription,
                qualificationRootPath = structure.QualificationRootPath,
                localSourceUpload = new
                {
                    inbox = structure.LocalInboxPath,
                    archive = structure.LocalArchivePath
                },
                developerKnowledgeBase = new
                {
                    inbox = structure.DeveloperInboxPath,
                    archive = structure.DeveloperArchivePath
                },
                uploadReadmePath = structure.UploadReadmePath
            });
        }

        [HttpPost("sync-knowledge-hierarchy")]
        public IActionResult SyncKnowledgeHierarchy([FromBody] SyncKnowledgeHierarchyRequest? req)
        {
            var options = new KnowledgeHierarchyService.SyncOptions
            {
                QualificationCode = req?.QualificationCode,
                QualificationDescription = req?.QualificationDescription,
                IncludeLocalSourceUploads = req?.IncludeLocalSourceUploads ?? true,
                IncludeDeveloperKnowledgeBase = req?.IncludeDeveloperKnowledgeBase ?? true,
                MaxFilesPerInbox = req?.MaxFilesPerInbox ?? 1000,
                RebuildUploadReadme = req?.RebuildUploadReadme ?? true,
                ConsolidateLegacyFolders = req?.ConsolidateLegacyFolders ?? true
            };

            var sync = _knowledgeHierarchyService.SyncKnowledgeHierarchy(options);
            return Ok(sync);
        }

        [HttpGet("agent-knowledge-structure")]
        public IActionResult AgentKnowledgeStructure()
        {
            var structures = _knowledgeHierarchyService.EnsureAgentKnowledgeStructures();
            return Ok(new
            {
                rootPath = _knowledgeHierarchyService.GetAgentKnowledgeRootPath(),
                readmePath = _knowledgeHierarchyService.GetAgentKnowledgeReadmePath(),
                shared = new
                {
                    rootPath = structures["shared"].ScopeRootPath,
                    inbox = structures["shared"].InboxPath,
                    archive = structures["shared"].ArchivePath,
                    duplicates = structures["shared"].DuplicatePath
                },
                mira = new
                {
                    rootPath = structures["mira"].ScopeRootPath,
                    inbox = structures["mira"].InboxPath,
                    archive = structures["mira"].ArchivePath,
                    duplicates = structures["mira"].DuplicatePath
                },
                gemma = new
                {
                    rootPath = (structures.ContainsKey("gemma") ? structures["gemma"] : (structures.ContainsKey("qwen") ? structures["qwen"] : structures["shared"])).ScopeRootPath,
                    inbox = (structures.ContainsKey("gemma") ? structures["gemma"] : (structures.ContainsKey("qwen") ? structures["qwen"] : structures["shared"])).InboxPath,
                    archive = (structures.ContainsKey("gemma") ? structures["gemma"] : (structures.ContainsKey("qwen") ? structures["qwen"] : structures["shared"])).ArchivePath,
                    duplicates = (structures.ContainsKey("gemma") ? structures["gemma"] : (structures.ContainsKey("qwen") ? structures["qwen"] : structures["shared"])).DuplicatePath
                },
                qwen = new
                {
                    rootPath = (structures.ContainsKey("gemma") ? structures["gemma"] : (structures.ContainsKey("qwen") ? structures["qwen"] : structures["shared"])).ScopeRootPath,
                    inbox = (structures.ContainsKey("gemma") ? structures["gemma"] : (structures.ContainsKey("qwen") ? structures["qwen"] : structures["shared"])).InboxPath,
                    archive = (structures.ContainsKey("gemma") ? structures["gemma"] : (structures.ContainsKey("qwen") ? structures["qwen"] : structures["shared"])).ArchivePath,
                    duplicates = (structures.ContainsKey("gemma") ? structures["gemma"] : (structures.ContainsKey("qwen") ? structures["qwen"] : structures["shared"])).DuplicatePath
                }
            });
        }

        [HttpPost("sync-agent-knowledge")]
        public IActionResult SyncAgentKnowledge([FromBody] SyncAgentKnowledgeRequest? req)
        {
            var sync = _knowledgeHierarchyService.SyncAgentKnowledge(new KnowledgeHierarchyService.AgentKnowledgeSyncOptions
            {
                Scope = req?.AgentMode,
                IncludeSharedKnowledge = req?.IncludeSharedKnowledge ?? true,
                MaxFilesPerInbox = req?.MaxFilesPerInbox ?? 1000,
                RebuildReadme = req?.RebuildReadme ?? true
            });
            return Ok(sync);
        }

        [HttpPost("consolidate-knowledge-hierarchy")]
        public IActionResult ConsolidateKnowledgeHierarchy([FromBody] ConsolidateKnowledgeHierarchyRequest? req)
        {
            var result = _knowledgeHierarchyService.ConsolidateLegacyQualificationFolders(
                new KnowledgeHierarchyService.ConsolidationOptions
                {
                    QualificationCode = req?.QualificationCode,
                    RebuildUploadReadme = req?.RebuildUploadReadme ?? true,
                    RemoveEmptyLegacyFolders = req?.RemoveEmptyLegacyFolders ?? true
                });
            return Ok(result);
        }

        [HttpGet("upload-structure-readme")]
        public IActionResult UploadStructureReadme()
        {
            var path = _knowledgeHierarchyService.EnsureUploadReadme();
            if (!System.IO.File.Exists(path))
                return NotFound("Upload structure readme was not found.");
            var content = System.IO.File.ReadAllText(path);
            return Ok(new { path, content });
        }

        [HttpPost("index-qualification-knowledge")]
        public async Task<IActionResult> IndexQualificationKnowledge([FromBody] IndexQualificationKnowledgeRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.RootPath))
                return BadRequest("RootPath is required.");

            if (!TryResolveProjectScopedPath(req.RootPath, out var requestedPath, out var pathError))
                return BadRequest(pathError);
            var isSingleFile = System.IO.File.Exists(requestedPath);
            var isDirectory = Directory.Exists(requestedPath);
            if (!isSingleFile && !isDirectory)
                return NotFound($"Path not found: {requestedPath}");

            var sourceType = NormalizeKnowledgeSourceType(req.SourceType);
            if (string.IsNullOrWhiteSpace(sourceType))
                sourceType = "developer_knowledge_base";

            var qualificationCode = (req.QualificationCode ?? string.Empty).Trim();
            var qualificationDescription = (req.QualificationDescription ?? string.Empty).Trim();

            Qualification? qualification = null;
            if (req.QualificationId.HasValue && req.QualificationId.Value > 0)
            {
                qualification = _context.Qualifications.Find(req.QualificationId.Value);
            }
            if (qualification == null && !string.IsNullOrWhiteSpace(qualificationCode))
            {
                qualification = _context.Qualifications.FirstOrDefault(q => q.QualificationNumber == qualificationCode);
            }
            if (qualification == null && !string.IsNullOrWhiteSpace(qualificationDescription))
            {
                qualification = _context.Qualifications.FirstOrDefault(q => q.QualificationDescription == qualificationDescription);
            }

            if (qualification != null)
            {
                if (string.IsNullOrWhiteSpace(qualificationCode))
                    qualificationCode = qualification.QualificationNumber ?? string.Empty;
                if (string.IsNullOrWhiteSpace(qualificationDescription))
                    qualificationDescription = qualification.QualificationDescription ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(qualificationCode) && !string.IsNullOrWhiteSpace(qualificationDescription))
            {
                qualificationCode = ResolveQualificationCode(qualificationDescription);
            }
            if (string.IsNullOrWhiteSpace(qualificationDescription))
            {
                qualificationDescription = qualificationCode;
            }

            if (string.IsNullOrWhiteSpace(qualificationDescription))
                return BadRequest("QualificationCode or QualificationDescription is required.");

            var scaffold = _knowledgeHierarchyService.EnsureQualificationStructure(qualificationCode, qualificationDescription);
            var knowledgeSourceRootPath = Path.Combine(scaffold.QualificationRootPath, sourceType);
            var knowledgeArchivePath = Path.Combine(knowledgeSourceRootPath, "archive");
            Directory.CreateDirectory(knowledgeSourceRootPath);
            Directory.CreateDirectory(knowledgeArchivePath);

            var maxFiles = req.MaxFiles <= 0 ? 1000 : Math.Min(req.MaxFiles, 20000);
            var allowedExtensions = KnowledgeUploadExtensions;

            List<string> candidateFiles;
            if (isSingleFile)
            {
                candidateFiles = new List<string> { requestedPath };
            }
            else
            {
                var searchOption = req.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                candidateFiles = Directory.GetFiles(requestedPath, "*", searchOption)
                    .Where(p => allowedExtensions.Contains(Path.GetExtension(p)))
                    .Where(p => !IsImageSidecarFile(p))
                    .Where(p => !IsManagedKnowledgeArtifactPath(p))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .Take(maxFiles)
                    .ToList();
            }

            if (candidateFiles.Count == 0)
            {
                return Ok(new
                {
                    qualificationCode,
                    qualificationDescription,
                    sourceType,
                    scanned = 0,
                    created = 0,
                    skipped = 0,
                    failed = 0,
                    knowledgeRootPath = knowledgeSourceRootPath,
                    note = "No supported files found."
                });
            }

            var existingNumbers = _context.SourceMaterials
                .Where(s => (s.QualificationCode ?? "") == qualificationCode &&
                            (s.KnowledgeSourceType ?? "") == sourceType &&
                            s.KnowledgeNumber.HasValue)
                .Select(s => s.KnowledgeNumber!.Value)
                .ToList();
            var nextKnowledgeNumber = req.StartingKnowledgeNumber.HasValue && req.StartingKnowledgeNumber.Value > 0
                ? req.StartingKnowledgeNumber.Value
                : (existingNumbers.Count > 0 ? existingNumbers.Max() + 1 : 1);

            var created = 0;
            var skipped = 0;
            var failed = 0;
            var details = new List<object>();

            foreach (var sourcePath in candidateFiles)
            {
                var ext = Path.GetExtension(sourcePath);
                if (!allowedExtensions.Contains(ext))
                {
                    skipped++;
                    details.Add(new { file = sourcePath, status = "skipped", reason = "unsupported_extension" });
                    continue;
                }

                var originalName = Path.GetFileName(sourcePath);
                var sidecarPaths = KnowledgeImageExtensions.Contains(ext)
                    ? FindImageSidecarPaths(sourcePath)
                    : new List<string>();
                var parsedNumber = ParseKnowledgeNumber(originalName);
                var knowledgeNumber = parsedNumber ?? nextKnowledgeNumber;
                if (!parsedNumber.HasValue)
                {
                    nextKnowledgeNumber++;
                }
                else
                {
                    nextKnowledgeNumber = Math.Max(nextKnowledgeNumber, knowledgeNumber + 1);
                }

                var now = DateTime.UtcNow;
                var safeStem = MakeSafeFilePart(Path.GetFileNameWithoutExtension(originalName), $"knowledge_{knowledgeNumber:D4}");
                var archivedName = $"KB-{knowledgeNumber:D4}_{now:yyyyMMddHHmmss}_{safeStem}{ext.ToLowerInvariant()}";
                var archivedPath = Path.Combine(knowledgeArchivePath, archivedName);
                if (!string.Equals(sourcePath, archivedPath, StringComparison.OrdinalIgnoreCase))
                {
                    System.IO.File.Copy(sourcePath, archivedPath, true);
                }

                var knowledgeUrl = $"knowledge://{Uri.EscapeDataString(qualificationCode)}/{Uri.EscapeDataString(sourceType)}/kb-{knowledgeNumber:D4}/{Uri.EscapeDataString(originalName)}";
                var duplicate = _context.SourceMaterials.Any(s =>
                    s.Url == knowledgeUrl &&
                    (s.QualificationCode ?? "") == qualificationCode &&
                    (s.KnowledgeSourceType ?? "") == sourceType);
                if (duplicate)
                {
                    skipped++;
                    details.Add(new { file = originalName, status = "skipped", reason = "already_indexed", knowledgeNumber });
                    continue;
                }

                try
                {
                    string text;
                    if (KnowledgeImageExtensions.Contains(ext))
                    {
                        text = ExtractTextFromImageFile(archivedPath, sidecarPaths);
                    }
                    else
                    {
                        await using var fs = new FileStream(archivedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        if (IsGitHubTextExtension(ext))
                        {
                            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, true, 4096, true);
                            text = CleanExtractedText(await reader.ReadToEndAsync());
                        }
                        else
                        {
                            text = await ExtractTextFromFileStreamAsync(fs, ext);
                        }
                    }
                    text = await ApplyOcrEnhancementAsync(archivedPath, ext, text);

                    var derivedVisualResult = new DerivedPdfVisualImportResult();
                    if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        derivedVisualResult = ExtractDerivedPdfVisualMaterials(
                            archivedPath,
                            originalName,
                            sourceType,
                            qualificationCode,
                            qualificationDescription,
                            knowledgeSourceRootPath,
                            knowledgeUrl,
                            ref nextKnowledgeNumber);
                        text = AppendPdfVisualSummary(text, derivedVisualResult.SummaryText);
                    }
                    var derivedVisualMaterials = derivedVisualResult.Materials;

                    var material = new SourceMaterial
                    {
                        Title = $"[KB {knowledgeNumber:D4}] {qualificationCode} - {qualificationDescription} :: {originalName}",
                        FileName = archivedName,
                        FilePath = archivedPath,
                        FileType = ext.TrimStart('.'),
                        Url = knowledgeUrl,
                        QualificationCode = qualificationCode,
                        QualificationDescription = qualificationDescription,
                        SubjectDescription = $"KnowledgeBase:{sourceType}",
                        TopicDescription = $"KnowledgeNumber:{knowledgeNumber:D4}",
                        AssessmentCriteriaDescription = $"UploadedAtUtc:{now:O};Source:{originalName}",
                        ExtractedText = text ?? string.Empty
                    };

                    ApplyKnowledgeMetadata(
                        material,
                        sourceType,
                        qualificationCode,
                        knowledgeNumber: knowledgeNumber,
                        uploadedAtUtc: now,
                        knowledgeRootPath: knowledgeSourceRootPath,
                        knowledgeLabel: BuildKnowledgeLabel(originalName, originalName, knowledgeNumber));

                    _context.SourceMaterials.Add(material);
                    if (derivedVisualMaterials.Count > 0)
                    {
                        _context.SourceMaterials.AddRange(derivedVisualMaterials);
                        created += derivedVisualMaterials.Count;
                        foreach (var visualMaterial in derivedVisualMaterials)
                        {
                            details.Add(new
                            {
                                file = visualMaterial.FileName,
                                status = "created_visual",
                                knowledgeNumber = visualMaterial.KnowledgeNumber,
                                archivedPath = visualMaterial.FilePath
                            });
                        }
                    }
                    created++;
                    details.Add(new { file = originalName, status = "created", knowledgeNumber, archivedPath });
                }
                catch (Exception ex)
                {
                    failed++;
                    details.Add(new { file = originalName, status = "failed", reason = ex.Message, knowledgeNumber });
                }
            }

            _context.SaveChanges();
            KnowledgeHierarchyService.CoverageReportSummary? coverageReport = null;
            if (string.Equals(sourceType, "developer_knowledge_base", StringComparison.OrdinalIgnoreCase))
            {
                coverageReport = _knowledgeHierarchyService.GenerateDeveloperCoverageReport(new KnowledgeHierarchyService.CoverageReportOptions
                {
                    QualificationCode = qualificationCode,
                    QualificationDescription = qualificationDescription,
                    QualificationRootPath = scaffold.QualificationRootPath,
                    UploadedInRun = created,
                    SkippedInRun = skipped,
                    FailedInRun = failed
                });
            }

            return Ok(new
            {
                qualificationCode,
                qualificationDescription,
                sourceType,
                scanned = candidateFiles.Count,
                created,
                skipped,
                failed,
                knowledgeRootPath = knowledgeSourceRootPath,
                coverageReport,
                details = details.Take(200).ToList()
            });
        }

        [HttpPost("launch-knowledge-focus")]
        public async Task<IActionResult> LaunchKnowledgeFocus([FromBody] LaunchKnowledgeFocusRequest? req)
        {
            req ??= new LaunchKnowledgeFocusRequest();

            var requestedQualificationCode = (req.QualificationCode ?? string.Empty).Trim();
            var qualificationCode = (req.QualificationCode ?? string.Empty).Trim();
            var qualificationDescription = (req.QualificationDescription ?? string.Empty).Trim();

            Qualification? qualification = null;
            if (req.QualificationId.HasValue && req.QualificationId.Value > 0)
            {
                qualification = _context.Qualifications.Find(req.QualificationId.Value);
            }
            if (qualification == null && !string.IsNullOrWhiteSpace(qualificationCode))
            {
                qualification = _context.Qualifications.FirstOrDefault(q => q.QualificationNumber == qualificationCode);
            }
            if (qualification == null && qualificationCode == "90420")
            {
                qualification = _context.Qualifications
                    .OrderByDescending(q => q.Id)
                    .FirstOrDefault(q => q.QualificationNumber == "94020");
            }
            if (qualification == null && !string.IsNullOrWhiteSpace(qualificationDescription))
            {
                qualification = _context.Qualifications.FirstOrDefault(q => q.QualificationDescription == qualificationDescription);
            }

            if (qualification != null)
            {
                if (!string.IsNullOrWhiteSpace(qualification.QualificationNumber))
                    qualificationCode = qualification.QualificationNumber.Trim();
                if (!string.IsNullOrWhiteSpace(qualification.QualificationDescription))
                    qualificationDescription = qualification.QualificationDescription.Trim();
            }

            if (string.IsNullOrWhiteSpace(qualificationCode) && !string.IsNullOrWhiteSpace(qualificationDescription))
            {
                qualificationCode = ResolveQualificationCode(qualificationDescription);
            }
            if (string.IsNullOrWhiteSpace(qualificationDescription))
            {
                qualificationDescription = qualificationCode;
            }

            if (string.IsNullOrWhiteSpace(qualificationCode) || string.IsNullOrWhiteSpace(qualificationDescription))
            {
                return BadRequest("QualificationCode or QualificationDescription is required.");
            }

            var stages = new List<object>();
            var stopOnFailure = req.StopOnStageFailure;

            async Task<bool> RunFolderStageAsync(string stageName, int order, string rootPath, int maxFiles, string sourceType)
            {
                var stageRequest = new IndexQualificationKnowledgeRequest
                {
                    RootPath = rootPath,
                    QualificationId = qualification?.Id,
                    QualificationCode = qualificationCode,
                    QualificationDescription = qualificationDescription,
                    SourceType = sourceType,
                    MaxFiles = maxFiles <= 0 ? 1000 : maxFiles,
                    Recursive = req.Recursive
                };

                var action = await IndexQualificationKnowledge(stageRequest);
                var parsed = ParseActionResult(action);
                stages.Add(new
                {
                    order,
                    stage = stageName,
                    sourceType,
                    rootPath,
                    success = parsed.Success,
                    statusCode = parsed.StatusCode,
                    result = parsed.Payload
                });

                return parsed.Success;
            }

            var educationSuccess = await RunFolderStageAsync(
                stageName: "education_library",
                order: 1,
                rootPath: req.EducationRootPath,
                maxFiles: req.EducationMaxFiles,
                sourceType: "focus_education_library");
            if (!educationSuccess && stopOnFailure)
            {
                return StatusCode(StatusCodes.Status409Conflict, new
                {
                    launched = false,
                    stopReason = "Education library stage failed.",
                    qualificationId = qualification?.Id,
                    qualificationCode,
                    qualificationDescription,
                    stages
                });
            }

            if (req.IncludeLessonPlanContent)
            {
                var lessonPlanStage = await IndexLessonPlanKnowledgeStageAsync(
                    qualificationId: qualification?.Id,
                    qualificationCode: qualificationCode,
                    qualificationDescription: qualificationDescription,
                    maxRows: req.LessonPlanMaxRows);

                stages.Add(new
                {
                    order = 2,
                    stage = "lesson_plan_content",
                    sourceType = "focus_lesson_plan_content",
                    rootPath = lessonPlanStage.RootPath,
                    success = lessonPlanStage.Success,
                    statusCode = lessonPlanStage.StatusCode,
                    result = lessonPlanStage.Payload
                });

                if (!lessonPlanStage.Success && stopOnFailure)
                {
                    return StatusCode(StatusCodes.Status409Conflict, new
                    {
                        launched = false,
                        stopReason = "Lesson plan content stage failed.",
                        qualificationId = qualification?.Id,
                        qualificationCode,
                        qualificationDescription,
                        stages
                    });
                }
            }

            var engineeringSuccess = await RunFolderStageAsync(
                stageName: "engineering_library",
                order: req.IncludeLessonPlanContent ? 3 : 2,
                rootPath: req.EngineeringRootPath,
                maxFiles: req.EngineeringMaxFiles,
                sourceType: "focus_engineering_library");
            if (!engineeringSuccess && stopOnFailure)
            {
                return StatusCode(StatusCodes.Status409Conflict, new
                {
                    launched = false,
                    stopReason = "Engineering library stage failed.",
                    qualificationId = qualification?.Id,
                    qualificationCode,
                    qualificationDescription,
                    stages
                });
            }

            var allSuccessful = stages.All(x =>
            {
                var prop = x.GetType().GetProperty("success");
                return prop?.GetValue(x) is bool ok && ok;
            });

            return Ok(new
            {
                launched = true,
                completed = allSuccessful,
                qualificationId = qualification?.Id,
                requestedQualificationCode,
                qualificationCode,
                qualificationDescription,
                launchOrder = req.IncludeLessonPlanContent
                    ? new[] { "education_library", "lesson_plan_content", "engineering_library" }
                    : new[] { "education_library", "engineering_library" },
                stages
            });
        }

        private static (bool Success, int StatusCode, object Payload) ParseActionResult(IActionResult action)
        {
            if (action is OkObjectResult ok)
            {
                return (true, StatusCodes.Status200OK, ok.Value ?? new { });
            }

            if (action is ObjectResult obj)
            {
                var code = obj.StatusCode ?? StatusCodes.Status500InternalServerError;
                return (code >= 200 && code <= 299, code, obj.Value ?? new { });
            }

            if (action is StatusCodeResult statusCode)
            {
                var code = statusCode.StatusCode;
                return (code >= 200 && code <= 299, code, new { });
            }

            return (false, StatusCodes.Status500InternalServerError, new
            {
                error = "Unsupported action result type.",
                resultType = action.GetType().Name
            });
        }

        private async Task<(bool Success, int StatusCode, object Payload, string RootPath)> IndexLessonPlanKnowledgeStageAsync(
            int? qualificationId,
            string qualificationCode,
            string qualificationDescription,
            int maxRows)
        {
            if (!qualificationId.HasValue || qualificationId.Value <= 0)
            {
                return (false, StatusCodes.Status400BadRequest, new
                {
                    error = "QualificationId is required to index lesson plan content."
                }, string.Empty);
            }

            var sourceType = "focus_lesson_plan_content";
            try
            {
                var rows = _context.LecturerToolkitEntries
                    .Where(x => x.QualificationsId == qualificationId.Value)
                    .OrderBy(x => x.SubjectCode)
                    .ThenBy(x => x.Lpn)
                    .ThenBy(x => x.Id)
                    .ToList();
                if (maxRows > 0)
                {
                    rows = rows.Take(maxRows).ToList();
                }
                if (rows.Count == 0)
                {
                    return (false, StatusCodes.Status404NotFound, new
                    {
                        error = "No lecturer toolkit rows found for lesson plan content indexing.",
                        qualificationId = qualificationId.Value
                    }, string.Empty);
                }

                var scaffold = _knowledgeHierarchyService.EnsureQualificationStructure(qualificationCode, qualificationDescription);
                var sourceRootPath = Path.Combine(scaffold.QualificationRootPath, sourceType);
                var archivePath = Path.Combine(sourceRootPath, "archive");
                Directory.CreateDirectory(sourceRootPath);
                Directory.CreateDirectory(archivePath);

                var existingUrls = _context.SourceMaterials
                    .Where(s =>
                        (s.QualificationCode ?? string.Empty) == qualificationCode &&
                        (s.KnowledgeSourceType ?? string.Empty) == sourceType &&
                        !string.IsNullOrWhiteSpace(s.Url))
                    .Select(s => s.Url!)
                    .ToList();
                var existingSet = new HashSet<string>(existingUrls, StringComparer.OrdinalIgnoreCase);

                var existingNumbers = _context.SourceMaterials
                    .Where(s =>
                        (s.QualificationCode ?? string.Empty) == qualificationCode &&
                        (s.KnowledgeSourceType ?? string.Empty) == sourceType &&
                        s.KnowledgeNumber.HasValue)
                    .Select(s => s.KnowledgeNumber!.Value)
                    .ToList();
                var nextKnowledgeNumber = existingNumbers.Count > 0 ? existingNumbers.Max() + 1 : 1;

                var created = 0;
                var skipped = 0;
                var failed = 0;
                var details = new List<object>();
                foreach (var row in rows)
                {
                    var subjectCode = string.IsNullOrWhiteSpace(row.SubjectCode)
                        ? "UNKNOWN_SUBJECT"
                        : row.SubjectCode.Trim();
                    var lpn = string.IsNullOrWhiteSpace(row.Lpn)
                        ? $"LPN-{row.Id}"
                        : row.Lpn.Trim();
                    var sourceUrl = $"knowledge://{Uri.EscapeDataString(qualificationCode)}/{Uri.EscapeDataString(sourceType)}/toolkit/{row.Id}";
                    if (existingSet.Contains(sourceUrl))
                    {
                        skipped++;
                        details.Add(new { rowId = row.Id, status = "skipped", reason = "already_indexed", sourceUrl });
                        continue;
                    }

                    var text = BuildLessonPlanKnowledgeText(row);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        skipped++;
                        details.Add(new { rowId = row.Id, status = "skipped", reason = "empty_lesson_plan_content" });
                        continue;
                    }

                    try
                    {
                        var safeStem = MakeSafeFilePart($"{subjectCode}_{lpn}_row{row.Id}", $"lesson_plan_{row.Id}");
                        var fileName = $"KB-LP-{nextKnowledgeNumber:D4}_{safeStem}.txt";
                        var archivedPath = EnsureUniqueFilePath(Path.Combine(archivePath, fileName));
                        await System.IO.File.WriteAllTextAsync(archivedPath, text, Encoding.UTF8);

                        var title = $"[LessonPlan] {subjectCode} {lpn}".Trim();
                        var material = new SourceMaterial
                        {
                            Title = title,
                            FileName = Path.GetFileName(archivedPath),
                            FilePath = archivedPath,
                            FileType = "txt",
                            Url = sourceUrl,
                            ExtractedText = text,
                            QualificationCode = qualificationCode,
                            QualificationDescription = qualificationDescription,
                            SubjectDescription = subjectCode,
                            TopicDescription = string.IsNullOrWhiteSpace(row.SubjectDescription) ? null : row.SubjectDescription.Trim(),
                            AssessmentCriteriaDescription = string.IsNullOrWhiteSpace(row.AssessmentCriteriaDescription)
                                ? null
                                : row.AssessmentCriteriaDescription.Trim()
                        };

                        ApplyKnowledgeMetadata(
                            material,
                            sourceType,
                            qualificationCode,
                            knowledgeNumber: nextKnowledgeNumber,
                            uploadedAtUtc: DateTime.UtcNow,
                            knowledgeRootPath: sourceRootPath,
                            knowledgeLabel: $"LessonPlanContent::{subjectCode}::{lpn}");

                        _context.SourceMaterials.Add(material);
                        existingSet.Add(sourceUrl);
                        created++;
                        details.Add(new
                        {
                            rowId = row.Id,
                            status = "created",
                            subjectCode,
                            lpn,
                            knowledgeNumber = nextKnowledgeNumber,
                            archivedPath
                        });
                        nextKnowledgeNumber++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        details.Add(new { rowId = row.Id, status = "failed", reason = ex.Message });
                    }
                }

                _context.SaveChanges();

                return (failed == 0, StatusCodes.Status200OK, new
                {
                    qualificationId = qualificationId.Value,
                    qualificationCode,
                    qualificationDescription,
                    sourceType,
                    scanned = rows.Count,
                    created,
                    skipped,
                    failed,
                    knowledgeRootPath = sourceRootPath,
                    details = details.Take(200).ToList()
                }, sourceRootPath);
            }
            catch (Exception ex)
            {
                return (false, StatusCodes.Status500InternalServerError, new
                {
                    qualificationId = qualificationId.Value,
                    sourceType,
                    error = ex.Message
                }, string.Empty);
            }
        }

        private static string BuildLessonPlanKnowledgeText(LecturerToolkitEntry row)
        {
            var lines = new List<string>
            {
                "Qualification Lesson Plan Content",
                $"SubjectCode: {(row.SubjectCode ?? string.Empty).Trim()}",
                $"SubjectDescription: {(row.SubjectDescription ?? string.Empty).Trim()}",
                $"LPN: {(row.Lpn ?? string.Empty).Trim()}",
                $"LessonPlanDescription: {(row.LessonPlanDescription ?? string.Empty).Trim()}",
                $"AssessmentCriteriaDescription: {(row.AssessmentCriteriaDescription ?? string.Empty).Trim()}",
                $"TimeStart: {(row.TimeStart ?? string.Empty).Trim()}",
                $"TimeEnd: {(row.TimeEnd ?? string.Empty).Trim()}",
                $"LecturerActions: {(row.LecturerActions ?? string.Empty).Trim()}",
                $"LearnerActions: {(row.LearnerActions ?? string.Empty).Trim()}",
                $"LearningAids: {(row.LearningAids ?? string.Empty).Trim()}",
                "LessonPlanContent:",
                (row.LessonPlanContent ?? string.Empty).Trim()
            };

            return CleanExtractedText(string.Join("\n", lines));
        }

        [HttpGet("qualification-knowledge-hierarchy")]
        public IActionResult QualificationKnowledgeHierarchy([FromQuery] int? qualificationId = null, [FromQuery] string? qualificationCode = null, [FromQuery] string? qualificationDescription = null)
        {
            var resolvedQualificationCode = (qualificationCode ?? string.Empty).Trim();
            var resolvedQualificationDescription = (qualificationDescription ?? string.Empty).Trim();

            Qualification? qualification = null;
            if (qualificationId.HasValue && qualificationId.Value > 0)
            {
                qualification = _context.Qualifications.Find(qualificationId.Value);
            }
            if (qualification == null && !string.IsNullOrWhiteSpace(resolvedQualificationCode))
            {
                qualification = _context.Qualifications.FirstOrDefault(q => q.QualificationNumber == resolvedQualificationCode);
            }
            if (qualification == null && !string.IsNullOrWhiteSpace(resolvedQualificationDescription))
            {
                qualification = _context.Qualifications.FirstOrDefault(q => q.QualificationDescription == resolvedQualificationDescription);
            }

            if (qualification != null)
            {
                if (string.IsNullOrWhiteSpace(resolvedQualificationCode))
                    resolvedQualificationCode = qualification.QualificationNumber ?? string.Empty;
                if (string.IsNullOrWhiteSpace(resolvedQualificationDescription))
                    resolvedQualificationDescription = qualification.QualificationDescription ?? string.Empty;
            }

            var query = _context.SourceMaterials.AsQueryable();
            if (!string.IsNullOrWhiteSpace(resolvedQualificationCode))
            {
                query = query.Where(s => (s.QualificationCode ?? "") == resolvedQualificationCode);
            }
            else if (!string.IsNullOrWhiteSpace(resolvedQualificationDescription))
            {
                query = query.Where(s => (s.QualificationDescription ?? "") == resolvedQualificationDescription);
            }

            var rows = query
                .Where(s => !string.IsNullOrWhiteSpace(s.KnowledgeSourceType) || !string.IsNullOrWhiteSpace(s.KnowledgeRootPath))
                .OrderByDescending(s => s.KnowledgeUploadedAtUtc ?? s.CreatedAt)
                .Select(s => new
                {
                    s.Id,
                    s.Title,
                    s.FileName,
                    s.FilePath,
                    s.Url,
                    s.QualificationCode,
                    s.QualificationDescription,
                    s.KnowledgeSourceType,
                    s.KnowledgeNumber,
                    s.KnowledgeLabel,
                    s.KnowledgeRootPath,
                    uploadedAtUtc = s.KnowledgeUploadedAtUtc ?? s.CreatedAt
                })
                .ToList();

            var grouped = rows
                .GroupBy(r => string.IsNullOrWhiteSpace(r.KnowledgeSourceType) ? "local_source_upload" : r.KnowledgeSourceType!)
                .OrderBy(g => g.Key)
                .Select(sourceGroup => new
                {
                    sourceType = sourceGroup.Key,
                    knowledgeRootPath = sourceGroup.Select(x => x.KnowledgeRootPath).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                    count = sourceGroup.Count(),
                    knowledgeSets = sourceGroup
                        .GroupBy(x => x.KnowledgeNumber ?? 0)
                        .OrderBy(g => g.Key)
                        .Select(numberGroup => new
                        {
                            knowledgeNumber = numberGroup.Key,
                            entries = numberGroup
                                .OrderByDescending(x => x.uploadedAtUtc)
                                .Select(x => new
                                {
                                    x.Id,
                                    x.Title,
                                    x.FileName,
                                    x.FilePath,
                                    x.Url,
                                    x.KnowledgeLabel,
                                    x.uploadedAtUtc
                                })
                                .ToList()
                        })
                        .ToList()
                })
                .ToList();

            return Ok(new
            {
                qualificationId = qualification?.Id,
                qualificationCode = resolvedQualificationCode,
                qualificationDescription = resolvedQualificationDescription,
                totalMaterials = rows.Count,
                sources = grouped
            });
        }

        private void TryMirrorImportedMaterial(string sourcePath, string fileName, string? qualificationDescription)
        {
            try
            {
                var qual = !string.IsNullOrWhiteSpace(qualificationDescription)
                    ? _context.Qualifications.FirstOrDefault(q => q.QualificationDescription == qualificationDescription)
                    : _context.Qualifications.FirstOrDefault();
                var qualNumber = qual != null && !string.IsNullOrWhiteSpace(qual.QualificationNumber)
                    ? qual.QualificationNumber
                    : (qualificationDescription ?? "Unknown");
                var safeFolder = Regex.Replace(qualNumber, @"[^\w\- ]+", "").Trim().Replace(" ", "_");
                var importBase = GetConfiguredImportBasePath();
                var qualDir = Path.Combine(importBase, safeFolder);
                Directory.CreateDirectory(qualDir);
                var localPath = Path.Combine(qualDir, fileName);
                System.IO.File.Copy(sourcePath, localPath, true);
            }
            catch { }
        }

        private static bool IsSupportedGitHubImportExtension(string ext, bool includeCodeFiles)
        {
            if (string.IsNullOrWhiteSpace(ext)) return false;
            if (GitHubDocumentExtensions.Contains(ext)) return true;
            return includeCodeFiles && GitHubCodeExtensions.Contains(ext);
        }

        private static bool IsGitHubTextExtension(string ext)
        {
            if (string.IsNullOrWhiteSpace(ext)) return false;
            return ext == ".txt" ||
                   ext == ".md" ||
                   ext == ".csv" ||
                   ext == ".html" ||
                   ext == ".htm" ||
                   ext == ".json" ||
                   ext == ".jsonl" ||
                   ext == ".xml" ||
                   ext == ".yml" ||
                   ext == ".yaml" ||
                   GitHubCodeExtensions.Contains(ext);
        }

        private static string BuildGitHubRawFileUrl(string owner, string repo, string branch, string path)
        {
            var encodedPath = string.Join("/", (path ?? "")
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
            return $"https://raw.githubusercontent.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/{Uri.EscapeDataString(branch)}/{encodedPath}";
        }

        private static string BuildGitHubMaterialFileName(string owner, string repo, string repoPath)
        {
            var ext = Path.GetExtension(repoPath);
            var stem = $"{owner}_{repo}_{(repoPath ?? "").Replace('/', '_').Replace('\\', '_')}";
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                stem = stem.Replace(c, '_');
            }

            if (stem.Length > 180)
            {
                var noExt = Path.GetFileNameWithoutExtension(stem);
                noExt = noExt.Substring(Math.Max(0, noExt.Length - 176));
                stem = string.IsNullOrWhiteSpace(ext) ? noExt : $"{noExt}{ext}";
            }
            return stem;
        }

        private static string BuildLocalFolderMaterialUrl(string rootPath, string relativePath)
        {
            var rootTag = MakeSafeFilePart(Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), "local");
            var encodedPath = string.Join("/", (relativePath ?? "")
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
            return $"localpool://{Uri.EscapeDataString(rootTag)}/{encodedPath}";
        }

        private static string BuildLocalFolderMaterialFileName(string poolName, string relativePath)
        {
            var ext = Path.GetExtension(relativePath);
            var stem = $"{poolName}_{(relativePath ?? "").Replace('/', '_').Replace('\\', '_')}";
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                stem = stem.Replace(c, '_');
            }

            if (stem.Length > 180)
            {
                var noExt = Path.GetFileNameWithoutExtension(stem);
                noExt = noExt.Substring(Math.Max(0, noExt.Length - 176));
                stem = string.IsNullOrWhiteSpace(ext) ? noExt : $"{noExt}{ext}";
            }
            return stem;
        }

        private static string BuildOaiPmhRequestUri(
            string baseUrl,
            string metadataPrefix,
            string setSpec,
            string fromUtc,
            string untilUtc,
            string resumptionToken,
            string? apiKeyQueryParam,
            string? apiKey)
        {
            var parameters = new List<KeyValuePair<string, string>>
            {
                new("verb", "ListRecords")
            };

            if (!string.IsNullOrWhiteSpace(resumptionToken))
            {
                parameters.Add(new("resumptionToken", resumptionToken));
            }
            else
            {
                parameters.Add(new("metadataPrefix", metadataPrefix));
                if (!string.IsNullOrWhiteSpace(setSpec)) parameters.Add(new("set", setSpec));
                if (!string.IsNullOrWhiteSpace(fromUtc)) parameters.Add(new("from", fromUtc));
                if (!string.IsNullOrWhiteSpace(untilUtc)) parameters.Add(new("until", untilUtc));
            }

            if (!string.IsNullOrWhiteSpace(apiKeyQueryParam) && !string.IsNullOrWhiteSpace(apiKey))
            {
                parameters.Add(new(apiKeyQueryParam.Trim(), apiKey.Trim()));
            }

            var query = string.Join("&", parameters.Select(p =>
                $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
            var sep = baseUrl.Contains("?", StringComparison.Ordinal) ? "&" : "?";
            return $"{baseUrl.TrimEnd()}{sep}{query}";
        }

        private static string BuildOaiMaterialUrl(Uri baseUri, string identifier, string metadataPrefix)
        {
            return $"oai://{baseUri.Host}/{Uri.EscapeDataString(identifier)}?prefix={Uri.EscapeDataString(metadataPrefix)}";
        }

        private static string BuildOaiMaterialFileName(string host, string identifier, string metadataPrefix)
        {
            var safeHost = Regex.Replace(host ?? "oai", @"[^\w\-\.]+", "_");
            var safePrefix = Regex.Replace(metadataPrefix ?? "oai_dc", @"[^\w\-\.]+", "_");
            var safeId = Regex.Replace(identifier ?? "record", @"[^\w\-\.]+", "_");
            if (safeId.Length > 120) safeId = safeId.Substring(0, 120);
            return $"{safeHost}_{safePrefix}_{safeId}.txt";
        }

        private static Dictionary<string, Dictionary<string, string>> LoadEngineeringSeedLabelMaps(string rootPath)
        {
            var output = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var files = Directory.GetFiles(rootPath, "*_labels.json", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                try
                {
                    var dataset = Path.GetFileNameWithoutExtension(file)
                        .Replace("_labels", "", StringComparison.OrdinalIgnoreCase)
                        .Trim();
                    if (string.IsNullOrWhiteSpace(dataset)) dataset = "Unknown";

                    using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        var key = (prop.Name ?? "").Trim();
                        if (string.IsNullOrWhiteSpace(key)) continue;

                        var value = prop.Value.ValueKind switch
                        {
                            JsonValueKind.String => prop.Value.GetString() ?? "",
                            JsonValueKind.Number => prop.Value.ToString(),
                            JsonValueKind.True => "true",
                            JsonValueKind.False => "false",
                            _ => prop.Value.ToString()
                        };
                        if (string.IsNullOrWhiteSpace(value)) continue;
                        map[key] = value.Trim();
                    }
                    output[dataset] = map;
                }
                catch { }
            }
            return output;
        }

        private static string ResolveEngineeringSeedLabel(
            Dictionary<string, Dictionary<string, string>> labelMaps,
            string dataset,
            string fileName)
        {
            if (labelMaps.TryGetValue(dataset, out var map) && map.TryGetValue(fileName, out var value))
                return value;

            foreach (var m in labelMaps.Values)
            {
                if (m.TryGetValue(fileName, out var v)) return v;
            }

            return "";
        }

        private static string BuildEngineeringSeedMetadataText(
            string rootPath,
            string relPath,
            string fileName,
            string dataset,
            long fileBytes,
            string label)
        {
            var lines = new List<string>
            {
                "Engineering Seed Metadata Index",
                $"Dataset: {dataset}",
                $"RelativePath: {relPath}",
                $"FileName: {fileName}",
                $"RootPath: {rootPath}",
                $"FileSizeBytes: {fileBytes}",
                $"FileSizeMB: {Math.Round(fileBytes / 1024d / 1024d, 3)}",
                $"Label: {(string.IsNullOrWhiteSpace(label) ? "unknown" : label)}",
                "ContentMode: metadata-only (pickle not deserialized)"
            };
            return CleanExtractedText(string.Join("\n", lines));
        }

        private static string BuildEngineeringSeedUrl(string relativePath)
        {
            var normalized = (relativePath ?? "").Replace('\\', '/');
            return "seed://engineering/" + string.Join("/", normalized
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
        }

        private static string BuildEngineeringSeedMetaFileName(string dataset, string fileName)
        {
            var stem = $"{dataset}_{fileName}_meta";
            stem = Regex.Replace(stem, @"[^\w\-\.]+", "_");
            if (!stem.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) stem += ".txt";
            if (stem.Length > 180)
            {
                var ext = Path.GetExtension(stem);
                var name = Path.GetFileNameWithoutExtension(stem);
                name = name.Substring(Math.Max(0, name.Length - 160));
                stem = $"{name}{ext}";
            }
            return stem;
        }

        private static bool TryParseGitHubRepository(string input, out string owner, out string repo, out string branch)
        {
            owner = "";
            repo = "";
            branch = "";
            var raw = (input ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw)) return false;

            if (Regex.IsMatch(raw, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(\.git)?$"))
            {
                var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    owner = parts[0].Trim();
                    repo = parts[1].Trim();
                    if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                        repo = repo.Substring(0, repo.Length - 4);
                    return !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo);
                }
            }

            if (raw.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            {
                raw = "https://github.com/" + raw.Substring("git@github.com:".Length);
            }

            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return false;
            if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                return false;

            var segments = (uri.AbsolutePath ?? "")
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            if (segments.Count < 2) return false;

            owner = segments[0].Trim();
            repo = segments[1].Trim();
            if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                repo = repo.Substring(0, repo.Length - 4);

            if (segments.Count >= 4 && string.Equals(segments[2], "tree", StringComparison.OrdinalIgnoreCase))
            {
                branch = Uri.UnescapeDataString(string.Join("/", segments.Skip(3)));
            }

            return !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo);
        }

        private static async Task<string> ExtractTextFromFileStreamAsync(Stream stream, string ext)
        {
            if (stream.CanSeek) stream.Position = 0;
            if (KnowledgeTextExtensions.Contains(ext))
            {
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true, 1024, true);
                var raw = await reader.ReadToEndAsync();
                return CleanExtractedText(raw);
            }
            if (ext == ".docx")
            {
                return await ExtractTextFromDocxStreamAsync(stream);
            }
            if (ext == ".pptx")
            {
                return await ExtractTextFromPptxStreamAsync(stream);
            }
            if (ext == ".pdf")
            {
                return ExtractTextFromPdfStream(stream);
            }
            return "";
        }

        private static async Task<string> ExtractTextFromDocxStreamAsync(Stream stream)
        {
            if (stream.CanSeek) stream.Position = 0;
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;
            using var doc = WordprocessingDocument.Open(ms, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null) return "";

            var sb = new System.Text.StringBuilder();
            foreach (var para in body.Descendants<Paragraph>())
            {
                var line = string.Join("", para
                    .Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>()
                    .Where(t => !t.Ancestors<FieldCode>().Any() && !t.Ancestors<SimpleField>().Any())
                    .Select(t => t.Text));
                if (!string.IsNullOrWhiteSpace(line))
                {
                    sb.AppendLine(line.Trim());
                }
            }

            if (sb.Length == 0)
            {
                var fallback = string.Join(" ", body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().Select(t => t.Text));
                sb.AppendLine(fallback);
            }

            return CleanExtractedText(sb.ToString());
        }

        private static async Task<string> ExtractTextFromPptxStreamAsync(Stream stream)
        {
            if (stream.CanSeek) stream.Position = 0;
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;

            using var presentation = PresentationDocument.Open(ms, false);
            var presentationPart = presentation.PresentationPart;
            var slideIdList = presentationPart?.Presentation?.SlideIdList;
            if (presentationPart == null || slideIdList == null) return "";

            var sb = new System.Text.StringBuilder();
            foreach (var slideId in slideIdList.Elements<SlideId>())
            {
                var relationshipId = slideId.RelationshipId?.Value;
                if (string.IsNullOrWhiteSpace(relationshipId)) continue;
                if (presentationPart.GetPartById(relationshipId) is not SlidePart slidePart) continue;

                var slideText = slidePart.Slide?
                    .Descendants<A.Text>()
                    .Select(t => t.Text?.Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList() ?? new List<string?>();

                foreach (var line in slideText)
                {
                    sb.AppendLine(line);
                }

                var noteText = slidePart.NotesSlidePart?.NotesSlide?
                    .Descendants<A.Text>()
                    .Select(t => t.Text?.Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList() ?? new List<string?>();

                foreach (var line in noteText)
                {
                    sb.AppendLine(line);
                }

                if (slideText.Count > 0 || noteText.Count > 0)
                {
                    sb.AppendLine();
                }
            }

            return CleanExtractedText(sb.ToString());
        }

        private static List<(int Number, string Text)> ReadPdfPages(string path)
        {
            using var reader = new PdfReader(path);
            using var doc = new PdfDocument(reader);
            var pages = new List<(int Number, string Text)>();
            var totalPages = doc.GetNumberOfPages();
            for (var pageNumber = 1; pageNumber <= totalPages; pageNumber++)
            {
                var text = PdfTextExtractor.GetTextFromPage(doc.GetPage(pageNumber)) ?? string.Empty;
                pages.Add((pageNumber, text));
            }
            return pages;
        }

        private static List<(int Number, string Text)> ReadPdfPages(Stream stream)
        {
            if (stream.CanSeek) stream.Position = 0;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;

            using var reader = new PdfReader(ms);
            using var doc = new PdfDocument(reader);
            var pages = new List<(int Number, string Text)>();
            var totalPages = doc.GetNumberOfPages();
            for (var pageNumber = 1; pageNumber <= totalPages; pageNumber++)
            {
                var text = PdfTextExtractor.GetTextFromPage(doc.GetPage(pageNumber)) ?? string.Empty;
                pages.Add((pageNumber, text));
            }
            return pages;
        }

        private static string ExtractTextFromPdfStream(Stream stream)
        {
            try
            {
                var pdfPages = ReadPdfPages(stream);
                var totalPages = pdfPages.Count;
                var extractedPages = new List<(int Number, List<string> Lines)>();
                foreach (var page in pdfPages)
                {
                    var rawPageText = page.Text;
                    var pageText = DocumentTextCleaner.CleanPdfPageText(rawPageText ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(pageText)) continue;

                    var pageLines = pageText
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => Regex.Replace(x ?? string.Empty, @"\s+", " ").Trim())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Where(x => !DocumentTextCleaner.IsNoiseLine(x))
                        .ToList();

                    if (pageLines.Count == 0) continue;
                    extractedPages.Add((page.Number, pageLines));
                }

                var repeatedBoundaryKeys = DocumentTextCleaner.DetectRepeatedBoundaryLineKeys(
                    extractedPages.Select(x => (IReadOnlyList<string>)x.Lines).ToList());

                var normalizedPages = new List<(int Number, string Text, bool Skip)>();
                foreach (var page in extractedPages)
                {
                    var filteredLines = page.Lines
                        .Where(line => !repeatedBoundaryKeys.Contains(DocumentTextCleaner.NormalizeLineKey(line)))
                        .ToList();
                    if (filteredLines.Count == 0) continue;

                    var normalizedText = DocumentTextCleaner.CleanPdfPageText(string.Join("\n", filteredLines));
                    if (string.IsNullOrWhiteSpace(normalizedText)) continue;

                    var skip = ShouldSkipPdfPageText(page.Number, totalPages, normalizedText);
                    normalizedPages.Add((page.Number, normalizedText, skip));
                }

                var selected = normalizedPages
                    .Where(x => !x.Skip && !string.IsNullOrWhiteSpace(x.Text))
                    .ToList();
                if (selected.Count == 0)
                {
                    selected = normalizedPages
                        .Where(x => !string.IsNullOrWhiteSpace(x.Text))
                        .ToList();
                }

                var sb = new System.Text.StringBuilder();
                foreach (var page in selected)
                {
                    sb.AppendLine($"[Page {page.Number}]");
                    sb.AppendLine(page.Text);
                    sb.AppendLine();
                }
                return CleanExtractedText(sb.ToString());
            }
            catch
            {
                return "";
            }
        }

        private static bool IsImageSidecarFile(string path)
        {
            var fileName = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(fileName)) return false;

            var lower = fileName.ToLowerInvariant();
            if (lower.EndsWith(".caption.md") ||
                lower.EndsWith(".captions.md") ||
                lower.EndsWith(".alt.md") ||
                lower.EndsWith(".caption.txt") ||
                lower.EndsWith(".captions.txt") ||
                lower.EndsWith(".alt.txt"))
            {
                return true;
            }

            var ext = Path.GetExtension(fileName);
            if (!string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var stem = Path.GetFileNameWithoutExtension(fileName);
            var innerExt = Path.GetExtension(stem);
            return KnowledgeImageExtensions.Contains(innerExt);
        }

        private static bool IsManagedKnowledgeArtifactPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var normalized = path.Replace('/', Path.DirectorySeparatorChar).Trim();
            return normalized.IndexOf($"{Path.DirectorySeparatorChar}visual_archive{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<string> FindImageSidecarPaths(string imagePath)
        {
            var results = new List<string>();
            var directory = Path.GetDirectoryName(imagePath);
            if (string.IsNullOrWhiteSpace(directory)) return results;

            var imageFileName = Path.GetFileName(imagePath);
            var stem = Path.GetFileNameWithoutExtension(imagePath);
            var candidates = new[]
            {
                Path.Combine(directory, $"{imageFileName}.md"),
                Path.Combine(directory, $"{imageFileName}.txt"),
                Path.Combine(directory, $"{stem}.caption.md"),
                Path.Combine(directory, $"{stem}.captions.md"),
                Path.Combine(directory, $"{stem}.alt.md"),
                Path.Combine(directory, $"{stem}.caption.txt"),
                Path.Combine(directory, $"{stem}.captions.txt"),
                Path.Combine(directory, $"{stem}.alt.txt"),
            };

            foreach (var candidate in candidates)
            {
                if (System.IO.File.Exists(candidate))
                {
                    results.Add(candidate);
                }
            }

            return results;
        }

        private static string ExtractTextFromImageFile(string imagePath, IEnumerable<string>? sidecarPaths = null)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Indexed visual source: {Path.GetFileName(imagePath)}.");

            var sidecars = (sidecarPaths ?? FindImageSidecarPaths(imagePath))
                .Where(System.IO.File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sidecars.Count == 0)
            {
                sb.AppendLine("No sidecar description text found.");
                sb.AppendLine("Add <image>.caption.md or <image>.caption.txt to make image content searchable.");
                return CleanExtractedText(sb.ToString());
            }

            sb.AppendLine("Sidecar description text:");
            foreach (var sidecar in sidecars.Take(5))
            {
                try
                {
                    var text = System.IO.File.ReadAllText(sidecar);
                    text = CleanExtractedText(text);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    sb.AppendLine($"[Source: {Path.GetFileName(sidecar)}]");
                    sb.AppendLine(text);
                }
                catch
                {
                    // best-effort sidecar read only
                }
            }

            return CleanExtractedText(sb.ToString());
        }

        public class RescanMaterialImagesRequest
        {
            public int? QualificationId { get; set; }
            public string? QualificationCode { get; set; }
            public string? QualificationDescription { get; set; }
            public int LimitDocuments { get; set; } = 200;
            public bool DryRun { get; set; }
        }

        [HttpGet("topic-images")]
        public IActionResult TopicImages([FromQuery] int topicId, [FromQuery] int max = 8)
        {
            if (topicId <= 0) return BadRequest("TopicId is required.");
            var topic = _context.Topics.Find(topicId);
            if (topic == null) return NotFound("Topic not found.");

            var images = ResolveSlideVisualResourcesForTopic(topic, Math.Clamp(max <= 0 ? 8 : max, 1, 8))
                .Select(image => new
                {
                    materialId = image.MaterialId,
                    fileName = image.FileName,
                    caption = image.Caption,
                    score = image.Score,
                    source = image.Source
                })
                .ToList();

            return Ok(new
            {
                topicId,
                scanned = true,
                matchedImageCount = images.Count,
                images
            });
        }

        [HttpPost("rescan-material-images")]
        public IActionResult RescanMaterialImages([FromBody] RescanMaterialImagesRequest? req)
        {
            req ??= new RescanMaterialImagesRequest();
            var resolvedQualificationCode = (req.QualificationCode ?? string.Empty).Trim();
            var resolvedQualificationDescription = (req.QualificationDescription ?? string.Empty).Trim();

            Qualification? qualification = null;
            if (req.QualificationId.HasValue && req.QualificationId.Value > 0)
            {
                qualification = _context.Qualifications.Find(req.QualificationId.Value);
            }
            if (qualification == null && !string.IsNullOrWhiteSpace(resolvedQualificationCode))
            {
                qualification = _context.Qualifications.FirstOrDefault(q => q.QualificationNumber == resolvedQualificationCode);
            }
            if (qualification == null && !string.IsNullOrWhiteSpace(resolvedQualificationDescription))
            {
                qualification = _context.Qualifications.FirstOrDefault(q => q.QualificationDescription == resolvedQualificationDescription);
            }

            if (qualification != null)
            {
                if (string.IsNullOrWhiteSpace(resolvedQualificationCode))
                    resolvedQualificationCode = (qualification.QualificationNumber ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(resolvedQualificationDescription))
                    resolvedQualificationDescription = (qualification.QualificationDescription ?? string.Empty).Trim();
            }

            var query = _context.SourceMaterials.AsQueryable();
            if (!string.IsNullOrWhiteSpace(resolvedQualificationCode))
            {
                query = query.Where(s => (s.QualificationCode ?? string.Empty) == resolvedQualificationCode);
            }
            else if (!string.IsNullOrWhiteSpace(resolvedQualificationDescription))
            {
                query = query.Where(s => (s.QualificationDescription ?? string.Empty) == resolvedQualificationDescription);
            }

            var limitDocuments = Math.Clamp(req.LimitDocuments <= 0 ? 200 : req.LimitDocuments, 1, 1000);
            var sourcePdfs = query
                .Where(s => (s.FileType ?? string.Empty).ToLower() == "pdf" && !string.IsNullOrWhiteSpace(s.FilePath))
                .OrderByDescending(s => s.KnowledgeUploadedAtUtc ?? s.CreatedAt)
                .Take(limitDocuments)
                .ToList()
                .Where(s => System.IO.File.Exists(s.FilePath) && !IsDerivedVisualMaterial(s))
                .ToList();

            var existingVisualKeys = _context.SourceMaterials
                .Where(s => !string.IsNullOrWhiteSpace(s.AssessmentCriteriaDescription))
                .Select(s => s.AssessmentCriteriaDescription ?? string.Empty)
                .ToList()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var existingNumbers = _context.SourceMaterials
                .Where(s => (s.QualificationCode ?? string.Empty) == resolvedQualificationCode && s.KnowledgeNumber.HasValue)
                .Select(s => s.KnowledgeNumber!.Value)
                .ToList();
            var nextKnowledgeNumber = existingNumbers.Count > 0 ? existingNumbers.Max() + 1 : 1;

            var scannedDocuments = 0;
            var createdImages = 0;
            var skippedImages = 0;
            var failedDocuments = 0;
            var details = new List<object>();

            foreach (var sourcePdf in sourcePdfs)
            {
                scannedDocuments++;
                try
                {
                    var sourceType = string.IsNullOrWhiteSpace(sourcePdf.KnowledgeSourceType)
                        ? "local_source_upload"
                        : sourcePdf.KnowledgeSourceType!.Trim();
                    var sourceRootPath = !string.IsNullOrWhiteSpace(sourcePdf.KnowledgeRootPath)
                        ? sourcePdf.KnowledgeRootPath!.Trim()
                        : Path.GetDirectoryName(sourcePdf.FilePath) ?? ".";
                    var originalName = !string.IsNullOrWhiteSpace(sourcePdf.FileName)
                        ? sourcePdf.FileName.Trim()
                        : Path.GetFileName(sourcePdf.FilePath);
                    var parentKnowledgeUrl = sourcePdf.Url ?? string.Empty;

                    var extracted = ExtractDerivedPdfVisualMaterials(
                        sourcePdf.FilePath,
                        originalName,
                        sourceType,
                        resolvedQualificationCode,
                        resolvedQualificationDescription,
                        sourceRootPath,
                        parentKnowledgeUrl,
                        ref nextKnowledgeNumber);

                    var createdForDocument = 0;
                    foreach (var material in extracted.Materials)
                    {
                        var note = material.AssessmentCriteriaDescription ?? string.Empty;
                        if (!existingVisualKeys.Add(note))
                        {
                            skippedImages++;
                            continue;
                        }

                        if (!req.DryRun)
                        {
                            _context.SourceMaterials.Add(material);
                        }
                        createdImages++;
                        createdForDocument++;
                    }

                    details.Add(new
                    {
                        file = originalName,
                        status = createdForDocument > 0 ? (req.DryRun ? "would_create_visuals" : "created_visuals") : "no_new_visuals",
                        createdImages = createdForDocument,
                        extractedImages = extracted.Materials.Count
                    });
                }
                catch (Exception ex)
                {
                    failedDocuments++;
                    details.Add(new { file = sourcePdf.FileName, status = "failed", reason = ex.Message });
                }
            }

            if (!req.DryRun)
            {
                _context.SaveChanges();
            }

            return Ok(new
            {
                qualificationId = qualification?.Id,
                qualificationCode = resolvedQualificationCode,
                qualificationDescription = resolvedQualificationDescription,
                dryRun = req.DryRun,
                scannedDocuments,
                createdImages,
                skippedImages,
                failedDocuments,
                successful = failedDocuments == 0,
                details = details.Take(200).ToList()
            });
        }

        private static bool IsDerivedVisualMaterial(SourceMaterial material)
        {
            var note = material.AssessmentCriteriaDescription ?? string.Empty;
            var path = material.FilePath ?? string.Empty;
            return note.Contains("DerivedFromPath:", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains($"{Path.DirectorySeparatorChar}visual_archive{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
        }

        [HttpGet("materials")]
        public IActionResult Materials(
            [FromQuery] int? qualificationId = null,
            [FromQuery] string? qualificationCode = null,
            [FromQuery] string? qualificationDescription = null)
        {
            var resolvedQualificationCode = (qualificationCode ?? string.Empty).Trim();
            var resolvedQualificationDescription = (qualificationDescription ?? string.Empty).Trim();

            if (qualificationId.HasValue && qualificationId.Value > 0)
            {
                var q = _context.Qualifications.Find(qualificationId.Value);
                if (q == null) return NotFound("Qualification not found.");

                if (string.IsNullOrWhiteSpace(resolvedQualificationCode))
                    resolvedQualificationCode = (q.QualificationNumber ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(resolvedQualificationDescription))
                    resolvedQualificationDescription = (q.QualificationDescription ?? string.Empty).Trim();
            }

            if (string.IsNullOrWhiteSpace(resolvedQualificationCode) && !string.IsNullOrWhiteSpace(resolvedQualificationDescription))
            {
                resolvedQualificationCode = ResolveQualificationCode(resolvedQualificationDescription, qualificationId);
            }

            var query = _context.SourceMaterials.AsQueryable();
            if (!string.IsNullOrWhiteSpace(resolvedQualificationCode))
            {
                query = query.Where(s => (s.QualificationCode ?? string.Empty) == resolvedQualificationCode);
            }
            else if (!string.IsNullOrWhiteSpace(resolvedQualificationDescription))
            {
                query = query.Where(s => (s.QualificationDescription ?? string.Empty) == resolvedQualificationDescription);
            }

            var items = query
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => new
                {
                    s.Id,
                    s.Title,
                    s.FileName,
                    s.FilePath,
                    s.Url,
                    s.FileType,
                    s.QualificationCode,
                    s.QualificationDescription,
                    s.SubjectDescription,
                    s.TopicDescription,
                    s.AssessmentCriteriaDescription,
                    s.KnowledgeSourceType,
                    s.CreatedAt
                })
                .ToList();

            return Ok(items);
        }

        public class UpdateMaterialRequest
        {
            public string? Title { get; set; }
            public string? QualificationDescription { get; set; }
            public string? SubjectDescription { get; set; }
            public string? TopicDescription { get; set; }
            public string? AssessmentCriteriaDescription { get; set; }
        }

        [HttpPut("materials/{id}")]
        public IActionResult UpdateMaterial(int id, [FromBody] UpdateMaterialRequest req)
        {
            var material = _context.SourceMaterials.Find(id);
            if (material == null) return NotFound("Material not found");

            var title = string.IsNullOrWhiteSpace(req?.Title) ? material.Title : req!.Title!.Trim();
            material.Title = title;
            if (req != null)
            {
                if (req.QualificationDescription != null) material.QualificationDescription = req.QualificationDescription.Trim();
                if (req.SubjectDescription != null) material.SubjectDescription = req.SubjectDescription.Trim();
                if (req.TopicDescription != null) material.TopicDescription = req.TopicDescription.Trim();
                if (req.AssessmentCriteriaDescription != null) material.AssessmentCriteriaDescription = req.AssessmentCriteriaDescription.Trim();
            }

            _context.SaveChanges();
            return Ok(new
            {
                updated = true,
                material.Id,
                material.Title,
                material.FileType
            });
        }

        [HttpDelete("materials/{id}")]
        public async Task<IActionResult> DeleteMaterial(int id)
        {
            var s = _context.SourceMaterials.Find(id);
            if (s == null) return NotFound("Material not found");

            var linked = new List<SourceMaterial> { s };
            var processedTitle = string.IsNullOrWhiteSpace(s.Title) ? "" : $"{s.Title} (Processed)";
            if (!string.IsNullOrWhiteSpace(processedTitle))
            {
                var derived = _context.SourceMaterials
                    .Where(x => x.Id != s.Id &&
                                x.Title == processedTitle &&
                                (x.QualificationDescription ?? "") == (s.QualificationDescription ?? ""))
                    .ToList();
                if (derived.Count > 0) linked.AddRange(derived);
            }

            var derivedVisuals = _context.SourceMaterials
                .Where(x => x.Id != s.Id &&
                            ((x.QualificationCode ?? string.Empty) == (s.QualificationCode ?? string.Empty) ||
                             (x.QualificationDescription ?? string.Empty) == (s.QualificationDescription ?? string.Empty)))
                .AsEnumerable()
                .Where(x =>
                    (!string.IsNullOrWhiteSpace(s.Url) &&
                     (x.AssessmentCriteriaDescription ?? string.Empty).Contains($"DerivedFromUrl:{s.Url}", StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(s.FilePath) &&
                     (x.AssessmentCriteriaDescription ?? string.Empty).Contains($"DerivedFromPath:{s.FilePath}", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (derivedVisuals.Count > 0)
            {
                linked.AddRange(derivedVisuals);
            }

            linked = linked.GroupBy(x => x.Id).Select(g => g.First()).ToList();

            var localDeleted = false;
            var localDeletedCount = 0;
            var importsMirrorDeletedCount = 0;
            var blobDeleted = false;
            var blobDeletedCount = 0;
            string blobDeleteMessage = "";

            var qualificationNumber = "";
            var qdesc = (s.QualificationDescription ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(qdesc))
            {
                var q = _context.Qualifications.FirstOrDefault(x => x.QualificationDescription == qdesc);
                qualificationNumber = (q?.QualificationNumber ?? qdesc).Trim();
            }

            foreach (var material in linked)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(material.FilePath) && System.IO.File.Exists(material.FilePath))
                    {
                        System.IO.File.Delete(material.FilePath);
                        localDeleted = true;
                        localDeletedCount++;
                    }
                }
                catch (Exception ex)
                {
                    blobDeleteMessage = $"Local file delete failed: {ex.Message}";
                }

                try
                {
                    if (!string.IsNullOrWhiteSpace(material.FilePath))
                    {
                        foreach (var sidecarPath in FindImageSidecarPaths(material.FilePath).Where(System.IO.File.Exists))
                        {
                            System.IO.File.Delete(sidecarPath);
                        }
                    }
                }
                catch
                {
                    // Sidecar cleanup is best-effort only.
                }

                try
                {
                    if (!string.IsNullOrWhiteSpace(material.Url))
                    {
                        var result = await DeleteBlobViaSasAsync(material.Url);
                        if (result.deleted)
                        {
                            blobDeleted = true;
                            blobDeletedCount++;
                        }
                        if (!string.IsNullOrWhiteSpace(result.message)) blobDeleteMessage = result.message;
                    }
                }
                catch (Exception ex)
                {
                    blobDeleteMessage = $"Blob delete failed: {ex.Message}";
                }

                try
                {
                    if (!string.IsNullOrWhiteSpace(material.FileName) && !string.IsNullOrWhiteSpace(qualificationNumber))
                    {
                        var safeFolder = Regex.Replace(qualificationNumber, @"[^\w\- ]+", "").Trim().Replace(" ", "_");
                        foreach (var baseDir in GetImportBasePathCandidates())
                        {
                            var mirrorPath = Path.Combine(baseDir, safeFolder, material.FileName);
                            if (System.IO.File.Exists(mirrorPath))
                            {
                                System.IO.File.Delete(mirrorPath);
                                importsMirrorDeletedCount++;
                            }
                        }
                    }
                }
                catch { }
            }

            _context.SourceMaterials.RemoveRange(linked);
            _context.SaveChanges();

            return Ok(new
            {
                deleted = true,
                id,
                deletedIds = linked.Select(x => x.Id).ToList(),
                localDeleted,
                localDeletedCount,
                blobDeleted,
                blobDeletedCount,
                importsMirrorDeletedCount,
                blobDeleteMessage
            });
        }

        [HttpGet("materials/{id}/text")]
        public IActionResult MaterialText(int id)
        {
            var s = _context.SourceMaterials.Find(id);
            if (s == null) return NotFound();
            return Ok(new { text = s.ExtractedText, title = s.Title });
        }

        [HttpGet("materials/{id}/pages")]
        public IActionResult MaterialPages(int id)
        {
            var s = _context.SourceMaterials.Find(id);
            if (s == null) return NotFound();
            var pages = new List<object>();
            if (string.Equals(s.FileType, "pdf", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(s.FilePath))
            {
                foreach (var page in ReadPdfPages(s.FilePath))
                {
                    var text = CleanExtractedText(page.Text);
                    pages.Add(new { number = page.Number, text });
                }
            }
            else
            {
                var text = CleanExtractedText(s.ExtractedText ?? "");
                var chunks = text.Split(new[] { "\n\n\n" }, StringSplitOptions.None);
                for (int i = 0; i < chunks.Length; i++)
                {
                    pages.Add(new { number = i + 1, text = chunks[i] });
                }
            }
            return Ok(new { title = s.Title, pages });
        }

        [HttpGet("materials/{id}/export/{format}")]
        public IActionResult ExportMaterial(int id, string format)
        {
            var s = _context.SourceMaterials.Find(id);
            if (s == null) return NotFound();
            var title = string.IsNullOrWhiteSpace(s.Title) ? s.FileName : s.Title;
            var safeTitle = Regex.Replace(title ?? "material", @"[^\w\-]+", "_");
            format = (format ?? "txt").ToLowerInvariant();
            byte[] bytes;
            string contentType;
            string filename;
            if (format == "md")
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"# {title}");
                if (string.Equals(s.FileType, "pdf", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(s.FilePath))
                {
                    foreach (var page in ReadPdfPages(s.FilePath))
                    {
                        var text = CleanExtractedText(page.Text);
                        sb.AppendLine();
                        sb.AppendLine($"## Page {page.Number}");
                        sb.AppendLine();
                        sb.AppendLine(text);
                    }
                }
                else
                {
                    sb.AppendLine();
                    sb.AppendLine(CleanExtractedText(s.ExtractedText ?? ""));
                }
                bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
                contentType = "text/markdown";
                filename = $"{safeTitle}.md";
            }
            else if (format == "csv")
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Page,Text");
                if (string.Equals(s.FileType, "pdf", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(s.FilePath))
                {
                    foreach (var page in ReadPdfPages(s.FilePath))
                    {
                        var text = CleanExtractedText(page.Text);
                        sb.AppendLine($"{page.Number},{CsvEscape(text)}");
                    }
                }
                else
                {
                    var text = CleanExtractedText(s.ExtractedText ?? "");
                    sb.AppendLine($"1,{CsvEscape(text)}");
                }
                bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
                contentType = "text/csv";
                filename = $"{safeTitle}.csv";
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                if (string.Equals(s.FileType, "pdf", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(s.FilePath))
                {
                    foreach (var page in ReadPdfPages(s.FilePath))
                    {
                        var text = CleanExtractedText(page.Text);
                        sb.AppendLine($"=== Page {page.Number} ===");
                        sb.AppendLine(text);
                        sb.AppendLine();
                    }
                }
                else
                {
                    sb.AppendLine(CleanExtractedText(s.ExtractedText ?? ""));
                }
                bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
                contentType = "text/plain";
                filename = $"{safeTitle}.txt";
            }
            return File(bytes, contentType, filename);
        }

        private static string CsvEscape(string s)
        {
            s ??= "";
            var v = s.Replace("\r\n", "\n").Replace("\r", "\n");
            if (v.Contains(",") || v.Contains("\"") || v.Contains("\n"))
            {
                v = "\"" + v.Replace("\"", "\"\"") + "\"";
            }
            return v;
        }
        public class BibliographyRequest
        {
            public int[] MaterialIds { get; set; } = Array.Empty<int>();
        }

        [HttpPost("generate-bibliography")]
        public IActionResult GenerateBibliography([FromBody] BibliographyRequest req)
        {
            var items = _context.SourceMaterials.Where(m => req.MaterialIds.Contains(m.Id)).ToList();
            var lines = new List<string>();
            foreach (var m in items)
            {
                var title = string.IsNullOrWhiteSpace(m.Title) ? m.FileName : m.Title;
                var org = "ETDP Source";
                var year = "n.d.";
                var url = string.IsNullOrWhiteSpace(m.Url) ? "(local file)" : m.Url;
                lines.Add($"{org}. ({year}). {title}. Retrieved from {url}");
            }
            return Ok(new { bibliography = string.Join("\n", lines) });
        }

        public class ExportSlidesRequest
        {
            public int TopicId { get; set; }
        }

        public class ExportSlidesTopicDownloadRequest
        {
            public int TopicId { get; set; }
            public string? TitleOverride { get; set; }
            public int? BulletsPerSlide { get; set; }
            public bool IncludeCoverSlide { get; set; }
            public bool IncludeVisualResourceSlides { get; set; }
            public int? MaxVisualSlides { get; set; }
            public bool IncludeGeneratedImageSlides { get; set; }
            public int? MaxGeneratedImageSlides { get; set; }
            public string? GeneratedImageModel { get; set; }
            public string? GeneratedImageSize { get; set; }
            public string? GeneratedImageStyle { get; set; }
        }

        public class PreviewSlidesTopicResponse
        {
            public int TopicId { get; set; }
            public string TopicCode { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public int BulletsPerSlide { get; set; }
            public bool IncludeCoverSlide { get; set; }
            public int VisualResourcesMatched { get; set; }
            public int LocalVisualResourcesMatched { get; set; }
            public int GeneratedVisualResourcesMatched { get; set; }
            public List<SlideVisualResourceInfo> VisualResources { get; set; } = new();
            public List<string> Warnings { get; set; } = new();
            public List<PreviewSlidesTopicSlide> Slides { get; set; } = new();
        }

        public class SlideVisualResourceInfo
        {
            public int MaterialId { get; set; }
            public string FileName { get; set; } = string.Empty;
            public string Caption { get; set; } = string.Empty;
            public string Source { get; set; } = "local";
        }

        public class PreviewSlidesTopicSlide
        {
            public int Number { get; set; }
            public string Type { get; set; } = "content"; // cover | visual | content
            public string Title { get; set; } = string.Empty;
            public string? Subtitle { get; set; }
            public string? ImageFileName { get; set; }
            public string? ImageSource { get; set; }
            public string? ImageCaption { get; set; }
            public List<string> Bullets { get; set; } = new();
        }

        [HttpPost("export-slides")]
        public IActionResult ExportSlides([FromBody] ExportSlidesRequest req)
        {
            var topic = _context.Topics.Find(req.TopicId);
            if (topic == null) return NotFound("Topic not found");
            var bullets = ResolveSlideBulletsForTopic(topic);
            if (bullets.Count == 0) return BadRequest("No lesson plan content for topic.");
            var exportDir = Path.Combine("C:\\ETDP\\ETDP", "Exports", "Slides");
            Directory.CreateDirectory(exportDir);
            var safeDesc = Regex.Replace(topic.TopicDescription ?? "Topic", @"[^\w\-]+", "_");
            var name = $"{topic.TopicCode}_{safeDesc}_{DateTime.Now:yyyyMMdd}.pptx";
            var path = Path.Combine(exportDir, name);
            var title = (topic.TopicDescription ?? "Topic").Trim();
            CreateSimpleSlideDeck(path, title, bullets);
            return Ok(new { path });
        }

        [HttpPost("export-slides-topic-download")]
        public async Task<IActionResult> ExportSlidesTopicDownload([FromBody] ExportSlidesTopicDownloadRequest req, CancellationToken cancellationToken)
        {
            if (req == null) return BadRequest("Request body is required.");

            var topicId = req.TopicId;
            if (topicId <= 0) return BadRequest("TopicId is required.");

            var topic = _context.Topics.Find(topicId);
            if (topic == null) return NotFound("Topic not found");
            SlideDeckArtifact artifact;
            try
            {
                artifact = await BuildTopicSlideDeckArtifactAsync(topic, req, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
            return File(
                artifact.FileBytes,
                "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                artifact.FileName);
        }

        [HttpPost("export-slides-topic-save")]
        public async Task<IActionResult> ExportSlidesTopicSave([FromBody] ExportSlidesTopicDownloadRequest req, CancellationToken cancellationToken)
        {
            if (req == null) return BadRequest("Request body is required.");

            var topicId = req.TopicId;
            if (topicId <= 0) return BadRequest("TopicId is required.");

            var topic = _context.Topics.Find(topicId);
            if (topic == null) return NotFound("Topic not found");

            var qualification = ResolveQualificationForTopic(topic);
            if (qualification == null)
            {
                return BadRequest("No qualification linked to this topic.");
            }

            SlideDeckArtifact artifact;
            try
            {
                artifact = await BuildTopicSlideDeckArtifactAsync(topic, req, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
            var savedPath = LearningMaterialWorkspacePaths.SaveBytes(
                qualification,
                qualification.Id,
                "SlideShows",
                artifact.FileName,
                artifact.FileBytes);

            return Ok(new
            {
                fileName = Path.GetFileName(savedPath),
                savedPath,
                folderPath = Path.GetDirectoryName(savedPath)
            });
        }

        [HttpPost("preview-slides-topic")]
        public async Task<IActionResult> PreviewSlidesTopic([FromBody] ExportSlidesTopicDownloadRequest req, CancellationToken cancellationToken)
        {
            if (req == null) return BadRequest("Request body is required.");

            var topicId = req.TopicId;
            if (topicId <= 0) return BadRequest("TopicId is required.");

            var topic = _context.Topics.Find(topicId);
            if (topic == null) return NotFound("Topic not found");

            try
            {
                return Ok(await BuildPreviewSlidesTopicResponseAsync(topic, req, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        public class ExportSlidesBatchRequest
        {
            public int[] TopicIds { get; set; } = Array.Empty<int>();
            public int? SubjectId { get; set; }
            public int? QualificationId { get; set; }
            public int? SubjectFromId { get; set; }
            public int? SubjectToId { get; set; }
        }

        [HttpPost("export-slides-batch")]
        public async Task<IActionResult> ExportSlidesBatch([FromBody] ExportSlidesBatchRequest req, CancellationToken cancellationToken)
        {
            var topics = ResolveSlideExportTopics(req, out var error);
            if (!string.IsNullOrEmpty(error)) return BadRequest(error);

            var artifacts = new List<object>();
            foreach (var topic in topics)
            {
                try
                {
                    var artifact = await BuildTopicSlideDeckArtifactAsync(
                        topic,
                        BuildDefaultSlideExportRequest(topic.Id),
                        cancellationToken);
                    artifacts.Add(new { fileName = artifact.FileName });
                }
                catch (InvalidOperationException)
                {
                    // Skip topics without enough slide content.
                }
            }

            if (artifacts.Count == 0) return BadRequest("No slides generated for the selected scope.");
            return Ok(new { files = artifacts });
        }

        [HttpPost("export-slides-batch-download")]
        public async Task<IActionResult> ExportSlidesBatchDownload([FromBody] ExportSlidesBatchRequest req, CancellationToken cancellationToken)
        {
            var topics = ResolveSlideExportTopics(req, out var error);
            if (!string.IsNullOrEmpty(error)) return BadRequest(error);
            if (topics.Count == 0) return BadRequest("No slides generated for the selected scope.");

            var artifacts = new List<SlideDeckArtifact>();
            foreach (var topic in topics)
            {
                try
                {
                    artifacts.Add(await BuildTopicSlideDeckArtifactAsync(
                        topic,
                        BuildDefaultSlideExportRequest(topic.Id),
                        cancellationToken));
                }
                catch (InvalidOperationException)
                {
                    // Skip topics without enough slide content.
                }
            }

            if (artifacts.Count == 0) return BadRequest("No slides generated for the selected scope.");

            if (artifacts.Count == 1)
            {
                var single = artifacts[0];
                return File(single.FileBytes, "application/vnd.openxmlformats-officedocument.presentationml.presentation", single.FileName);
            }

            var zipName = BuildQualificationSlidesArchiveFileName(req, topics);
            return File(CreateSlideArchiveBytes(artifacts), "application/zip", zipName);
        }

        [HttpPost("export-slides-batch-save")]
        public async Task<IActionResult> ExportSlidesBatchSave([FromBody] ExportSlidesBatchRequest req, CancellationToken cancellationToken)
        {
            var topics = ResolveSlideExportTopics(req, out var error);
            if (!string.IsNullOrEmpty(error)) return BadRequest(error);
            if (topics.Count == 0) return BadRequest("No slides generated for the selected scope.");

            var qualification = ResolveQualificationForSlideExport(req, topics);
            if (qualification == null)
            {
                return BadRequest("No qualification linked to this slide export scope.");
            }

            var artifacts = new List<SlideDeckArtifact>();
            foreach (var topic in topics)
            {
                try
                {
                    artifacts.Add(await BuildTopicSlideDeckArtifactAsync(
                        topic,
                        BuildDefaultSlideExportRequest(topic.Id),
                        cancellationToken));
                }
                catch (InvalidOperationException)
                {
                    // Skip topics without enough slide content.
                }
            }

            if (artifacts.Count == 0) return BadRequest("No slides generated for the selected scope.");

            var zipName = BuildQualificationSlidesArchiveFileName(req, topics, qualification);
            var zipBytes = CreateSlideArchiveBytes(artifacts);
            var savedPath = LearningMaterialWorkspacePaths.SaveBytes(
                qualification,
                qualification.Id,
                "SlideShows",
                zipName,
                zipBytes);

            return Ok(new
            {
                fileName = Path.GetFileName(savedPath),
                savedPath,
                folderPath = Path.GetDirectoryName(savedPath),
                generatedCount = artifacts.Count
            });
        }

        [HttpPost("export-slides-by-lpn")]
        public IActionResult ExportSlidesByLpn([FromQuery] int qualificationId)
        {
            if (qualificationId <= 0) return BadRequest("qualificationId is required.");

            var qualification = _context.Qualifications.FirstOrDefault(q => q.Id == qualificationId);
            if (qualification == null) return NotFound("Qualification not found.");

            var entries = _context.LecturerToolkitEntries
                .Where(x => x.QualificationsId == qualificationId)
                .OrderBy(x => x.SubjectCode)
                .ThenBy(x => x.Lpn)
                .ToList();
            if (entries.Count == 0) return BadRequest("No lecturer toolkit entries found for this qualification.");

            var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var qualFolder = MakeSafeFilePart(qualification.QualificationNumber, $"Qualification_{qualificationId}");
            var outputDir = Path.Combine(myDocuments, qualFolder, "LessonPlanSlides");
            Directory.CreateDirectory(outputDir);

            var grouped = entries
                .GroupBy(e => new { SubjectCode = (e.SubjectCode ?? "").Trim(), Lpn = (e.Lpn ?? "").Trim() })
                .ToList();

            var saved = new List<string>();
            var skipped = new List<string>();
            foreach (var grp in grouped)
            {
                if (string.IsNullOrWhiteSpace(grp.Key.SubjectCode) || string.IsNullOrWhiteSpace(grp.Key.Lpn))
                {
                    skipped.Add($"Skipped row group: SubjectCode or LPN missing (SubjectCode='{grp.Key.SubjectCode}', LPN='{grp.Key.Lpn}').");
                    continue;
                }

                var primary = grp.First();
                var content = string.Join(
                    "\n",
                    grp.Select(x => (x.LessonPlanContent ?? "").Trim())
                        .Where(x => x.Length > 0)
                        .Distinct()
                );
                if (string.IsNullOrWhiteSpace(content))
                {
                    content = string.Join(
                        "\n",
                        grp.Select(x => (x.LessonPlanDescription ?? "").Trim())
                            .Where(x => x.Length > 0)
                            .Distinct()
                    );
                }
                if (string.IsNullOrWhiteSpace(content))
                {
                    skipped.Add($"Skipped {grp.Key.SubjectCode}+{grp.Key.Lpn}: no lesson-plan content/description.");
                    continue;
                }

                var bullets = content
                    .Replace("\r\n", "\n")
                    .Split('\n')
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();
                if (bullets.Count == 0)
                {
                    skipped.Add($"Skipped {grp.Key.SubjectCode}+{grp.Key.Lpn}: no bullet lines after parsing.");
                    continue;
                }

                var fileName = $"{MakeSafeFilePart(grp.Key.SubjectCode, "Subject")}+{MakeSafeFilePart(grp.Key.Lpn, "LPN")}.pptx";
                var fullPath = GetUniquePath(outputDir, fileName);
                var topicName = ResolveTopicName(primary);
                var lessonPlanName = string.IsNullOrWhiteSpace(primary.LessonPlanDescription)
                    ? "Lesson Plan"
                    : primary.LessonPlanDescription;
                CreateSlideDeck(
                    fullPath,
                    titleText: $"{primary.SubjectCode} {primary.Lpn} {lessonPlanName}".Trim(),
                    bullets: bullets,
                    coverTopicName: topicName,
                    coverLpn: primary.Lpn,
                    coverLessonPlanName: lessonPlanName,
                    logoPath: qualification.LogoPath
                );
                saved.Add(fullPath);
            }

            var reportPath = Path.Combine(outputDir, $"export_report_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            var report = new System.Text.StringBuilder();
            report.AppendLine("Lesson Plan Slides Export Report (PPTX by LPN)");
            report.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"Qualification: {qualification.QualificationNumber} ({qualification.Id})");
            report.AppendLine($"Folder: {outputDir}");
            report.AppendLine($"Saved count: {saved.Count}");
            report.AppendLine($"Skipped count: {skipped.Count}");
            report.AppendLine();
            report.AppendLine("Saved files:");
            if (saved.Count == 0) report.AppendLine("- None");
            foreach (var path in saved) report.AppendLine($"- {Path.GetFileName(path)}");
            report.AppendLine();
            report.AppendLine("Skipped items:");
            if (skipped.Count == 0) report.AppendLine("- None");
            foreach (var reason in skipped) report.AppendLine($"- {reason}");
            System.IO.File.WriteAllText(reportPath, report.ToString());

            return Ok(new
            {
                qualificationId,
                qualificationNumber = qualification.QualificationNumber,
                folderPath = outputDir,
                savedCount = saved.Count,
                skippedCount = skipped.Count,
                reportPath,
                files = saved
            });
        }

        private sealed class SlideDeckArtifact
        {
            public string FileName { get; set; } = string.Empty;
            public byte[] FileBytes { get; set; } = Array.Empty<byte>();
        }

        private ExportSlidesTopicDownloadRequest BuildDefaultSlideExportRequest(int topicId)
        {
            return new ExportSlidesTopicDownloadRequest
            {
                TopicId = topicId,
                BulletsPerSlide = 8,
                IncludeCoverSlide = true,
                IncludeVisualResourceSlides = false,
                IncludeGeneratedImageSlides = false,
                MaxVisualSlides = 3,
                MaxGeneratedImageSlides = 2
            };
        }

        private Qualification? ResolveQualificationForTopic(Topic topic)
        {
            if (topic == null || topic.SubjectId <= 0) return null;

            var qualificationId = _context.Subjects
                .Where(s => s.Id == topic.SubjectId)
                .Select(s => (int?)s.QualificationId)
                .FirstOrDefault();

            if (!qualificationId.HasValue || qualificationId.Value <= 0) return null;
            return _context.Qualifications.FirstOrDefault(q => q.Id == qualificationId.Value);
        }

        private Qualification? ResolveQualificationForSlideExport(ExportSlidesBatchRequest req, List<Topic> topics)
        {
            if (req.QualificationId.HasValue && req.QualificationId.Value > 0)
            {
                var qualification = _context.Qualifications.FirstOrDefault(q => q.Id == req.QualificationId.Value);
                if (qualification != null) return qualification;
            }

            var firstTopic = topics.FirstOrDefault();
            return firstTopic == null ? null : ResolveQualificationForTopic(firstTopic);
        }

        private List<Topic> ResolveSlideExportTopics(ExportSlidesBatchRequest req, out string? error)
        {
            error = null;
            List<Topic> topics;

            if (req.TopicIds != null && req.TopicIds.Length > 0)
            {
                topics = _context.Topics
                    .Where(t => req.TopicIds.Contains(t.Id))
                    .ToList();
            }
            else if (req.SubjectId.HasValue)
            {
                topics = _context.Topics
                    .Where(t => t.SubjectId == req.SubjectId.Value)
                    .ToList();
            }
            else if ((req.SubjectFromId.HasValue || req.SubjectToId.HasValue) && req.QualificationId.HasValue)
            {
                var subjectRangeIds = ResolveSubjectIdRange(req.QualificationId.Value, req.SubjectFromId, req.SubjectToId);
                topics = _context.Topics
                    .Where(t => subjectRangeIds.Contains(t.SubjectId))
                    .ToList();
            }
            else if (req.QualificationId.HasValue)
            {
                var subjectIds = _context.Subjects
                    .Where(s => s.QualificationId == req.QualificationId.Value)
                    .Select(s => s.Id)
                    .ToList();
                topics = _context.Topics
                    .Where(t => subjectIds.Contains(t.SubjectId))
                    .ToList();
            }
            else
            {
                error = "Provide TopicIds, SubjectId, QualificationId, or a subject range.";
                return new List<Topic>();
            }

            if (topics.Count == 0)
            {
                return topics;
            }

            var topicSubjectIds = topics
                .Select(t => t.SubjectId)
                .Distinct()
                .ToList();
            var subjectMeta = _context.Subjects
                .Where(s => topicSubjectIds.Contains(s.Id))
                .Select(s => new { s.Id, s.SubjectCode, s.SubjectDescription })
                .ToList()
                .ToDictionary(
                    x => x.Id,
                    x => new
                    {
                        Code = (x.SubjectCode ?? string.Empty).Trim(),
                        Description = (x.SubjectDescription ?? string.Empty).Trim()
                    });

            return topics
                .OrderBy(t => subjectMeta.TryGetValue(t.SubjectId, out var subject) ? subject.Code : string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => subjectMeta.TryGetValue(t.SubjectId, out var subject) ? subject.Description : string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => (t.TopicCode ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => (t.TopicDescription ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task<SlideDeckArtifact> BuildTopicSlideDeckArtifactAsync(
            Topic topic,
            ExportSlidesTopicDownloadRequest req,
            CancellationToken cancellationToken)
        {
            var preview = await BuildPreviewSlidesTopicResponseAsync(topic, req, cancellationToken);
            return new SlideDeckArtifact
            {
                FileName = BuildSlideTopicFileName(topic, preview.Title),
                FileBytes = CreatePreviewStyledSlideDeckBytes(preview)
            };
        }

        private async Task<PreviewSlidesTopicResponse> BuildPreviewSlidesTopicResponseAsync(
            Topic topic,
            ExportSlidesTopicDownloadRequest req,
            CancellationToken cancellationToken)
        {
            var bullets = ResolveSlideBulletsForTopic(topic);
            if (bullets.Count == 0)
            {
                throw new InvalidOperationException("No slide bullet lines found for topic.");
            }

            var requestedTitle = (req.TitleOverride ?? string.Empty).Trim();
            var title = !string.IsNullOrWhiteSpace(requestedTitle)
                ? requestedTitle
                : ((topic.TopicDescription ?? "Topic").Trim());
            var topicCode = (topic.TopicCode ?? string.Empty).Trim().ToUpperInvariant();
            var bulletsPerSlide = Math.Clamp(req.BulletsPerSlide.GetValueOrDefault(8), 1, 20);
            var localVisualSlides = req.IncludeVisualResourceSlides
                ? ResolveSlideVisualResourcesForTopic(topic, req.MaxVisualSlides)
                : new List<SlideVisualResource>();
            var generatedVisualResult = req.IncludeGeneratedImageSlides
                ? await ResolveGeneratedSlideVisualResourcesForTopicAsync(topic, bullets, req, cancellationToken)
                : new GeneratedSlideVisualResourcesResult();
            var visualSlides = CombineSlideVisualResources(localVisualSlides, generatedVisualResult.Resources);

            var warnings = new List<string>();
            if (!string.IsNullOrWhiteSpace(generatedVisualResult.Warning))
            {
                warnings.Add(generatedVisualResult.Warning.Trim());
            }

            return BuildSlidePreviewModel(
                topic.Id,
                topicCode,
                title,
                bulletsPerSlide,
                req.IncludeCoverSlide,
                visualSlides,
                warnings,
                localVisualSlides.Count,
                generatedVisualResult.Resources.Count,
                bullets);
        }

        private static PreviewSlidesTopicResponse BuildSlidePreviewModel(
            int topicId,
            string topicCode,
            string title,
            int bulletsPerSlide,
            bool includeCoverSlide,
            List<SlideVisualResource> visualSlides,
            List<string> warnings,
            int localVisualCount,
            int generatedVisualCount,
            List<string> bullets)
        {
            var safeBulletsPerSlide = Math.Clamp(bulletsPerSlide <= 0 ? 8 : bulletsPerSlide, 1, 20);
            var safeTitle = string.IsNullOrWhiteSpace(title) ? "Topic" : title.Trim();
            var safeTopicCode = (topicCode ?? string.Empty).Trim().ToUpperInvariant();

            var slides = new List<PreviewSlidesTopicSlide>();
            var slideNumber = 1;

            if (includeCoverSlide)
            {
                slides.Add(new PreviewSlidesTopicSlide
                {
                    Number = slideNumber++,
                    Type = "cover",
                    Title = safeTitle,
                    Subtitle = safeTopicCode,
                    Bullets = new List<string>()
                });
            }

            foreach (var visual in (visualSlides ?? new List<SlideVisualResource>()).Take(8))
            {
                var visualCaption = string.IsNullOrWhiteSpace(visual.Caption)
                    ? (IsGeneratedSlideVisual(visual) ? "AI-generated visual resource" : "Local visual resource")
                    : visual.Caption.Trim();
                slides.Add(new PreviewSlidesTopicSlide
                {
                    Number = slideNumber++,
                    Type = "visual",
                    Title = safeTitle,
                    Subtitle = safeTopicCode,
                    ImageFileName = visual.FileName,
                    ImageSource = string.IsNullOrWhiteSpace(visual.Source) ? "local" : visual.Source,
                    ImageCaption = visualCaption,
                    Bullets = new List<string> { visualCaption }
                });
            }

            for (var i = 0; i < bullets.Count; i += safeBulletsPerSlide)
            {
                slides.Add(new PreviewSlidesTopicSlide
                {
                    Number = slideNumber++,
                    Type = "content",
                    Title = safeTitle,
                    Subtitle = safeTopicCode,
                    Bullets = bullets.Skip(i).Take(safeBulletsPerSlide).ToList()
                });
            }

            return new PreviewSlidesTopicResponse
            {
                TopicId = topicId,
                TopicCode = safeTopicCode,
                Title = safeTitle,
                BulletsPerSlide = safeBulletsPerSlide,
                IncludeCoverSlide = includeCoverSlide,
                VisualResourcesMatched = visualSlides?.Count ?? 0,
                LocalVisualResourcesMatched = localVisualCount,
                GeneratedVisualResourcesMatched = generatedVisualCount,
                VisualResources = (visualSlides ?? new List<SlideVisualResource>())
                    .Select(v => new SlideVisualResourceInfo
                    {
                        MaterialId = v.MaterialId,
                        FileName = v.FileName,
                        Caption = v.Caption,
                        Source = string.IsNullOrWhiteSpace(v.Source) ? "local" : v.Source
                    })
                    .ToList(),
                Warnings = (warnings ?? new List<string>())
                    .Select(w => (w ?? string.Empty).Trim())
                    .Where(w => w.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Slides = slides
            };
        }

        private static string BuildSlideTopicFileName(Topic topic, string? titleOverride = null)
        {
            var safeCode = MakeSafeFilePart(topic.TopicCode, "Topic");
            var safeDesc = MakeSafeFilePart(titleOverride ?? topic.TopicDescription, "Slides");
            return $"{safeCode}_{safeDesc}_{DateTime.Now:yyyyMMdd_HHmmss}.pptx";
        }

        private string BuildQualificationSlidesArchiveFileName(
            ExportSlidesBatchRequest req,
            List<Topic> topics,
            Qualification? qualification = null)
        {
            qualification ??= ResolveQualificationForSlideExport(req, topics);
            var safeCode = MakeSafeFilePart(qualification?.QualificationNumber, "Qualification");
            var safeTitle = MakeSafeFilePart(qualification?.QualificationDescription, "SlideShows");
            return $"{safeCode}_{safeTitle}_SlideShows_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
        }

        private static byte[] CreateSlideArchiveBytes(List<SlideDeckArtifact> artifacts)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                foreach (var artifact in artifacts.Where(a => a.FileBytes.Length > 0))
                {
                    var entry = zip.CreateEntry(artifact.FileName, CompressionLevel.Fastest);
                    using var entryStream = entry.Open();
                    entryStream.Write(artifact.FileBytes, 0, artifact.FileBytes.Length);
                }
            }

            return ms.ToArray();
        }

        private List<string> GenerateSlidesBatchPaths(ExportSlidesBatchRequest req, out string? error)
        {
            var topics = new List<Topic>();
            error = null;
            if (req.TopicIds != null && req.TopicIds.Length > 0)
            {
                topics = _context.Topics.Where(t => req.TopicIds.Contains(t.Id)).ToList();
            }
            else if (req.SubjectId.HasValue)
            {
                topics = _context.Topics.Where(t => t.SubjectId == req.SubjectId.Value).ToList();
            }
            else if ((req.SubjectFromId.HasValue || req.SubjectToId.HasValue) && req.QualificationId.HasValue)
            {
                var subjectRangeIds = ResolveSubjectIdRange(req.QualificationId.Value, req.SubjectFromId, req.SubjectToId);
                topics = _context.Topics.Where(t => subjectRangeIds.Contains(t.SubjectId)).ToList();
            }
            else if (req.QualificationId.HasValue)
            {
                var subjectIds = _context.Subjects.Where(s => s.QualificationId == req.QualificationId.Value).Select(s => s.Id).ToList();
                topics = _context.Topics.Where(t => subjectIds.Contains(t.SubjectId)).ToList();
            }
            else
            {
                error = "Provide TopicIds, SubjectId, QualificationId, or a subject range.";
                return new List<string>();
            }
            var paths = new List<string>();
            foreach (var topic in topics)
            {
                var bullets = ResolveSlideBulletsForTopic(topic);
                if (bullets.Count == 0) continue;
                var exportDir = Path.Combine("C:\\ETDP\\ETDP", "Exports", "Slides");
                Directory.CreateDirectory(exportDir);
                var safeDesc = Regex.Replace(topic.TopicDescription ?? "Topic", @"[^\w\-]+", "_");
                var name = $"{topic.TopicCode}_{safeDesc}_{DateTime.Now:yyyyMMdd}.pptx";
                var path = Path.Combine(exportDir, name);
                var title = (topic.TopicDescription ?? "Topic").Trim();
                CreateSimpleSlideDeck(path, title, bullets);
                paths.Add(path);
            }
            return paths;
        }

        private List<int> ResolveSubjectIdRange(int qualificationId, int? subjectFromId, int? subjectToId)
        {
            var subjects = _context.Subjects
                .Where(s => s.QualificationId == qualificationId)
                .OrderBy(s => s.SubjectCode)
                .ThenBy(s => s.SubjectDescription)
                .ToList();
            if (subjects.Count == 0) return new List<int>();

            var fromIndex = 0;
            var toIndex = subjects.Count - 1;

            if (subjectFromId.HasValue && subjectFromId.Value > 0)
            {
                fromIndex = subjects.FindIndex(s => s.Id == subjectFromId.Value);
                if (fromIndex < 0) return new List<int>();
            }

            if (subjectToId.HasValue && subjectToId.Value > 0)
            {
                toIndex = subjects.FindIndex(s => s.Id == subjectToId.Value);
                if (toIndex < 0) return new List<int>();
            }

            if (fromIndex > toIndex) (fromIndex, toIndex) = (toIndex, fromIndex);

            return subjects.Skip(fromIndex).Take(toIndex - fromIndex + 1).Select(s => s.Id).ToList();
        }

        private List<string> ResolveSlideBulletsForTopic(Topic topic)
        {
            var bullets = new List<string>();
            if (topic == null) return bullets;

            var criteriaRows = _context.AssessmentCriteria
                .Where(c => c.TopicId == topic.Id)
                .Select(c => new { c.Id, c.Description })
                .ToList();
            var criteriaIds = criteriaRows.Select(c => c.Id).ToList();
            var criteriaDescriptions = criteriaRows
                .Select(c => (c.Description ?? string.Empty).Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var subject = topic.SubjectId > 0
                ? _context.Subjects.FirstOrDefault(s => s.Id == topic.SubjectId)
                : null;
            var subjectCode = (subject?.SubjectCode ?? string.Empty).Trim();
            var qualificationId = subject?.QualificationId ?? 0;

            if (criteriaIds.Count > 0)
            {
                var lessonPlanTexts = _context.LessonPlans
                    .Where(lp => criteriaIds.Contains(lp.AssessmentCriteriaId))
                    .OrderBy(lp => lp.SortOrder)
                    .ThenBy(lp => lp.Id)
                    .Select(lp => lp.Content)
                    .ToList();
                AppendUniqueSlideBullets(bullets, lessonPlanTexts.SelectMany(SplitSlideTextIntoBullets));

                var toolkitRows = _context.LecturerToolkitEntries
                    .Where(e => e.AssessmentCriteriaId.HasValue && criteriaIds.Contains(e.AssessmentCriteriaId.Value))
                    .OrderBy(e => e.Lpn)
                    .ThenBy(e => e.Id)
                    .Select(e => new { e.LessonPlanContent, e.LessonPlanDescription })
                    .ToList();

                AppendUniqueSlideBullets(bullets, toolkitRows.SelectMany(x => SplitSlideTextIntoBullets(x.LessonPlanContent)));
                if (bullets.Count == 0)
                {
                    AppendUniqueSlideBullets(bullets, toolkitRows.SelectMany(x => SplitSlideTextIntoBullets(x.LessonPlanDescription)));
                }
            }

            if (!string.IsNullOrWhiteSpace(subjectCode) && qualificationId > 0 && bullets.Count < 12)
            {
                var subjectToolkitRows = _context.LecturerToolkitEntries
                    .Where(e => e.QualificationsId == qualificationId && e.SubjectCode == subjectCode)
                    .OrderBy(e => e.Lpn)
                    .ThenBy(e => e.Id)
                    .Select(e => new
                    {
                        e.LessonPlanContent,
                        e.LessonPlanDescription,
                        e.AssessmentCriteriaDescription
                    })
                    .ToList();

                var scopedRows = subjectToolkitRows
                    .Where(row =>
                    {
                        if (criteriaDescriptions.Count == 0) return true;
                        var rowCriteria = (row.AssessmentCriteriaDescription ?? string.Empty).Trim();
                        if (rowCriteria.Length == 0) return false;
                        return criteriaDescriptions.Any(cd =>
                            string.Equals(cd, rowCriteria, StringComparison.OrdinalIgnoreCase) ||
                            rowCriteria.Contains(cd, StringComparison.OrdinalIgnoreCase) ||
                            cd.Contains(rowCriteria, StringComparison.OrdinalIgnoreCase));
                    })
                    .ToList();

                if (scopedRows.Count == 0)
                {
                    scopedRows = subjectToolkitRows;
                }

                AppendUniqueSlideBullets(bullets, scopedRows.SelectMany(x => SplitSlideTextIntoBullets(x.LessonPlanContent)));
                if (bullets.Count < 6)
                {
                    AppendUniqueSlideBullets(bullets, scopedRows.SelectMany(x => SplitSlideTextIntoBullets(x.LessonPlanDescription)));
                }
            }

            if (bullets.Count == 0 && topic.SubjectId > 0)
            {
                var evidenceItems = AssessmentDrivenQuestionGenerator.BuildOrderedLessonEvidence(_context, topic.SubjectId)
                    .Where(x => x.TopicId == topic.Id)
                    .OrderBy(x => x.LessonSortOrder)
                    .ThenBy(x => x.LessonPlanLabel)
                    .ToList();

                AppendUniqueSlideBullets(bullets, evidenceItems.SelectMany(x => SplitSlideTextIntoBullets(x.LessonPlanContent)));
                if (bullets.Count == 0)
                {
                    AppendUniqueSlideBullets(bullets, evidenceItems.SelectMany(x => SplitSlideTextIntoBullets(x.EvidenceText)));
                }
                if (bullets.Count == 0)
                {
                    AppendUniqueSlideBullets(bullets, evidenceItems.SelectMany(x => SplitSlideTextIntoBullets(x.LessonPlanDescription)));
                }
            }

            if (bullets.Count == 0)
            {
                AppendUniqueSlideBullets(bullets, SplitSlideTextIntoBullets(topic.TopicPurpose));
                AppendUniqueSlideBullets(bullets, SplitSlideTextIntoBullets(topic.TopicDescription));
            }

            var disallowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var marker in new[] { topic.TopicDescription, topic.TopicPurpose, topic.TopicCode })
            {
                var cleaned = CleanSlideBulletLine(marker ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    disallowed.Add(cleaned);
                }
            }

            var cleanedBullets = bullets
                .Select(CleanSlideBulletLine)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Where(line => !IsOperationalScheduleLine(line))
                .Where(line => !IsTopicTitleEcho(line, disallowed))
                .Take(240)
                .ToList();

            if (cleanedBullets.Count > 0)
            {
                return cleanedBullets;
            }

            var templateFallback = ResolveSlideBulletsFromTemplateLibrary(topic, subjectCode, criteriaDescriptions)
                .Select(CleanSlideBulletLine)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Where(line => !IsOperationalScheduleLine(line))
                .Where(line => !IsTopicTitleEcho(line, disallowed))
                .Take(240)
                .ToList();
            if (templateFallback.Count > 0)
            {
                return templateFallback;
            }

            return bullets.Take(240).ToList();
        }

        private List<string> ResolveSlideBulletsFromTemplateLibrary(
            Topic topic,
            string subjectCode,
            List<string> criteriaDescriptions)
        {
            var path = ResolveSlideFallbackTemplateCsvPath();
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                return new List<string>();
            }

            var delimiter = ';';
            try
            {
                var firstLine = System.IO.File.ReadLines(path).FirstOrDefault() ?? string.Empty;
                delimiter = DetectCsvDelimiter(firstLine);
            }
            catch
            {
                delimiter = ';';
            }

            List<string[]> rows;
            try
            {
                rows = Csv.ReadDelimitedCsv(path, delimiter);
            }
            catch
            {
                try
                {
                    rows = ReadDelimitedCsvWithEncoding(path, delimiter, Encoding.GetEncoding(1252));
                }
                catch
                {
                    return new List<string>();
                }
            }

            if (rows.Count <= 1)
            {
                return new List<string>();
            }

            var header = rows[0] ?? Array.Empty<string>();
            var cSubjectCode = FindCsvColumnIndex(header, "Subject Code", "SubjectCode");
            var cTopicCode = FindCsvColumnIndex(header, "Topic Code", "TopicCode");
            var cTopicDescription = FindCsvColumnIndex(header, "Topic Description", "TopicDescription");
            var cCriteriaDescription = FindCsvColumnIndex(header, "Assesment Criteria Description", "Assessment Criteria Description", "AssessmentCriteriaDescription");
            var cLessonContent = FindCsvColumnIndex(header, "Lesson Plan Content", "LessonPlanContent");
            var cLessonDescription = FindCsvColumnIndex(header, "Lesson Plan Description", "Lesson Plan Description ", "LessonPlanDescription", "Description");

            var wantedTopicCode = (topic.TopicCode ?? string.Empty).Trim();
            var wantedTopicDescription = (topic.TopicDescription ?? string.Empty).Trim();
            var wantedSubjectCode = (subjectCode ?? string.Empty).Trim();
            var wantedCriteria = (criteriaDescriptions ?? new List<string>())
                .Select(x => (x ?? string.Empty).Trim())
                .Where(x => x.Length > 0)
                .ToList();

            var fallbackBullets = new List<string>();

            foreach (var row in rows.Skip(1))
            {
                if (row == null || row.Length == 0) continue;

                var rowTopicCode = CsvCell(row, cTopicCode);
                var rowTopicDescription = CsvCell(row, cTopicDescription);
                var rowSubjectCode = CsvCell(row, cSubjectCode);
                var rowCriteriaDescription = CsvCell(row, cCriteriaDescription);

                var topicMatch = !string.IsNullOrWhiteSpace(wantedTopicCode) &&
                                 string.Equals(rowTopicCode, wantedTopicCode, StringComparison.OrdinalIgnoreCase);
                if (!topicMatch && !string.IsNullOrWhiteSpace(wantedTopicDescription))
                {
                    topicMatch = string.Equals(rowTopicDescription, wantedTopicDescription, StringComparison.OrdinalIgnoreCase);
                }
                if (!topicMatch) continue;

                if (!string.IsNullOrWhiteSpace(wantedSubjectCode) &&
                    !string.IsNullOrWhiteSpace(rowSubjectCode) &&
                    !string.Equals(rowSubjectCode, wantedSubjectCode, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (wantedCriteria.Count > 0 && !string.IsNullOrWhiteSpace(rowCriteriaDescription))
                {
                    var hasCriteriaMatch = wantedCriteria.Any(cd =>
                        string.Equals(cd, rowCriteriaDescription, StringComparison.OrdinalIgnoreCase) ||
                        rowCriteriaDescription.Contains(cd, StringComparison.OrdinalIgnoreCase) ||
                        cd.Contains(rowCriteriaDescription, StringComparison.OrdinalIgnoreCase));
                    if (!hasCriteriaMatch)
                    {
                        continue;
                    }
                }

                var lessonContent = CsvCell(row, cLessonContent);
                if (!string.IsNullOrWhiteSpace(lessonContent))
                {
                    AppendUniqueSlideBullets(fallbackBullets, SplitSlideTextIntoBullets(lessonContent));
                }

                if (fallbackBullets.Count < 6)
                {
                    var lessonDescription = CsvCell(row, cLessonDescription);
                    if (!string.IsNullOrWhiteSpace(lessonDescription))
                    {
                        AppendUniqueSlideBullets(fallbackBullets, SplitSlideTextIntoBullets(lessonDescription));
                    }
                }
            }

            return fallbackBullets;
        }

        private List<SlideVisualResource> ResolveSlideVisualResourcesForTopic(Topic topic, int? requestedMaxVisualSlides)
        {
            var maxVisualSlides = requestedMaxVisualSlides.GetValueOrDefault(3);
            if (maxVisualSlides <= 0) maxVisualSlides = 3;
            maxVisualSlides = Math.Clamp(maxVisualSlides, 1, 8);
            if (topic == null) return new List<SlideVisualResource>();

            var subject = topic.SubjectId > 0
                ? _context.Subjects.FirstOrDefault(s => s.Id == topic.SubjectId)
                : null;
            var qualification = subject != null
                ? _context.Qualifications.FirstOrDefault(q => q.Id == subject.QualificationId)
                : null;

            var qualificationCode = (qualification?.QualificationNumber ?? string.Empty).Trim();
            var qualificationDescription = (qualification?.QualificationDescription ?? string.Empty).Trim();
            var subjectCode = (subject?.SubjectCode ?? string.Empty).Trim();
            var subjectDescription = (subject?.SubjectDescription ?? string.Empty).Trim();
            var topicCode = (topic.TopicCode ?? string.Empty).Trim();
            var topicDescription = (topic.TopicDescription ?? string.Empty).Trim();

            var keywordTokens = BuildSlideVisualKeywordTokens(topicCode, topicDescription, subjectCode, subjectDescription);

            var combined = new List<SlideVisualResource>();
            combined.AddRange(ResolveSlideVisualResourcesFromMaterials(
                qualificationCode,
                qualificationDescription,
                subjectCode,
                subjectDescription,
                topicCode,
                topicDescription,
                keywordTokens));
            combined.AddRange(ResolveSlideVisualResourcesFromAssetFolders(keywordTokens));

            var selected = combined
                .Where(x => !string.IsNullOrWhiteSpace(x.ImagePath) && System.IO.File.Exists(x.ImagePath))
                .GroupBy(x => x.ImagePath.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g
                    .OrderByDescending(x => x.Score)
                    .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                    .First())
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                .Take(maxVisualSlides)
                .ToList();

            return selected;
        }

        private async Task<GeneratedSlideVisualResourcesResult> ResolveGeneratedSlideVisualResourcesForTopicAsync(
            Topic topic,
            List<string> bullets,
            ExportSlidesTopicDownloadRequest request,
            CancellationToken cancellationToken)
        {
            var result = new GeneratedSlideVisualResourcesResult();
            if (topic == null)
            {
                return result;
            }

            if (!AiRuntime.AllowOpenAi())
            {
                result.Warning = "Generated image slides are disabled by AI_MODE.";
                return result;
            }

            var key = (Secrets.GetOpenAIKey() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                result.Warning = "OPENAI_API_KEY is not configured. Generated image slides were skipped.";
                return result;
            }

            var maxSlides = request.MaxGeneratedImageSlides.GetValueOrDefault(2);
            if (maxSlides <= 0) maxSlides = 2;
            maxSlides = Math.Clamp(maxSlides, 1, 4);

            var model = (request.GeneratedImageModel ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(model))
            {
                model = (Environment.GetEnvironmentVariable("OPENAI_IMAGE_MODEL") ?? "gpt-image-1").Trim();
            }

            var size = NormalizeGeneratedSlideImageSize(request.GeneratedImageSize);
            var style = string.IsNullOrWhiteSpace(request.GeneratedImageStyle)
                ? "clean educational illustration for vocational training"
                : request.GeneratedImageStyle.Trim();
            var prompts = BuildGeneratedSlideImagePrompts(topic, bullets, maxSlides, style);
            if (prompts.Count == 0)
            {
                result.Warning = "No meaningful prompt text found for generated image slides.";
                return result;
            }

            var exportDir = Path.Combine("C:\\ETDP\\ETDP", "Exports", "Slides", "GeneratedImages", $"topic_{topic.Id}");
            Directory.CreateDirectory(exportDir);

            string firstError = string.Empty;
            for (var i = 0; i < prompts.Count; i += 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prompt = prompts[i];
                var generated = await TryGenerateSlideImageWithOpenAiAsync(key, model, size, prompt.Prompt, cancellationToken);
                if (generated.Bytes == null || generated.Bytes.Length == 0)
                {
                    if (string.IsNullOrWhiteSpace(firstError) && !string.IsNullOrWhiteSpace(generated.Error))
                    {
                        firstError = generated.Error.Trim();
                    }
                    continue;
                }

                var safeTopicCode = MakeSafeFilePart(topic.TopicCode, $"topic_{topic.Id}");
                var fileName = $"{safeTopicCode}_ai_{i + 1:00}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                var fullPath = GetUniquePath(exportDir, fileName);
                await System.IO.File.WriteAllBytesAsync(fullPath, generated.Bytes, cancellationToken);

                result.Resources.Add(new SlideVisualResource
                {
                    MaterialId = 0,
                    ImagePath = fullPath,
                    FileName = Path.GetFileName(fullPath),
                    Caption = prompt.Caption,
                    Score = 300 - i,
                    Source = "generated"
                });
            }

            if (result.Resources.Count == 0)
            {
                result.Warning = string.IsNullOrWhiteSpace(firstError)
                    ? "OpenAI did not return generated images for this topic."
                    : $"Generated image request failed: {firstError}";
            }

            return result;
        }

        private static List<SlideVisualResource> CombineSlideVisualResources(params IEnumerable<SlideVisualResource>[] groups)
        {
            return (groups ?? Array.Empty<IEnumerable<SlideVisualResource>>())
                .SelectMany(g => g ?? Enumerable.Empty<SlideVisualResource>())
                .Where(v => v != null)
                .Where(v => !string.IsNullOrWhiteSpace(v.ImagePath) && System.IO.File.Exists(v.ImagePath))
                .GroupBy(v => v.ImagePath.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g
                    .OrderByDescending(v => v.Score)
                    .ThenBy(v => v.FileName, StringComparer.OrdinalIgnoreCase)
                    .First())
                .OrderByDescending(v => v.Score)
                .ThenBy(v => v.FileName, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
        }

        private static bool IsGeneratedSlideVisual(SlideVisualResource visual)
        {
            return string.Equals((visual?.Source ?? string.Empty).Trim(), "generated", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeGeneratedSlideImageSize(string? requestedSize)
        {
            var normalized = (requestedSize ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "1024x1024" => "1024x1024",
                "1536x1024" => "1536x1024",
                "1024x1536" => "1024x1536",
                _ => "1024x1024"
            };
        }

        private static List<GeneratedSlidePrompt> BuildGeneratedSlideImagePrompts(
            Topic topic,
            List<string> bullets,
            int maxSlides,
            string style)
        {
            var topicTitle = string.IsNullOrWhiteSpace(topic?.TopicDescription) ? "Vocational training topic" : topic.TopicDescription.Trim();
            var topicCode = string.IsNullOrWhiteSpace(topic?.TopicCode) ? string.Empty : topic.TopicCode.Trim().ToUpperInvariant();
            var candidates = (bullets ?? new List<string>())
                .Select(x => (x ?? string.Empty).Trim())
                .Where(x => x.Length >= 6)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(maxSlides * 3)
                .ToList();

            var prompts = new List<GeneratedSlidePrompt>();
            for (var i = 0; i < maxSlides; i += 1)
            {
                var focus = i < candidates.Count ? candidates[i] : topicTitle;
                var prompt = new StringBuilder();
                prompt.Append("Create a professional educational slide illustration (16:9 composition). ");
                prompt.Append("Use a clean training style suitable for South African vocational learners. ");
                prompt.Append($"Topic: {topicTitle}. ");
                if (!string.IsNullOrWhiteSpace(topicCode))
                {
                    prompt.Append($"Topic code: {topicCode}. ");
                }
                prompt.Append($"Focus concept: {focus}. ");
                prompt.Append($"Style: {style}. ");
                prompt.Append("No logos, no watermarks, no trademarks, no explicit brand references, and no dense paragraphs of text.");

                prompts.Add(new GeneratedSlidePrompt
                {
                    Prompt = prompt.ToString(),
                    Caption = $"AI generated visual: {TruncateGeneratedSlideCaption(focus, 140)}"
                });
            }

            return prompts;
        }

        private async Task<OpenAiGeneratedSlideImageResult> TryGenerateSlideImageWithOpenAiAsync(
            string apiKey,
            string model,
            string size,
            string prompt,
            CancellationToken cancellationToken)
        {
            var payload = new
            {
                model,
                prompt,
                size
            };

            using var msg = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/generations");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            try
            {
                var response = await _http.SendAsync(msg, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return OpenAiGeneratedSlideImageResult.Fail($"HTTP {(int)response.StatusCode}: {TrimSlideWarning(body, 220)}");
                }

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                {
                    return OpenAiGeneratedSlideImageResult.Fail("Image response did not include any data items.");
                }

                var first = data[0];
                if (first.TryGetProperty("b64_json", out var b64Node) && b64Node.ValueKind == JsonValueKind.String)
                {
                    var b64 = (b64Node.GetString() ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(b64))
                    {
                        try
                        {
                            return OpenAiGeneratedSlideImageResult.Success(Convert.FromBase64String(b64));
                        }
                        catch
                        {
                            return OpenAiGeneratedSlideImageResult.Fail("Image base64 payload could not be decoded.");
                        }
                    }
                }

                if (first.TryGetProperty("url", out var urlNode) && urlNode.ValueKind == JsonValueKind.String)
                {
                    var url = (urlNode.GetString() ?? string.Empty).Trim();
                    if (Uri.TryCreate(url, UriKind.Absolute, out var _))
                    {
                        var bytes = await _http.GetByteArrayAsync(url, cancellationToken);
                        return OpenAiGeneratedSlideImageResult.Success(bytes);
                    }
                }

                return OpenAiGeneratedSlideImageResult.Fail("Image response did not include b64_json or url.");
            }
            catch (Exception ex)
            {
                return OpenAiGeneratedSlideImageResult.Fail(ex.Message);
            }
        }

        private static string TrimSlideWarning(string? input, int maxLen)
        {
            var text = Regex.Replace((input ?? string.Empty).Trim(), @"\s+", " ");
            if (text.Length <= maxLen) return text;
            return text.Substring(0, Math.Max(0, maxLen - 3)) + "...";
        }

        private static string TruncateGeneratedSlideCaption(string value, int maxLen)
        {
            var text = Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
            if (text.Length <= maxLen) return text;
            return text.Substring(0, Math.Max(0, maxLen - 3)) + "...";
        }

        private sealed class GeneratedSlidePrompt
        {
            public string Prompt { get; set; } = string.Empty;
            public string Caption { get; set; } = string.Empty;
        }

        private sealed class OpenAiGeneratedSlideImageResult
        {
            public byte[]? Bytes { get; set; }
            public string Error { get; set; } = string.Empty;

            public static OpenAiGeneratedSlideImageResult Success(byte[] bytes) => new()
            {
                Bytes = bytes ?? Array.Empty<byte>(),
                Error = string.Empty
            };

            public static OpenAiGeneratedSlideImageResult Fail(string error) => new()
            {
                Bytes = null,
                Error = error ?? string.Empty
            };
        }

        private sealed class GeneratedSlideVisualResourcesResult
        {
            public List<SlideVisualResource> Resources { get; set; } = new();
            public string Warning { get; set; } = string.Empty;
        }

        private List<SlideVisualResource> ResolveSlideVisualResourcesFromMaterials(
            string qualificationCode,
            string qualificationDescription,
            string subjectCode,
            string subjectDescription,
            string topicCode,
            string topicDescription,
            List<string> keywordTokens)
        {
            var materials = _context.SourceMaterials
                .Where(m => !string.IsNullOrWhiteSpace(m.FilePath))
                .ToList();

            var matches = new List<SlideVisualResource>();

            foreach (var material in materials)
            {
                var filePath = (material.FilePath ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
                {
                    continue;
                }

                var ext = NormalizeSlideImageExtension(material.FileType, filePath);
                if (!SlideVisualImageExtensions.Contains(ext))
                {
                    continue;
                }

                var score = 0;
                var materialQualificationCode = (material.QualificationCode ?? string.Empty).Trim();
                var materialQualificationDescription = (material.QualificationDescription ?? string.Empty).Trim();
                var materialSubjectDescription = (material.SubjectDescription ?? string.Empty).Trim();
                var materialTopicDescription = (material.TopicDescription ?? string.Empty).Trim();

                if (!string.IsNullOrWhiteSpace(qualificationCode))
                {
                    if (string.Equals(materialQualificationCode, qualificationCode, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 35;
                    }
                    else if (!string.IsNullOrWhiteSpace(materialQualificationCode))
                    {
                        continue;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(qualificationDescription) &&
                         !string.IsNullOrWhiteSpace(materialQualificationDescription) &&
                         string.Equals(materialQualificationDescription, qualificationDescription, StringComparison.OrdinalIgnoreCase))
                {
                    score += 18;
                }

                if (!string.IsNullOrWhiteSpace(subjectDescription) && !string.IsNullOrWhiteSpace(materialSubjectDescription))
                {
                    if (string.Equals(materialSubjectDescription, subjectDescription, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 25;
                    }
                    else if (!string.IsNullOrWhiteSpace(subjectCode) &&
                             materialSubjectDescription.Contains(subjectCode, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 12;
                    }
                }

                if (!string.IsNullOrWhiteSpace(topicDescription) && !string.IsNullOrWhiteSpace(materialTopicDescription))
                {
                    if (string.Equals(materialTopicDescription, topicDescription, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 35;
                    }
                    else if (materialTopicDescription.Contains(topicDescription, StringComparison.OrdinalIgnoreCase) ||
                             topicDescription.Contains(materialTopicDescription, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 20;
                    }
                }

                var haystack = string.Join(" ", new[]
                {
                    material.Title,
                    material.FileName,
                    material.TopicDescription,
                    material.SubjectDescription,
                    material.AssessmentCriteriaDescription,
                    material.ExtractedText
                }).ToLowerInvariant();

                if (!string.IsNullOrWhiteSpace(topicCode) && haystack.Contains(topicCode.ToLowerInvariant()))
                {
                    score += 14;
                }
                if (!string.IsNullOrWhiteSpace(subjectCode) && haystack.Contains(subjectCode.ToLowerInvariant()))
                {
                    score += 10;
                }

                var keywordHits = keywordTokens.Count(t => haystack.Contains(t));
                if (keywordHits > 0)
                {
                    score += keywordHits * 7;
                }

                if (score <= 0)
                {
                    continue;
                }

                var fileName = !string.IsNullOrWhiteSpace(material.FileName)
                    ? material.FileName.Trim()
                    : Path.GetFileName(filePath);
                var caption = BuildSlideVisualCaption(material.Title, material.TopicDescription, material.SubjectDescription, fileName);

                matches.Add(new SlideVisualResource
                {
                    MaterialId = material.Id,
                    ImagePath = filePath,
                    FileName = fileName,
                    Caption = caption,
                    Score = score,
                    Source = "local"
                });
            }

            return matches;
        }

        private static List<SlideVisualResource> ResolveSlideVisualResourcesFromAssetFolders(List<string> keywordTokens)
        {
            var folders = GetSlideVisualAssetFolders().ToList();
            var matches = new List<SlideVisualResource>();
            if (folders.Count == 0 || keywordTokens.Count == 0)
            {
                return matches;
            }

            foreach (var folder in folders)
            {
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories);
                }
                catch
                {
                    continue;
                }

                foreach (var filePath in files)
                {
                    var ext = Path.GetExtension(filePath);
                    if (!SlideVisualImageExtensions.Contains(ext))
                    {
                        continue;
                    }

                    var fileName = Path.GetFileName(filePath);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        continue;
                    }

                    var haystack = $"{fileName} {filePath}".ToLowerInvariant();
                    var hits = keywordTokens.Count(t => haystack.Contains(t));
                    if (hits <= 0)
                    {
                        continue;
                    }

                    var captionStem = Path.GetFileNameWithoutExtension(fileName) ?? fileName;
                    var caption = Regex.Replace(captionStem.Replace('_', ' ').Replace('-', ' '), @"\s+", " ").Trim();
                    if (string.IsNullOrWhiteSpace(caption))
                    {
                        caption = fileName;
                    }

                    matches.Add(new SlideVisualResource
                    {
                        MaterialId = 0,
                        ImagePath = filePath,
                        FileName = fileName,
                        Caption = caption,
                        Score = (hits * 8) + 6,
                        Source = "local"
                    });
                }
            }

            return matches;
        }

        private static IEnumerable<string> GetSlideVisualAssetFolders()
        {
            var candidates = new[]
            {
                Path.Combine("Imports", "SlideAssets"),
                Path.Combine("Imports", "SlideResourceImages"),
                Path.Combine("Imports", "LecturerResources"),
                Path.Combine("ETDP", "Imports", "SlideAssets"),
                @"C:\ETDP\ETDP\Imports\SlideAssets"
            };

            var resolved = new List<string>();
            foreach (var candidate in candidates)
            {
                var path = Path.IsPathRooted(candidate)
                    ? candidate
                    : ResolveDirectoryFromCurrentOrParents(candidate, 6);
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    resolved.Add(path);
                }
            }

            return resolved.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static List<string> BuildSlideVisualKeywordTokens(params string?[] values)
        {
            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the", "and", "for", "with", "from", "into", "that", "this", "your", "their",
                "subject", "topic", "lesson", "learning", "knowledge", "introduction", "fundamental",
                "practical", "experience", "module", "trade", "level", "unit"
            };

            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in values ?? Array.Empty<string>())
            {
                var value = (raw ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var normalized = Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", " ").Trim();
                if (normalized.Length == 0)
                {
                    continue;
                }

                if (normalized.Contains(' ') && normalized.Length <= 64)
                {
                    tokens.Add(normalized);
                }

                foreach (var piece in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (piece.Length < 3) continue;
                    if (stopWords.Contains(piece)) continue;
                    tokens.Add(piece);
                }
            }

            return tokens.Take(24).ToList();
        }

        private static string NormalizeSlideImageExtension(string? fileType, string? filePath)
        {
            var extFromType = (fileType ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(extFromType))
            {
                if (!extFromType.StartsWith(".")) extFromType = "." + extFromType;
                return extFromType.ToLowerInvariant();
            }

            var extFromPath = Path.GetExtension(filePath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(extFromPath)) return string.Empty;
            return extFromPath.ToLowerInvariant();
        }

        private static string BuildSlideVisualCaption(
            string? title,
            string? topicDescription,
            string? subjectDescription,
            string? fallbackFileName)
        {
            var resolvedTitle = (title ?? string.Empty).Trim();
            var resolvedTopic = (topicDescription ?? string.Empty).Trim();
            var resolvedSubject = (subjectDescription ?? string.Empty).Trim();
            var fallback = (fallbackFileName ?? "Visual resource").Trim();
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(resolvedTitle))
            {
                parts.Add(resolvedTitle);
            }
            else if (!string.IsNullOrWhiteSpace(fallback))
            {
                parts.Add(Path.GetFileNameWithoutExtension(fallback) ?? fallback);
            }

            if (!string.IsNullOrWhiteSpace(resolvedTopic))
            {
                parts.Add(resolvedTopic);
            }
            else if (!string.IsNullOrWhiteSpace(resolvedSubject))
            {
                parts.Add(resolvedSubject);
            }

            var caption = string.Join(" • ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
            caption = Regex.Replace(caption, @"\s+", " ").Trim();
            return string.IsNullOrWhiteSpace(caption) ? "Local visual resource" : caption;
        }

        private static string? ResolveSlideFallbackTemplateCsvPath()
        {
            var candidates = new[]
            {
                Path.Combine("Imports", "ExcelCSVTemplates", "Lesson PLan.csv"),
                Path.Combine("Imports", "ExcelCSVTemplates", "Lesson Plan.csv"),
                Path.Combine("ETDP", "Imports", "ExcelCSVTemplates", "Lesson PLan.csv"),
                Path.Combine("ETDP", "Imports", "ExcelCSVTemplates", "Lesson Plan.csv"),
                @"E:\ETDP\ETDP\Imports\ExcelCSVTemplates\Lesson PLan.csv",
                @"E:\ETDP\ETDP\Imports\ExcelCSVTemplates\Lesson Plan.csv",
                @"C:\ETDP\ETDP\Imports\ExcelCSVTemplates\Lesson PLan.csv",
                @"C:\ETDP\ETDP\Imports\ExcelCSVTemplates\Lesson Plan.csv"
            };

            foreach (var candidate in candidates)
            {
                var resolved = Path.IsPathRooted(candidate)
                    ? candidate
                    : ResolveFromCurrentOrParents(candidate, 6);
                if (!string.IsNullOrWhiteSpace(resolved) && System.IO.File.Exists(resolved))
                {
                    return resolved;
                }
            }

            return null;
        }

        private static char DetectCsvDelimiter(string? headerLine)
        {
            var line = headerLine ?? string.Empty;
            var semicolon = line.Count(ch => ch == ';');
            var pipe = line.Count(ch => ch == '|');
            var comma = line.Count(ch => ch == ',');
            if (semicolon >= pipe && semicolon >= comma) return ';';
            if (pipe >= semicolon && pipe >= comma) return '|';
            return ',';
        }

        private static int FindCsvColumnIndex(string[] header, params string[] aliases)
        {
            if (header == null || header.Length == 0 || aliases == null || aliases.Length == 0) return -1;
            for (var i = 0; i < header.Length; i++)
            {
                var value = (header[i] ?? string.Empty).Trim();
                if (value.Length == 0) continue;
                foreach (var alias in aliases)
                {
                    if (string.Equals(value, (alias ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
            }
            return -1;
        }

        private static string CsvCell(string[] row, int index)
        {
            if (row == null || index < 0 || index >= row.Length) return string.Empty;
            return (row[index] ?? string.Empty).Trim();
        }

        private static List<string[]> ReadDelimitedCsvWithEncoding(string path, char delimiter, Encoding encoding)
        {
            var rows = new List<string[]>();
            using var reader = new StreamReader(path, encoding);
            var fields = new List<string>();
            var sb = new StringBuilder();
            var inQuotes = false;
            int c;

            while ((c = reader.Read()) != -1)
            {
                var ch = (char)c;
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        var peek = reader.Peek();
                        if (peek == '"')
                        {
                            reader.Read();
                            sb.Append('"');
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                }
                else
                {
                    if (ch == '"')
                    {
                        inQuotes = true;
                    }
                    else if (ch == delimiter)
                    {
                        fields.Add(sb.ToString());
                        sb.Clear();
                    }
                    else if (ch == '\r')
                    {
                        // no-op, consume on \n
                    }
                    else if (ch == '\n')
                    {
                        fields.Add(sb.ToString());
                        sb.Clear();
                        rows.Add(fields.ToArray());
                        fields.Clear();
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                }
            }

            if (sb.Length > 0 || fields.Count > 0)
            {
                fields.Add(sb.ToString());
                rows.Add(fields.ToArray());
            }

            return rows;
        }

        private static IEnumerable<string> SplitSlideTextIntoBullets(string? raw)
        {
            var normalized = (raw ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Trim();
            if (string.IsNullOrWhiteSpace(normalized)) return Enumerable.Empty<string>();

            normalized = Regex.Replace(normalized, @"\u2022", "\n");
            normalized = Regex.Replace(normalized, @"\s*;\s*", ";\n");
            normalized = Regex.Replace(normalized, @"\s+\|\s+", "\n");
            normalized = Regex.Replace(normalized, @"(?<=[.!?])\s+(?=[A-Z0-9])", "\n");
            normalized = Regex.Replace(
                normalized,
                @"(?i)\b(lesson focus|topic purpose|assessment criteria|lecturer actions|learner actions|learning aids|schedule|instruction)\s*:\s*",
                "\n$1: ");
            normalized = Regex.Replace(normalized, @"\n{2,}", "\n");

            var lineParts = normalized
                .Split('\n')
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();

            if (lineParts.Count <= 1)
            {
                lineParts = Regex.Split(normalized, @"(?<=[.!?])\s+")
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0)
                    .ToList();
            }

            if (lineParts.Count == 1 && lineParts[0].Length > 180)
            {
                lineParts = Regex.Split(lineParts[0], @"(?<=[,:])\s+")
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0)
                    .ToList();
            }

            return lineParts
                .Select(CleanSlideBulletLine)
                .Where(x => x.Length > 0)
                .Where(x => !IsOperationalScheduleLine(x))
                .Where(x => !IsReferenceNoiseSlideLine(x));
        }

        private static string CleanSlideBulletLine(string value)
        {
            var cleaned = Regex.Replace(value ?? string.Empty, @"^[\-\*\u2022\d\.\)\(\[\]]+\s*", "");
            cleaned = Regex.Replace(cleaned, @"^\[\d+\]\s*", "");
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
            if (cleaned.Length > 320) cleaned = $"{cleaned.Substring(0, 317).TrimEnd()}...";
            return cleaned;
        }

        private static bool IsOperationalScheduleLine(string line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0) return true;
            return Regex.IsMatch(
                text,
                @"^(lesson activities?\s+for|lecturer actions?|learner actions?|learning aids?|schedule|date|time\s*start|time\s*end)\b",
                RegexOptions.IgnoreCase);
        }

        private static bool IsReferenceNoiseSlideLine(string line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0) return true;
            if (Regex.IsMatch(text, @"^(citations?|bibliography|references|assessment alignment|overview|key concepts)\b", RegexOptions.IgnoreCase))
            {
                return true;
            }

            return Regex.IsMatch(
                text,
                @"[A-Za-z]:\\|\.pdf\b|\.docx\b|\.pptx\b|\.xlsx\b|##\s*Page\b|https?://|\bsource\s+excerpt\b|\bcurriculum\s+content\s+map\b",
                RegexOptions.IgnoreCase);
        }

        private static bool IsTopicTitleEcho(string line, HashSet<string> disallowed)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0) return true;
            if (disallowed == null || disallowed.Count == 0) return false;

            foreach (var marker in disallowed.Where(m => !string.IsNullOrWhiteSpace(m)))
            {
                if (text.Equals(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (text.StartsWith(marker + ":", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (marker.Length >= 14 && text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void AppendUniqueSlideBullets(List<string> target, IEnumerable<string> source)
        {
            var seen = new HashSet<string>(
                target.Select(x => (x ?? string.Empty).Trim()).Where(x => x.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            foreach (var item in source ?? Enumerable.Empty<string>())
            {
                var value = (item ?? string.Empty).Trim();
                if (value.Length == 0) continue;
                if (seen.Add(value))
                {
                    target.Add(value);
                }
            }
        }

        private string ResolveTopicName(LecturerToolkitEntry entry)
        {
            if (entry.AssessmentCriteriaId.HasValue)
            {
                var topic = (from c in _context.AssessmentCriteria
                             join t in _context.Topics on c.TopicId equals t.Id
                             where c.Id == entry.AssessmentCriteriaId.Value
                             select t.TopicDescription).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(topic)) return topic;
            }

            if (!string.IsNullOrWhiteSpace(entry.AssessmentCriteriaDescription))
            {
                return entry.AssessmentCriteriaDescription;
            }
            if (!string.IsNullOrWhiteSpace(entry.SubjectDescription))
            {
                return entry.SubjectDescription;
            }

            return "Topic";
        }

        private sealed class SlideVisualResource
        {
            public int MaterialId { get; set; }
            public string ImagePath { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public string Caption { get; set; } = string.Empty;
            public int Score { get; set; }
            public string Source { get; set; } = "local";
        }

        private sealed class SimpleSlideDeckOptions
        {
            public int BulletsPerSlide { get; set; } = 8;
            public bool IncludeCoverSlide { get; set; }
            public string? CoverSubtitle { get; set; }
            public List<SlideVisualResource> VisualSlides { get; set; } = new();
        }

        private const long PreviewSlideWidthEmu = 12192000L;
        private const long PreviewSlideHeightEmu = 6858000L;
        private const string PreviewDarkBackgroundHex = "17375F";
        private const string PreviewDarkBackgroundAltHex = "0F2645";
        private const string PreviewLightBackgroundHex = "EEF4FB";
        private const string PreviewDarkTextHex = "17375F";
        private const string PreviewLightTextHex = "FFFFFF";
        private const string PreviewAccentOutlineHex = "7A94B3";

        private static void CreateSimpleSlideDeck(string path, string topicTitle, List<string> bullets, SimpleSlideDeckOptions? options = null)
        {
            System.IO.File.WriteAllBytes(path, CreateSimpleSlideDeckBytes(topicTitle, bullets, options));
        }

        private static byte[] CreateSimpleSlideDeckBytes(string topicTitle, List<string> bullets, SimpleSlideDeckOptions? options = null)
        {
            var config = options ?? new SimpleSlideDeckOptions();
            var preview = BuildSlidePreviewModel(
                0,
                config.CoverSubtitle ?? string.Empty,
                topicTitle,
                config.BulletsPerSlide,
                config.IncludeCoverSlide,
                config.VisualSlides ?? new List<SlideVisualResource>(),
                new List<string>(),
                0,
                0,
                bullets ?? new List<string>());

            return CreatePreviewStyledSlideDeckBytes(preview);
        }

        private static byte[] CreatePreviewStyledSlideDeckBytes(PreviewSlidesTopicResponse preview)
        {
            using var stream = new MemoryStream();
            using (var pres = PresentationDocument.Create(stream, DocumentFormat.OpenXml.PresentationDocumentType.Presentation))
            {
                var presentationPart = pres.AddPresentationPart();
                var slideLayoutPart = InitializePresentationScaffold(presentationPart);
                uint slideIndex = 256U;

                foreach (var slide in (preview.Slides ?? new List<PreviewSlidesTopicSlide>()))
                {
                    var slidePart = CreatePreviewSlidePart(presentationPart, slideLayoutPart, slide);
                    var slideId = new SlideId
                    {
                        Id = slideIndex,
                        RelationshipId = presentationPart.GetIdOfPart(slidePart)
                    };
                    presentationPart.Presentation.SlideIdList!.Append(slideId);
                    slideIndex += 1U;
                }

                presentationPart.Presentation.Save();
            }

            return stream.ToArray();
        }

        private static SlidePart CreatePreviewSlidePart(
            PresentationPart presentationPart,
            SlideLayoutPart slideLayoutPart,
            PreviewSlidesTopicSlide slide)
        {
            var slideType = (slide.Type ?? "content").Trim().ToLowerInvariant();
            return slideType switch
            {
                "cover" => AddPreviewCoverSlidePart(presentationPart, slideLayoutPart, slide),
                "visual" => AddPreviewVisualSlidePart(presentationPart, slideLayoutPart, slide),
                _ => AddPreviewContentSlidePart(presentationPart, slideLayoutPart, slide)
            };
        }

        private static SlidePart AddPreviewCoverSlidePart(
            PresentationPart presentationPart,
            SlideLayoutPart slideLayoutPart,
            PreviewSlidesTopicSlide slide)
        {
            var slidePart = CreateBaseSlidePart(presentationPart, slideLayoutPart);
            var tree = slidePart.Slide.CommonSlideData!.ShapeTree!;
            tree.Append(CreateBackgroundShape(2U, PreviewDarkBackgroundHex));
            tree.Append(CreateTextShape(3U, "SlideLabel", 396240L, 182880L, 1524000L, 320040L,
                new[] { CreateParagraph($"Slide {slide.Number}", PreviewLightTextHex, 1600, false) }));
            tree.Append(CreateTextShape(4U, "Title", 396240L, 426720L, 11125200L, 1432560L,
                new[] { CreateParagraph(slide.Title, PreviewLightTextHex, 3400, true) }));

            if (!string.IsNullOrWhiteSpace(slide.Subtitle))
            {
                tree.Append(CreateTextShape(5U, "Subtitle", 396240L, 1524000L, 9753600L, 426720L,
                    new[] { CreateParagraph(slide.Subtitle, PreviewLightTextHex, 1900, false) }));
            }

            tree.Append(CreateTextShape(6U, "CoverNote", 396240L, 5638800L, 3048000L, 396240L,
                new[] { CreateParagraph("Cover slide", PreviewLightTextHex, 1500, false) }));

            slidePart.Slide.Save();
            return slidePart;
        }

        private static SlidePart AddPreviewContentSlidePart(
            PresentationPart presentationPart,
            SlideLayoutPart slideLayoutPart,
            PreviewSlidesTopicSlide slide)
        {
            var slidePart = CreateBaseSlidePart(presentationPart, slideLayoutPart);
            var tree = slidePart.Slide.CommonSlideData!.ShapeTree!;
            tree.Append(CreateBackgroundShape(2U, PreviewDarkBackgroundHex));
            tree.Append(CreateTextShape(3U, "SlideLabel", 396240L, 182880L, 1524000L, 320040L,
                new[] { CreateParagraph($"Slide {slide.Number}", PreviewLightTextHex, 1600, false) }));
            tree.Append(CreateTextShape(4U, "Title", 396240L, 426720L, 11125200L, 1432560L,
                new[] { CreateParagraph(slide.Title, PreviewLightTextHex, 3400, true) }));

            if (!string.IsNullOrWhiteSpace(slide.Subtitle))
            {
                tree.Append(CreateTextShape(5U, "Subtitle", 396240L, 1524000L, 9753600L, 426720L,
                    new[] { CreateParagraph(slide.Subtitle, PreviewLightTextHex, 1900, false) }));
            }

            var bulletParagraphs = (slide.Bullets ?? new List<string>())
                .Select(line => CreateParagraph($"• {line}", PreviewLightTextHex, 2000, false))
                .ToList();
            if (bulletParagraphs.Count == 0)
            {
                bulletParagraphs.Add(CreateParagraph(string.Empty, PreviewLightTextHex, 2000, false));
            }

            tree.Append(CreateTextShape(6U, "Body", 396240L, 2011680L, 11125200L, 4145280L, bulletParagraphs));

            slidePart.Slide.Save();
            return slidePart;
        }

        private static SlidePart AddPreviewVisualSlidePart(
            PresentationPart presentationPart,
            SlideLayoutPart slideLayoutPart,
            PreviewSlidesTopicSlide slide)
        {
            var slidePart = CreateBaseSlidePart(presentationPart, slideLayoutPart);
            var tree = slidePart.Slide.CommonSlideData!.ShapeTree!;
            tree.Append(CreateBackgroundShape(2U, PreviewLightBackgroundHex));
            tree.Append(CreateTextShape(3U, "SlideLabel", 396240L, 182880L, 1524000L, 320040L,
                new[] { CreateParagraph($"Slide {slide.Number}", PreviewDarkTextHex, 1600, false) }));
            tree.Append(CreateTextShape(4U, "Title", 396240L, 426720L, 11125200L, 1432560L,
                new[] { CreateParagraph(slide.Title, PreviewDarkTextHex, 3400, true) }));

            if (!string.IsNullOrWhiteSpace(slide.Subtitle))
            {
                tree.Append(CreateTextShape(5U, "Subtitle", 396240L, 1524000L, 9753600L, 426720L,
                    new[] { CreateParagraph(slide.Subtitle, PreviewDarkTextHex, 1900, false) }));
            }

            var visualLabel = !string.IsNullOrWhiteSpace(slide.ImageFileName)
                ? $"{(string.Equals(slide.ImageSource, "generated", StringComparison.OrdinalIgnoreCase) ? "AI image slide" : "Local image slide")}: {slide.ImageFileName}"
                : "Visual resource";
            tree.Append(CreatePanelShape(6U, "VisualPanel", 777240L, 2103120L, 10698480L, 2743200L, "FFFFFF", PreviewAccentOutlineHex,
                new[] { CreateParagraph(visualLabel, "406389", 1800, false, A.TextAlignmentTypeValues.Center) }));

            var caption = !string.IsNullOrWhiteSpace(slide.ImageCaption)
                ? slide.ImageCaption
                : (slide.Bullets?.FirstOrDefault() ?? "Visual resource");
            tree.Append(CreateTextShape(7U, "VisualCaption", 777240L, 5029200L, 10698480L, 457200L,
                new[] { CreateParagraph(caption, PreviewDarkTextHex, 1700, true) }));

            slidePart.Slide.Save();
            return slidePart;
        }

        private static SlidePart CreateBaseSlidePart(PresentationPart presentationPart, SlideLayoutPart slideLayoutPart)
        {
            var slidePart = presentationPart.AddNewPart<SlidePart>();
            slidePart.AddPart(slideLayoutPart);
            slidePart.Slide = new Slide(new CommonSlideData(new ShapeTree()), new ColorMapOverride(new A.MasterColorMapping()));

            var common = slidePart.Slide.CommonSlideData ??= new CommonSlideData(new ShapeTree());
            var tree = common.ShapeTree ??= new ShapeTree();
            tree.Append(new NonVisualGroupShapeProperties(
                new NonVisualDrawingProperties() { Id = 1U, Name = "Slide" },
                new NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()));
            tree.Append(new GroupShapeProperties(new A.TransformGroup()));
            return slidePart;
        }

        private static Shape CreateBackgroundShape(uint id, string colorHex)
        {
            return new Shape(
                new NonVisualShapeProperties(
                    new NonVisualDrawingProperties() { Id = id, Name = "Background" },
                    new NonVisualShapeDrawingProperties(new A.ShapeLocks() { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties()),
                new ShapeProperties(
                    new A.Transform2D(
                        new A.Offset() { X = 0L, Y = 0L },
                        new A.Extents() { Cx = PreviewSlideWidthEmu, Cy = PreviewSlideHeightEmu }),
                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
                    new A.SolidFill(new A.RgbColorModelHex() { Val = colorHex })),
                new TextBody(
                    new A.BodyProperties(),
                    new A.ListStyle(),
                    new A.Paragraph(new A.Run(new A.Text(string.Empty)))));
        }

        private static Shape CreateTextShape(
            uint id,
            string name,
            long x,
            long y,
            long cx,
            long cy,
            IEnumerable<A.Paragraph> paragraphs)
        {
            var body = new TextBody(new A.BodyProperties(), new A.ListStyle());
            foreach (var paragraph in paragraphs)
            {
                body.Append(paragraph);
            }

            return new Shape(
                new NonVisualShapeProperties(
                    new NonVisualDrawingProperties() { Id = id, Name = name },
                    new NonVisualShapeDrawingProperties(new A.ShapeLocks() { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties()),
                CreateRectangleShapeProperties(x, y, cx, cy),
                body);
        }

        private static Shape CreatePanelShape(
            uint id,
            string name,
            long x,
            long y,
            long cx,
            long cy,
            string fillHex,
            string outlineHex,
            IEnumerable<A.Paragraph> paragraphs)
        {
            var body = new TextBody(new A.BodyProperties(), new A.ListStyle());
            foreach (var paragraph in paragraphs)
            {
                body.Append(paragraph);
            }

            return new Shape(
                new NonVisualShapeProperties(
                    new NonVisualDrawingProperties() { Id = id, Name = name },
                    new NonVisualShapeDrawingProperties(new A.ShapeLocks() { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties()),
                CreateRectangleShapeProperties(
                    x,
                    y,
                    cx,
                    cy,
                    new A.SolidFill(new A.RgbColorModelHex() { Val = fillHex }),
                    new A.Outline(
                        new A.SolidFill(new A.RgbColorModelHex() { Val = outlineHex }))
                    { Width = 19050 }),
                body);
        }

        private static ShapeProperties CreateRectangleShapeProperties(long x, long y, long cx, long cy, params OpenXmlElement[] extraChildren)
        {
            var props = new ShapeProperties(
                new A.Transform2D(
                    new A.Offset() { X = x, Y = y },
                    new A.Extents() { Cx = cx, Cy = cy }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle });

            foreach (var child in extraChildren)
            {
                props.Append(child);
            }

            return props;
        }

        private static A.Paragraph CreateParagraph(
            string text,
            string colorHex,
            int fontSize,
            bool bold,
            A.TextAlignmentTypeValues alignment = A.TextAlignmentTypeValues.Left)
        {
            var runProps = new A.RunProperties() { FontSize = fontSize, Bold = bold };
            runProps.Append(new A.SolidFill(new A.RgbColorModelHex() { Val = colorHex }));
            runProps.Append(new A.LatinFont() { Typeface = "Calibri" });

            return new A.Paragraph(
                new A.ParagraphProperties() { Alignment = alignment },
                new A.Run(runProps, new A.Text(text ?? string.Empty)));
        }

        private static SlidePart AddVisualResourceSlidePart(
            PresentationPart presentationPart,
            SlideLayoutPart slideLayoutPart,
            string pageTitle,
            SlideVisualResource visual)
        {
            var slidePart = presentationPart.AddNewPart<SlidePart>();
            slidePart.AddPart(slideLayoutPart);
            slidePart.Slide = new Slide(new CommonSlideData(new ShapeTree()), new ColorMapOverride(new A.MasterColorMapping()));

            var common = slidePart.Slide.CommonSlideData ??= new CommonSlideData(new ShapeTree());
            var tree = common.ShapeTree ??= new ShapeTree();
            var nvGroupProps = new NonVisualGroupShapeProperties(
                new NonVisualDrawingProperties() { Id = 1U, Name = "Slide" },
                new NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties());
            var groupShapeProps = new GroupShapeProperties(new A.TransformGroup());
            tree.Append(nvGroupProps);
            tree.Append(groupShapeProps);

            var bgShape = new Shape(
                new NonVisualShapeProperties(
                    new NonVisualDrawingProperties() { Id = 2U, Name = "Background" },
                    new NonVisualShapeDrawingProperties(new A.ShapeLocks() { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties()),
                CreateRectangleShapeProperties(
                    0,
                    0,
                    9144000,
                    6858000,
                    new A.SolidFill(new A.RgbColorModelHex() { Val = "F4F8FD" })),
                new TextBody(
                    new A.BodyProperties(),
                    new A.ListStyle(),
                    new A.Paragraph(new A.Run(new A.Text(string.Empty)))));
            tree.Append(bgShape);

            var titleRpr = new A.RunProperties() { Bold = true, FontSize = 1800 };
            titleRpr.Append(new A.SolidFill(new A.RgbColorModelHex() { Val = "133660" }));
            titleRpr.Append(new A.LatinFont() { Typeface = "Calibri" });
            var titleParagraph = new A.Paragraph(
                new A.ParagraphProperties() { Alignment = A.TextAlignmentTypeValues.Center },
                new A.Run(titleRpr, new A.Text(pageTitle)));
            var titleBody = new TextBody(new A.BodyProperties(), new A.ListStyle(), titleParagraph);
            var titleShape = new Shape(
                new NonVisualShapeProperties(
                    new NonVisualDrawingProperties() { Id = 3U, Name = "VisualTitle" },
                    new NonVisualShapeDrawingProperties(new A.ShapeLocks() { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties()),
                CreateRectangleShapeProperties(457200, 228600, 8229600, 609600),
                titleBody);
            tree.Append(titleShape);

            var imagePartType = TryResolveImagePartType(visual.ImagePath);
            if (imagePartType.HasValue && System.IO.File.Exists(visual.ImagePath))
            {
                var imagePart = slidePart.AddImagePart(imagePartType.Value);
                using (var stream = new FileStream(visual.ImagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    imagePart.FeedData(stream);
                }

                var relId = slidePart.GetIdOfPart(imagePart);
                var picture = new DocumentFormat.OpenXml.Presentation.Picture(
                    new NonVisualPictureProperties(
                        new NonVisualDrawingProperties() { Id = 4U, Name = $"Visual_{visual.FileName}" },
                        new NonVisualPictureDrawingProperties(new A.PictureLocks() { NoChangeAspect = true }),
                        new ApplicationNonVisualDrawingProperties()),
                    new BlipFill(
                        new A.Blip() { Embed = relId, CompressionState = A.BlipCompressionValues.Print },
                        new A.Stretch(new A.FillRectangle())),
                    new ShapeProperties(
                        new A.Transform2D(
                            new A.Offset() { X = 914400, Y = 960120 },
                            new A.Extents() { Cx = 7315200, Cy = 4297680 }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })
                );
                tree.Append(picture);
            }
            else
            {
                var placeholderRpr = new A.RunProperties() { FontSize = 1400 };
                placeholderRpr.Append(new A.SolidFill(new A.RgbColorModelHex() { Val = "566D86" }));
                placeholderRpr.Append(new A.LatinFont() { Typeface = "Calibri" });
                var placeholderParagraph = new A.Paragraph(
                    new A.ParagraphProperties() { Alignment = A.TextAlignmentTypeValues.Center },
                    new A.Run(placeholderRpr, new A.Text("Visual resource file unavailable.")));
                var placeholderBody = new TextBody(new A.BodyProperties(), new A.ListStyle(), placeholderParagraph);
                var placeholderShape = new Shape(
                    new NonVisualShapeProperties(
                        new NonVisualDrawingProperties() { Id = 4U, Name = "VisualPlaceholder" },
                        new NonVisualShapeDrawingProperties(new A.ShapeLocks() { NoGrouping = true }),
                        new ApplicationNonVisualDrawingProperties()),
                    CreateRectangleShapeProperties(914400, 960120, 7315200, 4297680),
                    placeholderBody);
                tree.Append(placeholderShape);
            }

            var caption = string.IsNullOrWhiteSpace(visual.Caption)
                ? (IsGeneratedSlideVisual(visual) ? "AI-generated visual resource" : "Local visual resource")
                : visual.Caption.Trim();
            var captionRpr = new A.RunProperties() { FontSize = 1300 };
            captionRpr.Append(new A.SolidFill(new A.RgbColorModelHex() { Val = "1F3550" }));
            captionRpr.Append(new A.LatinFont() { Typeface = "Calibri" });
            var captionParagraph = new A.Paragraph(
                new A.ParagraphProperties() { Alignment = A.TextAlignmentTypeValues.Center },
                new A.Run(captionRpr, new A.Text(caption)));
            var captionBody = new TextBody(new A.BodyProperties(), new A.ListStyle(), captionParagraph);
            var captionShape = new Shape(
                new NonVisualShapeProperties(
                    new NonVisualDrawingProperties() { Id = 5U, Name = "VisualCaption" },
                    new NonVisualShapeDrawingProperties(new A.ShapeLocks() { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties()),
                CreateRectangleShapeProperties(685800, 5448300, 7772400, 838200),
                captionBody);
            tree.Append(captionShape);

            slidePart.Slide.Save();
            return slidePart;
        }

        private static ImagePartType? TryResolveImagePartType(string? imagePath)
        {
            var ext = Path.GetExtension(imagePath ?? string.Empty).ToLowerInvariant();
            return ext switch
            {
                ".png" => ImagePartType.Png,
                ".jpg" => ImagePartType.Jpeg,
                ".jpeg" => ImagePartType.Jpeg,
                ".gif" => ImagePartType.Gif,
                ".bmp" => ImagePartType.Bmp,
                ".tif" => ImagePartType.Tiff,
                ".tiff" => ImagePartType.Tiff,
                _ => null
            };
        }

        private static SlidePart AddSimpleCoverSlidePart(
            PresentationPart presentationPart,
            SlideLayoutPart slideLayoutPart,
            string titleText,
            string? subtitleText)
        {
            var slidePart = presentationPart.AddNewPart<SlidePart>();
            slidePart.AddPart(slideLayoutPart);
            slidePart.Slide = new Slide(new CommonSlideData(new ShapeTree()), new ColorMapOverride(new A.MasterColorMapping()));

            var common = slidePart.Slide.CommonSlideData ??= new CommonSlideData(new ShapeTree());
            var tree = common.ShapeTree ??= new ShapeTree();
            var nvGroupProps = new NonVisualGroupShapeProperties(
                new NonVisualDrawingProperties() { Id = 1U, Name = "Slide" },
                new NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties());
            var groupShapeProps = new GroupShapeProperties(new A.TransformGroup());
            tree.Append(nvGroupProps);
            tree.Append(groupShapeProps);

            var bgShape = new Shape(
                new NonVisualShapeProperties(
                    new NonVisualDrawingProperties() { Id = 2U, Name = "Background" },
                    new NonVisualShapeDrawingProperties(new A.ShapeLocks() { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties()),
                CreateRectangleShapeProperties(
                    0,
                    0,
                    9144000,
                    6858000,
                    new A.SolidFill(new A.RgbColorModelHex() { Val = "0B2447" })),
                new TextBody(
                    new A.BodyProperties(),
                    new A.ListStyle(),
                    new A.Paragraph(new A.Run(new A.Text(string.Empty)))));
            tree.Append(bgShape);

            var resolvedTitle = string.IsNullOrWhiteSpace(titleText) ? "TOPIC" : titleText.Trim().ToUpperInvariant();
            var titleRPr = new A.RunProperties() { Bold = true, FontSize = 3000 };
            titleRPr.Append(new A.SolidFill(new A.RgbColorModelHex() { Val = "FFFFFF" }));
            titleRPr.Append(new A.LatinFont() { Typeface = "Calibri" });
            var titleParagraph = new A.Paragraph(
                new A.ParagraphProperties() { Alignment = A.TextAlignmentTypeValues.Center },
                new A.Run(titleRPr, new A.Text(resolvedTitle)));
            var titleTextBody = new TextBody(new A.BodyProperties(), new A.ListStyle(), titleParagraph);
            var titleShape = new Shape(
                new NonVisualShapeProperties(
                    new NonVisualDrawingProperties() { Id = 3U, Name = "CoverTitle" },
                    new NonVisualShapeDrawingProperties(new A.ShapeLocks() { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties()),
                CreateRectangleShapeProperties(914400, 2560320, 7315200, 914400),
                titleTextBody);
            tree.Append(titleShape);

            var subtitle = (subtitleText ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                var subtitleRPr = new A.RunProperties() { FontSize = 1400 };
                subtitleRPr.Append(new A.SolidFill(new A.RgbColorModelHex() { Val = "FFFFFF" }));
                subtitleRPr.Append(new A.LatinFont() { Typeface = "Calibri" });
                var subtitleParagraph = new A.Paragraph(
                    new A.ParagraphProperties() { Alignment = A.TextAlignmentTypeValues.Center },
                    new A.Run(subtitleRPr, new A.Text(subtitle)));
                var subtitleBody = new TextBody(new A.BodyProperties(), new A.ListStyle(), subtitleParagraph);
                var subtitleShape = new Shape(
                    new NonVisualShapeProperties(
                        new NonVisualDrawingProperties() { Id = 4U, Name = "CoverSubtitle" },
                        new NonVisualShapeDrawingProperties(new A.ShapeLocks() { NoGrouping = true }),
                        new ApplicationNonVisualDrawingProperties()),
                    CreateRectangleShapeProperties(914400, 3535680, 7315200, 533400),
                    subtitleBody);
                tree.Append(subtitleShape);
            }

            slidePart.Slide.Save();
            return slidePart;
        }

        private static void CreateSlideDeck(
            string path,
            string titleText,
            List<string> bullets,
            string coverTopicName,
            string coverLpn,
            string coverLessonPlanName,
            string? logoPath)
        {
            using var pres = PresentationDocument.Create(path, DocumentFormat.OpenXml.PresentationDocumentType.Presentation);
            var presentationPart = pres.AddPresentationPart();
            var slideLayoutPart = InitializePresentationScaffold(presentationPart);
            var slideIndex = 256U;

            var coverSlidePart = presentationPart.AddNewPart<SlidePart>();
            coverSlidePart.AddPart(slideLayoutPart);
            coverSlidePart.Slide = new Slide(new CommonSlideData(new ShapeTree()), new ColorMapOverride(new A.MasterColorMapping()));
            var coverCommon = coverSlidePart.Slide.CommonSlideData ??= new CommonSlideData(new ShapeTree());
            var coverTree = coverCommon.ShapeTree ??= new ShapeTree();
            var coverNvGroupProps = new NonVisualGroupShapeProperties(
                new NonVisualDrawingProperties() { Id = 1U, Name = "Slide" },
                new NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties());
            var coverGroupShapeProps = new GroupShapeProperties(new A.TransformGroup());
            coverTree.Append(coverNvGroupProps);
            coverTree.Append(coverGroupShapeProps);

            var coverBg = new Shape(
                new NonVisualShapeProperties(
                    new NonVisualDrawingProperties() { Id = 2U, Name = "Background" },
                    new NonVisualShapeDrawingProperties(new A.ShapeLocks() { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties()),
                CreateRectangleShapeProperties(
                    0,
                    0,
                    9144000,
                    6858000,
                    new A.SolidFill(new A.RgbColorModelHex() { Val = "0B2447" })),
                new TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(new A.Run(new A.Text("")))));
            coverTree.Append(coverBg);

            if (!string.IsNullOrWhiteSpace(logoPath) && System.IO.File.Exists(logoPath))
            {
                var ext = Path.GetExtension(logoPath).ToLowerInvariant();
                var imagePartType = ext switch
                {
                    ".png" => ImagePartType.Png,
                    ".jpg" => ImagePartType.Jpeg,
                    ".jpeg" => ImagePartType.Jpeg,
                    ".gif" => ImagePartType.Gif,
                    ".bmp" => ImagePartType.Bmp,
                    _ => ImagePartType.Png
                };

                var imagePart = coverSlidePart.AddImagePart(imagePartType);
                using (var stream = new FileStream(logoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    imagePart.FeedData(stream);
                }

                var relId = coverSlidePart.GetIdOfPart(imagePart);
                var logoCx = 1524000L;
                var logoCy = 1524000L;
                var logoX = (9144000L - logoCx) / 2;
                var logoY = 457200L;

                var picture = new DocumentFormat.OpenXml.Presentation.Picture(
                    new NonVisualPictureProperties(
                        new NonVisualDrawingProperties() { Id = 3U, Name = "Qualification Logo" },
                        new NonVisualPictureDrawingProperties(new A.PictureLocks() { NoChangeAspect = true }),
                        new ApplicationNonVisualDrawingProperties()),
                    new BlipFill(
                        new A.Blip() { Embed = relId, CompressionState = A.BlipCompressionValues.Print },
                        new A.Stretch(new A.FillRectangle())),
                    new ShapeProperties(
                        new A.Transform2D(
                            new A.Offset() { X = logoX, Y = logoY },
                            new A.Extents() { Cx = logoCx, Cy = logoCy }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })
                );
                coverTree.Append(picture);
            }

            var topicRPr = new A.RunProperties() { Bold = true, FontSize = 2200 };
            topicRPr.Append(new A.SolidFill(new A.RgbColorModelHex() { Val = "FFFFFF" }));
            topicRPr.Append(new A.LatinFont() { Typeface = "Calibri" });
            var topicParagraph = new A.Paragraph(
                new A.ParagraphProperties() { Alignment = A.TextAlignmentTypeValues.Center },
                new A.Run(topicRPr, new A.Text((coverTopicName ?? "Topic").ToUpperInvariant())));
            var topicTextBody = new TextBody(new A.BodyProperties(), new A.ListStyle(), topicParagraph);
            var topicShape = new Shape(
                new NonVisualShapeProperties(
                    new NonVisualDrawingProperties() { Id = 4U, Name = "CoverTopic" },
                    new NonVisualShapeDrawingProperties(new A.ShapeLocks() { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties()),
                CreateRectangleShapeProperties(914400, 2438400, 7315200, 914400),
                topicTextBody);
            coverTree.Append(topicShape);

            var lpnLine = $"{coverLpn} + {coverLessonPlanName}".Trim(' ', '+');
            var subtitleRPr = new A.RunProperties() { FontSize = 1400 };
            subtitleRPr.Append(new A.SolidFill(new A.RgbColorModelHex() { Val = "FFFFFF" }));
            subtitleRPr.Append(new A.LatinFont() { Typeface = "Calibri" });
            var subtitleParagraph = new A.Paragraph(
                new A.ParagraphProperties() { Alignment = A.TextAlignmentTypeValues.Center },
                new A.Run(subtitleRPr, new A.Text(lpnLine)));
            var subtitleTextBody = new TextBody(new A.BodyProperties(), new A.ListStyle(), subtitleParagraph);
            var subtitleShape = new Shape(
                new NonVisualShapeProperties(
                    new NonVisualDrawingProperties() { Id = 5U, Name = "CoverLpnLesson" },
                    new NonVisualShapeDrawingProperties(new A.ShapeLocks() { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties()),
                CreateRectangleShapeProperties(914400, 3352800, 7315200, 685800),
                subtitleTextBody);
            coverTree.Append(subtitleShape);

            coverSlidePart.Slide.Save();
            var coverSlideId = new SlideId() { Id = slideIndex, RelationshipId = presentationPart.GetIdOfPart(coverSlidePart) };
            presentationPart.Presentation.SlideIdList!.Append(coverSlideId);
            slideIndex += 1U;

            var pageTitle = (titleText ?? "Lesson Plan").ToUpperInvariant();
            for (int i = 0; i < bullets.Count; i += 8)
            {
                var slice = bullets.Skip(i).Take(8).ToList();
                var slidePart = AddContentSlidePart(presentationPart, slideLayoutPart, pageTitle, slice);
                var slideId = new SlideId() { Id = slideIndex, RelationshipId = presentationPart.GetIdOfPart(slidePart) };
                presentationPart.Presentation.SlideIdList!.Append(slideId);
                slideIndex += 1U;
            }

            presentationPart.Presentation.Save();
        }

        private static SlideLayoutPart InitializePresentationScaffold(PresentationPart presentationPart)
        {
            var slideMasterPart = presentationPart.AddNewPart<SlideMasterPart>();
            var slideLayoutPart = slideMasterPart.AddNewPart<SlideLayoutPart>();

            slideLayoutPart.SlideLayout = new SlideLayout(
                new CommonSlideData(
                    new ShapeTree(
                        new NonVisualGroupShapeProperties(
                            new NonVisualDrawingProperties() { Id = 1U, Name = "" },
                            new NonVisualGroupShapeDrawingProperties(),
                            new ApplicationNonVisualDrawingProperties()),
                        new GroupShapeProperties(new A.TransformGroup()))),
                new ColorMapOverride(new A.MasterColorMapping()))
            { Type = SlideLayoutValues.Blank, Preserve = true };
            slideLayoutPart.SlideLayout.Save();

            var layoutRelId = slideMasterPart.GetIdOfPart(slideLayoutPart);
            slideMasterPart.SlideMaster = new SlideMaster(
                new CommonSlideData(
                    new ShapeTree(
                        new NonVisualGroupShapeProperties(
                            new NonVisualDrawingProperties() { Id = 1U, Name = "" },
                            new NonVisualGroupShapeDrawingProperties(),
                            new ApplicationNonVisualDrawingProperties()),
                        new GroupShapeProperties(new A.TransformGroup()))),
                new ColorMap()
                {
                    Background1 = A.ColorSchemeIndexValues.Light1,
                    Text1 = A.ColorSchemeIndexValues.Dark1,
                    Background2 = A.ColorSchemeIndexValues.Light2,
                    Text2 = A.ColorSchemeIndexValues.Dark2,
                    Accent1 = A.ColorSchemeIndexValues.Accent1,
                    Accent2 = A.ColorSchemeIndexValues.Accent2,
                    Accent3 = A.ColorSchemeIndexValues.Accent3,
                    Accent4 = A.ColorSchemeIndexValues.Accent4,
                    Accent5 = A.ColorSchemeIndexValues.Accent5,
                    Accent6 = A.ColorSchemeIndexValues.Accent6,
                    Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                    FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink
                },
                new SlideLayoutIdList(new SlideLayoutId() { Id = 2147483649U, RelationshipId = layoutRelId }));
            slideMasterPart.SlideMaster.Save();

            var masterRelId = presentationPart.GetIdOfPart(slideMasterPart);
            presentationPart.Presentation = new Presentation(
                new SlideMasterIdList(new SlideMasterId() { Id = 2147483648U, RelationshipId = masterRelId }),
                new SlideIdList(),
                new SlideSize() { Cx = (int)PreviewSlideWidthEmu, Cy = (int)PreviewSlideHeightEmu, Type = SlideSizeValues.Screen16x9 },
                new NotesSize() { Cx = 6858000, Cy = 9144000 });
            presentationPart.Presentation.Save();

            return slideLayoutPart;
        }

        private static SlideLayoutPart? TryInitializePresentationFromTemplate(PresentationPart presentationPart)
        {
            var templatePath = ResolveSlideTemplatePath();
            if (string.IsNullOrWhiteSpace(templatePath) || !System.IO.File.Exists(templatePath))
            {
                return null;
            }

            try
            {
                using var templateDoc = PresentationDocument.Open(templatePath, false);
                var templatePresentationPart = templateDoc.PresentationPart;
                if (templatePresentationPart?.Presentation == null) return null;

                var templateMaster = templatePresentationPart.SlideMasterParts.FirstOrDefault();
                if (templateMaster == null) return null;

                var clonedMaster = presentationPart.AddPart(templateMaster);
                var masterRelId = presentationPart.GetIdOfPart(clonedMaster);

                var preferredLayout = clonedMaster.SlideLayoutParts
                    .OrderBy(lp =>
                    {
                        var t = lp.SlideLayout?.Type?.Value;
                        return t == SlideLayoutValues.Object ? 0 : 1;
                    })
                    .ThenBy(lp => lp.Uri.ToString(), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (preferredLayout == null) return null;

                var slideSize = templatePresentationPart.Presentation.SlideSize != null
                    ? (SlideSize)templatePresentationPart.Presentation.SlideSize.CloneNode(true)
                    : new SlideSize() { Cx = 9144000, Cy = 6858000, Type = SlideSizeValues.Screen4x3 };

                var notesSize = templatePresentationPart.Presentation.NotesSize != null
                    ? (NotesSize)templatePresentationPart.Presentation.NotesSize.CloneNode(true)
                    : new NotesSize() { Cx = 6858000, Cy = 9144000 };

                var presentation = new Presentation(
                    new SlideMasterIdList(new SlideMasterId() { Id = 2147483648U, RelationshipId = masterRelId }),
                    new SlideIdList(),
                    slideSize,
                    notesSize);

                if (templatePresentationPart.Presentation.DefaultTextStyle != null)
                {
                    presentation.Append((DefaultTextStyle)templatePresentationPart.Presentation.DefaultTextStyle.CloneNode(true));
                }

                presentationPart.Presentation = presentation;
                presentationPart.Presentation.Save();

                return preferredLayout;
            }
            catch
            {
                return null;
            }
        }

        private static string? ResolveSlideTemplatePath()
        {
            var candidates = new[]
            {
                Path.Combine("Imports", "SlideTemplate", "SlideTemplate.pptx"),
                Path.Combine("ETDP", "Imports", "SlideTemplate", "SlideTemplate.pptx"),
                @"C:\ETDP\ETDP\Imports\SlideTemplate\SlideTemplate.pptx"
            };

            foreach (var relativeOrAbsolute in candidates)
            {
                var path = Path.IsPathRooted(relativeOrAbsolute)
                    ? relativeOrAbsolute
                    : ResolveFromCurrentOrParents(relativeOrAbsolute, 6);
                if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private static SlidePart AddContentSlidePart(
            PresentationPart presentationPart,
            SlideLayoutPart slideLayoutPart,
            string pageTitle,
            List<string> lines)
        {
            var slidePart = presentationPart.AddNewPart<SlidePart>();
            slidePart.AddPart(slideLayoutPart);
            slidePart.Slide = new Slide(new CommonSlideData(new ShapeTree()), new ColorMapOverride(new A.MasterColorMapping()));

            var common = slidePart.Slide.CommonSlideData ??= new CommonSlideData(new ShapeTree());
            var tree = common.ShapeTree ??= new ShapeTree();
            var nvGroupProps = new NonVisualGroupShapeProperties(
                new NonVisualDrawingProperties() { Id = 1U, Name = "Slide" },
                new NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties());
            var groupShapeProps = new GroupShapeProperties(new A.TransformGroup());
            tree.Append(nvGroupProps);
            tree.Append(groupShapeProps);

            var bgShape = new Shape(
                new NonVisualShapeProperties(
                    new NonVisualDrawingProperties() { Id = 2U, Name = "Background" },
                    new NonVisualShapeDrawingProperties(new A.ShapeLocks() { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties()),
                CreateRectangleShapeProperties(
                    0,
                    0,
                    9144000,
                    6858000,
                    new A.SolidFill(new A.RgbColorModelHex() { Val = "0B2447" })),
                new TextBody(
                    new A.BodyProperties(),
                    new A.ListStyle(),
                    new A.Paragraph(new A.Run(new A.Text(string.Empty)))));
            tree.Append(bgShape);

            var titleRPr = new A.RunProperties() { Bold = true, FontSize = 1800 };
            titleRPr.Append(new A.SolidFill(new A.RgbColorModelHex() { Val = "FFFFFF" }));
            titleRPr.Append(new A.LatinFont() { Typeface = "Calibri" });
            var titleParagraph = new A.Paragraph(new A.ParagraphProperties(), new A.Run(titleRPr, new A.Text((pageTitle ?? "LESSON PLAN").ToUpperInvariant())));
            var titleTextBody = new TextBody(new A.BodyProperties(), new A.ListStyle(), titleParagraph);
            var titleShape = new Shape(
                new NonVisualShapeProperties(
                    new NonVisualDrawingProperties() { Id = 3U, Name = "Title" },
                    new NonVisualShapeDrawingProperties(new A.ShapeLocks() { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties()),
                CreateRectangleShapeProperties(457200, 457200, 8229600, 914400),
                titleTextBody);
            tree.Append(titleShape);

            var bodyTextBody = new TextBody(new A.BodyProperties(), new A.ListStyle());
            foreach (var line in lines ?? new List<string>())
            {
                var text = (line ?? string.Empty).Trim();
                if (text.Length == 0) continue;
                var rPr = new A.RunProperties() { FontSize = 1600 };
                rPr.Append(new A.SolidFill(new A.RgbColorModelHex() { Val = "FFFFFF" }));
                rPr.Append(new A.LatinFont() { Typeface = "Calibri" });
                var p = new A.Paragraph(new A.ParagraphProperties(), new A.Run(rPr, new A.Text("• " + text)));
                bodyTextBody.Append(p);
            }
            if (!bodyTextBody.Elements<A.Paragraph>().Any())
            {
                bodyTextBody.Append(new A.Paragraph(new A.Run(new A.Text("• "))));
            }

            var bodyShape = new Shape(
                new NonVisualShapeProperties(
                    new NonVisualDrawingProperties() { Id = 4U, Name = "Body" },
                    new NonVisualShapeDrawingProperties(new A.ShapeLocks() { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties()),
                CreateRectangleShapeProperties(457200, 1645920, 8229600, 4572000),
                bodyTextBody);
            tree.Append(bodyShape);

            slidePart.Slide.Save();
            return slidePart;
        }

        private static string MakeSafeFilePart(string? value, string fallback)
        {
            var v = (value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(v)) return fallback;
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                v = v.Replace(c, '_');
            }
            v = v.Replace(" ", "");
            return string.IsNullOrWhiteSpace(v) ? fallback : v;
        }

        private static string? ResolveFromCurrentOrParents(string relativePath, int maxDepth)
        {
            var current = Directory.GetCurrentDirectory();
            for (var depth = 0; depth <= maxDepth; depth++)
            {
                var combined = Path.Combine(current, relativePath);
                if (System.IO.File.Exists(combined)) return combined;
                var parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }
            return null;
        }

        private static string? ResolveDirectoryFromCurrentOrParents(string relativePath, int maxDepth)
        {
            var current = Directory.GetCurrentDirectory();
            for (var depth = 0; depth <= maxDepth; depth++)
            {
                var combined = Path.Combine(current, relativePath);
                if (Directory.Exists(combined)) return combined;
                var parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }
            return null;
        }

        private static string GetUniquePath(string dir, string baseName)
        {
            var target = Path.Combine(dir, baseName);
            if (!System.IO.File.Exists(target)) return target;
            var stem = Path.GetFileNameWithoutExtension(baseName);
            var ext = Path.GetExtension(baseName);
            var i = 2;
            while (true)
            {
                var candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
                if (!System.IO.File.Exists(candidate)) return candidate;
                i++;
            }
        }

        [HttpPost("process-material/{id}")]
        public IActionResult ProcessMaterial(int id)
        {
            var s = _context.SourceMaterials.Find(id);
            if (s == null) return NotFound();
            if (!System.IO.File.Exists(s.FilePath)) return NotFound("File not found");
            var title = string.IsNullOrWhiteSpace(s.Title) ? s.FileName : s.Title;
            var safeTitle = Regex.Replace(title ?? "material", @"[^\w\-]+", "_");
            var outDir = Path.Combine("C:\\ETDP\\ETDP", "Processed");
            Directory.CreateDirectory(outDir);
            var mdPath = Path.Combine(outDir, $"{safeTitle}_processed.md");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# {title}");
            if (string.Equals(s.FileType, "pdf", StringComparison.OrdinalIgnoreCase))
            {
                var pdfPages = ReadPdfPages(s.FilePath);
                var totalPages = pdfPages.Count;
                var pageBlocks = new List<(int Number, string Text, bool Skip)>();
                foreach (var page in pdfPages)
                {
                    var raw = page.Text;
                    var text = CleanExtractedText(raw ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var skip = ShouldSkipPdfPageText(page.Number, totalPages, text);
                    pageBlocks.Add((page.Number, text, skip));
                }

                var selectedBlocks = pageBlocks.Where(x => !x.Skip).ToList();
                if (selectedBlocks.Count == 0) selectedBlocks = pageBlocks;

                foreach (var page in selectedBlocks)
                {
                    sb.AppendLine();
                    sb.AppendLine($"## Page {page.Number}");
                    sb.AppendLine();
                    sb.AppendLine(page.Text);
                }
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine(CleanExtractedText(s.ExtractedText ?? ""));
            }
            System.IO.File.WriteAllText(mdPath, sb.ToString());
            var processed = new ETD.Api.Models.SourceMaterial
            {
                Title = $"{title} (Processed)",
                FileName = Path.GetFileName(mdPath),
                FilePath = mdPath,
                FileType = "md",
                ExtractedText = sb.ToString(),
                QualificationCode = s.QualificationCode,
                QualificationDescription = s.QualificationDescription,
                SubjectDescription = s.SubjectDescription,
                TopicDescription = s.TopicDescription,
                AssessmentCriteriaDescription = s.AssessmentCriteriaDescription,
                Url = s.Url,
                KnowledgeSourceType = s.KnowledgeSourceType,
                KnowledgeNumber = s.KnowledgeNumber,
                KnowledgeLabel = string.IsNullOrWhiteSpace(s.KnowledgeLabel) ? null : $"{s.KnowledgeLabel} (Processed)",
                KnowledgeRootPath = s.KnowledgeRootPath,
                KnowledgeUploadedAtUtc = DateTime.UtcNow
            };
            ApplyKnowledgeMetadata(
                processed,
                s.KnowledgeSourceType,
                s.QualificationCode,
                knowledgeNumber: s.KnowledgeNumber,
                uploadedAtUtc: DateTime.UtcNow,
                knowledgeRootPath: s.KnowledgeRootPath,
                knowledgeLabel: processed.KnowledgeLabel);
            _context.SourceMaterials.Add(processed);
            _context.SaveChanges();
            return Ok(new { createdId = processed.Id, path = mdPath });
        }
        public class AssembleRequest
        {
            public int LecturerToolkitEntryId { get; set; }
            public string Content { get; set; } = string.Empty;
        }

        [HttpPost("assemble")]
        public IActionResult Assemble([FromBody] AssembleRequest req)
        {
            var entry = _context.LecturerToolkitEntries.Find(req.LecturerToolkitEntryId);
            if (entry == null) return NotFound("Toolkit entry not found");
            var incoming = (req.Content ?? "").Trim();
            if (string.IsNullOrWhiteSpace(incoming)) return BadRequest("Content is empty");

            var existing = (entry.LessonPlanContent ?? "").Trim();
            if (string.Equals(existing, incoming, StringComparison.Ordinal))
            {
                return Ok(new { saved = true, appended = false, reason = "duplicate_exact" });
            }
            if (!string.IsNullOrEmpty(existing) && existing.Contains(incoming, StringComparison.Ordinal))
            {
                return Ok(new { saved = true, appended = false, reason = "duplicate_segment" });
            }

            entry.LessonPlanContent = string.IsNullOrEmpty(existing)
                ? incoming
                : $"{existing}\n\n{incoming}";
            _context.SaveChanges();
            return Ok(new { saved = true, appended = true });
        }

        public class DraftRequest
        {
            public string SubjectName { get; set; } = string.Empty;
            public string SubjectDescription { get; set; } = string.Empty;
            public string TopicDescription { get; set; } = string.Empty;
            public string TopicPurpose { get; set; } = string.Empty;
            public string LessonPlanDescription { get; set; } = string.Empty;
            public string AssessmentCriteriaDescription { get; set; } = string.Empty;
            public string LecturerActions { get; set; } = string.Empty;
            public string LearnerActions { get; set; } = string.Empty;
            public string[] Sources { get; set; } = Array.Empty<string>();
            public string Length { get; set; } = "800–1200 words";
            public string Level { get; set; } = "Grade 11 TVET";
        }

        [HttpPost("draft")]
        public async Task<IActionResult> Draft([FromBody] DraftRequest req)
        {
            if (req == null) return BadRequest("Request body is required");
            var aiMode = AiRuntime.GetMode();
            var systemPrompt = BuildDraftSystemPrompt();
            var userPrompt = BuildDraftUserPrompt(req);

            if (AiRuntime.PreferLocalFirst())
            {
                var localDraft = await TryGenerateDraftWithLocalLlmAsync(systemPrompt, userPrompt);
                if (!string.IsNullOrWhiteSpace(localDraft))
                    return Ok(new { content = localDraft, backend = "local_llm", aiMode });
            }

            if (AiRuntime.AllowGoogle())
            {
                var geminiDraft = await TryGenerateDraftWithGeminiAsync(systemPrompt, userPrompt);
                if (!string.IsNullOrWhiteSpace(geminiDraft))
                    return Ok(new { content = geminiDraft, backend = "google_gemini", aiMode });
            }

            if (AiRuntime.AllowOpenAi())
            {
                var openAiDraft = await TryGenerateDraftWithOpenAiAsync(systemPrompt, userPrompt);
                if (!string.IsNullOrWhiteSpace(openAiDraft))
                    return Ok(new { content = openAiDraft, backend = "openai", aiMode });
            }

            if (!AiRuntime.PreferLocalFirst())
            {
                var localDraft = await TryGenerateDraftWithLocalLlmAsync(systemPrompt, userPrompt);
                if (!string.IsNullOrWhiteSpace(localDraft))
                    return Ok(new { content = localDraft, backend = "local_llm", aiMode });
            }

            var fallback = BuildDeterministicDraftFallback(req);
            return Ok(new { content = fallback, backend = "deterministic_local", aiMode });
        }

        private static string BuildDraftSystemPrompt()
        {
            return
                "You are a vocational learning designer and subject-matter explainer for South African TVET courseware.\n" +
                "Goals:\n- Draft Lesson Plan Content for a specific Topic within a Subject.\n- Transform the provided source excerpts into coherent teaching content that helps the learner understand and apply the topic.\n- Align content with the provided Assessment Criteria Description, but do not merely restate curriculum wording.\n- Use the provided source excerpts for factual details; do not invent facts outside the evidence.\n- If the evidence is thin, do not pad with generic filler. Return a short explicit coverage-gap note instead.\n" +
                "Style:\n- Clear, textbook-style prose suitable for TVET learners.\n- Address the learner directly as 'you' whenever instructional guidance is needed.\n- Write the assessment criteria out in full when they need to be referenced; do not reduce them to codes.\n- Paraphrase and digest the evidence; do not copy verbatim except short technical terms or definitions.\n- Do NOT mention file paths, relative paths, source IDs, bracket citations, bibliography labels, or URLs inside the lesson content.\n- Do NOT say phrases such as 'according to the source', 'the cited text', 'the lesson plan content', or 'the curriculum content map'.\n- Write as if an experienced lecturer is teaching vocational learners, not as if you are auditing a document.\n" +
                "Structure:\n- Use the title, the mapped subject, the topic, and the full assessment criteria as anchors when relevant.\n- Let the explanation expand naturally from definitions and component knowledge into sequence, application, checks, examples, and workplace meaning according to the actual source material.\n- Do not force fixed sections such as Procedure and Application, Safety and Quality Checks, Common Faults / Errors, or Summary unless the operator explicitly asks for that format.\n" +
                "Constraints:\n- Match the requested length.\n- Avoid sensitive or personal data.\n- If contradictory sources appear, flag them and prefer the most reputable.\n- Do not pad with filler such as 'learn this topic', 'focus your study', 'you must understand', or 'study the explanation below' unless you immediately provide the actual grounded explanation.\n- If the provided excerpts do not contain enough grounded subject matter to answer the topic directly, return a short note that starts with 'INSUFFICIENT_SOURCE_COVERAGE:' and say what content is still missing.";
        }

        private static string BuildDraftUserPrompt(DraftRequest req)
        {
            var sources = req.Sources ?? Array.Empty<string>();
            var sourcesText = new System.Text.StringBuilder();
            for (int i = 0; i < sources.Length; i++)
            {
                var cleaned = SanitizeDraftSourceExcerpt(sources[i]);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    sourcesText.AppendLine($"Source Excerpt {i + 1}: {cleaned}");
                }
            }

            return
                $"Subject Name: {req.SubjectName}\nSubject Description: {req.SubjectDescription}\n\n" +
                $"Topic Description: {req.TopicDescription}\nTopic Purpose: {req.TopicPurpose}\n\n" +
                $"Lesson Plan Description: {req.LessonPlanDescription}\nAssessment Criteria Description: {req.AssessmentCriteriaDescription}\n\n" +
                $"Requested Length: {req.Length}\nReading Level: {req.Level}\n\n" +
                $"Lecturer Actions: {req.LecturerActions}\nLearner Actions: {req.LearnerActions}\n\n" +
                "Writing Directives:\n" +
                "- Digest the evidence into teaching prose.\n" +
                "- Name the real technical concept, tool, process, component, safety rule, or fault explicitly.\n" +
                "- Do not echo source labels or navigation text.\n" +
                "- Answer the topic and assessment criteria directly instead of telling the learner to go and study the topic somewhere else.\n" +
                "- If coverage is too thin, output 'INSUFFICIENT_SOURCE_COVERAGE:' and state what subject matter is missing.\n" +
                "- The learner should be able to study this section and apply the topic afterwards.\n\n" +
                $"Source Excerpts:\n{sourcesText}";
        }

        private static async Task<string?> TryGenerateDraftWithGeminiAsync(string systemPrompt, string userPrompt)
        {
            if (!AiRuntime.AllowGoogle()) return null;
            try
            {
                var combined = $"{systemPrompt}\n\n{userPrompt}";
                var result = await GoogleGeminiClient.GenerateContentAsync(combined, AiRuntime.GetGeminiModel());
                if (!string.IsNullOrWhiteSpace(result))
                {
                    return result.Trim();
                }
            }
            catch
            {
                // Fall through
            }
            return null;
        }

        private async Task<string?> TryGenerateDraftWithLocalLlmAsync(string systemPrompt, string userPrompt)
        {
            var endpoint = AiRuntime.GetLocalLlmEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint)) return null;

            var payload = new
            {
                model = AiRuntime.GetLocalLlmModel(),
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.25
            };

            using var msg = new HttpRequestMessage(HttpMethod.Post, endpoint.Trim());
            var localApiKey = AiRuntime.GetLocalLlmApiKey();
            if (!string.IsNullOrWhiteSpace(localApiKey))
            {
                var token = localApiKey.Trim();
                if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    token = token.Substring(7).Trim();
                if (!string.IsNullOrWhiteSpace(token))
                    msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            msg.Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

            try
            {
                var resp = await _http.SendAsync(msg);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) return null;
                var content = TryExtractChatCompletionText(body) ?? TryExtractResponseOutputTextForModerator(body);
                return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
            }
            catch
            {
                return null;
            }
        }

        private async Task<string?> TryGenerateDraftWithOpenAiAsync(string systemPrompt, string userPrompt)
        {
            if (!AiRuntime.AllowOpenAi()) return null;
            var key = Secrets.GetOpenAIKey();
            if (string.IsNullOrWhiteSpace(key)) return null;

            var model = AiRuntime.GetOpenAiModel("gpt-5-mini");
            var payload = new
            {
                model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.3
            };

            using var msg = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            msg.Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

            try
            {
                var resp = await _http.SendAsync(msg);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) return null;
                var content = TryExtractChatCompletionText(body);
                return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static string BuildDeterministicDraftFallback(DraftRequest req)
        {
            var subject = string.IsNullOrWhiteSpace(req.SubjectDescription) ? req.SubjectName : req.SubjectDescription;
            var topic = string.IsNullOrWhiteSpace(req.TopicDescription) ? "Current Topic" : req.TopicDescription;
            var criteria = string.IsNullOrWhiteSpace(req.AssessmentCriteriaDescription) ? "Use current assessment criteria." : req.AssessmentCriteriaDescription;
            var lesson = string.IsNullOrWhiteSpace(req.LessonPlanDescription) ? topic : req.LessonPlanDescription;
            var groundedSources = (req.Sources ?? Array.Empty<string>())
                .Select(SanitizeDraftSourceExcerpt)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Take(3)
                .ToList();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Title: {lesson}");
            sb.AppendLine($"Assessment Criteria: {criteria}");
            sb.AppendLine();

            if (groundedSources.Count == 0)
            {
                sb.AppendLine($"INSUFFICIENT_SOURCE_COVERAGE: ETDP does not yet have enough grounded source material to answer {criteria} directly for {topic} in {subject}. Upload subject matter that explains the topic in detail.");
                return sb.ToString().Trim();
            }

            foreach (var source in groundedSources)
            {
                sb.AppendLine(source);
                sb.AppendLine();
            }

            return sb.ToString().Trim();
        }

        private static string SanitizeDraftSourceExcerpt(string? value)
        {
            var text = (value ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var cleanedLines = text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Where(line => !Regex.IsMatch(line, @"^(citations?|bibliography|references)\s*:?\s*$", RegexOptions.IgnoreCase))
                .Where(line => !Regex.IsMatch(line, @"^\[\d+\]\s*"))
                .Where(line => !Regex.IsMatch(line, @"^[A-Za-z]:\\", RegexOptions.IgnoreCase))
                .Where(line => !Regex.IsMatch(line, @"\bhttps?://", RegexOptions.IgnoreCase))
                .Take(12)
                .ToList();

            var cleaned = string.Join(" ", cleanedLines);
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
            if (cleaned.Length > 1600)
            {
                cleaned = cleaned[..1600].TrimEnd();
            }

            return cleaned;
        }

        private static string? TryExtractChatCompletionText(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return null;

            var message = choices[0].TryGetProperty("message", out var msgObj) ? msgObj : default;
            if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var content))
                return null;

            if (content.ValueKind == JsonValueKind.String)
                return content.GetString();

            if (content.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object &&
                    part.TryGetProperty("text", out var txt) &&
                    txt.ValueKind == JsonValueKind.String)
                {
                    var text = txt.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }

            return null;
        }
        private async Task<string> ExtractHolisticContentWithLocalLlmAsync(string fullText, string query, List<string> terms, int snippetLength)
        {
            return BuildSnippet(fullText, query, terms, snippetLength).Trim();
        }
    }
}
