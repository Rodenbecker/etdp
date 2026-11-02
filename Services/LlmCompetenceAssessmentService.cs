using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ETD.Api.Data;
using ETD.Api.Utils;
using Microsoft.EntityFrameworkCore;

namespace ETD.Api.Services
{
    public sealed class LlmCompetenceAssessmentService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
        private readonly ApplicationDbContext _context;
        private readonly ILogger<LlmCompetenceAssessmentService> _logger;

        public LlmCompetenceAssessmentService(ApplicationDbContext context, ILogger<LlmCompetenceAssessmentService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<LlmCompetenceReport> RunAsync(LlmCompetenceRunRequest request, CancellationToken ct)
        {
            request ??= new LlmCompetenceRunRequest();
            var maxTopics = Math.Clamp(request.MaxTopics, 1, 50);
            var topicQuery = _context.Topics
                .AsNoTracking()
                .Include(x => x.Subject)
                .Where(x => x.Subject != null);

            var requestedTopicId = request.TopicId.GetValueOrDefault();
            var requestedSubjectId = request.SubjectId.GetValueOrDefault();
            var requestedQualificationId = request.QualificationId.GetValueOrDefault();
            if (requestedTopicId > 0)
            {
                topicQuery = topicQuery.Where(x => x.Id == requestedTopicId);
            }
            else if (requestedSubjectId > 0)
            {
                topicQuery = topicQuery.Where(x => x.SubjectId == requestedSubjectId);
            }
            else if (requestedQualificationId > 0)
            {
                var qid = requestedQualificationId;
                topicQuery = topicQuery.Where(x => x.Subject != null && x.Subject.QualificationId == qid);
            }

            var topics = await topicQuery
                .OrderBy(x => x.Subject!.SubjectCode)
                .ThenBy(x => x.Order)
                .ThenBy(x => x.TopicCode)
                .Take(maxTopics)
                .Select(x => new TopicProbe
                {
                    TopicId = x.Id,
                    TopicCode = x.TopicCode,
                    TopicDescription = x.TopicDescription ?? string.Empty,
                    SubjectId = x.SubjectId,
                    SubjectCode = x.Subject == null ? string.Empty : x.Subject.SubjectCode,
                    SubjectDescription = x.Subject == null ? string.Empty : x.Subject.SubjectDescription,
                    QualificationId = x.Subject == null ? 0 : x.Subject.QualificationId
                })
                .ToListAsync(ct);

            var report = new LlmCompetenceReport
            {
                Id = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"),
                CreatedAtUtc = DateTime.UtcNow,
                QualificationId = request.QualificationId,
                SubjectId = request.SubjectId,
                TopicId = request.TopicId,
                MaxTopics = maxTopics,
                UseLlm = request.UseLlm,
                Rubric = LlmCompetenceRubric.Default()
            };

            foreach (var topic in topics)
            {
                ct.ThrowIfCancellationRequested();
                report.Results.Add(await AssessTopicAsync(topic, request, ct));
            }

            report.TopicCount = report.Results.Count;
            report.PassedCount = report.Results.Count(x => x.Passed);
            report.AverageScore = report.Results.Count == 0 ? 0 : (int)Math.Round(report.Results.Average(x => x.TotalScore));
            report.PassRate = report.TopicCount == 0 ? 0 : (int)Math.Round(report.PassedCount * 100.0 / report.TopicCount);
            report.ReportPath = await SaveReportAsync(report, ct);
            return report;
        }

        public async Task<LlmCompetenceReport?> GetLatestAsync(CancellationToken ct)
        {
            var root = GetReportRoot();
            if (!Directory.Exists(root)) return null;
            var latest = Directory.EnumerateFiles(root, "llm-competence-*.json")
                .OrderByDescending(x => x)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(latest)) return null;
            await using var stream = File.OpenRead(latest);
            return await JsonSerializer.DeserializeAsync<LlmCompetenceReport>(stream, JsonOptions, ct);
        }

        public async Task<List<LlmCompetenceTopicOption>> GetTopicOptionsAsync(int? qualificationId, int take, CancellationToken ct)
        {
            var query = _context.Topics.AsNoTracking().Include(x => x.Subject).Where(x => x.Subject != null);
            var requestedQualificationId = qualificationId.GetValueOrDefault();
            if (requestedQualificationId > 0)
            {
                var qid = requestedQualificationId;
                query = query.Where(x => x.Subject != null && x.Subject.QualificationId == qid);
            }

            return await query
                .OrderBy(x => x.Subject!.SubjectCode)
                .ThenBy(x => x.Order)
                .ThenBy(x => x.TopicCode)
                .Take(Math.Clamp(take, 1, 500))
                .Select(x => new LlmCompetenceTopicOption
                {
                    TopicId = x.Id,
                    TopicCode = x.TopicCode,
                    TopicDescription = x.TopicDescription ?? string.Empty,
                    SubjectId = x.SubjectId,
                    SubjectCode = x.Subject == null ? string.Empty : x.Subject.SubjectCode,
                    SubjectDescription = x.Subject == null ? string.Empty : x.Subject.SubjectDescription
                })
                .ToListAsync(ct);
        }

