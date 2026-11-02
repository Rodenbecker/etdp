#pragma warning disable SKEXP0010
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ETD.Api.Utils;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace ETD.Api.Services
{
    public sealed class SemanticKernelQuestionService
    {
        private readonly Kernel? _kernel;
        private readonly bool _enabled;
        private readonly string _model;

                public SemanticKernelQuestionService()
        {
            _enabled = ResolveEnabledFlag();
            _model = (Environment.GetEnvironmentVariable("SK_OPENAI_MODEL")
                ?? Environment.GetEnvironmentVariable("OPENAI_MODEL")
                ?? "gemini-3.8-flash").Trim();

            if (!_enabled)
            {
                return;
            }

            var localEndpoint = AiRuntime.GetLocalLlmEndpoint();
            var apiKey = (Environment.GetEnvironmentVariable("SK_OPENAI_API_KEY") ?? Secrets.GetOpenAIKey() ?? string.Empty).Trim();
            
            // If we have a local endpoint, we can use it even in offline mode.
            // Otherwise, we need cloud mode to be enabled.
            if (string.IsNullOrWhiteSpace(localEndpoint) && !AiRuntime.AllowOpenAi())
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(apiKey) && string.IsNullOrWhiteSpace(localEndpoint))
            {
                return;
            }

            try
            {
                var builder = Kernel.CreateBuilder();
                if (!string.IsNullOrWhiteSpace(localEndpoint))
                {
                    // Use local LLM (Gemma, etc.) via OpenAI-compatible endpoint
                    builder.AddOpenAIChatCompletion(
                        modelId: _model,
                        apiKey: string.IsNullOrWhiteSpace(apiKey) ? "no-key" : apiKey,
                        endpoint: new Uri(localEndpoint));
                }
                else
                {
                    builder.AddOpenAIChatCompletion(modelId: _model, apiKey: apiKey);
                }
                _kernel = builder.Build();
            }
            catch
            {
                _kernel = null;
            }
        }

        public bool IsAvailable() => _enabled && _kernel != null;

        public async Task<AssessmentDrivenQuestionGenerator.GeneratedQuestion?> GenerateTrueFalseQuestionAsync(
            AssessmentDrivenQuestionGenerator.LessonEvidenceItem item,
            int number,
            int marks = 1,
            int optionCount = 4,
            CancellationToken cancellationToken = default)
        {
            if (!IsAvailable()) return null;

            var prompt = BuildPrompt(
                item,
                questionType: "TrueFalse",
                optionCount: Math.Clamp(optionCount, 2, 6),
                marks: Math.Max(1, marks));

            var generated = await InvokeAndParseAsync(prompt, cancellationToken);
            if (generated == null) return null;

            generated.Number = number;
            generated.Type = "TrueFalse";
            generated.TopicCode = item.TopicCode;
            generated.TopicDescription = item.TopicDescription;
            generated.LessonPlanLabel = item.LessonPlanLabel;
            generated.AssessmentCriteriaDescription = item.AssessmentCriteriaDescription;
            generated.BundleKey = item.BundleKey;
            generated.Marks = Math.Max(1, marks);
            NormalizeGeneratedQuestion(generated, item, "TrueFalse");

            if (!ValidateTrueFalse(generated, expectedOptionCount: Math.Clamp(optionCount, 2, 6)))
            {
                return null;
            }

            return generated.ToGeneratedQuestion();
        }

        public async Task<AssessmentDrivenQuestionGenerator.GeneratedQuestion?> GenerateMultipleChoiceQuestionAsync(
            AssessmentDrivenQuestionGenerator.LessonEvidenceItem item,
            int number,
            int marks = 1,
            int distractorCount = 4,
            CancellationToken cancellationToken = default)
        {
            if (!IsAvailable()) return null;

            var optionCount = Math.Clamp(distractorCount, 2, 8) + 1;
            var prompt = BuildPrompt(
                item,
                questionType: "MultipleChoice",
                optionCount: optionCount,
                marks: Math.Max(1, marks));

            var generated = await InvokeAndParseAsync(prompt, cancellationToken);
            if (generated == null) return null;

            generated.Number = number;
            generated.Type = "MultipleChoice";
            generated.TopicCode = item.TopicCode;
            generated.TopicDescription = item.TopicDescription;
            generated.LessonPlanLabel = item.LessonPlanLabel;
            generated.AssessmentCriteriaDescription = item.AssessmentCriteriaDescription;
            generated.BundleKey = item.BundleKey;
            generated.Marks = Math.Max(1, marks);
            NormalizeGeneratedQuestion(generated, item, "MultipleChoice");

            if (!ValidateMultipleChoice(generated, expectedOptionCount: optionCount))
            {
                return null;
            }

            return generated.ToGeneratedQuestion();
        }

        private async Task<GeneratedQuestionEnvelope?> InvokeAndParseAsync(string prompt, CancellationToken cancellationToken)
        {
            if (_kernel == null) return null;

            var settings = new OpenAIPromptExecutionSettings
            {
                Temperature = 0.2,
                TopP = 0.9,
                MaxTokens = 1100
            };

            var args = new KernelArguments(settings);
            var result = await _kernel.InvokePromptAsync(prompt, args, cancellationToken: cancellationToken);
            var raw = result.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw)) return null;

            return TryParse(raw);
        }

        private static GeneratedQuestionEnvelope? TryParse(string raw)
        {
            var text = StripCodeFence(raw).Trim();
            if (string.IsNullOrWhiteSpace(text)) return null;

            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                var prompt = ReadString(root, "prompt");
                var rationale = ReadString(root, "rationale");
                var correctAnswer = ReadString(root, "correctAnswer");

                var options = new List<string>();
                if (root.TryGetProperty("options", out var optionsEl) && optionsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var option in optionsEl.EnumerateArray())
                    {
                        var v = option.GetString();
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            options.Add(v.Trim());
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(prompt) || options.Count == 0 || string.IsNullOrWhiteSpace(correctAnswer))
                {
                    return null;
                }

                return new GeneratedQuestionEnvelope
                {
                    Prompt = prompt,
                    Options = options,
                    CorrectAnswer = correctAnswer,
                    Rationale = string.IsNullOrWhiteSpace(rationale)
                        ? "Generated with Semantic Kernel using lesson evidence and pedagogy guardrails."
                        : rationale
                };
            }
            catch
            {
                return null;
            }
        }

        private static string ReadString(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var v)) return string.Empty;
            return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? string.Empty).Trim() : string.Empty;
        }

        private static string StripCodeFence(string raw)
        {
            var trimmed = raw.Trim();
            if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;

            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline < 0) return trimmed.Trim('`');

            var body = trimmed[(firstNewline + 1)..];
            var closingFence = body.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFence >= 0)
            {
                body = body[..closingFence];
            }
            return body;
        }

        private static bool ValidateTrueFalse(GeneratedQuestionEnvelope candidate, int expectedOptionCount)
        {
            if (!ValidatePedagogy(candidate)) return false;
            if (candidate.Options.Count != expectedOptionCount) return false;
            var correctKeys = ParseTrueFalseCorrectKeys(candidate.CorrectAnswer);
            if (correctKeys.Count != 1) return false;
            var key = correctKeys[0];
            return key >= 0 && key < expectedOptionCount;
        }

        private static bool ValidateMultipleChoice(GeneratedQuestionEnvelope candidate, int expectedOptionCount)
        {
            if (!ValidatePedagogy(candidate)) return false;
            if (candidate.Options.Count != expectedOptionCount) return false;
            var correctIndex = ParseOptionLabel(candidate.CorrectAnswer);
            return correctIndex >= 0 && correctIndex < expectedOptionCount;
        }

        private static void NormalizeGeneratedQuestion(
            GeneratedQuestionEnvelope candidate,
            AssessmentDrivenQuestionGenerator.LessonEvidenceItem item,
            string questionType)
        {
            var context = AssessmentDrivenQuestionGenerator.BuildLearnerContextLabel(item);
            var defaultPrompt = string.Equals(questionType, "TrueFalse", StringComparison.OrdinalIgnoreCase)
                ? $"Read each statement about {context}. Mark each statement as True or False. Only one statement is True."
                : $"Which option best reflects correct practice for {context}?";

            candidate.Prompt = AssessmentDrivenQuestionGenerator.NormalizeQuestionStem(candidate.Prompt, defaultPrompt);
            candidate.Options = candidate.Options
                .Select(o => AssessmentDrivenQuestionGenerator.NormalizeQuestionStatement(o))
                .ToList();
            candidate.Rationale = AssessmentDrivenQuestionGenerator.SanitizeQuestionText(candidate.Rationale);
        }

        private static bool ValidatePedagogy(GeneratedQuestionEnvelope candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate.Prompt)) return false;
            if (Regex.IsMatch(candidate.Prompt, @"^\s*do\s+you\s+think\b", RegexOptions.IgnoreCase)) return false;
            if (AssessmentDrivenQuestionGenerator.ContainsQuestionAdministrativeReference(candidate.Prompt)) return false;
            if (candidate.Options.Count == 0) return false;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lengths = new List<int>();
            foreach (var option in candidate.Options)
            {
                var text = (option ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(text)) return false;
                if (AssessmentDrivenQuestionGenerator.ContainsQuestionAdministrativeReference(text)) return false;
                if (text.Contains("all of the above", StringComparison.OrdinalIgnoreCase)) return false;
                if (text.Contains("none of the above", StringComparison.OrdinalIgnoreCase)) return false;
                if (!seen.Add(text)) return false;
                lengths.Add(text.Length);
            }

            if (lengths.Count >= 3)
            {
                var shortest = lengths.Min();
                var longest = lengths.Max();
                if (shortest >= 12 && longest > shortest * 3 && longest - shortest > 80)
                {
                    return false;
                }
            }

            return true;
        }

        private static List<int> ParseTrueFalseCorrectKeys(string correctAnswer)
        {
            var result = new List<int>();
            var pieces = (correctAnswer ?? string.Empty)
                .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var piece in pieces)
            {
                var row = piece.Trim();
                if (string.IsNullOrWhiteSpace(row)) continue;
                var eq = row.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0) continue;

                var label = row.Substring(0, eq).Trim();
                var tf = row[(eq + 1)..].Trim();
                if (!tf.Equals("True", StringComparison.OrdinalIgnoreCase)) continue;

                var index = ParseOptionLabel(label);
                if (index >= 0) result.Add(index);
            }
            return result;
        }

        private static int ParseOptionLabel(string value)
        {
            var v = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(v)) return -1;
            var c = char.ToUpperInvariant(v[0]);
            if (c < 'A' || c > 'Z') return -1;
            return c - 'A';
        }

        private static string BuildPrompt(
            AssessmentDrivenQuestionGenerator.LessonEvidenceItem item,
            string questionType,
            int optionCount,
            int marks)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a strict TVET assessment designer for South African occupational training.");
            sb.AppendLine("Create exactly one assessment item and return ONLY raw JSON (no markdown, no explanation).");
            sb.AppendLine();
            sb.AppendLine("JSON schema:");
            sb.AppendLine("{");
            sb.AppendLine("  \"prompt\": \"string\",");
            sb.AppendLine("  \"options\": [\"string\"],");
            sb.AppendLine("  \"correctAnswer\": \"string\",");
            sb.AppendLine("  \"rationale\": \"string\"");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"QuestionType: {questionType}");
            sb.AppendLine($"OptionCount: {optionCount}");
            sb.AppendLine($"Marks: {marks}");
            sb.AppendLine("Rules:");
            sb.AppendLine("- The stem must be self-contained and clear before learners read options.");
            sb.AppendLine("- Use professional assessment language; do not start with conversational phrasing like \"Do you think\".");
            sb.AppendLine("- Prefer application, analysis, or evaluation wording where appropriate.");
            sb.AppendLine("- Do NOT mention assessment criteria labels, criterion IDs, topic codes, or codes such as AC01, KG01, or LPN1 in the stem or options.");
            sb.AppendLine("- Use plausible near-miss distractors that reflect common learner misconceptions.");
            sb.AppendLine("- Keep options homogeneous in style and roughly similar length.");
            sb.AppendLine("- Do not use \"All of the above\" or \"None of the above\".");
            sb.AppendLine("- Avoid negative stems such as \"Which is NOT...\".");
            sb.AppendLine("- Use trade-specific or workplace context from the evidence when available, such as tools, components, measurements, safety controls, inspection steps, or reporting decisions.");
            sb.AppendLine("- The correct answer must not be noticeably longer, more detailed, or more formal than the distractors.");
            sb.AppendLine("- Each wrong option must be a plausible near-miss, not a silly answer, and should reflect a realistic misconception, shortcut, wrong unit, wrong sequence, wrong tool, or safety mistake.");
            if (string.Equals(questionType, "TrueFalse", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("- Build one self-contained factual statement that can be marked True or False without reading any other option.");
                sb.AppendLine("- options must be exactly [\"True\", \"False\"].");
                sb.AppendLine("- correctAnswer format must be exactly \"True\" or \"False\".");
                sb.AppendLine("- Avoid statements that begin with \"It is incorrect that\" or \"It is false that\"; alter the technical detail itself instead.");
            }
            else
            {
                sb.AppendLine("- Build one stem and exactly OptionCount options.");
                sb.AppendLine("- Exactly one option must be correct.");
                sb.AppendLine("- Distractors must be credible alternatives a learner might select.");
                sb.AppendLine("- correctAnswer format must be the option letter only, for example: \"B\".");
            }
            sb.AppendLine("- Keep wording clear and measurable.");
            sb.AppendLine();
            sb.AppendLine("Context:");
            sb.AppendLine($"TopicFocus: {CleanPromptField(AssessmentDrivenQuestionGenerator.BuildLearnerContextLabel(item))}");
            sb.AppendLine($"TopicDescription: {CleanPromptField(item.TopicDescription)}");
            sb.AppendLine($"LessonPlanLabel: {CleanPromptField(item.LessonPlanLabel)}");
            sb.AppendLine($"LessonPlanDescription: {CleanPromptField(item.LessonPlanDescription)}");
            sb.AppendLine($"LearningRequirement: {CleanPromptField(AssessmentDrivenQuestionGenerator.SanitizeQuestionText(item.AssessmentCriteriaDescription))}");
            sb.AppendLine($"EvidenceText: {CleanPromptField(TrimForPrompt(item.EvidenceText, 1500))}");
            return sb.ToString();
        }

        private static string CleanPromptField(string? value)
            => (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();

        private static string TrimForPrompt(string? value, int maxChars)
        {
            var clean = CleanPromptField(value);
            if (clean.Length <= maxChars) return clean;
            return clean[..maxChars];
        }

        private static bool ResolveEnabledFlag()
        {
            var explicitSetting = (Environment.GetEnvironmentVariable("ETDP_USE_SEMANTIC_KERNEL_QUESTIONS") ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(explicitSetting))
            {
                var v = explicitSetting.ToLowerInvariant();
                return v == "1" || v == "true" || v == "yes" || v == "on";
            }

            // Default on in this build; set ETDP_USE_SEMANTIC_KERNEL_QUESTIONS=false to disable.
            return true;
        }

        private sealed class GeneratedQuestionEnvelope
        {
            public int Number { get; set; }
            public string Type { get; set; } = string.Empty;
            public string Prompt { get; set; } = string.Empty;
            public List<string> Options { get; set; } = new();
            public string CorrectAnswer { get; set; } = string.Empty;
            public string TopicCode { get; set; } = string.Empty;
            public string TopicDescription { get; set; } = string.Empty;
            public string LessonPlanLabel { get; set; } = string.Empty;
            public string AssessmentCriteriaDescription { get; set; } = string.Empty;
            public string Rationale { get; set; } = string.Empty;
            public int Marks { get; set; }
            public string BundleKey { get; set; } = string.Empty;

            public AssessmentDrivenQuestionGenerator.GeneratedQuestion ToGeneratedQuestion()
            {
                return new AssessmentDrivenQuestionGenerator.GeneratedQuestion
                {
                    Number = Number,
                    Type = Type,
                    Prompt = Prompt,
                    Options = Options,
                    CorrectAnswer = CorrectAnswer,
                    TopicCode = TopicCode,
                    TopicDescription = TopicDescription,
                    LessonPlanLabel = LessonPlanLabel,
                    AssessmentCriteriaDescription = AssessmentCriteriaDescription,
                    Rationale = Rationale,
                    Marks = Marks,
                    BundleKey = BundleKey
                };
            }
        }
    }
}



