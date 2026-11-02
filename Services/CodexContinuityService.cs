using System.Reflection;
using System.Text;
using System.Text.Json;
using ETD.Api.Controllers;
using ETD.Api.Data;
using ETD.Api.Models;
using ETD.Api.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.DataProtection;

namespace ETD.Api.Services
{
    public class GeminiContinuityService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<GeminiContinuityService> _logger;
        private readonly IDataProtector _protector;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private readonly object _statusLock = new();

        private ContinuityStatus _status = new();

        public sealed class ContinuityStatus
        {
            public bool Enabled { get; set; } = true;
            public string RootPath { get; set; } = string.Empty;
            public string LatestMarkdownPath { get; set; } = string.Empty;
            public string LatestJsonPath { get; set; } = string.Empty;
            public string LatestEncryptedPath { get; set; } = string.Empty;
            public string LedgerPath { get; set; } = string.Empty;
            public int RefreshIntervalMinutes { get; set; } = 360;
            public DateTime? LastRunAtUtc { get; set; }
            public DateTime? LastSuccessAtUtc { get; set; }
            public string LastReason { get; set; } = string.Empty;
            public string LastError { get; set; } = string.Empty;
            public DateTime? LastErrorAtUtc { get; set; }
            public int LastControllersCount { get; set; }
            public int LastServicesCount { get; set; }
            public int LastModelsCount { get; set; }
            public int LastEndpointCount { get; set; }
            public DateTime CheckedAtUtc { get; set; }
        }

        private sealed class ContinuitySnapshot
        {
            public DateTime GeneratedAtUtc { get; set; }
            public string Reason { get; set; } = string.Empty;
            public string ApplicationRoot { get; set; } = string.Empty;
            public string WorkspaceRoot { get; set; } = string.Empty;
            public Dictionary<string, object> Runtime { get; set; } = new();
            public Dictionary<string, int> DataOverview { get; set; } = new();
            public List<object> Controllers { get; set; } = new();
            public List<object> Services { get; set; } = new();
            public List<string> DbSets { get; set; } = new();
            public List<object> KeyKnowledgeFiles { get; set; } = new();
            public List<object> RecentCoreFileUpdates { get; set; } = new();
        }

        private sealed class ContinuityLedgerEntry
        {
            public DateTime GeneratedAtUtc { get; set; }
            public string Reason { get; set; } = string.Empty;
            public string MarkdownPath { get; set; } = string.Empty;
            public string JsonPath { get; set; } = string.Empty;
            public string EncryptedPath { get; set; } = string.Empty;
            public int Controllers { get; set; }
            public int Services { get; set; }
            public int Models { get; set; }
            public int Endpoints { get; set; }
            public string Hash { get; set; } = string.Empty;
        }

        private sealed class EndpointSummary
        {
            public string Method { get; set; } = string.Empty;
            public string Route { get; set; } = string.Empty;
            public string Action { get; set; } = string.Empty;
        }

        public GeminiContinuityService(
            IServiceScopeFactory scopeFactory,
            IWebHostEnvironment environment,
            IDataProtectionProvider dataProtectionProvider,
            ILogger<GeminiContinuityService> logger)
        {
            _scopeFactory = scopeFactory;
            _environment = environment;
            _logger = logger;
            _protector = dataProtectionProvider.CreateProtector("ETDP.GeminiContinuity.v1");
        }

        public ContinuityStatus GetStatus()
        {
            lock (_statusLock)
            {
                return new ContinuityStatus
                {
                    Enabled = _status.Enabled,
                    RootPath = _status.RootPath,
                    LatestMarkdownPath = _status.LatestMarkdownPath,
                    LatestJsonPath = _status.LatestJsonPath,
                    LatestEncryptedPath = _status.LatestEncryptedPath,
                    LedgerPath = _status.LedgerPath,
                    RefreshIntervalMinutes = _status.RefreshIntervalMinutes,
                    LastRunAtUtc = _status.LastRunAtUtc,
                    LastSuccessAtUtc = _status.LastSuccessAtUtc,
                    LastReason = _status.LastReason,
                    LastError = _status.LastError,
                    LastErrorAtUtc = _status.LastErrorAtUtc,
                    LastControllersCount = _status.LastControllersCount,
                    LastServicesCount = _status.LastServicesCount,
                    LastModelsCount = _status.LastModelsCount,
                    LastEndpointCount = _status.LastEndpointCount,
                    CheckedAtUtc = DateTime.UtcNow
                };
            }
        }