        private async Task<LlmCompetenceTopicResult> AssessTopicAsync(TopicProbe topic, LlmCompetenceRunRequest request, CancellationToken ct)
        {
            var evidence = await RetrieveEvidenceAsync(topic, ct);
            var answer = request.UseLlm
                ? await TryGenerateLlmAnswerAsync(topic, evidence, ct)
                : LlmAnswerResult.Skipped("LLM answer generation was disabled for this run.");

            if (string.IsNullOrWhiteSpace(answer.Answer))
            {
                answer = LlmAnswerResult.Skipped(answer.ErrorMessage);
            }

            var grade = Grade(topic, evidence, answer.Answer);
            return new LlmCompetenceTopicResult
            {
                TopicId = topic.TopicId,
                TopicCode = topic.TopicCode,
                TopicDescription = topic.TopicDescription,
                SubjectCode = topic.SubjectCode,
                SubjectDescription = topic.SubjectDescription,
                EvidenceCount = evidence.Count,
                EvidenceTitles = evidence.Select(x => x.Title).Take(5).ToList(),
                Evidence = evidence.Select(x => new LlmCompetenceEvidenceSummary
                {
                    Id = x.Id,
                    Title = x.Title,
                    SourceType = x.SourceType,
                    Score = x.Score,
                    Snippet = Trim(x.Snippet, 420)
                }).Take(6).ToList(),
                AnswerSource = answer.Source,
                AnswerPreview = Trim(answer.Answer, 1200),
                RetrievalScore = grade.Retrieval,
                CorrectnessScore = grade.Correctness,
                CoverageScore = grade.Coverage,
                GroundingScore = grade.Grounding,
                TeachingScore = grade.Teaching,
                TotalScore = grade.Total,
                Passed = grade.Total >= 80 && grade.Retrieval >= 20 && grade.Correctness >= 24 && grade.Grounding >= 12,
                Findings = grade.Findings.Concat(string.IsNullOrWhiteSpace(answer.ErrorMessage) ? Array.Empty<string>() : new[] { answer.ErrorMessage }).ToList()
            };
        }

        private async Task<List<EvidenceHit>> RetrieveEvidenceAsync(TopicProbe topic, CancellationToken ct)
        {
            var terms = Tokenize($"{topic.SubjectCode} {topic.SubjectDescription} {topic.TopicCode} {topic.TopicDescription}")
                .Where(x => x.Length >= 4)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(16)
                .ToList();

            var rows = await _context.SourceMaterials.AsNoTracking()
                .Where(x => !string.IsNullOrWhiteSpace(x.ExtractedText))
                .OrderByDescending(x => x.KnowledgeUploadedAtUtc ?? x.CreatedAt)
                .Take(900)
                .Select(x => new
                {
                    x.Id,
                    x.Title,
                    x.KnowledgeSourceType,
                    x.SubjectDescription,
                    x.TopicDescription,
                    x.AssessmentCriteriaDescription,
                    x.ExtractedText
                })
                .ToListAsync(ct);

            return rows
                .Select(row =>
                {
                    var metadata = $"{row.Title} {row.SubjectDescription} {row.TopicDescription} {row.AssessmentCriteriaDescription}";
                    var body = row.ExtractedText ?? string.Empty;
                    var score = terms.Sum(term =>
                        Contains(metadata, term) ? 8 :
                        Contains(body, term) ? 2 : 0);
                    return new EvidenceHit
                    {
                        Id = row.Id,
                        Title = row.Title ?? string.Empty,
                        SourceType = row.KnowledgeSourceType ?? string.Empty,
                        Score = score,
                        Snippet = BuildSnippet(body, terms)
                    };
                })
                .Where(x => x.Score > 0 && !string.IsNullOrWhiteSpace(x.Snippet))
                .OrderByDescending(x => x.Score)
                .Take(6)
                .ToList();
        }

