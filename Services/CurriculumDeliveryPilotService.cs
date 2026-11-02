using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ETD.Api.Data;
using ETD.Api.Models;
using ETD.Api.Utils;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ETD.Api.Services
{
    public sealed class CurriculumDeliveryPilotService
    {
        private const string EvidenceMappingVersion = "2026-05-04-vocational-source-bridge-v1";
        private const string AutoDraftMarker = "[AUTO_CURRICULUM_EVIDENCE_DRAFT]";
        private const string AutoDraftCoverageGapMarker = "[AUTO_CURRICULUM_INSUFFICIENT_COVERAGE]";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".docx", ".pdf", ".pptx", ".csv", ".json", ".jsonl", ".xml", ".yml", ".yaml", ".html", ".htm",
            ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".svg"
        };

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".svg"
        };

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "with", "from", "that", "this", "into", "within", "also", "shall", "must", "are",
            "was", "were", "have", "has", "had", "can", "could", "should", "will", "would", "about", "their", "them",
            "they", "then", "than", "your", "you", "our", "ours", "its", "which", "what", "when", "where", "while",
            "there", "these", "those", "been", "being", "over", "under", "through", "such", "using", "used", "use",
            "topic", "subject", "phase", "unit", "module", "outcome", "criteria", "criterion", "assessment", "learning",
            "programme", "program", "qualification", "code", "description", "level", "credits", "nqf", "learner",
            "learners", "lecturer", "lecturers", "content", "guide", "chapter", "section"
        };

        private static readonly ConcurrentDictionary<int, TopicEvidenceCacheEntry> TopicEvidenceCache = new();
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> TopicEvidenceCacheLocks = new();
        private static readonly HttpClient OpenAiHttpClient = new();

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<CurriculumDeliveryPilotService> _logger;

        public CurriculumDeliveryPilotService(
            IServiceScopeFactory scopeFactory,
            ILogger<CurriculumDeliveryPilotService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task<DeliveryPilotResult> ExecuteQualificationPilotAsync(
            DeliveryPilotRequest request,
            CancellationToken cancellationToken = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var knowledgeHierarchy = scope.ServiceProvider.GetRequiredService<KnowledgeHierarchyService>();

            var qualification = await db.Qualifications
                .AsNoTracking()
                .FirstOrDefaultAsync(q => q.Id == request.QualificationId, cancellationToken);

            if (qualification == null)
            {
                throw new InvalidOperationException("Qualification not found for delivery pilot.");
            }

            var qualificationCode = (qualification.QualificationNumber ?? string.Empty).Trim();
            var qualificationDescription = (qualification.QualificationDescription ?? string.Empty).Trim();
            var structure = knowledgeHierarchy.EnsureQualificationStructure(qualificationCode, qualificationDescription);

            var artifactsDirectory = Path.Combine(request.JobFolder, "delivery-pilot");
            Directory.CreateDirectory(artifactsDirectory);

            var result = new DeliveryPilotResult
            {
                QualificationId = qualification.Id,
                QualificationCode = qualificationCode,
                QualificationDescription = qualificationDescription,
                QualificationRootPath = structure.QualificationRootPath,
                ArtifactsDirectory = artifactsDirectory
            };

            var importResult = await ImportExternalResourcesAsync(
                db,
                knowledgeHierarchy,
                structure,
                qualificationCode,
                qualificationDescription,
                cancellationToken);
            result.Import = importResult;
            result.DetectedExternalResourceFolder = importResult.DetectedExternalFolder;

            var sourceMaterials = await LoadSourceMaterialsAsync(db, qualificationCode, qualificationDescription, cancellationToken);
            result.SourceMaterialCount = sourceMaterials.Count;
            if (sourceMaterials.Count == 0)
            {
                result.Warnings.Add("No qualification-linked source material is available yet for detailed topic mapping.");
                return result;
            }

            var chunks = BuildSourceMaterialChunks(sourceMaterials);
            result.SourceChunkCount = chunks.Count;
            result.SourceChunksPath = await WriteJsonArtifactAsync(
                artifactsDirectory,
                "source-chunks.json",
                chunks.Select(BuildChunkArtifact).ToList(),
                cancellationToken);

            var topics = await LoadTopicTargetsAsync(db, qualification.Id, cancellationToken);
            var criteria = await LoadCriteriaTargetsAsync(db, qualification.Id, cancellationToken);
            result.TopicCount = topics.Count;
            result.CriteriaCount = criteria.Count;

            if (chunks.Count == 0)
            {
                result.Warnings.Add("Source material was found, but no clean content chunks survived sanitation and TOC filtering.");
                return result;
            }

            var tokenIndex = BuildChunkTokenIndex(chunks);
            var topicMaps = BuildTopicSourceMap(topics, chunks, tokenIndex);
            var criteriaMaps = BuildCriteriaSourceMap(criteria, chunks, tokenIndex);

            result.TopicsMappedCount = topicMaps.Count(x => x.Matches.Count > 0);
            result.CriteriaMappedCount = criteriaMaps.Count(x => x.Matches.Count > 0);
            result.TopicSourceMapPath = await WriteJsonArtifactAsync(artifactsDirectory, "topic-source-map.json", topicMaps, cancellationToken);
            result.CriteriaSourceMapPath = await WriteJsonArtifactAsync(artifactsDirectory, "criteria-source-map.json", criteriaMaps, cancellationToken);

            var draftWriteResult = await SeedLessonPlanDraftsAsync(
                db,
                qualification.Id,
                criteriaMaps,
                request.PopulateLessonPlanDrafts,
                cancellationToken);

            result.LessonPlanDraftsCreated = draftWriteResult.Created;
            result.LessonPlanDraftsUpdated = draftWriteResult.Updated;
            result.LessonPlanDraftsSkipped = draftWriteResult.Skipped;
            result.LessonPlanDraftsPath = await WriteJsonArtifactAsync(
                artifactsDirectory,
                "lesson-plan-drafts.json",
                draftWriteResult.Entries,
                cancellationToken);

            foreach (var warning in importResult.Warnings
                .Concat(draftWriteResult.Warnings)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                result.Warnings.Add(warning);
            }

            return result;
        }

        public async Task<TopicEvidenceSummary> BuildTopicEvidenceSummaryAsync(
            int qualificationId,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var qualification = await db.Qualifications
                .AsNoTracking()
                .FirstOrDefaultAsync(q => q.Id == qualificationId, cancellationToken);

            if (qualification == null)
            {
                throw new InvalidOperationException("Qualification not found for topic evidence summary.");
            }

            var qualificationCode = (qualification.QualificationNumber ?? string.Empty).Trim();
            var qualificationDescription = (qualification.QualificationDescription ?? string.Empty).Trim();
            var cacheContext = await BuildTopicEvidenceCacheContextAsync(db, qualification, cancellationToken);

            if (!forceRefresh)
            {
                var cached = await TryLoadCachedTopicEvidenceSummaryAsync(qualification.Id, cacheContext.CacheKey, cancellationToken);
                if (cached != null)
                {
                    _logger.LogInformation(
                        "Topic evidence cache hit for qualification {QualificationId} ({TopicCount} topics).",
                        qualification.Id,
                        cached.TopicCount);
                    return cached;
                }
            }

            var cacheLock = TopicEvidenceCacheLocks.GetOrAdd(qualification.Id, _ => new SemaphoreSlim(1, 1));
            await cacheLock.WaitAsync(cancellationToken);
            try
            {
                if (!forceRefresh)
                {
                    var cached = await TryLoadCachedTopicEvidenceSummaryAsync(qualification.Id, cacheContext.CacheKey, cancellationToken);
                    if (cached != null)
                    {
                        _logger.LogInformation(
                            "Topic evidence cache hit after wait for qualification {QualificationId} ({TopicCount} topics).",
                            qualification.Id,
                            cached.TopicCount);
                        return cached;
                    }
                }

                var startedAtUtc = DateTime.UtcNow;
                var summary = new TopicEvidenceSummary
                {
                    QualificationId = qualification.Id,
                    QualificationNumber = qualificationCode,
                    QualificationDescription = qualificationDescription,
                    TopicCount = cacheContext.Topics.Count,
                    DuplicateCriteriaGroups = cacheContext.DuplicateCriteriaGroups.ToList()
                };

                if (cacheContext.DuplicateCriteriaGroups.Count > 0)
                {
                    summary.Warnings.Add(
                        $"Detected {cacheContext.DuplicateCriteriaGroups.Count} duplicated assessment-criteria cluster(s) across topics, so ETDP is showing topic evidence coverage as the primary measure.");
                }

                summary.SourceMaterialCount = cacheContext.SourceFingerprintRows.Count;
                if (summary.SourceMaterialCount == 0)
                {
                    summary.Warnings.Add("No qualification-linked subject matter has been uploaded yet for this qualification.");
                    summary.Topics = cacheContext.Topics.Select(BuildEmptyTopicEvidenceItem).ToList();
                    FinalizeTopicEvidenceSummary(summary);
                    await SaveCachedTopicEvidenceSummaryAsync(qualification.Id, cacheContext.CacheKey, summary, cancellationToken);
                    return CloneTopicEvidenceSummary(summary);
                }

                var artifacts = await BuildTopicEvidenceArtifactsAsync(
                    db,
                    qualification.Id,
                    qualificationCode,
                    qualificationDescription,
                    cacheContext,
                    cancellationToken);

                summary.SourceChunkCount = artifacts.Chunks.Count;
                if (artifacts.Chunks.Count == 0)
                {
                    summary.Warnings.Add("Subject matter exists, but no clean content chunks survived sanitation and table-of-contents filtering.");
                    summary.Topics = cacheContext.Topics.Select(BuildEmptyTopicEvidenceItem).ToList();
                    FinalizeTopicEvidenceSummary(summary);
                    await SaveCachedTopicEvidenceSummaryAsync(qualification.Id, cacheContext.CacheKey, summary, cancellationToken);
                    return CloneTopicEvidenceSummary(summary);
                }

                summary.Topics = artifacts.TopicMaps
                    .Select(BuildTopicEvidenceItem)
                    .OrderBy(item => item.SubjectCode, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.TopicCode, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.TopicDescription, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                FinalizeTopicEvidenceSummary(summary);
                await SaveCachedTopicEvidenceSummaryAsync(qualification.Id, cacheContext.CacheKey, summary, cancellationToken);

                var elapsed = DateTime.UtcNow - startedAtUtc;
                _logger.LogInformation(
                    "Topic evidence rebuilt for qualification {QualificationId} in {ElapsedMs} ms ({TopicCount} topics, {MaterialCount} source materials, {ChunkCount} chunks, reused materials {ReusedMaterialCount}, rebuilt materials {RebuiltMaterialCount}, reused topics {ReusedTopicCount}, recomputed topics {RecomputedTopicCount}).",
                    qualification.Id,
                    (int)elapsed.TotalMilliseconds,
                    summary.TopicCount,
                    summary.SourceMaterialCount,
                    summary.SourceChunkCount,
                    artifacts.ReusedMaterialCount,
                    artifacts.RebuiltMaterialCount,
                    artifacts.ReusedTopicCount,
                    artifacts.RecomputedTopicCount);

                return CloneTopicEvidenceSummary(summary);
            }
            finally
            {
                cacheLock.Release();
            }
        }

        private async Task<TopicEvidenceCacheContext> BuildTopicEvidenceCacheContextAsync(
            ApplicationDbContext db,
            Qualification qualification,
            CancellationToken cancellationToken)
        {
            var qualificationCode = (qualification.QualificationNumber ?? string.Empty).Trim();
            var qualificationDescription = (qualification.QualificationDescription ?? string.Empty).Trim();
            var topics = await LoadTopicTargetsAsync(db, qualification.Id, cancellationToken);
            var duplicateCriteriaGroups = await LoadDuplicateCriteriaGroupsAsync(db, qualification.Id, cancellationToken);
            var sourceFingerprintRows = await LoadSourceMaterialFingerprintRowsAsync(
                db,
                qualificationCode,
                qualificationDescription,
                cancellationToken);

            return new TopicEvidenceCacheContext
            {
                QualificationId = qualification.Id,
                QualificationCode = qualificationCode,
                QualificationDescription = qualificationDescription,
                Topics = topics,
                DuplicateCriteriaGroups = duplicateCriteriaGroups,
                SourceFingerprintRows = sourceFingerprintRows,
                TopicStructureKey = ComputeTopicEvidenceTopicStructureKey(
                    qualification.Id,
                    qualificationCode,
                    qualificationDescription,
                    topics,
                    duplicateCriteriaGroups),
                CacheKey = ComputeTopicEvidenceCacheKey(
                    qualification.Id,
                    qualificationCode,
                    qualificationDescription,
                    topics,
                    duplicateCriteriaGroups,
                    sourceFingerprintRows)
            };
        }

        private async Task<TopicEvidenceSummary?> TryLoadCachedTopicEvidenceSummaryAsync(
            int qualificationId,
            string cacheKey,
            CancellationToken cancellationToken)
        {
            if (TopicEvidenceCache.TryGetValue(qualificationId, out var cachedEntry) &&
                string.Equals(cachedEntry.CacheKey, cacheKey, StringComparison.Ordinal) &&
                cachedEntry.Summary != null)
            {
                return CloneTopicEvidenceSummary(cachedEntry.Summary);
            }

            var cachePath = GetTopicEvidenceCachePath(qualificationId);
            if (!File.Exists(cachePath))
            {
                return null;
            }

            try
            {
                var json = await File.ReadAllTextAsync(cachePath, cancellationToken);
                var envelope = JsonSerializer.Deserialize<TopicEvidenceCacheEnvelope>(json, JsonOptions);
                if (envelope?.Summary == null ||
                    !string.Equals(envelope.CacheKey, cacheKey, StringComparison.Ordinal))
                {
                    return null;
                }

                TopicEvidenceCache[qualificationId] = new TopicEvidenceCacheEntry
                {
                    CacheKey = envelope.CacheKey,
                    SavedAtUtc = envelope.SavedAtUtc,
                    Summary = CloneTopicEvidenceSummary(envelope.Summary)
                };

                return CloneTopicEvidenceSummary(envelope.Summary);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read topic evidence cache file for qualification {QualificationId}.", qualificationId);
                return null;
            }
        }

        private async Task SaveCachedTopicEvidenceSummaryAsync(
            int qualificationId,
            string cacheKey,
            TopicEvidenceSummary summary,
            CancellationToken cancellationToken)
        {
            var cloned = CloneTopicEvidenceSummary(summary);
            TopicEvidenceCache[qualificationId] = new TopicEvidenceCacheEntry
            {
                CacheKey = cacheKey,
                SavedAtUtc = DateTime.UtcNow,
                Summary = cloned
            };

            var cachePath = GetTopicEvidenceCachePath(qualificationId);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath) ?? EtdpPaths.CombineProject("artifacts"));

            try
            {
                var envelope = new TopicEvidenceCacheEnvelope
                {
                    CacheKey = cacheKey,
                    SavedAtUtc = DateTime.UtcNow,
                    Summary = cloned
                };
                var json = JsonSerializer.Serialize(envelope, JsonOptions);
                await File.WriteAllTextAsync(cachePath, json, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist topic evidence cache file for qualification {QualificationId}.", qualificationId);
            }
        }

        private static string GetTopicEvidenceCachePath(int qualificationId)
        {
            return Path.Combine(EtdpPaths.CombineProject("artifacts", "topic-evidence-cache"), $"qualification-{qualificationId}.json");
        }

        private static string GetTopicEvidenceComputationCachePath(int qualificationId)
        {
            return Path.Combine(EtdpPaths.CombineProject("artifacts", "topic-evidence-cache"), $"qualification-{qualificationId}.compute.json");
        }

        private static string ComputeTopicEvidenceTopicStructureKey(
            int qualificationId,
            string qualificationCode,
            string qualificationDescription,
            IReadOnlyList<CurriculumTopicTarget> topics,
            IReadOnlyList<DuplicateCriteriaGroup> duplicateCriteriaGroups)
        {
            var sb = new StringBuilder();
            sb.Append("mapping-version|").Append(EvidenceMappingVersion).AppendLine();
            sb.Append("qualification|").Append(qualificationId).Append('|').Append(qualificationCode).Append('|').Append(qualificationDescription).AppendLine();

            foreach (var topic in topics
                .OrderBy(item => item.TopicId)
                .ThenBy(item => item.SubjectCode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.TopicCode, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("topic|")
                    .Append(topic.TopicId).Append('|')
                    .Append(topic.SubjectCode).Append('|')
                    .Append(topic.SubjectDescription).Append('|')
                    .Append(topic.SubjectPurpose).Append('|')
                    .Append(topic.TopicCode).Append('|')
                    .Append(topic.TopicDescription).Append('|')
                    .Append(topic.TopicPurpose)
                    .AppendLine();
            }

            foreach (var group in duplicateCriteriaGroups
                .OrderBy(item => item.CriteriaDescription, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("duplicate|")
                    .Append(group.CriteriaDescription).Append('|')
                    .Append(group.TopicCount).Append('|')
                    .Append(string.Join("|", (group.Topics ?? new List<string>()).OrderBy(item => item, StringComparer.OrdinalIgnoreCase)))
                    .AppendLine();
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        private static string ComputeTopicEvidenceCacheKey(
            int qualificationId,
            string qualificationCode,
            string qualificationDescription,
            IReadOnlyList<CurriculumTopicTarget> topics,
            IReadOnlyList<DuplicateCriteriaGroup> duplicateCriteriaGroups,
            IReadOnlyList<SourceMaterialFingerprintRow> sourceFingerprintRows)
        {
            var sb = new StringBuilder();
            sb.Append("mapping-version|").Append(EvidenceMappingVersion).AppendLine();
            sb.Append("qualification|").Append(qualificationId).Append('|').Append(qualificationCode).Append('|').Append(qualificationDescription).AppendLine();

            foreach (var topic in topics
                .OrderBy(item => item.TopicId)
                .ThenBy(item => item.SubjectCode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.TopicCode, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("topic|")
                    .Append(topic.TopicId).Append('|')
                    .Append(topic.SubjectCode).Append('|')
                    .Append(topic.SubjectDescription).Append('|')
                    .Append(topic.SubjectPurpose).Append('|')
                    .Append(topic.TopicCode).Append('|')
                    .Append(topic.TopicDescription).Append('|')
                    .Append(topic.TopicPurpose)
                    .AppendLine();
            }

            foreach (var group in duplicateCriteriaGroups
                .OrderBy(item => item.CriteriaDescription, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("duplicate|")
                    .Append(group.CriteriaDescription).Append('|')
                    .Append(group.TopicCount).Append('|')
                    .Append(string.Join("|", (group.Topics ?? new List<string>()).OrderBy(item => item, StringComparer.OrdinalIgnoreCase)))
                    .AppendLine();
            }

            foreach (var row in sourceFingerprintRows
                .OrderBy(item => item.Id))
            {
                sb.Append("source|")
                    .Append(row.Id).Append('|')
                    .Append(row.FileName).Append('|')
                    .Append(row.Title).Append('|')
                    .Append(row.KnowledgeSourceType).Append('|')
                    .Append(row.KnowledgeNumber).Append('|')
                    .Append(row.UploadedAtUtc.Ticks).Append('|')
                    .Append(row.ExtractedTextLength)
                    .AppendLine();
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        private async Task<List<SourceMaterialFingerprintRow>> LoadSourceMaterialFingerprintRowsAsync(
            ApplicationDbContext db,
            string qualificationCode,
            string qualificationDescription,
            CancellationToken cancellationToken)
        {
            var rawRows = await db.SourceMaterials
                .AsNoTracking()
                .Where(s => ((s.QualificationCode ?? string.Empty) == qualificationCode ||
                             (string.IsNullOrWhiteSpace(qualificationCode) &&
                              (s.QualificationDescription ?? string.Empty) == qualificationDescription)) &&
                            ((s.KnowledgeSourceType ?? string.Empty) == "developer_knowledge_base" ||
                             (s.KnowledgeSourceType ?? string.Empty) == "local_source_upload"))
                .OrderBy(s => s.Id)
                .Select(s => new
                {
                    s.Id,
                    s.Title,
                    s.FileName,
                    s.KnowledgeSourceType,
                    s.KnowledgeNumber,
                    UploadedAtUtc = s.KnowledgeUploadedAtUtc ?? s.CreatedAt,
                    ExtractedTextLength = (s.ExtractedText ?? string.Empty).Length
                })
                .ToListAsync(cancellationToken);

            var sourceRows = rawRows
                .Where(x => !IsCurriculumSpecificationOrGeneratedScanMaterial(x.Title, x.FileName, null))
                .Select(x => new SourceMaterialFingerprintRow
                {
                    Id = x.Id,
                    Title = x.Title ?? string.Empty,
                    FileName = x.FileName ?? string.Empty,
                    KnowledgeSourceType = x.KnowledgeSourceType ?? string.Empty,
                    KnowledgeNumber = x.KnowledgeNumber ?? 0,
                    UploadedAtUtc = x.UploadedAtUtc,
                    ExtractedTextLength = x.ExtractedTextLength
                })
                .ToList();

            sourceRows.AddRange(LoadVocationalSourceMaterialFingerprintRows(qualificationCode, qualificationDescription));
            return sourceRows;
        }

        private static TopicEvidenceSummary CloneTopicEvidenceSummary(TopicEvidenceSummary summary)
        {
            var json = JsonSerializer.Serialize(summary, JsonOptions);
            return JsonSerializer.Deserialize<TopicEvidenceSummary>(json, JsonOptions) ?? summary;
        }

        private async Task<TopicEvidenceBuildArtifacts> BuildTopicEvidenceArtifactsAsync(
            ApplicationDbContext db,
            int qualificationId,
            string qualificationCode,
            string qualificationDescription,
            TopicEvidenceCacheContext cacheContext,
            CancellationToken cancellationToken)
        {
            var orderedSourceRows = cacheContext.SourceFingerprintRows
                .OrderByDescending(row => row.UploadedAtUtc)
                .ThenBy(row => row.Id)
                .ToList();

            var computationCache = await TryLoadTopicEvidenceComputationCacheAsync(qualificationId, cancellationToken);
            var cachedMaterials = (computationCache?.MaterialChunks ?? new List<TopicEvidenceMaterialChunkCacheItem>())
                .Where(item => item.MaterialId != 0)
                .GroupBy(item => item.MaterialId)
                .ToDictionary(group => group.Key, group => group.First());

            var currentMaterialIds = orderedSourceRows
                .Select(row => row.Id)
                .Where(id => id != 0)
                .ToHashSet();

            var removedMaterialIds = cachedMaterials.Keys
                .Where(id => !currentMaterialIds.Contains(id))
                .ToHashSet();

            var rebuiltMaterialIds = new HashSet<int>();
            foreach (var row in orderedSourceRows)
            {
                var fingerprint = ComputeSourceMaterialFingerprint(row);
                if (!cachedMaterials.TryGetValue(row.Id, out var cachedMaterial) ||
                    !string.Equals(cachedMaterial.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    rebuiltMaterialIds.Add(row.Id);
                }
            }

            var changedMaterialsById = rebuiltMaterialIds.Count == 0
                ? new Dictionary<int, SourceMaterialRow>()
                : (await LoadSourceMaterialsByIdsAsync(
                        db,
                        qualificationCode,
                        qualificationDescription,
                        rebuiltMaterialIds,
                        cancellationToken))
                    .ToDictionary(material => material.Id);

            var chunks = new List<SourceMaterialChunk>();
            var changedChunks = new List<SourceMaterialChunk>();
            var savedMaterialChunks = new List<TopicEvidenceMaterialChunkCacheItem>(orderedSourceRows.Count);
            var reusedMaterialCount = 0;
            var rebuiltMaterialCount = 0;

            foreach (var row in orderedSourceRows)
            {
                var fingerprint = ComputeSourceMaterialFingerprint(row);
                if (cachedMaterials.TryGetValue(row.Id, out var cachedMaterial) &&
                    !rebuiltMaterialIds.Contains(row.Id))
                {
                    var runtimeChunks = (cachedMaterial.Chunks ?? new List<CachedSourceMaterialChunk>())
                        .Select(BuildSourceMaterialChunk)
                        .ToList();
                    chunks.AddRange(runtimeChunks);
                    savedMaterialChunks.Add(CloneTopicEvidenceMaterialChunkCacheItem(cachedMaterial));
                    reusedMaterialCount++;
                    continue;
                }

                if (!changedMaterialsById.TryGetValue(row.Id, out var material))
                {
                    continue;
                }

                var rebuiltChunks = BuildSourceMaterialChunks(material);
                chunks.AddRange(rebuiltChunks);
                changedChunks.AddRange(rebuiltChunks);
                savedMaterialChunks.Add(new TopicEvidenceMaterialChunkCacheItem
                {
                    MaterialId = row.Id,
                    Fingerprint = fingerprint,
                    Chunks = rebuiltChunks.Select(BuildCachedSourceMaterialChunk).ToList()
                });
                rebuiltMaterialCount++;
            }

            var tokenIndex = BuildChunkTokenIndex(chunks);
            var previousTopicMaps = string.Equals(computationCache?.TopicStructureKey, cacheContext.TopicStructureKey, StringComparison.Ordinal)
                ? (computationCache?.TopicMaps ?? new List<TopicSourceMapItem>())
                    .Where(item => item.TopicId > 0)
                    .GroupBy(item => item.TopicId)
                    .ToDictionary(group => group.Key, group => CloneTopicSourceMapItem(group.First()))
                : new Dictionary<int, TopicSourceMapItem>();

            List<TopicSourceMapItem> topicMaps;
            var reusedTopicCount = 0;
            var recomputedTopicCount = 0;

            if (chunks.Count == 0)
            {
                topicMaps = new List<TopicSourceMapItem>();
            }
            else if (previousTopicMaps.Count == 0)
            {
                topicMaps = BuildTopicSourceMap(cacheContext.Topics, chunks, tokenIndex);
                recomputedTopicCount = topicMaps.Count;
            }
            else
            {
                var impactedTopicIds = DetermineImpactedTopicIds(
                    cacheContext.Topics,
                    changedChunks,
                    chunks.Count,
                    rebuiltMaterialIds,
                    removedMaterialIds,
                    previousTopicMaps);

                var mapsByTopicId = new Dictionary<int, TopicSourceMapItem>();
                foreach (var topic in cacheContext.Topics)
                {
                    if (impactedTopicIds.Contains(topic.TopicId) ||
                        !previousTopicMaps.TryGetValue(topic.TopicId, out var previousMap))
                    {
                        mapsByTopicId[topic.TopicId] = BuildTopicSourceMapItem(topic, chunks, tokenIndex);
                        recomputedTopicCount++;
                    }
                    else
                    {
                        mapsByTopicId[topic.TopicId] = CloneTopicSourceMapItem(previousMap);
                        reusedTopicCount++;
                    }
                }

                topicMaps = cacheContext.Topics
                    .Select(topic => mapsByTopicId[topic.TopicId])
                    .ToList();
            }

            await SaveTopicEvidenceComputationCacheAsync(
                qualificationId,
                new TopicEvidenceComputationEnvelope
                {
                    QualificationId = qualificationId,
                    QualificationCode = qualificationCode,
                    QualificationDescription = qualificationDescription,
                    TopicStructureKey = cacheContext.TopicStructureKey,
                    SavedAtUtc = DateTime.UtcNow,
                    MaterialChunks = savedMaterialChunks,
                    TopicMaps = topicMaps.Select(CloneTopicSourceMapItem).ToList()
                },
                cancellationToken);

            return new TopicEvidenceBuildArtifacts
            {
                Chunks = chunks,
                TopicMaps = topicMaps,
                ReusedMaterialCount = reusedMaterialCount,
                RebuiltMaterialCount = rebuiltMaterialCount,
                RemovedMaterialCount = removedMaterialIds.Count,
                ReusedTopicCount = reusedTopicCount,
                RecomputedTopicCount = recomputedTopicCount
            };
        }

        private async Task<TopicEvidenceComputationEnvelope?> TryLoadTopicEvidenceComputationCacheAsync(
            int qualificationId,
            CancellationToken cancellationToken)
        {
            var cachePath = GetTopicEvidenceComputationCachePath(qualificationId);
            if (!File.Exists(cachePath))
            {
                return null;
            }

            try
            {
                var json = await File.ReadAllTextAsync(cachePath, cancellationToken);
                return JsonSerializer.Deserialize<TopicEvidenceComputationEnvelope>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read topic evidence computation cache for qualification {QualificationId}.", qualificationId);
                return null;
            }
        }

        private async Task SaveTopicEvidenceComputationCacheAsync(
            int qualificationId,
            TopicEvidenceComputationEnvelope envelope,
            CancellationToken cancellationToken)
        {
            var cachePath = GetTopicEvidenceComputationCachePath(qualificationId);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath) ?? EtdpPaths.CombineProject("artifacts"));

            try
            {
                var json = JsonSerializer.Serialize(envelope, JsonOptions);
                await File.WriteAllTextAsync(cachePath, json, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist topic evidence computation cache for qualification {QualificationId}.", qualificationId);
            }
        }

        private static string ComputeSourceMaterialFingerprint(SourceMaterialFingerprintRow row)
        {
            var sb = new StringBuilder();
            sb.Append(row.Id).Append('|')
                .Append(row.Title).Append('|')
                .Append(row.FileName).Append('|')
                .Append(row.KnowledgeSourceType).Append('|')
                .Append(row.KnowledgeNumber).Append('|')
                .Append(row.UploadedAtUtc.Ticks).Append('|')
                .Append(row.ExtractedTextLength);

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        private static CachedSourceMaterialChunk BuildCachedSourceMaterialChunk(SourceMaterialChunk chunk)
        {
            return new CachedSourceMaterialChunk
            {
                Id = chunk.Id,
                MaterialId = chunk.MaterialId,
                MaterialTitle = chunk.MaterialTitle,
                FileName = chunk.FileName,
                FileType = chunk.FileType,
                KnowledgeNumber = chunk.KnowledgeNumber,
                KnowledgeSourceType = chunk.KnowledgeSourceType,
                Url = chunk.Url,
                PageNumber = chunk.PageNumber,
                ChapterTitle = chunk.ChapterTitle,
                Citation = chunk.Citation,
                Excerpt = chunk.Excerpt,
                SearchText = chunk.SearchText,
                SourcePriority = chunk.SourcePriority,
                IndexTokens = chunk.IndexTokens
                    .OrderBy(token => token, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }

        private static SourceMaterialChunk BuildSourceMaterialChunk(CachedSourceMaterialChunk chunk)
        {
            return new SourceMaterialChunk
            {
                Id = chunk.Id,
                MaterialId = chunk.MaterialId,
                MaterialTitle = chunk.MaterialTitle,
                FileName = chunk.FileName,
                FileType = chunk.FileType,
                KnowledgeNumber = chunk.KnowledgeNumber,
                KnowledgeSourceType = chunk.KnowledgeSourceType,
                Url = chunk.Url,
                PageNumber = chunk.PageNumber,
                ChapterTitle = chunk.ChapterTitle,
                Citation = chunk.Citation,
                Excerpt = chunk.Excerpt,
                SearchText = chunk.SearchText,
                Text = string.Empty,
                IndexTokens = new HashSet<string>(chunk.IndexTokens ?? new List<string>(), StringComparer.OrdinalIgnoreCase),
                SourcePriority = chunk.SourcePriority
            };
        }

        private static TopicEvidenceMaterialChunkCacheItem CloneTopicEvidenceMaterialChunkCacheItem(TopicEvidenceMaterialChunkCacheItem item)
        {
            return new TopicEvidenceMaterialChunkCacheItem
            {
                MaterialId = item.MaterialId,
                Fingerprint = item.Fingerprint,
                Chunks = (item.Chunks ?? new List<CachedSourceMaterialChunk>())
                    .Select(chunk => new CachedSourceMaterialChunk
                    {
                        Id = chunk.Id,
                        MaterialId = chunk.MaterialId,
                        MaterialTitle = chunk.MaterialTitle,
                        FileName = chunk.FileName,
                        FileType = chunk.FileType,
                        KnowledgeNumber = chunk.KnowledgeNumber,
                        KnowledgeSourceType = chunk.KnowledgeSourceType,
                        Url = chunk.Url,
                        PageNumber = chunk.PageNumber,
                        ChapterTitle = chunk.ChapterTitle,
                        Citation = chunk.Citation,
                        Excerpt = chunk.Excerpt,
                        SearchText = chunk.SearchText,
                        SourcePriority = chunk.SourcePriority,
                        IndexTokens = (chunk.IndexTokens ?? new List<string>()).ToList()
                    })
                    .ToList()
            };
        }

        private static TopicSourceMapItem CloneTopicSourceMapItem(TopicSourceMapItem item)
        {
            return new TopicSourceMapItem
            {
                TopicId = item.TopicId,
                TopicCode = item.TopicCode,
                TopicDescription = item.TopicDescription,
                SubjectCode = item.SubjectCode,
                SubjectDescription = item.SubjectDescription,
                Matches = (item.Matches ?? new List<ChunkMatch>())
                    .Select(match => new ChunkMatch
                    {
                        MaterialId = match.MaterialId,
                        ChunkId = match.ChunkId,
                        MaterialTitle = match.MaterialTitle,
                        FileName = match.FileName,
                        KnowledgeNumber = match.KnowledgeNumber,
                        KnowledgeSourceType = match.KnowledgeSourceType,
                        Url = match.Url,
                        Citation = match.Citation,
                        ChapterTitle = match.ChapterTitle,
                        PageNumber = match.PageNumber,
                        Score = match.Score,
                        Confidence = match.Confidence,
                        TopicTokenMatches = match.TopicTokenMatches,
                        SubjectTokenMatches = match.SubjectTokenMatches,
                        CriteriaTokenMatches = match.CriteriaTokenMatches,
                        Excerpt = match.Excerpt
                    })
                    .ToList()
            };
        }

        private static HashSet<int> DetermineImpactedTopicIds(
            IReadOnlyList<CurriculumTopicTarget> topics,
            IReadOnlyList<SourceMaterialChunk> changedChunks,
            int totalChunkCount,
            IReadOnlySet<int> rebuiltMaterialIds,
            IReadOnlySet<int> removedMaterialIds,
            IReadOnlyDictionary<int, TopicSourceMapItem> previousTopicMaps)
        {
            if (previousTopicMaps.Count == 0)
            {
                return topics.Select(topic => topic.TopicId).ToHashSet();
            }

            if (rebuiltMaterialIds.Count == 0 && removedMaterialIds.Count == 0)
            {
                return new HashSet<int>();
            }

            if (totalChunkCount <= 180)
            {
                return topics.Select(topic => topic.TopicId).ToHashSet();
            }

            var impacted = new HashSet<int>();
            var changedMaterialIds = rebuiltMaterialIds
                .Concat(removedMaterialIds)
                .ToHashSet();

            foreach (var entry in previousTopicMaps)
            {
                if ((entry.Value.Matches ?? new List<ChunkMatch>()).Any(match => changedMaterialIds.Contains(match.MaterialId)))
                {
                    impacted.Add(entry.Key);
                }
            }

            var topicTokenIndex = BuildTopicTokenIndex(topics);
            foreach (var chunk in changedChunks)
            {
                foreach (var token in chunk.IndexTokens)
                {
                    if (!topicTokenIndex.TryGetValue(token, out var topicIds))
                    {
                        continue;
                    }

                    foreach (var topicId in topicIds)
                    {
                        impacted.Add(topicId);
                    }
                }
            }

            return impacted;
        }

        private static Dictionary<string, List<int>> BuildTopicTokenIndex(IReadOnlyList<CurriculumTopicTarget> topics)
        {
            var index = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var topic in topics)
            {
                foreach (var token in topic.TopicTokens
                    .Concat(topic.SubjectTokens)
                    .Where(token => !string.IsNullOrWhiteSpace(token))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!index.TryGetValue(token, out var ids))
                    {
                        ids = new List<int>();
                        index[token] = ids;
                    }

                    ids.Add(topic.TopicId);
                }
            }

            return index;
        }

        private async Task<ImportResult> ImportExternalResourcesAsync(
            ApplicationDbContext db,
            KnowledgeHierarchyService knowledgeHierarchy,
            KnowledgeHierarchyService.StructureInfo structure,
            string qualificationCode,
            string qualificationDescription,
            CancellationToken cancellationToken)
        {
            var result = new ImportResult();
            var detectedFolder = ResolveExternalResourceFolder(qualificationCode, qualificationDescription);
            result.DetectedExternalFolder = detectedFolder;

            var hasInboxFiles = Directory.Exists(structure.DeveloperInboxPath) &&
                Directory.GetFiles(structure.DeveloperInboxPath, "*", SearchOption.TopDirectoryOnly)
                    .Any(path => SupportedExtensions.Contains(Path.GetExtension(path)) && !IsImageSidecarFile(path));

            if (string.IsNullOrWhiteSpace(detectedFolder) || !Directory.Exists(detectedFolder))
            {
                if (!hasInboxFiles)
                {
                    result.Warnings.Add("No external vocational source folder was auto-detected; using only already indexed qualification resources.");
                    return result;
                }
            }
            else
            {
                var existingSourceNames = await db.SourceMaterials
                    .AsNoTracking()
                    .Where(s => (s.QualificationCode ?? string.Empty) == qualificationCode &&
                                (s.KnowledgeSourceType ?? string.Empty) == "developer_knowledge_base")
                    .Select(s => new { s.Title, s.FileName, s.AssessmentCriteriaDescription })
                    .ToListAsync(cancellationToken);

                var knownSourceNames = existingSourceNames
                    .Select(x => ResolveOriginalSourceName(x.Title, x.FileName, x.AssessmentCriteriaDescription))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var copiedInRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var candidateFiles = Directory.GetFiles(detectedFolder, "*", SearchOption.AllDirectories)
                    .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
                    .Where(path => !IsImageSidecarFile(path))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var sourcePath in candidateFiles)
                {
                    var originalName = Path.GetFileName(sourcePath);
                    if (string.IsNullOrWhiteSpace(originalName))
                    {
                        continue;
                    }

                    if (!copiedInRun.Add(originalName))
                    {
                        result.SkippedCount++;
                        continue;
                    }

                    if (knownSourceNames.Contains(originalName))
                    {
                        result.SkippedCount++;
                        continue;
                    }

                    var destination = EnsureUniquePath(Path.Combine(structure.DeveloperInboxPath, originalName));
                    File.Copy(sourcePath, destination, overwrite: false);
                    result.CopiedToInboxCount++;
                }

                hasInboxFiles = hasInboxFiles || result.CopiedToInboxCount > 0;
            }

            if (!hasInboxFiles)
            {
                return result;
            }

            var sync = knowledgeHierarchy.SyncKnowledgeHierarchy(new KnowledgeHierarchyService.SyncOptions
            {
                QualificationCode = qualificationCode,
                QualificationDescription = qualificationDescription,
                IncludeLocalSourceUploads = false,
                IncludeDeveloperKnowledgeBase = true,
                RebuildUploadReadme = false,
                ConsolidateLegacyFolders = false
            });

            result.SyncCreatedCount = sync.Created;
            result.SyncSkippedCount = sync.Skipped;
            result.SyncFailedCount = sync.Failed;
            result.CoverageReportPath = sync.CoverageReports.FirstOrDefault()?.MarkdownPath ?? string.Empty;
            return result;
        }

        private static string ResolveExternalResourceFolder(string qualificationCode, string qualificationDescription)
        {
            foreach (var root in FindVocationalDisciplineRoots())
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                var target = NormalizeMatchToken($"{qualificationCode} {qualificationDescription}");
                var best = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly)
                    .Select(path => new
                    {
                        Path = path,
                        Score = ScoreResourceFolder(path, target, qualificationDescription, qualificationCode)
                    })
                    .OrderByDescending(x => x.Score)
                    .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (best != null && best.Score >= 4)
                {
                    return best.Path;
                }
            }

            return string.Empty;
        }

        private static IEnumerable<string> FindVocationalDisciplineRoots()
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddIfPresent(string? candidate)
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    candidates.Add(candidate.Trim());
                }
            }

            var current = AppContext.BaseDirectory;
            for (var i = 0; i < 8 && !string.IsNullOrWhiteSpace(current); i++)
            {
                AddIfPresent(Path.Combine(current, "VocationalLLM", "data", "knowledge_taxonomy", "vocational_disciplines"));
                current = Directory.GetParent(current)?.FullName ?? string.Empty;
            }

            var cwd = Directory.GetCurrentDirectory();
            for (var i = 0; i < 8 && !string.IsNullOrWhiteSpace(cwd); i++)
            {
                AddIfPresent(Path.Combine(cwd, "VocationalLLM", "data", "knowledge_taxonomy", "vocational_disciplines"));
                cwd = Directory.GetParent(cwd)?.FullName ?? string.Empty;
            }

            return candidates;
        }

        private static int ScoreResourceFolder(string folderPath, string target, string qualificationDescription, string qualificationCode)
        {
            var folderName = Path.GetFileName(folderPath) ?? string.Empty;
            var normalizedFolder = NormalizeMatchToken(folderName);
            var score = 0;

            if (!string.IsNullOrWhiteSpace(target))
            {
                if (string.Equals(normalizedFolder, target, StringComparison.OrdinalIgnoreCase))
                {
                    score += 8;
                }
                else if (normalizedFolder.Contains(target, StringComparison.OrdinalIgnoreCase) ||
                         target.Contains(normalizedFolder, StringComparison.OrdinalIgnoreCase))
                {
                    score += 6;
                }
            }

            foreach (var token in BuildTargetTokens($"{qualificationDescription} {qualificationCode}"))
            {
                if (normalizedFolder.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    score += 1;
                }
            }

            return score;
        }

        private async Task<List<SourceMaterialRow>> LoadSourceMaterialsAsync(
            ApplicationDbContext db,
            string qualificationCode,
            string qualificationDescription,
            CancellationToken cancellationToken)
        {
            var rawRows = await db.SourceMaterials
                .AsNoTracking()
                .Where(s => ((s.QualificationCode ?? string.Empty) == qualificationCode ||
                             (string.IsNullOrWhiteSpace(qualificationCode) &&
                              (s.QualificationDescription ?? string.Empty) == qualificationDescription)) &&
                            ((s.KnowledgeSourceType ?? string.Empty) == "developer_knowledge_base" ||
                             (s.KnowledgeSourceType ?? string.Empty) == "local_source_upload"))
                .OrderByDescending(s => s.KnowledgeUploadedAtUtc ?? s.CreatedAt)
                .Select(s => new
                {
                    s.Id,
                    s.Title,
                    s.FileName,
                    s.FilePath,
                    s.FileType,
                    s.Url,
                    s.KnowledgeSourceType,
                    s.KnowledgeNumber,
                    UploadedAtUtc = s.KnowledgeUploadedAtUtc ?? s.CreatedAt,
                    s.AssessmentCriteriaDescription,
                    s.KnowledgeLabel,
                    s.ExtractedText
                })
                .ToListAsync(cancellationToken);

            var sourceRows = rawRows
                .Where(x => !IsCurriculumSpecificationOrGeneratedScanMaterial(x.Title, x.FileName, x.AssessmentCriteriaDescription))
                .Select(x => new SourceMaterialRow
                {
                    Id = x.Id,
                    Title = x.Title ?? string.Empty,
                    FileName = x.FileName ?? string.Empty,
                    FilePath = x.FilePath ?? string.Empty,
                    FileType = x.FileType ?? string.Empty,
                    Url = x.Url ?? string.Empty,
                    KnowledgeSourceType = x.KnowledgeSourceType ?? string.Empty,
                    KnowledgeNumber = x.KnowledgeNumber ?? 0,
                    UploadedAtUtc = x.UploadedAtUtc,
                    AssessmentCriteriaDescription = x.AssessmentCriteriaDescription ?? string.Empty,
                    KnowledgeLabel = x.KnowledgeLabel ?? string.Empty,
                    ExtractedText = x.ExtractedText ?? string.Empty,
                    OriginalSourceName = ResolveOriginalSourceName(x.Title, x.FileName, x.AssessmentCriteriaDescription)
                })
                .ToList();

            sourceRows.AddRange(LoadVocationalSourceMaterials(qualificationCode, qualificationDescription, null));
            return sourceRows;
        }

        private async Task<List<SourceMaterialRow>> LoadSourceMaterialsByIdsAsync(
            ApplicationDbContext db,
            string qualificationCode,
            string qualificationDescription,
            IReadOnlyCollection<int> materialIds,
            CancellationToken cancellationToken)
        {
            var ids = (materialIds ?? Array.Empty<int>())
                .Where(id => id != 0)
                .Distinct()
                .ToList();
            if (ids.Count == 0)
            {
                return new List<SourceMaterialRow>();
            }

            var etdpIds = ids.Where(id => id > 0).ToList();
            var vocationalIds = ids.Where(id => id < 0).Select(id => -id).ToList();
            var sourceRows = new List<SourceMaterialRow>();
            if (etdpIds.Count == 0)
            {
                sourceRows.AddRange(LoadVocationalSourceMaterials(qualificationCode, qualificationDescription, vocationalIds));
                return sourceRows;
            }

            var rawRows = await db.SourceMaterials
                .AsNoTracking()
                .Where(s => etdpIds.Contains(s.Id) &&
                            (((s.QualificationCode ?? string.Empty) == qualificationCode) ||
                             (string.IsNullOrWhiteSpace(qualificationCode) &&
                              (s.QualificationDescription ?? string.Empty) == qualificationDescription)) &&
                            ((s.KnowledgeSourceType ?? string.Empty) == "developer_knowledge_base" ||
                             (s.KnowledgeSourceType ?? string.Empty) == "local_source_upload"))
                .Select(s => new
                {
                    s.Id,
                    s.Title,
                    s.FileName,
                    s.FilePath,
                    s.FileType,
                    s.Url,
                    s.KnowledgeSourceType,
                    s.KnowledgeNumber,
                    UploadedAtUtc = s.KnowledgeUploadedAtUtc ?? s.CreatedAt,
                    s.AssessmentCriteriaDescription,
                    s.KnowledgeLabel,
                    s.ExtractedText
                })
                .ToListAsync(cancellationToken);

            sourceRows.AddRange(rawRows
                .Where(x => !IsCurriculumSpecificationOrGeneratedScanMaterial(x.Title, x.FileName, x.AssessmentCriteriaDescription))
                .Select(x => new SourceMaterialRow
                {
                    Id = x.Id,
                    Title = x.Title ?? string.Empty,
                    FileName = x.FileName ?? string.Empty,
                    FilePath = x.FilePath ?? string.Empty,
                    FileType = x.FileType ?? string.Empty,
                    Url = x.Url ?? string.Empty,
                    KnowledgeSourceType = x.KnowledgeSourceType ?? string.Empty,
                    KnowledgeNumber = x.KnowledgeNumber ?? 0,
                    UploadedAtUtc = x.UploadedAtUtc,
                    AssessmentCriteriaDescription = x.AssessmentCriteriaDescription ?? string.Empty,
                    KnowledgeLabel = x.KnowledgeLabel ?? string.Empty,
                    ExtractedText = x.ExtractedText ?? string.Empty,
                    OriginalSourceName = ResolveOriginalSourceName(x.Title, x.FileName, x.AssessmentCriteriaDescription)
                }));

            sourceRows.AddRange(LoadVocationalSourceMaterials(qualificationCode, qualificationDescription, vocationalIds));
            return sourceRows;
        }

        private static List<SourceMaterialFingerprintRow> LoadVocationalSourceMaterialFingerprintRows(
            string qualificationCode,
            string qualificationDescription)
        {
            return LoadVocationalDocumentRows(qualificationCode, qualificationDescription, null)
                .Select(row => new SourceMaterialFingerprintRow
                {
                    Id = -row.DocumentId,
                    Title = row.Title,
                    FileName = row.FileName,
                    KnowledgeSourceType = "local_source_upload",
                    KnowledgeNumber = 100000 + row.DocumentId,
                    UploadedAtUtc = row.CreatedAtUtc,
                    ExtractedTextLength = row.RawCharCount
                })
                .ToList();
        }

        private static List<SourceMaterialRow> LoadVocationalSourceMaterials(
            string qualificationCode,
            string qualificationDescription,
            IReadOnlyCollection<int>? documentIds)
        {
            return LoadVocationalDocumentRows(qualificationCode, qualificationDescription, documentIds)
                .Select(row => new SourceMaterialRow
                {
                    Id = -row.DocumentId,
                    Title = row.Title,
                    FileName = row.FileName,
                    FilePath = row.SourcePath,
                    FileType = Path.GetExtension(row.SourcePath),
                    Url = string.Empty,
                    KnowledgeSourceType = "local_source_upload",
                    KnowledgeNumber = 100000 + row.DocumentId,
                    UploadedAtUtc = row.CreatedAtUtc,
                    AssessmentCriteriaDescription = string.Empty,
                    KnowledgeLabel = row.Title,
                    ExtractedText = row.ExtractedText,
                    OriginalSourceName = string.IsNullOrWhiteSpace(row.FileName) ? row.Title : row.FileName
                })
                .Where(row => !string.IsNullOrWhiteSpace(row.ExtractedText))
                .ToList();
        }

        private static List<VocationalDocumentRow> LoadVocationalDocumentRows(
            string qualificationCode,
            string qualificationDescription,
            IReadOnlyCollection<int>? documentIds)
        {
            var discipline = ResolveVocationalDiscipline(qualificationCode, qualificationDescription);
            if (string.IsNullOrWhiteSpace(discipline))
            {
                return new List<VocationalDocumentRow>();
            }

            var dbPath = ResolveVocationalLlmDatabasePath();
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            {
                return new List<VocationalDocumentRow>();
            }

            try
            {
                var ids = (documentIds ?? Array.Empty<int>())
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();
                using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                var sql = new StringBuilder();
                sql.AppendLine("select d.id, d.title, d.source_path, d.source_type, d.raw_char_count, d.created_at,");
                sql.AppendLine("       (select group_concat(content, char(10) || char(10))");
                sql.AppendLine("          from (select content from chunks where document_id = d.id order by chunk_index)) as extracted_text");
                sql.AppendLine("from documents d");
                sql.AppendLine("where (coalesce(d.vocational_discipline, '') = @discipline");
                sql.AppendLine("       or coalesce(d.source_path, '') like @disciplinePath)");
                if (ids.Count > 0)
                {
                    var idParameters = new List<string>();
                    for (var i = 0; i < ids.Count; i++)
                    {
                        var parameterName = $"@id{i}";
                        idParameters.Add(parameterName);
                        command.Parameters.AddWithValue(parameterName, ids[i]);
                    }
                    sql.AppendLine($"and d.id in ({string.Join(",", idParameters)})");
                }
                sql.AppendLine("order by d.created_at desc, d.id desc");
                command.CommandText = sql.ToString();
                command.Parameters.AddWithValue("@discipline", discipline);
                command.Parameters.AddWithValue("@disciplinePath", $"%vocational_disciplines%{discipline}%");

                using var reader = command.ExecuteReader();
                var rows = new List<VocationalDocumentRow>();
                while (reader.Read())
                {
                    var sourcePath = ReadSqliteString(reader, "source_path");
                    var title = ReadSqliteString(reader, "title");
                    rows.Add(new VocationalDocumentRow
                    {
                        DocumentId = ReadSqliteInt(reader, "id"),
                        Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(sourcePath) : title,
                        SourcePath = sourcePath,
                        FileName = Path.GetFileName(sourcePath),
                        RawCharCount = ReadSqliteInt(reader, "raw_char_count"),
                        CreatedAtUtc = ReadSqliteDateTime(reader, "created_at"),
                        ExtractedText = ReadSqliteString(reader, "extracted_text")
                    });
                }

                return rows;
            }
            catch
            {
                return new List<VocationalDocumentRow>();
            }
        }

        private static string ResolveVocationalDiscipline(string qualificationCode, string qualificationDescription)
        {
            var text = NormalizeSearchPhrase($"{qualificationCode} {qualificationDescription}");
            return text.Contains("diesel", StringComparison.Ordinal) ? "Diesel Mechanic" : string.Empty;
        }

        private static string ResolveVocationalLlmDatabasePath()
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "VocationalLLM", "data", "vocational_llm.db"),
                Path.Combine(Directory.GetCurrentDirectory(), "..", "VocationalLLM", "data", "vocational_llm.db"),
                @"D:\ETDP\VocationalLLM\data\vocational_llm.db"
            };

            return candidates
                .Select(path => Path.GetFullPath(path))
                .FirstOrDefault(File.Exists) ?? string.Empty;
        }

        private static string ReadSqliteString(SqliteDataReader reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }

        private static int ReadSqliteInt(SqliteDataReader reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            if (reader.IsDBNull(ordinal)) return 0;
            return Convert.ToInt32(reader.GetValue(ordinal));
        }

        private static DateTime ReadSqliteDateTime(SqliteDataReader reader, string name)
        {
            var value = ReadSqliteString(reader, name);
            return DateTime.TryParse(value, out var parsed) ? parsed : DateTime.UtcNow;
        }

        private async Task<List<DuplicateCriteriaGroup>> LoadDuplicateCriteriaGroupsAsync(
            ApplicationDbContext db,
            int qualificationId,
            CancellationToken cancellationToken)
        {
            var rows =
                await (from criteria in db.AssessmentCriteria.AsNoTracking()
                       join topic in db.Topics.AsNoTracking() on criteria.TopicId equals topic.Id
                       join subject in db.Subjects.AsNoTracking() on topic.SubjectId equals subject.Id
                       where subject.QualificationId == qualificationId
                       select new
                       {
                           topic.Id,
                           topic.TopicCode,
                           topic.TopicDescription,
                           criteria.Description
                       })
                    .ToListAsync(cancellationToken);

            return rows
                .Select(x => new
                {
                    x.Id,
                    x.TopicCode,
                    x.TopicDescription,
                    Description = NormalizeLine(x.Description),
                    Key = NormalizeDuplicateCriteriaKey(x.Description)
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Key) && x.Key.Length >= 24)
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Select(x => x.Id).Distinct().Count() > 1)
                .OrderByDescending(group => group.Select(x => x.Id).Distinct().Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .Select(group => new DuplicateCriteriaGroup
                {
                    CriteriaDescription = TruncateWithEllipsis(group.First().Description, 220),
                    TopicCount = group.Select(x => x.Id).Distinct().Count(),
                    Topics = group
                        .Select(x => $"{x.TopicCode} {x.TopicDescription}".Trim())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(8)
                        .ToList()
                })
                .ToList();
        }

        private static string ResolveOriginalSourceName(string? title, string? fileName, string? assessmentCriteriaDescription)
        {
            var assessmentDescription = (assessmentCriteriaDescription ?? string.Empty).Trim();
            var sourceIdx = assessmentDescription.IndexOf("Source:", StringComparison.OrdinalIgnoreCase);
            if (sourceIdx >= 0)
            {
                var value = assessmentDescription[(sourceIdx + "Source:".Length)..].Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            var titleText = (title ?? string.Empty).Trim();
            var separatorIndex = titleText.LastIndexOf("::", StringComparison.Ordinal);
            if (separatorIndex >= 0 && separatorIndex < titleText.Length - 2)
            {
                var value = titleText[(separatorIndex + 2)..].Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return (fileName ?? string.Empty).Trim();
        }

        private static bool IsCurriculumSpecificationOrGeneratedScanMaterial(string? title, string? fileName, string? assessmentCriteriaDescription)
        {
            var originalName = ResolveOriginalSourceName(title, fileName, assessmentCriteriaDescription);
            var normalized = NormalizeSearchPhrase($"{originalName} {fileName} {title}");
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            return normalized.Contains("qc curriculumspecification", StringComparison.Ordinal) ||
                   normalized.Contains("qc assessmentspecification", StringComparison.Ordinal) ||
                   normalized.Contains("qc curriculum specification", StringComparison.Ordinal) ||
                   normalized.Contains("qc assessment specification", StringComparison.Ordinal) ||
                   normalized.Contains("curriculum topics", StringComparison.Ordinal) ||
                   normalized.Contains("curriculum subjects", StringComparison.Ordinal) ||
                   normalized.Contains("curriculum phases", StringComparison.Ordinal) ||
                   normalized.Contains("curriculum baseline", StringComparison.Ordinal) ||
                   normalized.Contains("curriculum knowledge extract", StringComparison.Ordinal) ||
                   normalized.Contains("curriculum ocr enriched", StringComparison.Ordinal) ||
                   normalized.Contains("knowledge scan report", StringComparison.Ordinal) ||
                   normalized.Contains("template detection", StringComparison.Ordinal);
        }

        private static List<SourceMaterialChunk> BuildSourceMaterialChunks(IReadOnlyList<SourceMaterialRow> materials)
        {
            var chunks = new List<SourceMaterialChunk>();
            foreach (var material in materials)
            {
                chunks.AddRange(BuildSourceMaterialChunks(material));
            }

            return chunks;
        }

        private static List<SourceMaterialChunk> BuildSourceMaterialChunks(SourceMaterialRow material)
        {
            var chunks = new List<SourceMaterialChunk>();
            var rawText = (material.ExtractedText ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return chunks;
            }

            var lines = rawText.Split('\n', StringSplitOptions.None);
            var navigationHints = new List<string>();
            var pageNumber = 0;
            var chunkIndex = 0;
            var currentHeading = string.Empty;
            var buffer = new List<string>();
            var bufferWordCount = 0;
            var inTableOfContents = false;
            var contentEvidenceAfterToc = 0;

            void Flush()
            {
                if (buffer.Count == 0 || bufferWordCount < 45)
                {
                    buffer.Clear();
                    bufferWordCount = 0;
                    return;
                }

                var text = CleanGeneratedText(string.Join(" ", buffer));
                if (DocumentTextCleaner.WordCount(text) < 45)
                {
                    buffer.Clear();
                    bufferWordCount = 0;
                    return;
                }

                var excerpt = BuildExcerpt(text);
                var chapterTitle = CleanHeading(currentHeading);
                var citation = BuildCitation(material, pageNumber, chapterTitle);
                var searchText = BuildSearchText(material, text, chapterTitle, navigationHints);
                var indexTokens = BuildIndexTokens(searchText);

                chunks.Add(new SourceMaterialChunk
                {
                    Id = $"{material.Id}:{chunkIndex:D4}",
                    MaterialId = material.Id,
                    MaterialTitle = material.Title,
                    FileName = material.OriginalSourceName,
                    FileType = material.FileType,
                    KnowledgeNumber = material.KnowledgeNumber,
                    KnowledgeSourceType = material.KnowledgeSourceType,
                    Url = material.Url,
                    PageNumber = pageNumber,
                    ChapterTitle = chapterTitle,
                    Citation = citation,
                    Text = text,
                    Excerpt = excerpt,
                    SearchText = searchText,
                    IndexTokens = indexTokens,
                    SourcePriority = DetermineSourcePriority(material)
                });

                chunkIndex++;
                buffer.Clear();
                bufferWordCount = 0;
            }

            foreach (var rawLine in lines)
            {
                var line = NormalizeLine(rawLine);
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var pageMatch = Regex.Match(line, @"^\[Page\s+(\d+)\]$", RegexOptions.IgnoreCase);
                if (pageMatch.Success)
                {
                    if (bufferWordCount >= 180)
                    {
                        Flush();
                    }

                    int.TryParse(pageMatch.Groups[1].Value, out pageNumber);
                    if (inTableOfContents && contentEvidenceAfterToc > 0)
                    {
                        inTableOfContents = false;
                        contentEvidenceAfterToc = 0;
                    }

                    continue;
                }

                if (DocumentTextCleaner.IsNoiseLine(line))
                {
                    continue;
                }

                if (IsTableOfContentsHeading(line))
                {
                    inTableOfContents = true;
                    contentEvidenceAfterToc = 0;
                    continue;
                }

                if (LooksLikeTableOfContentsLine(line))
                {
                    var hint = ExtractNavigationHint(line);
                    if (!string.IsNullOrWhiteSpace(hint))
                    {
                        AddUnique(navigationHints, hint, 24);
                    }

                    continue;
                }

                if (inTableOfContents)
                {
                    if (DocumentTextCleaner.WordCount(line) >= 6)
                    {
                        contentEvidenceAfterToc++;
                    }

                    if (contentEvidenceAfterToc < 2)
                    {
                        continue;
                    }

                    inTableOfContents = false;
                }

                if (IsLikelyHeading(line))
                {
                    if (bufferWordCount >= 120)
                    {
                        Flush();
                    }

                    currentHeading = line;
                    continue;
                }

                var cleaned = CleanGeneratedText(line);
                var words = DocumentTextCleaner.WordCount(cleaned);
                if (words < 3)
                {
                    continue;
                }

                buffer.Add(cleaned);
                bufferWordCount += words;
                if (bufferWordCount >= 210)
                {
                    Flush();
                }
            }

            Flush();
            return chunks;
        }

        private static Dictionary<string, List<int>> BuildChunkTokenIndex(IReadOnlyList<SourceMaterialChunk> chunks)
        {
            var index = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < chunks.Count; i++)
            {
                foreach (var token in chunks[i].IndexTokens)
                {
                    if (!index.TryGetValue(token, out var list))
                    {
                        list = new List<int>();
                        index[token] = list;
                    }

                    list.Add(i);
                }
            }

            return index;
        }

        private async Task<List<CurriculumTopicTarget>> LoadTopicTargetsAsync(
            ApplicationDbContext db,
            int qualificationId,
            CancellationToken cancellationToken)
        {
            var rows =
                await (from topic in db.Topics.AsNoTracking()
                       join subject in db.Subjects.AsNoTracking() on topic.SubjectId equals subject.Id
                       where subject.QualificationId == qualificationId
                       orderby subject.SubjectCode, topic.TopicCode, topic.Id
                       select new
                       {
                           topic.Id,
                           topic.TopicCode,
                           topic.TopicDescription,
                           topic.TopicPurpose,
                           subject.SubjectCode,
                           subject.SubjectDescription,
                           subject.SubjectPurpose
                       })
                    .ToListAsync(cancellationToken);

            return rows.Select(x => new CurriculumTopicTarget
            {
                TopicId = x.Id,
                TopicCode = x.TopicCode ?? string.Empty,
                TopicDescription = x.TopicDescription ?? string.Empty,
                TopicPurpose = x.TopicPurpose ?? string.Empty,
                SubjectCode = x.SubjectCode ?? string.Empty,
                SubjectDescription = x.SubjectDescription ?? string.Empty,
                SubjectPurpose = x.SubjectPurpose ?? string.Empty,
                TopicPhrase = NormalizeSearchPhrase(x.TopicDescription ?? string.Empty),
                SubjectPhrase = NormalizeSearchPhrase(x.SubjectDescription ?? string.Empty),
                TopicTokens = BuildTargetTokens($"{x.TopicCode} {x.TopicDescription} {x.TopicPurpose}"),
                SubjectTokens = BuildTargetTokens($"{x.SubjectCode} {x.SubjectDescription} {x.SubjectPurpose}")
            }).ToList();
        }

        private async Task<List<CurriculumCriteriaTarget>> LoadCriteriaTargetsAsync(
            ApplicationDbContext db,
            int qualificationId,
            CancellationToken cancellationToken)
        {
            var rows =
                await (from criteria in db.AssessmentCriteria.AsNoTracking()
                       join topic in db.Topics.AsNoTracking() on criteria.TopicId equals topic.Id
                       join subject in db.Subjects.AsNoTracking() on topic.SubjectId equals subject.Id
                       where subject.QualificationId == qualificationId
                       orderby subject.SubjectCode, topic.TopicCode, criteria.Id
                       select new
                       {
                           CriteriaId = criteria.Id,
                           CriteriaDescription = criteria.Description,
                           topic.Id,
                           topic.TopicCode,
                           topic.TopicDescription,
                           topic.TopicPurpose,
                           subject.SubjectCode,
                           subject.SubjectDescription,
                           subject.SubjectPurpose
                       })
                    .ToListAsync(cancellationToken);

            return rows.Select(x => new CurriculumCriteriaTarget
            {
                CriteriaId = x.CriteriaId,
                CriteriaDescription = x.CriteriaDescription ?? string.Empty,
                TopicId = x.Id,
                TopicCode = x.TopicCode ?? string.Empty,
                TopicDescription = x.TopicDescription ?? string.Empty,
                TopicPurpose = x.TopicPurpose ?? string.Empty,
                SubjectCode = x.SubjectCode ?? string.Empty,
                SubjectDescription = x.SubjectDescription ?? string.Empty,
                SubjectPurpose = x.SubjectPurpose ?? string.Empty,
                CriteriaPhrase = NormalizeSearchPhrase(x.CriteriaDescription ?? string.Empty),
                TopicPhrase = NormalizeSearchPhrase(x.TopicDescription ?? string.Empty),
                SubjectPhrase = NormalizeSearchPhrase(x.SubjectDescription ?? string.Empty),
                CriteriaTokens = BuildTargetTokens(x.CriteriaDescription ?? string.Empty),
                TopicTokens = BuildTargetTokens($"{x.TopicCode} {x.TopicDescription} {x.TopicPurpose}"),
                SubjectTokens = BuildTargetTokens($"{x.SubjectCode} {x.SubjectDescription} {x.SubjectPurpose}")
            }).ToList();
        }

        private static TopicEvidenceItem BuildEmptyTopicEvidenceItem(CurriculumTopicTarget topic)
        {
            return new TopicEvidenceItem
            {
                TopicId = topic.TopicId,
                TopicCode = topic.TopicCode,
                TopicDescription = topic.TopicDescription,
                SubjectCode = topic.SubjectCode,
                SubjectDescription = topic.SubjectDescription,
                CoverageBand = "gap",
                CoverageBandLabel = "Gap"
            };
        }

        private static List<TopicSourceMapItem> BuildTopicSourceMap(
            IReadOnlyList<CurriculumTopicTarget> topics,
            IReadOnlyList<SourceMaterialChunk> chunks,
            IReadOnlyDictionary<string, List<int>> chunkIndex)
        {
            var results = new List<TopicSourceMapItem>();
            foreach (var topic in topics)
            {
                results.Add(BuildTopicSourceMapItem(topic, chunks, chunkIndex));
            }

            return results;
        }

        private static TopicSourceMapItem BuildTopicSourceMapItem(
            CurriculumTopicTarget topic,
            IReadOnlyList<SourceMaterialChunk> chunks,
            IReadOnlyDictionary<string, List<int>> chunkIndex)
        {
            var candidateIds = ResolveCandidateChunkIds(topic.TopicTokens.Concat(topic.SubjectTokens), chunks, chunkIndex);
            var matches = candidateIds
                .Select(id => ScoreTopicMatch(topic, chunks[id]))
                .Where(match => match.Score >= 6)
                .OrderByDescending(match => match.Score)
                .ThenByDescending(match => match.SourcePriority)
                .ThenByDescending(match => match.Confidence)
                .ThenBy(match => match.Citation, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList();

            return new TopicSourceMapItem
            {
                TopicId = topic.TopicId,
                TopicCode = topic.TopicCode,
                TopicDescription = topic.TopicDescription,
                SubjectCode = topic.SubjectCode,
                SubjectDescription = topic.SubjectDescription,
                Matches = matches
            };
        }

        private static TopicEvidenceItem BuildTopicEvidenceItem(TopicSourceMapItem map)
        {
            var matches = (map.Matches ?? new List<ChunkMatch>())
                .OrderByDescending(match => match.Confidence)
                .ThenByDescending(match => match.Score)
                .ThenBy(match => match.Citation, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var distinctSourceCount = matches
                .Select(match => match.MaterialId)
                .Where(id => id > 0)
                .Distinct()
                .Count();

            var topMatches = matches.Take(3).ToList();
            var bestConfidencePercent = matches.Count == 0
                ? 0
                : ClampPercent(matches.Max(match => match.Confidence * 100d));
            var averageConfidencePercent = topMatches.Count == 0
                ? 0
                : ClampPercent(topMatches.Average(match => match.Confidence * 100d));
            var evidenceDepthPercent = ClampPercent((Math.Min(5, matches.Count) / 5d) * 100d);
            var sourceDiversityPercent = ClampPercent((Math.Min(3, distinctSourceCount) / 3d) * 100d);
            var coveragePercent = matches.Count == 0
                ? 0
                : ClampPercent(
                    (averageConfidencePercent * 0.50d) +
                    (evidenceDepthPercent * 0.30d) +
                    (sourceDiversityPercent * 0.20d));

            var coverageBand = DetermineCoverageBand(coveragePercent);

            return new TopicEvidenceItem
            {
                TopicId = map.TopicId,
                TopicCode = map.TopicCode,
                TopicDescription = map.TopicDescription,
                SubjectCode = map.SubjectCode,
                SubjectDescription = map.SubjectDescription,
                CoveragePercent = coveragePercent,
                CoverageBand = coverageBand,
                CoverageBandLabel = DetermineCoverageBandLabel(coverageBand),
                EvidenceCount = matches.Count,
                DistinctSourceCount = distinctSourceCount,
                BestConfidencePercent = bestConfidencePercent,
                AverageConfidencePercent = averageConfidencePercent,
                TopCitations = matches
                    .Select(match => match.Citation)
                    .Where(citation => !string.IsNullOrWhiteSpace(citation))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToList(),
                TopEvidence = topMatches
                    .Select(match => new TopicEvidenceSource
                    {
                        Citation = match.Citation,
                        ConfidencePercent = ClampPercent(match.Confidence * 100d),
                        KnowledgeSourceType = match.KnowledgeSourceType,
                        MaterialTitle = match.MaterialTitle,
                        PageNumber = match.PageNumber,
                        Excerpt = match.Excerpt
                    })
                    .ToList()
            };
        }

        private static List<CriteriaSourceMapItem> BuildCriteriaSourceMap(
            IReadOnlyList<CurriculumCriteriaTarget> criteriaTargets,
            IReadOnlyList<SourceMaterialChunk> chunks,
            IReadOnlyDictionary<string, List<int>> chunkIndex)
        {
            var results = new List<CriteriaSourceMapItem>();
            foreach (var criteria in criteriaTargets)
            {
                var candidateIds = ResolveCandidateChunkIds(criteria.CriteriaTokens.Concat(criteria.TopicTokens).Concat(criteria.SubjectTokens), chunks, chunkIndex);
                var matches = candidateIds
                    .Select(id => ScoreCriteriaMatch(criteria, chunks[id]))
                    .Where(match => match.Score >= 8)
                    .OrderByDescending(match => match.Score)
                    .ThenByDescending(match => match.SourcePriority)
                    .ThenByDescending(match => match.Confidence)
                    .ThenBy(match => match.Citation, StringComparer.OrdinalIgnoreCase)
                    .Take(4)
                    .ToList();

                results.Add(new CriteriaSourceMapItem
                {
                    CriteriaId = criteria.CriteriaId,
                    CriteriaDescription = criteria.CriteriaDescription,
                    TopicId = criteria.TopicId,
                    TopicCode = criteria.TopicCode,
                    TopicDescription = criteria.TopicDescription,
                    SubjectCode = criteria.SubjectCode,
                    SubjectDescription = criteria.SubjectDescription,
                    Matches = matches
                });
            }

            return results;
        }

        private static List<int> ResolveCandidateChunkIds(
            IEnumerable<string> targetTokens,
            IReadOnlyList<SourceMaterialChunk> chunks,
            IReadOnlyDictionary<string, List<int>> chunkIndex)
        {
            var candidates = new Dictionary<int, int>();
            foreach (var token in targetTokens
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24))
            {
                if (!chunkIndex.TryGetValue(token, out var ids))
                {
                    continue;
                }

                foreach (var id in ids.Take(240))
                {
                    candidates[id] = candidates.TryGetValue(id, out var count) ? count + 1 : 1;
                }
            }

            if (candidates.Count == 0)
            {
                return chunks.Count <= 180
                    ? Enumerable.Range(0, chunks.Count).ToList()
                    : new List<int>();
            }

            return candidates
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Key)
                .Take(140)
                .Select(x => x.Key)
                .ToList();
        }

        private static ChunkMatch ScoreTopicMatch(CurriculumTopicTarget target, SourceMaterialChunk chunk)
        {
            var tokenSet = chunk.IndexTokens;
            var score = 0;

            if (ContainsPhrase(chunk.SearchText, target.TopicPhrase))
            {
                score += 12;
            }

            if (ContainsPhrase(chunk.SearchText, target.SubjectPhrase))
            {
                score += 6;
            }

            if (!string.IsNullOrWhiteSpace(target.TopicCode) &&
                chunk.SearchText.Contains($" {NormalizeSearchPhrase(target.TopicCode)} ", StringComparison.Ordinal))
            {
                score += 4;
            }

            var topicOverlap = CountOverlap(target.TopicTokens, tokenSet);
            var subjectOverlap = CountOverlap(target.SubjectTokens, tokenSet);
            score += Math.Min(12, topicOverlap * 2);
            score += Math.Min(6, subjectOverlap);
            score = ApplySourcePriorityBoost(score, chunk.SourcePriority, topicOverlap + subjectOverlap);

            return BuildChunkMatch(chunk, score, topicOverlap, subjectOverlap, 0);
        }

        private static ChunkMatch ScoreCriteriaMatch(CurriculumCriteriaTarget target, SourceMaterialChunk chunk)
        {
            var tokenSet = chunk.IndexTokens;
            var score = 0;

            if (ContainsPhrase(chunk.SearchText, target.CriteriaPhrase))
            {
                score += 12;
            }

            if (ContainsPhrase(chunk.SearchText, target.TopicPhrase))
            {
                score += 9;
            }

            if (ContainsPhrase(chunk.SearchText, target.SubjectPhrase))
            {
                score += 5;
            }

            var criteriaOverlap = CountOverlap(target.CriteriaTokens, tokenSet);
            var topicOverlap = CountOverlap(target.TopicTokens, tokenSet);
            var subjectOverlap = CountOverlap(target.SubjectTokens, tokenSet);

            score += Math.Min(14, criteriaOverlap * 2);
            score += Math.Min(8, topicOverlap * 2);
            score += Math.Min(5, subjectOverlap);
            score = ApplySourcePriorityBoost(score, chunk.SourcePriority, criteriaOverlap + topicOverlap + subjectOverlap);

            return BuildChunkMatch(chunk, score, topicOverlap, subjectOverlap, criteriaOverlap);
        }

        private static ChunkMatch BuildChunkMatch(
            SourceMaterialChunk chunk,
            int score,
            int topicOverlap,
            int subjectOverlap,
            int criteriaOverlap)
        {
            return new ChunkMatch
            {
                MaterialId = chunk.MaterialId,
                ChunkId = chunk.Id,
                MaterialTitle = chunk.MaterialTitle,
                FileName = chunk.FileName,
                KnowledgeNumber = chunk.KnowledgeNumber,
                KnowledgeSourceType = chunk.KnowledgeSourceType,
                Url = chunk.Url,
                Citation = chunk.Citation,
                ChapterTitle = chunk.ChapterTitle,
                PageNumber = chunk.PageNumber,
                Score = score,
                Confidence = ScoreToConfidence(score),
                SourcePriority = chunk.SourcePriority,
                TopicTokenMatches = topicOverlap,
                SubjectTokenMatches = subjectOverlap,
                CriteriaTokenMatches = criteriaOverlap,
                Excerpt = chunk.Excerpt
            };
        }

        private async Task<DraftWriteResult> SeedLessonPlanDraftsAsync(
            ApplicationDbContext db,
            int qualificationId,
            IReadOnlyList<CriteriaSourceMapItem> criteriaMaps,
            bool populateLessonPlanDrafts,
            CancellationToken cancellationToken)
        {
            var result = new DraftWriteResult();
            if (!populateLessonPlanDrafts)
            {
                result.Warnings.Add("Lesson-plan draft seeding was disabled for this pipeline run.");
                return result;
            }

            var authoringRules = LearningMaterialAuthoringRulesStore.Load();

            var existingRows = await db.LecturerToolkitEntries
                .Where(x => x.QualificationsId == qualificationId)
                .ToListAsync(cancellationToken);

            foreach (var map in criteriaMaps)
            {
                var matches = map.Matches.Where(x => x.Confidence >= 0.65d).Take(3).ToList();
                if (matches.Count == 0)
                {
                    result.Skipped++;
                    result.Entries.Add(new LessonPlanDraftArtifact
                    {
                        CriteriaId = map.CriteriaId,
                        CriteriaDescription = map.CriteriaDescription,
                        SubjectCode = map.SubjectCode,
                        TopicCode = map.TopicCode,
                        Status = "skipped_no_confident_match",
                        ConfidencePercent = 0
                    });
                    continue;
                }

                var manualRows = existingRows
                    .Where(x => x.AssessmentCriteriaId == map.CriteriaId && !ContainsAutoDraftMarker(x.LearningAids))
                    .ToList();
                if (manualRows.Count > 0)
                {
                    result.Skipped++;
                    result.Entries.Add(new LessonPlanDraftArtifact
                    {
                        CriteriaId = map.CriteriaId,
                        CriteriaDescription = map.CriteriaDescription,
                        SubjectCode = map.SubjectCode,
                        TopicCode = map.TopicCode,
                        Status = "skipped_manual_row_present",
                        ConfidencePercent = (int)Math.Round(matches[0].Confidence * 100d),
                        Citations = matches.Select(x => x.Citation).ToList()
                    });
                    continue;
                }

                var generatedRow = existingRows
                    .FirstOrDefault(x => x.AssessmentCriteriaId == map.CriteriaId && ContainsAutoDraftMarker(x.LearningAids));

                var lessonContent = await BuildLessonPlanContentAsync(map, matches, authoringRules, cancellationToken);
                var hasGroundedLessonContent = HasGroundedLessonPlanContent(lessonContent);
                var learningAids = BuildLearningAids(map, matches, hasGroundedLessonContent);
                if (!hasGroundedLessonContent)
                {
                    if (generatedRow != null)
                    {
                        generatedRow.SubjectCode = map.SubjectCode;
                        generatedRow.SubjectDescription = map.SubjectDescription;
                        generatedRow.AssessmentCriteriaDescription = map.CriteriaDescription;
                        generatedRow.Lpn = BuildAutoLpn(map);
                        generatedRow.LessonPlanDescription = BuildLessonPlanDescription(map);
                        generatedRow.LessonPlanContent = lessonContent;
                        generatedRow.LecturerActions = string.Empty;
                        generatedRow.LearnerActions = string.Empty;
                        generatedRow.LearningAids = learningAids;

                        result.Updated++;
                        result.Entries.Add(BuildDraftArtifact(map, matches, "updated_insufficient_grounding"));
                    }
                    else
                    {
                        result.Skipped++;
                        result.Entries.Add(BuildDraftArtifact(map, matches, "skipped_insufficient_grounding"));
                    }

                    continue;
                }

                if (generatedRow == null)
                {
                    generatedRow = new LecturerToolkitEntry
                    {
                        QualificationsId = qualificationId,
                        SubjectCode = map.SubjectCode,
                        SubjectDescription = map.SubjectDescription,
                        AssessmentCriteriaId = map.CriteriaId,
                        AssessmentCriteriaDescription = map.CriteriaDescription,
                        Lpn = BuildAutoLpn(map),
                        LessonPlanDescription = BuildLessonPlanDescription(map),
                        LessonPlanContent = lessonContent,
                        LecturerActions = BuildLecturerActions(map, matches, authoringRules),
                        LearnerActions = BuildLearnerActions(map, matches, authoringRules),
                        LearningAids = learningAids
                    };

                    db.LecturerToolkitEntries.Add(generatedRow);
                    existingRows.Add(generatedRow);
                    result.Created++;
                    result.Entries.Add(BuildDraftArtifact(map, matches, "created"));
                }
                else
                {
                    generatedRow.SubjectCode = map.SubjectCode;
                    generatedRow.SubjectDescription = map.SubjectDescription;
                    generatedRow.AssessmentCriteriaDescription = map.CriteriaDescription;
                    generatedRow.Lpn = BuildAutoLpn(map);
                    generatedRow.LessonPlanDescription = BuildLessonPlanDescription(map);
                    generatedRow.LessonPlanContent = lessonContent;
                    generatedRow.LecturerActions = BuildLecturerActions(map, matches, authoringRules);
                    generatedRow.LearnerActions = BuildLearnerActions(map, matches, authoringRules);
                    generatedRow.LearningAids = learningAids;

                    result.Updated++;
                    result.Entries.Add(BuildDraftArtifact(map, matches, "updated"));
                }
            }

            if (result.Created > 0 || result.Updated > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            if (result.Created == 0 && result.Updated == 0 && result.Skipped > 0)
            {
                result.Warnings.Add("All lesson-plan draft candidates were skipped because they already have manual rows or lacked confident, grounded subject-matter coverage.");
            }

            return result;
        }

        private static LessonPlanDraftArtifact BuildDraftArtifact(CriteriaSourceMapItem map, IReadOnlyList<ChunkMatch> matches, string status)
        {
            return new LessonPlanDraftArtifact
            {
                CriteriaId = map.CriteriaId,
                CriteriaDescription = map.CriteriaDescription,
                SubjectCode = map.SubjectCode,
                TopicCode = map.TopicCode,
                Status = status,
                ConfidencePercent = (int)Math.Round(matches[0].Confidence * 100d),
                Citations = matches.Select(x => x.Citation).ToList()
            };
        }

        private static string BuildLessonPlanDescription(CriteriaSourceMapItem map)
        {
            if (!string.IsNullOrWhiteSpace(map.TopicDescription))
            {
                return $"{map.TopicCode} - {map.TopicDescription}".Trim(' ', '-');
            }

            return map.CriteriaDescription;
        }

        private static string BuildAutoLpn(CriteriaSourceMapItem map)
        {
            var topicCode = Regex.Replace(map.TopicCode ?? string.Empty, @"[^A-Za-z0-9]+", string.Empty);
            if (string.IsNullOrWhiteSpace(topicCode))
            {
                topicCode = $"AC{map.CriteriaId}";
            }

            return $"AUTO-{topicCode}-{map.CriteriaId:D4}";
        }

        private async Task<string> BuildLessonPlanContentAsync(
            CriteriaSourceMapItem map,
            IReadOnlyList<ChunkMatch> matches,
            LearningMaterialAuthoringRules authoringRules,
            CancellationToken cancellationToken)
        {
            var compiled = NormalizeGeneratedLessonDraftContent(BuildSourceBackedLearnerGuideContent(map, matches, authoringRules));
            if (HasComprehensiveLearnerGuideContent(compiled))
            {
                return compiled;
            }

            if (AiRuntime.AllowGoogle())
            {
                var geminiContent = await TryBuildGeminiLessonContentAsync(map, compiled, cancellationToken);
                if (HasComprehensiveLearnerGuideContent(geminiContent))
                {
                    return NormalizeGeneratedLessonDraftContent(geminiContent);
                }
            }

            var webContent = await TryBuildOpenAiSearchLessonContentAsync(map, compiled, cancellationToken);
            if (HasComprehensiveLearnerGuideContent(webContent))
            {
                return NormalizeGeneratedLessonDraftContent(webContent);
            }

            return BuildScopedLearnerGuideFallbackContent(map, compiled);
        }

        private static string BuildKeyConceptSummary(CriteriaSourceMapItem map, IReadOnlyList<ChunkMatch> matches)
        {
            var keywords = ExtractWeightedKeywords(map, matches)
                .Where(IsUsableEvidenceKeyword)
                .Take(5)
                .ToList();
            if (keywords.Count < 2)
            {
                return string.Empty;
            }

            return $"{BuildTopicLabel(map)} covers {JoinAsPhrase(keywords)}.";
        }

        private static string BuildSourceBackedLearnerGuideContent(
            CriteriaSourceMapItem map,
            IReadOnlyList<ChunkMatch> matches,
            LearningMaterialAuthoringRules authoringRules)
        {
            _ = authoringRules;
            var sourceSentences = SelectLearnerGuideSourceSentences(map, matches, maxSentences: 12);
            if (sourceSentences.Count == 0)
            {
                return string.Empty;
            }

            var processSentences = sourceSentences
                .Where(LooksLikeProcedureOrPracticeSentence)
                .Take(6)
                .ToList();

            var sb = new StringBuilder();
            foreach (var paragraph in BuildParagraphs(sourceSentences.Take(10), maxSentencesPerParagraph: 3)
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                sb.AppendLine(paragraph);
                sb.AppendLine();
            }

            if (processSentences.Count > 0)
            {
                var step = 1;
                foreach (var sentence in processSentences)
                {
                    sb.AppendLine($"{step}. {NormalizeSentence(sentence)}");
                    step++;
                }
                sb.AppendLine();
            }

            return CleanGeneratedText(sb.ToString());
        }

        private async Task<string> TryBuildGeminiLessonContentAsync(
            CriteriaSourceMapItem map,
            string localDraft,
            CancellationToken cancellationToken)
        {
            if (!AiRuntime.AllowGoogle())
            {
                return string.Empty;
            }

            var prompt = BuildGeminiLessonPrompt(map, localDraft);
            try
            {
                var text = await GoogleGeminiClient.GenerateContentAsync(prompt, AiRuntime.GetGeminiModel());
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Google Gemini learner-guide lesson synthesis failed for criteria {CriteriaId}.", map.CriteriaId);
            }

            return string.Empty;
        }

        private static string BuildGeminiLessonPrompt(CriteriaSourceMapItem map, string localDraft)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are an expert South African vocational curriculum author creating official TVET courseware.");
            sb.AppendLine("Your goal is to write a comprehensive, rigorous, publication-grade Learner Guide chapter section for South African TVET colleges.");
            sb.AppendLine("The content must be perfectly aligned with the Quality Council for Trades and Occupations (QCTO) Curriculum and Assessment Specifications.");
            sb.AppendLine();
            sb.AppendLine($"Subject: {NormalizeSentence(map.SubjectDescription)} ({map.SubjectCode})");
            sb.AppendLine($"Topic: {NormalizeSentence(map.TopicDescription)} ({map.TopicCode})");
            sb.AppendLine($"Assessment Criteria to Satisfy: {NormalizeSentence(map.CriteriaDescription)}");
            sb.AppendLine();
            sb.AppendLine("Pedagogical Requirements:");
            sb.AppendLine("1. Write full, publication-ready educational prose suitable for TVET learners. Do not use generic filler or study recommendations.");
            sb.AppendLine("2. Directly explain the theoretical concepts, core scientific/technical principles, terminology, and operational standards.");
            sb.AppendLine("3. Provide clear step-by-step procedures, tool usage, safety precautions (OHSA standards), and practical workshop guidelines.");
            sb.AppendLine("4. Explicitly explain what constitutes competent performance to meet the QCTO Assessment Criteria.");
            sb.AppendLine("5. Include 2-3 formative self-check questions with answers for student revision at the end of the section.");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(localDraft))
            {
                sb.AppendLine("Extracted Technical Evidence from Uploaded College Source Material:");
                sb.AppendLine(localDraft);
                sb.AppendLine();
            }
            sb.AppendLine("Now synthesize and write the complete, learner-facing textbook section:");
            return sb.ToString();
        }

        private async Task<string> TryBuildOpenAiSearchLessonContentAsync(
            CriteriaSourceMapItem map,
            string localDraft,
            CancellationToken cancellationToken)
        {
            if (!AiRuntime.AllowOpenAi())
            {
                return string.Empty;
            }

            var key = (Secrets.GetOpenAIKey() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            var payload = new
            {
                model = AiRuntime.GetOpenAiModel("gpt-5-mini"),
                tools = new[]
                {
                    new { type = "web_search" }
                },
                tool_choice = "auto",
                input = BuildOpenAiSearchLessonPrompt(map, localDraft)
            };

            using var msg = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            try
            {
                using var response = await OpenAiHttpClient.SendAsync(msg, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("OpenAI learner-guide web-search fallback failed for criteria {CriteriaId}: HTTP {StatusCode}.", map.CriteriaId, (int)response.StatusCode);
                    return string.Empty;
                }

                return ExtractResponsesOutputText(body);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenAI learner-guide web-search fallback failed for criteria {CriteriaId}.", map.CriteriaId);
                return string.Empty;
            }
        }

        private static string BuildOpenAiSearchLessonPrompt(CriteriaSourceMapItem map, string localDraft)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Write learner-facing vocational textbook content for a South African occupational learner guide.");
            sb.AppendLine("Use the curriculum topic and assessment wording only as scope anchors and paragraph-heading guidance.");
            sb.AppendLine("Do not write curriculum mapping, evidence requirements, source suggestions, source labels, citations, or statements that more material must be learned elsewhere.");
            sb.AppendLine("Write the actual lesson content as natural learner-guide prose. Do not force fixed headings or repeated templates.");
            sb.AppendLine("Prefer uploaded subject-matter wording and sequence where it already contains the explanation. If the topic is procedural, explain how the task is performed step by step.");
            sb.AppendLine();
            sb.AppendLine($"Subject: {NormalizeSentence(map.SubjectDescription)}");
            sb.AppendLine($"Topic: {NormalizeSentence(map.TopicDescription)}");
            sb.AppendLine($"Assessment scope: {NormalizeSentence(map.CriteriaDescription)}");
            if (!string.IsNullOrWhiteSpace(localDraft))
            {
                sb.AppendLine();
                sb.AppendLine("Locally mapped uploaded material summary to preserve where useful:");
                sb.AppendLine(localDraft.Length <= 1800 ? localDraft : localDraft[..1800]);
            }

            return sb.ToString();
        }

        private static string ExtractResponsesOutputText(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return string.Empty;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("output_text", out var outputText) &&
                    outputText.ValueKind == JsonValueKind.String)
                {
                    return outputText.GetString() ?? string.Empty;
                }

                var pieces = new List<string>();
                if (doc.RootElement.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in output.EnumerateArray())
                    {
                        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        foreach (var part in content.EnumerateArray())
                        {
                            if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                            {
                                pieces.Add(text.GetString() ?? string.Empty);
                            }
                        }
                    }
                }

                return string.Join("\n\n", pieces.Where(x => !string.IsNullOrWhiteSpace(x)));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string BuildEvidenceBackedNarrative(
            CriteriaSourceMapItem map,
            IReadOnlyList<ChunkMatch> matches,
            bool includeLead = true)
        {
            var selectedSentences = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var targetTokens = BuildTargetTokens($"{map.SubjectDescription} {map.TopicDescription} {map.CriteriaDescription}");
            foreach (var match in matches)
            {
                foreach (var sentence in SelectEvidenceSentences(match.Excerpt))
                {
                    var normalized = NormalizeSearchPhrase(sentence);
                    if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                    {
                        continue;
                    }

                    var overlap = BuildTargetTokens(sentence)
                        .Count(token => targetTokens.Contains(token, StringComparer.OrdinalIgnoreCase));
                    if (overlap < 2)
                    {
                        continue;
                    }

                    if (LooksLikeGenericEvidenceInstruction(sentence))
                    {
                        continue;
                    }

                    selectedSentences.Add(sentence);
                    if (selectedSentences.Count >= 6)
                    {
                        break;
                    }
                }

                if (selectedSentences.Count >= 6)
                {
                    break;
                }
            }

            if (selectedSentences.Count == 0)
            {
                return string.Empty;
            }

            return CleanGeneratedText(string.Join(" ", selectedSentences));
        }

        private static IReadOnlyList<string> ExtractWeightedKeywords(CriteriaSourceMapItem map, IReadOnlyList<ChunkMatch> matches)
        {
            var weights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in BuildTargetTokens($"{map.SubjectDescription} {map.TopicDescription} {map.CriteriaDescription}"))
            {
                weights[token] = weights.TryGetValue(token, out var value) ? value + 2 : 2;
            }

            foreach (var match in matches)
            {
                foreach (var token in BuildTargetTokens(match.Excerpt).Take(24))
                {
                    weights[token] = weights.TryGetValue(token, out var value) ? value + 1 : 1;
                }
            }

            return weights
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Key)
                .Take(8)
                .ToList();
        }

        private static IEnumerable<string> SelectEvidenceSentences(string excerpt)
        {
            return Regex.Split(excerpt ?? string.Empty, @"(?<=[\.\!\?])\s+")
                .Select(CleanGeneratedText)
                .Where(sentence => DocumentTextCleaner.WordCount(sentence) >= 7)
                .Where(sentence => sentence.Length <= 260)
                .Where(sentence => !LooksLikeGenericEvidenceInstruction(sentence))
                .Take(4)
                .ToList();
        }

        private static List<string> SelectLearnerGuideSourceSentences(
            CriteriaSourceMapItem map,
            IReadOnlyList<ChunkMatch> matches,
            int maxSentences)
        {
            var selected = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var targetTokens = BuildTargetTokens($"{map.SubjectDescription} {map.TopicDescription} {map.CriteriaDescription}");
            foreach (var match in matches.OrderByDescending(x => x.Confidence))
            {
                foreach (var sentence in SelectEvidenceSentences(match.Excerpt))
                {
                    var normalized = NormalizeSearchPhrase(sentence);
                    if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                    {
                        continue;
                    }

                    var overlap = BuildTargetTokens(sentence)
                        .Count(token => targetTokens.Contains(token, StringComparer.OrdinalIgnoreCase));
                    if (overlap < 1 && selected.Count > 0)
                    {
                        continue;
                    }

                    if (LooksLikeGenericEvidenceInstruction(sentence) || LooksLikeAssessmentCriteriaRestatement(sentence))
                    {
                        continue;
                    }

                    selected.Add(sentence);
                    if (selected.Count >= maxSentences)
                    {
                        return selected;
                    }
                }
            }

            return selected;
        }

        private static string BuildParagraphs(IEnumerable<string> sentences, int maxSentencesPerParagraph)
        {
            var parts = sentences
                .Select(NormalizeSentence)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
            if (parts.Count == 0)
            {
                return string.Empty;
            }

            var paragraphs = new List<string>();
            for (var i = 0; i < parts.Count; i += Math.Max(1, maxSentencesPerParagraph))
            {
                paragraphs.Add(string.Join(" ", parts.Skip(i).Take(maxSentencesPerParagraph)));
            }

            return string.Join("\n\n", paragraphs);
        }

        private static List<string> BuildSourceBackedKeyTerms(CriteriaSourceMapItem map, IReadOnlyList<string> sourceSentences)
        {
            var terms = ExtractWeightedKeywords(map, Array.Empty<ChunkMatch>())
                .Where(IsUsableEvidenceKeyword)
                .Take(5)
                .ToList();
            var lines = new List<string>();
            foreach (var term in terms)
            {
                var sentence = sourceSentences.FirstOrDefault(x =>
                    NormalizeSearchPhrase(x).Contains($" {NormalizeSearchPhrase(term)} ", StringComparison.Ordinal));
                if (string.IsNullOrWhiteSpace(sentence))
                {
                    continue;
                }

                lines.Add($"{ToTitleCase(term)}: {NormalizeSentence(sentence)}");
            }

            return lines
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();
        }

        private static bool LooksLikeProcedureOrPracticeSentence(string? value)
        {
            var normalized = NormalizeSearchPhrase(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            return normalized.Contains(" step ", StringComparison.Ordinal)
                || normalized.Contains(" inspect", StringComparison.Ordinal)
                || normalized.Contains(" check", StringComparison.Ordinal)
                || normalized.Contains(" test", StringComparison.Ordinal)
                || normalized.Contains(" remove", StringComparison.Ordinal)
                || normalized.Contains(" install", StringComparison.Ordinal)
                || normalized.Contains(" adjust", StringComparison.Ordinal)
                || normalized.Contains(" maintain", StringComparison.Ordinal)
                || normalized.Contains(" repair", StringComparison.Ordinal)
                || normalized.Contains(" service", StringComparison.Ordinal)
                || normalized.Contains(" ensure", StringComparison.Ordinal)
                || normalized.Contains(" record", StringComparison.Ordinal)
                || normalized.Contains(" safety", StringComparison.Ordinal);
        }

        private static string BuildWorkplaceExample(CriteriaSourceMapItem map, IReadOnlyList<string> sourceSentences)
        {
            var practicalSentence = sourceSentences.FirstOrDefault(LooksLikeProcedureOrPracticeSentence)
                ?? sourceSentences.FirstOrDefault()
                ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(practicalSentence))
            {
                return $"In a workplace task about {NormalizeSentence(map.TopicDescription)}, you would first identify the condition or fault, then apply the correct checks and actions. {NormalizeSentence(practicalSentence)} The final decision must be based on whether the equipment, process, or result is safe, functional, and within the required standard.";
            }

            return $"In a workplace task about {NormalizeSentence(map.TopicDescription)}, you would identify the job requirement, prepare the tools and safety controls, carry out the correct sequence, check the result, and record any fault or follow-up action.";
        }

        private static string ToTitleCase(string value)
        {
            var cleaned = NormalizeLine(value);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return string.Empty;
            }

            return char.ToUpperInvariant(cleaned[0]) + cleaned[1..];
        }

        private static string BuildCriteriaFocus(CriteriaSourceMapItem map)
        {
            _ = map;
            return string.Empty;
        }

        private static string BuildLecturerActions(
            CriteriaSourceMapItem map,
            IReadOnlyList<ChunkMatch> matches,
            LearningMaterialAuthoringRules authoringRules)
        {
            if (authoringRules.DisableRigidLessonTemplate)
            {
                return string.Empty;
            }

            return CleanGeneratedText(
                $"Introduce {BuildTopicLabel(map)} as a practical vocational lesson. Demonstrate the correct concepts, sequence, tools, safety requirements, and quality checks, then connect the explanation back to the full assessment criteria that the learner must meet.");
        }

        private static string BuildLearnerActions(
            CriteriaSourceMapItem map,
            IReadOnlyList<ChunkMatch> matches,
            LearningMaterialAuthoringRules authoringRules)
        {
            _ = matches;
            if (authoringRules.DisableRigidLessonTemplate)
            {
                return string.Empty;
            }

            return CleanGeneratedText(
                $"Use the explained subject matter for {BuildTopicLabel(map)}, identify the key technical concepts, practise the correct sequence or decision points, and explain how the topic helps you {NormalizeSentence(map.CriteriaDescription)} in real workplace conditions.");
        }

        private static string BuildLearningAids(CriteriaSourceMapItem map, IReadOnlyList<ChunkMatch> matches, bool hasGroundedLessonContent)
        {
            _ = map;
            _ = matches;
            var sb = new StringBuilder();
            sb.AppendLine(AutoDraftMarker);
            if (!hasGroundedLessonContent)
            {
                sb.AppendLine(AutoDraftCoverageGapMarker);
            }

            return CleanGeneratedText(sb.ToString());
        }

        private static string BuildTopicLabel(CriteriaSourceMapItem map)
        {
            return $"{map.TopicCode} {map.TopicDescription}".Trim();
        }

        private static bool ContainsAutoDraftMarker(string? value)
        {
            return (value ?? string.Empty).IndexOf(AutoDraftMarker, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasGroundedLessonPlanContent(string? value)
        {
            var cleaned = NormalizeGeneratedLessonDraftContent(value ?? string.Empty)
                .Replace(AutoDraftMarker, string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace(AutoDraftCoverageGapMarker, string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
            return !string.IsNullOrWhiteSpace(cleaned);
        }

        private static bool HasComprehensiveLearnerGuideContent(string? value)
        {
            var cleaned = NormalizeGeneratedLessonDraftContent(value ?? string.Empty)
                .Replace(AutoDraftMarker, string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace(AutoDraftCoverageGapMarker, string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return false;
            }

            var wordCount = DocumentTextCleaner.WordCount(cleaned);
            if (wordCount < 95)
            {
                return false;
            }

            return wordCount >= 95;
        }

        private static string BuildScopedLearnerGuideFallbackContent(CriteriaSourceMapItem map, string localDraft)
        {
            if (!string.IsNullOrWhiteSpace(localDraft))
            {
                return CleanGeneratedText(localDraft);
            }

            return string.Empty;
        }

        private static bool IsUsableEvidenceKeyword(string? value)
        {
            var token = NormalizeLine(value);
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            if (token.Length < 4 || token.Length > 32)
            {
                return false;
            }

            return token.Count(char.IsLetter) >= 3;
        }

        private static bool LooksLikeGenericEvidenceInstruction(string? value)
        {
            var normalized = NormalizeSearchPhrase(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            return normalized.StartsWith("focus your study of", StringComparison.Ordinal)
                || normalized.StartsWith("build your understanding of", StringComparison.Ordinal)
                || normalized.StartsWith("learn what each term means", StringComparison.Ordinal)
                || normalized.StartsWith("where it appears in practice", StringComparison.Ordinal)
                || normalized.StartsWith("you must understand", StringComparison.Ordinal)
                || normalized.StartsWith("study the explanation below", StringComparison.Ordinal)
                || normalized.StartsWith("follow the sequence step by step", StringComparison.Ordinal)
                || normalized.StartsWith("work through the topic", StringComparison.Ordinal)
                || normalized.StartsWith("study the mapped source content", StringComparison.Ordinal)
                || normalized.StartsWith("study the lesson material", StringComparison.Ordinal)
                || normalized.StartsWith("this lesson develops the learner s ability to", StringComparison.Ordinal);
        }

        private static string NormalizeGeneratedLessonDraftContent(string? value)
        {
            var raw = CleanGeneratedText(value ?? string.Empty)
                .Replace(AutoDraftMarker, string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace(AutoDraftCoverageGapMarker, string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var kept = new List<string>();
            foreach (var line in raw
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var cleanedLine = CleanGeneratedText(line);
                if (string.IsNullOrWhiteSpace(cleanedLine))
                {
                    continue;
                }

                var sentences = Regex.Split(cleanedLine, @"(?<=[\.\!\?])\s+")
                    .Select(CleanGeneratedText)
                    .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
                    .Where(sentence => !LooksLikeGenericEvidenceInstruction(sentence))
                    .Where(sentence => !LooksLikeAssessmentCriteriaRestatement(sentence))
                    .ToList();
                if (sentences.Count == 0)
                {
                    continue;
                }

                kept.Add(string.Join(" ", sentences));
            }

            return CleanGeneratedText(string.Join("\n", kept));
        }

        private static bool LooksLikeAssessmentCriteriaRestatement(string? value)
        {
            var normalized = NormalizeLine(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            if (normalized.Count(ch => ch == '|') >= 2)
            {
                return true;
            }

            var compact = NormalizeSearchPhrase(normalized);
            return compact.StartsWith("define and describe ", StringComparison.Ordinal)
                || compact.StartsWith("discuss the impact ", StringComparison.Ordinal)
                || compact.StartsWith("describe the processes ", StringComparison.Ordinal);
        }

        private static ChunkArtifact BuildChunkArtifact(SourceMaterialChunk chunk)
        {
            return new ChunkArtifact
            {
                ChunkId = chunk.Id,
                MaterialId = chunk.MaterialId,
                MaterialTitle = chunk.MaterialTitle,
                FileName = chunk.FileName,
                KnowledgeNumber = chunk.KnowledgeNumber,
                KnowledgeSourceType = chunk.KnowledgeSourceType,
                Citation = chunk.Citation,
                ChapterTitle = chunk.ChapterTitle,
                PageNumber = chunk.PageNumber,
                Excerpt = chunk.Excerpt
            };
        }

        private static async Task<string> WriteJsonArtifactAsync<T>(
            string directory,
            string fileName,
            T value,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            var json = JsonSerializer.Serialize(value, JsonOptions);
            await File.WriteAllTextAsync(path, json, cancellationToken);
            return path;
        }

        private static string BuildExcerpt(string text)
        {
            var trimmed = CleanGeneratedText(text);
            if (trimmed.Length <= 520)
            {
                return trimmed;
            }

            var excerpt = trimmed[..520].TrimEnd();
            var lastStop = excerpt.LastIndexOfAny(new[] { '.', '!', '?' });
            if (lastStop >= 180)
            {
                excerpt = excerpt[..(lastStop + 1)];
            }

            return excerpt.Trim();
        }

        private static string BuildCitation(SourceMaterialRow material, int pageNumber, string chapterTitle)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(material.OriginalSourceName))
            {
                parts.Add(material.OriginalSourceName);
            }
            else if (!string.IsNullOrWhiteSpace(material.Title))
            {
                parts.Add(material.Title);
            }

            if (pageNumber > 0)
            {
                parts.Add($"p. {pageNumber}");
            }

            if (!string.IsNullOrWhiteSpace(chapterTitle))
            {
                parts.Add(chapterTitle);
            }

            return string.Join(" | ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private static string BuildSearchText(
            SourceMaterialRow material,
            string text,
            string chapterTitle,
            IReadOnlyList<string> navigationHints)
        {
            var composed = string.Join("\n", new[]
            {
                material.Title,
                material.KnowledgeLabel,
                material.OriginalSourceName,
                chapterTitle,
                string.Join(" ", navigationHints.Take(8)),
                text
            });

            var normalized = NormalizeSearchPhrase(composed);
            return string.IsNullOrWhiteSpace(normalized) ? string.Empty : $" {normalized} ";
        }

        private static HashSet<string> BuildIndexTokens(string searchText)
        {
            return searchText
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token.Length >= 4 || token.Any(char.IsDigit))
                .Where(token => !StopWords.Contains(token))
                .Where(token => !IsAdministrativeToken(token))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(160)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static int DetermineSourcePriority(SourceMaterialRow material)
        {
            var sourceType = material.KnowledgeSourceType ?? string.Empty;
            var name = NormalizeSearchPhrase($"{material.OriginalSourceName} {material.FileName} {material.Title}");
            var ext = (Path.GetExtension(material.OriginalSourceName) ??
                       Path.GetExtension(material.FileName) ??
                       string.Empty).ToLowerInvariant();

            var priority = 0;
            if (string.Equals(sourceType, "local_source_upload", StringComparison.OrdinalIgnoreCase))
            {
                priority += 6;
            }

            if (ext is ".pdf" or ".docx" or ".pptx")
            {
                priority += 3;
            }

            if (name.Contains("learning material", StringComparison.Ordinal) ||
                name.Contains("learner guide", StringComparison.Ordinal) ||
                name.Contains("textbook", StringComparison.Ordinal) ||
                name.Contains("manual", StringComparison.Ordinal))
            {
                priority += 2;
            }

            if (name.Contains("curriculum topics", StringComparison.Ordinal) ||
                name.Contains("curriculum subjects", StringComparison.Ordinal) ||
                name.Contains("curriculum phases", StringComparison.Ordinal) ||
                name.Contains("curriculum baseline", StringComparison.Ordinal) ||
                name.Contains("knowledge extract", StringComparison.Ordinal) ||
                name.Contains("knowledge scan report", StringComparison.Ordinal))
            {
                priority -= 8;
            }

            if (ext is ".csv" or ".json" or ".jsonl")
            {
                priority -= 2;
            }

            return priority;
        }

        private static int ApplySourcePriorityBoost(int score, int sourcePriority, int overlapCount)
        {
            if (score <= 0 || overlapCount <= 0)
            {
                return score;
            }

            if (sourcePriority > 0)
            {
                return score + Math.Min(8, sourcePriority);
            }

            if (sourcePriority < 0)
            {
                return Math.Max(0, score + Math.Max(-8, sourcePriority));
            }

            return score;
        }

        private static List<string> BuildTargetTokens(string value)
        {
            return NormalizeSearchPhrase(value)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token.Length >= 4 || token.Any(char.IsDigit))
                .Where(token => !StopWords.Contains(token))
                .Where(token => !IsAdministrativeToken(token))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToList();
        }

        private static string NormalizeSearchPhrase(string value)
        {
            var source = (value ?? string.Empty).ToLowerInvariant();
            var normalized = Regex.Replace(source, @"[^a-z0-9]+", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            return normalized;
        }

        private static bool ContainsPhrase(string searchText, string phrase)
        {
            if (string.IsNullOrWhiteSpace(searchText) || string.IsNullOrWhiteSpace(phrase))
            {
                return false;
            }

            return searchText.Contains($" {phrase} ", StringComparison.Ordinal);
        }

        private static int CountOverlap(IEnumerable<string> targetTokens, IReadOnlySet<string> chunkTokens)
        {
            var count = 0;
            foreach (var token in targetTokens)
            {
                if (chunkTokens.Contains(token))
                {
                    count++;
                }
            }

            return count;
        }

        private static double ScoreToConfidence(int score)
        {
            return score switch
            {
                >= 32 => 0.97d,
                >= 28 => 0.93d,
                >= 24 => 0.88d,
                >= 20 => 0.81d,
                >= 16 => 0.73d,
                >= 12 => 0.66d,
                >= 8 => 0.56d,
                _ => 0.45d
            };
        }

        private static void FinalizeTopicEvidenceSummary(TopicEvidenceSummary summary)
        {
            summary.TopicsWithEvidenceCount = summary.Topics.Count(topic => topic.EvidenceCount > 0);
            summary.MappedTopicsCount = summary.Topics.Count(topic => string.Equals(topic.CoverageBand, "mapped", StringComparison.OrdinalIgnoreCase));
            summary.DevelopingTopicsCount = summary.Topics.Count(topic => string.Equals(topic.CoverageBand, "developing", StringComparison.OrdinalIgnoreCase));
            summary.GapTopicsCount = Math.Max(0, summary.Topics.Count - summary.MappedTopicsCount - summary.DevelopingTopicsCount);
            summary.CoveragePercent = summary.Topics.Count == 0
                ? 0
                : ClampPercent(summary.Topics.Average(topic => topic.CoveragePercent));
        }

        private static int ClampPercent(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0;
            }

            return (int)Math.Max(0d, Math.Min(100d, Math.Round(value)));
        }

        private static string DetermineCoverageBand(int coveragePercent)
        {
            if (coveragePercent >= 75)
            {
                return "mapped";
            }

            if (coveragePercent >= 40)
            {
                return "developing";
            }

            return "gap";
        }

        private static string DetermineCoverageBandLabel(string coverageBand)
        {
            return coverageBand switch
            {
                "mapped" => "Mapped",
                "developing" => "Developing",
                _ => "Gap"
            };
        }

        private static string NormalizeSentence(string value)
        {
            var text = CleanGeneratedText(value).Trim();
            text = Regex.Replace(text, @"\b(?:IAC|AC|ELO)\d+[A-Za-z0-9]*\b", string.Empty, RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\(\s*Weight\s*\d+%?\s*\)", string.Empty, RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return "demonstrate the mapped outcome";
            }

            return (char.ToLowerInvariant(text[0]) + text[1..]).TrimEnd('.', ';');
        }

        private static bool IsAdministrativeToken(string token)
        {
            var value = (token ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            return Regex.IsMatch(value, @"^(?:iac|ac|elo)\d+[a-z0-9]*$", RegexOptions.IgnoreCase)
                || value.Equals("weight", StringComparison.OrdinalIgnoreCase);
        }

        private static string JoinAsPhrase(IReadOnlyList<string> values)
        {
            if (values.Count == 0) return string.Empty;
            if (values.Count == 1) return values[0];
            if (values.Count == 2) return $"{values[0]} and {values[1]}";
            return string.Join(", ", values.Take(values.Count - 1)) + $", and {values[^1]}";
        }

        private static string CleanGeneratedText(string value)
        {
            var cleaned = DocumentTextCleaner.Clean(value, preservePdfPageMarkers: false);
            cleaned = Regex.Replace(cleaned, @"\[(?:page|ocr)[^\]]+\]", " ", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"<[^>]+>", " ");
            cleaned = Regex.Replace(cleaned, @"[^\S\r\n]{2,}", " ");
            cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");
            return cleaned.Trim();
        }

        private static string NormalizeLine(string? value)
        {
            return Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        }

        private static string NormalizeDuplicateCriteriaKey(string? value)
        {
            return Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"\s+", " ").Trim();
        }

        private static string TruncateWithEllipsis(string value, int maxLength)
        {
            var normalized = NormalizeLine(value);
            if (normalized.Length <= maxLength)
            {
                return normalized;
            }

            return normalized[..Math.Max(0, maxLength - 3)].TrimEnd() + "...";
        }

        private static string CleanHeading(string value)
        {
            var cleaned = CleanGeneratedText(value);
            return cleaned.Length > 90 ? cleaned[..90].Trim() : cleaned;
        }

        private static bool IsLikelyHeading(string line)
        {
            var text = NormalizeLine(line);
            if (string.IsNullOrWhiteSpace(text) || text.Length > 110)
            {
                return false;
            }

            if (Regex.IsMatch(text, @"^(chapter|section|module|unit|part)\s+[a-z0-9ivx]+(?:[\.\-:]\s*|\s+).{3,}$", RegexOptions.IgnoreCase))
            {
                return true;
            }

            if (text.EndsWith(".") || text.EndsWith(";"))
            {
                return false;
            }

            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2 || words.Length > 10)
            {
                return false;
            }

            return words.Count(word => word.All(ch => !char.IsLetter(ch) || char.IsUpper(ch))) >= Math.Max(2, words.Length - 1);
        }

        private static bool IsTableOfContentsHeading(string line)
        {
            var normalized = NormalizeSearchPhrase(line);
            return normalized == "table of contents" ||
                   normalized == "contents" ||
                   normalized == "list of figures" ||
                   normalized == "list of tables" ||
                   normalized == "index";
        }

        private static bool LooksLikeTableOfContentsLine(string line)
        {
            if (Regex.IsMatch(line, @"^.{0,180}\.{2,}\s*\d{1,4}\s*$"))
            {
                return true;
            }

            if (Regex.IsMatch(line, @"^(chapter|section|module|unit)\s+.+\s+\d{1,4}$", RegexOptions.IgnoreCase))
            {
                return true;
            }

            return Regex.IsMatch(line, @"^[A-Za-z].{3,120}\s+\d{1,4}$") &&
                   DocumentTextCleaner.WordCount(line) <= 12;
        }

        private static string ExtractNavigationHint(string line)
        {
            var value = Regex.Replace(line ?? string.Empty, @"\.{2,}\s*\d{1,4}\s*$", " ");
            value = Regex.Replace(value, @"\s+\d{1,4}\s*$", " ");
            return CleanGeneratedText(value);
        }

        private static void AddUnique(List<string> list, string value, int maxCount)
        {
            if (string.IsNullOrWhiteSpace(value) || list.Count >= maxCount)
            {
                return;
            }

            if (!list.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(value);
            }
        }

        private static string EnsureUniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return path;
            }

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

        private static bool IsImageSidecarFile(string path)
        {
            var fileName = Path.GetFileName(path) ?? string.Empty;
            if (!fileName.Contains(".caption.", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var stem = fileName[..fileName.IndexOf(".caption.", StringComparison.OrdinalIgnoreCase)];
            return ImageExtensions.Contains(Path.GetExtension(stem));
        }

        private static string NormalizeMatchToken(string value)
        {
            return Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+", string.Empty);
        }

        public sealed class DeliveryPilotRequest
        {
            public int QualificationId { get; set; }
            public string JobFolder { get; set; } = string.Empty;
            public bool PopulateLessonPlanDrafts { get; set; } = true;
        }

        public sealed class DeliveryPilotResult
        {
            public int QualificationId { get; set; }
            public string QualificationCode { get; set; } = string.Empty;
            public string QualificationDescription { get; set; } = string.Empty;
            public string QualificationRootPath { get; set; } = string.Empty;
            public string ArtifactsDirectory { get; set; } = string.Empty;
            public string DetectedExternalResourceFolder { get; set; } = string.Empty;
            public int SourceMaterialCount { get; set; }
            public int SourceChunkCount { get; set; }
            public int TopicCount { get; set; }
            public int CriteriaCount { get; set; }
            public int TopicsMappedCount { get; set; }
            public int CriteriaMappedCount { get; set; }
            public int LessonPlanDraftsCreated { get; set; }
            public int LessonPlanDraftsUpdated { get; set; }
            public int LessonPlanDraftsSkipped { get; set; }
            public string SourceChunksPath { get; set; } = string.Empty;
            public string TopicSourceMapPath { get; set; } = string.Empty;
            public string CriteriaSourceMapPath { get; set; } = string.Empty;
            public string LessonPlanDraftsPath { get; set; } = string.Empty;
            public ImportResult Import { get; set; } = new();
            public List<string> Warnings { get; set; } = new();
        }

        public sealed class ImportResult
        {
            public string DetectedExternalFolder { get; set; } = string.Empty;
            public int CopiedToInboxCount { get; set; }
            public int SkippedCount { get; set; }
            public int SyncCreatedCount { get; set; }
            public int SyncSkippedCount { get; set; }
            public int SyncFailedCount { get; set; }
            public string CoverageReportPath { get; set; } = string.Empty;
            public List<string> Warnings { get; set; } = new();
        }

        private sealed class SourceMaterialRow
        {
            public int Id { get; set; }
            public string Title { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public string FilePath { get; set; } = string.Empty;
            public string FileType { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public string KnowledgeSourceType { get; set; } = string.Empty;
            public int KnowledgeNumber { get; set; }
            public DateTime UploadedAtUtc { get; set; }
            public string AssessmentCriteriaDescription { get; set; } = string.Empty;
            public string KnowledgeLabel { get; set; } = string.Empty;
            public string ExtractedText { get; set; } = string.Empty;
            public string OriginalSourceName { get; set; } = string.Empty;
        }

        private sealed class VocationalDocumentRow
        {
            public int DocumentId { get; set; }
            public string Title { get; set; } = string.Empty;
            public string SourcePath { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public int RawCharCount { get; set; }
            public DateTime CreatedAtUtc { get; set; }
            public string ExtractedText { get; set; } = string.Empty;
        }

        private sealed class SourceMaterialFingerprintRow
        {
            public int Id { get; set; }
            public string Title { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public string KnowledgeSourceType { get; set; } = string.Empty;
            public int KnowledgeNumber { get; set; }
            public DateTime UploadedAtUtc { get; set; }
            public int ExtractedTextLength { get; set; }
        }

        private sealed class SourceMaterialChunk
        {
            public string Id { get; set; } = string.Empty;
            public int MaterialId { get; set; }
            public string MaterialTitle { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public string FileType { get; set; } = string.Empty;
            public int KnowledgeNumber { get; set; }
            public string KnowledgeSourceType { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public int PageNumber { get; set; }
            public string ChapterTitle { get; set; } = string.Empty;
            public string Citation { get; set; } = string.Empty;
            public string Text { get; set; } = string.Empty;
            public string Excerpt { get; set; } = string.Empty;
            public string SearchText { get; set; } = string.Empty;
            public HashSet<string> IndexTokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public int SourcePriority { get; set; }
        }

        private sealed class ChunkArtifact
        {
            public string ChunkId { get; set; } = string.Empty;
            public int MaterialId { get; set; }
            public string MaterialTitle { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public int KnowledgeNumber { get; set; }
            public string KnowledgeSourceType { get; set; } = string.Empty;
            public string Citation { get; set; } = string.Empty;
            public string ChapterTitle { get; set; } = string.Empty;
            public int PageNumber { get; set; }
            public string Excerpt { get; set; } = string.Empty;
        }

        private sealed class CurriculumTopicTarget
        {
            public int TopicId { get; set; }
            public string TopicCode { get; set; } = string.Empty;
            public string TopicDescription { get; set; } = string.Empty;
            public string TopicPurpose { get; set; } = string.Empty;
            public string SubjectCode { get; set; } = string.Empty;
            public string SubjectDescription { get; set; } = string.Empty;
            public string SubjectPurpose { get; set; } = string.Empty;
            public string TopicPhrase { get; set; } = string.Empty;
            public string SubjectPhrase { get; set; } = string.Empty;
            public List<string> TopicTokens { get; set; } = new();
            public List<string> SubjectTokens { get; set; } = new();
        }

        private sealed class CurriculumCriteriaTarget
        {
            public int CriteriaId { get; set; }
            public string CriteriaDescription { get; set; } = string.Empty;
            public int TopicId { get; set; }
            public string TopicCode { get; set; } = string.Empty;
            public string TopicDescription { get; set; } = string.Empty;
            public string TopicPurpose { get; set; } = string.Empty;
            public string SubjectCode { get; set; } = string.Empty;
            public string SubjectDescription { get; set; } = string.Empty;
            public string SubjectPurpose { get; set; } = string.Empty;
            public string CriteriaPhrase { get; set; } = string.Empty;
            public string TopicPhrase { get; set; } = string.Empty;
            public string SubjectPhrase { get; set; } = string.Empty;
            public List<string> CriteriaTokens { get; set; } = new();
            public List<string> TopicTokens { get; set; } = new();
            public List<string> SubjectTokens { get; set; } = new();
        }

        public sealed class TopicSourceMapItem
        {
            public int TopicId { get; set; }
            public string TopicCode { get; set; } = string.Empty;
            public string TopicDescription { get; set; } = string.Empty;
            public string SubjectCode { get; set; } = string.Empty;
            public string SubjectDescription { get; set; } = string.Empty;
            public List<ChunkMatch> Matches { get; set; } = new();
        }

        public sealed class CriteriaSourceMapItem
        {
            public int CriteriaId { get; set; }
            public string CriteriaDescription { get; set; } = string.Empty;
            public int TopicId { get; set; }
            public string TopicCode { get; set; } = string.Empty;
            public string TopicDescription { get; set; } = string.Empty;
            public string SubjectCode { get; set; } = string.Empty;
            public string SubjectDescription { get; set; } = string.Empty;
            public List<ChunkMatch> Matches { get; set; } = new();
        }

        public sealed class ChunkMatch
        {
            public int MaterialId { get; set; }
            public string ChunkId { get; set; } = string.Empty;
            public string MaterialTitle { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public int KnowledgeNumber { get; set; }
            public string KnowledgeSourceType { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public string Citation { get; set; } = string.Empty;
            public string ChapterTitle { get; set; } = string.Empty;
            public int PageNumber { get; set; }
            public int Score { get; set; }
            public double Confidence { get; set; }
            public int SourcePriority { get; set; }
            public int TopicTokenMatches { get; set; }
            public int SubjectTokenMatches { get; set; }
            public int CriteriaTokenMatches { get; set; }
            public string Excerpt { get; set; } = string.Empty;
        }

        private sealed class DraftWriteResult
        {
            public int Created { get; set; }
            public int Updated { get; set; }
            public int Skipped { get; set; }
            public List<LessonPlanDraftArtifact> Entries { get; set; } = new();
            public List<string> Warnings { get; set; } = new();
        }

        private sealed class TopicEvidenceCacheContext
        {
            public int QualificationId { get; set; }
            public string QualificationCode { get; set; } = string.Empty;
            public string QualificationDescription { get; set; } = string.Empty;
            public IReadOnlyList<CurriculumTopicTarget> Topics { get; set; } = Array.Empty<CurriculumTopicTarget>();
            public IReadOnlyList<DuplicateCriteriaGroup> DuplicateCriteriaGroups { get; set; } = Array.Empty<DuplicateCriteriaGroup>();
            public IReadOnlyList<SourceMaterialFingerprintRow> SourceFingerprintRows { get; set; } = Array.Empty<SourceMaterialFingerprintRow>();
            public string TopicStructureKey { get; set; } = string.Empty;
            public string CacheKey { get; set; } = string.Empty;
        }

        private sealed class TopicEvidenceCacheEntry
        {
            public string CacheKey { get; set; } = string.Empty;
            public DateTime SavedAtUtc { get; set; }
            public TopicEvidenceSummary? Summary { get; set; }
        }

        private sealed class TopicEvidenceCacheEnvelope
        {
            public string CacheKey { get; set; } = string.Empty;
            public DateTime SavedAtUtc { get; set; }
            public TopicEvidenceSummary? Summary { get; set; }
        }

        private sealed class TopicEvidenceBuildArtifacts
        {
            public List<SourceMaterialChunk> Chunks { get; set; } = new();
            public List<TopicSourceMapItem> TopicMaps { get; set; } = new();
            public int ReusedMaterialCount { get; set; }
            public int RebuiltMaterialCount { get; set; }
            public int RemovedMaterialCount { get; set; }
            public int ReusedTopicCount { get; set; }
            public int RecomputedTopicCount { get; set; }
        }

        private sealed class TopicEvidenceComputationEnvelope
        {
            public int QualificationId { get; set; }
            public string QualificationCode { get; set; } = string.Empty;
            public string QualificationDescription { get; set; } = string.Empty;
            public string TopicStructureKey { get; set; } = string.Empty;
            public DateTime SavedAtUtc { get; set; }
            public List<TopicEvidenceMaterialChunkCacheItem> MaterialChunks { get; set; } = new();
            public List<TopicSourceMapItem> TopicMaps { get; set; } = new();
        }

        private sealed class TopicEvidenceMaterialChunkCacheItem
        {
            public int MaterialId { get; set; }
            public string Fingerprint { get; set; } = string.Empty;
            public List<CachedSourceMaterialChunk> Chunks { get; set; } = new();
        }

        private sealed class CachedSourceMaterialChunk
        {
            public string Id { get; set; } = string.Empty;
            public int MaterialId { get; set; }
            public string MaterialTitle { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public string FileType { get; set; } = string.Empty;
            public int KnowledgeNumber { get; set; }
            public string KnowledgeSourceType { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public int PageNumber { get; set; }
            public string ChapterTitle { get; set; } = string.Empty;
            public string Citation { get; set; } = string.Empty;
            public string Excerpt { get; set; } = string.Empty;
            public string SearchText { get; set; } = string.Empty;
            public List<string> IndexTokens { get; set; } = new();
            public int SourcePriority { get; set; }
        }

        public sealed class LessonPlanDraftArtifact
        {
            public int CriteriaId { get; set; }
            public string CriteriaDescription { get; set; } = string.Empty;
            public string SubjectCode { get; set; } = string.Empty;
            public string TopicCode { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public int ConfidencePercent { get; set; }
            public List<string> Citations { get; set; } = new();
        }

        public sealed class TopicEvidenceSummary
        {
            public int QualificationId { get; set; }
            public string QualificationNumber { get; set; } = string.Empty;
            public string QualificationDescription { get; set; } = string.Empty;
            public int SourceMaterialCount { get; set; }
            public int SourceChunkCount { get; set; }
            public int TopicCount { get; set; }
            public int TopicsWithEvidenceCount { get; set; }
            public int MappedTopicsCount { get; set; }
            public int DevelopingTopicsCount { get; set; }
            public int GapTopicsCount { get; set; }
            public int CoveragePercent { get; set; }
            public List<TopicEvidenceItem> Topics { get; set; } = new();
            public List<DuplicateCriteriaGroup> DuplicateCriteriaGroups { get; set; } = new();
            public List<string> Warnings { get; set; } = new();
        }

        public sealed class TopicEvidenceItem
        {
            public int TopicId { get; set; }
            public string TopicCode { get; set; } = string.Empty;
            public string TopicDescription { get; set; } = string.Empty;
            public string SubjectCode { get; set; } = string.Empty;
            public string SubjectDescription { get; set; } = string.Empty;
            public int CoveragePercent { get; set; }
            public string CoverageBand { get; set; } = string.Empty;
            public string CoverageBandLabel { get; set; } = string.Empty;
            public int EvidenceCount { get; set; }
            public int DistinctSourceCount { get; set; }
            public int BestConfidencePercent { get; set; }
            public int AverageConfidencePercent { get; set; }
            public List<string> TopCitations { get; set; } = new();
            public List<TopicEvidenceSource> TopEvidence { get; set; } = new();
        }

        public sealed class TopicEvidenceSource
        {
            public string Citation { get; set; } = string.Empty;
            public int ConfidencePercent { get; set; }
            public string KnowledgeSourceType { get; set; } = string.Empty;
            public string MaterialTitle { get; set; } = string.Empty;
            public int PageNumber { get; set; }
            public string Excerpt { get; set; } = string.Empty;
        }

        public sealed class DuplicateCriteriaGroup
        {
            public string CriteriaDescription { get; set; } = string.Empty;
            public int TopicCount { get; set; }
            public List<string> Topics { get; set; } = new();
        }
    }
}