        public string GetLatestMarkdown()
        {
            var path = GetStatus().LatestMarkdownPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return string.Empty;
            }
            return File.ReadAllText(path);
        }

        public async Task<ContinuityStatus> RefreshNowAsync(string reason, CancellationToken cancellationToken = default)
        {
            await _refreshLock.WaitAsync(cancellationToken);
            try
            {
                var now = DateTime.UtcNow;
                var rootPath = ResolveContinuityRootPath();
                Directory.CreateDirectory(rootPath);

                var snapshot = await BuildSnapshotAsync(reason, cancellationToken);
                snapshot.GeneratedAtUtc = now;

                var jsonPath = Path.Combine(rootPath, "gemini-continuity-latest.json");
                var markdownPath = Path.Combine(rootPath, "gemini-continuity-latest.md");
                var encryptedPath = Path.Combine(rootPath, "gemini-continuity-latest.protected.txt");
                var ledgerPath = Path.Combine(rootPath, "gemini-continuity-ledger.jsonl");

                var legacyJsonPath = Path.Combine(rootPath, "codex-continuity-latest.json");
                var legacyMarkdownPath = Path.Combine(rootPath, "codex-continuity-latest.md");

                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                var markdown = BuildMarkdown(snapshot);
                var encrypted = _protector.Protect(json);

                File.WriteAllText(jsonPath, json, Encoding.UTF8);
                File.WriteAllText(markdownPath, markdown, Encoding.UTF8);
                File.WriteAllText(encryptedPath, encrypted, Encoding.UTF8);

                // Keep legacy files synchronized for backward compatibility
                try
                {
                    File.WriteAllText(legacyJsonPath, json, Encoding.UTF8);
                    File.WriteAllText(legacyMarkdownPath, markdown, Encoding.UTF8);
                }
                catch { }

                var ledgerEntry = new ContinuityLedgerEntry
                {
                    GeneratedAtUtc = now,
                    Reason = reason,
                    MarkdownPath = markdownPath,
                    JsonPath = jsonPath,
                    EncryptedPath = encryptedPath,
                    Controllers = snapshot.Controllers.Count,
                    Services = snapshot.Services.Count,
                    Models = snapshot.DataOverview.TryGetValue("Models", out var modelCount) ? modelCount : 0,
                    Endpoints = snapshot.DataOverview.TryGetValue("Endpoints", out var endpointCount) ? endpointCount : 0,
                    Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant()
                };
                File.AppendAllText(ledgerPath, JsonSerializer.Serialize(ledgerEntry) + Environment.NewLine, Encoding.UTF8);

                lock (_statusLock)
                {
                    _status.RootPath = rootPath;
                    _status.LatestMarkdownPath = markdownPath;
                    _status.LatestJsonPath = jsonPath;
                    _status.LatestEncryptedPath = encryptedPath;
                    _status.LedgerPath = ledgerPath;
                    _status.LastRunAtUtc = now;
                    _status.LastSuccessAtUtc = now;
                    _status.LastReason = reason;
                    _status.LastError = string.Empty;
                    _status.LastControllersCount = snapshot.Controllers.Count;
                    _status.LastServicesCount = snapshot.Services.Count;
                    _status.LastModelsCount = snapshot.DataOverview.TryGetValue("Models", out var m) ? m : 0;
                    _status.LastEndpointCount = snapshot.DataOverview.TryGetValue("Endpoints", out var e) ? e : 0;
                }

                return GetStatus();
            }
            catch (Exception ex)
            {
                lock (_statusLock)
                {
                    _status.LastRunAtUtc = DateTime.UtcNow;
                    _status.LastError = ex.Message;
                    _status.LastErrorAtUtc = DateTime.UtcNow;
                    _status.LastReason = reason;
                }
                _logger.LogWarning(ex, "Failed to refresh Gemini continuity log.");
                return GetStatus();
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var intervalMinutes = GetIntEnv("GEMINI_CONTINUITY_INTERVAL_MINUTES",
                GetIntEnv("CODEX_CONTINUITY_INTERVAL_MINUTES", 360, 15, 1440), 15, 1440);
            lock (_statusLock)
            {
                _status.Enabled = GetBoolEnv("GEMINI_CONTINUITY_ENABLED", GetBoolEnv("CODEX_CONTINUITY_ENABLED", true));
                _status.RefreshIntervalMinutes = intervalMinutes;
                _status.RootPath = ResolveContinuityRootPath();
                _status.LatestMarkdownPath = Path.Combine(_status.RootPath, "gemini-continuity-latest.md");
                _status.LatestJsonPath = Path.Combine(_status.RootPath, "gemini-continuity-latest.json");
                _status.LatestEncryptedPath = Path.Combine(_status.RootPath, "gemini-continuity-latest.protected.txt");
                _status.LedgerPath = Path.Combine(_status.RootPath, "gemini-continuity-ledger.jsonl");
            }

            if (!_status.Enabled)
            {
                return;
            }

            await RefreshNowAsync("startup", stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
                    await RefreshNowAsync("scheduled", stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Scheduled Gemini continuity refresh failed.");
                }
            }
        }

        private async Task<ContinuitySnapshot> BuildSnapshotAsync(string reason, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var assembly = typeof(KnowledgeController).Assembly;
            var controllerTypes = assembly
                .GetTypes()
                .Where(t => !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var serviceTypes = assembly
                .GetTypes()
                .Where(t => !t.IsAbstract &&
                            t.IsClass &&
                            t.Namespace == "ETD.Api.Services")
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var modelTypes = assembly
                .GetTypes()
                .Where(t => !t.IsAbstract &&
                            t.IsClass &&
                            t.Namespace == "ETD.Api.Models")
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var controllers = new List<object>();
            var endpointCount = 0;
            foreach (var type in controllerTypes)
            {
                var routeBase = type.GetCustomAttribute<RouteAttribute>()?.Template ?? "api/[controller]";
                var constructor = type.GetConstructors()
                    .OrderByDescending(c => c.GetParameters().Length)
                    .FirstOrDefault();
                var dependencies = constructor == null
                    ? new List<string>()
                    : constructor.GetParameters()
                        .Select(p => p.ParameterType.Name)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                var endpoints = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .SelectMany(method =>
                    {
                        var attrs = method.GetCustomAttributes()
                            .OfType<HttpMethodAttribute>()
                            .ToList();
                        if (attrs.Count == 0)
                        {
                            return new[]
                            {
                                new EndpointSummary
                                {
                                    Method = "UNKNOWN",
                                    Route = string.Empty,
                                    Action = method.Name
                                }
                            };
                        }

                        return attrs.Select(attr => new EndpointSummary
                        {
                            Method = attr.HttpMethods.FirstOrDefault() ?? "ANY",
                            Route = attr.Template ?? string.Empty,
                            Action = method.Name
                        });
                    })
                    .OrderBy(e => e.Method, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Route, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                endpointCount += endpoints.Count;
                controllers.Add(new
                {
                    Name = type.Name,
                    RouteBase = routeBase,
                    Dependencies = dependencies,
                    Endpoints = endpoints
                });
            }

            var services = serviceTypes.Select(type =>
            {
                var constructor = type.GetConstructors()
                    .OrderByDescending(c => c.GetParameters().Length)
                    .FirstOrDefault();
                var dependencies = constructor == null
                    ? new List<string>()
                    : constructor.GetParameters()
                        .Select(p => p.ParameterType.Name)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                return (object)new
                {
                    Name = type.Name,
                    IsHosted = typeof(IHostedService).IsAssignableFrom(type),
                    Dependencies = dependencies
                };
            }).ToList();

            var dbSetNames = typeof(ApplicationDbContext)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType.IsGenericType &&
                            p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
                .Select(p => p.Name)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var dataOverview = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Controllers"] = controllerTypes.Count,
                ["Services"] = serviceTypes.Count,
                ["Models"] = modelTypes.Count,
                ["Endpoints"] = endpointCount,
                ["DbSets"] = dbSetNames.Count
            };

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<ApplicationDbContext>();
            if (db != null)
            {
                try
                {
                    dataOverview["Qualifications"] = await db.Qualifications.CountAsync(cancellationToken);
                    dataOverview["Subjects"] = await db.Subjects.CountAsync(cancellationToken);
                    dataOverview["Topics"] = await db.Topics.CountAsync(cancellationToken);
                    dataOverview["Outcomes"] = await db.Outcomes.CountAsync(cancellationToken);
                    dataOverview["LessonPlans"] = await db.LessonPlans.CountAsync(cancellationToken);
                    dataOverview["AssessmentCriteria"] = await db.AssessmentCriteria.CountAsync(cancellationToken);
                    dataOverview["SourceMaterials"] = await db.SourceMaterials.CountAsync(cancellationToken);
                    dataOverview["LearnerGuides"] = await db.LearnerGuides.CountAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    dataOverview["DbReadError"] = 1;
                    _logger.LogDebug(ex, "Failed reading data overview counts for Gemini continuity snapshot.");
                }
            }

            var runtime = new Dictionary<string, object>
            {
                ["Environment"] = _environment.EnvironmentName,
                ["ContentRoot"] = _environment.ContentRootPath,
                ["AiMode"] = AiRuntime.GetMode(),
                ["GeminiConfigured"] = !string.IsNullOrWhiteSpace(Secrets.GetGoogleKey()),
                ["LocalLlmConfigured"] = !string.IsNullOrWhiteSpace(AiRuntime.GetLocalLlmEndpoint()),
                ["DotNetVersion"] = Environment.Version.ToString(),
                ["MachineName"] = Environment.MachineName,
                ["OsDescription"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription
            };

            var keyKnowledgeFiles = CollectKeyKnowledgeFiles();
            var recentUpdates = CollectRecentCoreUpdates();

            return new ContinuitySnapshot
            {
                Reason = reason,
                ApplicationRoot = _environment.ContentRootPath,
                WorkspaceRoot = ResolveWorkspaceRoot(_environment.ContentRootPath),
                Runtime = runtime,
                DataOverview = dataOverview,
                Controllers = controllers,
                Services = services,
                DbSets = dbSetNames,
                KeyKnowledgeFiles = keyKnowledgeFiles,
                RecentCoreFileUpdates = recentUpdates
            };
        }

        private List<object> CollectKeyKnowledgeFiles()
        {
            var results = new List<object>();
            var candidatePaths = new[]
            {
                Path.Combine(_environment.ContentRootPath, "Imports"),
                Path.Combine(_environment.ContentRootPath, "Exports"),
                Path.Combine(_environment.ContentRootPath, "SystemData"),
                Path.Combine(_environment.ContentRootPath, "Google")
            };

            foreach (var folder in candidatePaths)
            {
                if (!Directory.Exists(folder)) continue;
                try
                {
                    var files = Directory.GetFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .Take(25);
                    foreach (var file in files)
                    {
                        var info = new FileInfo(file);
                        results.Add(new
                        {
                            Path = file,
                            Name = info.Name,
                            Length = info.Length,
                            LastWriteTimeUtc = info.LastWriteTimeUtc
                        });
                    }
                }
                catch { }
            }

            return results;
        }

        private List<object> CollectRecentCoreUpdates()
        {
            var results = new List<object>();
            var targets = new[] { "Controllers", "Services", "Models", "Utils", "Data" };
            foreach (var target in targets)
            {
                var dir = Path.Combine(_environment.ContentRootPath, target);
                if (!Directory.Exists(dir)) continue;

                try
                {
                    var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .Take(6);
                    foreach (var file in files)
                    {
                        results.Add(new
                        {
                            Area = target,
                            Name = file.Name,
                            LastWriteTimeUtc = file.LastWriteTimeUtc,
                            Length = file.Length
                        });
                    }
                }
                catch { }
            }

            return results;
        }

        private static string BuildMarkdown(ContinuitySnapshot snapshot)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Google Gemini & Antigravity Continuity Log");
            sb.AppendLine();
            sb.AppendLine($"- **Generated At (UTC):** {snapshot.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"- **Reason:** {snapshot.Reason}");
            sb.AppendLine($"- **Application Root:** `{snapshot.ApplicationRoot}`");
            sb.AppendLine($"- **Workspace Root:** `{snapshot.WorkspaceRoot}`");
            sb.AppendLine();

            sb.AppendLine("## Runtime Status");
            sb.AppendLine();
            foreach (var kvp in snapshot.Runtime)
            {
                sb.AppendLine($"- **{kvp.Key}:** {kvp.Value}");
            }
            sb.AppendLine();

            sb.AppendLine("## Data Overview");
            sb.AppendLine();
            foreach (var kvp in snapshot.DataOverview)
            {
                sb.AppendLine($"- **{kvp.Key}:** {kvp.Value}");
            }
            sb.AppendLine();

            sb.AppendLine("## Controllers");
            sb.AppendLine();
            foreach (var controller in snapshot.Controllers)
            {
                var json = JsonSerializer.Serialize(controller);
                sb.AppendLine($"- {json}");
            }
            sb.AppendLine();

            sb.AppendLine("## Services");
            sb.AppendLine();
            foreach (var service in snapshot.Services)
            {
                var json = JsonSerializer.Serialize(service);
                sb.AppendLine($"- {json}");
            }
            sb.AppendLine();

            sb.AppendLine("## DbSets");
            sb.AppendLine();
            foreach (var dbset in snapshot.DbSets)
            {
                sb.AppendLine($"- {dbset}");
            }
            sb.AppendLine();

            sb.AppendLine("## Key Knowledge Files");
            sb.AppendLine();
            foreach (var file in snapshot.KeyKnowledgeFiles)
            {
                var json = JsonSerializer.Serialize(file);
                sb.AppendLine($"- {json}");
            }
            sb.AppendLine();

            sb.AppendLine("## Recent Core File Updates");
            sb.AppendLine();
            foreach (var file in snapshot.RecentCoreFileUpdates)
            {
                var json = JsonSerializer.Serialize(file);
                sb.AppendLine($"- {json}");
            }

            return sb.ToString().TrimEnd();
        }

        private string ResolveContinuityRootPath()
        {
            var configured = (Environment.GetEnvironmentVariable("GEMINI_CONTINUITY_PATH") ??
                              Environment.GetEnvironmentVariable("CODEX_CONTINUITY_PATH") ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }
            return Path.Combine(_environment.ContentRootPath, "SystemData", "GeminiContinuity");
        }

        private static string ResolveWorkspaceRoot(string contentRoot)
        {
            try
            {
                var parent = Directory.GetParent(contentRoot);
                if (parent == null) return contentRoot;
                return parent.FullName;
            }
            catch
            {
                return contentRoot;
            }
        }

        private static bool GetBoolEnv(string key, bool defaultValue)
        {
            var raw = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
            if (bool.TryParse(raw.Trim(), out var parsed)) return parsed;
            return defaultValue;
        }

        private static int GetIntEnv(string key, int defaultValue, int min, int max)
        {
            var raw = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
            if (!int.TryParse(raw.Trim(), out var parsed)) return defaultValue;
            if (parsed < min) return min;
            if (parsed > max) return max;
            return parsed;
        }
    }

    // Backward-compatible alias for existing DI registrations
    public sealed class CodexContinuityService : GeminiContinuityService
    {
        public CodexContinuityService(
            IServiceScopeFactory scopeFactory,
            IWebHostEnvironment environment,
            IDataProtectionProvider dataProtectionProvider,
            ILogger<GeminiContinuityService> logger)
            : base(scopeFactory, environment, dataProtectionProvider, logger)
        {
        }
    }
}