        private async Task<LlmAnswerResult> TryGenerateLlmAnswerAsync(TopicProbe topic, List<EvidenceHit> evidence, CancellationToken ct)
        {
            if (evidence.Count == 0)
            {
                return LlmAnswerResult.Skipped("No evidence was retrieved, so LLM competence cannot be assessed for this topic.");
            }

            var attempts = new List<string>();
            var prompt = BuildPrompt(topic, evidence);
            var localEndpoint = AiRuntime.GetLocalLlmEndpoint();
            if (!string.IsNullOrWhiteSpace(localEndpoint))
            {
                var local = await TryLocalChatAsync(localEndpoint, prompt, ct);
                if (!string.IsNullOrWhiteSpace(local.Answer)) return local;
                attempts.Add(local.ErrorMessage);
            }

            if (AiRuntime.AllowGoogle())
            {
                try
                {
                    var googleAnswer = await GoogleGeminiClient.GenerateContentAsync(prompt, ct: ct);
                    if (!string.IsNullOrWhiteSpace(googleAnswer))
                    {
                        return new LlmAnswerResult { Source = $"gemini:{AiRuntime.GetGeminiModel()}", Answer = googleAnswer.Trim() };
                    }
                }
                catch { }
            }

            var openAiKey = Secrets.GetOpenAIKey();
            if (AiRuntime.AllowOpenAi() && !string.IsNullOrWhiteSpace(openAiKey))
            {
                var cloud = await TryOpenAiChatAsync(prompt, openAiKey, ct);
                if (!string.IsNullOrWhiteSpace(cloud.Answer)) return cloud;
                attempts.Add(cloud.ErrorMessage);
            }
            else if (!AiRuntime.AllowOpenAi())
            {
                attempts.Add("OpenAI was not attempted because AI mode does not allow cloud fallback.");
            }
            else
            {
                attempts.Add("OpenAI was not attempted because no OpenAI API key was available to the assessment service.");
            }

            var detail = string.Join(" ", attempts.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
            return LlmAnswerResult.Skipped($"No configured LLM endpoint produced an answer. {detail}".Trim());
        }

        private static string BuildPrompt(TopicProbe topic, List<EvidenceHit> evidence)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are being tested for subject competence. Write full learner-guide instructional prose, not a syllabus list.");
            sb.AppendLine($"Subject: {topic.SubjectCode} {topic.SubjectDescription}");
            sb.AppendLine($"Topic: {topic.TopicCode} {topic.TopicDescription}");
            sb.AppendLine("Required: concept explanation, key terms with real definitions, how it works, worked example, common mistakes, and check-for-understanding questions.");
            sb.AppendLine("Use only the reference evidence below.");
            sb.AppendLine();
            foreach (var hit in evidence.Take(5))
            {
                sb.AppendLine($"[Evidence {hit.Id}: {hit.Title}]");
                sb.AppendLine(hit.Snippet);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private async Task<LlmAnswerResult> TryLocalChatAsync(string endpoint, string prompt, CancellationToken ct)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
                var payload = new
                {
                    model = AiRuntime.GetLocalLlmModel(),
                    stream = false,
                    messages = new[]
                    {
                        new { role = "system", content = "You are an expert vocational textbook author." },
                        new { role = "user", content = prompt }
                    }
                };
                using var response = await client.PostAsync(endpoint, Json(payload), timeoutCts.Token);
                var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    return LlmAnswerResult.Skipped($"Local LLM failed: HTTP {(int)response.StatusCode}.");
                }
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var answer = root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content)
                    ? content.GetString()
                    : root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                        ? choices[0].GetProperty("message").GetProperty("content").GetString()
                        : string.Empty;
                return new LlmAnswerResult { Source = $"local:{AiRuntime.GetLocalLlmModel()}", Answer = answer ?? string.Empty };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Local LLM competence answer generation failed.");
                return LlmAnswerResult.Skipped($"Local LLM failed: {ex.Message}");
            }
        }

        private async Task<LlmAnswerResult> TryOpenAiChatAsync(string prompt, string apiKey, CancellationToken ct)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                var payload = new
                {
                    model = AiRuntime.GetOpenAiModel("gpt-5-mini"),
                    messages = new[]
                    {
                        new { role = "system", content = "You are an expert vocational textbook author." },
                        new { role = "user", content = prompt }
                    },
                    temperature = 0.2
                };
                using var response = await client.PostAsync("https://api.openai.com/v1/chat/completions", Json(payload), ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    return LlmAnswerResult.Skipped($"OpenAI failed: HTTP {(int)response.StatusCode}.");
                }
                using var doc = JsonDocument.Parse(body);
                var answer = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
                return new LlmAnswerResult { Source = $"openai:{AiRuntime.GetOpenAiModel()}", Answer = answer };
            }
            catch (Exception ex)
            {
                return LlmAnswerResult.Skipped($"OpenAI failed: {ex.Message}");
            }
        }

        private static ScoreBreakdown Grade(TopicProbe topic, List<EvidenceHit> evidence, string answer)
        {
            var findings = new List<string>();
            var answerTokens = Tokenize(answer).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var topicTokens = Tokenize($"{topic.SubjectDescription} {topic.TopicDescription}").Where(x => x.Length >= 4).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var evidenceTokens = Tokenize(string.Join(" ", evidence.Select(x => x.Snippet))).Where(x => x.Length >= 5).Distinct(StringComparer.OrdinalIgnoreCase).Take(80).ToList();

            var retrieval = Math.Min(25, evidence.Count * 4 + Math.Min(5, evidence.Sum(x => x.Score) / 20));
            if (retrieval < 20) findings.Add("Retrieved evidence is thin or weakly matched.");

            var requiredHitRate = topicTokens.Count == 0 ? 0 : topicTokens.Count(t => answerTokens.Contains(t)) * 1.0 / topicTokens.Count;
            var correctness = Math.Min(30, (int)Math.Round(requiredHitRate * 18) + Math.Min(12, evidenceTokens.Count(t => answerTokens.Contains(t)) / 2));
            if (correctness < 24) findings.Add("Answer does not cover enough topic/evidence concepts.");

            var lower = answer.ToLowerInvariant();
            var requiredSections = new[] { "concept", "key term", "works", "example", "mistake", "understanding" };
            var sectionHits = requiredSections.Count(x => lower.Contains(x));
            var coverage = Math.Min(20, sectionHits * 2 + Math.Min(8, answer.Length / 700));
            if (coverage < 14) findings.Add("Answer is not deep or structured enough for learner-guide use.");

            var grounding = Math.Min(15, evidenceTokens.Count == 0 ? 0 : (int)Math.Round(evidenceTokens.Count(t => answerTokens.Contains(t)) * 15.0 / Math.Min(30, evidenceTokens.Count)));
            if (grounding < 12) findings.Add("Answer is not strongly grounded in retrieved evidence.");

            var teaching = 0;
            if (answer.Length >= 1200) teaching += 3;
            if (Regex.IsMatch(answer, @"\bfor example\b|\bscenario\b|\bcase\b", RegexOptions.IgnoreCase)) teaching += 3;
            if (Regex.IsMatch(answer, @"\bdefine|means|refers to\b", RegexOptions.IgnoreCase)) teaching += 2;
            if (Regex.IsMatch(answer, @"\bwhy\b|\bbecause\b|\btherefore\b", RegexOptions.IgnoreCase)) teaching += 2;
            teaching = Math.Min(10, teaching);
            if (teaching < 7) findings.Add("Teaching quality is below the expected textbook-author standard.");

            if (topic.SubjectDescription.Contains("diesel", StringComparison.OrdinalIgnoreCase) &&
                answer.Contains("fitter and turner", StringComparison.OrdinalIgnoreCase))
            {
                correctness = Math.Min(correctness, 10);
                findings.Add("Detected possible cross-qualification contamination: Fitter and Turner appears in a Diesel topic answer.");
            }

            return new ScoreBreakdown(retrieval, correctness, coverage, grounding, teaching, findings);
        }

        private async Task<string> SaveReportAsync(LlmCompetenceReport report, CancellationToken ct)
        {
            var root = GetReportRoot();
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, $"llm-competence-{report.Id}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonOptions), ct);
            return path;
        }

        private static string GetReportRoot() => Path.Combine(EtdpPaths.GetExportsRoot(), "CompetenceAssessments");
        private static StringContent Json(object payload) => new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        private static bool Contains(string? haystack, string needle) => (haystack ?? string.Empty).Contains(needle, StringComparison.OrdinalIgnoreCase);
        private static string Trim(string value, int max) => string.IsNullOrEmpty(value) || value.Length <= max ? value ?? string.Empty : value[..max];

        private static string BuildSnippet(string text, List<string> terms)
        {
            var clean = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
            if (clean.Length == 0) return string.Empty;
            var index = terms.Select(t => clean.IndexOf(t, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
            var start = Math.Max(0, index - 350);
            var len = Math.Min(1800, clean.Length - start);
            return clean.Substring(start, len);
        }

        private static IEnumerable<string> Tokenize(string value)
        {
            var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the", "and", "that", "this", "with", "from", "their", "topic", "subject", "describe", "explain", "role", "importance"
            };
            return Regex.Matches(value ?? string.Empty, @"\b[A-Za-z][A-Za-z0-9-]{2,}\b")
                .Select(x => x.Value.Trim().ToLowerInvariant())
                .Where(x => !stop.Contains(x));
        }

        private sealed record ScoreBreakdown(int Retrieval, int Correctness, int Coverage, int Grounding, int Teaching, List<string> Findings)
        {
            public int Total => Retrieval + Correctness + Coverage + Grounding + Teaching;
        }

        private sealed class TopicProbe
        {
            public int TopicId { get; set; }
            public string TopicCode { get; set; } = string.Empty;
            public string TopicDescription { get; set; } = string.Empty;
            public int SubjectId { get; set; }
            public string SubjectCode { get; set; } = string.Empty;
            public string SubjectDescription { get; set; } = string.Empty;
            public int QualificationId { get; set; }
        }

        private sealed class EvidenceHit
        {
            public int Id { get; set; }
            public string Title { get; set; } = string.Empty;
            public string SourceType { get; set; } = string.Empty;
            public int Score { get; set; }
            public string Snippet { get; set; } = string.Empty;
        }

        private sealed class LlmAnswerResult
        {
            public string Source { get; set; } = string.Empty;
            public string Answer { get; set; } = string.Empty;
            public string ErrorMessage { get; set; } = string.Empty;
            public static LlmAnswerResult Skipped(string message) => new() { Source = "not-generated", ErrorMessage = message };
        }
    }

    public sealed class LlmCompetenceRunRequest
    {
        public int? QualificationId { get; set; }
        public int? SubjectId { get; set; }
        public int? TopicId { get; set; }
        public int MaxTopics { get; set; } = 10;
        public bool UseLlm { get; set; } = true;
    }

    public sealed class LlmCompetenceReport
    {
        public string Id { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public int? QualificationId { get; set; }
        public int? SubjectId { get; set; }
        public int? TopicId { get; set; }
        public int MaxTopics { get; set; }
        public bool UseLlm { get; set; }
        public LlmCompetenceRubric Rubric { get; set; } = LlmCompetenceRubric.Default();
        public int TopicCount { get; set; }
        public int PassedCount { get; set; }
        public int AverageScore { get; set; }
        public int PassRate { get; set; }
        public string ReportPath { get; set; } = string.Empty;
        public List<LlmCompetenceTopicResult> Results { get; set; } = new();
    }

    public sealed class LlmCompetenceTopicResult
    {
        public int TopicId { get; set; }
        public string TopicCode { get; set; } = string.Empty;
        public string TopicDescription { get; set; } = string.Empty;
        public string SubjectCode { get; set; } = string.Empty;
        public string SubjectDescription { get; set; } = string.Empty;
        public int EvidenceCount { get; set; }
        public List<string> EvidenceTitles { get; set; } = new();
        public List<LlmCompetenceEvidenceSummary> Evidence { get; set; } = new();
        public string AnswerSource { get; set; } = string.Empty;
        public string AnswerPreview { get; set; } = string.Empty;
        public int RetrievalScore { get; set; }
        public int CorrectnessScore { get; set; }
        public int CoverageScore { get; set; }
        public int GroundingScore { get; set; }
        public int TeachingScore { get; set; }
        public int TotalScore { get; set; }
        public bool Passed { get; set; }
        public List<string> Findings { get; set; } = new();
    }

    public sealed class LlmCompetenceEvidenceSummary
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string SourceType { get; set; } = string.Empty;
        public int Score { get; set; }
        public string Snippet { get; set; } = string.Empty;
    }

    public sealed class LlmCompetenceTopicOption
    {
        public int TopicId { get; set; }
        public string TopicCode { get; set; } = string.Empty;
        public string TopicDescription { get; set; } = string.Empty;
        public int SubjectId { get; set; }
        public string SubjectCode { get; set; } = string.Empty;
        public string SubjectDescription { get; set; } = string.Empty;
    }

    public sealed class LlmCompetenceRubric
    {
        public int RetrievalRelevance { get; set; } = 25;
        public int ContentCorrectness { get; set; } = 30;
        public int CoverageDepth { get; set; } = 20;
        public int Grounding { get; set; } = 15;
        public int TeachingQuality { get; set; } = 10;
        public int PassScore { get; set; } = 80;
        public string PassRule { get; set; } = "Pass requires total >= 80, retrieval >= 20, correctness >= 24, and grounding >= 12.";
        public static LlmCompetenceRubric Default() => new();
    }
}
