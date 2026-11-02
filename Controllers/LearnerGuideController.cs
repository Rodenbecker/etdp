using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using SX = DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using ETD.Api.Data;
using ETD.Api.Models;
using ETD.Api.Utils;
using ETD.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace ETD.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LearnerGuideController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly CurriculumDeliveryPilotService _pilotService;
        private static readonly HttpClient _http = new();
        private static readonly object LocalParaphraseBackoffLock = new();
        private static DateTime _localParaphraseUnavailableUntilUtc = DateTime.MinValue;
        private const string DefaultModeratorResponsesEndpoint = "";
        private const string AutoCurriculumDraftMarker = "[AUTO_CURRICULUM_EVIDENCE_DRAFT]";
        private const string AutoCurriculumCoverageGapMarker = "[AUTO_CURRICULUM_INSUFFICIENT_COVERAGE]";
        private const string AssessmentCriteriaLeadLine = "Assessment criteria for this subject:";
        private const string ChapterSummaryLeadLine = "This chapter covers the following assessment criteria in full:";

        public LearnerGuideController(ApplicationDbContext context, CurriculumDeliveryPilotService pilotService)
        {
            _context = context;
            _pilotService = pilotService;
        }

        [HttpGet]
        public IActionResult GetAll()
        {
            var items = _context.LearnerGuides.Select(lg => new ETD.Api.DTOs.LearnerGuideDto
            {
                Id = lg.Id,
                SubjectId = lg.SubjectId,
                Title = lg.Title,
                Version = lg.Version,
                Content = lg.Content
            }).ToList();
            return Ok(items);
        }

        [HttpGet("{id}")]
        public IActionResult Get(int id)
        {
            var lg = _context.LearnerGuides.Find(id);
            if (lg == null) return NotFound();
            return Ok(new ETD.Api.DTOs.LearnerGuideDto
            {
                Id = lg.Id,
                SubjectId = lg.SubjectId,
                Title = lg.Title,
                Version = lg.Version,
                Content = lg.Content
            });
        }

        [HttpPost]
        public IActionResult Create(ETD.Api.DTOs.CreateLearnerGuideDto dto)
        {
            var model = new LearnerGuide
            {
                SubjectId = dto.SubjectId,
                Title = dto.Title,
                Version = dto.Version,
                Content = dto.Content
            };
            _context.LearnerGuides.Add(model);
            _context.SaveChanges();
            return CreatedAtAction(nameof(Get), new { id = model.Id }, model);
        }

        [HttpPut("{id}")]
        public IActionResult Update(int id, ETD.Api.DTOs.UpdateLearnerGuideDto dto)
        {
            var item = _context.LearnerGuides.Find(id);
            if (item == null) return NotFound();
            item.Title = dto.Title;
            item.Version = dto.Version;
            item.Content = dto.Content;
            _context.SaveChanges();
            return Ok(item);
        }

        [HttpDelete("{id}")]
        public IActionResult Delete(int id)
        {
            var item = _context.LearnerGuides.Find(id);
            if (item == null) return NotFound();
            _context.LearnerGuides.Remove(item);
            _context.SaveChanges();
            return NoContent();
        }

        public class ParaphraseRequest
        {
            public string Text { get; set; } = string.Empty;
            public string Style { get; set; } = "educational";
            public bool PreserveTerminology { get; set; } = true;
        }

        public class ParaphraseWorkflowRequest
        {
            public int? QualificationId { get; set; }
            public string Style { get; set; } = "educational";
            public bool PreserveTerminology { get; set; } = true;
            public bool ForceRefresh { get; set; } = false;
        }

        public class ParaphraseCacheFile
        {
            public int QualificationId { get; set; }
            public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
            public Dictionary<string, ParaphraseCacheEntry> Entries { get; set; } = new(StringComparer.Ordinal);
        }

        public class ParaphraseCacheEntry
        {
            public string ParaphrasedText { get; set; } = string.Empty;
            public string Backend { get; set; } = "unknown";
            public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        }

        public class ParaphraseReviewSaveRequest
        {
            public int? QualificationId { get; set; }
            public List<ParaphraseReviewSaveItem> Entries { get; set; } = new();
        }

        public class ParaphraseReviewSaveItem
        {
            public string SourceText { get; set; } = string.Empty;
            public string ParaphrasedText { get; set; } = string.Empty;
        }

        public class SynthesizeTextbookRequest
        {
            public int QualificationId { get; set; }
            public int? SubjectId { get; set; }
            public string? Model { get; set; } = "gemma4:26b";
            public bool ForceRefresh { get; set; } = false;
            public int MaxTopics { get; set; } = 100;
        }

        public class SynthesizeTextbookResult
        {
            public bool Success { get; set; }
            public string Message { get; set; } = string.Empty;
            public int QualificationId { get; set; }
            public string QualificationTitle { get; set; } = string.Empty;
            public int SubjectsProcessed { get; set; }
            public int TopicsProcessed { get; set; }
            public int CriteriaProcessed { get; set; }
            public int WordsGenerated { get; set; }
            public string ModelUsed { get; set; } = string.Empty;
            public List<SynthesizedTopicSummary> Details { get; set; } = new();
        }

        public class SynthesizedTopicSummary
        {
            public string SubjectCode { get; set; } = string.Empty;
            public string TopicCode { get; set; } = string.Empty;
            public string TopicTitle { get; set; } = string.Empty;
            public int CriteriaCount { get; set; }
            public int WordCount { get; set; }
            public string Model { get; set; } = string.Empty;
            public bool Success { get; set; }
        }

        [HttpPost("paraphrase")]
        public async Task<IActionResult> Paraphrase([FromBody] ParaphraseRequest req)
        {
            var text = (req?.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)) return BadRequest("Text is required.");
            var (paraphrased, backend) = await ParaphraseTextWithBackendAsync(text, req?.Style ?? "educational", req?.PreserveTerminology ?? true);
            return Ok(new { paraphrased, backend, changed = !string.Equals(text, paraphrased, StringComparison.Ordinal) });
        }

        [HttpPost("paraphrase-workflow")]
        public async Task<IActionResult> BuildParaphraseWorkflow([FromBody] ParaphraseWorkflowRequest? req)
        {
            req ??= new ParaphraseWorkflowRequest();

            var qualification = req.QualificationId.HasValue && req.QualificationId.Value > 0
                ? _context.Qualifications.FirstOrDefault(q => q.Id == req.QualificationId.Value)
                : _context.Qualifications.FirstOrDefault();
            if (qualification == null) return BadRequest("No qualification available for paraphrase workflow.");

            var subjects = _context.Subjects.Where(s => s.QualificationId == qualification.Id)
                .OrderBy(s => s.CurriculumPhaseId).ThenBy(s => s.SubjectCode).ToList();
            subjects = subjects.Where(HasSubjectIdentity).ToList();
            if (subjects.Count == 0) return BadRequest("No subjects found for this qualification.");

            var topics = _context.Topics.Where(t => subjects.Select(s => s.Id).Contains(t.SubjectId))
                .OrderBy(t => t.Order ?? int.MaxValue).ThenBy(t => t.TopicCode).ThenBy(t => t.Id).ToList();
            var criteria = _context.AssessmentCriteria.Where(c => topics.Select(t => t.Id).Contains(c.TopicId))
                .OrderBy(c => c.TopicId).ThenBy(c => c.Id).ToList();
            var lessonPlans = _context.LessonPlans.Where(lp => criteria.Select(c => c.Id).Contains(lp.AssessmentCriteriaId))
                .OrderBy(lp => lp.SortOrder).ThenBy(lp => lp.Id).ToList();
            var toolkit = _context.LecturerToolkitEntries
                .Where(e => e.QualificationsId == qualification.Id)
                .ToList()
                .OrderBy(e => ParseLpnSort(e.Lpn))
                .ThenBy(e => e.Id)
                .ToList();

            var topicsBySubject = topics.GroupBy(t => t.SubjectId).ToDictionary(g => g.Key, g => g.ToList());
            var criteriaByTopic = criteria.GroupBy(c => c.TopicId).ToDictionary(g => g.Key, g => g.ToList());
            var lessonByCriteria = lessonPlans.GroupBy(lp => lp.AssessmentCriteriaId).ToDictionary(g => g.Key, g => g.ToList());

            var cacheFile = LoadParaphraseCacheFile(qualification.Id);
            if (cacheFile.QualificationId <= 0) cacheFile.QualificationId = qualification.Id;
            if (cacheFile.Entries == null) cacheFile.Entries = new Dictionary<string, ParaphraseCacheEntry>(StringComparer.Ordinal);

            var uniqueTexts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var subject in subjects)
            {
                var purpose = (subject.SubjectPurpose ?? string.Empty).Trim();
                // Allow shorter purpose blocks to be considered for paraphrase (lowered from 32 -> 16)
                if (!string.IsNullOrWhiteSpace(purpose) && purpose.Length >= 16) uniqueTexts.Add(purpose);

                var subjectTopics = topicsBySubject.TryGetValue(subject.Id, out var st) ? st : new List<Topic>();
                foreach (var topic in subjectTopics)
                {
                    var topicCriteria = criteriaByTopic.TryGetValue(topic.Id, out var tc) ? tc : new List<AssessmentCriteria>();
                    foreach (var criterion in topicCriteria)
                    {
                        var tkList = ResolveToolkitRowsForCriterion(toolkit, criterion, subject);
                        if (tkList.Count > 0)
                        {
                            foreach (var tk in tkList)
                            {
                                var label = NormalizeLpn(tk.Lpn);
                                var desc = string.IsNullOrWhiteSpace(tk.LessonPlanDescription) ? "Lesson Plan" : tk.LessonPlanDescription.Trim();
                                var content = NormalizeToolkitLessonContentForGuide(tk);
                                var block = string.IsNullOrWhiteSpace(content) ? $"{label}: {desc}" : $"{label}: {desc}\n{content}";
                                // Accept shorter blocks for paraphrase workflow (lowered from 32 -> 16)
                                if (!string.IsNullOrWhiteSpace(block) && block.Length >= 16) uniqueTexts.Add(block.Trim());
                            }
                        }
                        else if (lessonByCriteria.TryGetValue(criterion.Id, out var lpList) && lpList.Count > 0)
                        {
                            foreach (var lp in lpList)
                            {
                                var title = string.IsNullOrWhiteSpace(lp.Title) ? "Lesson Plan" : lp.Title.Trim();
                                var content = (lp.Content ?? string.Empty).Trim();
                                var block = string.IsNullOrWhiteSpace(content) ? title : $"{title}\n{content}";
                                if (!string.IsNullOrWhiteSpace(block) && block.Length >= 32) uniqueTexts.Add(block.Trim());
                            }
                        }
                    }
                }
            }

            var created = 0;
            var refreshed = 0;
            var reused = 0;
            var failed = 0;
            var backendCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var text in uniqueTexts)
            {
                if (!req.ForceRefresh &&
                    cacheFile.Entries.TryGetValue(text, out var existing) &&
                    !string.IsNullOrWhiteSpace(existing.ParaphrasedText))
                {
                    reused++;
                    continue;
                }

                var (paraphrased, backend) = await ParaphraseTextWithBackendAsync(text, req.Style, req.PreserveTerminology);
                if (string.IsNullOrWhiteSpace(paraphrased))
                {
                    failed++;
                    continue;
                }

                if (cacheFile.Entries.ContainsKey(text)) refreshed++; else created++;
                cacheFile.Entries[text] = new ParaphraseCacheEntry
                {
                    ParaphrasedText = paraphrased.Trim(),
                    Backend = backend,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                backendCounts[backend] = backendCounts.TryGetValue(backend, out var count) ? count + 1 : 1;
            }

            cacheFile.UpdatedAtUtc = DateTime.UtcNow;
            var cachePath = GetParaphraseCachePath(qualification.Id);
            SaveParaphraseCacheFile(cacheFile);

            return Ok(new
            {
                qualificationId = qualification.Id,
                qualification = qualification.QualificationNumber,
                totalCandidates = uniqueTexts.Count,
                created,
                refreshed,
                reused,
                failed,
                backendCounts,
                cachePath
            });
        }

        [HttpGet("paraphrase-review")]
        public IActionResult GetParaphraseReview([FromQuery] int? qualificationId = null, [FromQuery] int take = 120, [FromQuery] int skip = 0)
        {
            var qualification = qualificationId.HasValue && qualificationId.Value > 0
                ? _context.Qualifications.FirstOrDefault(q => q.Id == qualificationId.Value)
                : _context.Qualifications.FirstOrDefault();
            if (qualification == null) return BadRequest("No qualification available for paraphrase review.");

            var normalizedTake = Math.Max(1, Math.Min(600, take));
            var normalizedSkip = Math.Max(0, skip);
            var file = LoadParaphraseCacheFile(qualification.Id);
            var entries = (file.Entries ?? new Dictionary<string, ParaphraseCacheEntry>(StringComparer.Ordinal))
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
                .Select(kv => new
                {
                    sourceText = kv.Key,
                    paraphrasedText = kv.Value?.ParaphrasedText ?? string.Empty,
                    backend = kv.Value?.Backend ?? "unknown",
                    updatedAtUtc = kv.Value?.UpdatedAtUtc ?? file.UpdatedAtUtc
                })
                .OrderByDescending(x => x.updatedAtUtc)
                .ThenBy(x => x.sourceText.Length)
                .Skip(normalizedSkip)
                .Take(normalizedTake)
                .ToList();

            var total = file.Entries?.Count ?? 0;
            var hasMore = normalizedSkip + entries.Count < total;
            return Ok(new
            {
                qualificationId = qualification.Id,
                total,
                skip = normalizedSkip,
                take = normalizedTake,
                hasMore,
                entries
            });
        }

        [HttpPost("paraphrase-review/save")]
        public IActionResult SaveParaphraseReview([FromBody] ParaphraseReviewSaveRequest? req)
        {
            req ??= new ParaphraseReviewSaveRequest();
            var qualification = req.QualificationId.HasValue && req.QualificationId.Value > 0
                ? _context.Qualifications.FirstOrDefault(q => q.Id == req.QualificationId.Value)
                : _context.Qualifications.FirstOrDefault();
            if (qualification == null) return BadRequest("No qualification available for paraphrase review save.");

            if (req.Entries == null || req.Entries.Count == 0)
                return BadRequest("No review entries provided.");

            var file = LoadParaphraseCacheFile(qualification.Id);
            if (file.QualificationId <= 0) file.QualificationId = qualification.Id;
            file.Entries ??= new Dictionary<string, ParaphraseCacheEntry>(StringComparer.Ordinal);

            var saved = 0;
            foreach (var item in req.Entries)
            {
                var source = (item?.SourceText ?? string.Empty).Trim();
                var paraphrased = (item?.ParaphrasedText ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(paraphrased)) continue;
                file.Entries[source] = new ParaphraseCacheEntry
                {
                    ParaphrasedText = paraphrased,
                    Backend = "manual_review",
                    UpdatedAtUtc = DateTime.UtcNow
                };
                saved++;
            }

            file.UpdatedAtUtc = DateTime.UtcNow;
            SaveParaphraseCacheFile(file);
            return Ok(new
            {
                qualificationId = qualification.Id,
                saved,
                total = file.Entries.Count,
                cachePath = GetParaphraseCachePath(qualification.Id)
            });
        }

        [HttpGet("synthesis-status")]
        public IActionResult GetSynthesisStatus([FromQuery] int? qualificationId = null, [FromQuery] int? subjectId = null)
        {
            var qualification = qualificationId.HasValue && qualificationId.Value > 0
                ? _context.Qualifications.FirstOrDefault(q => q.Id == qualificationId.Value)
                : _context.Qualifications.FirstOrDefault();

            if (qualification == null) return NotFound(new { error = "Qualification not found" });

            var subjectsQuery = _context.Subjects.Where(s => s.QualificationId == qualification.Id);
            if (subjectId.HasValue && subjectId.Value > 0)
            {
                subjectsQuery = subjectsQuery.Where(s => s.Id == subjectId.Value);
            }
            var subjects = subjectsQuery.ToList();
            var subjectIds = subjects.Select(s => s.Id).ToList();

            var topics = _context.Topics.Where(t => subjectIds.Contains(t.SubjectId)).ToList();
            var topicIds = topics.Select(t => t.Id).ToList();
            var criteria = _context.AssessmentCriteria.Where(c => topicIds.Contains(c.TopicId)).ToList();

            var toolkit = _context.LecturerToolkitEntries.Where(e => e.QualificationsId == qualification.Id).ToList();
            var synthesizedCount = 0;
            var totalWordCount = 0;

            foreach (var topic in topics)
            {
                var desc = (topic.TopicDescription ?? string.Empty).Trim();
                var code = (topic.TopicCode ?? string.Empty).Trim();
                var entry = toolkit.FirstOrDefault(e =>
                    (!string.IsNullOrWhiteSpace(code) && e.Lpn == code) ||
                    (!string.IsNullOrWhiteSpace(desc) && !string.IsNullOrWhiteSpace(e.LessonPlanDescription) && e.LessonPlanDescription.Contains(desc)));

                if (entry != null && !string.IsNullOrWhiteSpace(entry.LessonPlanContent) && entry.LessonPlanContent.Length > 200)
                {
                    synthesizedCount++;
                    totalWordCount += DocumentTextCleaner.WordCount(entry.LessonPlanContent);
                }
            }

            var cacheFile = LoadParaphraseCacheFile(qualification.Id);
            var cacheCount = cacheFile.Entries?.Count ?? 0;

            return Ok(new
            {
                qualificationId = qualification.Id,
                qualificationTitle = qualification.QualificationDescription,
                totalSubjects = subjects.Count,
                totalTopics = topics.Count,
                totalCriteria = criteria.Count,
                synthesizedTopicsCount = synthesizedCount,
                coveragePercent = topics.Count > 0 ? Math.Round((double)synthesizedCount / topics.Count * 100, 1) : 0,
                totalWordCount,
                averageWordsPerTopic = synthesizedCount > 0 ? totalWordCount / synthesizedCount : 0,
                cachedEntriesCount = cacheCount,
                googleConfigured = AiRuntime.AllowGoogle(),
                recommendedModel = "gemma4:26b",
                availableModels = new[]
                {
                    new { id = "gemma4:26b", label = "Gemma 4:26B (Local / Ollama)", description = "Runs offline on your workstation, high-precision vocational reasoning" },
                    new { id = "gemini-3.8-flash", label = "Google Gemini 3.8 Flash (Cloud)", description = "Fast high-speed generation directly via Google AI" },
                    new { id = "auto", label = "Auto (Gemma 4:26B Local with Gemini Fallback)", description = "Tries Gemma 4:26B first, falls back automatically to Gemini" }
                }
            });
        }

        [HttpPost("synthesize-textbook")]
        public async Task<IActionResult> SynthesizeTextbook(
            [FromBody] SynthesizeTextbookRequest? req,
            CancellationToken cancellationToken)
        {
            req ??= new SynthesizeTextbookRequest();
            var qualification = req.QualificationId > 0
                ? _context.Qualifications.FirstOrDefault(q => q.Id == req.QualificationId)
                : _context.Qualifications.FirstOrDefault();

            if (qualification == null)
            {
                return BadRequest(new { error = "No qualification found for textbook synthesis." });
            }

            var subjectsQuery = _context.Subjects.Where(s => s.QualificationId == qualification.Id);
            if (req.SubjectId.HasValue && req.SubjectId.Value > 0)
            {
                subjectsQuery = subjectsQuery.Where(s => s.Id == req.SubjectId.Value);
            }

            var subjects = subjectsQuery
                .OrderBy(s => s.CurriculumPhaseId)
                .ThenBy(s => s.SubjectCode)
                .ToList();

            if (subjects.Count == 0)
            {
                return BadRequest(new { error = "No subjects found matching the criteria." });
            }

            var cacheFile = LoadParaphraseCacheFile(qualification.Id);
            if (cacheFile.QualificationId <= 0) cacheFile.QualificationId = qualification.Id;
            cacheFile.Entries ??= new Dictionary<string, ParaphraseCacheEntry>(StringComparer.Ordinal);

            var existingToolkit = _context.LecturerToolkitEntries
                .Where(e => e.QualificationsId == qualification.Id)
                .ToList();

            var result = new SynthesizeTextbookResult
            {
                QualificationId = qualification.Id,
                QualificationTitle = qualification.QualificationDescription ?? "TVET Qualification",
                ModelUsed = req.Model ?? "gemma4:26b",
                Success = true
            };

            int processedTopics = 0;
            int totalWords = 0;
            int totalCriteriaCount = 0;

            foreach (var subject in subjects)
            {
                result.SubjectsProcessed++;
                var topics = _context.Topics
                    .Where(t => t.SubjectId == subject.Id)
                    .OrderBy(t => t.Order ?? int.MaxValue)
                    .ThenBy(t => t.TopicCode)
                    .ThenBy(t => t.Id)
                    .ToList();

                foreach (var topic in topics)
                {
                    if (processedTopics >= req.MaxTopics) break;

                    var topicCode = (topic.TopicCode ?? string.Empty).Trim();
                    var topicDesc = (topic.TopicDescription ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(topicDesc)) continue;

                    var criteria = _context.AssessmentCriteria
                        .Where(c => c.TopicId == topic.Id)
                        .OrderBy(c => c.Id)
                        .ToList();

                    var existingEntry = existingToolkit.FirstOrDefault(e =>
                        e.SubjectCode == subject.SubjectCode &&
                        (e.Lpn == topicCode || (!string.IsNullOrWhiteSpace(e.LessonPlanDescription) && e.LessonPlanDescription.Contains(topicDesc))));

                    bool hasRichContent = existingEntry != null &&
                        !string.IsNullOrWhiteSpace(existingEntry.LessonPlanContent) &&
                        existingEntry.LessonPlanContent.Length > 400 &&
                        !existingEntry.LessonPlanContent.Contains(AutoCurriculumCoverageGapMarker);

                    string synthesizedContent;
                    string actualModelUsed;

                    if (hasRichContent && !req.ForceRefresh)
                    {
                        synthesizedContent = existingEntry!.LessonPlanContent!;
                        actualModelUsed = "existing_database_cached";
                    }
                    else
                    {
                        var evidence = FindTopicTradeEvidence(qualification.Id, qualification.QualificationDescription ?? "", topicDesc, topicCode);
                        var (draft, modelTag) = await GenerateTopicTextbookSectionAsync(qualification, subject, topic, criteria, evidence, req.Model, cancellationToken);
                        synthesizedContent = draft;
                        actualModelUsed = modelTag;

                        if (existingEntry == null)
                        {
                            existingEntry = new LecturerToolkitEntry
                            {
                                QualificationsId = qualification.Id,
                                SubjectCode = subject.SubjectCode ?? string.Empty,
                                SubjectDescription = subject.SubjectDescription ?? string.Empty,
                                Lpn = !string.IsNullOrWhiteSpace(topicCode) ? topicCode : "KT01",
                                LearningInstitutionName = qualification.LearningInstitutionName ?? "TVET College",
                                LecturerName = qualification.SeniorLecturer ?? "Senior Trade Lecturer"
                            };
                            _context.LecturerToolkitEntries.Add(existingEntry);
                            existingToolkit.Add(existingEntry);
                        }

                        existingEntry.LessonPlanDescription = topicDesc;
                        existingEntry.LessonPlanContent = synthesizedContent;
                        existingEntry.LecturerActions = "Demonstrate technical principles, explain OHSA standards, supervise workshop tasks.";
                        existingEntry.LearnerActions = "Study theoretical principles, follow step-by-step procedures, complete formative self-test.";
                        existingEntry.LearningAids = "Trade tools, PPE, schematic drawings, reference manuals, multimeter/measuring instruments.";

                        foreach (var crit in criteria)
                        {
                            var lp = _context.LessonPlans.FirstOrDefault(p => p.AssessmentCriteriaId == crit.Id);
                            if (lp == null)
                            {
                                lp = new LessonPlan
                                {
                                    AssessmentCriteriaId = crit.Id,
                                    Title = topicDesc,
                                    Date = DateTime.UtcNow,
                                    DurationMinutes = 60
                                };
                                _context.LessonPlans.Add(lp);
                            }
                            lp.Title = topicDesc;
                            lp.Content = synthesizedContent;
                        }

                        cacheFile.Entries[topicDesc] = new ParaphraseCacheEntry
                        {
                            ParaphrasedText = synthesizedContent,
                            Backend = actualModelUsed,
                            UpdatedAtUtc = DateTime.UtcNow
                        };
                        if (!string.IsNullOrWhiteSpace(topicCode))
                        {
                            cacheFile.Entries[$"{topicCode}: {topicDesc}"] = new ParaphraseCacheEntry
                            {
                                ParaphrasedText = synthesizedContent,
                                Backend = actualModelUsed,
                                UpdatedAtUtc = DateTime.UtcNow
                            };
                        }
                    }

                    int words = DocumentTextCleaner.WordCount(synthesizedContent);
                    totalWords += words;
                    processedTopics++;
                    totalCriteriaCount += criteria.Count;

                    result.Details.Add(new SynthesizedTopicSummary
                    {
                        SubjectCode = subject.SubjectCode ?? "",
                        TopicCode = topicCode,
                        TopicTitle = topicDesc,
                        CriteriaCount = criteria.Count,
                        WordCount = words,
                        Model = actualModelUsed,
                        Success = !string.IsNullOrWhiteSpace(synthesizedContent) && words >= 50
                    });
                }
            }

            await _context.SaveChangesAsync(cancellationToken);
            SaveParaphraseCacheFile(cacheFile);

            result.TopicsProcessed = processedTopics;
            result.CriteriaProcessed = totalCriteriaCount;
            result.WordsGenerated = totalWords;
            result.Message = $"Successfully synthesized {processedTopics} TVET textbook topics ({totalWords} words) aligned to QCTO standards.";

            return Ok(result);
        }

        [HttpGet("export-readiness")]
        public async Task<IActionResult> ExportReadiness(
            [FromQuery] int? qualificationId = null,
            [FromQuery] int? subjectId = null,
            [FromQuery] bool details = false)
        {
            var qualification = qualificationId.HasValue && qualificationId.Value > 0
                ? _context.Qualifications.FirstOrDefault(q => q.Id == qualificationId.Value)
                : _context.Qualifications.FirstOrDefault();
            if (qualification == null) return BadRequest("No qualification available for Learner Guide export.");

            var qualificationSubjects = _context.Subjects
                .Where(s => s.QualificationId == qualification.Id)
                .OrderBy(s => s.CurriculumPhaseId)
                .ThenBy(s => s.SubjectCode)
                .ToList();
            qualificationSubjects = qualificationSubjects.Where(HasSubjectIdentity).ToList();
            if (qualificationSubjects.Count == 0) return BadRequest("No subjects found for this qualification.");

            if ((!subjectId.HasValue || subjectId.Value <= 0) && qualificationSubjects.Count > 1)
            {
                return Ok(new
                {
                    qualificationId = qualification.Id,
                    ready = false,
                    requiresSubjectSelection = true,
                    message = "Select a subjectId for Learner Guide export readiness check.",
                    subjects = qualificationSubjects.Select(s => new
                    {
                        id = s.Id,
                        subjectCode = (s.SubjectCode ?? string.Empty).Trim(),
                        subjectDescription = (s.SubjectDescription ?? string.Empty).Trim()
                    }).ToList()
                });
            }

            var subject = subjectId.HasValue && subjectId.Value > 0
                ? qualificationSubjects.FirstOrDefault(s => s.Id == subjectId.Value)
                : qualificationSubjects[0];
            if (subject == null) return BadRequest("Selected subjectId was not found for this qualification.");

            var rawTopics = _context.Topics
                .Where(t => t.SubjectId == subject.Id)
                .OrderBy(t => t.Order ?? int.MaxValue)
                .ThenBy(t => t.TopicCode)
                .ThenBy(t => t.Id)
                .ToList();
            var topicById = rawTopics.ToDictionary(t => t.Id);
            var topics = rawTopics
                .GroupBy(BuildTopicIdentity)
                .Select(g => g
                    .OrderBy(t => t.Order ?? int.MaxValue)
                    .ThenBy(t => t.TopicCode)
                    .ThenBy(t => t.Id)
                    .First())
                .ToList();

            var topicIds = rawTopics.Select(t => t.Id).ToList();
            var rawCriteria = _context.AssessmentCriteria
                .Where(c => topicIds.Contains(c.TopicId))
                .OrderBy(c => c.TopicId)
                .ThenBy(c => c.Id)
                .ToList();
            var criteriaGroups = rawCriteria
                .GroupBy(c => BuildCriterionIdentity(c, topicById))
                .Select(g => g.OrderBy(c => c.Id).ToList())
                .ToList();
            var criteria = criteriaGroups
                .Select(g => g.First())
                .ToList();
            var criteriaIds = rawCriteria.Select(c => c.Id).ToList();

            var lessonPlans = _context.LessonPlans
                .Where(lp => criteriaIds.Contains(lp.AssessmentCriteriaId))
                .OrderBy(lp => lp.SortOrder)
                .ThenBy(lp => lp.Id)
                .ToList();
            var lessonByCriteria = lessonPlans
                .GroupBy(lp => lp.AssessmentCriteriaId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var toolkit = _context.LecturerToolkitEntries
                .Where(e => e.QualificationsId == qualification.Id)
                .ToList()
                .OrderBy(e => ParseLpnSort(e.Lpn))
                .ThenBy(e => e.Id)
                .ToList();

            var toolkitForSubject = toolkit
                .Where(row => ToolkitMatchesSubjectCode(row, subject))
                .ToList();

            var toolkitRowsWithContent = toolkitForSubject
                .Where(HasGuideSourceContent)
                .ToList();
            var toolkitRowsWithContentOtherSubjects = toolkit
                .Where(row => !ToolkitMatchesSubjectCode(row, subject))
                .Where(HasGuideSourceContent)
                .ToList();

            CurriculumDeliveryPilotService.TopicEvidenceSummary? pilotSummary = null;
            try
            {
                pilotSummary = await _pilotService.BuildTopicEvidenceSummaryAsync(qualification.Id, forceRefresh: false, HttpContext.RequestAborted);
            }
            catch
            {
                // Readiness must still respond if the pilot evidence cache is temporarily unavailable.
            }

            var criteriaById = criteria
                .GroupBy(c => c.Id)
                .ToDictionary(g => g.Key, g => g.First());
            var mappedToolkitRowIds = new HashSet<int>();
            var criteriaDiagnostics = new List<CriteriaReadinessDiagnostic>();
            foreach (var group in criteriaGroups)
            {
                var c = group.First();
                var matchedRows = ResolveToolkitRowsForCriteria(toolkit, group, subject);
                foreach (var row in matchedRows)
                {
                    mappedToolkitRowIds.Add(row.Id);
                }

                var matchedRowsWithContent = matchedRows
                    .Count(HasGuideSourceContent);
                var fallbackPlans = ResolveLessonPlansForCriteria(lessonByCriteria, group);
                var fallbackPlansWithContent = fallbackPlans
                    .Count(HasGuideSourceContent);
                var topic = topicById.TryGetValue(c.TopicId, out var criterionTopic)
                    ? criterionTopic
                    : null;
                var pilotTopic = pilotSummary?.Topics?.FirstOrDefault(t => t.TopicId == c.TopicId);
                var evidenceFallbackBlocks = BuildEvidenceBackedLessonBlocks(
                    pilotTopic,
                    topic?.TopicCode,
                    topic?.TopicDescription,
                    c.Description);

                criteriaDiagnostics.Add(new CriteriaReadinessDiagnostic
                {
                    CriteriaId = c.Id,
                    CriteriaDescription = (c.Description ?? string.Empty).Trim(),
                    MatchedRows = matchedRows.Count,
                    MatchedRowsWithContent = matchedRowsWithContent,
                    FallbackPlans = fallbackPlans.Count,
                    FallbackPlansWithContent = fallbackPlansWithContent,
                    EvidenceFallbackBlocks = evidenceFallbackBlocks.Count,
                    HasAnyContent = matchedRowsWithContent > 0 || fallbackPlansWithContent > 0 || evidenceFallbackBlocks.Count > 0
                });
            }

            var criteriaTotal = criteriaDiagnostics.Count;
            var criteriaMatched = criteriaDiagnostics.Count(x => x.MatchedRows > 0);
            var criteriaWithLessonContent = criteriaDiagnostics.Count(x => x.MatchedRowsWithContent > 0);
            var criteriaWithAnyContent = criteriaDiagnostics.Count(x => x.HasAnyContent);
            var missingCriteria = criteriaDiagnostics
                .Where(x => !x.HasAnyContent)
                .Select(x => new { criteriaId = x.CriteriaId, criteriaDescription = x.CriteriaDescription })
                .Take(20)
                .ToList();

            var totalLessonContentChars = toolkitRowsWithContent
                .Sum(row =>
                    (row.LessonPlanContent ?? string.Empty).Trim().Length +
                    (row.LessonPlanDescription ?? string.Empty).Trim().Length);
            var criteriaWithEvidenceFallback = criteriaDiagnostics.Count(x => x.EvidenceFallbackBlocks > 0);
            var topicsWithEvidence = pilotSummary?.Topics?.Count(t => t.TopEvidence != null && t.TopEvidence.Count > 0) ?? 0;

            var ready = criteriaWithAnyContent > 0 || toolkitRowsWithContent.Count > 0;
            var coveragePercent = criteriaTotal > 0
                ? Math.Round((double)criteriaWithAnyContent / criteriaTotal * 100, 1)
                : 0.0;

            var unmappedToolkitRowsWithContent = toolkitRowsWithContent
                .Where(row => !mappedToolkitRowIds.Contains(row.Id))
                .OrderBy(row => ParseLpnSort(row.Lpn))
                .ThenBy(row => row.Id)
                .Select(row => new
                {
                    rowId = row.Id,
                    lpn = NormalizeLpn(row.Lpn),
                    subjectCode = (row.SubjectCode ?? string.Empty).Trim(),
                    criteriaId = row.AssessmentCriteriaId,
                    criteriaDescription = (row.AssessmentCriteriaDescription ?? string.Empty).Trim(),
                    lessonPlanDescription = (row.LessonPlanDescription ?? string.Empty).Trim(),
                    lessonContentChars = (row.LessonPlanContent ?? string.Empty).Trim().Length,
                    reason = DescribeUnmappedToolkitRow(row, criteriaById)
                })
                .ToList();

            var subjectMismatchRowsWithContent = toolkitRowsWithContentOtherSubjects
                .OrderBy(row => ParseLpnSort(row.Lpn))
                .ThenBy(row => row.Id)
                .Select(row => new
                {
                    rowId = row.Id,
                    lpn = NormalizeLpn(row.Lpn),
                    rowSubjectCode = (row.SubjectCode ?? string.Empty).Trim(),
                    selectedSubjectCode = (subject.SubjectCode ?? string.Empty).Trim(),
                    criteriaId = row.AssessmentCriteriaId,
                    criteriaDescription = (row.AssessmentCriteriaDescription ?? string.Empty).Trim(),
                    lessonContentChars = (row.LessonPlanContent ?? string.Empty).Trim().Length
                })
                .ToList();

            return Ok(new
            {
                qualificationId = qualification.Id,
                qualificationNumber = (qualification.QualificationNumber ?? string.Empty).Trim(),
                subjectId = subject.Id,
                subjectCode = (subject.SubjectCode ?? string.Empty).Trim(),
                subjectDescription = (subject.SubjectDescription ?? string.Empty).Trim(),
                topics = topics.Count,
                criteria = criteriaTotal,
                toolkitRowsForSubject = toolkitForSubject.Count,
                toolkitRowsWithLessonContent = toolkitRowsWithContent.Count,
                criteriaMatched,
                criteriaWithLessonContent,
                criteriaWithAnyContent,
                criteriaWithEvidenceFallback,
                missingCriteriaCount = Math.Max(0, criteriaTotal - criteriaWithAnyContent),
                criteriaCoveragePercent = coveragePercent,
                topicsWithEvidence,
                totalLessonContentChars,
                mappedToolkitRowsWithLessonContent = toolkitRowsWithContent.Count - unmappedToolkitRowsWithContent.Count,
                unmappedToolkitRowsWithLessonContent = unmappedToolkitRowsWithContent.Count,
                toolkitRowsWithLessonContentOtherSubjects = subjectMismatchRowsWithContent.Count,
                ready,
                message = ready
                    ? "Learner guide source rows are available for export."
                    : "No mapped learner guide source rows were found yet for the selected subject.",
                missingCriteria,
                detailsIncluded = details,
                criteriaDiagnostics = details
                    ? criteriaDiagnostics.Take(120).Select(x => new
                    {
                        criteriaId = x.CriteriaId,
                        criteriaDescription = x.CriteriaDescription,
                        matchedRows = x.MatchedRows,
                        matchedRowsWithContent = x.MatchedRowsWithContent,
                        fallbackPlans = x.FallbackPlans,
                        fallbackPlansWithContent = x.FallbackPlansWithContent,
                        evidenceFallbackBlocks = x.EvidenceFallbackBlocks,
                        hasAnyContent = x.HasAnyContent
                    }).Cast<object>().ToList()
                    : new List<object>(),
                unmappedToolkitRows = details
                    ? unmappedToolkitRowsWithContent.Take(80).Cast<object>().ToList()
                    : new List<object>(),
                subjectMismatchToolkitRows = details
                    ? subjectMismatchRowsWithContent.Take(80).Cast<object>().ToList()
                    : new List<object>()
            });
        }

        [HttpGet("download")]
        public async Task<IActionResult> Download(
            [FromQuery] int? qualificationId = null,
            [FromQuery] int? subjectId = null,
            [FromQuery] bool paraphrase = false,
            [FromQuery] bool useWorkflowCache = true,
            [FromQuery] bool includeIllustrations = true,
            [FromQuery] bool generateIllustrations = false,
            [FromQuery] int maxIllustrationsPerTopic = 2)
        {
            var qualification = qualificationId.HasValue && qualificationId.Value > 0
                ? _context.Qualifications.FirstOrDefault(q => q.Id == qualificationId.Value)
                : _context.Qualifications.FirstOrDefault();
            if (qualification == null) return BadRequest("No qualification available for Learner Guide export.");

            var qualificationSubjects = _context.Subjects
                .Where(s => s.QualificationId == qualification.Id)
                .OrderBy(s => s.CurriculumPhaseId)
                .ThenBy(s => s.SubjectCode)
                .ToList();
            qualificationSubjects = qualificationSubjects.Where(HasSubjectIdentity).ToList();
            if (qualificationSubjects.Count == 0) return BadRequest("No subjects found for this qualification.");

            if (subjectId.HasValue && subjectId.Value > 0)
            {
                qualificationSubjects = qualificationSubjects.Where(s => s.Id == subjectId.Value).ToList();
            }
            if (qualificationSubjects.Count == 0) return BadRequest("Selected subjectId was not found for this qualification.");

            var buildResult = await BuildLearnerGuideDocumentAsync(
                qualification,
                qualificationSubjects,
                paraphrase,
                useWorkflowCache,
                includeIllustrations,
                generateIllustrations,
                maxIllustrationsPerTopic,
                HttpContext.RequestAborted);
            if (!buildResult.Success)
            {
                return BadRequest(buildResult.ErrorMessage);
            }

            return File(
                buildResult.FileBytes,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                buildResult.FileName);
        }

        [HttpGet("save-to-workspace")]
        public async Task<IActionResult> SaveToWorkspace(
            [FromQuery] int? qualificationId = null,
            [FromQuery] int? subjectId = null,
            [FromQuery] bool paraphrase = false,
            [FromQuery] bool useWorkflowCache = true,
            [FromQuery] bool includeIllustrations = true,
            [FromQuery] bool generateIllustrations = false,
            [FromQuery] int maxIllustrationsPerTopic = 2)
        {
            var qualification = qualificationId.HasValue && qualificationId.Value > 0
                ? _context.Qualifications.FirstOrDefault(q => q.Id == qualificationId.Value)
                : _context.Qualifications.FirstOrDefault();
            if (qualification == null) return BadRequest("No qualification available for Learner Guide export.");

            var qualificationSubjects = _context.Subjects
                .Where(s => s.QualificationId == qualification.Id)
                .OrderBy(s => s.CurriculumPhaseId)
                .ThenBy(s => s.SubjectCode)
                .ToList();
            qualificationSubjects = qualificationSubjects.Where(HasSubjectIdentity).ToList();
            if (qualificationSubjects.Count == 0) return BadRequest("No subjects found for this qualification.");

            if (subjectId.HasValue && subjectId.Value > 0)
            {
                qualificationSubjects = qualificationSubjects.Where(s => s.Id == subjectId.Value).ToList();
            }
            if (qualificationSubjects.Count == 0) return BadRequest("Selected subjectId was not found for this qualification.");

            var buildResult = await BuildLearnerGuideDocumentAsync(
                qualification,
                qualificationSubjects,
                paraphrase,
                useWorkflowCache,
                includeIllustrations,
                generateIllustrations,
                maxIllustrationsPerTopic,
                HttpContext.RequestAborted);
            if (!buildResult.Success)
            {
                return BadRequest(buildResult.ErrorMessage);
            }

            var savedPath = LearningMaterialWorkspacePaths.SaveBytes(
                qualification,
                qualification.Id,
                "Learner Guide",
                buildResult.FileName,
                buildResult.FileBytes);

            return Ok(new
            {
                fileName = Path.GetFileName(savedPath),
                savedPath,
                folderPath = Path.GetDirectoryName(savedPath)
            });
        }

        [HttpGet("download-range")]
        public async Task<IActionResult> DownloadRange(
            [FromQuery] int qualificationId,
            [FromQuery] int? subjectFromId = null,
            [FromQuery] int? subjectToId = null,
            [FromQuery] bool paraphrase = false,
            [FromQuery] bool useWorkflowCache = true,
            [FromQuery] bool includeIllustrations = true,
            [FromQuery] bool generateIllustrations = false,
            [FromQuery] int maxIllustrationsPerTopic = 2)
        {
            if (qualificationId <= 0) return BadRequest("qualificationId is required for range export.");

            var qualification = _context.Qualifications.FirstOrDefault(q => q.Id == qualificationId);
            if (qualification == null) return BadRequest("No qualification available for Learner Guide range export.");

            var subjects = _context.Subjects
                .Where(s => s.QualificationId == qualification.Id)
                .OrderBy(s => s.CurriculumPhaseId)
                .ThenBy(s => s.SubjectCode)
                .ToList();
            subjects = subjects.Where(HasSubjectIdentity).ToList();
            if (subjects.Count == 0) return BadRequest("No subjects found for this qualification.");

            var fromIndex = 0;
            var toIndex = subjects.Count - 1;

            if (subjectFromId.HasValue && subjectFromId.Value > 0)
            {
                fromIndex = subjects.FindIndex(s => s.Id == subjectFromId.Value);
                if (fromIndex < 0) return BadRequest("subjectFromId was not found for this qualification.");
            }
            if (subjectToId.HasValue && subjectToId.Value > 0)
            {
                toIndex = subjects.FindIndex(s => s.Id == subjectToId.Value);
                if (toIndex < 0) return BadRequest("subjectToId was not found for this qualification.");
            }

            if (fromIndex > toIndex)
            {
                (fromIndex, toIndex) = (toIndex, fromIndex);
            }

            var scopedSubjects = subjects
                .Skip(fromIndex)
                .Take((toIndex - fromIndex) + 1)
                .ToList();
            if (scopedSubjects.Count == 0) return BadRequest("No subjects were resolved for the selected range.");

            var buildResult = await BuildLearnerGuideDocumentAsync(
                qualification,
                scopedSubjects,
                paraphrase,
                useWorkflowCache,
                includeIllustrations,
                generateIllustrations,
                maxIllustrationsPerTopic,
                HttpContext.RequestAborted);
            if (!buildResult.Success)
            {
                return BadRequest(buildResult.ErrorMessage);
            }

            return File(
                buildResult.FileBytes,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                buildResult.FileName);
        }

        [HttpGet("save-range-to-workspace")]
        public async Task<IActionResult> SaveRangeToWorkspace(
            [FromQuery] int qualificationId,
            [FromQuery] int? subjectFromId = null,
            [FromQuery] int? subjectToId = null,
            [FromQuery] bool paraphrase = false,
            [FromQuery] bool useWorkflowCache = true,
            [FromQuery] bool includeIllustrations = true,
            [FromQuery] bool generateIllustrations = false,
            [FromQuery] int maxIllustrationsPerTopic = 2)
        {
            if (qualificationId <= 0) return BadRequest("qualificationId is required for range export.");

            var qualification = _context.Qualifications.FirstOrDefault(q => q.Id == qualificationId);
            if (qualification == null) return BadRequest("No qualification available for Learner Guide range export.");

            var subjects = _context.Subjects
                .Where(s => s.QualificationId == qualification.Id)
                .OrderBy(s => s.CurriculumPhaseId)
                .ThenBy(s => s.SubjectCode)
                .ToList();
            subjects = subjects.Where(HasSubjectIdentity).ToList();
            if (subjects.Count == 0) return BadRequest("No subjects found for this qualification.");

            var fromIndex = 0;
            var toIndex = subjects.Count - 1;

            if (subjectFromId.HasValue && subjectFromId.Value > 0)
            {
                fromIndex = subjects.FindIndex(s => s.Id == subjectFromId.Value);
                if (fromIndex < 0) return BadRequest("subjectFromId was not found for this qualification.");
            }
            if (subjectToId.HasValue && subjectToId.Value > 0)
            {
                toIndex = subjects.FindIndex(s => s.Id == subjectToId.Value);
                if (toIndex < 0) return BadRequest("subjectToId was not found for this qualification.");
            }

            if (fromIndex > toIndex)
            {
                (fromIndex, toIndex) = (toIndex, fromIndex);
            }

            var scopedSubjects = subjects
                .Skip(fromIndex)
                .Take((toIndex - fromIndex) + 1)
                .ToList();
            if (scopedSubjects.Count == 0) return BadRequest("No subjects were resolved for the selected range.");

            var buildResult = await BuildLearnerGuideDocumentAsync(
                qualification,
                scopedSubjects,
                paraphrase,
                useWorkflowCache,
                includeIllustrations,
                generateIllustrations,
                maxIllustrationsPerTopic,
                HttpContext.RequestAborted);
            if (!buildResult.Success)
            {
                return BadRequest(buildResult.ErrorMessage);
            }

            var savedPath = LearningMaterialWorkspacePaths.SaveBytes(
                qualification,
                qualification.Id,
                "Learner Guide",
                buildResult.FileName,
                buildResult.FileBytes);

            return Ok(new
            {
                fileName = Path.GetFileName(savedPath),
                savedPath,
                folderPath = Path.GetDirectoryName(savedPath)
            });
        }

        private sealed class LearnerGuideBuildResult
        {
            public bool Success { get; init; }
            public string ErrorMessage { get; init; } = string.Empty;
            public byte[] FileBytes { get; init; } = Array.Empty<byte>();
            public string FileName { get; init; } = string.Empty;

            public static LearnerGuideBuildResult Fail(string message)
                => new() { Success = false, ErrorMessage = message };

            public static LearnerGuideBuildResult Ok(byte[] bytes, string fileName)
                => new() { Success = true, FileBytes = bytes, FileName = fileName };
        }

        private sealed class SubjectGuideChapter
        {
            public CurriculumPhase? Phase { get; init; }
            public Subject Subject { get; init; } = null!;
            public List<TopicGuideSection> Topics { get; init; } = new();
            public Dictionary<int, List<GuideIllustration>> IllustrationsByTopic { get; init; } = new();
            public List<string> SubjectAssessmentCriteria { get; init; } = new();
            public List<string> WorkbookActivities { get; init; } = new();
        }

        private async Task<LearnerGuideBuildResult> BuildLearnerGuideDocumentAsync(
            Qualification qualification,
            IReadOnlyList<Subject> subjects,
            bool paraphrase,
            bool useWorkflowCache,
            bool includeIllustrations,
            bool generateIllustrations,
            int maxIllustrationsPerTopic,
            CancellationToken cancellationToken)
        {
            if (subjects == null || subjects.Count == 0)
            {
                return LearnerGuideBuildResult.Fail("No subjects were resolved for Learner Guide export.");
            }

            var cache = new Dictionary<string, string>(StringComparer.Ordinal);
            if (paraphrase && useWorkflowCache)
            {
                foreach (var kv in LoadParaphraseMap(qualification.Id))
                {
                    if (!cache.ContainsKey(kv.Key))
                    {
                        cache[kv.Key] = kv.Value;
                    }
                }
            }

            var chapters = new List<SubjectGuideChapter>();
            foreach (var subject in subjects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chapter = await BuildSubjectGuideChapterAsync(
                    qualification,
                    subject,
                    paraphrase,
                    cache,
                    includeIllustrations,
                    generateIllustrations,
                    maxIllustrationsPerTopic,
                    cancellationToken);
                if (chapter != null)
                {
                    chapters.Add(chapter);
                }
            }

            if (chapters.Count == 0)
            {
                return LearnerGuideBuildResult.Fail("No topic data found for the selected subject scope.");
            }

            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, true))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body());
                var body = main.Document.Body ?? (main.Document.Body = new Body());
                var settingsPart = main.AddNewPart<DocumentSettingsPart>();
                settingsPart.Settings = new Settings(new UpdateFieldsOnOpen() { Val = true });
                settingsPart.Settings.Save();
                EnsureLearnerGuideStyles(main);

                var portraitHeaderRelId = EnsureLearnerGuideHeader(main, qualification);

                AppendCoverPage(body, main, qualification, chapters.Count == 1 ? chapters[0].Subject : null);
                body.Append(PageBreak());

                AppendDisclaimerPage(body, qualification);
                body.Append(PageBreak());

                DocumentRevisionQualityControlPage.Append(body, qualification, new DocumentRevisionQualityControlPageOptions
                {
                    DocumentTitle = "Learner Guide",
                    DocumentType = "Learning Material",
                    Phase = "Knowledge Learning",
                    IncludeAiAssistedLegalBlock = false
                });
                body.Append(PageBreak());

                AppendTableOfContentsPage(body);
                body.Append(PageBreak());

                var drawingId = 1U;
                for (var i = 0; i < chapters.Count; i++)
                {
                    var chapter = chapters[i];
                    AppendSubjectChapter(
                        body,
                        main,
                        qualification,
                        chapter.Phase,
                        chapter.Subject,
                        chapter.Topics,
                        chapter.IllustrationsByTopic,
                        chapter.SubjectAssessmentCriteria,
                        chapter.WorkbookActivities,
                        chapterNumber: i + 1,
                        drawingId: ref drawingId);

                    if (i < chapters.Count - 1)
                    {
                        body.Append(PageBreak());
                    }
                }

                body.Append(DefaultPortraitSectionProperties(portraitHeaderRelId));
                main.Document.Save();
            }

            ms.Position = 0;
            var safeQualification = SafeFileNameToken(qualification.QualificationNumber);
            var suffix = chapters.Count > 1 ? "Full" : SafeFileNameToken(chapters[0].Subject.SubjectCode);
            var fileName = $"LearnerGuide_{safeQualification}_{suffix}_{DateTime.Now:yyyyMMdd}.docx";
            return LearnerGuideBuildResult.Ok(ms.ToArray(), fileName);
        }

        private async Task<SubjectGuideChapter?> BuildSubjectGuideChapterAsync(
            Qualification qualification,
            Subject subject,
            bool paraphrase,
            Dictionary<string, string> cache,
            bool includeIllustrations,
            bool generateIllustrations,
            int maxIllustrationsPerTopic,
            CancellationToken cancellationToken)
        {
            var phase = _context.CurriculumPhases.FirstOrDefault(p => p.Id == subject.CurriculumPhaseId);

            var rawTopics = _context.Topics
                .Where(t => t.SubjectId == subject.Id)
                .OrderBy(t => t.Order ?? int.MaxValue)
                .ThenBy(t => t.TopicCode)
                .ThenBy(t => t.Id)
                .ToList();
            if (rawTopics.Count == 0) return null;
            var topicById = rawTopics.ToDictionary(t => t.Id);
            var topics = rawTopics
                .GroupBy(BuildTopicIdentity)
                .Select(g => g
                    .OrderBy(t => t.Order ?? int.MaxValue)
                    .ThenBy(t => t.TopicCode)
                    .ThenBy(t => t.Id)
                    .First())
                .ToList();

            var topicIds = rawTopics.Select(t => t.Id).ToList();
            var rawCriteria = _context.AssessmentCriteria
                .Where(c => topicIds.Contains(c.TopicId))
                .OrderBy(c => c.TopicId)
                .ThenBy(c => c.Id)
                .ToList();
            var criteriaGroups = rawCriteria
                .GroupBy(c => BuildCriterionIdentity(c, topicById))
                .Select(g => new GuideCriteriaGroup
                {
                    TopicKey = topicById.TryGetValue(g.First().TopicId, out var topic)
                        ? BuildTopicIdentity(topic)
                        : $"topic:{g.First().TopicId}",
                    Representative = g.First(),
                    Members = g.OrderBy(c => c.Id).ToList()
                })
                .OrderBy(g => g.Representative.TopicId)
                .ThenBy(g => g.Representative.Id)
                .ToList();
            var criteria = criteriaGroups.Select(g => g.Representative).ToList();
            var criteriaIds = rawCriteria.Select(c => c.Id).ToList();

            var lessonPlans = _context.LessonPlans
                .Where(lp => criteriaIds.Contains(lp.AssessmentCriteriaId))
                .OrderBy(lp => lp.SortOrder)
                .ThenBy(lp => lp.Id)
                .ToList();
            var lessonByCriteria = lessonPlans
                .GroupBy(lp => lp.AssessmentCriteriaId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var toolkit = _context.LecturerToolkitEntries
                .Where(e => e.QualificationsId == qualification.Id)
                .ToList()
                .OrderBy(e => ParseLpnSort(e.Lpn))
                .ThenBy(e => e.Id)
                .ToList();
            var uploadedLessonPlanRows = LoadUploadedLessonPlanRowsForSubject(qualification, subject);

            CurriculumDeliveryPilotService.TopicEvidenceSummary? pilotSummary = null;
            try
            {
                pilotSummary = await _pilotService.BuildTopicEvidenceSummaryAsync(qualification.Id, forceRefresh: false, cancellationToken);
            }
            catch
            {
                // Pilot evidence is a fallback; do not fail the export if it's unavailable.
            }

            var criteriaByTopic = criteriaGroups
                .GroupBy(g => g.TopicKey)
                .ToDictionary(g => g.Key, g => g.ToList());
            var topicSections = new List<TopicGuideSection>();
            var consumedToolkitRowIds = new HashSet<int>();
            foreach (var topic in topics)
            {
                var topicKey = BuildTopicIdentity(topic);
                var topicCriteria = criteriaByTopic.TryGetValue(topicKey, out var list)
                    ? list
                    : new List<GuideCriteriaGroup>();

                var pilotTopic = pilotSummary?.Topics?.FirstOrDefault(t => t.TopicId == topic.Id);

                var criteriaSections = new List<CriteriaGuideSection>();
                foreach (var criterionGroup in topicCriteria)
                {
                    var criterion = criterionGroup.Representative;
                    var lessonBlocks = new List<GuideLessonBlock>();
                    var fallbackPlans = ResolveLessonPlansForCriteria(lessonByCriteria, criterionGroup.Members);
                    var sourceRows = ResolveUploadedLessonPlanRowsForCriteria(uploadedLessonPlanRows, subject, topic, criterionGroup.Members);

                    if (sourceRows.Count > 0)
                    {
                        foreach (var row in sourceRows)
                        {
                            var lessonDescription = ResolveGuideLessonDescription(
                                sourceLessonDescription: row.LessonPlanDescription,
                                fallbackPlans: fallbackPlans,
                                fallbackCriteriaDescription: criterion.Description,
                                fallbackTopicDescription: topic.TopicDescription);
                            var lessonContent = NormalizeLessonContentForGuide(row.LessonPlanContent);
                            lessonContent = HasMeaningfulLessonContent(lessonContent)
                                ? lessonContent
                                : string.Join("\n\n", fallbackPlans
                                    .Select(x => NormalizeLessonContentForGuide(x.Content))
                                    .Where(HasMeaningfulLessonContent));

                            if (paraphrase)
                            {
                                lessonDescription = await ParaphraseForGuideAsync(lessonDescription, true, cache);
                            }

                            lessonBlocks.Add(new GuideLessonBlock
                            {
                                Lpn = NormalizeLpn(row.Lpn),
                                LessonPlanDescription = lessonDescription,
                                LessonContent = lessonContent,
                                LecturerActions = string.Empty,
                                LearnerActions = string.Empty,
                                LearningAids = string.Empty,
                                TimeStart = string.Empty,
                                TimeEnd = string.Empty
                            });
                        }
                    }
                    else
                    {
                        var toolkitRows = ResolveToolkitRowsForCriteria(toolkit, criterionGroup.Members, subject);
                        if (toolkitRows.Count > 0)
                        {
                            var availableRows = toolkitRows
                                .Where(row => consumedToolkitRowIds.Add(row.Id))
                                .OrderByDescending(ScoreToolkitRowForGuide)
                                .ThenBy(x => ParseLpnSort(x.Lpn))
                                .ThenBy(x => x.Id)
                                .ToList();

                            foreach (var row in availableRows)
                            {
                                var lessonDescription = ResolveGuideLessonDescription(
                                    sourceLessonDescription: row.LessonPlanDescription,
                                    fallbackPlans: fallbackPlans,
                                    fallbackCriteriaDescription: criterion.Description,
                                    fallbackTopicDescription: topic.TopicDescription);

                                var normalizedToolkitLessonContent = NormalizeToolkitLessonContentForGuide(row);
                                var lessonContent = HasMeaningfulLessonContent(normalizedToolkitLessonContent)
                                    ? normalizedToolkitLessonContent
                                    : string.Join("\n\n", fallbackPlans
                                        .Select(x => NormalizeLessonContentForGuide(x.Content))
                                        .Where(HasMeaningfulLessonContent));

                                var lecturerActions = (row.LecturerActions ?? string.Empty).Trim();
                                var learnerActions = (row.LearnerActions ?? string.Empty).Trim();
                                if (paraphrase)
                                {
                                    lessonDescription = await ParaphraseForGuideAsync(lessonDescription, true, cache);
                                    lecturerActions = await ParaphraseForGuideAsync(lecturerActions, true, cache);
                                    learnerActions = await ParaphraseForGuideAsync(learnerActions, true, cache);
                                }

                                lessonBlocks.Add(new GuideLessonBlock
                                {
                                    Lpn = NormalizeLpn(row.Lpn),
                                    LessonPlanDescription = lessonDescription,
                                    LessonContent = lessonContent,
                                    LecturerActions = lecturerActions,
                                    LearnerActions = learnerActions,
                                    LearningAids = (row.LearningAids ?? string.Empty).Trim(),
                                    TimeStart = (row.TimeStart ?? string.Empty).Trim(),
                                    TimeEnd = (row.TimeEnd ?? string.Empty).Trim()
                                });
                            }
                        }
                    }

                    if (lessonBlocks.Count == 0)
                    {
                        foreach (var plan in fallbackPlans.OrderBy(x => x.SortOrder).ThenBy(x => x.Id))
                        {
                            var lessonDescription = (plan.Title ?? string.Empty).Trim();
                            if (string.IsNullOrWhiteSpace(lessonDescription))
                            {
                                lessonDescription = ResolveGuideLessonDescription(
                                    sourceLessonDescription: string.Empty,
                                    fallbackPlans: fallbackPlans,
                                    fallbackCriteriaDescription: criterion.Description,
                                    fallbackTopicDescription: topic.TopicDescription);
                            }

                            if (paraphrase)
                            {
                                lessonDescription = await ParaphraseForGuideAsync(lessonDescription, true, cache);
                            }

                            lessonBlocks.Add(new GuideLessonBlock
                            {
                                Lpn = plan.SortOrder > 0 ? $"LPN {plan.SortOrder}" : "LPN 1",
                                LessonPlanDescription = lessonDescription,
                                LessonContent = NormalizeLessonContentForGuide(plan.Content),
                                LecturerActions = string.Empty,
                                LearnerActions = string.Empty,
                                LearningAids = string.Empty,
                                TimeStart = string.Empty,
                                TimeEnd = string.Empty
                            });
                        }
                    }

                    lessonBlocks = await ApplyEvidenceFallbackToLessonBlocksAsync(
                        lessonBlocks,
                        pilotTopic,
                        topic.TopicCode,
                        topic.TopicDescription,
                        criterion.Description,
                        cache);

                    lessonBlocks = DeduplicateLessonBlocks(lessonBlocks);

                    criteriaSections.Add(new CriteriaGuideSection
                    {
                        CriteriaId = criterion.Id,
                        CriteriaDescription = (criterion.Description ?? string.Empty).Trim(),
                        Lessons = lessonBlocks.OrderBy(x => ParseLpnSort(x.Lpn)).ToList()
                    });
                }

                if (!TopicHasMeaningfulLessonContent(criteriaSections))
                {
                    var topicEvidenceBlocks = await ApplyEvidenceFallbackToLessonBlocksAsync(
                        new List<GuideLessonBlock>(),
                        pilotTopic,
                        topic.TopicCode,
                        topic.TopicDescription,
                        topic.TopicPurpose,
                        cache);
                    topicEvidenceBlocks = DeduplicateLessonBlocks(topicEvidenceBlocks)
                        .Where(block => HasMeaningfulLessonContent(block.LessonContent))
                        .ToList();
                    if (topicEvidenceBlocks.Count > 0)
                    {
                        criteriaSections.Add(new CriteriaGuideSection
                        {
                            CriteriaId = 0,
                            CriteriaDescription = (topic.TopicDescription ?? string.Empty).Trim(),
                            Lessons = topicEvidenceBlocks
                        });
                    }
                }

                if (!TopicHasMeaningfulLessonContent(criteriaSections))
                {
                    var topicFallback = BuildTopicAsLessonFallbackBlock(topic);
                    criteriaSections.Add(new CriteriaGuideSection
                    {
                        CriteriaId = 0,
                        CriteriaDescription = (topic.TopicDescription ?? topic.TopicPurpose ?? string.Empty).Trim(),
                        Lessons = new List<GuideLessonBlock> { topicFallback }
                    });
                }

                topicSections.Add(new TopicGuideSection
                {
                    TopicId = topic.Id,
                    TopicCode = (topic.TopicCode ?? string.Empty).Trim(),
                    TopicDescription = (topic.TopicDescription ?? string.Empty).Trim(),
                    TopicPurpose = (topic.TopicPurpose ?? string.Empty).Trim(),
                    Criteria = criteriaSections
                });
            }

            if (topicSections.Count == 0) return null;

            var subjectAssessmentCriteria = criteria
                .Select(c => (c.Description ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var workbookActivities = ResolveWorkbookActivitiesForSubject(qualification.Id, subject);

            var illustrationsByTopic = includeIllustrations
                ? await ResolveGuideIllustrationsByTopicAsync(
                    qualification,
                    subject,
                    topics,
                    Math.Clamp(maxIllustrationsPerTopic, 1, 4),
                    generateIllustrations,
                    cancellationToken)
                : new Dictionary<int, List<GuideIllustration>>();

            return new SubjectGuideChapter
            {
                Phase = phase,
                Subject = subject,
                Topics = topicSections,
                IllustrationsByTopic = illustrationsByTopic,
                SubjectAssessmentCriteria = subjectAssessmentCriteria,
                WorkbookActivities = workbookActivities
            };
        }

        private List<string> ResolveWorkbookActivitiesForSubject(int qualificationId, Subject subject)
        {
            var topicIds = _context.Topics
                .Where(t => t.SubjectId == subject.Id)
                .Select(t => t.Id)
                .ToList();
            if (topicIds.Count == 0) return new List<string>();

            var subtopicIds = _context.Subtopics
                .Where(st => topicIds.Contains(st.TopicId))
                .Select(st => st.Id)
                .ToList();

            var activities = subtopicIds.Count == 0
                ? new List<string>()
                : _context.Activities
                    .Where(a => subtopicIds.Contains(a.SubtopicId))
                    .OrderBy(a => a.Order ?? int.MaxValue)
                    .ThenBy(a => a.Id)
                    .Select(a => new
                    {
                        Name = (a.Name ?? string.Empty).Trim(),
                        Description = (a.Description ?? string.Empty).Trim()
                    })
                    .ToList()
                    .Select(a =>
                    {
                        if (string.IsNullOrWhiteSpace(a.Name)) return a.Description;
                        if (string.IsNullOrWhiteSpace(a.Description)) return a.Name;
                        return $"{a.Name}: {a.Description}";
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (activities.Count > 0) return activities;

            var subjectCode = (subject.SubjectCode ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(subjectCode)) return new List<string>();

            var toolkitActions = _context.LecturerToolkitEntries
                .Where(e => e.QualificationsId == qualificationId && e.SubjectCode == subjectCode)
                .ToList()
                .OrderBy(e => ParseLpnSort(e.Lpn))
                .ThenBy(e => e.Id)
                .Select(e => (e.LearnerActions ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            var normalized = new List<string>();
            foreach (var block in toolkitActions)
            {
                var lines = block
                    .Replace("\r\n", "\n")
                    .Replace('\r', '\n')
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x));
                normalized.AddRange(lines);
            }

            return normalized
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        [HttpGet("download-audio")]
        public async Task<IActionResult> DownloadAudio(
            [FromQuery] int? qualificationId = null,
            [FromQuery] int? subjectId = null,
            [FromQuery] bool paraphrase = false,
            [FromQuery] string? model = null,
            [FromQuery] string? voice = null,
            [FromQuery] string? format = "mp3",
            [FromQuery] double? speed = null,
            [FromQuery] int maxChunks = 24)
        {
            if (!AiRuntime.AllowOpenAi())
            {
                return StatusCode(403, "OpenAI TTS is disabled by AI_MODE.");
            }

            var key = (Secrets.GetOpenAIKey() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                return StatusCode(503, "OPENAI_API_KEY is not configured.");
            }

            var qualification = qualificationId.HasValue && qualificationId.Value > 0
                ? _context.Qualifications.FirstOrDefault(q => q.Id == qualificationId.Value)
                : _context.Qualifications.FirstOrDefault();
            if (qualification == null) return BadRequest("No qualification available for Learner Guide audio export.");

            var qualificationSubjects = _context.Subjects
                .Where(s => s.QualificationId == qualification.Id)
                .OrderBy(s => s.CurriculumPhaseId)
                .ThenBy(s => s.SubjectCode)
                .ToList();
            qualificationSubjects = qualificationSubjects.Where(HasSubjectIdentity).ToList();
            if (qualificationSubjects.Count == 0) return BadRequest("No subjects found for this qualification.");

            Subject? subject;
            if (subjectId.HasValue && subjectId.Value > 0)
            {
                subject = qualificationSubjects.FirstOrDefault(s => s.Id == subjectId.Value);
                if (subject == null) return BadRequest("Selected subjectId was not found for this qualification.");
            }
            else
            {
                subject = qualificationSubjects.FirstOrDefault();
            }
            if (subject == null) return BadRequest("No subject available for Learner Guide audio export.");

            var phase = _context.CurriculumPhases.FirstOrDefault(p => p.Id == subject.CurriculumPhaseId);
            var topics = _context.Topics
                .Where(t => t.SubjectId == subject.Id)
                .OrderBy(t => t.Order ?? int.MaxValue)
                .ThenBy(t => t.TopicCode)
                .ThenBy(t => t.Id)
                .ToList();
            if (topics.Count == 0) return BadRequest("No topics found for the selected subject.");

            var topicIds = topics.Select(t => t.Id).ToList();
            var criteria = _context.AssessmentCriteria
                .Where(c => topicIds.Contains(c.TopicId))
                .OrderBy(c => c.TopicId)
                .ThenBy(c => c.Id)
                .ToList();
            var criteriaIds = criteria.Select(c => c.Id).ToList();

            var lessonPlans = _context.LessonPlans
                .Where(lp => criteriaIds.Contains(lp.AssessmentCriteriaId))
                .OrderBy(lp => lp.SortOrder)
                .ThenBy(lp => lp.Id)
                .ToList();
            var lessonByCriteria = lessonPlans
                .GroupBy(lp => lp.AssessmentCriteriaId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var toolkit = _context.LecturerToolkitEntries
                .Where(e => e.QualificationsId == qualification.Id)
                .ToList()
                .OrderBy(e => ParseLpnSort(e.Lpn))
                .ThenBy(e => e.Id)
                .ToList();
            var uploadedLessonPlanRows = LoadUploadedLessonPlanRowsForSubject(qualification, subject);

            CurriculumDeliveryPilotService.TopicEvidenceSummary? pilotSummary = null;
            try
            {
                pilotSummary = await _pilotService.BuildTopicEvidenceSummaryAsync(qualification.Id, forceRefresh: false, HttpContext.RequestAborted);
            }
            catch
            {
                // Audio export can still fall back to any uploaded lesson-plan rows already present.
            }

            var cache = new Dictionary<string, string>(StringComparer.Ordinal);
            var criteriaByTopic = criteria.GroupBy(c => c.TopicId).ToDictionary(g => g.Key, g => g.ToList());
            var consumedToolkitRowIds = new HashSet<int>();
            var topicSections = new List<TopicGuideSection>();
            foreach (var topic in topics)
            {
                var topicCriteria = criteriaByTopic.TryGetValue(topic.Id, out var list)
                    ? list
                    : new List<AssessmentCriteria>();
                var pilotTopic = pilotSummary?.Topics?.FirstOrDefault(t => t.TopicId == topic.Id);

                var criteriaSections = new List<CriteriaGuideSection>();
                foreach (var criterion in topicCriteria)
                {
                    var lessonBlocks = new List<GuideLessonBlock>();
                    var fallbackPlans = lessonByCriteria.TryGetValue(criterion.Id, out var criterionPlans)
                        ? criterionPlans
                        : new List<LessonPlan>();
                    var sourceRows = ResolveUploadedLessonPlanRowsForCriteria(uploadedLessonPlanRows, subject, topic, new[] { criterion });

                    if (sourceRows.Count > 0)
                    {
                        foreach (var row in sourceRows)
                        {
                            var lessonDescription = ResolveGuideLessonDescription(
                                sourceLessonDescription: row.LessonPlanDescription,
                                fallbackPlans: fallbackPlans,
                                fallbackCriteriaDescription: criterion.Description,
                                fallbackTopicDescription: topic.TopicDescription);
                            var lessonContent = NormalizeLessonContentForGuide(row.LessonPlanContent);
                            lessonContent = HasMeaningfulLessonContent(lessonContent)
                                ? lessonContent
                                : string.Join("\n\n", fallbackPlans
                                    .Select(x => NormalizeLessonContentForGuide(x.Content))
                                    .Where(HasMeaningfulLessonContent)
                                    .Take(3));

                            if (paraphrase)
                            {
                                lessonDescription = await ParaphraseForGuideAsync(lessonDescription, true, cache);
                                lessonContent = await ParaphraseForGuideAsync(lessonContent, true, cache);
                            }

                            lessonBlocks.Add(new GuideLessonBlock
                            {
                                Lpn = NormalizeLpn(row.Lpn),
                                LessonPlanDescription = lessonDescription,
                                LessonContent = lessonContent,
                                LecturerActions = string.Empty,
                                LearnerActions = string.Empty,
                                LearningAids = string.Empty,
                                TimeStart = string.Empty,
                                TimeEnd = string.Empty
                            });
                        }
                    }
                    else
                    {
                        var toolkitRows = ResolveToolkitRowsForCriterion(toolkit, criterion, subject);
                        if (toolkitRows.Count > 0)
                        {
                            var availableRows = toolkitRows
                                .Where(row => consumedToolkitRowIds.Add(row.Id))
                                .OrderByDescending(ScoreToolkitRowForGuide)
                                .ThenBy(x => ParseLpnSort(x.Lpn))
                                .ThenBy(x => x.Id)
                                .ToList();

                            foreach (var row in availableRows)
                            {
                                var lessonDescription = (row.LessonPlanDescription ?? string.Empty).Trim();
                                if (string.IsNullOrWhiteSpace(lessonDescription))
                                {
                                    lessonDescription = fallbackPlans
                                        .Select(x => (x.Title ?? string.Empty).Trim())
                                        .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
                                        ?? string.Empty;
                                }

                                var normalizedToolkitLessonContent = NormalizeToolkitLessonContentForGuide(row);
                                var lessonContent = HasMeaningfulLessonContent(normalizedToolkitLessonContent)
                                    ? normalizedToolkitLessonContent
                                    : string.Join("\n\n", fallbackPlans
                                        .Select(x => NormalizeLessonContentForGuide(x.Content))
                                        .Where(HasMeaningfulLessonContent)
                                        .Take(3));

                                if (paraphrase)
                                {
                                    lessonDescription = await ParaphraseForGuideAsync(lessonDescription, true, cache);
                                    lessonContent = await ParaphraseForGuideAsync(lessonContent, true, cache);
                                }

                                lessonBlocks.Add(new GuideLessonBlock
                                {
                                    Lpn = NormalizeLpn(row.Lpn),
                                    LessonPlanDescription = lessonDescription,
                                    LessonContent = lessonContent,
                                    LecturerActions = string.Empty,
                                    LearnerActions = string.Empty,
                                    LearningAids = (row.LearningAids ?? string.Empty).Trim(),
                                    TimeStart = string.Empty,
                                    TimeEnd = string.Empty
                                });
                            }
                        }
                    }

                    if (lessonBlocks.Count == 0)
                    {
                        foreach (var plan in fallbackPlans.OrderBy(x => x.SortOrder).ThenBy(x => x.Id))
                        {
                            var lessonDescription = (plan.Title ?? string.Empty).Trim();
                            var lessonContent = NormalizeLessonContentForGuide(plan.Content);
                            if (paraphrase)
                            {
                                lessonDescription = await ParaphraseForGuideAsync(lessonDescription, true, cache);
                                lessonContent = await ParaphraseForGuideAsync(lessonContent, true, cache);
                            }

                            lessonBlocks.Add(new GuideLessonBlock
                            {
                                Lpn = plan.SortOrder > 0 ? $"LPN {plan.SortOrder}" : "LPN 1",
                                LessonPlanDescription = lessonDescription,
                                LessonContent = lessonContent,
                                LecturerActions = string.Empty,
                                LearnerActions = string.Empty,
                                LearningAids = string.Empty,
                                TimeStart = string.Empty,
                                TimeEnd = string.Empty
                            });
                        }
                    }

                    lessonBlocks = await ApplyEvidenceFallbackToLessonBlocksAsync(
                        lessonBlocks,
                        pilotTopic,
                        topic.TopicCode,
                        topic.TopicDescription,
                        criterion.Description,
                        cache);

                    lessonBlocks = DeduplicateLessonBlocks(lessonBlocks);

                    criteriaSections.Add(new CriteriaGuideSection
                    {
                        CriteriaId = criterion.Id,
                        CriteriaDescription = (criterion.Description ?? string.Empty).Trim(),
                        Lessons = lessonBlocks.OrderBy(x => ParseLpnSort(x.Lpn)).ToList()
                    });
                }

                topicSections.Add(new TopicGuideSection
                {
                    TopicId = topic.Id,
                    TopicCode = (topic.TopicCode ?? string.Empty).Trim(),
                    TopicDescription = (topic.TopicDescription ?? string.Empty).Trim(),
                    TopicPurpose = (topic.TopicPurpose ?? string.Empty).Trim(),
                    Criteria = criteriaSections
                });
            }

            if (topicSections.Count == 0)
            {
                return BadRequest("No learner-guide topic sections found for audio export.");
            }

            var script = BuildLearnerGuideNarrationScript(qualification, phase, subject, topicSections);
            if (string.IsNullOrWhiteSpace(script))
            {
                return BadRequest("No learner-guide text is available to synthesize.");
            }

            var normalizedFormat = NormalizeTtsFormat(format);
            var normalizedModel = string.IsNullOrWhiteSpace(model)
                ? (Environment.GetEnvironmentVariable("OPENAI_TTS_MODEL") ?? "gpt-4o-mini-tts").Trim()
                : model.Trim();
            var normalizedVoice = string.IsNullOrWhiteSpace(voice)
                ? (Environment.GetEnvironmentVariable("OPENAI_TTS_VOICE") ?? "alloy").Trim()
                : voice.Trim();
            var normalizedSpeed = Math.Clamp(speed.GetValueOrDefault(1.0), 0.25, 2.0);
            var chunks = SplitNarrationIntoChunks(script, 3600, Math.Clamp(maxChunks, 1, 60));
            if (chunks.Count == 0) return BadRequest("Narration chunking produced no output.");

            using var zipStream = new MemoryStream();
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
            {
                var manifest = zip.CreateEntry("manifest.txt", CompressionLevel.Fastest);
                await using (var manifestWriter = new StreamWriter(manifest.Open(), Encoding.UTF8))
                {
                    await manifestWriter.WriteLineAsync("Learner Guide Audio Export");
                    await manifestWriter.WriteLineAsync($"Generated UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
                    await manifestWriter.WriteLineAsync($"Qualification: {(qualification.QualificationNumber ?? "").Trim()} - {(qualification.QualificationDescription ?? "").Trim()}");
                    await manifestWriter.WriteLineAsync($"Subject: {(subject.SubjectCode ?? "").Trim()} - {(subject.SubjectDescription ?? "").Trim()}");
                    await manifestWriter.WriteLineAsync($"Model: {normalizedModel}");
                    await manifestWriter.WriteLineAsync($"Voice: {normalizedVoice}");
                    await manifestWriter.WriteLineAsync($"Format: {normalizedFormat}");
                    await manifestWriter.WriteLineAsync($"Speed: {normalizedSpeed:0.##}");
                    await manifestWriter.WriteLineAsync($"Chunks: {chunks.Count}");
                }

                var scriptEntry = zip.CreateEntry("script.txt", CompressionLevel.Fastest);
                await using (var scriptWriter = new StreamWriter(scriptEntry.Open(), Encoding.UTF8))
                {
                    await scriptWriter.WriteAsync(script);
                }

                var failures = new List<string>();
                var successCount = 0;
                for (var i = 0; i < chunks.Count; i++)
                {
                    var text = chunks[i];
                    var audio = await TrySynthesizeOpenAiTtsChunkAsync(
                        key,
                        normalizedModel,
                        normalizedVoice,
                        normalizedFormat,
                        normalizedSpeed,
                        text,
                        HttpContext.RequestAborted);
                    if (audio.Bytes == null || audio.Bytes.Length == 0)
                    {
                        failures.Add($"Part {i + 1:000}: {audio.Error}");
                        continue;
                    }

                    successCount += 1;
                    var entry = zip.CreateEntry($"part_{i + 1:000}.{normalizedFormat}", CompressionLevel.NoCompression);
                    await using var stream = entry.Open();
                    await stream.WriteAsync(audio.Bytes, 0, audio.Bytes.Length, HttpContext.RequestAborted);
                }

                if (failures.Count > 0)
                {
                    var failEntry = zip.CreateEntry("_errors.txt", CompressionLevel.Fastest);
                    await using var failWriter = new StreamWriter(failEntry.Open(), Encoding.UTF8);
                    await failWriter.WriteLineAsync("Some TTS chunks failed:");
                    foreach (var failure in failures)
                    {
                        await failWriter.WriteLineAsync(failure);
                    }
                }

                if (successCount == 0)
                {
                    return StatusCode(502, failures.Count > 0 ? string.Join(" | ", failures.Take(2)) : "TTS synthesis returned no audio data.");
                }
            }

            zipStream.Position = 0;
            var safeQualification = SafeFileNameToken(qualification.QualificationNumber);
            var safeSubject = SafeFileNameToken(subject.SubjectCode);
            var fileName = $"LearnerGuide_Audio_{safeQualification}_{safeSubject}_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            return File(zipStream.ToArray(), "application/zip", fileName);
        }

        private async Task<string> ParaphraseForGuideAsync(string text, bool enabled, Dictionary<string, string> cache)
        {
            var trimmed = (text ?? string.Empty).Trim();
            // Paraphrase shorter blocks as well (lowered threshold from 32 -> 16)
            if (!enabled || string.IsNullOrWhiteSpace(trimmed) || trimmed.Length < 16) return trimmed;
            if (cache.TryGetValue(trimmed, out var cached)) return cached;
            string finalText;
            try
            {
                var (paraphrased, backend) = await ParaphraseTextWithBackendAsync(trimmed, "educational", true);
                finalText = string.IsNullOrWhiteSpace(paraphrased) || backend == "unavailable"
                    ? trimmed
                    : paraphrased.Trim();
            }
            catch
            {
                finalText = trimmed;
            }
            cache[trimmed] = finalText;
            return finalText;
        }

        private async Task<(string text, string backend)> ParaphraseTextWithBackendAsync(string text, string style, bool preserveTerminology)
        {
            var preferLocalFirst = AiRuntime.PreferLocalFirst();
            var cloudEnabled = AiRuntime.AllowCloudProviders();
            var allowOpenAi = AiRuntime.AllowOpenAi();
            var wrapperPriority = (Environment.GetEnvironmentVariable("PARAPHRASE_WRAPPER_PRIORITY") ?? "fallback").Trim();
            var wrapperFirst = preferLocalFirst || wrapperPriority.Equals("first", StringComparison.OrdinalIgnoreCase);
            if (wrapperFirst)
            {
                var wrapperFirstText = await TryParaphraseWithLocalWrapperAsync(text, style, preserveTerminology);
                if (!string.IsNullOrWhiteSpace(wrapperFirstText)) return (wrapperFirstText, "local_wrapper");
            }

            if (cloudEnabled && allowOpenAi)
            {
                var openAi = await TryParaphraseWithOpenAiAsync(text, style, preserveTerminology);
                if (!string.IsNullOrWhiteSpace(openAi)) return (openAi, "openai");
            }

            if (!wrapperFirst)
            {
                var wrapperFallbackText = await TryParaphraseWithLocalWrapperAsync(text, style, preserveTerminology);
                if (!string.IsNullOrWhiteSpace(wrapperFallbackText)) return (wrapperFallbackText, "local_wrapper");
            }

            return (text, "unavailable");
        }

        private static string BuildLearnerGuideParaphrasePrompt(string style, bool preserveTerminology)
        {
            var rules = LearningMaterialAuthoringRulesStore.Load();
            var prompt = new StringBuilder();
            prompt.AppendLine("Paraphrase the text for a learner guide.");
            prompt.AppendLine("Keep meaning, sequence, and technical accuracy.");
            prompt.AppendLine($"Style: {style}.");
            prompt.AppendLine($"Preserve terminology: {(preserveTerminology ? "yes" : "no")}.");
            prompt.AppendLine("Address the learner directly as 'you'.");
            prompt.AppendLine("Do not write 'the learner must' or similar third-person lecturer wording.");
            prompt.AppendLine("Never refer to shorthand assessment-criteria codes such as KT0101, AC01, KG01, or LPN numbers.");
            prompt.AppendLine("When assessment criteria must be shown, write the full criteria in plain English.");
            prompt.AppendLine("Use curriculum subject, topic, and assessment criteria only as headers or section labels, not as filler sentences inside the lesson text.");
            prompt.AppendLine("Explain the subject matter in full step-by-step detail so the learner can follow the guide without extra lecturer explanation.");
            prompt.AppendLine("Do not force the lesson into fixed lecturer-style section headings or mandatory subsection blocks. Let each topic expand naturally according to the actual subject matter.");
            prompt.AppendLine("If the source text contains drafting instructions or lesson-plan directives such as 'Explain KT0101...' or 'Show learners how to apply...', rewrite that into actual learner-facing explanation and remove the instruction wording.");
            prompt.AppendLine("Do not return meta-instructions about what must be explained. Return the explanation itself.");
            if (rules.DisableRigidLessonTemplate)
            {
                prompt.AppendLine("Do not convert the text into lecturer presentation notes or rigid teaching templates unless explicitly asked.");
                prompt.AppendLine("Keep the table of contents to one level only when headings are produced.");
            }

            if (!string.IsNullOrWhiteSpace(rules.SourceMaterialPriorityRules))
            {
                prompt.AppendLine($"Source material priority: {rules.SourceMaterialPriorityRules}");
            }

            if (!string.IsNullOrWhiteSpace(rules.LearnerGuideRules))
            {
                prompt.AppendLine($"Learner guide rules: {rules.LearnerGuideRules}");
            }

            prompt.AppendLine("Return only paraphrased text.");
            return prompt.ToString().Trim();
        }

        private async Task<string?> TryParaphraseWithFoundryAsync(string text, string style, bool preserveTerminology)
        {
            if (!AiRuntime.AllowFoundry()) return null;
            var endpoint = Environment.GetEnvironmentVariable("FOUNDRY_RESPONSES_ENDPOINT") ?? DefaultModeratorResponsesEndpoint;
            var foundryApiKey = Environment.GetEnvironmentVariable("FOUNDRY_API_KEY") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(foundryApiKey)) return null;

            var instructions = BuildLearnerGuideParaphrasePrompt(style, preserveTerminology);
            var payload = new { input = text, instructions };
            using var msg = new HttpRequestMessage(HttpMethod.Post, endpoint);
            msg.Headers.Add("api-key", foundryApiKey);
            msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var resp = await _http.SendAsync(msg);
            var json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return null;
            return TryExtractResponseOutputText(json);
        }

        private async Task<string?> TryParaphraseWithOpenAiAsync(string text, string style, bool preserveTerminology)
        {
            var prompt = BuildLearnerGuideParaphrasePrompt(style, preserveTerminology);
            if (AiRuntime.AllowGoogle())
            {
                try
                {
                    var geminiResult = await GoogleGeminiClient.GenerateContentAsync(text, systemInstruction: prompt);
                    if (!string.IsNullOrWhiteSpace(geminiResult)) return geminiResult.Trim();
                }
                catch { }
            }
            if (!AiRuntime.AllowOpenAi()) return null;
            var key = Secrets.GetOpenAIKey();
            if (string.IsNullOrWhiteSpace(key)) return null;
            var model = AiRuntime.GetOpenAiModel("gemini-3.8-flash");
            var payload = new
            {
                model,
                messages = new[] { new { role = "system", content = prompt }, new { role = "user", content = text } },
                temperature = 0.35
            };
            using var msg = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var resp = await _http.SendAsync(msg);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return null;
            return TryExtractChatCompletionText(body);
        }

        private async Task<string?> TryParaphraseWithLocalWrapperAsync(string text, string style, bool preserveTerminology)
        {
            if (IsLocalParaphraseBackoffActive())
            {
                return null;
            }

            var endpoint = AiRuntime.GetLocalLlmEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                endpoint = Environment.GetEnvironmentVariable("PARAPHRASE_WRAPPER_ENDPOINT")
                    ?? Environment.GetEnvironmentVariable("LOCAL_PARAPHRASE_ENDPOINT")
                    ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(endpoint)) return null;

            var model = AiRuntime.GetLocalLlmModel();
            if (string.IsNullOrWhiteSpace(model))
            {
                model = Environment.GetEnvironmentVariable("PARAPHRASE_WRAPPER_MODEL");
                if (string.IsNullOrWhiteSpace(model)) model = "local-paraphrase";
            }

            var prompt = BuildLearnerGuideParaphrasePrompt(style, preserveTerminology);
            var payload = new
            {
                model,
                messages = new[] { new { role = "system", content = prompt }, new { role = "user", content = text } },
                temperature = 0.2
            };

            using var msg = new HttpRequestMessage(HttpMethod.Post, endpoint.Trim());
            var wrapperApiKey = AiRuntime.GetLocalLlmApiKey();
            if (string.IsNullOrWhiteSpace(wrapperApiKey))
            {
                wrapperApiKey = Environment.GetEnvironmentVariable("PARAPHRASE_WRAPPER_API_KEY") ?? string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(wrapperApiKey))
            {
                var token = wrapperApiKey.Trim();
                if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    token = token.Substring(7).Trim();
                if (!string.IsNullOrWhiteSpace(token))
                    msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var timeoutSeconds = Math.Clamp(
                ParseInt(Environment.GetEnvironmentVariable("LEARNER_GUIDE_PARAPHRASE_TIMEOUT_SECONDS"), 12),
                3,
                100);

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var resp = await _http.SendAsync(msg, timeout.Token);
                var body = await resp.Content.ReadAsStringAsync(timeout.Token);
                if (!resp.IsSuccessStatusCode) return null;
                return TryExtractChatCompletionText(body) ?? TryExtractResponseOutputText(body);
            }
            catch (TaskCanceledException)
            {
                MarkLocalParaphraseBackoff();
                return null;
            }
            catch (HttpRequestException)
            {
                MarkLocalParaphraseBackoff();
                return null;
            }
            catch (IOException)
            {
                MarkLocalParaphraseBackoff();
                return null;
            }
        }

        private static bool IsLocalParaphraseBackoffActive()
        {
            lock (LocalParaphraseBackoffLock)
            {
                return DateTime.UtcNow < _localParaphraseUnavailableUntilUtc;
            }
        }

        private static void MarkLocalParaphraseBackoff()
        {
            var seconds = Math.Clamp(
                ParseInt(Environment.GetEnvironmentVariable("LEARNER_GUIDE_PARAPHRASE_BACKOFF_SECONDS"), 600),
                30,
                3600);
            lock (LocalParaphraseBackoffLock)
            {
                _localParaphraseUnavailableUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
            }
        }

        private static string? TryExtractChatCompletionText(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("message", out var directMessage)
                    && directMessage.ValueKind == JsonValueKind.Object
                    && directMessage.TryGetProperty("content", out var directContent))
                {
                    return ReadChatContentText(directContent);
                }
                if (doc.RootElement.TryGetProperty("response", out var responseText)
                    && responseText.ValueKind == JsonValueKind.String)
                {
                    var response = responseText.GetString();
                    if (!string.IsNullOrWhiteSpace(response)) return response;
                }
                if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return null;
                var m = choices[0].TryGetProperty("message", out var msgObj) ? msgObj : default;
                if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("content", out var c)) return null;
                return ReadChatContentText(c);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? ReadChatContentText(JsonElement content)
        {
            if (content.ValueKind == JsonValueKind.String) return content.GetString();
            if (content.ValueKind != JsonValueKind.Array) return null;
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object &&
                    part.TryGetProperty("text", out var txt) &&
                    txt.ValueKind == JsonValueKind.String)
                {
                    var t = txt.GetString();
                    if (!string.IsNullOrWhiteSpace(t)) return t;
                }
            }
            return null;
        }

        private static string? TryExtractResponseOutputText(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return null;
                foreach (var item in output.EnumerateArray())
                {
                    if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var txt))
                        {
                            var t = txt.GetString();
                            if (!string.IsNullOrWhiteSpace(t)) return t;
                        }
                    }
                }

                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string HeuristicParaphrase(string text)
        {
            var cleaned = Regex.Replace((text ?? string.Empty).Trim(), @"\s+", " ");
            var repl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "must", "is required to" }, { "should", "is expected to" }, { "demonstrate", "show" }, { "use", "apply" }, { "ensure", "make sure" }
            };
            foreach (var kv in repl)
            {
                cleaned = Regex.Replace(cleaned, $@"\b{Regex.Escape(kv.Key)}\b", kv.Value, RegexOptions.IgnoreCase);
            }
            return cleaned;
        }

        private static string GetParaphraseCachePath(int qualificationId)
        {
            var root = Path.Combine(Directory.GetCurrentDirectory(), "Exports", "paraphrase-cache");
            return Path.Combine(root, $"qualification-{qualificationId}.json");
        }

        private static Dictionary<string, string> LoadParaphraseMap(int qualificationId)
        {
            var file = LoadParaphraseCacheFile(qualificationId);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (file.Entries == null) return map;
            foreach (var kv in file.Entries)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null || string.IsNullOrWhiteSpace(kv.Value.ParaphrasedText)) continue;
                map[kv.Key] = kv.Value.ParaphrasedText.Trim();
            }
            return map;
        }

        private static ParaphraseCacheFile LoadParaphraseCacheFile(int qualificationId)
        {
            var path = GetParaphraseCachePath(qualificationId);
            if (!System.IO.File.Exists(path))
                return new ParaphraseCacheFile { QualificationId = qualificationId };

            try
            {
                var json = System.IO.File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<ParaphraseCacheFile>(json);
                if (loaded == null) return new ParaphraseCacheFile { QualificationId = qualificationId };
                loaded.QualificationId = loaded.QualificationId <= 0 ? qualificationId : loaded.QualificationId;
                loaded.Entries ??= new Dictionary<string, ParaphraseCacheEntry>(StringComparer.Ordinal);
                return loaded;
            }
            catch
            {
                return new ParaphraseCacheFile { QualificationId = qualificationId };
            }
        }

        private static void SaveParaphraseCacheFile(ParaphraseCacheFile file)
        {
            var path = GetParaphraseCachePath(file.QualificationId);
            var dir = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(path, json);
        }

        private static void AppendCoverPage(Body body, MainDocumentPart main, Qualification qualification, Subject? subject)
        {
            var coverPath = ResolveLearnerGuideCoverPath();
            var institutionLine = (qualification.LearningInstitutionName ?? string.Empty).Trim();
            var phaseLine = "Phase: Knowledge Learning";
            var nqfAndCreditsLine = BuildLearnerGuideCoverNqfCreditsLine(qualification);
            var qualificationLine = BuildCoverQualificationLine(qualification);
            var subjectLine = subject == null
                ? string.Empty
                : $"{(subject.SubjectCode ?? string.Empty).Trim()} {(subject.SubjectDescription ?? string.Empty).Trim()}".Trim();
            const string coverTextColor = "000000";
            var topBlockStartTwips = CentimetresToTwips(7.0d);
            var phaseBlockGapTwips = string.IsNullOrWhiteSpace(subjectLine)
                ? CentimetresToTwips(11.4d)
                : CentimetresToTwips(9.8d);
            var coverLines = new List<DocxCoverPageOverlay.CoverTextLine>();
            if (!string.IsNullOrWhiteSpace(institutionLine))
            {
                coverLines.Add(new DocxCoverPageOverlay.CoverTextLine
                {
                    Text = institutionLine.ToUpperInvariant(),
                    FontSizeHalfPt = 52,
                    Bold = true,
                    BeforeTwips = topBlockStartTwips,
                    AfterTwips = 120,
                    ColorHex = coverTextColor
                });
            }
            if (!string.IsNullOrWhiteSpace(qualificationLine))
            {
                coverLines.Add(new DocxCoverPageOverlay.CoverTextLine
                {
                    Text = qualificationLine,
                    FontSizeHalfPt = 50,
                    Bold = true,
                    BeforeTwips = 520,
                    AfterTwips = 120,
                    ColorHex = coverTextColor
                });
            }
            if (!string.IsNullOrWhiteSpace(subjectLine))
            {
                coverLines.Add(new DocxCoverPageOverlay.CoverTextLine
                {
                    Text = subjectLine.ToUpperInvariant(),
                    FontSizeHalfPt = 28,
                    Bold = true,
                    BeforeTwips = 900,
                    AfterTwips = 0,
                    ColorHex = coverTextColor
                });
            }
            coverLines.Add(new DocxCoverPageOverlay.CoverTextLine
            {
                Text = phaseLine,
                FontSizeHalfPt = 44,
                Bold = true,
                BeforeTwips = phaseBlockGapTwips,
                AfterTwips = 120,
                ColorHex = coverTextColor
            });
            if (!string.IsNullOrWhiteSpace(nqfAndCreditsLine))
            {
                coverLines.Add(new DocxCoverPageOverlay.CoverTextLine
                {
                    Text = nqfAndCreditsLine,
                    FontSizeHalfPt = 40,
                    Bold = true,
                    BeforeTwips = 240,
                    AfterTwips = 120,
                    ColorHex = coverTextColor
                });
            }

            var appended = DocxCoverPageOverlay.TryAppendCenteredPortraitCoverPage(
                body,
                main,
                coverPath,
                coverLines,
                PortraitA4PageWidthTwips,
                1U);

            if (appended)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(institutionLine))
            {
                body.Append(CenterPara(institutionLine.ToUpperInvariant(), 36, bold: true));
            }
            body.Append(CenterPara(phaseLine, 32, bold: true));
            if (!string.IsNullOrWhiteSpace(nqfAndCreditsLine))
            {
                body.Append(CenterPara(nqfAndCreditsLine, 30, bold: true));
            }
            if (!string.IsNullOrWhiteSpace(qualificationLine))
            {
                body.Append(CenterPara(qualificationLine, 36, bold: true));
            }
            if (!string.IsNullOrWhiteSpace(subjectLine))
            {
                body.Append(CenterPara(subjectLine.ToUpperInvariant(), 24, bold: true));
            }
            if (string.IsNullOrWhiteSpace(institutionLine) &&
                string.IsNullOrWhiteSpace(nqfAndCreditsLine) &&
                string.IsNullOrWhiteSpace(qualificationLine) &&
                string.IsNullOrWhiteSpace(subjectLine))
            {
                body.Append(StyledHeading("Learner Guide", "Heading1", 30));
            }
        }

        private static void AppendTopicPage(
            Body body,
            Qualification qualification,
            CurriculumPhase? phase,
            Subject subject,
            IReadOnlyList<TopicGuideSection> topics)
        {
            body.Append(StyledHeading("Topic Page", "Heading1", 24));
            body.Append(BodyPara($"Qualification: {qualification.QualificationNumber} - {qualification.QualificationDescription}", 22, 0));
            body.Append(BodyPara($"Phase: {phase?.Name ?? "Not assigned"}", 22, 0));
            body.Append(BodyPara($"Subject: {subject.SubjectCode} - {subject.SubjectDescription}", 22, 0));

            var subjectMeta = new List<string>();
            if (subject.SubjectCredits.HasValue) subjectMeta.Add($"Credits: {subject.SubjectCredits.Value}");
            if (subject.SubjectPercentage.HasValue) subjectMeta.Add($"Percentage: {subject.SubjectPercentage.Value}%");
            if (subject.SubjectNQFLevel.HasValue) subjectMeta.Add($"NQF: {subject.SubjectNQFLevel.Value}");
            if (subjectMeta.Count > 0)
            {
                body.Append(BodyPara(string.Join(" | ", subjectMeta), 22, 0));
            }

            var purpose = (subject.SubjectPurpose ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(purpose))
            {
                body.Append(BodyPara($"Purpose: {purpose}", 22, 0));
            }

            body.Append(StyledHeading("Topics Included", "Heading2", 18));
            for (var i = 0; i < topics.Count; i++)
            {
                var t = topics[i];
                var criteriaCount = t.Criteria.Count;
                body.Append(BodyPara($"{i + 1}. {t.TopicCode} - {t.TopicDescription} (Criteria: {criteriaCount})", 22, 0));
            }
        }

        private static void AppendTableOfContentsPage(Body body)
        {
            body.Append(StyledHeading("TABLE OF CONTENT", "Heading1", 18));
            body.Append(BuildTableOfContentsField());
        }

        private static void AppendDisclaimerPage(Body body, Qualification qualification)
        {
            var year = DateTime.Now.Year;
            var institution = (qualification.LearningInstitutionName ?? string.Empty).Trim();
            const int disclaimerBodySizeHalfPt = 21;

            body.Append(StyledHeading("DISCLAIMER", "Heading1", 18));
            body.Append(BodyPara("ETDP Courseware Release ETDP RSA PATENT 004/026785", disclaimerBodySizeHalfPt, 0));
            body.Append(BodyPara($"(C) {year} by Dr P.C. Wepener. This document is generated by the ETDP App under the authority and final approval of the authorised learning-material owner.", disclaimerBodySizeHalfPt));
            body.Append(BodyPara("Neither Dr P.C. Wepener nor the ETDP App is accountable or liable for the correctness, completeness, factual, or academic correctness of this document. The accredited learning institution should be contacted for content inquiries, sources, references, or citations.", disclaimerBodySizeHalfPt));

            body.Append(StyledHeading("NOTICE OF RIGHTS", "Heading2", 14));
            body.Append(BodyPara("No part of this publication may be reproduced, transmitted, transcribed, stored in a retrieval system, or translated into any language or computer language, in any form or by any means, electronic, mechanical, magnetic, optical, chemical, manual, or otherwise, without prior written permission from the branded learning institution that owns the legal and intellectual property rights to the content of this document.", disclaimerBodySizeHalfPt));

            body.Append(StyledHeading("TRADEMARK NOTICE", "Heading2", 14));
            body.Append(BodyPara("Throughout this courseware title, trademark names may be used. Rather than placing a trademark symbol at every occurrence, names are used in an editorial manner for the benefit of the trademark owner, with no intention of infringement.", disclaimerBodySizeHalfPt));

            body.Append(StyledHeading("NOTICE OF LIABILITY", "Heading2", 14));
            body.Append(BodyPara("The information in this courseware title is distributed on an 'as is' basis, without warranty. While every precaution has been taken in preparation of this courseware, neither Dr P.C. Wepener nor the ETDP App shall have any liability to any person or entity for any loss or damage caused, or alleged to be caused, directly or indirectly by the instructions in this document or by the learning design and development processes described in it.", disclaimerBodySizeHalfPt));
            body.Append(BodyPara("A sincere effort has been made to ensure typology accuracy of the material; however, no warranty, express or implied, is made regarding quality, correctness, reliability, accuracy, or freedom from error of this document or the products it describes. Data used in examples and sample files may be fictional. Any resemblance to real persons or companies is coincidental.", disclaimerBodySizeHalfPt));

            body.Append(StyledHeading("TERMS AND CONDITIONS", "Heading2", 14));
            body.Append(BodyPara("This document is developed for the learning institution holding a legal permit and may not be resold by the learning institution. Sample versions may be shared but may not be resold to a third party. For licensed users, this document may only be used under the terms of the license agreement between the learning institution and Dr P.C. Wepener.", disclaimerBodySizeHalfPt));

            if (!string.IsNullOrWhiteSpace(institution))
            {
                body.Append(BodyPara($"Learning Institution: {institution}", disclaimerBodySizeHalfPt, 0, bold: true));
            }
            body.Append(BodyPara("PC WEPENER (Ph.D.) BUSINESS MANAGEMENT UJ 2005", disclaimerBodySizeHalfPt, 0));
            body.Append(BodyPara($"Pretoria, South Africa, {year}.", disclaimerBodySizeHalfPt, 0));
        }

        private static void AppendSubjectChapter(
            Body body,
            MainDocumentPart main,
            Qualification qualification,
            CurriculumPhase? phase,
            Subject subject,
            IReadOnlyList<TopicGuideSection> topics,
            IReadOnlyDictionary<int, List<GuideIllustration>> illustrationsByTopic,
            IReadOnlyList<string> subjectAssessmentCriteria,
            IReadOnlyList<string> workbookActivities,
            int chapterNumber,
            ref uint drawingId)
        {
            _ = qualification;
            _ = phase;

            body.Append(BuildChapterHeading($"CHAPTER {chapterNumber}"));
            body.Append(BuildSubjectHeading($"{(subject.SubjectCode ?? string.Empty).Trim()}: {(subject.SubjectDescription ?? string.Empty).Trim()}"));

            var activeTopics = topics
                .Where(topic => topic != null)
                .ToList();

            if (activeTopics.Count > 0)
            {
                body.Append(BodyPara("In this Chapter we will discuss the following Topics:", 22, 0, bold: true));
                foreach (var topic in activeTopics)
                {
                    body.Append(BodyPara($"{topic.TopicCode} {topic.TopicDescription}".Trim(), 22, 360));
                }
            }

            if (subjectAssessmentCriteria != null && subjectAssessmentCriteria.Count > 0)
            {
                body.Append(StyledHeading("INTERNAL ASSESSMENT CRITERIA AND WEIGHT", "Heading3", 13));
                body.Append(BodyPara("By the end of this Chapter the learner will be able to:", 22, 0, bold: true));
                foreach (var criterion in subjectAssessmentCriteria.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    body.Append(BodyPara(criterion.Trim(), 22, 360));
                }
            }

            foreach (var topic in activeTopics)
            {
                body.Append(BuildTopicHeading($"TOPIC {topic.TopicCode}: {topic.TopicDescription}"));

                var lessonBlocks = BuildTopicLessonBlocks(topic);
                if (lessonBlocks.Count > 0)
                {
                    foreach (var lesson in lessonBlocks)
                    {
                        var lessonContent = (lesson.LessonContent ?? string.Empty).Trim();
                        if (!HasMeaningfulLessonContent(lessonContent))
                        {
                            continue;
                        }

                        var lessonHeading = BuildLessonPlanHeadingText(lesson.Lpn, lesson.LessonPlanDescription);
                        if (!string.IsNullOrWhiteSpace(lessonHeading))
                        {
                            body.Append(BodyPara(lessonHeading, 22, 0, bold: true));
                        }

                        AppendExactLessonPlanContent(body, lessonContent, 22);
                    }
                }

                if (illustrationsByTopic != null &&
                    illustrationsByTopic.TryGetValue(topic.TopicId, out var topicIllustrations) &&
                    topicIllustrations.Count > 0)
                {
                    AppendTopicIllustrations(body, main, topicIllustrations, ref drawingId);
                }
            }

            if (activeTopics.Count > 0)
            {
                body.Append(StyledHeading("IN SUMMARY", "Heading3", 13));
                body.Append(BodyPara("In this unit we have focussed on:", 22, 0, bold: true));
                foreach (var topic in activeTopics)
                {
                    body.Append(BodyPara($"{topic.TopicCode} {topic.TopicDescription}".Trim(), 22, 360));
                }
            }

            var discussionCount = subjectAssessmentCriteria?.Count(x => !string.IsNullOrWhiteSpace(x)) ?? 0;
            if (discussionCount == 0)
            {
                discussionCount = ResolveWorkbookActivityCount(activeTopics, workbookActivities);
            }

            if (discussionCount > 0)
            {
                body.Append(BodyPara(BuildWorkbookActivitiesSummaryLine(discussionCount), 22, 0));
            }
        }

        private static void AppendTopicIllustrations(
            Body body,
            MainDocumentPart main,
            IReadOnlyList<GuideIllustration> illustrations,
            ref uint drawingId)
        {
            if (illustrations == null || illustrations.Count == 0) return;

            body.Append(StyledHeading("TOPIC ILLUSTRATIONS", "Heading3", 14));
            var contentWidthTwips = PortraitA4PageWidthTwips - 2040U;
            var contentHeightTwips = 4200U;

            foreach (var item in illustrations
                .Where(i => i != null && !string.IsNullOrWhiteSpace(i.ImagePath) && System.IO.File.Exists(i.ImagePath))
                .Take(4))
            {
                try
                {
                    using var stream = System.IO.File.OpenRead(item.ImagePath);
                    var imagePart = main.AddImagePart(ResolveImagePartType(item.ImagePath));
                    imagePart.FeedData(stream);
                    var relId = main.GetIdOfPart(imagePart);
                    var drawing = BuildInlineImage(
                        relId,
                        TwipsToEmu(contentWidthTwips),
                        TwipsToEmu(contentHeightTwips),
                        drawingId++,
                        Path.GetFileName(item.ImagePath));

                    var centered = new ParagraphProperties(
                        new SpacingBetweenLines { Before = "80", After = "80" },
                        new Justification { Val = JustificationValues.Center });
                    body.Append(new Paragraph(centered, new Run(drawing)));

                    var sourceText = string.IsNullOrWhiteSpace(item.Source) ? string.Empty : $" ({item.Source})";
                    var caption = string.IsNullOrWhiteSpace(item.Caption)
                        ? Path.GetFileNameWithoutExtension(item.ImagePath)
                        : item.Caption;
                    body.Append(BodyPara($"Illustration{sourceText}: {caption}", 20, 0));
                }
                catch
                {
                    // Skip unreadable image files without breaking export.
                }
            }
        }

        private async Task<Dictionary<int, List<GuideIllustration>>> ResolveGuideIllustrationsByTopicAsync(
            Qualification qualification,
            Subject subject,
            IReadOnlyList<Topic> topics,
            int maxIllustrationsPerTopic,
            bool generateIllustrations,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<int, List<GuideIllustration>>();
            if (topics == null || topics.Count == 0) return result;

            var qualificationCode = (qualification.QualificationNumber ?? string.Empty).Trim();
            var subjectCode = (subject.SubjectCode ?? string.Empty).Trim();
            var subjectDescription = (subject.SubjectDescription ?? string.Empty).Trim();
            var maxPerTopic = Math.Clamp(maxIllustrationsPerTopic, 1, 4);

            var materials = _context.SourceMaterials
                .AsNoTracking()
                .OrderByDescending(s => s.KnowledgeUploadedAtUtc ?? s.CreatedAt)
                .Take(1800)
                .ToList();

            var openAiAllowed = generateIllustrations && AiRuntime.AllowOpenAi();
            var openAiKey = openAiAllowed ? (Secrets.GetOpenAIKey() ?? string.Empty).Trim() : string.Empty;
            var imageModel = (Environment.GetEnvironmentVariable("OPENAI_IMAGE_MODEL") ?? "gpt-image-1").Trim();
            if (string.IsNullOrWhiteSpace(imageModel))
            {
                imageModel = "gpt-image-1";
            }

            foreach (var topic in topics)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var local = ResolveGuideIllustrationsFromSourceMaterials(
                    materials,
                    qualificationCode,
                    subjectCode,
                    subjectDescription,
                    topic)
                    .Take(maxPerTopic)
                    .ToList();

                var combined = new List<GuideIllustration>(local);
                var remaining = maxPerTopic - combined.Count;
                if (remaining > 0 &&
                    openAiAllowed &&
                    !string.IsNullOrWhiteSpace(openAiKey))
                {
                    var outputDir = Path.Combine(EtdpPaths.GetExportsRoot(), "LearnerGuide", "GeneratedImages", $"topic_{topic.Id}");
                    Directory.CreateDirectory(outputDir);

                    for (var i = 0; i < remaining; i += 1)
                    {
                        var prompt = BuildGuideGeneratedImagePrompt(qualification, subject, topic, i + 1);
                        var generated = await TryGenerateGuideImageWithOpenAiAsync(
                            openAiKey,
                            imageModel,
                            "1024x1024",
                            prompt,
                            cancellationToken);
                        if (generated.Bytes == null || generated.Bytes.Length == 0)
                        {
                            continue;
                        }

                        var safeTopic = SafeFileNameToken(topic.TopicCode);
                        var generatedName = $"{safeTopic}_guide_{i + 1:00}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                        var fullPath = GetUniquePath(outputDir, generatedName);
                        await System.IO.File.WriteAllBytesAsync(fullPath, generated.Bytes, cancellationToken);

                        combined.Add(new GuideIllustration
                        {
                            ImagePath = fullPath,
                            Caption = $"AI generated learning illustration for {(topic.TopicDescription ?? topic.TopicCode)}",
                            Source = "generated",
                            Score = 120
                        });
                    }
                }

                if (combined.Count > 0)
                {
                    result[topic.Id] = combined
                        .Where(x => !string.IsNullOrWhiteSpace(x.ImagePath) && System.IO.File.Exists(x.ImagePath))
                        .OrderByDescending(x => x.Score)
                        .ThenBy(x => x.ImagePath, StringComparer.OrdinalIgnoreCase)
                        .Take(maxPerTopic)
                        .ToList();
                }
            }

            return result;
        }

        private static List<GuideIllustration> ResolveGuideIllustrationsFromSourceMaterials(
            IReadOnlyList<SourceMaterial> materials,
            string qualificationCode,
            string subjectCode,
            string subjectDescription,
            Topic topic)
        {
            var topicCode = (topic.TopicCode ?? string.Empty).Trim();
            var topicDescription = (topic.TopicDescription ?? string.Empty).Trim();

            var keywordTokens = Regex.Matches(
                    $"{topicCode} {topicDescription} {subjectCode} {subjectDescription}".ToLowerInvariant(),
                    @"[a-z0-9]{3,}")
                .Select(m => m.Value)
                .Distinct(StringComparer.Ordinal)
                .Take(14)
                .ToList();

            var picked = new List<GuideIllustration>();
            foreach (var row in materials ?? Array.Empty<SourceMaterial>())
            {
                var imagePath = ResolveSourceMaterialImagePath(row);
                if (string.IsNullOrWhiteSpace(imagePath) || !System.IO.File.Exists(imagePath)) continue;

                var rowQualificationCode = (row.QualificationCode ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(qualificationCode) &&
                    !string.IsNullOrWhiteSpace(rowQualificationCode) &&
                    !string.Equals(rowQualificationCode, qualificationCode, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var score = 0;
                if (ContainsLoose((row.TopicDescription ?? string.Empty), topicDescription)) score += 120;
                if (ContainsLoose((row.TopicDescription ?? string.Empty), topicCode)) score += 90;
                if (ContainsLoose((row.AssessmentCriteriaDescription ?? string.Empty), topicDescription)) score += 40;
                if (ContainsLoose((row.SubjectDescription ?? string.Empty), subjectDescription)) score += 20;
                if (ContainsLoose((row.SubjectDescription ?? string.Empty), subjectCode)) score += 16;

                var searchable = $"{row.Title} {row.FileName} {row.ExtractedText}".ToLowerInvariant();
                score += keywordTokens.Count(token => searchable.Contains(token, StringComparison.Ordinal)) * 4;
                if (score < 18) continue;

                var caption = (row.Title ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(caption))
                {
                    caption = (row.TopicDescription ?? string.Empty).Trim();
                }
                if (string.IsNullOrWhiteSpace(caption))
                {
                    caption = Path.GetFileNameWithoutExtension(imagePath);
                }

                picked.Add(new GuideIllustration
                {
                    ImagePath = imagePath,
                    Caption = caption,
                    Source = "source_material",
                    Score = score
                });
            }

            return picked
                .GroupBy(x => x.ImagePath.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.Score).First())
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.ImagePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool ContainsLoose(string haystack, string needle)
        {
            var h = (haystack ?? string.Empty).Trim();
            var n = (needle ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(h) || string.IsNullOrWhiteSpace(n)) return false;
            return h.Contains(n, StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveSourceMaterialImagePath(SourceMaterial row)
        {
            if (row == null) return string.Empty;

            var filePath = (row.FilePath ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                if (Path.IsPathRooted(filePath) && System.IO.File.Exists(filePath) && IsImageFile(filePath))
                {
                    return filePath;
                }

                var rooted = Path.GetFullPath(filePath);
                if (System.IO.File.Exists(rooted) && IsImageFile(rooted))
                {
                    return rooted;
                }

                var viaParents = ResolveFromCurrentOrParents(filePath, 6);
                if (!string.IsNullOrWhiteSpace(viaParents) && IsImageFile(viaParents))
                {
                    return viaParents;
                }
            }

            var fileName = (row.FileName ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(fileName) && IsImageFile(fileName))
            {
                var resolvedName = ResolveFromCurrentOrParents(fileName, 6);
                if (!string.IsNullOrWhiteSpace(resolvedName))
                {
                    return resolvedName;
                }
            }

            var url = (row.Url ?? string.Empty).Trim();
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            {
                var localPath = uri.LocalPath;
                if (System.IO.File.Exists(localPath) && IsImageFile(localPath))
                {
                    return localPath;
                }
            }

            return string.Empty;
        }

        private static bool IsImageFile(string path)
        {
            var ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".webp";
        }

        private static string BuildGuideGeneratedImagePrompt(
            Qualification qualification,
            Subject subject,
            Topic topic,
            int variant)
        {
            var topicTitle = string.IsNullOrWhiteSpace(topic?.TopicDescription)
                ? "vocational training concept"
                : topic.TopicDescription.Trim();
            var topicCode = (topic?.TopicCode ?? string.Empty).Trim();
            var qualificationLabel = $"{qualification?.QualificationNumber} {qualification?.QualificationDescription}".Trim();
            var subjectLabel = $"{subject?.SubjectCode} {subject?.SubjectDescription}".Trim();

            var prompt = new StringBuilder();
            prompt.Append("Create a clean educational technical illustration for a learner guide. ");
            prompt.Append("Use textbook style, white background, high clarity labels, and realistic proportions. ");
            prompt.Append("No logos, no watermarks, no trademarks, and no brand names. ");
            prompt.Append("Avoid dense paragraphs of text in the image. ");
            prompt.Append($"Topic: {topicTitle}. ");
            if (!string.IsNullOrWhiteSpace(topicCode))
            {
                prompt.Append($"Topic code: {topicCode}. ");
            }
            if (!string.IsNullOrWhiteSpace(subjectLabel))
            {
                prompt.Append($"Subject context: {subjectLabel}. ");
            }
            if (!string.IsNullOrWhiteSpace(qualificationLabel))
            {
                prompt.Append($"Qualification context: {qualificationLabel}. ");
            }
            prompt.Append($"Variation index: {variant}. ");
            prompt.Append("Style direction: vocational training diagram that helps learners identify machine parts and working principles.");
            return prompt.ToString();
        }

        private async Task<OpenAiGeneratedGuideImageResult> TryGenerateGuideImageWithOpenAiAsync(
            string apiKey,
            string model,
            string size,
            string prompt,
            CancellationToken cancellationToken)
        {
            if (AiRuntime.AllowGoogle())
            {
                try
                {
                    var googleImage = await GoogleGeminiClient.GenerateImageAsync(prompt, cancellationToken);
                    if (googleImage != null && googleImage.Length > 0)
                    {
                        return OpenAiGeneratedGuideImageResult.Success(googleImage);
                    }
                }
                catch { }
            }
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
                    return OpenAiGeneratedGuideImageResult.Fail($"HTTP {(int)response.StatusCode}: {TrimTail(body, 320)}");
                }

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array ||
                    data.GetArrayLength() == 0)
                {
                    return OpenAiGeneratedGuideImageResult.Fail("Image response did not include any data items.");
                }

                var first = data[0];
                if (first.TryGetProperty("b64_json", out var b64Node) && b64Node.ValueKind == JsonValueKind.String)
                {
                    var b64 = (b64Node.GetString() ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(b64))
                    {
                        try
                        {
                            return OpenAiGeneratedGuideImageResult.Success(Convert.FromBase64String(b64));
                        }
                        catch
                        {
                            return OpenAiGeneratedGuideImageResult.Fail("Image base64 payload could not be decoded.");
                        }
                    }
                }

                if (first.TryGetProperty("url", out var urlNode) && urlNode.ValueKind == JsonValueKind.String)
                {
                    var url = (urlNode.GetString() ?? string.Empty).Trim();
                    if (Uri.TryCreate(url, UriKind.Absolute, out var _))
                    {
                        var bytes = await _http.GetByteArrayAsync(url, cancellationToken);
                        return OpenAiGeneratedGuideImageResult.Success(bytes);
                    }
                }

                return OpenAiGeneratedGuideImageResult.Fail("Image response did not include b64_json or url.");
            }
            catch (Exception ex)
            {
                return OpenAiGeneratedGuideImageResult.Fail(ex.Message);
            }
        }

        private static string GetUniquePath(string directory, string fileName)
        {
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            var path = Path.Combine(directory, fileName);
            var index = 1;
            while (System.IO.File.Exists(path))
            {
                path = Path.Combine(directory, $"{baseName}_{index:00}{ext}");
                index += 1;
            }
            return path;
        }

        private static string BuildTopicSummary(TopicGuideSection topic)
        {
            var contentBlocks = BuildTopicContentBlocks(topic)
                .Take(10)
                .ToList();

            if (contentBlocks.Count == 0)
            {
                return $"This topic covered {topic.TopicCode} with {topic.Criteria.Count} assessment criteria. Add lesson content to generate richer summaries.";
            }

            return BuildSummary(contentBlocks, $"{topic.TopicCode} - {topic.TopicDescription}");
        }

        private static List<GuideLessonBlock> BuildTopicLessonBlocks(TopicGuideSection topic)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var lessons = new List<GuideLessonBlock>();

            var orderedLessons = topic.Criteria
                .SelectMany(c => c.Lessons)
                .OrderBy(l => ParseLpnSort(l.Lpn))
                .ThenBy(l => NormalizeLooseText(l.LessonPlanDescription))
                .ToList();

            var preferredLessons = orderedLessons
                .Where(l => HasMeaningfulLessonContent(l.LessonContent))
                .ToList();

            foreach (var lesson in preferredLessons)
            {
                var signature = string.Join("|", new[]
                {
                    NormalizeLooseText(lesson.Lpn),
                    NormalizeLooseText(lesson.LessonPlanDescription),
                    NormalizeLooseText(lesson.LessonContent)
                });
                if (string.IsNullOrWhiteSpace(signature) || !seen.Add(signature))
                {
                    continue;
                }

                lessons.Add(lesson);
            }

            return lessons;
        }

        private static List<string> BuildTopicContentBlocks(TopicGuideSection topic)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var blocks = new List<string>();

            foreach (var block in topic.Criteria
                .SelectMany(c => c.Lessons)
                .Select(l => (l.LessonContent ?? string.Empty).Trim())
                .Where(HasMeaningfulLessonContent))
            {
                var signature = NormalizeLooseText(block);
                if (string.IsNullOrWhiteSpace(signature) || !seen.Add(signature))
                {
                    continue;
                }

                blocks.Add(block);
            }

            if (blocks.Count > 0)
            {
                return blocks;
            }

            foreach (var criteriaText in topic.Criteria
                .Select(c => (c.CriteriaDescription ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                var signature = NormalizeLooseText(criteriaText);
                if (string.IsNullOrWhiteSpace(signature) || !seen.Add(signature))
                {
                    continue;
                }

                blocks.Add(criteriaText);
            }

            return blocks;
        }

        private static string BuildLearnerGuideNarrationScript(
            Qualification qualification,
            CurriculumPhase? phase,
            Subject subject,
            IReadOnlyList<TopicGuideSection> topics)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Learner guide narration for {(subject.SubjectCode ?? string.Empty).Trim()} {(subject.SubjectDescription ?? string.Empty).Trim()}.");
            sb.AppendLine($"Qualification {(qualification.QualificationNumber ?? string.Empty).Trim()} {(qualification.QualificationDescription ?? string.Empty).Trim()}.");
            if (!string.IsNullOrWhiteSpace(phase?.Name))
            {
                sb.AppendLine($"Phase: {phase.Name.Trim()}.");
            }
            sb.AppendLine();

            var purpose = (subject.SubjectPurpose ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(purpose))
            {
                sb.AppendLine("Subject purpose.");
                sb.AppendLine(purpose);
                sb.AppendLine();
            }

            foreach (var topic in topics ?? Array.Empty<TopicGuideSection>())
            {
                var topicTitle = $"{(topic.TopicCode ?? string.Empty).Trim()} {(topic.TopicDescription ?? string.Empty).Trim()}".Trim();
                if (string.IsNullOrWhiteSpace(topicTitle))
                {
                    topicTitle = "Topic";
                }

                sb.AppendLine($"Topic: {topicTitle}.");
                if (!string.IsNullOrWhiteSpace(topic.TopicPurpose))
                {
                    sb.AppendLine($"Purpose: {topic.TopicPurpose.Trim()}");
                }

                var topicSummary = BuildTopicSummary(topic);
                if (!string.IsNullOrWhiteSpace(topicSummary))
                {
                    sb.AppendLine($"Summary: {topicSummary}");
                }

                foreach (var criterion in topic.Criteria ?? new List<CriteriaGuideSection>())
                {
                    var criterionText = (criterion.CriteriaDescription ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(criterionText))
                    {
                        sb.AppendLine($"Assessment criterion: {criterionText}");
                    }
                }

                foreach (var block in BuildTopicContentBlocks(topic))
                {
                    sb.AppendLine(block.Trim());
                }

                sb.AppendLine();
            }

            return Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();
        }

        private static List<string> SplitNarrationIntoChunks(string script, int maxCharsPerChunk, int maxChunks)
        {
            var paragraphs = Regex.Split((script ?? string.Empty).Trim(), @"\r?\n\r?\n")
                .Select(p => Regex.Replace((p ?? string.Empty).Trim(), @"\s+", " "))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            var chunks = new List<string>();
            if (paragraphs.Count == 0) return chunks;
            var maxChars = Math.Clamp(maxCharsPerChunk, 1000, 3900);
            var hardMaxChunks = Math.Clamp(maxChunks, 1, 60);
            var current = new StringBuilder();

            foreach (var paragraph in paragraphs)
            {
                if (chunks.Count >= hardMaxChunks) break;

                if (paragraph.Length > maxChars)
                {
                    var sentences = Regex.Split(paragraph, @"(?<=[.!?])\s+")
                        .Select(s => s.Trim())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();
                    foreach (var sentence in sentences)
                    {
                        if (chunks.Count >= hardMaxChunks) break;
                        if (current.Length > 0 && current.Length + sentence.Length + 1 > maxChars)
                        {
                            chunks.Add(current.ToString().Trim());
                            current.Clear();
                            if (chunks.Count >= hardMaxChunks) break;
                        }

                        if (sentence.Length > maxChars)
                        {
                            var trimmed = sentence[..Math.Min(maxChars, sentence.Length)].Trim();
                            if (!string.IsNullOrWhiteSpace(trimmed))
                            {
                                chunks.Add(trimmed);
                            }
                            continue;
                        }

                        if (current.Length > 0) current.Append(' ');
                        current.Append(sentence);
                    }
                    continue;
                }

                if (current.Length > 0 && current.Length + paragraph.Length + 2 > maxChars)
                {
                    chunks.Add(current.ToString().Trim());
                    current.Clear();
                    if (chunks.Count >= hardMaxChunks) break;
                }

                if (current.Length > 0) current.Append("\n\n");
                current.Append(paragraph);
            }

            if (current.Length > 0 && chunks.Count < hardMaxChunks)
            {
                chunks.Add(current.ToString().Trim());
            }

            return chunks
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .ToList();
        }

        private async Task<OpenAiTtsResult> TrySynthesizeOpenAiTtsChunkAsync(
            string apiKey,
            string model,
            string voice,
            string format,
            double speed,
            string text,
            CancellationToken cancellationToken)
        {
            var payload = new
            {
                model,
                voice,
                input = text,
                format,
                speed
            };

            using var msg = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            try
            {
                using var response = await _http.SendAsync(msg, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    return OpenAiTtsResult.Fail($"HTTP {(int)response.StatusCode}: {TrimTail(body, 320)}");
                }

                var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (audio.Length == 0)
                {
                    return OpenAiTtsResult.Fail("OpenAI returned empty audio output.");
                }

                return OpenAiTtsResult.Success(audio);
            }
            catch (Exception ex)
            {
                return OpenAiTtsResult.Fail(ex.Message);
            }
        }

        private static string NormalizeTtsFormat(string? raw)
        {
            var normalized = (raw ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "aac" => "aac",
                "wav" => "wav",
                "flac" => "flac",
                "opus" => "opus",
                _ => "mp3"
            };
        }

        private static string TrimTail(string? input, int maxChars)
        {
            var s = (input ?? string.Empty).Trim();
            if (s.Length <= maxChars) return s;
            return s.Substring(s.Length - maxChars);
        }

        private static string NormalizeDocumentText(string? value)
        {
            var normalized = (value ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Trim();
            return SanitizeXmlText(normalized);
        }

        private static string SanitizeXmlText(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var input = value!;
            var sb = new StringBuilder(input.Length);
            for (var i = 0; i < input.Length; i++)
            {
                var ch = input[i];
                if (XmlConvert.IsXmlChar(ch))
                {
                    sb.Append(ch);
                    continue;
                }

                if (char.IsHighSurrogate(ch) &&
                    i + 1 < input.Length &&
                    char.IsLowSurrogate(input[i + 1]) &&
                    XmlConvert.IsXmlSurrogatePair(ch, input[i + 1]))
                {
                    sb.Append(ch);
                    sb.Append(input[i + 1]);
                    i++;
                }
                // Drop invalid XML characters.
            }
            return sb.ToString();
        }

        private static int ParseLpnSort(string? value)
        {
            var raw = (value ?? string.Empty).Trim();
            if (int.TryParse(raw, out var direct)) return direct;
            var digits = new string(raw.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var parsed) ? parsed : int.MaxValue - 1;
        }

        private static string NormalizeLpn(string? value)
        {
            var raw = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw)) return "LPN 1";
            return raw.StartsWith("LPN", StringComparison.OrdinalIgnoreCase) ? raw.ToUpperInvariant() : $"LPN {raw}";
        }

        private static string NormalizeLooseText(string? value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", " ");
        }

        private static string NormalizeCodeKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var chars = value
                .Trim()
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray();
            return new string(chars);
        }

        private static bool HasMeaningfulLessonContent(string? value)
        {
            var raw = NormalizeLessonContentForGuide(value);
            return raw.Length > 0 && !IsPlaceholderLessonContent(raw);
        }

        private static string NormalizeToolkitLessonContentForGuide(LecturerToolkitEntry? row)
        {
            return NormalizeLessonContentForGuide(row?.LessonPlanContent, row?.LearningAids);
        }

        private static string NormalizeLessonContentForGuide(string? lessonPlanContent, string? learningAids = null)
        {
            var content = (lessonPlanContent ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            var lines = content
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.None);

            var headings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "overview",
                "core technical understanding",
                "detailed technical content",
                "procedure and application",
                "safety and quality checks",
                "common faults / errors",
                "summary"
            };

            var boilerplatePrefixes = new[]
            {
                "this lesson develops the learner's ability to",
                "key emphasis areas for",
                "show learners how to apply",
                "emphasise the specific safety",
                "emphasise the safe method of work",
                "highlight the common mistakes learners may make",
                "conclude the lesson by checking that learners can explain and apply"
            };

            var kept = new List<string>();
            foreach (var rawLine in lines)
            {
                var line = NormalizeDocumentText(rawLine);
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                line = TrimLeadingGuideNoiseSentences(line);
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var comparison = NormalizeLooseText(line);
                if (headings.Contains(comparison))
                {
                    continue;
                }

                if (LooksLikeLessonCodeLabel(line))
                {
                    continue;
                }

                if (boilerplatePrefixes.Any(prefix => comparison.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (LooksLikeLessonInstructionDirective(line) || LooksLikeLessonReferenceNoise(line))
                {
                    continue;
                }

                if (LooksLikeAssessmentCriteriaRestatement(line))
                {
                    continue;
                }

                line = StripCurriculumCodesFromText(line);
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                kept.Add(line);
            }

            return string.Join("\n", kept).Trim();
        }

        private static bool IsPlaceholderLessonContent(string? value)
        {
            var raw = NormalizeLooseText(value);
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return raw.Equals("[done]", StringComparison.Ordinal)
                || raw.Equals(NormalizeLooseText(AutoCurriculumCoverageGapMarker), StringComparison.Ordinal)
                || raw.Equals("done", StringComparison.Ordinal)
                || raw.Equals("[todo]", StringComparison.Ordinal)
                || raw.Equals("todo", StringComparison.Ordinal)
                || raw.Equals("n/a", StringComparison.Ordinal)
                || raw.Equals("na", StringComparison.Ordinal)
                || raw.Equals("tbc", StringComparison.Ordinal);
        }

        private static bool LooksGenericLessonDescription(string? value)
        {
            var raw = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw)) return true;
            return raw.Equals("Lesson activities", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("Lesson activities for ", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveGuideLessonDescription(
            string? sourceLessonDescription,
            IEnumerable<LessonPlan>? fallbackPlans,
            string? fallbackCriteriaDescription,
            string? fallbackTopicDescription)
        {
            var lessonDescription = NormalizeGuideLessonDescription(sourceLessonDescription);
            if (LooksGenericLessonDescription(lessonDescription))
            {
                lessonDescription = string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(lessonDescription))
            {
                return lessonDescription;
            }

            var titleFallback = (fallbackPlans ?? Enumerable.Empty<LessonPlan>())
                .Select(x => NormalizeGuideLessonDescription(x.Title))
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && !LooksGenericLessonDescription(x));
            if (string.IsNullOrWhiteSpace(titleFallback))
            {
                titleFallback = (fallbackPlans ?? Enumerable.Empty<LessonPlan>())
                    .Select(x => NormalizeGuideLessonDescription(x.Title))
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            }
            if (!string.IsNullOrWhiteSpace(titleFallback))
            {
                return titleFallback;
            }

            var criteriaDescription = StripCurriculumCodesFromText(fallbackCriteriaDescription);
            if (!string.IsNullOrWhiteSpace(criteriaDescription))
            {
                return criteriaDescription;
            }

            var topicDescription = StripCurriculumCodesFromText(fallbackTopicDescription);
            if (!string.IsNullOrWhiteSpace(topicDescription))
            {
                return topicDescription;
            }

            return "Lesson content";
        }

        private static int ScoreToolkitRowForGuide(LecturerToolkitEntry row)
        {
            var score = 0;
            if (HasMeaningfulLessonContent(NormalizeToolkitLessonContentForGuide(row))) score += 100;
            if (!LooksGenericLessonDescription(NormalizeGuideLessonDescription(row.LessonPlanDescription))) score += 10;
            return score;
        }

        private static bool HasSubjectIdentity(Subject subject)
        {
            return !string.IsNullOrWhiteSpace((subject.SubjectCode ?? string.Empty).Trim()) ||
                   !string.IsNullOrWhiteSpace((subject.SubjectDescription ?? string.Empty).Trim());
        }

        private static bool ToolkitMatchesSubjectCode(LecturerToolkitEntry row, Subject subject)
        {
            var rowSubjectCode = NormalizeCodeKey(row.SubjectCode);
            var subjectCode = NormalizeCodeKey(subject.SubjectCode);
            var rowSubjectDescription = NormalizeLooseText(row.SubjectDescription);
            var subjectDescription = NormalizeLooseText(subject.SubjectDescription);
            if (!string.IsNullOrWhiteSpace(rowSubjectCode) &&
                !string.IsNullOrWhiteSpace(subjectCode) &&
                string.Equals(rowSubjectCode, subjectCode, StringComparison.Ordinal))
            {
                return string.IsNullOrWhiteSpace(rowSubjectDescription) ||
                       string.IsNullOrWhiteSpace(subjectDescription) ||
                       string.Equals(rowSubjectDescription, subjectDescription, StringComparison.Ordinal);
            }

            if (!string.IsNullOrWhiteSpace(rowSubjectDescription) &&
                !string.IsNullOrWhiteSpace(subjectDescription) &&
                string.Equals(rowSubjectDescription, subjectDescription, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private static string BuildTopicIdentity(Topic topic)
        {
            return string.Join("|", new[]
            {
                NormalizeLooseText(topic.TopicCode),
                NormalizeLooseText(topic.TopicDescription)
            });
        }

        private static string BuildCriterionIdentity(
            AssessmentCriteria criterion,
            IReadOnlyDictionary<int, Topic> topicsById)
        {
            if (topicsById.TryGetValue(criterion.TopicId, out var topic))
            {
                return $"{BuildTopicIdentity(topic)}|{NormalizeLooseText(criterion.Description)}";
            }

            return $"topic:{criterion.TopicId}|{NormalizeLooseText(criterion.Description)}";
        }

        private static bool HasGuideSourceContent(LecturerToolkitEntry? row)
        {
            if (row == null) return false;
            if (IsAutoCurriculumCoverageGapRow(row)) return false;
            return HasMeaningfulLessonContent(NormalizeToolkitLessonContentForGuide(row)) ||
                   (!LooksGenericLessonDescription(row.LessonPlanDescription) &&
                    !string.IsNullOrWhiteSpace((row.LessonPlanDescription ?? string.Empty).Trim()));
        }

        private static bool HasGuideSourceContent(LessonPlan? row)
        {
            if (row == null) return false;
            return HasMeaningfulLessonContent(row.Content) ||
                   !string.IsNullOrWhiteSpace((row.Title ?? string.Empty).Trim());
        }

        private static string DescribeUnmappedToolkitRow(
            LecturerToolkitEntry row,
            IReadOnlyDictionary<int, AssessmentCriteria> criteriaById)
        {
            var criteriaId = row.AssessmentCriteriaId.GetValueOrDefault();
            if (criteriaId > 0)
            {
                if (!criteriaById.ContainsKey(criteriaId))
                {
                    return "AssessmentCriteriaId is not part of the selected subject/topics.";
                }

                return "AssessmentCriteriaId exists but row did not map. Verify criteria text and subject selection.";
            }

            var criteriaDescription = (row.AssessmentCriteriaDescription ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(criteriaDescription))
            {
                return "Missing AssessmentCriteriaId and AssessmentCriteriaDescription.";
            }

            return "Criteria description did not match selected subject criteria.";
        }

        private static List<LecturerToolkitEntry> ResolveToolkitRowsForCriterion(
            IEnumerable<LecturerToolkitEntry> toolkitRows,
            AssessmentCriteria criterion,
            Subject subject)
        {
            var allRows = (toolkitRows ?? Enumerable.Empty<LecturerToolkitEntry>())
                .Where(row => ToolkitMatchesSubjectCode(row, subject))
                .Where(row => !IsAutoCurriculumCoverageGapRow(row))
                .ToList();

            var matches = new List<LecturerToolkitEntry>();
            var byId = allRows
                .Where(row => row.AssessmentCriteriaId.HasValue && row.AssessmentCriteriaId.Value == criterion.Id)
                .ToList();
            if (byId.Count > 0)
            {
                matches.AddRange(byId);
            }

            var criterionDescriptionNorm = NormalizeLooseText(criterion.Description);
            if (!string.IsNullOrWhiteSpace(criterionDescriptionNorm))
            {
                var byDescription = allRows
                    .Where(row =>
                        string.Equals(
                            NormalizeLooseText(row.AssessmentCriteriaDescription),
                            criterionDescriptionNorm,
                            StringComparison.Ordinal))
                    .ToList();
                if (byDescription.Count > 0)
                {
                    foreach (var row in byDescription)
                    {
                        if (matches.Any(existing => existing.Id == row.Id)) continue;
                        matches.Add(row);
                    }
                }
                else
                {
                    // Try a looser contains-based match when exact normalized description equality fails.
                    var byContains = allRows
                        .Where(row =>
                            !string.IsNullOrWhiteSpace(NormalizeLooseText(row.AssessmentCriteriaDescription)) &&
                            (NormalizeLooseText(row.AssessmentCriteriaDescription).Contains(criterionDescriptionNorm, StringComparison.Ordinal) ||
                             criterionDescriptionNorm.Contains(NormalizeLooseText(row.AssessmentCriteriaDescription), StringComparison.Ordinal)))
                        .ToList();
                    if (byContains.Count > 0)
                    {
                        foreach (var row in byContains)
                        {
                            if (matches.Any(existing => existing.Id == row.Id)) continue;
                            matches.Add(row);
                        }
                    }
                }
            }

            return matches
                .OrderByDescending(ScoreToolkitRowForGuide)
                .ThenBy(row => ParseLpnSort(row.Lpn))
                .ThenBy(row => row.Id)
                .ToList();
        }

        private static bool ContainsAutoCurriculumCoverageGapMarker(string? value)
        {
            return (value ?? string.Empty).IndexOf(AutoCurriculumCoverageGapMarker, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsAutoCurriculumCoverageGapRow(LecturerToolkitEntry? row)
        {
            if (row == null) return false;
            return ContainsAutoCurriculumCoverageGapMarker(row.LessonPlanContent) ||
                   ContainsAutoCurriculumCoverageGapMarker(row.LearningAids);
        }

        private static List<LecturerToolkitEntry> ResolveToolkitRowsForCriteria(
            IEnumerable<LecturerToolkitEntry> toolkitRows,
            IEnumerable<AssessmentCriteria> criteria,
            Subject subject)
        {
            var merged = new Dictionary<int, LecturerToolkitEntry>();
            foreach (var criterion in criteria ?? Enumerable.Empty<AssessmentCriteria>())
            {
                foreach (var row in ResolveToolkitRowsForCriterion(toolkitRows, criterion, subject))
                {
                    if (!merged.ContainsKey(row.Id))
                    {
                        merged[row.Id] = row;
                    }
                }
            }

            return merged.Values
                .OrderBy(row => ParseLpnSort(row.Lpn))
                .ThenBy(row => row.Id)
                .ToList();
        }

        private static List<LessonPlan> ResolveLessonPlansForCriteria(
            IReadOnlyDictionary<int, List<LessonPlan>> lessonByCriteria,
            IEnumerable<AssessmentCriteria> criteria)
        {
            var merged = new Dictionary<int, LessonPlan>();
            foreach (var criterion in criteria ?? Enumerable.Empty<AssessmentCriteria>())
            {
                if (!lessonByCriteria.TryGetValue(criterion.Id, out var rows) || rows == null) continue;
                foreach (var row in rows)
                {
                    if (!merged.ContainsKey(row.Id))
                    {
                        merged[row.Id] = row;
                    }
                }
            }

            return merged.Values
                .OrderBy(row => row.SortOrder)
                .ThenBy(row => row.Id)
                .ToList();
        }

        private static List<UploadedLessonPlanSourceRow> LoadUploadedLessonPlanRowsForSubject(
            Qualification qualification,
            Subject subject)
        {
            var sourcePath = ResolveUploadedLessonPlanSourcePath();
            if (string.IsNullOrWhiteSpace(sourcePath) || !System.IO.File.Exists(sourcePath))
            {
                return new List<UploadedLessonPlanSourceRow>();
            }

            var sourceMetadata = LoadUploadedLessonPlanSourceMetadata(sourcePath);
            if (UploadedLessonPlanSourceHasScope(sourceMetadata) &&
                !UploadedLessonPlanSourceMatchesQualification(sourceMetadata, qualification))
            {
                return new List<UploadedLessonPlanSourceRow>();
            }

            List<string[]> rows;
            try
            {
                rows = ReadUploadedLessonPlanRows(sourcePath);
            }
            catch
            {
                return new List<UploadedLessonPlanSourceRow>();
            }

            if (rows.Count <= 1)
            {
                return new List<UploadedLessonPlanSourceRow>();
            }

            var header = rows[0] ?? Array.Empty<string>();
            var cQualificationsId = FindLessonPlanColumnIndex(header, "QualificationsId", "QualificationsID");
            var cQualificationCode = FindLessonPlanColumnIndex(header, "Qualification Code", "Qualification Number", "QualificationNo", "Qualification No", "Qaulification Code");
            var cSubjectCode = FindLessonPlanColumnIndex(header, "Subject Code", "SubjectCode");
            var cSubjectDescription = FindLessonPlanColumnIndex(header, "Subject Description", "Subject Decription", "SubjectDescription");
            var cTopicCode = FindLessonPlanColumnIndex(header, "Topic Code", "TopicCode");
            var cTopicDescription = FindLessonPlanColumnIndex(header, "Topic Description", "TopicDescription");
            var cCriteriaDescription = FindLessonPlanColumnIndex(header, "Assesment Criteria Description", "Assessment Criteria Description", "AssessmentCriteriaDescription");
            var cLpn = FindLessonPlanColumnIndex(header, "LPN", "Lesson Plan Number (LPN)");
            var cLessonDescription = FindLessonPlanColumnIndex(header, "Lesson Plan Description", "Lesson Plan Description ", "LessonPlanDescription", "Description");
            var cLessonContent = FindLessonPlanColumnIndex(header, "Lesson Plan Content", "LessonPlanContent");

            var wantedSubjectCode = NormalizeCodeKey(subject.SubjectCode);
            var wantedSubjectDescription = NormalizeLooseText(subject.SubjectDescription);
            var allowMetadataScopedRows = UploadedLessonPlanSourceHasScope(sourceMetadata);
            var loaded = new List<UploadedLessonPlanSourceRow>();
            var sourceOrder = 0;
            var lastQualificationId = 0;
            var lastQualificationCode = string.Empty;
            var lastSubjectCode = string.Empty;
            var lastSubjectDescription = string.Empty;

            foreach (var row in rows.Skip(1))
            {
                if (row == null || row.Length == 0) continue;

                var lessonContent = LessonPlanCell(row, cLessonContent);
                var lessonDescription = LessonPlanCell(row, cLessonDescription);
                if (!HasMeaningfulLessonContent(lessonContent) && string.IsNullOrWhiteSpace(lessonDescription))
                {
                    continue;
                }

                var rowQualificationId = ParsePositiveInt(LessonPlanCell(row, cQualificationsId));
                var rowQualificationCode = LessonPlanCell(row, cQualificationCode);
                var rowSubjectCode = LessonPlanCell(row, cSubjectCode);
                var rowSubjectDescription = LessonPlanCell(row, cSubjectDescription);

                if ((rowQualificationId.HasValue || !string.IsNullOrWhiteSpace(rowQualificationCode) ||
                     !string.IsNullOrWhiteSpace(rowSubjectCode) || !string.IsNullOrWhiteSpace(rowSubjectDescription)) &&
                    (HasMeaningfulLessonContent(lessonContent) || !string.IsNullOrWhiteSpace(lessonDescription)))
                {
                    if (rowQualificationId.HasValue && rowQualificationId.Value > 0)
                    {
                        lastQualificationId = rowQualificationId.Value;
                    }
                    if (!string.IsNullOrWhiteSpace(rowQualificationCode))
                    {
                        lastQualificationCode = rowQualificationCode;
                    }
                    if (!string.IsNullOrWhiteSpace(rowSubjectCode))
                    {
                        lastSubjectCode = rowSubjectCode;
                    }
                    if (!string.IsNullOrWhiteSpace(rowSubjectDescription))
                    {
                        lastSubjectDescription = rowSubjectDescription;
                    }
                }

                if (!rowQualificationId.HasValue && lastQualificationId > 0)
                {
                    rowQualificationId = lastQualificationId;
                }
                if (string.IsNullOrWhiteSpace(rowQualificationCode) && !string.IsNullOrWhiteSpace(lastQualificationCode))
                {
                    rowQualificationCode = lastQualificationCode;
                }
                if (string.IsNullOrWhiteSpace(rowSubjectCode) && !string.IsNullOrWhiteSpace(lastSubjectCode))
                {
                    rowSubjectCode = lastSubjectCode;
                }
                if (string.IsNullOrWhiteSpace(rowSubjectDescription) && !string.IsNullOrWhiteSpace(lastSubjectDescription))
                {
                    rowSubjectDescription = lastSubjectDescription;
                }

                if (!UploadedLessonPlanRowMatchesQualification(
                        rowQualificationId,
                        rowQualificationCode,
                        qualification,
                        allowMetadataScopedRows))
                {
                    continue;
                }

                if (!UploadedLessonPlanRowMatchesSubject(
                        rowSubjectCode,
                        rowSubjectDescription,
                        wantedSubjectCode,
                        wantedSubjectDescription))
                {
                    continue;
                }

                loaded.Add(new UploadedLessonPlanSourceRow
                {
                    QualificationId = rowQualificationId,
                    QualificationCode = rowQualificationCode,
                    SubjectCode = rowSubjectCode,
                    SubjectDescription = rowSubjectDescription,
                    TopicCode = LessonPlanCell(row, cTopicCode),
                    TopicDescription = LessonPlanCell(row, cTopicDescription),
                    CriteriaDescription = LessonPlanCell(row, cCriteriaDescription),
                    Lpn = LessonPlanCell(row, cLpn),
                    LessonPlanDescription = lessonDescription,
                    LessonPlanContent = lessonContent,
                    SourceOrder = sourceOrder++
                });
            }

            return loaded;
        }

        private static List<UploadedLessonPlanSourceRow> ResolveUploadedLessonPlanRowsForCriteria(
            IReadOnlyList<UploadedLessonPlanSourceRow> sourceRows,
            Subject subject,
            Topic topic,
            IEnumerable<AssessmentCriteria> criteria)
        {
            var candidates = (sourceRows ?? Array.Empty<UploadedLessonPlanSourceRow>())
                .Where(row => UploadedLessonPlanRowMatchesSubject(row, subject))
                .Where(row => UploadedLessonPlanRowMatchesTopic(row, topic))
                .ToList();
            if (candidates.Count == 0)
            {
                return new List<UploadedLessonPlanSourceRow>();
            }

            var criteriaDescriptions = (criteria ?? Enumerable.Empty<AssessmentCriteria>())
                .Select(c => NormalizeLooseText(c.Description))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var criteriaMatches = criteriaDescriptions.Count == 0
                ? candidates
                : candidates
                    .Where(row => criteriaDescriptions.Any(criteriaDescription =>
                        UploadedLessonPlanRowMatchesCriteria(row, criteriaDescription)))
                    .ToList();

            var selected = criteriaDescriptions.Count == 0 ? candidates : criteriaMatches;
            if (selected.Count == 0)
            {
                return new List<UploadedLessonPlanSourceRow>();
            }
            var deduped = new List<UploadedLessonPlanSourceRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in selected
                .OrderBy(x => ParseLpnSort(x.Lpn))
                .ThenBy(x => x.SourceOrder))
            {
                var signature = string.Join("|", new[]
                {
                    NormalizeLooseText(row.TopicCode),
                    NormalizeLooseText(row.CriteriaDescription),
                    NormalizeLooseText(row.Lpn),
                    NormalizeLooseText(row.LessonPlanDescription),
                    NormalizeLooseText(row.LessonPlanContent)
                });
                if (string.IsNullOrWhiteSpace(signature) || !seen.Add(signature))
                {
                    continue;
                }

                deduped.Add(row);
            }

            return deduped;
        }

        private static bool UploadedLessonPlanRowMatchesSubject(UploadedLessonPlanSourceRow row, Subject subject)
        {
            return UploadedLessonPlanRowMatchesSubject(
                row.SubjectCode,
                row.SubjectDescription,
                NormalizeCodeKey(subject.SubjectCode),
                NormalizeLooseText(subject.SubjectDescription));
        }

        private static bool UploadedLessonPlanRowMatchesSubject(
            string? rowSubjectCode,
            string? rowSubjectDescription,
            string wantedSubjectCode,
            string wantedSubjectDescription)
        {
            var normalizedRowSubjectCode = NormalizeCodeKey(rowSubjectCode);
            if (!string.IsNullOrWhiteSpace(normalizedRowSubjectCode) &&
                !string.IsNullOrWhiteSpace(wantedSubjectCode) &&
                string.Equals(normalizedRowSubjectCode, wantedSubjectCode, StringComparison.Ordinal))
            {
                return true;
            }

            var normalizedRowSubjectDescription = NormalizeLooseText(rowSubjectDescription);
            if (!string.IsNullOrWhiteSpace(normalizedRowSubjectDescription) &&
                !string.IsNullOrWhiteSpace(wantedSubjectDescription) &&
                string.Equals(normalizedRowSubjectDescription, wantedSubjectDescription, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private static bool UploadedLessonPlanRowMatchesQualification(
            int? rowQualificationId,
            string? rowQualificationCode,
            Qualification qualification,
            bool allowMetadataScopedRows)
        {
            if (rowQualificationId.HasValue && rowQualificationId.Value > 0)
            {
                return rowQualificationId.Value == qualification.Id;
            }

            var normalizedRowQualificationCode = NormalizeLooseText(rowQualificationCode);
            var normalizedWantedQualificationCode = NormalizeLooseText(qualification.QualificationNumber);
            if (!string.IsNullOrWhiteSpace(normalizedRowQualificationCode) &&
                !string.IsNullOrWhiteSpace(normalizedWantedQualificationCode))
            {
                return string.Equals(normalizedRowQualificationCode, normalizedWantedQualificationCode, StringComparison.Ordinal);
            }

            return allowMetadataScopedRows;
        }

        private static bool UploadedLessonPlanRowMatchesTopic(UploadedLessonPlanSourceRow row, Topic topic)
        {
            var rowTopicCode = NormalizeCodeKey(row.TopicCode);
            var topicCode = NormalizeCodeKey(topic.TopicCode);
            if (!string.IsNullOrWhiteSpace(rowTopicCode) &&
                !string.IsNullOrWhiteSpace(topicCode) &&
                string.Equals(rowTopicCode, topicCode, StringComparison.Ordinal))
            {
                return true;
            }

            var rowTopicDescription = NormalizeLooseText(row.TopicDescription);
            var topicDescription = NormalizeLooseText(topic.TopicDescription);
            if (!string.IsNullOrWhiteSpace(rowTopicDescription) &&
                !string.IsNullOrWhiteSpace(topicDescription) &&
                string.Equals(rowTopicDescription, topicDescription, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private static bool UploadedLessonPlanRowMatchesCriteria(UploadedLessonPlanSourceRow row, string criteriaDescription)
        {
            var rowCriteriaDescription = NormalizeLooseText(row.CriteriaDescription);
            if (string.IsNullOrWhiteSpace(rowCriteriaDescription) || string.IsNullOrWhiteSpace(criteriaDescription))
            {
                return false;
            }

            return string.Equals(rowCriteriaDescription, criteriaDescription, StringComparison.Ordinal) ||
                   rowCriteriaDescription.Contains(criteriaDescription, StringComparison.Ordinal) ||
                   criteriaDescription.Contains(rowCriteriaDescription, StringComparison.Ordinal);
        }

        private static int? ParsePositiveInt(string? value)
        {
            if (int.TryParse((value ?? string.Empty).Trim(), out var parsed) && parsed > 0)
            {
                return parsed;
            }

            return null;
        }

        private static bool UploadedLessonPlanSourceHasScope(UploadedLessonPlanSourceMetadata? metadata)
        {
            return metadata != null &&
                   (metadata.QualificationId.GetValueOrDefault() > 0 ||
                    !string.IsNullOrWhiteSpace((metadata.QualificationCode ?? string.Empty).Trim()));
        }

        private static bool UploadedLessonPlanSourceMatchesQualification(
            UploadedLessonPlanSourceMetadata? metadata,
            Qualification qualification)
        {
            if (metadata == null) return false;
            var metadataQualificationId = metadata.QualificationId.GetValueOrDefault();
            if (metadataQualificationId > 0)
            {
                return metadataQualificationId == qualification.Id;
            }

            var metadataQualificationCode = NormalizeLooseText(metadata.QualificationCode);
            var qualificationCode = NormalizeLooseText(qualification.QualificationNumber);
            if (!string.IsNullOrWhiteSpace(metadataQualificationCode) &&
                !string.IsNullOrWhiteSpace(qualificationCode))
            {
                return string.Equals(metadataQualificationCode, qualificationCode, StringComparison.Ordinal);
            }

            return false;
        }

        private static UploadedLessonPlanSourceMetadata? LoadUploadedLessonPlanSourceMetadata(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return null;
            var metadataPath = $"{sourcePath}.metadata.json";
            if (!System.IO.File.Exists(metadataPath)) return null;

            try
            {
                var raw = System.IO.File.ReadAllText(metadataPath);
                return JsonSerializer.Deserialize<UploadedLessonPlanSourceMetadata>(raw, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch
            {
                return null;
            }
        }

        private static string? ResolveUploadedLessonPlanSourcePath()
        {
            var candidates = new[]
            {
                Path.Combine("Imports", "ExcelCSVTemplates", "Lesson Plan.xlsx"),
                Path.Combine("Imports", "ExcelCSVTemplates", "Lesson PLan.csv"),
                Path.Combine("Imports", "ExcelCSVTemplates", "Lesson Plan.csv"),
                Path.Combine("ETDP", "Imports", "ExcelCSVTemplates", "Lesson Plan.xlsx"),
                Path.Combine("ETDP", "Imports", "ExcelCSVTemplates", "Lesson PLan.csv"),
                Path.Combine("ETDP", "Imports", "ExcelCSVTemplates", "Lesson Plan.csv"),
                @"E:\ETDP\ETDP\Imports\ExcelCSVTemplates\Lesson Plan.xlsx",
                @"E:\ETDP\ETDP\Imports\ExcelCSVTemplates\Lesson PLan.csv",
                @"E:\ETDP\ETDP\Imports\ExcelCSVTemplates\Lesson Plan.csv",
                @"C:\ETDP\ETDP\Imports\ExcelCSVTemplates\Lesson Plan.xlsx",
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

        private static List<string[]> ReadUploadedLessonPlanRows(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".xlsx" => ReadUploadedLessonPlanRowsFromXlsx(path),
                ".csv" => ReadUploadedLessonPlanRowsFromCsv(path),
                _ => new List<string[]>()
            };
        }

        private static List<string[]> ReadUploadedLessonPlanRowsFromCsv(string path)
        {
            var headerLine = System.IO.File.ReadLines(path).FirstOrDefault() ?? string.Empty;
            var delimiter = DetectUploadedLessonPlanDelimiter(headerLine);
            try
            {
                return Csv.ReadDelimitedCsv(path, delimiter);
            }
            catch
            {
                return ReadUploadedLessonPlanRowsFromCsvWithEncoding(path, delimiter, Encoding.GetEncoding(1252));
            }
        }

        private static List<string[]> ReadUploadedLessonPlanRowsFromCsvWithEncoding(string path, char delimiter, Encoding encoding)
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
                        // Consume on \n.
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

        private static List<string[]> ReadUploadedLessonPlanRowsFromXlsx(string path)
        {
            using var document = SpreadsheetDocument.Open(path, false);
            var workbookPart = document.WorkbookPart;
            if (workbookPart?.Workbook?.Sheets == null) return new List<string[]>();

            var firstSheet = workbookPart.Workbook.Sheets.Elements<SX.Sheet>().FirstOrDefault();
            if (firstSheet?.Id == null) return new List<string[]>();

            var worksheetPart = workbookPart.GetPartById(firstSheet.Id!) as WorksheetPart;
            var sheetData = worksheetPart?.Worksheet?.Elements<SX.SheetData>().FirstOrDefault();
            if (sheetData == null) return new List<string[]>();

            var captured = new List<(int RowIndex, Dictionary<int, string> Cells)>();
            var maxColumn = -1;

            foreach (var row in sheetData.Elements<SX.Row>())
            {
                var map = new Dictionary<int, string>();
                foreach (var cell in row.Elements<SX.Cell>())
                {
                    var columnIndex = UploadedLessonPlanColumnIndexFromReference(cell.CellReference?.Value);
                    if (columnIndex < 0) continue;
                    map[columnIndex] = UploadedLessonPlanGetCellText(cell, workbookPart);
                    if (columnIndex > maxColumn) maxColumn = columnIndex;
                }

                captured.Add(((int)(row.RowIndex?.Value ?? 0), map));
            }

            if (maxColumn < 0) return new List<string[]>();

            var rows = new List<string[]>();
            foreach (var row in captured.OrderBy(x => x.RowIndex))
            {
                var values = Enumerable.Repeat(string.Empty, maxColumn + 1).ToArray();
                foreach (var kv in row.Cells)
                {
                    if (kv.Key >= 0 && kv.Key < values.Length)
                    {
                        values[kv.Key] = kv.Value ?? string.Empty;
                    }
                }

                rows.Add(values);
            }

            return rows;
        }

        private static string UploadedLessonPlanGetCellText(SX.Cell cell, WorkbookPart workbookPart)
        {
            var raw = cell.CellValue?.InnerText ?? cell.InnerText ?? string.Empty;
            if (cell.DataType == null) return raw;

            return cell.DataType.Value switch
            {
                SX.CellValues.SharedString => UploadedLessonPlanReadSharedString(raw, workbookPart),
                SX.CellValues.Boolean => raw == "1" ? "TRUE" : "FALSE",
                SX.CellValues.InlineString => cell.InlineString?.Text?.Text ?? cell.InlineString?.InnerText ?? raw,
                _ => raw
            };
        }

        private static string UploadedLessonPlanReadSharedString(string raw, WorkbookPart workbookPart)
        {
            if (!int.TryParse(raw, out var index)) return raw;
            var table = workbookPart.SharedStringTablePart?.SharedStringTable;
            var item = table?.Elements<SX.SharedStringItem>().ElementAtOrDefault(index);
            return item?.InnerText ?? raw;
        }

        private static int UploadedLessonPlanColumnIndexFromReference(string? cellReference)
        {
            if (string.IsNullOrWhiteSpace(cellReference)) return -1;

            var index = 0;
            foreach (var ch in cellReference)
            {
                if (!char.IsLetter(ch)) break;
                index = index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            }

            return index > 0 ? index - 1 : -1;
        }

        private static char DetectUploadedLessonPlanDelimiter(string? headerLine)
        {
            var line = headerLine ?? string.Empty;
            var semicolons = line.Count(ch => ch == ';');
            var commas = line.Count(ch => ch == ',');
            var tabs = line.Count(ch => ch == '\t');
            if (tabs > semicolons && tabs > commas) return '\t';
            return semicolons >= commas ? ';' : ',';
        }

        private static int FindLessonPlanColumnIndex(string[] header, params string[] aliases)
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

            var normalizedAliases = aliases
                .Select(NormalizeLessonPlanColumnAlias)
                .Where(x => x.Length > 0)
                .ToHashSet(StringComparer.Ordinal);

            for (var i = 0; i < header.Length; i++)
            {
                if (normalizedAliases.Contains(NormalizeLessonPlanColumnAlias(header[i])))
                {
                    return i;
                }
            }

            return -1;
        }

        private static string NormalizeLessonPlanColumnAlias(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var chars = value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
            return new string(chars);
        }

        private static string LessonPlanCell(string[] row, int index)
        {
            if (row == null || index < 0 || index >= row.Length) return string.Empty;
            return (row[index] ?? string.Empty).Trim();
        }

        private static List<GuideLessonBlock> DeduplicateLessonBlocks(IEnumerable<GuideLessonBlock>? lessons)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var deduped = new List<GuideLessonBlock>();
            foreach (var lesson in lessons ?? Enumerable.Empty<GuideLessonBlock>())
            {
                var signature = BuildLessonBlockSignature(lesson);
                if (!seen.Add(signature)) continue;
                deduped.Add(lesson);
            }
            return deduped;
        }

        private static string BuildLessonBlockSignature(GuideLessonBlock lesson)
        {
            return string.Join("|", new[]
            {
                NormalizeLooseText(lesson.Lpn),
                NormalizeLooseText(lesson.LessonPlanDescription),
                NormalizeLooseText(lesson.LessonContent)
            });
        }

        private async Task<List<GuideLessonBlock>> ApplyEvidenceFallbackToLessonBlocksAsync(
            List<GuideLessonBlock> lessonBlocks,
            CurriculumDeliveryPilotService.TopicEvidenceItem? pilotTopic,
            string? topicCode,
            string? topicDescription,
            string? criteriaDescription,
            Dictionary<string, string> authoringCache)
        {
            var evidenceBlocks = BuildEvidenceBackedLessonBlocks(
                pilotTopic,
                topicCode,
                topicDescription,
                criteriaDescription);
            if (evidenceBlocks.Count == 0)
            {
                return lessonBlocks;
            }

            _ = authoringCache;

            if (!HasLearnerReadyLessonBlock(lessonBlocks))
            {
                return evidenceBlocks;
            }

            if (lessonBlocks.All(IsThinLessonBlock))
            {
                lessonBlocks.AddRange(evidenceBlocks.Take(2));
            }

            return lessonBlocks;
        }

        private async Task<List<GuideLessonBlock>> AuthorEvidenceBackedLessonBlocksAsync(
            List<GuideLessonBlock> evidenceBlocks,
            string? topicCode,
            string? topicDescription,
            string? criteriaDescription,
            Dictionary<string, string> authoringCache)
        {
            var authoredBlocks = new List<GuideLessonBlock>(evidenceBlocks.Count);
            foreach (var block in evidenceBlocks)
            {
                var sourceText = NormalizeDocumentText(block.LessonContent);
                var cacheKey = string.Join("|", new[]
                {
                    "instructional-author-v2",
                    NormalizeLooseText(topicCode),
                    NormalizeLooseText(topicDescription),
                    NormalizeLooseText(criteriaDescription),
                    NormalizeLooseText(sourceText)
                });

                if (!authoringCache.TryGetValue(cacheKey, out var authoredText))
                {
                    authoredText = await TryAuthorInstructionalTextbookSectionAsync(
                        topicCode,
                        topicDescription,
                        criteriaDescription,
                        sourceText);
                    if (string.IsNullOrWhiteSpace(authoredText) || LooksLikeMetaInstructionOnly(authoredText))
                    {
                        authoredText = BuildSourceMatterFallbackSection(sourceText);
                    }

                    authoringCache[cacheKey] = authoredText;
                }

                authoredBlocks.Add(new GuideLessonBlock
                {
                    Lpn = block.Lpn,
                    LessonPlanDescription = block.LessonPlanDescription,
                    LessonContent = authoredText,
                    LecturerActions = block.LecturerActions,
                    LearnerActions = block.LearnerActions,
                    LearningAids = block.LearningAids,
                    TimeStart = block.TimeStart,
                    TimeEnd = block.TimeEnd
                });
            }

            return authoredBlocks;
        }

        private static bool HasLearnerReadyLessonBlock(IEnumerable<GuideLessonBlock>? lessonBlocks)
        {
            return (lessonBlocks ?? Enumerable.Empty<GuideLessonBlock>())
                .Any(lesson => HasMeaningfulLessonContent(lesson.LessonContent) && !IsThinLessonBlock(lesson));
        }

        private static bool TopicHasMeaningfulLessonContent(IEnumerable<CriteriaGuideSection>? criteriaSections)
        {
            return (criteriaSections ?? Enumerable.Empty<CriteriaGuideSection>())
                .SelectMany(section => section.Lessons ?? new List<GuideLessonBlock>())
                .Any(lesson => HasMeaningfulLessonContent(lesson.LessonContent));
        }

        private static GuideLessonBlock BuildTopicAsLessonFallbackBlock(Topic topic)
        {
            var topicCode = (topic.TopicCode ?? string.Empty).Trim();
            var topicDescription = (topic.TopicDescription ?? string.Empty).Trim();
            var topicPurpose = (topic.TopicPurpose ?? string.Empty).Trim();
            var lessonTitle = !string.IsNullOrWhiteSpace(topicDescription)
                ? topicDescription
                : (!string.IsNullOrWhiteSpace(topicPurpose) ? topicPurpose : "Topic learning content");
            var sourceText = string.Join(". ", new[]
            {
                topicDescription,
                topicPurpose
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

            if (string.IsNullOrWhiteSpace(sourceText))
            {
                sourceText = lessonTitle;
            }

            return new GuideLessonBlock
            {
                Lpn = !string.IsNullOrWhiteSpace(topicCode) ? topicCode : "Topic",
                LessonPlanDescription = lessonTitle,
                LessonContent = BuildSourceMatterFallbackSection(sourceText),
                LecturerActions = string.Empty,
                LearnerActions = string.Empty,
                LearningAids = string.Empty,
                TimeStart = string.Empty,
                TimeEnd = string.Empty
            };
        }

        private string FindTopicTradeEvidence(int qualificationId, string qualificationName, string topicDescription, string topicCode)
        {
            try
            {
                var keywords = topicDescription
                    .Split(new[] { ' ', ',', '-', ';', ':', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 4)
                    .Take(4)
                    .ToList();

                var query = _context.SourceMaterials
                    .AsNoTracking()
                    .Where(s => !string.IsNullOrEmpty(s.ExtractedText) && s.ExtractedText.Length > 100);

                var matched = query
                    .Where(s => keywords.Any(k => s.ExtractedText.Contains(k)) || (!string.IsNullOrEmpty(s.QualificationDescription) && s.QualificationDescription.Contains(qualificationName)))
                    .OrderByDescending(s => s.ExtractedText.Length)
                    .Select(s => s.ExtractedText)
                    .Take(2)
                    .ToList();

                if (matched.Count > 0)
                {
                    var joined = string.Join("\n\n---\n\n", matched);
                    return joined.Length > 3500 ? joined.Substring(0, 3500) + "..." : joined;
                }
            }
            catch
            {
            }
            return string.Empty;
        }

        private async Task<(string Content, string ModelTag)> GenerateTopicTextbookSectionAsync(
            Qualification qualification,
            Subject subject,
            Topic topic,
            List<AssessmentCriteria> criteria,
            string evidence,
            string? requestedModel,
            CancellationToken cancellationToken)
        {
            var systemPrompt = "You are an expert South African vocational education author and trade curriculum specialist writing official TVET courseware for the Quality Council for Trades and Occupations (QCTO).";

            var sb = new StringBuilder();
            sb.AppendLine($"QUALIFICATION: {qualification.QualificationDescription} (NQF Level {qualification.NqfLevel}, {qualification.Credits} Credits)");
            sb.AppendLine($"SUBJECT / MODULE: {subject.SubjectCode} - {subject.SubjectDescription}");
            sb.AppendLine($"TOPIC: {topic.TopicCode} - {topic.TopicDescription}");
            if (!string.IsNullOrWhiteSpace(topic.TopicPurpose))
            {
                sb.AppendLine($"TOPIC PURPOSE: {topic.TopicPurpose}");
            }
            sb.AppendLine();
            sb.AppendLine("QCTO ASSESSMENT CRITERIA (Competence Benchmarks & Rubrics):");
            if (criteria.Count > 0)
            {
                for (int i = 0; i < criteria.Count; i++)
                {
                    sb.AppendLine($"  {i + 1}. {criteria[i].Description}");
                }
            }
            else
            {
                sb.AppendLine("  1. Demonstrate complete theoretical understanding and workshop practical competence for this topic.");
            }
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                sb.AppendLine("TRADE REFERENCE TEXTBOOK EVIDENCE (Extracted from uploaded materials):");
                sb.AppendLine(evidence);
                sb.AppendLine();
            }
            sb.AppendLine("PEDAGOGICAL & TEXTBOOK DESIGN INSTRUCTIONS:");
            sb.AppendLine("1. Write formal, comprehensive, learner-facing textbook content (at least 350-500 words). Do NOT write meta-instructions like 'The student must understand' or 'Refer to the textbook'.");
            sb.AppendLine("2. Theoretical Principles: Thoroughly explain all core concepts, engineering terms, scientific definitions, operating mechanisms, and formulas.");
            sb.AppendLine("3. Workshop Procedures: Detail step-by-step practical procedures, tool selection, setup sequences, measurement, and testing.");
            sb.AppendLine("4. Safety & Standards: Detail statutory OHSA (Occupational Health & Safety Act) rules, SANS standards, hazard controls, and workshop PPE.");
            sb.AppendLine("5. Assessment Competence Rubrics: Clearly explain what demonstrates competent performance versus not-yet-competent performance.");
            sb.AppendLine("6. Formative Self-Assessment: Include 3-4 realistic revision questions with model answers at the end.");
            sb.AppendLine();
            sb.AppendLine("Write the complete textbook chapter section now:");

            var userPrompt = sb.ToString();
            var targetModel = (requestedModel ?? "gemma4:26b").Trim().ToLowerInvariant();

            if (targetModel.Contains("gemma") || targetModel == "auto" || targetModel == "local")
            {
                var localText = await TryCompleteWithLocalGemmaAsync(systemPrompt, userPrompt, "gemma4:26b", cancellationToken);
                if (!string.IsNullOrWhiteSpace(localText) && localText.Trim().Length > 200)
                {
                    return (localText.Trim(), "gemma4:26b (Local)");
                }

                if (AiRuntime.AllowGoogle())
                {
                    var geminiText = await GoogleGeminiClient.GenerateContentAsync(userPrompt, "gemini-3.8-flash", systemPrompt, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(geminiText))
                    {
                        return (geminiText.Trim(), "gemini-3.8-flash (Cloud Fallback)");
                    }
                }
            }

            if (AiRuntime.AllowGoogle())
            {
                var geminiText = await GoogleGeminiClient.GenerateContentAsync(userPrompt, "gemini-3.8-flash", systemPrompt, cancellationToken);
                if (!string.IsNullOrWhiteSpace(geminiText))
                {
                    return (geminiText.Trim(), "gemini-3.8-flash (Google Cloud)");
                }
            }

            var fallback = BuildDeterministicInstructionalSection(topic.TopicCode, topic.TopicDescription, criteria.FirstOrDefault()?.Description, evidence);
            return (fallback, "deterministic_educational_scaffold");
        }

        private async Task<string?> TryCompleteWithLocalGemmaAsync(string systemPrompt, string userPrompt, string modelName, CancellationToken ct)
        {
            var endpoints = AiRuntime.GetLocalLlmEndpointCandidates();
            var payload = new
            {
                model = string.IsNullOrWhiteSpace(modelName) ? "gemma4:26b" : modelName,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.3,
                stream = false
            };
            var json = JsonSerializer.Serialize(payload);

            foreach (var ep in endpoints)
            {
                if (string.IsNullOrWhiteSpace(ep)) continue;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, ep.Trim())
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(35));
                    using var resp = await _http.SendAsync(req, cts.Token);
                    if (resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync(cts.Token);
                        var text = TryExtractChatCompletionText(body) ?? TryExtractResponseOutputText(body);
                        if (!string.IsNullOrWhiteSpace(text) && text.Trim().Length > 100)
                        {
                            return text.Trim();
                        }
                    }
                }
                catch
                {
                }
            }
            return null;
        }

        private async Task<string?> TryAuthorInstructionalTextbookSectionAsync(
            string? topicCode,
            string? topicDescription,
            string? criteriaDescription,
            string sourceText)
        {
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                return null;
            }

            var systemPrompt = BuildInstructionalAuthorPrompt();
            var userPrompt = BuildInstructionalAuthorUserPrompt(topicCode, topicDescription, criteriaDescription, sourceText);

            var preferLocalFirst = AiRuntime.PreferLocalFirst();
            if (preferLocalFirst)
            {
                var localFirst = await TryCompleteLearnerGuideAuthoringWithLocalAsync(systemPrompt, userPrompt);
                if (HasAuthoredInstructionalContent(localFirst)) return NormalizeAuthoredInstructionalText(localFirst);
            }

            if (AiRuntime.AllowCloudProviders() && AiRuntime.AllowGoogle())
            {
                var gemini = await TryCompleteLearnerGuideAuthoringWithGeminiAsync(systemPrompt, userPrompt);
                if (HasAuthoredInstructionalContent(gemini)) return NormalizeAuthoredInstructionalText(gemini);
            }

            if (!preferLocalFirst)
            {
                var localFallback = await TryCompleteLearnerGuideAuthoringWithLocalAsync(systemPrompt, userPrompt);
                if (HasAuthoredInstructionalContent(localFallback)) return NormalizeAuthoredInstructionalText(localFallback);
            }

            return null;
        }

        private static string BuildInstructionalAuthorPrompt()
        {
            return string.Join("\n", new[]
            {
                "You are an expert Academic Author, Diesel Motor Mechanic technical textbook writer, and instructional designer.",
                "Your task is to transform curriculum requirements and retrieved subject-matter evidence into a comprehensive learner-guide section.",
                "Do not act as a curriculum mapper. Do not list what must be learned. Write the actual instructional text that enables the learner to learn from the guide without another source.",
                "Use the supplied source material as the knowledge base. Prefer the author's wording and sequence where it already reads like learner-guide content. Explain, connect, and expand only from that material and standard technical meaning. Do not invent unsupported specifications, legal requirements, torque values, or procedures.",
                "Never write meta-instructions such as 'learners must understand', 'explain the concept', 'study this source', or 'the topic covers'. Replace those with the explanation itself.",
                "Write in a natural learner-guide style. Do not force fixed headings or repeated templates. Let the source material and topic determine the structure, paragraph headings, examples, and sequence.",
                "If the source already contains authored instructional text, preserve it closely instead of replacing it with a generic explanation. If evidence is thin, still produce the fullest grounded explanation possible and clearly state only the specific remaining gap at the end.",
                "Return only the learner-guide content. Do not include prompts, notes to the author, markdown fences, or JSON."
            });
        }

        private static string BuildInstructionalAuthorUserPrompt(
            string? topicCode,
            string? topicDescription,
            string? criteriaDescription,
            string sourceText)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Target Topic: {NormalizeDocumentText($"{topicCode} {topicDescription}")}");
            sb.AppendLine($"Curriculum Requirement: {CleanCurriculumRequirementText(criteriaDescription)}");
            sb.AppendLine();
            sb.AppendLine("Bad output patterns to avoid:");
            sb.AppendLine("This topic explains the role and importance of engines and maintenance. Learners must describe types of establishments.");
            sb.AppendLine("Any repeated fixed template that replaces the uploaded author's actual explanation.");
            sb.AppendLine();
            sb.AppendLine("Good output pattern to follow:");
            sb.AppendLine("Use the source material as the starting text. Keep the author's concrete explanations, legal or technical sequence, examples and definitions. Only reorganise enough to align it with the target topic and remove duplication.");
            sb.AppendLine();
            sb.AppendLine("Source Material:");
            sb.AppendLine(TrimEvidenceExcerpt(sourceText, 6000));
            return sb.ToString().Trim();
        }

        private async Task<string?> TryCompleteLearnerGuideAuthoringWithLocalAsync(string systemPrompt, string userPrompt)
        {
            var apiKey = AiRuntime.GetLocalLlmApiKey();
            var timeoutSeconds = Math.Clamp(ParseInt(Environment.GetEnvironmentVariable("LEARNER_GUIDE_AUTHOR_TIMEOUT_SECONDS"), 25), 5, 180);
            foreach (var endpoint in AiRuntime.GetLocalLlmEndpointCandidates())
            {
                foreach (var model in AiRuntime.GetLocalLlmModelCandidates())
                {
                    var payload = new
                    {
                        model,
                        messages = new[]
                        {
                            new { role = "system", content = systemPrompt },
                            new { role = "user", content = userPrompt }
                        },
                        temperature = 0.25,
                        stream = false
                    };

                    using var msg = new HttpRequestMessage(HttpMethod.Post, endpoint.Trim());
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        var token = apiKey.Trim();
                        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        {
                            token = token.Substring(7).Trim();
                        }
                        if (!string.IsNullOrWhiteSpace(token))
                        {
                            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        }
                    }

                    msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                        using var resp = await _http.SendAsync(msg, timeout.Token);
                        var body = await resp.Content.ReadAsStringAsync(timeout.Token);
                        if (!resp.IsSuccessStatusCode) continue;
                        var text = TryExtractChatCompletionText(body) ?? TryExtractResponseOutputText(body);
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                    catch
                    {
                        // Try the next local endpoint/model candidate.
                    }
                }
            }

            return null;
        }

        private async Task<string?> TryCompleteLearnerGuideAuthoringWithGeminiAsync(string systemPrompt, string userPrompt)
        {
            if (!AiRuntime.AllowGoogle()) return null;
            try
            {
                var model = AiRuntime.GetGeminiModel("gemini-3.8-flash");
                var text = await GoogleGeminiClient.GenerateContentAsync(userPrompt, model, systemPrompt);
                return text?.Trim();
            }
            catch
            {
                return null;
            }
        }

        private Task<string?> TryCompleteLearnerGuideAuthoringWithOpenAiAsync(string systemPrompt, string userPrompt)
            => TryCompleteLearnerGuideAuthoringWithGeminiAsync(systemPrompt, userPrompt);

        private static bool HasAuthoredInstructionalContent(string? value)
        {
            var text = NormalizeAuthoredInstructionalText(value);
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (LooksLikeMetaInstructionOnly(text)) return false;
            return DocumentTextCleaner.WordCount(text) >= 80;
        }

        private static string NormalizeAuthoredInstructionalText(string? value)
        {
            var text = NormalizeDocumentText(value);
            text = Regex.Replace(text, @"```[a-zA-Z]*", string.Empty);
            text = text.Replace("```", string.Empty).Trim();
            return text;
        }

        private static string BuildSourceMatterFallbackSection(string sourceText)
        {
            var text = NormalizeDocumentText(sourceText);
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            return TrimEvidenceExcerpt(text, 6000);
        }

        private static bool LooksLikeMetaInstructionOnly(string? value)
        {
            var normalized = NormalizeLooseText(value);
            if (string.IsNullOrWhiteSpace(normalized)) return true;

            var metaSignals = new[]
            {
                "learners must understand",
                "learner must understand",
                "students must understand",
                "this topic covers",
                "this topic explains",
                "study this source",
                "using the provided reference material",
                "write a textbook section",
                "explain the concept",
                "should be able to"
            };

            var signalCount = metaSignals.Count(signal => normalized.Contains(signal, StringComparison.Ordinal));
            var wordCount = Regex.Matches(normalized, @"\b[\p{L}\p{N}]+\b").Count;
            return signalCount >= 2 && wordCount < 220;
        }

        private static string BuildDeterministicInstructionalSection(
            string? topicCode,
            string? topicDescription,
            string? criteriaDescription,
            string sourceText)
        {
            var topicTitle = NormalizeDocumentText($"{topicCode} {topicDescription}");
            if (string.IsNullOrWhiteSpace(topicTitle))
            {
                topicTitle = "This topic";
            }

            if (IsEngineRoleImportanceTopic(topicDescription, criteriaDescription))
            {
                return BuildEngineRoleImportanceInstructionalSection(topicTitle);
            }

            var focus = CleanCurriculumRequirementText(criteriaDescription);
            var evidence = ExtractInstructionalSentences(sourceText, 10);
            var keyTerms = ExtractKeyTermsForInstructionalSection(topicDescription, criteriaDescription, sourceText);
            var sb = new StringBuilder();

            sb.AppendLine("Concept Explanation");
            sb.AppendLine($"{topicTitle} is studied as practical working knowledge, not only as a curriculum heading. The purpose of this section is to give you the technical understanding needed to recognise the concept, explain it in your own words, and connect it to real diesel motor mechanic work.");
            if (!string.IsNullOrWhiteSpace(focus))
            {
                sb.AppendLine("The practical focus is to understand the meaning of the main concepts, how they work together, and why they matter in the workshop or workplace.");
            }

            foreach (var sentence in evidence.Take(5))
            {
                sb.AppendLine(sentence);
            }

            sb.AppendLine();
            sb.AppendLine("Key Terms");
            if (keyTerms.Count == 0)
            {
                sb.AppendLine("Technical concept: an idea, component, process, or relationship that must be understood well enough to explain and apply it.");
                sb.AppendLine("Application: the way the concept is used in real work, inspection, maintenance, diagnosis, repair, safety, or communication.");
            }
            else
            {
                foreach (var term in keyTerms.Take(6))
                {
                    sb.AppendLine($"{ToTitleCase(term)}: {BuildKeyTermDefinition(term)}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("How It Works");
            if (evidence.Count > 5)
            {
                foreach (var sentence in evidence.Skip(5).Take(5))
                {
                    sb.AppendLine(sentence);
                }
            }
            else
            {
                sb.AppendLine("Start by identifying the main parts of the topic, then connect each part to its function. A learner should ask: what is this item or idea, what does it do, why is it required, what can go wrong, and how would correct practice prevent failure or unsafe work?");
                sb.AppendLine("In a diesel motor mechanic context, theory becomes useful when it helps you make sound decisions about inspection, servicing, fault finding, repair, safety, quality, and communication with supervisors or clients.");
            }

            sb.AppendLine();
            sb.AppendLine("Worked Example");
            sb.AppendLine($"When you encounter {topicTitle.ToLowerInvariant()} in a workplace situation, first name the concept, then describe its purpose, then explain the sequence or relationship involved. For example, if the topic concerns a system, identify the input, the process that changes or controls that input, and the output expected from a correctly working system. If the topic concerns employment or workshop practice, identify the parties involved, the rule or process that governs the situation, and the practical consequence of applying it correctly.");

            sb.AppendLine();
            sb.AppendLine("Check Your Understanding");
            sb.AppendLine($"1. Define the main concept in {topicTitle} in your own words.");
            sb.AppendLine("2. Explain why the concept matters in real workshop or workplace practice.");
            sb.AppendLine("3. Give one example of correct application and one example of a problem that could occur if the concept is misunderstood.");

            return NormalizeDocumentText(sb.ToString());
        }

        private static bool IsEngineRoleImportanceTopic(string? topicDescription, string? criteriaDescription)
        {
            var normalized = NormalizeLooseText($"{topicDescription} {criteriaDescription}");
            return normalized.Contains("role and importance of engines", StringComparison.Ordinal) ||
                   (normalized.Contains("engines motors vehicles", StringComparison.Ordinal) &&
                    normalized.Contains("maintenance and repair", StringComparison.Ordinal));
        }

        private static string BuildEngineRoleImportanceInstructionalSection(string topicTitle)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Concept Explanation");
            sb.AppendLine("Engines, motors, vehicles and light and heavy equipment are important because they convert energy into useful work. In transport, construction, agriculture, mining and industry, machines must move people, goods, tools, soil, loads and materials reliably. The engine or motor is the power source that makes this possible. A vehicle or machine without a reliable power source cannot accelerate, pull, lift, pump, generate pressure, operate attachments, or complete productive work safely.");
            sb.AppendLine("An engine is a machine that changes energy into mechanical movement. In a diesel engine, chemical energy in diesel fuel is released through combustion. The heat and pressure from combustion push pistons downward. The piston movement is transferred through connecting rods to the crankshaft, where it becomes rotating motion. That rotating motion can drive wheels, hydraulic pumps, alternators, compressors, fans, power take-off units, or other machine systems. This is why the engine is often described as the heart of a vehicle or item of equipment: many other systems depend on it for power.");
            sb.AppendLine("A motor also converts energy into movement, but the term is often used for electric, hydraulic or pneumatic power units. In modern vehicles and equipment, engines and motors may work together. For example, a diesel engine may drive an alternator that supplies electrical power, while electric motors operate fans, pumps or actuators. Hybrid and electric machines use electric traction motors to move the vehicle, but the same principle remains: stored energy must be converted into controlled mechanical work.");
            sb.AppendLine("Vehicles and light and heavy equipment are important because they extend human capability. A light vehicle can move people and tools quickly. A truck can transport heavy loads. An excavator can dig, lift and swing material. A loader can move soil or aggregate. A tractor can pull implements. These machines increase productivity, reduce manual labour, support emergency services, keep supply chains working and make modern infrastructure possible. When they fail, work stops, costs rise and safety risks increase.");
            sb.AppendLine("Maintenance and repair keep these machines safe, reliable and economical. Maintenance includes planned actions such as checking fluid levels, replacing filters, inspecting belts and hoses, lubricating moving parts, checking cooling systems, testing batteries, inspecting tyres and brakes, and recording service information. Repair is corrective work done after a defect or failure is found. A mechanic must understand both because preventing a failure is usually safer and cheaper than repairing major damage after a breakdown.");

            sb.AppendLine();
            sb.AppendLine("Key Terms");
            sb.AppendLine("Engine: a machine that converts fuel energy into mechanical movement, usually by burning fuel inside cylinders and turning a crankshaft.");
            sb.AppendLine("Combustion: the controlled burning of fuel with oxygen that releases heat energy and pressure.");
            sb.AppendLine("Internal combustion engine: an engine where combustion takes place inside the engine cylinders, so expanding gases act directly on pistons or rotors.");
            sb.AppendLine("Diesel engine: an internal combustion engine that compresses air until it is hot enough to ignite diesel fuel injected into the cylinder.");
            sb.AppendLine("Motor: a device that converts electrical, hydraulic or pneumatic energy into mechanical movement.");
            sb.AppendLine("Vehicle: a machine designed to transport people, goods or equipment from one place to another.");
            sb.AppendLine("Heavy equipment: large work machines such as loaders, excavators, graders, trucks and tractors used for demanding industrial, construction, mining or agricultural work.");
            sb.AppendLine("Maintenance: planned inspection, servicing and adjustment carried out to keep a machine safe, reliable and efficient.");
            sb.AppendLine("Repair: fault-finding and corrective work carried out to restore a damaged, worn or failed system to proper operation.");
            sb.AppendLine("Mechanic: a skilled person who inspects, maintains, diagnoses and repairs engines, vehicles and equipment using technical knowledge, tools, measurements and safe work practices.");

            sb.AppendLine();
            sb.AppendLine("How It Works");
            sb.AppendLine("The role of an engine begins with energy conversion. Diesel fuel stores chemical energy. Air enters the cylinder and is compressed by the piston. Compression raises the air temperature. When diesel fuel is injected into this hot compressed air, it ignites. Combustion creates rapidly expanding gases. These gases push the piston down with force. The connecting rod transfers this force to the crankshaft, and the crankshaft changes the up-and-down piston movement into rotation.");
            sb.AppendLine("The rotating crankshaft does not work alone. It connects to the flywheel, clutch or torque converter, transmission, drive shafts, differentials and final drives. These systems control how power reaches the wheels or tracks. On equipment, engine power may also drive hydraulic pumps. Hydraulic pressure then moves booms, buckets, blades, steering systems or lifting equipment. This shows why the engine is central: it supplies the power that other systems control, transmit and apply.");
            sb.AppendLine("The importance of maintenance becomes clear when you consider what the engine needs to survive. It needs clean air for combustion, clean fuel for power, correct oil for lubrication, coolant for temperature control, strong electrical supply for starting and control systems, and unrestricted exhaust flow. If air filters block, combustion becomes poor. If oil is dirty or low, moving parts wear rapidly. If coolant leaks or the radiator is blocked, overheating can damage the cylinder head, pistons or seals. If fuel is contaminated, injectors and pumps can fail.");
            sb.AppendLine("Repair requires diagnosis before parts are replaced. A mechanic listens to the complaint, checks symptoms, confirms the fault, tests related systems and identifies the root cause. For example, an overheating engine may have low coolant, a faulty thermostat, a blocked radiator, a weak water pump, a damaged fan belt, trapped air, a failed pressure cap or combustion gases entering the cooling system. Good repair work finds the actual cause, not only the most obvious symptom.");

            sb.AppendLine();
            sb.AppendLine("Worked Example");
            sb.AppendLine("A diesel bakkie arrives at the workshop with poor power and black exhaust smoke. The role of the engine is to convert fuel into useful movement, but black smoke shows that the fuel is not burning cleanly. The mechanic checks the air intake, because diesel combustion needs enough clean air. A blocked air filter is found. The filter restriction reduces air flow, so the engine receives too much fuel for the available oxygen. Combustion becomes incomplete, power drops and soot leaves the exhaust as black smoke. Replacing the filter, checking the intake system and confirming performance restores the engine's ability to produce power efficiently.");
            sb.AppendLine("This example shows why the mechanic's work is important. The mechanic does not only replace parts. The mechanic understands the engine's purpose, recognises symptoms, links symptoms to system operation, tests likely causes and restores safe, reliable machine performance. This protects the owner from unnecessary costs and protects the workplace from downtime and unsafe operation.");

            sb.AppendLine();
            sb.AppendLine("Check Your Understanding");
            sb.AppendLine("1. Explain why an engine is described as a power source for a vehicle or machine.");
            sb.AppendLine("2. Describe how a diesel engine changes fuel energy into crankshaft rotation.");
            sb.AppendLine("3. Explain the difference between maintenance and repair.");
            sb.AppendLine("4. Give two examples of how poor maintenance can lead to engine failure.");
            sb.AppendLine("5. Explain why a mechanic must diagnose the root cause of a fault before replacing parts.");

            return NormalizeDocumentText(sb.ToString());
        }

        private static string BuildKeyTermDefinition(string term)
        {
            var normalized = NormalizeLooseText(term);
            return normalized switch
            {
                "engine" or "engines" => "a machine that converts stored energy, usually fuel energy, into mechanical movement that can do work.",
                "combustion" => "the controlled burning of fuel with oxygen, releasing heat and pressure that can be used to produce movement.",
                "internal" => "located or occurring inside a system; in an internal combustion engine, combustion happens inside the engine itself.",
                "diesel" => "a fuel and engine type associated with compression ignition, where hot compressed air ignites injected diesel fuel.",
                "reciprocating" => "moving backwards and forwards or up and down repeatedly, as pistons do inside many engines.",
                "vehicle" or "vehicles" => "a machine designed to move people, goods, tools or equipment from one place to another.",
                "maintenance" => "planned inspection and servicing used to prevent failures and keep a machine safe, reliable and efficient.",
                "repair" or "repairs" => "corrective work used to restore a worn, damaged or faulty component or system to proper operation.",
                "mechanic" => "a skilled person who inspects, diagnoses, services and repairs vehicles, engines and equipment.",
                "equipment" => "machines, tools or systems used to perform work, especially in workshop, construction, agricultural, mining or industrial settings.",
                "lubrication" => "the use of oil or grease to reduce friction, wear and heat between moving parts.",
                "cooling" => "the process of removing excess heat so an engine or component stays within safe operating temperature.",
                _ => "a technical term in this topic that must be defined by its function, where it is used, and how it affects safe and reliable work."
            };
        }

        private static List<string> ExtractInstructionalSentences(string? sourceText, int maxCount)
        {
            var cleanedSource = Regex.Replace(sourceText ?? string.Empty, @"\b(?:Curriculum requirement|Reference material|Citation|Source)\s*:\s*", " ", RegexOptions.IgnoreCase);
            return Regex.Split(cleanedSource, @"(?<=[\.\!\?])\s+")
                .Select(NormalizeDocumentText)
                .Where(sentence => DocumentTextCleaner.WordCount(sentence) >= 8)
                .Where(sentence => sentence.Length <= 420)
                .Where(sentence => !LooksLikeMetaInstructionOnly(sentence))
                .Where(sentence => !LooksLikeAssessmentCriteriaRestatement(sentence))
                .Where(sentence => !NormalizeLooseText(sentence).StartsWith("reference material", StringComparison.Ordinal))
                .Where(sentence => !NormalizeLooseText(sentence).StartsWith("curriculum requirement", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(maxCount)
                .ToList();
        }

        private static string CleanCurriculumRequirementText(string? value)
        {
            var text = NormalizeDocumentText(StripCurriculumCodesFromText(value));
            text = Regex.Replace(text, @"\b(?:IAC|AC|ELO|KT|KM|PM|WM)\d+[A-Za-z0-9]*\b", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\(\s*Weight\s*\d+%?\s*\)", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s*\|\s*", ". ");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text.Trim(' ', '.', ';', ':');
        }

        private static List<string> ExtractKeyTermsForInstructionalSection(params string?[] values)
        {
            var stop = new HashSet<string>(new[]
            {
                "define", "describe", "discuss", "explain", "impact", "weight", "topic", "source",
                "evidence", "learner", "focus", "study", "relation", "assessment", "criteria",
                "this", "that", "with", "from", "their", "which", "must", "will", "able"
            }, StringComparer.OrdinalIgnoreCase);

            return Regex.Matches(string.Join(" ", values.Where(v => !string.IsNullOrWhiteSpace(v))), @"\b[A-Za-z][A-Za-z\-]{4,}\b")
                .Select(match => match.Value.ToLowerInvariant())
                .Where(term => !stop.Contains(term))
                .GroupBy(term => term, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Key)
                .Take(8)
                .ToList();
        }

        private static string ToTitleCase(string value)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Length == 0) return text;
            return char.ToUpperInvariant(text[0]) + (text.Length > 1 ? text[1..] : string.Empty);
        }

        private static int ParseInt(string? value, int fallback)
        {
            return int.TryParse((value ?? string.Empty).Trim(), out var parsed) ? parsed : fallback;
        }

        private static bool IsThinLessonBlock(GuideLessonBlock lesson)
        {
            var normalizedContent = NormalizeLessonContentForGuide(lesson.LessonContent);
            if (string.IsNullOrWhiteSpace(normalizedContent))
            {
                return true;
            }

            var wordCount = Regex.Matches(normalizedContent, @"\b[\p{L}\p{N}]+\b").Count;
            return normalizedContent.Length < 320 || wordCount < 45;
        }

        private static List<GuideLessonBlock> BuildEvidenceBackedLessonBlocks(
            CurriculumDeliveryPilotService.TopicEvidenceItem? pilotTopic,
            string? topicCode,
            string? topicDescription,
            string? criteriaDescription)
        {
            if (pilotTopic?.TopEvidence == null || pilotTopic.TopEvidence.Count == 0)
            {
                return new List<GuideLessonBlock>();
            }

            var focus = CleanCurriculumRequirementText(criteriaDescription);
            if (string.IsNullOrWhiteSpace(focus))
            {
                focus = CleanCurriculumRequirementText(topicDescription);
            }
            if (string.IsNullOrWhiteSpace(focus))
            {
                focus = "this topic";
            }

            var blocks = new List<GuideLessonBlock>();
            var evidenceItems = pilotTopic.TopEvidence
                .Where(e => !string.IsNullOrWhiteSpace(e.Excerpt))
                .OrderByDescending(e => e.ConfidencePercent)
                .Take(4)
                .ToList();
            if (evidenceItems.Count == 0)
            {
                return blocks;
            }

            var bestTitle = NormalizeDocumentText(evidenceItems[0].MaterialTitle);
            if (string.IsNullOrWhiteSpace(bestTitle))
            {
                bestTitle = NormalizeDocumentText(evidenceItems[0].Citation);
            }
            if (string.IsNullOrWhiteSpace(bestTitle))
            {
                bestTitle = NormalizeDocumentText(topicDescription);
            }
            if (string.IsNullOrWhiteSpace(bestTitle))
            {
                bestTitle = "Learning content";
            }

            _ = focus;
            var contentLines = new List<string>();
            for (var i = 0; i < evidenceItems.Count; i++)
            {
                var evidence = evidenceItems[i];
                var excerpt = TrimEvidenceExcerpt(evidence.Excerpt, 1400);
                if (!string.IsNullOrWhiteSpace(excerpt))
                {
                    contentLines.Add(excerpt);
                }
            }

            blocks.Add(new GuideLessonBlock
            {
                Lpn = "LPN E01",
                LessonPlanDescription = bestTitle,
                LessonContent = string.Join("\n\n", contentLines),
                LearningAids = string.Empty,
                LecturerActions = string.Empty,
                LearnerActions = string.Empty,
                TimeStart = string.Empty,
                TimeEnd = string.Empty
            });

            return blocks;
        }

        private static string BuildEvidenceCoverageLine(CurriculumDeliveryPilotService.TopicEvidenceItem pilotTopic)
        {
            if (pilotTopic.CoveragePercent <= 0 && string.IsNullOrWhiteSpace(pilotTopic.CoverageBandLabel))
            {
                return string.Empty;
            }

            var label = NormalizeDocumentText(pilotTopic.CoverageBandLabel);
            var coverage = pilotTopic.CoveragePercent > 0 ? $"{pilotTopic.CoveragePercent}%" : "available";
            if (string.IsNullOrWhiteSpace(label))
            {
                return $"Evidence coverage for this topic: {coverage}.";
            }

            return $"Evidence coverage for this topic: {coverage} ({label}).";
        }

        private static string TrimEvidenceExcerpt(string? excerpt, int maxChars)
        {
            var normalized = NormalizeDocumentText(excerpt);
            if (normalized.Length <= maxChars)
            {
                return normalized;
            }

            var trimmed = normalized.Substring(0, maxChars).TrimEnd();
            var sentenceEnd = Math.Max(
                Math.Max(trimmed.LastIndexOf('.'), trimmed.LastIndexOf('!')),
                trimmed.LastIndexOf('?'));
            if (sentenceEnd > maxChars / 2)
            {
                trimmed = trimmed.Substring(0, sentenceEnd + 1);
            }

            return $"{trimmed}...";
        }

        private static string BuildSummary(List<string> blocks, string criteriaDescription)
        {
            if (blocks.Count == 0) return $"No lesson content available for {criteriaDescription}.";
            var flattened = string.Join(" ", blocks);
            var sentences = Regex.Split(flattened, @"(?<=[.!?])\s+")
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Take(4)
                .ToList();
            if (sentences.Count == 0)
            {
                var fallback = flattened.Trim();
                return fallback.Length <= 600 ? fallback : $"{fallback.Substring(0, 600)}...";
            }
            return string.Join(" ", sentences);
        }

        private sealed class TopicGuideSection
        {
            public int TopicId { get; init; }
            public string TopicCode { get; init; } = string.Empty;
            public string TopicDescription { get; init; } = string.Empty;
            public string TopicPurpose { get; init; } = string.Empty;
            public List<CriteriaGuideSection> Criteria { get; init; } = new();
        }

        private sealed class GuideCriteriaGroup
        {
            public string TopicKey { get; init; } = string.Empty;
            public AssessmentCriteria Representative { get; init; } = null!;
            public List<AssessmentCriteria> Members { get; init; } = new();
        }

        private sealed class GuideIllustration
        {
            public string ImagePath { get; init; } = string.Empty;
            public string Caption { get; init; } = string.Empty;
            public string Source { get; init; } = string.Empty;
            public int Score { get; init; }
        }

        private sealed class OpenAiGeneratedGuideImageResult
        {
            public byte[]? Bytes { get; init; }
            public string Error { get; init; } = string.Empty;

            public static OpenAiGeneratedGuideImageResult Success(byte[] bytes) => new()
            {
                Bytes = bytes ?? Array.Empty<byte>(),
                Error = string.Empty
            };

            public static OpenAiGeneratedGuideImageResult Fail(string error) => new()
            {
                Bytes = null,
                Error = error ?? string.Empty
            };
        }

        private sealed class OpenAiTtsResult
        {
            public byte[]? Bytes { get; init; }
            public string Error { get; init; } = string.Empty;

            public static OpenAiTtsResult Success(byte[] bytes) => new()
            {
                Bytes = bytes ?? Array.Empty<byte>(),
                Error = string.Empty
            };

            public static OpenAiTtsResult Fail(string error) => new()
            {
                Bytes = null,
                Error = error ?? string.Empty
            };
        }

        private sealed class CriteriaReadinessDiagnostic
        {
            public int CriteriaId { get; init; }
            public string CriteriaDescription { get; init; } = string.Empty;
            public int MatchedRows { get; init; }
            public int MatchedRowsWithContent { get; init; }
            public int FallbackPlans { get; init; }
            public int FallbackPlansWithContent { get; init; }
            public int EvidenceFallbackBlocks { get; init; }
            public bool HasAnyContent { get; init; }
        }

        private sealed class CriteriaGuideSection
        {
            public int CriteriaId { get; init; }
            public string CriteriaDescription { get; init; } = string.Empty;
            public List<GuideLessonBlock> Lessons { get; init; } = new();
        }

        private sealed class UploadedLessonPlanSourceRow
        {
            public int? QualificationId { get; init; }
            public string QualificationCode { get; init; } = string.Empty;
            public string SubjectCode { get; init; } = string.Empty;
            public string SubjectDescription { get; init; } = string.Empty;
            public string TopicCode { get; init; } = string.Empty;
            public string TopicDescription { get; init; } = string.Empty;
            public string CriteriaDescription { get; init; } = string.Empty;
            public string Lpn { get; init; } = string.Empty;
            public string LessonPlanDescription { get; init; } = string.Empty;
            public string LessonPlanContent { get; init; } = string.Empty;
            public int SourceOrder { get; init; }
        }

        private sealed class UploadedLessonPlanSourceMetadata
        {
            public int? QualificationId { get; init; }
            public string QualificationCode { get; init; } = string.Empty;
            public string SourceFileName { get; init; } = string.Empty;
            public DateTime UpdatedAtUtc { get; init; }
        }

        private sealed class GuideLessonBlock
        {
            public string Lpn { get; init; } = string.Empty;
            public string LessonPlanDescription { get; init; } = string.Empty;
            public string LessonContent { get; init; } = string.Empty;
            public string LecturerActions { get; init; } = string.Empty;
            public string LearnerActions { get; init; } = string.Empty;
            public string LearningAids { get; init; } = string.Empty;
            public string TimeStart { get; init; } = string.Empty;
            public string TimeEnd { get; init; } = string.Empty;
        }

        private static Paragraph PageBreak() => new(new Run(new Break() { Type = BreakValues.Page }));

        private static Paragraph StyledHeading(string text, string styleId, int sizePt)
        {
            var rp = new RunProperties
            {
                Bold = new Bold(),
                FontSize = new FontSize { Val = (sizePt * 2).ToString() },
                RunFonts = new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman" }
            };
            var pp = new ParagraphProperties(
                new ParagraphStyleId { Val = styleId },
                new SpacingBetweenLines { Before = "120", After = "120" });
            return new Paragraph(pp, new Run(rp, new Text(SanitizeXmlText(text ?? string.Empty))));
        }

        private static Paragraph BuildChapterHeading(string text)
        {
            return BuildGuideHeadingParagraph(
                text,
                "Heading1",
                56,
                bold: true,
                allCaps: true,
                beforeTwips: 120,
                afterTwips: 240,
                justification: JustificationValues.Left,
                outlineLevel: 0);
        }

        private static Paragraph BuildSubjectHeading(string text)
        {
            return BuildGuideHeadingParagraph(
                text,
                "Heading2",
                32,
                bold: false,
                allCaps: true,
                beforeTwips: 240,
                afterTwips: 240,
                justification: JustificationValues.Left,
                bottomBorderSize: 12,
                outlineLevel: 1);
        }

        private static Paragraph BuildPurposeHeading(string text)
        {
            return BuildGuideHeadingParagraph(
                text,
                null,
                32,
                bold: true,
                allCaps: true,
                beforeTwips: 240,
                afterTwips: 240,
                justification: JustificationValues.Left);
        }

        private static Paragraph BuildAssessmentCriteriaHeading(string text)
        {
            return BuildGuideHeadingParagraph(
                text,
                "Heading3",
                28,
                bold: true,
                allCaps: true,
                beforeTwips: 160,
                afterTwips: 160,
                justification: JustificationValues.Left,
                topBorderSize: 12,
                bottomBorderSize: 12,
                shadingFill: "D9D9D9",
                outlineLevel: 2);
        }

        private static Paragraph BuildTopicHeading(string text)
        {
            return BuildGuideHeadingParagraph(
                text,
                "Heading4",
                28,
                bold: true,
                allCaps: true,
                beforeTwips: 160,
                afterTwips: 160,
                justification: JustificationValues.Left,
                bottomBorderSize: 12,
                outlineLevel: 3);
        }

        private static Paragraph BuildLessonPlanHeading(string text)
        {
            return BuildGuideHeadingParagraph(
                text,
                "Heading5",
                28,
                bold: true,
                allCaps: true,
                beforeTwips: 0,
                afterTwips: 240,
                justification: JustificationValues.Right,
                outlineLevel: 4);
        }

        private static Paragraph BuildLessonContentTitleHeading(string text)
        {
            return BuildGuideHeadingParagraph(
                text,
                "Heading6",
                24,
                bold: true,
                allCaps: true,
                beforeTwips: 0,
                afterTwips: 240,
                justification: JustificationValues.Left,
                topBorderSize: 8,
                outlineLevel: 5);
        }

        private static Paragraph BuildWorkbookActivitiesHeading(string text)
        {
            return BuildGuideHeadingParagraph(
                text,
                "Heading3",
                28,
                bold: true,
                allCaps: false,
                beforeTwips: 160,
                afterTwips: 160,
                justification: JustificationValues.Left);
        }

        private static Paragraph BuildGuideHeadingParagraph(
            string text,
            string? styleId,
            int sizeHalfPt,
            bool bold,
            bool allCaps,
            int beforeTwips,
            int afterTwips,
            JustificationValues justification,
            uint? topBorderSize = null,
            uint? bottomBorderSize = null,
            string? shadingFill = null,
            int? outlineLevel = null)
        {
            var runProperties = new RunProperties
            {
                FontSize = new FontSize { Val = sizeHalfPt.ToString() },
                RunFonts = new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman" }
            };
            if (bold) runProperties.Bold = new Bold();
            if (allCaps) runProperties.Caps = new Caps();

            var paragraphProperties = new ParagraphProperties(
                new SpacingBetweenLines
                {
                    Before = beforeTwips.ToString(),
                    After = afterTwips.ToString(),
                    Line = "360",
                    LineRule = LineSpacingRuleValues.Auto
                },
                new Justification { Val = justification });

            if (!string.IsNullOrWhiteSpace(styleId))
            {
                paragraphProperties.ParagraphStyleId = new ParagraphStyleId { Val = styleId };
            }

            if (outlineLevel.HasValue && outlineLevel.Value >= 0)
            {
                paragraphProperties.OutlineLevel = new OutlineLevel { Val = outlineLevel.Value };
            }

            if (topBorderSize.HasValue || bottomBorderSize.HasValue)
            {
                var borders = new ParagraphBorders();
                if (topBorderSize.HasValue)
                {
                    borders.TopBorder = new TopBorder
                    {
                        Val = BorderValues.Single,
                        Size = topBorderSize.Value,
                        Space = 0U,
                        Color = "000000"
                    };
                }
                if (bottomBorderSize.HasValue)
                {
                    borders.BottomBorder = new BottomBorder
                    {
                        Val = BorderValues.Single,
                        Size = bottomBorderSize.Value,
                        Space = 0U,
                        Color = "000000"
                    };
                }
                paragraphProperties.Append(borders);
            }

            if (!string.IsNullOrWhiteSpace(shadingFill))
            {
                paragraphProperties.Shading = new Shading
                {
                    Val = ShadingPatternValues.Clear,
                    Color = "auto",
                    Fill = shadingFill
                };
            }

            return new Paragraph(
                paragraphProperties,
                new Run(runProperties, new Text(SanitizeXmlText(text ?? string.Empty)) { Space = SpaceProcessingModeValues.Preserve }));
        }

        private static Paragraph CenterPara(string text, int sizeHalfPt, bool bold = false)
        {
            var rp = new RunProperties
            {
                FontSize = new FontSize { Val = sizeHalfPt.ToString() },
                RunFonts = new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman" }
            };
            if (bold) rp.Bold = new Bold();
            var pp = new ParagraphProperties(
                new SpacingBetweenLines { Line = "320", LineRule = LineSpacingRuleValues.Auto },
                new Justification { Val = JustificationValues.Center });
            return new Paragraph(pp, new Run(rp, new Text(SanitizeXmlText(text ?? string.Empty))));
        }

        private static Paragraph BodyPara(string text, int sizeHalfPt, int firstLineTwips = 720, bool bold = false)
        {
            var rp = new RunProperties
            {
                FontSize = new FontSize { Val = sizeHalfPt.ToString() },
                RunFonts = new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman" }
            };
            if (bold) rp.Bold = new Bold();
            var pp = new ParagraphProperties(
                new SpacingBetweenLines { Line = "360", LineRule = LineSpacingRuleValues.Auto },
                new Indentation { FirstLine = firstLineTwips.ToString() });
            return new Paragraph(pp, new Run(rp, new Text(SanitizeXmlText(text ?? string.Empty))));
        }

        private static Paragraph BulletBodyPara(string text, int sizeHalfPt)
        {
            var rp = new RunProperties
            {
                FontSize = new FontSize { Val = sizeHalfPt.ToString() },
                RunFonts = new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman" }
            };
            var pp = new ParagraphProperties(
                new SpacingBetweenLines { Line = "360", LineRule = LineSpacingRuleValues.Auto },
                new Indentation { Left = "720", Hanging = "240" });
            return new Paragraph(pp, new Run(rp, new Text(SanitizeXmlText($"- {text ?? string.Empty}"))));
        }

        private static void AppendMultilineBody(Body body, string text, int sizeHalfPt)
        {
            var normalized = NormalizeDocumentText(text);
            if (string.IsNullOrWhiteSpace(normalized)) return;

            var blocks = normalized
                .Split('\n')
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();

            foreach (var block in blocks)
            {
                body.Append(BodyPara(block, sizeHalfPt, 0));
            }
        }

        private static void AppendExactLessonPlanContent(Body body, string text, int sizeHalfPt)
        {
            var raw = (text ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            if (string.IsNullOrWhiteSpace(raw)) return;

            var buffer = new List<string>();
            foreach (var rawLine in raw.Split('\n', StringSplitOptions.None))
            {
                var line = NormalizeDocumentText(rawLine);
                line = TrimLeadingGuideNoiseSentences(line);
                if (string.IsNullOrWhiteSpace(line))
                {
                    FlushLessonContentParagraph(body, buffer, sizeHalfPt);
                    continue;
                }

                if (IsRigidLessonHeading(line))
                {
                    FlushLessonContentParagraph(body, buffer, sizeHalfPt);
                    continue;
                }

                if (LooksLikeLessonCodeLabel(line) || LooksLikeLessonInstructionDirective(line))
                {
                    FlushLessonContentParagraph(body, buffer, sizeHalfPt);
                    continue;
                }

                if (LooksLikeLessonReferenceNoise(line))
                {
                    FlushLessonContentParagraph(body, buffer, sizeHalfPt);
                    continue;
                }

                line = StripCurriculumCodesFromText(line);
                if (string.IsNullOrWhiteSpace(line))
                {
                    FlushLessonContentParagraph(body, buffer, sizeHalfPt);
                    continue;
                }

                if (LooksLikeLessonContentTitle(line))
                {
                    FlushLessonContentParagraph(body, buffer, sizeHalfPt);
                    body.Append(BuildLessonContentTitleHeading(line));
                    continue;
                }

                buffer.Add(line);
            }

            FlushLessonContentParagraph(body, buffer, sizeHalfPt);
        }

        private static void FlushLessonContentParagraph(Body body, List<string> buffer, int sizeHalfPt)
        {
            if (buffer.Count == 0) return;
            var paragraph = SanitizeLessonNarrativeText(string.Join(" ", buffer));
            if (!string.IsNullOrWhiteSpace(paragraph))
            {
                body.Append(BodyPara(paragraph, sizeHalfPt, 0));
            }
            buffer.Clear();
        }

        private static bool IsRigidLessonHeading(string line)
        {
            var normalized = NormalizeDocumentText(line).Trim().TrimEnd(':').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized)) return false;

            return normalized is "overview"
                or "core technical understanding"
                or "detailed technical content"
                or "procedure and application"
                or "safety and quality checks"
                or "common faults / errors"
                or "common faults"
                or "errors"
                or "summary"
                or "assessment focus";
        }

        private static string TrimLeadingGuideNoiseSentences(string text)
        {
            var current = NormalizeDocumentText(text);
            if (string.IsNullOrWhiteSpace(current))
            {
                return string.Empty;
            }

            current = current.Replace(AutoCurriculumDraftMarker, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            while (!string.IsNullOrWhiteSpace(current))
            {
                var sentenceMatch = Regex.Match(current, @"^(?<lead>.*?[\.!?])\s*(?<tail>.+)$");
                if (!sentenceMatch.Success)
                {
                    break;
                }

                var lead = NormalizeDocumentText(sentenceMatch.Groups["lead"].Value);

                // If lead is an evidence lead-in or a code label, drop it and continue.
                if (LooksLikeLessonEvidenceLeadIn(lead) || LooksLikeLessonCodeLabel(lead))
                {
                    current = NormalizeDocumentText(sentenceMatch.Groups["tail"].Value);
                    continue;
                }

                // If lead looks like an instruction directive, convert it to learner-facing text
                // instead of discarding it. Insert converted sentence and stop trimming.
                if (LooksLikeLessonInstructionDirective(lead))
                {
                    var converted = ConvertInstructionDirectiveToLearnerSentence(lead);
                    if (!string.IsNullOrWhiteSpace(converted))
                    {
                        current = (converted + " " + NormalizeDocumentText(sentenceMatch.Groups["tail"].Value)).Trim();
                    }
                    else
                    {
                        current = NormalizeDocumentText(sentenceMatch.Groups["tail"].Value);
                    }
                    break;
                }

                // Otherwise stop trimming.
                break;
            }

            return current.Trim();
        }

        private static bool LooksLikeLessonEvidenceLeadIn(string line)
        {
            var normalized = NormalizeLooseText(StripCurriculumCodesFromText(line));
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            return normalized.StartsWith("the mapped technical evidence for", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeLessonInstructionDirective(string line)
        {
            var normalized = NormalizeLooseText(StripCurriculumCodesFromText(line));
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            if (normalized.StartsWith("this lesson develops the learner's ability to", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("key emphasis areas for", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("focus your study of", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("build your understanding of", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("learn what each term means", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("where it appears in practice", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("show learners how to apply", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("emphasise the specific safety", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("emphasise the safe method of work", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("highlight the common mistakes learners may make", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("conclude the lesson by checking", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("study the explanation below carefully", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("follow the sequence step by step", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("study the lesson material for", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("work through the topic in sequence", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("introduce ", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("you must understand ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return Regex.IsMatch(
                normalized,
                @"^(?:explain|describe|demonstrate|discuss|clarify|show|outline|identify|state)\b.*(?:\bin detail\b|\bby clarifying\b|\bhow it works\b|\bwhen it is applied\b|\bthe learner will\b|\blearners will\b|\bhow you will recognise\b|\bhow the learner will recognise\b)",
                RegexOptions.IgnoreCase);
        }

        private static bool LooksLikeAssessmentCriteriaRestatement(string? line)
        {
            var normalized = NormalizeLooseText(StripCurriculumCodesFromText(line));
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            if (normalized.Count(ch => ch == '|') >= 2)
            {
                return true;
            }

            return normalized.StartsWith("define and describe ", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("discuss the impact ", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("describe the processes ", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeLessonCodeLabel(string line)
        {
            var text = NormalizeDocumentText(line);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (Regex.IsMatch(text, @"^\s*LPN\s+[A-Z0-9\-]+\s*:", RegexOptions.IgnoreCase))
            {
                return true;
            }

            if (Regex.IsMatch(text, @"^\s*TOPIC\s+(?:KT|AC|KG)\s*-?\s*\d+[A-Z0-9\-]*\s*:", RegexOptions.IgnoreCase))
            {
                return true;
            }

            if (Regex.IsMatch(text, @"^\s*(?:KT|AC|KG)\s*-?\s*\d+[A-Z0-9\-]*\s*[:\-]", RegexOptions.IgnoreCase))
            {
                return true;
            }

            return Regex.IsMatch(text, @"^\s*Topic\s*:\s*(?:KT|AC|KG)\s*-?\s*\d+[A-Z0-9\-]*\b", RegexOptions.IgnoreCase);
        }

        private static string StripCurriculumCodesFromText(string? text)
        {
            var normalized = NormalizeDocumentText(text);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            normalized = normalized.Replace(AutoCurriculumDraftMarker, string.Empty, StringComparison.OrdinalIgnoreCase);
            normalized = normalized.Replace(AutoCurriculumCoverageGapMarker, string.Empty, StringComparison.OrdinalIgnoreCase);
            normalized = Regex.Replace(normalized, @"\bLPN\s+[A-Z0-9\-]+\b:?", string.Empty, RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\bAUTO-(?:KT|AC|KG)[A-Z0-9\-]*\b", string.Empty, RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\b(?:KT|AC|KG)\s*-?\s*\d+[A-Z0-9\-]*\b", string.Empty, RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\s{2,}", " ").Trim(' ', '-', ':', ';', '.');
            return normalized;
        }

        private static string SanitizeLessonNarrativeText(string text)
        {
            var normalized = StripCurriculumCodesFromText(text);
            if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;

            normalized = Regex.Replace(normalized, @"\bthe learner must be able to\b", "you must", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\bthe learner must understand\b", "you must understand", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\bthe learner must\b", "you must", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\blearners must\b", "you must", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\bthe learner should\b", "you should", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\bthe learner will\b", "you will", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\blearners will\b", "you will", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\s{2,}", " ").Trim();
            return normalized;
        }

        private static bool LooksLikeLessonContentTitle(string line)
        {
            var text = NormalizeDocumentText(line);
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.Length > 80) return false;
            if (text.EndsWith(".") || text.EndsWith(":") || text.EndsWith(";")) return false;
            if (text.StartsWith("LPN ", StringComparison.OrdinalIgnoreCase)) return false;
            if (text.StartsWith("Topic ", StringComparison.OrdinalIgnoreCase)) return false;
            if (Regex.IsMatch(text, @"^\d+[\.\)]")) return false;

            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || words.Length > 8) return false;

            return words.Count(word => char.IsLetter(word.FirstOrDefault())) >= 2;
        }

        private static bool LooksLikeLessonReferenceNoise(string line)
        {
            var text = NormalizeDocumentText(line);
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (Regex.IsMatch(text, @"^(citations?|bibliography|references|assessment alignment)\b", RegexOptions.IgnoreCase))
            {
                return true;
            }

            return Regex.IsMatch(
                text,
                @"^\[\d+\]\s*|[A-Za-z]:\\|\.pdf\b|\.docx\b|\.pptx\b|\.xlsx\b|##\s*Page\b|https?://|\bcurriculum\s+content\s+map\b",
                RegexOptions.IgnoreCase);
        }

        private static string BuildLessonPlanHeadingText(string? lpn, string? description)
        {
            _ = lpn;
            var normalizedDescription = NormalizeGuideLessonDescription(description);
            if (!string.IsNullOrWhiteSpace(normalizedDescription)) return normalizedDescription;
            return string.Empty;
        }

        private static string NormalizeGuideLessonDescription(string? text)
        {
            var normalized = StripCurriculumCodesFromText(text);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            var directiveMatch = Regex.Match(
                normalized,
                @"^(?:explain|describe|demonstrate|discuss|show|outline|identify|clarify)\s+(?<title>.+?)(?:\s+in\s+detail\b|\s+step\s+by\s+step\b|\s+for\s+the\s+learner\b|$)",
                RegexOptions.IgnoreCase);
            if (directiveMatch.Success)
            {
                normalized = NormalizeDocumentText(directiveMatch.Groups["title"].Value);
            }

            normalized = Regex.Replace(normalized, @"\s{2,}", " ").Trim(' ', '-', ':', ';', '.');
            if (LooksGenericLessonDescription(normalized) || LooksLikeLessonInstructionDirective(normalized))
            {
                return string.Empty;
            }

            return normalized;
        }

        private static int ResolveWorkbookActivityCount(
            IReadOnlyList<TopicGuideSection> topics,
            IReadOnlyList<string> workbookActivities)
        {
            if (workbookActivities != null && workbookActivities.Count > 0)
            {
                return workbookActivities.Count;
            }

            return topics?
                .SelectMany(topic => topic.Criteria)
                .SelectMany(criteria => criteria.Lessons)
                .Count() ?? 0;
        }

        private static string BuildWorkbookActivitiesSummaryLine(int activityCount)
        {
            if (activityCount <= 1)
            {
                return "Complete the workbook activity linked to this chapter, which is activity 1.";
            }

            return $"Complete the workbook activities linked to this chapter, which are activities 1 – {activityCount}.";
        }

        private static Paragraph BuildTableOfContentsField()
        {
            return new Paragraph(
                new Run(new FieldChar() { FieldCharType = FieldCharValues.Begin }),
                new Run(new FieldCode(" TOC \\o \"1-1\" \\h \\z \\u ") { Space = SpaceProcessingModeValues.Preserve }),
                new Run(new FieldChar() { FieldCharType = FieldCharValues.Separate }),
                new Run(new Text("Table of contents will populate after field update.") { Space = SpaceProcessingModeValues.Preserve }),
                new Run(new FieldChar() { FieldCharType = FieldCharValues.End }));
        }

        private static void EnsureLearnerGuideStyles(MainDocumentPart main)
        {
            var stylePart = main.StyleDefinitionsPart ?? main.AddNewPart<StyleDefinitionsPart>();
            stylePart.Styles ??= new Styles();

            UpsertParagraphStyle(stylePart.Styles, BuildNormalStyle());
            UpsertParagraphStyle(stylePart.Styles, BuildHeadingStyle("Heading1", "heading 1", 0));
            UpsertParagraphStyle(stylePart.Styles, BuildHeadingStyle("Heading2", "heading 2", 1));
            UpsertParagraphStyle(stylePart.Styles, BuildHeadingStyle("Heading3", "heading 3", 2));
            UpsertParagraphStyle(stylePart.Styles, BuildHeadingStyle("Heading4", "heading 4", 3));
            UpsertParagraphStyle(stylePart.Styles, BuildHeadingStyle("Heading5", "heading 5", 4));
            UpsertParagraphStyle(stylePart.Styles, BuildHeadingStyle("Heading6", "heading 6", 5));
            UpsertParagraphStyle(stylePart.Styles, BuildTocStyle("TOC1", "toc 1", 0, bold: true));
            stylePart.Styles.Save();
        }

        private static void UpsertParagraphStyle(Styles styles, Style style)
        {
            var existing = styles.Elements<Style>()
                .FirstOrDefault(candidate => string.Equals(candidate.StyleId?.Value, style.StyleId?.Value, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Remove();
            }

            styles.Append(style);
        }

        private static Style BuildNormalStyle()
        {
            var style = new Style
            {
                Type = StyleValues.Paragraph,
                StyleId = "Normal",
                Default = true
            };
            style.Append(
                new StyleName { Val = "Normal" },
                new PrimaryStyle(),
                new StyleRunProperties(
                    new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman" },
                    new FontSize { Val = "22" },
                    new FontSizeComplexScript { Val = "22" }));
            return style;
        }

        private static Style BuildHeadingStyle(string styleId, string styleName, int outlineLevel)
        {
            var style = new Style
            {
                Type = StyleValues.Paragraph,
                StyleId = styleId,
                CustomStyle = false
            };
            style.Append(
                new StyleName { Val = styleName },
                new BasedOn { Val = "Normal" },
                new NextParagraphStyle { Val = "Normal" },
                new UIPriority { Val = 9 },
                new UnhideWhenUsed(),
                new PrimaryStyle(),
                new StyleParagraphProperties(
                    new OutlineLevel { Val = outlineLevel }),
                new StyleRunProperties(
                    new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman" }));
            return style;
        }

        private static Style BuildTocStyle(string styleId, string styleName, int leftIndentTwips, bool bold)
        {
            var runProperties = new StyleRunProperties(
                new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman" },
                new FontSize { Val = "22" },
                new FontSizeComplexScript { Val = "22" });
            if (bold)
            {
                runProperties.Bold = new Bold();
            }

            var style = new Style
            {
                Type = StyleValues.Paragraph,
                StyleId = styleId,
                CustomStyle = false
            };
            style.Append(
                new StyleName { Val = styleName },
                new BasedOn { Val = "Normal" },
                new NextParagraphStyle { Val = "Normal" },
                new AutoRedefine(),
                new UIPriority { Val = 39 },
                new UnhideWhenUsed(),
                new StyleParagraphProperties(
                    new SpacingBetweenLines { After = "100" },
                    new Indentation { Left = leftIndentTwips.ToString() }),
                runProperties);
            return style;
        }

        private const uint PortraitA4PageWidthTwips = 11906U;
        private const uint PortraitA4PageHeightTwips = 16838U;
        private const uint PortraitCoverUsableWidthTwips = 9866U;

        private static long TwipsToEmu(uint twips) => twips * 635L;

        private static int CentimetresToTwips(double centimetres)
        {
            return Math.Max(0, (int)Math.Round(centimetres * 1440d / 2.54d));
        }

        private static string EnsureLearnerGuideHeader(MainDocumentPart main, Qualification qualification)
        {
            var institution = (qualification.LearningInstitutionName ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(institution)) institution = "LEARNING INSTITUTION";

            var lecturer = (qualification.SeniorLecturer ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(lecturer))
            {
                lecturer = (qualification.DeanPrincipalCEO ?? string.Empty).Trim().ToUpperInvariant();
            }
            if (string.IsNullOrWhiteSpace(lecturer)) lecturer = "LECTURER";

            var headerPart = main.AddNewPart<HeaderPart>();
            var header = new Header();

            var paragraph = new Paragraph(
                new ParagraphProperties(
                    new Tabs(
                        new TabStop { Val = TabStopValues.Center, Position = 5233 },
                        new TabStop { Val = TabStopValues.Right, Position = 10466 }),
                    new SpacingBetweenLines { Before = "0", After = "0" }),
                HeaderRun(institution, bold: true),
                new Run(new TabChar()),
                HeaderRun(lecturer, bold: true),
                new Run(new TabChar()),
                HeaderRun("Page "),
                new SimpleField { Instruction = " PAGE " },
                HeaderRun(" of "),
                new SimpleField { Instruction = " NUMPAGES " });

            header.Append(paragraph);
            headerPart.Header = header;
            headerPart.Header.Save();
            return main.GetIdOfPart(headerPart);
        }

        private static Run HeaderRun(string text, bool bold = false)
        {
            var rp = new RunProperties
            {
                FontSize = new FontSize { Val = "20" },
                RunFonts = new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman" }
            };
            if (bold) rp.Bold = new Bold();
            return new Run(rp, new Text(SanitizeXmlText(text ?? string.Empty)) { Space = SpaceProcessingModeValues.Preserve });
        }

        private static SectionProperties DefaultPortraitSectionProperties(string? headerRelId = null)
        {
            var section = new SectionProperties();
            if (!string.IsNullOrWhiteSpace(headerRelId))
            {
                section.Append(new HeaderReference { Type = HeaderFooterValues.Default, Id = headerRelId });
            }

            section.Append(
                new PageSize() { Orient = PageOrientationValues.Portrait, Width = PortraitA4PageWidthTwips, Height = PortraitA4PageHeightTwips },
                new PageMargin() { Top = 1020, Bottom = 1020, Left = 1020, Right = 1020, Header = 720, Footer = 720, Gutter = 0 });

            return section;
        }

        private static string? ResolveLearnerGuideCoverPath()
        {
            var candidates = new[]
            {
                Path.Combine("Imports", "Coverpages", "clean coverpage.jpg"),
                Path.Combine("ETDP", "Imports", "Coverpages", "clean coverpage.jpg"),
                Path.Combine("Imports", "Coverpages", "Learner Guide Coverpage.png"),
                Path.Combine("ETDP", "Imports", "Coverpages", "Learner Guide Coverpage.png")
            };

            foreach (var relative in candidates)
            {
                var path = ResolveFromCurrentOrParents(relative, 6);
                if (!string.IsNullOrWhiteSpace(path)) return path;
            }

            return null;
        }

        private static string BuildCoverQualificationLine(Qualification qualification)
        {
            var qualificationNumber = (qualification.QualificationNumber ?? string.Empty).Trim();
            var qualificationDescription = (qualification.QualificationDescription ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(qualificationDescription)) return qualificationDescription;
            return qualificationNumber;
        }

        private static string BuildLearnerGuideCoverNqfCreditsLine(Qualification qualification)
        {
            var nqfLevel = (qualification.NqfLevel ?? string.Empty).Trim();
            var credits = (qualification.Credits ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(nqfLevel) && string.IsNullOrWhiteSpace(credits))
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(nqfLevel))
            {
                return $"{credits} Credits".Trim();
            }

            if (string.IsNullOrWhiteSpace(credits))
            {
                return $"NQF Level {nqfLevel}".Trim();
            }

            return $"NQF Level {nqfLevel} - {credits} Credits".Trim();
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

        private static ImagePartType ResolveImagePartType(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".jpg" => ImagePartType.Jpeg,
                ".jpeg" => ImagePartType.Jpeg,
                ".gif" => ImagePartType.Gif,
                ".bmp" => ImagePartType.Bmp,
                ".tif" => ImagePartType.Tiff,
                ".tiff" => ImagePartType.Tiff,
                _ => ImagePartType.Png
            };
        }

        private static Drawing BuildInlineImage(string relId, long cx, long cy, uint drawingId, string imageName)
        {
            var inline = new DW.Inline(
                new DW.Extent() { Cx = cx, Cy = cy },
                new DW.DocProperties() { Id = drawingId, Name = imageName },
                new DW.NonVisualGraphicFrameDrawingProperties(
                    new A.GraphicFrameLocks() { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties() { Id = drawingId, Name = imageName },
                                new PIC.NonVisualPictureDrawingProperties()
                            ),
                            new PIC.BlipFill(
                                new A.Blip() { Embed = relId },
                                new A.Stretch(new A.FillRectangle())
                            ),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset() { X = 0, Y = 0 },
                                    new A.Extents() { Cx = cx, Cy = cy }
                                ),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }
                            )
                        )
                    ) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }
                )
            );
            return new Drawing(inline);
        }

        private static string ConvertInstructionDirectiveToLearnerSentence(string lead)
        {
            var text = StripCurriculumCodesFromText(lead).Trim();
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            var directiveMatch = Regex.Match(
                text,
                "^(?:explain|describe|demonstrate|discuss|show|outline|identify|clarify)\\s+(?<title>.+?)(?:\\s+in\\s+detail\\b|\\s+step\\s+by\\s+step\\b|\\s+for\\s+the\\s+learner\\b|$)",
                RegexOptions.IgnoreCase);
            if (directiveMatch.Success)
            {
                var title = NormalizeDocumentText(directiveMatch.Groups["title"].Value).Trim().TrimEnd('.', ':', ';');
                if (!string.IsNullOrWhiteSpace(title))
                {
                    var sentence = title;
                    sentence = char.ToLowerInvariant(sentence[0]) + sentence.Substring(1);
                    return $"You will {sentence}.";
                }
            }

            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "this lesson develops the learner's ability to", "You will" },
                { "show learners how to apply", "You will learn how to apply" },
                { "conclude the lesson by checking", "Check that you can" },
                { "focus your study of", "Focus on" },
                { "build your understanding of", "Build your understanding of" },
                { "learn what each term means", "Learn what each term means" },
                { "study the explanation below carefully", "Study the explanation below carefully" },
                { "work through the topic in sequence", "Work through the topic in sequence" },
                { "introduce ", "Learn about " },
                { "you must understand ", "You must understand " }
            };

            foreach (var kv in replacements)
            {
                if (text.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                {
                    var rest = text.Substring(kv.Key.Length).Trim();
                    if (string.IsNullOrWhiteSpace(rest)) return kv.Value + ".";
                    rest = rest.TrimEnd('.', ':', ';');
                    return kv.Value + " " + rest + ".";
                }
            }

            var fallback = Regex.Match(text, "^(?<verb>explain|describe|demonstrate|discuss|show|outline|identify|clarify)\\s+(?<rest>.+)$", RegexOptions.IgnoreCase);
            if (fallback.Success)
            {
                var rest = NormalizeDocumentText(fallback.Groups["rest"].Value).Trim();
                if (!string.IsNullOrWhiteSpace(rest))
                {
                    rest = char.ToLowerInvariant(rest[0]) + rest.Substring(1);
                    return $"You will {rest}.";
                }
            }

            return string.Empty;
        }

        private static string SafeFileNameToken(string? value)
        {
            var raw = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw)) return "NA";
            var safe = Regex.Replace(raw, @"[^A-Za-z0-9._-]+", "_");
            safe = Regex.Replace(safe, @"_+", "_").Trim('_');
            return string.IsNullOrWhiteSpace(safe) ? "NA" : safe;
        }
    }
}
