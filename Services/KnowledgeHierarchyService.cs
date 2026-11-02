using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Net.Http;
using System.Net.Http.Headers;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Wordprocessing;
using DrawingText = DocumentFormat.OpenXml.Drawing.Text;
using ETD.Api.Data;
using ETD.Api.Models;
using ETD.Api.Utils;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ETD.Api.Services
{
    public sealed class KnowledgeHierarchyService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<KnowledgeHierarchyService> _logger;
        private readonly OcrExtractionService _ocrExtractionService;
        private readonly PdfVisualExtractionService _pdfVisualExtractionService;
        private static readonly HttpClient _stirlingHttp = new HttpClient();

        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".docx", ".pdf", ".pptx", ".csv", ".json", ".jsonl", ".xml", ".yml", ".yaml", ".html", ".htm",
            ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".svg"
        };

        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".csv", ".json", ".jsonl", ".xml", ".yml", ".yaml", ".html", ".htm"
        };

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".svg"
        };

        private static readonly string[] LocalSubjectMatterAliasFolders =
        {
            "subject_matter",
            "subject matter",
            "subjectmatter",
            "local_source_upload",
            "local source upload",
            "localsourceupload"
        };

        private static readonly string[] DeveloperKnowledgeAliasFolders =
        {
            "developer_knowledge_base",
            "developer knowledge base",
            "developerknowledgebase",
            "developer_kb",
            "developer kb",
            "dev_knowledge"
        };

        private static readonly HashSet<string> CoverageStopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "with", "from", "this", "that", "into", "within", "also", "shall", "must",
            "are", "was", "were", "have", "has", "had", "can", "could", "should", "will", "would", "about",
            "topic", "subject", "phase", "unit", "module", "outcome", "criteria", "assessment", "learning",
            "programme", "program", "qualification", "code", "description", "level", "credits", "nqf"
        };

        public sealed class SyncOptions
        {
            public string? QualificationCode { get; set; }
            public string? QualificationDescription { get; set; }
            public bool IncludeLocalSourceUploads { get; set; } = true;
            public bool IncludeDeveloperKnowledgeBase { get; set; } = true;
            public int MaxFilesPerInbox { get; set; } = 1000;
            public bool RebuildUploadReadme { get; set; } = true;
            public bool ConsolidateLegacyFolders { get; set; } = true;
        }

        public sealed class ConsolidationOptions
        {
            public string? QualificationCode { get; set; }
            public bool RebuildUploadReadme { get; set; } = true;
            public bool RemoveEmptyLegacyFolders { get; set; } = true;
        }

        public sealed class ConsolidationResult
        {
            public string RootPath { get; set; } = string.Empty;
            public string UploadReadmePath { get; set; } = string.Empty;
            public int QualificationGroupsScanned { get; set; }
            public int LegacyFoldersProcessed { get; set; }
            public int FilesMoved { get; set; }
            public int MaterialsUpdated { get; set; }
            public int EmptyLegacyFoldersRemoved { get; set; }
            public List<string> ConsolidatedFolders { get; set; } = new();
        }

        public sealed class SyncDetail
        {
            public string QualificationCode { get; set; } = string.Empty;
            public string QualificationDescription { get; set; } = string.Empty;
            public string SourceType { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public string? Reason { get; set; }
            public int? KnowledgeNumber { get; set; }
            public string? ArchivedPath { get; set; }
        }

        public sealed class SyncResult
        {
            public string RootPath { get; set; } = string.Empty;
            public string UploadReadmePath { get; set; } = string.Empty;
            public int QualificationsScanned { get; set; }
            public int FilesScanned { get; set; }
            public int Created { get; set; }
            public int Skipped { get; set; }
            public int Failed { get; set; }
            public List<SyncDetail> Details { get; set; } = new();
            public List<CoverageReportSummary> CoverageReports { get; set; } = new();
        }

        public sealed class CoverageReportSummary
        {
            public string QualificationCode { get; set; } = string.Empty;
            public string QualificationDescription { get; set; } = string.Empty;
            public DateTime GeneratedAtUtc { get; set; }
            public string SourceType { get; set; } = "developer_knowledge_base";
            public string ReportsDirectory { get; set; } = string.Empty;
            public string MarkdownPath { get; set; } = string.Empty;
            public string TextPath { get; set; } = string.Empty;
            public int DeveloperResourcesConsidered { get; set; }
            public int TopicsTotal { get; set; }
            public int TopicsCovered { get; set; }
            public int TopicsMissing { get; set; }
            public int UploadedInRun { get; set; }
            public int SkippedInRun { get; set; }
            public int FailedInRun { get; set; }
            public List<string> MissingTopics { get; set; } = new();
        }

        public sealed class CoverageReportOptions
        {
            public string QualificationCode { get; set; } = string.Empty;
            public string QualificationDescription { get; set; } = string.Empty;
            public string? QualificationRootPath { get; set; }
            public int UploadedInRun { get; set; }
            public int SkippedInRun { get; set; }
            public int FailedInRun { get; set; }
        }

        public sealed class StructureInfo
        {
            public string QualificationCode { get; set; } = string.Empty;
            public string QualificationDescription { get; set; } = string.Empty;
            public string QualificationRootPath { get; set; } = string.Empty;
            public string CurriculumLibraryPath { get; set; } = string.Empty;
            public string LocalInboxPath { get; set; } = string.Empty;
            public string LocalArchivePath { get; set; } = string.Empty;
            public string DeveloperInboxPath { get; set; } = string.Empty;
            public string DeveloperArchivePath { get; set; } = string.Empty;
            public string DeveloperReportsPath { get; set; } = string.Empty;
            public string UploadReadmePath { get; set; } = string.Empty;
        }

        public sealed class AgentKnowledgeStructureInfo
        {
            public string Scope { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string SourceType { get; set; } = string.Empty;
            public string ScopeRootPath { get; set; } = string.Empty;
            public string InboxPath { get; set; } = string.Empty;
            public string ArchivePath { get; set; } = string.Empty;
            public string DuplicatePath { get; set; } = string.Empty;
            public string ReadmePath { get; set; } = string.Empty;
        }

        public sealed class AgentKnowledgeSyncOptions
        {
            public string? Scope { get; set; }
            public bool IncludeSharedKnowledge { get; set; } = true;
            public int MaxFilesPerInbox { get; set; } = 1000;
            public bool RebuildReadme { get; set; } = true;
        }

        public sealed class AgentKnowledgeSyncDetail
        {
            public string Scope { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string SourceType { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public string? Reason { get; set; }
            public int? KnowledgeNumber { get; set; }
            public string? ArchivedPath { get; set; }
        }

        public sealed class AgentKnowledgeSyncResult
        {
            public string RootPath { get; set; } = string.Empty;
            public string ReadmePath { get; set; } = string.Empty;
            public int ScopesScanned { get; set; }
            public int FilesScanned { get; set; }
            public int Created { get; set; }
            public int Skipped { get; set; }
            public int Failed { get; set; }
            public List<AgentKnowledgeSyncDetail> Details { get; set; } = new();
        }

        private sealed class QualificationFolderInfo
        {
            public string Path { get; set; } = string.Empty;
            public string FolderName { get; set; } = string.Empty;
            public string RawQualificationCode { get; set; } = string.Empty;
            public string RawQualificationDescription { get; set; } = string.Empty;
            public string QualificationCode { get; set; } = string.Empty;
            public string QualificationDescription { get; set; } = string.Empty;
            public int FileCount { get; set; }
        }

        private sealed class DeveloperUploadRunStats
        {
            public string QualificationCode { get; set; } = string.Empty;
            public string QualificationDescription { get; set; } = string.Empty;
            public string QualificationRootPath { get; set; } = string.Empty;
            public int Scanned { get; set; }
            public int Uploaded { get; set; }
            public int Skipped { get; set; }
            public int Failed { get; set; }
        }

        private sealed class TopicCoverageProbe
        {
            public int TopicId { get; set; }
            public string TopicCode { get; set; } = string.Empty;
            public string TopicDescription { get; set; } = string.Empty;
            public string SubjectCode { get; set; } = string.Empty;
            public string SubjectDescription { get; set; } = string.Empty;
            public List<string> Tokens { get; set; } = new();
            public List<TopicCoverageMatch> Matches { get; set; } = new();
        }

        private sealed class TopicCoverageMatch
        {
            public int MaterialId { get; set; }
            public string Title { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public int KnowledgeNumber { get; set; }
            public DateTime UploadedAtUtc { get; set; }
            public int Score { get; set; }
        }

        private sealed class ResourceCoverageCandidate
        {
            public int MaterialId { get; set; }
            public string Title { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public int KnowledgeNumber { get; set; }
            public DateTime UploadedAtUtc { get; set; }
            public string SearchText { get; set; } = string.Empty;
        }

        public KnowledgeHierarchyService(
            ApplicationDbContext context,
            ILogger<KnowledgeHierarchyService> logger,
            OcrExtractionService ocrExtractionService,
            PdfVisualExtractionService pdfVisualExtractionService)
        {
            _context = context;
            _logger = logger;
            _ocrExtractionService = ocrExtractionService;
            _pdfVisualExtractionService = pdfVisualExtractionService;
        }

        public string GetHierarchyRootPath()
        {
            return Path.Combine(AiRuntime.GetLocalLibraryPath(), "KnowledgeHierarchy");
        }

        public string GetCurriculumLibraryRootPath()
        {
            return EtdpPaths.GetImportsRoot();
        }

        public string GetUploadReadmePath()
        {
            return Path.Combine(GetHierarchyRootPath(), "upload.readme.md");
        }

        public string GetAgentKnowledgeRootPath()
        {
            return Path.Combine(AiRuntime.GetLocalLibraryPath(), "AgentKnowledge");
        }

        public string GetAgentKnowledgeReadmePath()
        {
            return Path.Combine(GetAgentKnowledgeRootPath(), "readme.md");
        }

        public string EnsureAgentKnowledgeReadme()
        {
            var root = GetAgentKnowledgeRootPath();
            Directory.CreateDirectory(root);

            var path = GetAgentKnowledgeReadmePath();
            var content = BuildAgentKnowledgeReadme(root);
            try
            {
                if (File.Exists(path))
                {
                    var existingContent = File.ReadAllText(path, Encoding.UTF8);
                    if (string.Equals(existingContent, content, StringComparison.Ordinal))
                    {
                        return path;
                    }
                }

                File.WriteAllText(path, content, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (File.Exists(path))
                {
                    _logger.LogWarning(ex, "Unable to refresh AgentKnowledge readme at {Path}; continuing with existing file.", path);
                    return path;
                }

                _logger.LogWarning(ex, "Unable to create AgentKnowledge readme at {Path}; continuing without rewriting it.", path);
            }

            return path;
        }

        public string EnsureUploadReadme()
        {
            var root = GetHierarchyRootPath();
            Directory.CreateDirectory(root);

            var path = GetUploadReadmePath();
            var content = BuildUploadReadme(root);
            try
            {
                if (File.Exists(path))
                {
                    var existingContent = File.ReadAllText(path, Encoding.UTF8);
                    if (string.Equals(existingContent, content, StringComparison.Ordinal))
                    {
                        return path;
                    }
                }

                File.WriteAllText(path, content, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (File.Exists(path))
                {
                    _logger.LogWarning(ex, "Unable to refresh KnowledgeHierarchy upload readme at {Path}; continuing with existing file.", path);
                    return path;
                }

                _logger.LogWarning(ex, "Unable to create KnowledgeHierarchy upload readme at {Path}; continuing without rewriting it.", path);
            }
            return path;
        }

        public Dictionary<string, AgentKnowledgeStructureInfo> EnsureAgentKnowledgeStructures()
        {
            var scopes = new[] { "shared", "mira", "qwen" };
            return scopes.ToDictionary(
                scope => scope,
                scope => EnsureAgentKnowledgeStructure(scope),
                StringComparer.OrdinalIgnoreCase);
        }

        public AgentKnowledgeStructureInfo EnsureAgentKnowledgeStructure(string? scope)
        {
            var normalizedScope = NormalizeAgentKnowledgeScope(scope);
            var displayName = GetAgentKnowledgeScopeDisplayName(normalizedScope);
            var folderName = GetAgentKnowledgeScopeFolderName(normalizedScope);
            var root = GetAgentKnowledgeRootPath();
            Directory.CreateDirectory(root);

            var scopeRoot = Path.Combine(root, folderName);
            var inboxPath = Path.Combine(scopeRoot, "inbox");
            var archivePath = Path.Combine(scopeRoot, "archive");
            var duplicatePath = Path.Combine(scopeRoot, "duplicates");

            Directory.CreateDirectory(inboxPath);
            Directory.CreateDirectory(archivePath);
            Directory.CreateDirectory(duplicatePath);

            return new AgentKnowledgeStructureInfo
            {
                Scope = normalizedScope,
                DisplayName = displayName,
                SourceType = GetAgentKnowledgeSourceType(normalizedScope),
                ScopeRootPath = scopeRoot,
                InboxPath = inboxPath,
                ArchivePath = archivePath,
                DuplicatePath = duplicatePath,
                ReadmePath = EnsureAgentKnowledgeReadme()
            };
        }

        public int EnsureStructuresForKnownQualifications()
        {
            var qualifications = _context.Qualifications
                .AsNoTracking()
                .Select(q => new { q.QualificationNumber, q.QualificationDescription })
                .ToList();

            var count = 0;
            foreach (var qualification in qualifications)
            {
                var code = string.IsNullOrWhiteSpace(qualification.QualificationNumber)
                    ? "UNASSIGNED"
                    : qualification.QualificationNumber.Trim();
                var description = string.IsNullOrWhiteSpace(qualification.QualificationDescription)
                    ? "Unassigned Qualification"
                    : qualification.QualificationDescription.Trim();
                EnsureQualificationStructure(code, description);
                count++;
            }

            return count;
        }

        public StructureInfo EnsureQualificationStructure(string qualificationCode, string qualificationDescription)
        {
            var resolvedIdentity = ResolveQualificationIdentity(qualificationCode, qualificationDescription);
            var code = string.IsNullOrWhiteSpace(resolvedIdentity.qualificationCode) ? "UNASSIGNED" : resolvedIdentity.qualificationCode.Trim();
            var description = string.IsNullOrWhiteSpace(resolvedIdentity.qualificationDescription) ? "Unassigned Qualification" : resolvedIdentity.qualificationDescription.Trim();
            var safeCode = MakeSafeFilePart(code, "UNASSIGNED");
            var safeDescription = MakeSafeFilePart(description, "Unassigned_Qualification");

            var root = GetHierarchyRootPath();
            Directory.CreateDirectory(root);

            var qualificationRoot = ResolvePreferredQualificationRoot(root, safeCode, safeDescription);
            var legacyCurriculumLibrary = Path.Combine(qualificationRoot, "curriculum_library");
            var curriculumLibrary = ResolveQualificationCurriculumLibraryPath(safeCode);
            var localInbox = Path.Combine(qualificationRoot, "local_source_upload", "inbox");
            var localArchive = Path.Combine(qualificationRoot, "local_source_upload", "archive");
            var developerInbox = Path.Combine(qualificationRoot, "developer_knowledge_base", "inbox");
            var developerArchive = Path.Combine(qualificationRoot, "developer_knowledge_base", "archive");
            var developerReports = Path.Combine(qualificationRoot, "developer_knowledge_base", "reports");

            Directory.CreateDirectory(curriculumLibrary);
            Directory.CreateDirectory(localInbox);
            Directory.CreateDirectory(localArchive);
            Directory.CreateDirectory(developerInbox);
            Directory.CreateDirectory(developerArchive);
            Directory.CreateDirectory(developerReports);
            BackfillLegacyCurriculumLibrary(legacyCurriculumLibrary, curriculumLibrary);

            var readmePath = EnsureUploadReadme();
            return new StructureInfo
            {
                QualificationCode = safeCode,
                QualificationDescription = description,
                QualificationRootPath = qualificationRoot,
                CurriculumLibraryPath = curriculumLibrary,
                LocalInboxPath = localInbox,
                LocalArchivePath = localArchive,
                DeveloperInboxPath = developerInbox,
                DeveloperArchivePath = developerArchive,
                DeveloperReportsPath = developerReports,
                UploadReadmePath = readmePath
            };
        }

        public AgentKnowledgeSyncResult SyncAgentKnowledge(AgentKnowledgeSyncOptions? options = null)
        {
            var opts = options ?? new AgentKnowledgeSyncOptions();
            var result = new AgentKnowledgeSyncResult
            {
                RootPath = GetAgentKnowledgeRootPath(),
                ReadmePath = opts.RebuildReadme ? EnsureAgentKnowledgeReadme() : GetAgentKnowledgeReadmePath()
            };

            var root = result.RootPath;
            Directory.CreateDirectory(root);

            var normalizedScope = NormalizeAgentKnowledgeScope(opts.Scope, "all");
            var scopes = new List<string>();
            if (string.Equals(normalizedScope, "all", StringComparison.OrdinalIgnoreCase))
            {
                scopes.AddRange(new[] { "shared", "mira", "gemma" });
            }
            else
            {
                if (opts.IncludeSharedKnowledge && !string.Equals(normalizedScope, "shared", StringComparison.OrdinalIgnoreCase))
                {
                    scopes.Add("shared");
                }

                scopes.Add(normalizedScope);
            }

            var orderedScopes = scopes
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(scope => NormalizeAgentKnowledgeScope(scope))
                .ToList();
            var maxFilesPerInbox = opts.MaxFilesPerInbox <= 0 ? 1000 : Math.Min(opts.MaxFilesPerInbox, 10000);
            var nextKnowledgeNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var scope in orderedScopes)
            {
                var structure = EnsureAgentKnowledgeStructure(scope);
                result.ScopesScanned++;

                var files = Directory.GetFiles(structure.InboxPath, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
                    .Where(path => !IsImageSidecarFile(path))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Take(maxFilesPerInbox)
                    .ToList();

                foreach (var filePath in files)
                {
                    result.FilesScanned++;
                    var now = DateTime.UtcNow;
                    var originalName = Path.GetFileName(filePath);
                    var ext = Path.GetExtension(filePath).ToLowerInvariant();
                    var sidecars = ImageExtensions.Contains(ext)
                        ? FindImageSidecarPaths(filePath)
                        : new List<string>();
                    var parsedKnowledgeNumber = ParseKnowledgeNumber(originalName);
                    var cacheKey = $"global|{structure.SourceType}";
                    var next = GetNextKnowledgeNumber(cacheKey, string.Empty, structure.SourceType, nextKnowledgeNumbers);
                    var knowledgeNumber = parsedKnowledgeNumber ?? next;
                    if (!parsedKnowledgeNumber.HasValue)
                    {
                        nextKnowledgeNumbers[cacheKey] = knowledgeNumber + 1;
                    }
                    else
                    {
                        nextKnowledgeNumbers[cacheKey] = Math.Max(next, knowledgeNumber + 1);
                    }

                    var knowledgeUrl = BuildAgentKnowledgeUrl(scope, knowledgeNumber, originalName);
                    var duplicateInDatabase = _context.SourceMaterials.Any(s =>
                        string.IsNullOrWhiteSpace(s.QualificationCode) &&
                        (s.KnowledgeSourceType ?? string.Empty) == structure.SourceType &&
                        (s.Url ?? string.Empty) == knowledgeUrl);

                    if (duplicateInDatabase)
                    {
                        var duplicateDestination = EnsureUniquePath(Path.Combine(structure.DuplicatePath, $"{now:yyyyMMddHHmmss}_{originalName}"));
                        File.Move(filePath, duplicateDestination, true);
                        foreach (var sidecar in sidecars)
                        {
                            try
                            {
                                var sidecarDuplicate = EnsureUniquePath(Path.Combine(structure.DuplicatePath, $"{now:yyyyMMddHHmmss}_{Path.GetFileName(sidecar)}"));
                                File.Move(sidecar, sidecarDuplicate, true);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to move agent knowledge sidecar '{SidecarPath}' to duplicate folder.", sidecar);
                            }
                        }

                        result.Skipped++;
                        result.Details.Add(new AgentKnowledgeSyncDetail
                        {
                            Scope = scope,
                            DisplayName = structure.DisplayName,
                            SourceType = structure.SourceType,
                            FileName = originalName,
                            Status = "skipped",
                            Reason = "already_indexed",
                            KnowledgeNumber = knowledgeNumber,
                            ArchivedPath = duplicateDestination
                        });
                        continue;
                    }

                    var safeStem = MakeSafeFilePart(Path.GetFileNameWithoutExtension(originalName), $"agent_{scope}_{knowledgeNumber:D4}");
                    var archivedName = $"AGENT-KB-{knowledgeNumber:D4}_{now:yyyyMMddHHmmss}_{safeStem}{ext}";
                    var archivedPath = EnsureUniquePath(Path.Combine(structure.ArchivePath, archivedName));
                    var archivedSidecars = new List<string>();
                    foreach (var sidecar in sidecars)
                    {
                        try
                        {
                            var sidecarArchive = EnsureUniquePath(Path.Combine(structure.ArchivePath, $"{now:yyyyMMddHHmmss}_{Path.GetFileName(sidecar)}"));
                            File.Move(sidecar, sidecarArchive, true);
                            archivedSidecars.Add(sidecarArchive);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to move agent knowledge sidecar '{SidecarPath}' to archive.", sidecar);
                        }
                    }

                    File.Move(filePath, archivedPath, true);

                    try
                    {
                        var extractedText = ExtractTextFromFile(archivedPath, ext, archivedSidecars);
                        var material = new SourceMaterial
                        {
                            Title = $"[Agent KB {knowledgeNumber:D4}] {structure.DisplayName} :: {originalName}",
                            FileName = Path.GetFileName(archivedPath),
                            FilePath = archivedPath,
                            FileType = ext.TrimStart('.'),
                            Url = knowledgeUrl,
                            QualificationCode = null,
                            QualificationDescription = null,
                            SubjectDescription = $"AgentKnowledge:{structure.DisplayName}",
                            TopicDescription = $"AgentKnowledgeNumber:{knowledgeNumber:D4}",
                            AssessmentCriteriaDescription = $"UploadedAtUtc:{now:O};Source:{originalName};AgentScope:{scope}",
                            ExtractedText = extractedText,
                            KnowledgeSourceType = structure.SourceType,
                            KnowledgeNumber = knowledgeNumber,
                            KnowledgeLabel = $"{structure.DisplayName} Agent Knowledge {knowledgeNumber}: {originalName}",
                            KnowledgeRootPath = structure.ScopeRootPath,
                            KnowledgeUploadedAtUtc = now
                        };
                        _context.SourceMaterials.Add(material);
                        result.Created++;
                        result.Details.Add(new AgentKnowledgeSyncDetail
                        {
                            Scope = scope,
                            DisplayName = structure.DisplayName,
                            SourceType = structure.SourceType,
                            FileName = originalName,
                            Status = "created",
                            KnowledgeNumber = knowledgeNumber,
                            ArchivedPath = archivedPath
                        });
                    }
                    catch (Exception ex)
                    {
                        result.Failed++;
                        result.Details.Add(new AgentKnowledgeSyncDetail
                        {
                            Scope = scope,
                            DisplayName = structure.DisplayName,
                            SourceType = structure.SourceType,
                            FileName = originalName,
                            Status = "failed",
                            Reason = ex.Message,
                            KnowledgeNumber = knowledgeNumber,
                            ArchivedPath = archivedPath
                        });
                        _logger.LogWarning(ex, "Failed to index agent knowledge file '{FilePath}'", archivedPath);
                    }
                }
            }

            if (result.Created > 0)
            {
                _context.SaveChanges();
            }

            return result;
        }

        public ConsolidationResult ConsolidateLegacyQualificationFolders(ConsolidationOptions? options = null)
        {
            var opts = options ?? new ConsolidationOptions();
            var result = new ConsolidationResult
            {
                RootPath = GetHierarchyRootPath(),
                UploadReadmePath = opts.RebuildUploadReadme ? EnsureUploadReadme() : GetUploadReadmePath()
            };

            var root = result.RootPath;
            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
                return result;
            }

            var qualificationCodeFilter = NormalizeForMatch(opts.QualificationCode);
            var hasChanges = false;

            var roots = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .Select(path =>
                {
                    var folderName = Path.GetFileName(path) ?? string.Empty;
                    var parts = SplitQualificationFolderName(folderName);
                    var resolved = ResolveQualificationIdentity(parts.qualificationCode, parts.qualificationDescription);
                    return new QualificationFolderInfo
                    {
                        Path = path,
                        FolderName = folderName,
                        RawQualificationCode = parts.qualificationCode,
                        RawQualificationDescription = parts.qualificationDescription,
                        QualificationCode = resolved.qualificationCode,
                        QualificationDescription = resolved.qualificationDescription,
                        FileCount = GetDirectoryFileCount(path)
                    };
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.FolderName))
                .GroupBy(x => x.QualificationCode, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var group in roots)
            {
                var qualificationCode = group.Key;
                if (!string.IsNullOrWhiteSpace(qualificationCodeFilter) &&
                    !ContainsLoose(NormalizeForMatch(qualificationCode), qualificationCodeFilter))
                {
                    continue;
                }

                result.QualificationGroupsScanned++;
                var folders = group.ToList();
                if (folders.Count <= 1)
                {
                    continue;
                }

                var preferred = SelectPreferredQualificationRoot(qualificationCode, folders);
                if (preferred == null) continue;

                var legacyFolders = folders
                    .Where(x => !string.Equals(x.Path, preferred.Path, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.FileCount)
                    .ThenBy(x => x.FolderName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var legacy in legacyFolders)
                {
                    var movedCount = 0;
                    var updatedCount = 0;
                    movedCount += ConsolidateLegacySourceType(
                        qualificationCode,
                        preferred.QualificationDescription,
                        "local_source_upload",
                        legacy.Path,
                        preferred.Path,
                        ref updatedCount);
                    movedCount += ConsolidateLegacySourceType(
                        qualificationCode,
                        preferred.QualificationDescription,
                        "developer_knowledge_base",
                        legacy.Path,
                        preferred.Path,
                        ref updatedCount);

                    if (movedCount > 0 || updatedCount > 0)
                    {
                        hasChanges = true;
                    }

                    result.LegacyFoldersProcessed++;
                    result.FilesMoved += movedCount;
                    result.MaterialsUpdated += updatedCount;
                    result.ConsolidatedFolders.Add($"{legacy.FolderName} => {preferred.FolderName}");

                    if (opts.RemoveEmptyLegacyFolders && IsFolderTreeEmpty(legacy.Path))
                    {
                        try
                        {
                            Directory.Delete(legacy.Path, true);
                            result.EmptyLegacyFoldersRemoved++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to remove empty legacy folder '{LegacyPath}'", legacy.Path);
                        }
                    }
                }
            }

            if (hasChanges)
            {
                _context.SaveChanges();
            }

            return result;
        }

        public SyncResult SyncKnowledgeHierarchy(SyncOptions? options = null)
        {
            var opts = options ?? new SyncOptions();
            var result = new SyncResult
            {
                RootPath = GetHierarchyRootPath(),
                UploadReadmePath = opts.RebuildUploadReadme ? EnsureUploadReadme() : GetUploadReadmePath()
            };

            var root = result.RootPath;
            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
                return result;
            }

            if (opts.ConsolidateLegacyFolders)
            {
                try
                {
                    ConsolidateLegacyQualificationFolders(new ConsolidationOptions
                    {
                        QualificationCode = opts.QualificationCode,
                        RebuildUploadReadme = false,
                        RemoveEmptyLegacyFolders = true
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Knowledge hierarchy consolidation failed before sync.");
                }
            }

            var qualificationCodeFilter = NormalizeForMatch(opts.QualificationCode);
            var qualificationDescriptionFilter = NormalizeForMatch(opts.QualificationDescription);
            var maxFilesPerInbox = opts.MaxFilesPerInbox <= 0 ? 1000 : Math.Min(opts.MaxFilesPerInbox, 10000);
            var nextKnowledgeNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var developerRunStats = new Dictionary<string, DeveloperUploadRunStats>(StringComparer.OrdinalIgnoreCase);

            var qualificationRoots = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .Select(path =>
                {
                    var folderName = Path.GetFileName(path) ?? string.Empty;
                    var parts = SplitQualificationFolderName(folderName);
                    var resolved = ResolveQualificationIdentity(parts.qualificationCode, parts.qualificationDescription);
                    return new QualificationFolderInfo
                    {
                        Path = path,
                        FolderName = folderName,
                        RawQualificationCode = parts.qualificationCode,
                        RawQualificationDescription = parts.qualificationDescription,
                        QualificationCode = resolved.qualificationCode,
                        QualificationDescription = resolved.qualificationDescription,
                        FileCount = GetDirectoryFileCount(path)
                    };
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.FolderName))
                .OrderBy(x => x.QualificationCode, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(x => x.FileCount)
                .ThenBy(x => x.FolderName, StringComparer.OrdinalIgnoreCase)
                .GroupBy(x => x.QualificationCode, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(x => x.FolderName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var folder in qualificationRoots)
            {
                var qualificationCode = folder.QualificationCode;
                var qualificationDescription = ResolveQualificationDescription(qualificationCode, folder.QualificationDescription);
                if (string.IsNullOrWhiteSpace(qualificationDescription))
                {
                    qualificationDescription = folder.QualificationDescription;
                }
                var structure = EnsureQualificationStructure(qualificationCode, qualificationDescription);
                var qualificationRoot = structure.QualificationRootPath;
                MirrorCurriculumLibraryKnowledgeAliases(structure, maxFilesPerInbox);
                var qualificationKey = $"{qualificationCode}|{NormalizeForMatch(qualificationDescription)}";

                var hasCodeFilter = !string.IsNullOrWhiteSpace(qualificationCodeFilter);
                var hasDescriptionFilter = !string.IsNullOrWhiteSpace(qualificationDescriptionFilter);
                if (hasCodeFilter)
                {
                    // Code is the stable identifier; when provided, do not exclude on description mismatch.
                    if (!ContainsLoose(NormalizeForMatch(qualificationCode), qualificationCodeFilter))
                    {
                        continue;
                    }
                }
                else if (hasDescriptionFilter &&
                         !ContainsLoose(NormalizeForMatch(qualificationDescription), qualificationDescriptionFilter))
                {
                    continue;
                }

                result.QualificationsScanned++;

                var sourceTypes = new List<string>();
                if (opts.IncludeLocalSourceUploads) sourceTypes.Add("local_source_upload");
                if (opts.IncludeDeveloperKnowledgeBase) sourceTypes.Add("developer_knowledge_base");

                foreach (var sourceType in sourceTypes)
                {
                    var sourceRoot = Path.Combine(qualificationRoot, sourceType);
                    var inboxPath = Path.Combine(sourceRoot, "inbox");
                    var archivePath = Path.Combine(sourceRoot, "archive");
                    var duplicatePath = Path.Combine(sourceRoot, "duplicates");
                    if (!Directory.Exists(inboxPath))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(archivePath);
                    Directory.CreateDirectory(duplicatePath);
                    if (string.Equals(sourceType, "developer_knowledge_base", StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(Path.Combine(sourceRoot, "reports"));
                    }

                    if (string.Equals(sourceType, "developer_knowledge_base", StringComparison.OrdinalIgnoreCase) &&
                        !developerRunStats.TryGetValue(qualificationKey, out var runStats))
                    {
                        runStats = new DeveloperUploadRunStats
                        {
                            QualificationCode = qualificationCode,
                            QualificationDescription = qualificationDescription,
                            QualificationRootPath = qualificationRoot
                        };
                        developerRunStats[qualificationKey] = runStats;
                    }

                    var files = Directory.GetFiles(inboxPath, "*", SearchOption.TopDirectoryOnly)
                        .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
                        .Where(path => !IsImageSidecarFile(path))
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .Take(maxFilesPerInbox)
                        .ToList();

                    if (string.Equals(sourceType, "developer_knowledge_base", StringComparison.OrdinalIgnoreCase) &&
                        developerRunStats.TryGetValue(qualificationKey, out var statsForScan))
                    {
                        statsForScan.Scanned += files.Count;
                    }

                    foreach (var filePath in files)
                    {
                        result.FilesScanned++;
                        var now = DateTime.UtcNow;
                        var originalName = Path.GetFileName(filePath);
                        var ext = Path.GetExtension(filePath).ToLowerInvariant();
                        var sidecars = ImageExtensions.Contains(ext)
                            ? FindImageSidecarPaths(filePath)
                            : new List<string>();
                        var parsedKnowledgeNumber = ParseKnowledgeNumber(originalName);
                        var key = $"{qualificationCode}|{sourceType}";
                        var next = GetNextKnowledgeNumber(key, qualificationCode, sourceType, nextKnowledgeNumbers);
                        var knowledgeNumber = parsedKnowledgeNumber ?? next;
                        if (!parsedKnowledgeNumber.HasValue)
                        {
                            nextKnowledgeNumbers[key] = knowledgeNumber + 1;
                        }
                        else
                        {
                            nextKnowledgeNumbers[key] = Math.Max(next, knowledgeNumber + 1);
                        }

                        var knowledgeUrl = BuildKnowledgeUrl(qualificationCode, sourceType, knowledgeNumber, originalName);
                        var duplicateInDatabase = _context.SourceMaterials.Any(s =>
                            (s.QualificationCode ?? string.Empty) == qualificationCode &&
                            (s.KnowledgeSourceType ?? string.Empty) == sourceType &&
                            (s.Url ?? string.Empty) == knowledgeUrl);

                        if (duplicateInDatabase)
                        {
                            var duplicateDestination = EnsureUniquePath(Path.Combine(duplicatePath, $"{now:yyyyMMddHHmmss}_{originalName}"));
                            File.Move(filePath, duplicateDestination, true);
                            foreach (var sidecar in sidecars)
                            {
                                try
                                {
                                    var sidecarDuplicate = EnsureUniquePath(Path.Combine(duplicatePath, $"{now:yyyyMMddHHmmss}_{Path.GetFileName(sidecar)}"));
                                    File.Move(sidecar, sidecarDuplicate, true);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to move sidecar '{SidecarPath}' to duplicate folder.", sidecar);
                                }
                            }
                            result.Skipped++;
                            result.Details.Add(new SyncDetail
                            {
                                QualificationCode = qualificationCode,
                                QualificationDescription = qualificationDescription,
                                SourceType = sourceType,
                                FileName = originalName,
                                Status = "skipped",
                                Reason = "already_indexed",
                                KnowledgeNumber = knowledgeNumber,
                                ArchivedPath = duplicateDestination
                            });
                            if (string.Equals(sourceType, "developer_knowledge_base", StringComparison.OrdinalIgnoreCase) &&
                                developerRunStats.TryGetValue(qualificationKey, out var statsForSkip))
                            {
                                statsForSkip.Skipped++;
                            }
                            continue;
                        }

                        var safeStem = MakeSafeFilePart(Path.GetFileNameWithoutExtension(originalName), $"source_{knowledgeNumber:D4}");
                        var archivedName = $"KB-{knowledgeNumber:D4}_{now:yyyyMMddHHmmss}_{safeStem}{ext}";
                        var archivedPath = EnsureUniquePath(Path.Combine(archivePath, archivedName));
                        var archivedSidecars = new List<string>();
                        foreach (var sidecar in sidecars)
                        {
                            try
                            {
                                var sidecarArchive = EnsureUniquePath(Path.Combine(archivePath, $"{now:yyyyMMddHHmmss}_{Path.GetFileName(sidecar)}"));
                                File.Move(sidecar, sidecarArchive, true);
                                archivedSidecars.Add(sidecarArchive);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to move sidecar '{SidecarPath}' to archive.", sidecar);
                            }
                        }
                        File.Move(filePath, archivedPath, true);

                        try
                        {
                            var extractedText = ExtractTextFromFile(archivedPath, ext, archivedSidecars);
                            var derivedVisuals = ext == ".pdf"
                                ? ExtractDerivedPdfVisuals(
                                    archivedPath,
                                    originalName,
                                    qualificationCode,
                                    qualificationDescription,
                                    sourceType,
                                    sourceRoot,
                                    knowledgeUrl,
                                    key,
                                    nextKnowledgeNumbers)
                                : new DerivedPdfVisualResult();
                            extractedText = AppendPdfVisualSummary(extractedText, derivedVisuals.SummaryText);
                            var material = new SourceMaterial
                            {
                                Title = $"[KB {knowledgeNumber:D4}] {qualificationCode} - {qualificationDescription} :: {originalName}",
                                FileName = Path.GetFileName(archivedPath),
                                FilePath = archivedPath,
                                FileType = ext.TrimStart('.'),
                                Url = knowledgeUrl,
                                QualificationCode = qualificationCode,
                                QualificationDescription = qualificationDescription,
                                SubjectDescription = $"KnowledgeBase:{sourceType}",
                                TopicDescription = $"KnowledgeNumber:{knowledgeNumber:D4}",
                                AssessmentCriteriaDescription = $"UploadedAtUtc:{now:O};Source:{originalName}",
                                ExtractedText = extractedText,
                                KnowledgeSourceType = sourceType,
                                KnowledgeNumber = knowledgeNumber,
                                KnowledgeLabel = $"Knowledge Base {knowledgeNumber}: {originalName}",
                                KnowledgeRootPath = sourceRoot,
                                KnowledgeUploadedAtUtc = now
                            };
                            _context.SourceMaterials.Add(material);
                            if (derivedVisuals.Materials.Count > 0)
                            {
                                _context.SourceMaterials.AddRange(derivedVisuals.Materials);
                                result.Created += derivedVisuals.Materials.Count;
                                foreach (var visualMaterial in derivedVisuals.Materials)
                                {
                                    result.Details.Add(new SyncDetail
                                    {
                                        QualificationCode = qualificationCode,
                                        QualificationDescription = qualificationDescription,
                                        SourceType = sourceType,
                                        FileName = visualMaterial.FileName,
                                        Status = "created",
                                        Reason = "pdf_visual_extracted",
                                        KnowledgeNumber = visualMaterial.KnowledgeNumber,
                                        ArchivedPath = visualMaterial.FilePath
                                    });
                                }
                            }
                            result.Created++;
                            if (string.Equals(sourceType, "developer_knowledge_base", StringComparison.OrdinalIgnoreCase) &&
                                developerRunStats.TryGetValue(qualificationKey, out var statsForCreated))
                            {
                                statsForCreated.Uploaded++;
                            }
                            result.Details.Add(new SyncDetail
                            {
                                QualificationCode = qualificationCode,
                                QualificationDescription = qualificationDescription,
                                SourceType = sourceType,
                                FileName = originalName,
                                Status = "created",
                                KnowledgeNumber = knowledgeNumber,
                                ArchivedPath = archivedPath
                            });
                        }
                        catch (Exception ex)
                        {
                            result.Failed++;
                            if (string.Equals(sourceType, "developer_knowledge_base", StringComparison.OrdinalIgnoreCase) &&
                                developerRunStats.TryGetValue(qualificationKey, out var statsForFailure))
                            {
                                statsForFailure.Failed++;
                            }
                            result.Details.Add(new SyncDetail
                            {
                                QualificationCode = qualificationCode,
                                QualificationDescription = qualificationDescription,
                                SourceType = sourceType,
                                FileName = originalName,
                                Status = "failed",
                                Reason = ex.Message,
                                KnowledgeNumber = knowledgeNumber,
                                ArchivedPath = archivedPath
                            });
                            _logger.LogWarning(ex, "Failed to index knowledge file '{FilePath}'", archivedPath);
                        }
                    }
                }
            }

            if (result.Created > 0)
            {
                _context.SaveChanges();
            }

            foreach (var run in developerRunStats.Values.Where(x => x.Scanned > 0))
            {
                try
                {
                    var report = GenerateDeveloperCoverageReport(new CoverageReportOptions
                    {
                        QualificationCode = run.QualificationCode,
                        QualificationDescription = run.QualificationDescription,
                        QualificationRootPath = run.QualificationRootPath,
                        UploadedInRun = run.Uploaded,
                        SkippedInRun = run.Skipped,
                        FailedInRun = run.Failed
                    });

                    if (report != null)
                    {
                        result.CoverageReports.Add(report);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to generate developer knowledge coverage report for qualification '{QualificationCode}'",
                        run.QualificationCode);
                }
            }

            return result;
        }

        public CoverageReportSummary? GenerateDeveloperCoverageReport(CoverageReportOptions? options)
        {
            if (options == null) return null;

            var qualificationCode = (options.QualificationCode ?? string.Empty).Trim();
            var qualificationDescription = (options.QualificationDescription ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(qualificationCode) && string.IsNullOrWhiteSpace(qualificationDescription))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(qualificationCode) && !string.IsNullOrWhiteSpace(qualificationDescription))
            {
                qualificationCode = _context.Qualifications
                    .AsNoTracking()
                    .Where(q => q.QualificationDescription == qualificationDescription)
                    .Select(q => q.QualificationNumber)
                    .FirstOrDefault() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(qualificationDescription) && !string.IsNullOrWhiteSpace(qualificationCode))
            {
                qualificationDescription = ResolveQualificationDescription(qualificationCode, qualificationCode);
            }

            var rootPath = (options.QualificationRootPath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                var scaffold = EnsureQualificationStructure(qualificationCode, qualificationDescription);
                rootPath = scaffold.QualificationRootPath;
                if (string.IsNullOrWhiteSpace(qualificationCode))
                {
                    qualificationCode = scaffold.QualificationCode;
                }
                if (string.IsNullOrWhiteSpace(qualificationDescription))
                {
                    qualificationDescription = scaffold.QualificationDescription;
                }
            }

            return BuildDeveloperCoverageReport(
                qualificationCode,
                qualificationDescription,
                rootPath,
                options.UploadedInRun,
                options.SkippedInRun,
                options.FailedInRun);
        }

        private CoverageReportSummary? BuildDeveloperCoverageReport(
            string qualificationCode,
            string qualificationDescription,
            string qualificationRootPath,
            int uploadedInRun,
            int skippedInRun,
            int failedInRun)
        {
            var code = (qualificationCode ?? string.Empty).Trim();
            var description = (qualificationDescription ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(description))
            {
                return null;
            }

            var topicsQuery =
                from topic in _context.Topics.AsNoTracking()
                join subject in _context.Subjects.AsNoTracking() on topic.SubjectId equals subject.Id
                join qualification in _context.Qualifications.AsNoTracking() on subject.QualificationId equals qualification.Id
                select new
                {
                    topic.Id,
                    topic.TopicCode,
                    topic.TopicDescription,
                    subject.SubjectCode,
                    subject.SubjectDescription,
                    qualification.QualificationNumber,
                    qualification.QualificationDescription
                };

            if (!string.IsNullOrWhiteSpace(code))
            {
                topicsQuery = topicsQuery.Where(x => x.QualificationNumber == code);
            }
            else if (!string.IsNullOrWhiteSpace(description))
            {
                topicsQuery = topicsQuery.Where(x => x.QualificationDescription == description);
            }

            var topics = topicsQuery
                .OrderBy(x => x.SubjectCode)
                .ThenBy(x => x.TopicCode)
                .ThenBy(x => x.Id)
                .ToList()
                .Select(x => new TopicCoverageProbe
                {
                    TopicId = x.Id,
                    TopicCode = x.TopicCode ?? string.Empty,
                    TopicDescription = x.TopicDescription ?? string.Empty,
                    SubjectCode = x.SubjectCode ?? string.Empty,
                    SubjectDescription = x.SubjectDescription ?? string.Empty,
                    Tokens = BuildCoverageTokens(
                        x.TopicCode ?? string.Empty,
                        x.TopicDescription ?? string.Empty,
                        x.SubjectDescription ?? string.Empty)
                })
                .ToList();

            if (string.IsNullOrWhiteSpace(code) && topics.Count > 0)
            {
                code = topicsQuery
                    .Select(x => x.QualificationNumber)
                    .FirstOrDefault() ?? code;
            }

            var materialsQuery = _context.SourceMaterials
                .AsNoTracking()
                .Where(s => (s.KnowledgeSourceType ?? string.Empty) == "developer_knowledge_base");

            if (!string.IsNullOrWhiteSpace(code))
            {
                materialsQuery = materialsQuery.Where(s => (s.QualificationCode ?? string.Empty) == code);
            }
            else if (!string.IsNullOrWhiteSpace(description))
            {
                materialsQuery = materialsQuery.Where(s => (s.QualificationDescription ?? string.Empty) == description);
            }

            var materialRows = materialsQuery
                .OrderByDescending(s => s.KnowledgeUploadedAtUtc ?? s.CreatedAt)
                .Select(s => new
                {
                    s.Id,
                    s.Title,
                    s.FileName,
                    s.Url,
                    s.KnowledgeNumber,
                    UploadedAtUtc = s.KnowledgeUploadedAtUtc ?? s.CreatedAt,
                    s.ExtractedText,
                    s.KnowledgeLabel,
                    s.AssessmentCriteriaDescription,
                    s.TopicDescription
                })
                .Take(5000)
                .ToList();

            var resources = materialRows
                .Select(x => new ResourceCoverageCandidate
                {
                    MaterialId = x.Id,
                    Title = x.Title ?? string.Empty,
                    FileName = x.FileName ?? string.Empty,
                    Url = x.Url ?? string.Empty,
                    KnowledgeNumber = x.KnowledgeNumber ?? 0,
                    UploadedAtUtc = x.UploadedAtUtc,
                    SearchText = BuildResourceCoverageSearchText(
                        x.Title,
                        x.KnowledgeLabel,
                        x.TopicDescription,
                        x.AssessmentCriteriaDescription,
                        x.ExtractedText)
                })
                .ToList();

            foreach (var topic in topics)
            {
                var matches = new List<TopicCoverageMatch>();
                foreach (var resource in resources)
                {
                    var score = ScoreTopicCoverage(topic, resource.SearchText);
                    if (score < 3) continue;

                    matches.Add(new TopicCoverageMatch
                    {
                        MaterialId = resource.MaterialId,
                        Title = resource.Title,
                        FileName = resource.FileName,
                        Url = resource.Url,
                        KnowledgeNumber = resource.KnowledgeNumber,
                        UploadedAtUtc = resource.UploadedAtUtc,
                        Score = score
                    });
                }

                topic.Matches = matches
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => x.UploadedAtUtc)
                    .Take(3)
                    .ToList();
            }

            var coveredCount = topics.Count(x => x.Matches.Count > 0);
            var missing = topics
                .Where(x => x.Matches.Count == 0)
                .Select(x =>
                {
                    var topicLabel = string.IsNullOrWhiteSpace(x.TopicDescription) ? "(No description)" : x.TopicDescription;
                    return $"[{x.SubjectCode}] {x.TopicCode} - {topicLabel}".Trim();
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var generatedAtUtc = DateTime.UtcNow;
            var safeCode = MakeSafeFilePart(code, "UNASSIGNED");
            var safeDescription = MakeSafeFilePart(description, "Unassigned_Qualification");
            var rootPath = ResolvePreferredQualificationRoot(GetHierarchyRootPath(), safeCode, safeDescription);
            if (!string.IsNullOrWhiteSpace(qualificationRootPath) && Directory.Exists(qualificationRootPath))
            {
                rootPath = qualificationRootPath;
            }

            var reportsDirectory = Path.Combine(rootPath, "developer_knowledge_base", "reports");
            Directory.CreateDirectory(reportsDirectory);

            var stamp = generatedAtUtc.ToString("yyyyMMdd_HHmmss");
            var baseName = $"{safeCode}_developer_coverage_{stamp}";
            var markdownPath = EnsureUniquePath(Path.Combine(reportsDirectory, $"{baseName}.md"));
            var textPath = EnsureUniquePath(Path.Combine(reportsDirectory, $"{baseName}.txt"));

            var summary = new CoverageReportSummary
            {
                QualificationCode = code,
                QualificationDescription = description,
                GeneratedAtUtc = generatedAtUtc,
                SourceType = "developer_knowledge_base",
                ReportsDirectory = reportsDirectory,
                MarkdownPath = markdownPath,
                TextPath = textPath,
                DeveloperResourcesConsidered = resources.Count,
                TopicsTotal = topics.Count,
                TopicsCovered = coveredCount,
                TopicsMissing = Math.Max(0, topics.Count - coveredCount),
                UploadedInRun = Math.Max(0, uploadedInRun),
                SkippedInRun = Math.Max(0, skippedInRun),
                FailedInRun = Math.Max(0, failedInRun),
                MissingTopics = missing.Take(200).ToList()
            };

            var markdown = BuildCoverageReportMarkdown(summary, topics);
            var text = BuildCoverageReportText(summary, topics);
            File.WriteAllText(markdownPath, markdown, Encoding.UTF8);
            File.WriteAllText(textPath, text, Encoding.UTF8);

            return summary;
        }

        private static string BuildResourceCoverageSearchText(
            string? title,
            string? label,
            string? topic,
            string? criteria,
            string? extractedText)
        {
            var body = extractedText ?? string.Empty;
            if (body.Length > 24000)
            {
                body = body.Substring(0, 24000);
            }

            var joined = string.Join("\n", new[]
            {
                title ?? string.Empty,
                label ?? string.Empty,
                topic ?? string.Empty,
                criteria ?? string.Empty,
                body
            });

            var normalized = NormalizeCoverageText(joined);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }
            return $" {normalized} ";
        }

        private static int ScoreTopicCoverage(TopicCoverageProbe topic, string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText)) return 0;

            var score = 0;
            var topicCode = NormalizeCoverageText(topic.TopicCode);
            if (!string.IsNullOrWhiteSpace(topicCode) &&
                searchText.Contains($" {topicCode} ", StringComparison.Ordinal))
            {
                score += 4;
            }

            var topicPhrase = NormalizeCoverageText(topic.TopicDescription);
            if (topicPhrase.Length >= 10 &&
                searchText.Contains($" {topicPhrase} ", StringComparison.Ordinal))
            {
                score += 6;
            }

            var subjectPhrase = NormalizeCoverageText(topic.SubjectDescription);
            if (subjectPhrase.Length >= 10 &&
                searchText.Contains($" {subjectPhrase} ", StringComparison.Ordinal))
            {
                score += 3;
            }

            foreach (var token in topic.Tokens.Take(14))
            {
                if (searchText.Contains($" {token} ", StringComparison.Ordinal))
                {
                    score += 1;
                }
            }

            return score;
        }

        private static List<string> BuildCoverageTokens(string topicCode, string topicDescription, string subjectDescription)
        {
            var merged = $"{topicCode} {topicDescription} {subjectDescription}";
            var normalized = NormalizeCoverageText(merged).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return new List<string>();
            }

            return normalized
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token.Length >= 4 || token.Any(char.IsDigit))
                .Where(token => !CoverageStopWords.Contains(token))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();
        }

        private static string NormalizeCoverageText(string? value)
        {
            var source = (value ?? string.Empty).ToLowerInvariant();
            var normalized = Regex.Replace(source, @"[^a-z0-9]+", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            return normalized;
        }

        private static string BuildCoverageReportMarkdown(CoverageReportSummary summary, IReadOnlyList<TopicCoverageProbe> topics)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Developer Knowledge Coverage Report");
            sb.AppendLine();
            sb.AppendLine($"- Generated (UTC): {summary.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"- Qualification: {summary.QualificationCode} - {summary.QualificationDescription}".TrimEnd(' ', '-'));
            sb.AppendLine($"- Source Type: {summary.SourceType}");
            sb.AppendLine();
            sb.AppendLine("## Upload Run Summary");
            sb.AppendLine();
            sb.AppendLine($"- Uploaded in this run: {summary.UploadedInRun}");
            sb.AppendLine($"- Skipped in this run: {summary.SkippedInRun}");
            sb.AppendLine($"- Failed in this run: {summary.FailedInRun}");
            sb.AppendLine();
            sb.AppendLine("## Coverage Summary");
            sb.AppendLine();
            sb.AppendLine($"- Developer resources considered: {summary.DeveloperResourcesConsidered}");
            sb.AppendLine($"- Topics total: {summary.TopicsTotal}");
            sb.AppendLine($"- Topics covered: {summary.TopicsCovered}");
            sb.AppendLine($"- Topics missing: {summary.TopicsMissing}");
            sb.AppendLine();
            sb.AppendLine("## Content Unavailable Per Topic");
            sb.AppendLine();
            if (summary.MissingTopics.Count == 0)
            {
                sb.AppendLine("- None. All topics have at least one mapped developer resource.");
            }
            else
            {
                foreach (var missing in summary.MissingTopics)
                {
                    sb.AppendLine($"- {missing}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("## Topic to Resource Mapping");
            sb.AppendLine();
            foreach (var topic in topics)
            {
                var topicLabel = string.IsNullOrWhiteSpace(topic.TopicDescription) ? "(No description)" : topic.TopicDescription;
                sb.AppendLine($"### [{topic.SubjectCode}] {topic.TopicCode} - {topicLabel}".Trim());
                sb.AppendLine($"- Subject: {topic.SubjectDescription}");
                sb.AppendLine($"- Status: {(topic.Matches.Count > 0 ? "Covered" : "Missing")}");
                if (topic.Matches.Count == 0)
                {
                    sb.AppendLine("- Matches: none");
                }
                else
                {
                    foreach (var match in topic.Matches)
                    {
                        sb.AppendLine($"- KB #{match.KnowledgeNumber:D4} | Score {match.Score} | {match.Title} | Uploaded {match.UploadedAtUtc:yyyy-MM-dd HH:mm} UTC");
                    }
                }
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        private static string BuildCoverageReportText(CoverageReportSummary summary, IReadOnlyList<TopicCoverageProbe> topics)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Developer Knowledge Coverage Report");
            sb.AppendLine(new string('=', 34));
            sb.AppendLine($"Generated (UTC): {summary.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Qualification: {summary.QualificationCode} - {summary.QualificationDescription}".TrimEnd(' ', '-'));
            sb.AppendLine($"Source Type: {summary.SourceType}");
            sb.AppendLine();
            sb.AppendLine("Upload Run Summary");
            sb.AppendLine($"Uploaded: {summary.UploadedInRun}");
            sb.AppendLine($"Skipped: {summary.SkippedInRun}");
            sb.AppendLine($"Failed: {summary.FailedInRun}");
            sb.AppendLine();
            sb.AppendLine("Coverage Summary");
            sb.AppendLine($"Developer resources considered: {summary.DeveloperResourcesConsidered}");
            sb.AppendLine($"Topics total: {summary.TopicsTotal}");
            sb.AppendLine($"Topics covered: {summary.TopicsCovered}");
            sb.AppendLine($"Topics missing: {summary.TopicsMissing}");
            sb.AppendLine();
            sb.AppendLine("Content Unavailable Per Topic");
            if (summary.MissingTopics.Count == 0)
            {
                sb.AppendLine("- None");
            }
            else
            {
                foreach (var missing in summary.MissingTopics)
                {
                    sb.AppendLine($"- {missing}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("Topic to Resource Mapping");
            foreach (var topic in topics)
            {
                var topicLabel = string.IsNullOrWhiteSpace(topic.TopicDescription) ? "(No description)" : topic.TopicDescription;
                sb.AppendLine($"[{topic.SubjectCode}] {topic.TopicCode} - {topicLabel}".Trim());
                sb.AppendLine($"Subject: {topic.SubjectDescription}");
                sb.AppendLine($"Status: {(topic.Matches.Count > 0 ? "Covered" : "Missing")}");
                if (topic.Matches.Count == 0)
                {
                    sb.AppendLine("Matches: none");
                }
                else
                {
                    foreach (var match in topic.Matches)
                    {
                        sb.AppendLine($"Match: KB #{match.KnowledgeNumber:D4} | Score {match.Score} | {match.Title} | Uploaded {match.UploadedAtUtc:yyyy-MM-dd HH:mm} UTC");
                    }
                }
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        private int GetNextKnowledgeNumber(
            string cacheKey,
            string qualificationCode,
            string sourceType,
            IDictionary<string, int> cache)
        {
            if (cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var max = _context.SourceMaterials
                .Where(s => (s.QualificationCode ?? string.Empty) == qualificationCode &&
                            (s.KnowledgeSourceType ?? string.Empty) == sourceType)
                .Max(s => (int?)s.KnowledgeNumber) ?? 0;

            var next = max + 1;
            cache[cacheKey] = next;
            return next;
        }

        private (string qualificationCode, string qualificationDescription) ResolveQualificationIdentity(string qualificationCode, string qualificationDescription)
        {
            var rawCode = (qualificationCode ?? string.Empty).Trim();
            var rawDescription = (qualificationDescription ?? string.Empty).Replace("_", " ").Trim();
            var qualifications = _context.Qualifications
                .AsNoTracking()
                .ToList();

            Qualification? match = null;
            if (!string.IsNullOrWhiteSpace(rawCode))
            {
                match = qualifications.FirstOrDefault(q =>
                    string.Equals((q.QualificationNumber ?? string.Empty).Trim(), rawCode, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    var codePrefix = ExtractQualificationCodePrefix(rawCode);
                    if (!string.IsNullOrWhiteSpace(codePrefix) &&
                        !string.Equals(codePrefix, rawCode, StringComparison.OrdinalIgnoreCase))
                    {
                        match = qualifications.FirstOrDefault(q =>
                            string.Equals((q.QualificationNumber ?? string.Empty).Trim(), codePrefix, StringComparison.OrdinalIgnoreCase));
                    }
                }
            }

            if (match == null && !string.IsNullOrWhiteSpace(rawDescription))
            {
                var normalizedDescription = NormalizeQualificationIdentityText(rawDescription);
                match = qualifications.FirstOrDefault(q =>
                    string.Equals(
                        NormalizeQualificationIdentityText(q.QualificationDescription),
                        normalizedDescription,
                        StringComparison.Ordinal));

                if (match == null)
                {
                    match = qualifications.FirstOrDefault(q =>
                    {
                        var candidate = NormalizeQualificationIdentityText(q.QualificationDescription);
                        return ContainsLoose(candidate, normalizedDescription) || ContainsLoose(normalizedDescription, candidate);
                    });
                }
            }

            var resolvedCode = !string.IsNullOrWhiteSpace(match?.QualificationNumber)
                ? match!.QualificationNumber.Trim()
                : ExtractQualificationCodePrefix(rawCode);
            if (string.IsNullOrWhiteSpace(resolvedCode))
            {
                resolvedCode = rawCode;
            }

            var resolvedDescription = !string.IsNullOrWhiteSpace(match?.QualificationDescription)
                ? match!.QualificationDescription.Trim()
                : rawDescription;
            if (string.IsNullOrWhiteSpace(resolvedDescription))
            {
                resolvedDescription = resolvedCode;
            }

            return (resolvedCode, resolvedDescription);
        }

        private string ResolveQualificationDescription(string qualificationCode, string fallbackDescription)
        {
            var resolved = ResolveQualificationIdentity(qualificationCode, fallbackDescription);
            return string.IsNullOrWhiteSpace(resolved.qualificationDescription)
                ? qualificationCode
                : resolved.qualificationDescription;
        }

        private static (string qualificationCode, string qualificationDescription) SplitQualificationFolderName(string folderName)
        {
            var name = folderName.Trim();
            var idx = name.IndexOf('_');
            if (idx <= 0)
            {
                return (name, name);
            }

            var code = name.Substring(0, idx).Trim();
            var description = name.Substring(idx + 1).Trim();
            if (string.IsNullOrWhiteSpace(description))
            {
                description = code;
            }
            return (code, description);
        }

        private static string ExtractQualificationCodePrefix(string? qualificationCode)
        {
            var raw = (qualificationCode ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            var numericPrefix = Regex.Match(raw, @"^\d{3,}");
            if (numericPrefix.Success)
            {
                return numericPrefix.Value;
            }

            return raw
                .Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? raw;
        }

        private static string NormalizeQualificationIdentityText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var normalized = Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9]+", " ");
            return Regex.Replace(normalized, @"\s+", " ").Trim();
        }

        private static string BuildKnowledgeUrl(string qualificationCode, string sourceType, int knowledgeNumber, string originalName)
        {
            return $"knowledge://{Uri.EscapeDataString(qualificationCode)}/{Uri.EscapeDataString(sourceType)}/kb-{knowledgeNumber:D4}/{Uri.EscapeDataString(originalName)}";
        }

        private static string BuildAgentKnowledgeUrl(string scope, int knowledgeNumber, string originalName)
        {
            var normalizedScope = NormalizeAgentKnowledgeScope(scope);
            return $"knowledge://agent/{Uri.EscapeDataString(normalizedScope)}/kb-{knowledgeNumber:D4}/{Uri.EscapeDataString(originalName)}";
        }

        private static string NormalizeAgentKnowledgeScope(string? scope, string fallback = "shared")
        {
            var value = (scope ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            return value switch
            {
                "common" => "shared",
                "global" => "shared",
                "all" => "all",
                "mira" => "mira",
                "gemma" => "gemma",
                "gemma4" => "gemma",
                "gemma4:26b" => "gemma",
                "qwen" => "gemma",
                "shared" => "shared",
                _ => fallback
            };
        }

        private static string GetAgentKnowledgeScopeDisplayName(string scope)
        {
            return NormalizeAgentKnowledgeScope(scope) switch
            {
                "mira" => "Mira",
                "gemma" or "qwen" => "Gemma",
                _ => "Shared"
            };
        }

        private static string GetAgentKnowledgeScopeFolderName(string scope)
        {
            return NormalizeAgentKnowledgeScope(scope) switch
            {
                "mira" => "Mira",
                "gemma" or "qwen" => "Gemma",
                _ => "Shared"
            };
        }

        private static string GetAgentKnowledgeSourceType(string scope)
        {
            return NormalizeAgentKnowledgeScope(scope) switch
            {
                "mira" => "agent_mira",
                "gemma" or "qwen" => "agent_gemma",
                _ => "agent_shared"
            };
        }

        private static string EnsureUniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            var directory = Path.GetDirectoryName(path) ?? ".";
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (var i = 1; i < 10000; i++)
            {
                var candidate = Path.Combine(directory, $"{stem}_{i}{ext}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(directory, $"{stem}_{Guid.NewGuid():N}{ext}");
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

        private QualificationFolderInfo? SelectPreferredQualificationRoot(string qualificationCode, List<QualificationFolderInfo> folders)
        {
            if (folders == null || folders.Count == 0) return null;

            var code = MakeSafeFilePart(qualificationCode, "UNASSIGNED");
            var resolvedDescription = ResolveQualificationDescription(qualificationCode, folders[0].QualificationDescription);
            var safeDescription = MakeSafeFilePart(resolvedDescription, "Unassigned_Qualification");
            var expectedName = $"{code}_{safeDescription}";

            var exact = folders
                .FirstOrDefault(x => string.Equals(x.FolderName, expectedName, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            return folders
                .OrderByDescending(x => x.FileCount)
                .ThenBy(x => x.FolderName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private int ConsolidateLegacySourceType(
            string qualificationCode,
            string qualificationDescription,
            string sourceType,
            string legacyQualificationRoot,
            string preferredQualificationRoot,
            ref int updatedCount)
        {
            var legacySourceRoot = Path.Combine(legacyQualificationRoot, sourceType);
            if (!Directory.Exists(legacySourceRoot)) return 0;

            var preferredSourceRoot = Path.Combine(preferredQualificationRoot, sourceType);
            Directory.CreateDirectory(Path.Combine(preferredSourceRoot, "inbox"));
            Directory.CreateDirectory(Path.Combine(preferredSourceRoot, "archive"));
            Directory.CreateDirectory(Path.Combine(preferredSourceRoot, "duplicates"));

            var movedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var files = Directory.GetFiles(legacySourceRoot, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(legacySourceRoot, file);
                var firstSegment = relative
                    .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? string.Empty;
                var bucket = firstSegment.Equals("inbox", StringComparison.OrdinalIgnoreCase) ? "inbox"
                    : firstSegment.Equals("duplicates", StringComparison.OrdinalIgnoreCase) ? "duplicates"
                    : "archive";

                var destinationDirectory = Path.Combine(preferredSourceRoot, bucket);
                Directory.CreateDirectory(destinationDirectory);
                var destinationPath = EnsureUniquePath(Path.Combine(destinationDirectory, Path.GetFileName(file)));
                File.Move(file, destinationPath, true);
                movedFiles[file] = destinationPath;
            }

            var materials = _context.SourceMaterials
                .Where(s => (s.KnowledgeSourceType ?? string.Empty) == sourceType)
                .ToList()
                .Where(s =>
                    IsPathUnder(s.FilePath ?? string.Empty, legacySourceRoot) ||
                    IsPathUnder(s.FilePath ?? string.Empty, preferredSourceRoot) ||
                    string.Equals(s.KnowledgeRootPath ?? string.Empty, legacySourceRoot, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.KnowledgeRootPath ?? string.Empty, preferredSourceRoot, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.QualificationCode ?? string.Empty, qualificationCode, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var material in materials)
            {
                var changed = false;
                var currentPath = material.FilePath ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(currentPath))
                {
                    if (movedFiles.TryGetValue(currentPath, out var mapped))
                    {
                        material.FilePath = mapped;
                        material.FileName = Path.GetFileName(mapped);
                        changed = true;
                    }
                    else if (IsPathUnder(currentPath, legacySourceRoot))
                    {
                        var relative = Path.GetRelativePath(legacySourceRoot, currentPath);
                        var fileName = Path.GetFileName(currentPath);
                        var firstSegment = relative
                            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                            .FirstOrDefault() ?? string.Empty;
                        var bucket = firstSegment.Equals("inbox", StringComparison.OrdinalIgnoreCase) ? "inbox"
                            : firstSegment.Equals("duplicates", StringComparison.OrdinalIgnoreCase) ? "duplicates"
                            : "archive";
                        var candidate = Path.Combine(preferredSourceRoot, bucket, fileName);
                        if (File.Exists(candidate))
                        {
                            material.FilePath = candidate;
                            material.FileName = Path.GetFileName(candidate);
                            changed = true;
                        }
                    }
                }

                if (!string.Equals(material.KnowledgeRootPath ?? string.Empty, preferredSourceRoot, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(material.KnowledgeRootPath ?? string.Empty, legacySourceRoot, StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(material.KnowledgeRootPath))
                    {
                        material.KnowledgeRootPath = preferredSourceRoot;
                        changed = true;
                    }
                }

                if (!string.Equals(material.QualificationCode ?? string.Empty, qualificationCode, StringComparison.OrdinalIgnoreCase))
                {
                    material.QualificationCode = qualificationCode;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(qualificationDescription) &&
                    !string.Equals(material.QualificationDescription ?? string.Empty, qualificationDescription, StringComparison.OrdinalIgnoreCase))
                {
                    material.QualificationDescription = qualificationDescription;
                    changed = true;
                }

                if (changed)
                {
                    updatedCount++;
                }
            }

            return movedFiles.Count;
        }

        private static bool IsPathUnder(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
            try
            {
                var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsFolderTreeEmpty(string path)
        {
            if (!Directory.Exists(path)) return true;
            try
            {
                return !Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any();
            }
            catch
            {
                return false;
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
            return ImageExtensions.Contains(innerExt);
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
                if (File.Exists(candidate))
                {
                    results.Add(candidate);
                }
            }

            return results;
        }

        private static string ResolvePreferredQualificationRoot(string rootPath, string safeCode, string safeDescription)
        {
            var preferred = Path.Combine(rootPath, $"{safeCode}_{safeDescription}");
            if (Directory.Exists(preferred))
            {
                return preferred;
            }

            var sameCode = Directory.GetDirectories(rootPath, $"{safeCode}_*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(GetDirectoryFileCount)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (sameCode.Count > 0)
            {
                return sameCode[0];
            }

            return preferred;
        }

        private static int GetDirectoryFileCount(string path)
        {
            try
            {
                return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Take(20000).Count();
            }
            catch
            {
                return 0;
            }
        }

        private static string MakeSafeFilePart(string? value, string fallback)
        {
            var raw = (value ?? string.Empty).Trim();
            if (raw.Length == 0) raw = fallback;
            var safe = Regex.Replace(raw, @"[^\w\-]+", "_");
            safe = safe.Trim('_');
            if (safe.Length == 0) safe = fallback;
            if (safe.Length > 120) safe = safe.Substring(0, 120).Trim('_');
            if (safe.Length == 0) safe = fallback;
            return safe;
        }

        private string ExtractTextFromFile(string filePath, string ext, IEnumerable<string>? sidecarPaths = null)
        {
            string extracted;
            if (TextExtensions.Contains(ext))
            {
                var raw = File.ReadAllText(filePath);
                extracted = CleanExtractedText(raw);
                return extracted;
            }

            if (ImageExtensions.Contains(ext))
            {
                extracted = ExtractTextFromImageFile(filePath, sidecarPaths);
                return _ocrExtractionService.EnhanceExtractedText(filePath, ext, extracted);
            }

            if (ext == ".docx")
            {
                extracted = ExtractTextFromDocx(filePath);
                return extracted;
            }

            if (ext == ".pptx")
            {
                extracted = ExtractTextFromPptx(filePath);
                return extracted;
            }

            if (ext == ".pdf")
            {
                extracted = ExtractTextFromPdf(filePath);
                return _ocrExtractionService.EnhanceExtractedText(filePath, ext, extracted);
            }

            extracted = CleanExtractedText($"Indexed binary source: {Path.GetFileName(filePath)}");
            return extracted;
        }

        private static string ExtractTextFromImageFile(string imagePath, IEnumerable<string>? sidecarPaths)
        {
            var sb = new StringBuilder();
            var imageName = Path.GetFileName(imagePath);
            sb.AppendLine($"Indexed visual source: {imageName}.");

            var sidecars = (sidecarPaths ?? FindImageSidecarPaths(imagePath))
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sidecars.Count == 0)
            {
                sb.AppendLine("No sidecar description text found.");
                sb.AppendLine("Add a sidecar file named <image>.caption.md or <image>.caption.txt to improve AI grounding.");
                return CleanExtractedText(sb.ToString());
            }

            sb.AppendLine("Sidecar description text:");
            foreach (var sidecar in sidecars.Take(5))
            {
                try
                {
                    var text = File.ReadAllText(sidecar);
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

        private static string ExtractTextFromDocx(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var doc = WordprocessingDocument.Open(stream, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null) return string.Empty;

            var sb = new StringBuilder();
            foreach (var paragraph in body.Descendants<Paragraph>())
            {
                var line = string.Join(string.Empty, paragraph
                    .Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>()
                    .Where(t => !t.Ancestors<FieldCode>().Any() && !t.Ancestors<SimpleField>().Any())
                    .Select(t => t.Text));
                if (!string.IsNullOrWhiteSpace(line))
                {
                    sb.AppendLine(line.Trim());
                }
            }

            return CleanExtractedText(sb.ToString());
        }

        private static string ExtractTextFromPptx(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var presentation = PresentationDocument.Open(stream, false);
            var presentationPart = presentation.PresentationPart;
            var slideIdList = presentationPart?.Presentation?.SlideIdList;
            if (presentationPart == null || slideIdList == null) return string.Empty;

            var sb = new StringBuilder();
            foreach (var slideId in slideIdList.Elements<SlideId>())
            {
                var relationshipId = slideId.RelationshipId?.Value;
                if (string.IsNullOrWhiteSpace(relationshipId)) continue;
                if (presentationPart.GetPartById(relationshipId) is not SlidePart slidePart) continue;

                var slideText = slidePart.Slide?
                    .Descendants<DrawingText>()
                    .Select(t => (t.Text ?? string.Empty).Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList() ?? new List<string>();
                if (slideText.Count == 0) continue;

                sb.AppendLine(string.Join(" ", slideText));
            }

            return CleanExtractedText(sb.ToString());
        }

        private static string ExtractTextFromPdf(string filePath)
        {
            var stirlingText = TryExtractTextFromPdfViaStirling(filePath);
            if (!string.IsNullOrWhiteSpace(stirlingText))
            {
                return CleanExtractedText(stirlingText);
            }

            using var reader = new PdfReader(filePath);
            using var doc = new PdfDocument(reader);
            var pages = new List<(int Number, List<string> Lines)>();
            var totalPages = doc.GetNumberOfPages();
            for (var pageNumber = 1; pageNumber <= totalPages; pageNumber++)
            {
                var text = PdfTextExtractor.GetTextFromPage(doc.GetPage(pageNumber));
                text = DocumentTextCleaner.CleanPdfPageText(text ?? string.Empty);
                if (string.IsNullOrWhiteSpace(text)) continue;

                var lines = text
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => Regex.Replace(x ?? string.Empty, @"\s+", " ").Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Where(x => !DocumentTextCleaner.IsNoiseLine(x))
                    .ToList();
                if (lines.Count == 0) continue;

                pages.Add((pageNumber, lines));
            }

            var repeatedBoundaryKeys = DocumentTextCleaner.DetectRepeatedBoundaryLineKeys(
                pages.Select(x => (IReadOnlyList<string>)x.Lines).ToList());

            var sb = new StringBuilder();
            foreach (var page in pages)
            {
                var filteredLines = page.Lines
                    .Where(line => !repeatedBoundaryKeys.Contains(DocumentTextCleaner.NormalizeLineKey(line)))
                    .ToList();
                if (filteredLines.Count == 0) continue;

                var normalizedPageText = DocumentTextCleaner.CleanPdfPageText(string.Join("\n", filteredLines));
                if (string.IsNullOrWhiteSpace(normalizedPageText)) continue;
                if (DocumentTextCleaner.IsLikelyBoilerplateParagraph(normalizedPageText) &&
                    DocumentTextCleaner.WordCount(normalizedPageText) < 220)
                {
                    continue;
                }

                sb.AppendLine();
                sb.AppendLine($"## Page {page.Number}");
                sb.AppendLine(normalizedPageText);
            }

            return CleanExtractedText(sb.ToString());
        }

        private sealed class DerivedPdfVisualResult
        {
            public List<SourceMaterial> Materials { get; } = new();
            public string SummaryText { get; set; } = string.Empty;
        }

        private DerivedPdfVisualResult ExtractDerivedPdfVisuals(
            string archivedPdfPath,
            string originalName,
            string qualificationCode,
            string qualificationDescription,
            string sourceType,
            string sourceRoot,
            string parentKnowledgeUrl,
            string cacheKey,
            IDictionary<string, int> nextKnowledgeNumbers)
        {
            var result = new DerivedPdfVisualResult();
            try
            {
                var outputFolderName = MakeSafeFilePart(Path.GetFileNameWithoutExtension(archivedPdfPath), "pdf_visuals");
                var outputDirectory = Path.Combine(sourceRoot, "visual_archive", outputFolderName);
                var extracted = _pdfVisualExtractionService.ExtractAndPersist(archivedPdfPath, new PdfVisualExtractionService.PersistOptions
                {
                    OutputDirectory = outputDirectory,
                    OutputNamePrefix = "visual",
                    SourceDocumentName = originalName
                });
                result.SummaryText = extracted.SummaryText;

                foreach (var visual in extracted.Visuals)
                {
                    var visualKnowledgeNumber = GetNextKnowledgeNumber(cacheKey, qualificationCode, sourceType, nextKnowledgeNumbers);
                    nextKnowledgeNumbers[cacheKey] = visualKnowledgeNumber + 1;

                    var visualFileName = Path.GetFileName(visual.FilePath);
                    var imageExt = Path.GetExtension(visual.FilePath);
                    var imageText = ExtractTextFromImageFile(visual.FilePath, FindImageSidecarPaths(visual.FilePath));
                    imageText = _ocrExtractionService.EnhanceExtractedText(visual.FilePath, imageExt, imageText);

                    result.Materials.Add(new SourceMaterial
                    {
                        Title = BuildDerivedPdfVisualTitle(originalName, visual.Caption, visual.PageNumber, visualKnowledgeNumber),
                        FileName = visualFileName,
                        FilePath = visual.FilePath,
                        FileType = visual.FileType,
                        Url = BuildKnowledgeUrl(qualificationCode, sourceType, visualKnowledgeNumber, visualFileName),
                        QualificationCode = qualificationCode,
                        QualificationDescription = qualificationDescription,
                        SubjectDescription = $"KnowledgeBase:{sourceType}",
                        TopicDescription = $"KnowledgeNumber:{visualKnowledgeNumber:D4};DerivedVisualPage:{visual.PageNumber}",
                        AssessmentCriteriaDescription = BuildDerivedPdfVisualAssessmentNote(
                            originalName,
                            archivedPdfPath,
                            parentKnowledgeUrl,
                            visual.PageNumber,
                            visual.PlaceholderTag,
                            visual.Caption),
                        ExtractedText = imageText,
                        KnowledgeSourceType = sourceType,
                        KnowledgeNumber = visualKnowledgeNumber,
                        KnowledgeLabel = BuildDerivedPdfVisualLabel(originalName, visual.PageNumber, visualKnowledgeNumber),
                        KnowledgeRootPath = sourceRoot,
                        KnowledgeUploadedAtUtc = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PDF visual extraction failed for knowledge source '{PdfPath}'", archivedPdfPath);
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

        private static string TryExtractTextFromPdfViaStirling(string filePath)
        {
            var enabledRaw = (Environment.GetEnvironmentVariable("PDF_PREPROCESS_ENGINE") ?? string.Empty).Trim();
            if (!enabledRaw.Equals("stirling", StringComparison.OrdinalIgnoreCase)) return string.Empty;

            var baseUrl = (Environment.GetEnvironmentVariable("STIRLING_PDF_URL") ?? string.Empty).Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;

            if (!File.Exists(filePath)) return string.Empty;

            var timeoutSecondsRaw = (Environment.GetEnvironmentVariable("STIRLING_PDF_TIMEOUT_SECONDS") ?? string.Empty).Trim();
            var timeoutSeconds = 45;
            if (int.TryParse(timeoutSecondsRaw, out var parsedTimeout))
            {
                timeoutSeconds = Math.Clamp(parsedTimeout, 5, 300);
            }

            var apiKeyEnv = (Environment.GetEnvironmentVariable("STIRLING_PDF_API_KEY_ENV") ?? "STIRLING_PDF_API_KEY").Trim();
            if (string.IsNullOrWhiteSpace(apiKeyEnv)) apiKeyEnv = "STIRLING_PDF_API_KEY";
            var apiKeyHeader = (Environment.GetEnvironmentVariable("STIRLING_PDF_API_KEY_HEADER") ?? "X-API-KEY").Trim();
            if (string.IsNullOrWhiteSpace(apiKeyHeader)) apiKeyHeader = "X-API-KEY";
            var apiKey = (Environment.GetEnvironmentVariable(apiKeyEnv) ?? string.Empty).Trim();

            using var fileStream = File.OpenRead(filePath);
            using var form = new MultipartFormDataContent();
            using var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(fileContent, "fileInput", Path.GetFileName(filePath));
            form.Add(new StringContent("txt"), "outputFormat");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/convert/pdf/text");
            request.Content = form;
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.TryAddWithoutValidation(apiKeyHeader, apiKey);
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                var response = _stirlingHttp.Send(request, cts.Token);
                if (!response.IsSuccessStatusCode) return string.Empty;
                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return string.Empty;
                return response.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string CleanExtractedText(string text)
        {
            return DocumentTextCleaner.Clean(text, preservePdfPageMarkers: true);
        }

        private static string NormalizeForMatch(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");
        }

        private static bool ContainsLoose(string source, string needle)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(needle)) return false;
            return source.Contains(needle, StringComparison.Ordinal);
        }

        private static string BuildAgentKnowledgeReadme(string rootPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ETDP Agent Knowledge Structure");
            sb.AppendLine();
            sb.AppendLine("This structure stores compulsory non-curriculum subject matter for Mira and Qwen.");
            sb.AppendLine("Use it for cross-disciplinary scientific, technical, pedagogical, or operational knowledge that should not be tied to a specific qualification.");
            sb.AppendLine();
            sb.AppendLine("Root path:");
            sb.AppendLine($"`{rootPath}`");
            sb.AppendLine();
            sb.AppendLine("## Folder Hierarchy");
            sb.AppendLine();
            sb.AppendLine("AgentKnowledge/");
            sb.AppendLine("  Shared/");
            sb.AppendLine("    inbox/       (knowledge both Mira and Qwen must digest)");
            sb.AppendLine("    archive/     (indexed files moved here automatically)");
            sb.AppendLine("    duplicates/  (duplicates moved here automatically)");
            sb.AppendLine("  Mira/");
            sb.AppendLine("    inbox/       (Mira-only compulsory knowledge)");
            sb.AppendLine("    archive/");
            sb.AppendLine("    duplicates/");
            sb.AppendLine("  Qwen/");
            sb.AppendLine("    inbox/       (Qwen-only compulsory knowledge)");
            sb.AppendLine("    archive/");
            sb.AppendLine("    duplicates/");
            sb.AppendLine();
            sb.AppendLine("## Digest Rules");
            sb.AppendLine();
            sb.AppendLine("- Mira always digests `Shared` plus `Mira`.");
            sb.AppendLine("- Qwen always digests `Shared` plus `Qwen`.");
            sb.AppendLine("- These folders are not part of `Imports\\\\KnowledgeHierarchy` and are not treated as qualification knowledge taxonomy.");
            sb.AppendLine("- These folders are not part of `Imports\\\\<QualificationCode>` and are not treated as curriculum or assessment-spec uploads.");
            sb.AppendLine("- Indexed files are stored in `SourceMaterials` with global agent source types (`agent_shared`, `agent_mira`, `agent_qwen`).");
            sb.AppendLine();
            sb.AppendLine($"Last generated (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            return sb.ToString();
        }

        private static string BuildUploadReadme(string rootPath)
        {
            var curriculumRoot = EtdpPaths.GetImportsRoot();
            var sb = new StringBuilder();
            sb.AppendLine("# ETDP Knowledge Upload Structure");
            sb.AppendLine();
            sb.AppendLine("This structure is the permanent source-of-truth for automatic knowledge indexing.");
            sb.AppendLine();
            sb.AppendLine("Root path:");
            sb.AppendLine($"`{rootPath}`");
            sb.AppendLine();
            sb.AppendLine("## Folder Hierarchy");
            sb.AppendLine();
            sb.AppendLine("```text");
            sb.AppendLine("KnowledgeHierarchy/");
            sb.AppendLine("  upload.readme.md");
            sb.AppendLine("  <QualificationCode>_<QualificationDescription>/");
            sb.AppendLine("    local_source_upload/");
            sb.AppendLine("      inbox/      (drop new local source files here)");
            sb.AppendLine("      archive/    (indexed files moved here automatically)");
            sb.AppendLine("      duplicates/ (duplicates moved here automatically)");
            sb.AppendLine("    developer_knowledge_base/");
            sb.AppendLine("      inbox/      (drop new Developer-Knowledge-Base files here)");
            sb.AppendLine("      archive/    (indexed files moved here automatically)");
            sb.AppendLine("      duplicates/ (duplicates moved here automatically)");
            sb.AppendLine("      reports/    (auto-generated topic coverage reports per upload run)");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("## Auto-Sync Triggers");
            sb.AppendLine();
            sb.AppendLine("- AI Agent chat: `/api/Knowledge/chat` auto-syncs inbox files for the selected qualification.");
            sb.AppendLine("- AI Agent quick actions (`/ai-agent`) can upload Local Source, upload Developer Knowledge Base, run cognitive queue build, and trigger sync.");
            sb.AppendLine("- Content Builder local search: `/api/Content/search-local` auto-syncs inbox files for the selected qualification.");
            sb.AppendLine("- Manual sync endpoint: `POST /api/Content/sync-knowledge-hierarchy`.");
            sb.AppendLine();
            sb.AppendLine("## Manual Structure Endpoints");
            sb.AppendLine();
            sb.AppendLine("- Scaffold qualification folders: `POST /api/Content/scaffold-knowledge-hierarchy`.");
            sb.AppendLine("- Upload local source material: `POST /api/Content/upload-material`.");
            sb.AppendLine("- Upload developer knowledge base material: `POST /api/Content/upload-developer-knowledge`.");
            sb.AppendLine("- Read structure guide: `GET /api/Content/upload-structure-readme`.");
            sb.AppendLine();
            sb.AppendLine("## Metadata Applied During Indexing");
            sb.AppendLine();
            sb.AppendLine("- `QualificationCode`");
            sb.AppendLine("- `QualificationDescription`");
            sb.AppendLine("- `KnowledgeSourceType` (`local_source_upload` or `developer_knowledge_base`)");
            sb.AppendLine("- `KnowledgeNumber` (sequence per qualification/source)");
            sb.AppendLine("- `KnowledgeUploadedAtUtc`");
            sb.AppendLine("- `KnowledgeRootPath`");
            sb.AppendLine("- `KnowledgeLabel`");
            sb.AppendLine();
            sb.AppendLine("## Notes");
            sb.AppendLine();
            sb.AppendLine($"- Curriculum Specification and Assessment Specification documents stay outside `KnowledgeHierarchy`. Store them under `{Path.Combine(curriculumRoot, "<QualificationCode>")}` using `QC_CurriculumSpecification.*` and `QC_AssessmentSpecification.*`.");
            sb.AppendLine($"- For qualification subject matter, you can drop files directly under `{Path.Combine(curriculumRoot, "<QualificationCode>", "subject_matter")}` or `{Path.Combine(curriculumRoot, "<QualificationCode>", "local_source_upload")}`. ETDP mirrors those folders into `KnowledgeHierarchy/.../local_source_upload/inbox` during sync.");
            sb.AppendLine($"- For structured helper files, developer notes, generated CSVs, or curated support material, use `{Path.Combine(curriculumRoot, "<QualificationCode>", "developer_knowledge_base")}` or `{Path.Combine(curriculumRoot, "<QualificationCode>", "developer_kb")}`. ETDP mirrors those into `KnowledgeHierarchy/.../developer_knowledge_base/inbox` during sync.");
            sb.AppendLine("- Supported file types: `.txt`, `.md`, `.docx`, `.pdf`, `.pptx`, `.csv`, `.json`, `.jsonl`, `.xml`, `.yml`, `.yaml`, `.html`, `.htm`, `.png`, `.jpg`, `.jpeg`, `.webp`, `.gif`, `.bmp`, `.tif`, `.tiff`, `.svg`.");
            sb.AppendLine("- Files are moved out of `inbox` after processing to avoid duplicate re-indexing.");
            sb.AppendLine("- Folder scaffolding uses `QualificationCode` as the stable key and reuses an existing code folder to avoid duplicates.");
            sb.AppendLine("- For image-based knowledge, add sidecar text files like `<image>.caption.md` or `<image>.caption.txt` to provide searchable descriptions.");
            sb.AppendLine("- OCR is built-in for scanned images and low-text PDFs using local Tesseract.");
            sb.AppendLine("- OCR environment keys: optional `OCR_ENGINE`, `OCR_PDF_MODE`, `TESSERACT_PATH`, `TESSERACT_LANG`, `TESSERACT_PSM`.");
            sb.AppendLine("- Developer knowledge sync generates timestamped coverage reports in `developer_knowledge_base/reports` with topic mapping and missing-topic lists.");
            sb.AppendLine("- `CognitiveScan/PipelineJobs` is a job archive, not the primary watched drop-zone. ETDP now promotes curated pipeline extracts into the qualification alias folders above so chat can ingest them on the next sync.");
            sb.AppendLine("- Legacy duplicate qualification folders are automatically consolidated into one canonical folder per `QualificationCode`.");
            sb.AppendLine();
            sb.AppendLine($"Last generated (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            return sb.ToString().TrimEnd();
        }

        private string ResolveQualificationCurriculumLibraryPath(string qualificationCode)
        {
            var safeFolder = Regex.Replace(qualificationCode ?? string.Empty, @"[^\w\- ]+", string.Empty).Trim().Replace(" ", "_");
            if (string.IsNullOrWhiteSpace(safeFolder))
            {
                safeFolder = "UNASSIGNED";
            }

            var curriculumRoot = GetCurriculumLibraryRootPath();
            Directory.CreateDirectory(curriculumRoot);
            return Path.Combine(curriculumRoot, safeFolder);
        }

        private static void BackfillLegacyCurriculumLibrary(string legacyPath, string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(legacyPath) ||
                string.IsNullOrWhiteSpace(destinationPath) ||
                !Directory.Exists(legacyPath))
            {
                return;
            }

            Directory.CreateDirectory(destinationPath);
            foreach (var sourcePath in Directory.EnumerateFiles(legacyPath, "QC_*.*", SearchOption.TopDirectoryOnly))
            {
                var destinationFile = Path.Combine(destinationPath, Path.GetFileName(sourcePath));
                if (File.Exists(destinationFile))
                {
                    continue;
                }

                File.Copy(sourcePath, destinationFile, overwrite: false);
            }
        }

        private int MirrorCurriculumLibraryKnowledgeAliases(StructureInfo structure, int maxFilesPerInbox)
        {
            if (structure == null || string.IsNullOrWhiteSpace(structure.CurriculumLibraryPath) || !Directory.Exists(structure.CurriculumLibraryPath))
            {
                return 0;
            }

            var mirrored = 0;
            mirrored += MirrorQualificationFilesIntoInbox(
                structure.QualificationCode,
                "local_source_upload",
                Directory.EnumerateFiles(structure.CurriculumLibraryPath, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
                    .Where(path => !IsImageSidecarFile(path))
                    .Where(path => !Path.GetFileName(path).StartsWith("QC_", StringComparison.OrdinalIgnoreCase))
                    .Take(maxFilesPerInbox),
                structure.LocalInboxPath);

            mirrored += MirrorAliasFolderGroup(structure, LocalSubjectMatterAliasFolders, structure.LocalInboxPath, "local_source_upload", maxFilesPerInbox);
            mirrored += MirrorAliasFolderGroup(structure, DeveloperKnowledgeAliasFolders, structure.DeveloperInboxPath, "developer_knowledge_base", maxFilesPerInbox);
            return mirrored;
        }

        private int MirrorAliasFolderGroup(
            StructureInfo structure,
            IReadOnlyList<string> aliases,
            string targetInboxPath,
            string sourceType,
            int maxFilesPerInbox)
        {
            var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mirrored = 0;

            foreach (var alias in aliases)
            {
                var aliasPath = Path.Combine(structure.CurriculumLibraryPath, alias);
                if (!Directory.Exists(aliasPath) || !seenDirectories.Add(aliasPath))
                {
                    continue;
                }

                var sourceDirectories = new List<string> { aliasPath };
                var nestedInbox = Path.Combine(aliasPath, "inbox");
                if (Directory.Exists(nestedInbox))
                {
                    sourceDirectories.Insert(0, nestedInbox);
                }

                foreach (var sourceDirectory in sourceDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var files = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                        .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
                        .Where(path => !IsImageSidecarFile(path))
                        .Take(maxFilesPerInbox)
                        .ToList();

                    mirrored += MirrorQualificationFilesIntoInbox(
                        structure.QualificationCode,
                        sourceType,
                        files,
                        targetInboxPath);
                }
            }

            return mirrored;
        }

        private int MirrorQualificationFilesIntoInbox(
            string qualificationCode,
            string sourceType,
            IEnumerable<string> sourcePaths,
            string targetInboxPath)
        {
            if (string.IsNullOrWhiteSpace(targetInboxPath))
            {
                return 0;
            }

            Directory.CreateDirectory(targetInboxPath);
            var mirrored = 0;

            foreach (var sourcePath in sourcePaths.Where(File.Exists))
            {
                var originalName = Path.GetFileName(sourcePath);
                if (string.IsNullOrWhiteSpace(originalName))
                {
                    continue;
                }

                if (IsKnowledgeFileAlreadyManaged(qualificationCode, sourceType, originalName, targetInboxPath))
                {
                    continue;
                }

                var destinationPath = Path.Combine(targetInboxPath, originalName);
                if (!string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourcePath, destinationPath, overwrite: false);
                }

                mirrored++;
            }

            return mirrored;
        }

        private bool IsKnowledgeFileAlreadyManaged(
            string qualificationCode,
            string sourceType,
            string originalName,
            string targetInboxPath)
        {
            if (string.IsNullOrWhiteSpace(originalName))
            {
                return true;
            }

            var inboxMatch = Path.Combine(targetInboxPath, originalName);
            if (File.Exists(inboxMatch))
            {
                return true;
            }

            var sourceMarker = $"Source:{originalName}";
            var normalizedQualificationCode = (qualificationCode ?? string.Empty).Trim().ToLowerInvariant();
            var normalizedSourceType = (sourceType ?? string.Empty).Trim().ToLowerInvariant();

            var candidates = _context.SourceMaterials
                .AsNoTracking()
                .Where(material =>
                    (material.QualificationCode ?? string.Empty).ToLower() == normalizedQualificationCode &&
                    (material.KnowledgeSourceType ?? string.Empty).ToLower() == normalizedSourceType)
                .Select(material => new
                {
                    material.AssessmentCriteriaDescription,
                    material.Title
                })
                .ToList();

            return candidates.Any(material =>
                (material.AssessmentCriteriaDescription ?? string.Empty).Contains(sourceMarker, StringComparison.OrdinalIgnoreCase) ||
                (material.Title ?? string.Empty).Contains(originalName, StringComparison.OrdinalIgnoreCase));
        }
    }
}
