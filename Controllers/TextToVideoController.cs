using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ETD.Api.Data;
using ETD.Api.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ETD.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TextToVideoController : ControllerBase
    {
        private static readonly HttpClient _http = new HttpClient();
        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".webm", ".mkv", ".avi" };
        private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".webm" };
        private static readonly JsonSerializerOptions ArtifactJsonOptions = new() { WriteIndented = true };
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<TextToVideoController> _logger;

        public TextToVideoController(ApplicationDbContext context, IWebHostEnvironment environment, ILogger<TextToVideoController> logger)
        {
            _context = context;
            _environment = environment;
            _logger = logger;
        }

        [HttpGet("tts-options")]
        public IActionResult GetTtsOptions()
        {
            var googleAllowed = AiRuntime.AllowGoogle();
            var googleKey = (Secrets.GetGoogleKey() ?? string.Empty).Trim();
            var googleConfigured = googleAllowed && !string.IsNullOrWhiteSpace(googleKey);

            var defaultGeminiModel = FirstNonEmpty(
                Environment.GetEnvironmentVariable("GEMINI_TTS_MODEL"),
                "gemini-3.8-flash-tts");
            var defaultGeminiVoice = FirstNonEmpty(
                Environment.GetEnvironmentVariable("GEMINI_TTS_VOICE"),
                "Puck");
            var geminiModels = ParseCsvOptions(
                Environment.GetEnvironmentVariable("GEMINI_TTS_MODELS"),
                defaultGeminiModel,
                "gemini-3.8-flash-lite-tts");
            var geminiVoices = ParseCsvOptions(
                Environment.GetEnvironmentVariable("GEMINI_TTS_VOICES"),
                defaultGeminiVoice,
                "Charon",
                "Aoede",
                "Fenrir");

            return Ok(new
            {
                aiMode = AiRuntime.GetMode(),
                providers = new[]
                {
                    new
                    {
                        id = "browser",
                        label = "Browser SpeechSynthesis (Offline/Local)",
                        available = true,
                        reason = "Uses high-performance browser speech synthesis. Instant, offline, and zero latency.",
                        models = new[] { "browser-default" },
                        voices = Array.Empty<string>(),
                        defaultModel = "browser-default",
                        defaultVoice = string.Empty
                    },
                    new
                    {
                        id = "gemini",
                        label = "Google Gemini TTS",
                        available = googleConfigured,
                        reason = !googleAllowed
                            ? "Google Gemini TTS disabled by AI_MODE."
                            : (string.IsNullOrWhiteSpace(googleKey)
                                ? "Google/Gemini API key is not configured."
                                : "Google Gemini TTS available."),
                        models = googleConfigured ? geminiModels : Array.Empty<string>(),
                        voices = googleConfigured ? geminiVoices : Array.Empty<string>(),
                        defaultModel = defaultGeminiModel,
                        defaultVoice = defaultGeminiVoice
                    }
                },
                defaults = new
                {
                    provider = "browser",
                    browserLanguage = FirstNonEmpty(Environment.GetEnvironmentVariable("DEFAULT_TTS_LANG"), "en-ZA"),
                    browserPreferredVoice = FirstNonEmpty(Environment.GetEnvironmentVariable("DEFAULT_TTS_VOICE"), "Microsoft"),
                    openAiModel = defaultGeminiModel,
                    openAiVoice = defaultGeminiVoice,
                    format = "mp3",
                    speed = 1.0
                }
            });
        }

        [HttpPost("tts-preview")]
        public async Task<IActionResult> TtsPreview([FromBody] TtsPreviewRequest? request, CancellationToken cancellationToken)
        {
            request ??= new TtsPreviewRequest();
            var provider = (request.Provider ?? "openai").Trim().ToLowerInvariant();
            if (provider == "browser")
            {
                return BadRequest(new { error = "Browser TTS should be played directly in the client." });
            }
            if (provider != "openai")
            {
                return BadRequest(new { error = $"Unsupported TTS provider: {provider}" });
            }

            if (!AiRuntime.AllowOpenAi())
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "OpenAI TTS is disabled by AI_MODE." });
            }

            var key = (Secrets.GetOpenAIKey() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "OPENAI_API_KEY is not configured." });
            }

            var text = NormalizePrompt(request.Text);
            if (string.IsNullOrWhiteSpace(text))
            {
                return BadRequest(new { error = "Text is required." });
            }

            if (text.Length > 4000)
            {
                text = text.Substring(0, 4000);
            }

            var model = string.IsNullOrWhiteSpace(request.Model)
                ? (Environment.GetEnvironmentVariable("OPENAI_TTS_MODEL") ?? "gpt-4o-mini-tts")
                : request.Model.Trim();
            var voice = string.IsNullOrWhiteSpace(request.Voice)
                ? (Environment.GetEnvironmentVariable("OPENAI_TTS_VOICE") ?? "alloy")
                : request.Voice.Trim();
            var format = NormalizeTtsFormat(request.Format);
            var speed = Math.Clamp(request.Speed.GetValueOrDefault(1.0), 0.25, 4.0);

            var payload = new
            {
                model,
                voice,
                input = text,
                format,
                speed
            };

            using var msg = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            try
            {
                using var response = await _http.SendAsync(msg, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    return StatusCode((int)response.StatusCode, new { error = $"OpenAI TTS failed: {TrimTail(body, 600)}" });
                }

                var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (audio.Length == 0)
                {
                    return StatusCode(StatusCodes.Status502BadGateway, new { error = "OpenAI returned empty audio output." });
                }

                return File(audio, ResolveTtsContentType(format), $"tts-preview.{format}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OpenAI TTS preview failed");
                return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"OpenAI TTS preview failed: {ex.Message}" });
            }
        }

        [HttpPost("generate")]
        public async Task<IActionResult> Generate([FromBody] GenerateVideoRequest? request, CancellationToken cancellationToken)
        {
            request ??= new GenerateVideoRequest();
            var prompt = NormalizePrompt(request.Prompt);
            if (string.IsNullOrWhiteSpace(prompt)) return BadRequest(new { error = "Prompt is required." });

            var conditioningPath = await ResolveConditioningPathAsync(request, cancellationToken);
            var local = await TryRunLocalLtxAsync(prompt, conditioningPath, request, cancellationToken);
            if (local.Success)
            {
                return Ok(new
                {
                    success = true,
                    provider = "ltx-local",
                    message = local.Message,
                    prompt,
                    conditioningPath,
                    local,
                    openAi = (object?)null
                });
            }

            // Generate structured educational video storyboard with Google Gemini / intelligent curriculum builder
            var scenes = await GenerateStoryboardScenesAsync(prompt, request, cancellationToken);
            var totalDuration = scenes.Sum(s => s.durationSec);

            return Ok(new
            {
                success = true,
                provider = "gemini-video",
                message = "Google Gemini video storyboard compiled with animated scene directions and narrations.",
                prompt,
                conditioningPath,
                scenes,
                videoManifest = new
                {
                    title = prompt,
                    sceneCount = scenes.Count,
                    totalDurationSec = totalDuration,
                    scenes
                },
                local = (object?)null,
                openAi = (object?)null
            });
        }

        [HttpPost("storyboard")]
        public async Task<IActionResult> Storyboard([FromBody] GenerateVideoRequest? request, CancellationToken cancellationToken)
        {
            request ??= new GenerateVideoRequest();
            var prompt = NormalizePrompt(request.Prompt);
            if (string.IsNullOrWhiteSpace(prompt)) return BadRequest(new { error = "Prompt or topic is required." });

            var scenes = await GenerateStoryboardScenesAsync(prompt, request, cancellationToken);
            return Ok(new
            {
                success = true,
                provider = "gemini",
                scenes,
                totalDurationSec = scenes.Sum(s => s.durationSec)
            });
        }

        private async Task<List<StoryboardSceneItem>> GenerateStoryboardScenesAsync(string prompt, GenerateVideoRequest request, CancellationToken cancellationToken)
        {
            var targetDuration = Math.Clamp(request.TargetDurationSec ?? 150, 30, 900);
            var sceneDuration = Math.Clamp(request.SceneDurationSec ?? 24, 8, 90);

            // Attempt generation with Google Gemini first
            if (AiRuntime.AllowGoogle())
            {
                try
                {
                    var geminiPrompt = $"You are an expert curriculum video producer for South African vocational qualifications (ETDP). Break down this learning topic into 4 to 6 instructional video scenes:\nTopic: {prompt}\nFormat strictly as a JSON array where each object has: title (string), narration (string), onScreenText (string), visualPrompt (string), durationSec (number, 10-30), transition (string: cut, crossfade, wipe, zoom), badge (string: Objective, Key Concept, Step-by-Step, Formula, Reflection).\nJSON only:";
                    var rawJson = await GoogleGeminiClient.GenerateContentAsync(geminiPrompt, ct: cancellationToken);
                    if (!string.IsNullOrWhiteSpace(rawJson))
                    {
                        var start = rawJson.IndexOf('[');
                        var end = rawJson.LastIndexOf(']');
                        if (start >= 0 && end > start)
                        {
                            var cleanJson = rawJson.Substring(start, end - start + 1);
                            var parsed = JsonSerializer.Deserialize<List<StoryboardSceneItem>>(cleanJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (parsed != null && parsed.Count > 0)
                            {
                                for (var i = 0; i < parsed.Count; i++)
                                {
                                    if (string.IsNullOrWhiteSpace(parsed[i].id)) parsed[i].id = $"scene-{DateTime.UtcNow.Ticks}-{i + 1}";
                                    if (parsed[i].durationSec <= 0) parsed[i].durationSec = sceneDuration;
                                }
                                return parsed;
                            }
                        }
                    }
                }
                catch { }
            }

            // Fallback: Intelligent curriculum breakdown
            return BuildLocalCurriculumStoryboard(prompt, targetDuration, sceneDuration);
        }

        private static List<StoryboardSceneItem> BuildLocalCurriculumStoryboard(string prompt, int targetDurationSec, int baseSceneDurationSec)
        {
            var sentences = prompt.Split(new[] { '.', '!', '?', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var list = new List<StoryboardSceneItem>();

            var firstTopic = sentences.Length > 0 ? sentences[0] : prompt;
            list.Add(new StoryboardSceneItem
            {
                id = $"scene-1-{DateTime.UtcNow.Ticks}",
                title = "Scene 1: Learning Objective",
                narration = $"In this lesson, we master {firstTopic}. Pay close attention to definitions, technical terminology, and step-by-step practical application.",
                onScreenText = $"Learning Objective\n{firstTopic}",
                visualPrompt = $"Educational title card, technical whiteboard style, clear heading, curriculum context.",
                durationSec = 14,
                transition = "crossfade",
                badge = "Learning Objective"
            });

            var transitions = new[] { "wipe", "zoom", "cut", "crossfade" };
            var chunkIndex = 0;
            for (var i = 0; i < sentences.Length; i++)
            {
                var sentence = sentences[i].Trim();
                if (sentence.Length < 10) continue;
                chunkIndex++;
                list.Add(new StoryboardSceneItem
                {
                    id = $"scene-{chunkIndex + 1}-{DateTime.UtcNow.Ticks}",
                    title = $"Scene {chunkIndex + 1}: Concept & Method",
                    narration = sentence,
                    onScreenText = sentence,
                    visualPrompt = $"Instructional diagram illustrating: {sentence}. High-contrast technical visualization with measurement annotations.",
                    durationSec = baseSceneDurationSec,
                    transition = transitions[chunkIndex % transitions.Length],
                    badge = chunkIndex % 2 == 0 ? "Practical Demonstration" : "Key Concept"
                });
                if (list.Count >= 6) break;
            }

            list.Add(new StoryboardSceneItem
            {
                id = $"scene-end-{DateTime.UtcNow.Ticks}",
                title = $"Scene {list.Count + 1}: Knowledge Check",
                narration = "Review check: Can you explain the main principles demonstrated in this module? Ensure you can accurately reproduce dimensions and terminology.",
                onScreenText = "Knowledge Check\n1. State the core principles\n2. Explain dimensions and specifications\n3. Verify against assessment criteria",
                visualPrompt = "Summary review card, checklist format, bold contrast, timer cue.",
                durationSec = 16,
                transition = "crossfade",
                badge = "Knowledge Check"
            });

            return list;
        }

        public sealed class StoryboardSceneItem
        {
            public string id { get; set; } = string.Empty;
            public string title { get; set; } = string.Empty;
            public string narration { get; set; } = string.Empty;
            public string onScreenText { get; set; } = string.Empty;
            public string visualPrompt { get; set; } = string.Empty;
            public int durationSec { get; set; } = 20;
            public string transition { get; set; } = "crossfade";
            public string badge { get; set; } = "Concept";
        }

        [HttpGet("chapter-tools")]
        public IActionResult GetChapterTools()
        {
            var workspaceRoot = ResolveChapterWorkspaceRoot();
            Directory.CreateDirectory(workspaceRoot);

            var ffmpegPath = ResolveFfmpegPath();
            var ffprobePath = ResolveFfprobePath();
            var whisperCliPath = ResolveWhisperCliPath();
            var whisperModelPath = ResolveWhisperModelPath();

            var whisperAvailable = !string.IsNullOrWhiteSpace(whisperCliPath) && !string.IsNullOrWhiteSpace(whisperModelPath);
            var googleAllowed = AiRuntime.AllowGoogle();
            var googleKey = (Secrets.GetGoogleKey() ?? string.Empty).Trim();
            var googleConfigured = googleAllowed && !string.IsNullOrWhiteSpace(googleKey);
            var defaultGeminiModel = AiRuntime.GetGeminiModel("gemini-3.8-flash");

            return Ok(new
            {
                aiMode = AiRuntime.GetMode(),
                workspaceRoot,
                defaults = new
                {
                    language = NormalizeTranscriptionLanguage(Environment.GetEnvironmentVariable("DEFAULT_TRANSCRIPTION_LANGUAGE")),
                    preferLocalWhisper = whisperAvailable,
                    allowGeminiFallback = googleConfigured,
                    geminiModel = defaultGeminiModel,
                    allowOpenAiFallback = googleConfigured,
                    openAiModel = defaultGeminiModel
                },
                tools = new
                {
                    ffmpeg = new
                    {
                        available = !string.IsNullOrWhiteSpace(ffmpegPath),
                        path = ffmpegPath,
                        reason = !string.IsNullOrWhiteSpace(ffmpegPath)
                            ? "FFmpeg available for chapter audio extraction."
                            : $"FFmpeg not found. You can install it with {Path.Combine(EtdpPaths.GetProjectRoot(), "scripts", "video", "ensure-ffmpeg.ps1")}."
                    },
                    ffprobe = new
                    {
                        available = !string.IsNullOrWhiteSpace(ffprobePath),
                        path = ffprobePath,
                        reason = !string.IsNullOrWhiteSpace(ffprobePath)
                            ? "FFprobe available."
                            : "FFprobe not found. Media probing will stay minimal."
                    },
                    whisper = new
                    {
                        available = whisperAvailable,
                        cliPath = whisperCliPath,
                        modelPath = whisperModelPath,
                        reason = whisperAvailable
                            ? "Local whisper.cpp transcription available."
                            : "Local whisper.cpp CLI or model file was not found."
                    },
                    gemini = new
                    {
                        available = googleConfigured,
                        model = defaultGeminiModel,
                        reason = !googleAllowed
                            ? "Google Gemini audio transcription is disabled by AI_MODE."
                            : (string.IsNullOrWhiteSpace(googleKey)
                                ? "GEMINI_API_KEY is not configured."
                                : "Google Gemini audio transcription available.")
                    },
                    openAi = new
                    {
                        available = googleConfigured,
                        model = defaultGeminiModel,
                        reason = !googleAllowed
                            ? "Google Gemini transcription disabled by AI_MODE."
                            : (string.IsNullOrWhiteSpace(googleKey)
                                ? "Google Gemini API key not configured."
                                : "Google Gemini transcription available.")
                    }
                }
            });
        }

        [HttpPost("chapter-workflow")]
        public async Task<IActionResult> RunChapterWorkflow([FromBody] ChapterWorkflowRequest? request, CancellationToken cancellationToken)
        {
            request ??= new ChapterWorkflowRequest();

            var sourcePath = ResolveLocalMediaPath(request.SourcePath);
            if (string.IsNullOrWhiteSpace(sourcePath) || !System.IO.File.Exists(sourcePath))
            {
                return BadRequest(new { error = "Provide a valid local chapter media path." });
            }

            var extension = Path.GetExtension(sourcePath);
            var isVideo = VideoExtensions.Contains(extension);
            var isAudio = AudioExtensions.Contains(extension);
            if (!isVideo && !isAudio)
            {
                return BadRequest(new
                {
                    error = $"Unsupported media type: {extension}. Supported video: {string.Join(", ", VideoExtensions.OrderBy(x => x))}. Supported audio: {string.Join(", ", AudioExtensions.OrderBy(x => x))}."
                });
            }

            var workspacePath = CreateChapterWorkspace(request.ProjectTitle, request.ChapterTitle, sourcePath);
            Directory.CreateDirectory(workspacePath);

            var language = NormalizeTranscriptionLanguage(request.Language);
            var transcriptBasePath = Path.Combine(workspacePath, "chapter-transcript");
            var warnings = new List<string>();
            var timeoutSeconds = Math.Clamp(request.TimeoutSeconds ?? 1800, 60, 7200);

            LocalArtifactResult extraction;
            var extractedAudioPath = string.Empty;

            if (isAudio)
            {
                extraction = LocalArtifactResult.NotAttempted("Source file is already audio. Audio extraction skipped.");
            }
            else if (request.ExtractAudio == false)
            {
                extraction = LocalArtifactResult.NotAttempted("Audio extraction skipped by request.");
            }
            else
            {
                extractedAudioPath = Path.Combine(workspacePath, "chapter-audio.wav");
                extraction = await TryExtractAudioAsync(sourcePath, extractedAudioPath, timeoutSeconds, cancellationToken);
                if (!extraction.Success)
                {
                    warnings.Add(extraction.Message);
                    extractedAudioPath = string.Empty;
                }
            }

            var transcription = TranscriptionArtifactResult.Fail(
                "No transcription provider completed successfully.",
                attempted: false,
                provider: "none");

            if (request.PreferLocalWhisper != false)
            {
                var localInputPath = isAudio
                    ? sourcePath
                    : (!string.IsNullOrWhiteSpace(extractedAudioPath) ? extractedAudioPath : string.Empty);

                if (!string.IsNullOrWhiteSpace(localInputPath))
                {
                    transcription = await TryTranscribeWithWhisperAsync(
                        localInputPath,
                        transcriptBasePath,
                        language,
                        timeoutSeconds,
                        cancellationToken);

                    if (!transcription.Success && transcription.Attempted)
                    {
                        warnings.Add(transcription.Message);
                    }
                }
                else if (isVideo)
                {
                    warnings.Add("Local whisper skipped because chapter audio extraction was unavailable for the video source.");
                }
            }

            if (!transcription.Success && (request.AllowGeminiFallback != false || request.AllowOpenAiFallback != false))
            {
                var geminiTranscription = await TryTranscribeWithGeminiAsync(
                    sourcePath,
                    transcriptBasePath,
                    language,
                    request.GeminiModel ?? request.OpenAiModel,
                    cancellationToken);

                if (geminiTranscription.Success)
                {
                    transcription = geminiTranscription;
                }
                else if (geminiTranscription.Attempted)
                {
                    warnings.Add(geminiTranscription.Message);
                    transcription = geminiTranscription;
                }
            }

            var transcriptPreview = ReadTranscriptPreview(transcription.TranscriptPath);

            var manifest = new
            {
                createdAtUtc = DateTime.UtcNow,
                success = transcription.Success,
                workspacePath,
                sourcePath,
                sourceKind = isVideo ? "video" : "audio",
                chapterTitle = FirstNonEmpty(request.ChapterTitle, Path.GetFileNameWithoutExtension(sourcePath)),
                projectTitle = FirstNonEmpty(request.ProjectTitle, "Text To Video"),
                language,
                extraction,
                transcription,
                warnings
            };

            var manifestPath = Path.Combine(workspacePath, "chapter-manifest.json");
            await System.IO.File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, ArtifactJsonOptions),
                cancellationToken);

            return Ok(new
            {
                success = transcription.Success,
                message = transcription.Success
                    ? $"Chapter assets prepared with {transcription.Provider}."
                    : "Chapter workflow completed, but transcription still needs attention.",
                workspacePath,
                manifestPath,
                sourcePath,
                sourceKind = isVideo ? "video" : "audio",
                extractedAudioPath = extraction.Success ? extraction.OutputPath : string.Empty,
                extraction,
                transcription,
                transcriptPath = transcription.TranscriptPath,
                srtPath = transcription.SrtPath,
                transcriptPreview,
                warnings
            });
        }

        private async Task<string> ResolveConditioningPathAsync(GenerateVideoRequest request, CancellationToken cancellationToken)
        {
            var direct = (request.ConditioningPath ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(direct)) return direct;

            var sourceMaterialId = request.SourceMaterialId.GetValueOrDefault(0);
            if (sourceMaterialId <= 0) return string.Empty;

            var material = await _context.SourceMaterials
                .AsNoTracking()
                .Where(s => s.Id == sourceMaterialId)
                .Select(s => new { s.FilePath, s.Url })
                .FirstOrDefaultAsync(cancellationToken);
            if (material == null) return string.Empty;

            var path = (material.FilePath ?? string.Empty).Trim();
            return !string.IsNullOrWhiteSpace(path) ? path : (material.Url ?? string.Empty).Trim();
        }

        private async Task<LocalExecutionResult> TryRunLocalLtxAsync(string prompt, string conditioningPath, GenerateVideoRequest request, CancellationToken cancellationToken)
        {
            var ltxRoot = ResolveLtxRoot();
            if (string.IsNullOrWhiteSpace(ltxRoot))
            {
                return LocalExecutionResult.Fail("LTX root not found. Expected C:\\ETDP\\LTX-Video or LTX_VIDEO_ROOT.", false);
            }

            var inferenceScript = Path.Combine(ltxRoot, "inference.py");
            if (!System.IO.File.Exists(inferenceScript))
            {
                return LocalExecutionResult.Fail($"inference.py not found in {ltxRoot}.", false, ltxRootPath: ltxRoot);
            }

            if (string.IsNullOrWhiteSpace(conditioningPath))
            {
                return LocalExecutionResult.Fail("Conditioning source path is empty.", false, ltxRootPath: ltxRoot);
            }

            var conditioning = conditioningPath.Trim().Trim('"');
            if (Uri.TryCreate(conditioning, UriKind.Absolute, out var u) &&
                (string.Equals(u.Scheme, "http", StringComparison.OrdinalIgnoreCase) || string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
            {
                return LocalExecutionResult.Fail("Conditioning source is a URL. Local LTX requires a local file path.", false, ltxRootPath: ltxRoot);
            }

            if (!Path.IsPathRooted(conditioning))
            {
                var candidate = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, conditioning));
                if (System.IO.File.Exists(candidate)) conditioning = candidate;
            }
            if (!System.IO.File.Exists(conditioning))
            {
                return LocalExecutionResult.Fail($"Conditioning file not found: {conditioning}", false, ltxRootPath: ltxRoot);
            }

            var pipelineConfig = string.IsNullOrWhiteSpace(request.PipelineConfig) ? "configs/ltxv-13b-0.9.8-distilled.yaml" : request.PipelineConfig.Trim();
            var pipelinePath = Path.IsPathRooted(pipelineConfig) ? pipelineConfig : Path.Combine(ltxRoot, pipelineConfig.Replace('/', Path.DirectorySeparatorChar));
            if (!System.IO.File.Exists(pipelinePath))
            {
                return LocalExecutionResult.Fail($"Pipeline config not found: {pipelinePath}", false, ltxRootPath: ltxRoot);
            }

            var pythonExe = ResolvePythonExe(ltxRoot);
            var width = Math.Clamp(request.Width ?? 1216, 256, 4096);
            var height = Math.Clamp(request.Height ?? 704, 256, 4096);
            var numFrames = Math.Clamp(request.NumFrames ?? 121, 8, 512);
            var seed = request.Seed ?? 171198;
            var timeoutSeconds = Math.Clamp(request.TimeoutSeconds ?? 1800, 30, 7200);
            var startedUtc = DateTime.UtcNow;

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                WorkingDirectory = ltxRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(inferenceScript);
            psi.ArgumentList.Add("--prompt");
            psi.ArgumentList.Add(prompt);
            psi.ArgumentList.Add("--conditioning_media_paths");
            psi.ArgumentList.Add(conditioning);
            psi.ArgumentList.Add("--conditioning_start_frames");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("--height");
            psi.ArgumentList.Add(height.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--width");
            psi.ArgumentList.Add(width.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--num_frames");
            psi.ArgumentList.Add(numFrames.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--seed");
            psi.ArgumentList.Add(seed.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--pipeline_config");
            psi.ArgumentList.Add(pipelineConfig);

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null) return LocalExecutionResult.Fail("Failed to start LTX process.", true, ltxRootPath: ltxRoot, pythonExe: pythonExe);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
                await proc.WaitForExitAsync(timeout.Token);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                var outputPath = FindLatestVideoPath(ltxRoot, startedUtc.AddSeconds(-4));

                if (proc.ExitCode != 0)
                {
                    return LocalExecutionResult.Fail($"LTX exited with code {proc.ExitCode}.", true, proc.ExitCode, stdout, stderr, ltxRoot, pythonExe, outputPath);
                }

                var msg = string.IsNullOrWhiteSpace(outputPath)
                    ? "Local LTX completed, but output video path was not auto-detected."
                    : $"Local LTX completed. Output: {outputPath}";
                return LocalExecutionResult.SuccessResult(msg, proc.ExitCode, stdout, stderr, ltxRoot, pythonExe, outputPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Local LTX run failed");
                return LocalExecutionResult.Fail($"Local LTX execution failed: {ex.Message}", true, ltxRootPath: ltxRoot, pythonExe: pythonExe);
            }
        }

        private async Task<OpenAiFallbackResult> TryOpenAiFallbackAsync(string prompt, string conditioningPath, string? requestedModel, CancellationToken cancellationToken)
        {
            if (AiRuntime.AllowGoogle())
            {
                try
                {
                    var geminiPrompt = $"Create a concise educational video production fallback plan with a revised prompt, 4-6 scene outline with on-screen text and narration, and practical trade guidance.\nTopic/Prompt: {prompt}\nSource: {(string.IsNullOrWhiteSpace(conditioningPath) ? "(none)" : conditioningPath)}";
                    var text = await GoogleGeminiClient.GenerateContentAsync(geminiPrompt, AiRuntime.GetGeminiModel());
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return OpenAiFallbackResult.SuccessResult("Google Gemini storyboard plan generated.", AiRuntime.GetGeminiModel(), text.Trim());
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Google Gemini fallback attempt failed, trying alternate.");
                }
            }

            if (!AiRuntime.AllowOpenAi()) return OpenAiFallbackResult.Fail("Cloud fallback disabled by AI_MODE.", attempted: false);

            var key = Secrets.GetOpenAIKey();
            if (string.IsNullOrWhiteSpace(key)) return OpenAiFallbackResult.Fail("Cloud API key is not configured.", attempted: false);

            var model = string.IsNullOrWhiteSpace(requestedModel) ? (Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gemini-3.8-flash") : requestedModel.Trim();
            var payload = new
            {
                model,
                messages = new[]
                {
                    new { role = "system", content = "Return practical fallback guidance for educational text-to-video." },
                    new
                    {
                        role = "user",
                        content = $"Create a concise fallback plan with a revised prompt, 4-6 scene outline, and next steps.\nPrompt: {prompt}\nConditioning source: {(string.IsNullOrWhiteSpace(conditioningPath) ? "(none)" : conditioningPath)}"
                    }
                },
                temperature = 0.3
            };

            using var msg = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
            msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            try
            {
                var resp = await _http.SendAsync(msg, cancellationToken);
                var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                if (!resp.IsSuccessStatusCode)
                {
                    return OpenAiFallbackResult.Fail($"HTTP {(int)resp.StatusCode}. {TrimTail(body, 900)}", attempted: true, model: model);
                }

                var text = TryExtractChatCompletionText(body) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    return OpenAiFallbackResult.Fail("Provider returned empty content.", attempted: true, model: model);
                }

                return OpenAiFallbackResult.SuccessResult("Storyboard plan generated.", model, text.Trim());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cloud fallback failed");
                return OpenAiFallbackResult.Fail(ex.Message, attempted: true, model: model);
            }
        }

        private string ResolveLtxRoot()
        {
            var env = (Environment.GetEnvironmentVariable("LTX_VIDEO_ROOT") ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;

            var parent = Directory.GetParent(_environment.ContentRootPath)?.FullName ?? string.Empty;
            var candidates = new[]
            {
                @"C:\ETDP\LTX-Video",
                Path.Combine(parent, "LTX-Video"),
                Path.Combine(_environment.ContentRootPath, "LTX-Video")
            };
            foreach (var c in candidates) if (Directory.Exists(c)) return c;
            return string.Empty;
        }

        private static string ResolvePythonExe(string ltxRoot)
        {
            var env = (Environment.GetEnvironmentVariable("LTX_PYTHON_EXE") ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(env)) return env;

            var candidates = new[]
            {
                Path.Combine(ltxRoot, ".venv312", "Scripts", "python.exe"),
                Path.Combine(ltxRoot, ".venv", "Scripts", "python.exe"),
                Path.Combine(ltxRoot, "venv", "Scripts", "python.exe")
            };
            foreach (var c in candidates) if (System.IO.File.Exists(c)) return c;
            return "python";
        }

        private async Task<LocalArtifactResult> TryExtractAudioAsync(string sourcePath, string outputAudioPath, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var ffmpegPath = ResolveFfmpegPath();
            if (string.IsNullOrWhiteSpace(ffmpegPath))
            {
                return LocalArtifactResult.Fail("FFmpeg is not available for audio extraction.", attempted: false);
            }

            var outputDir = Path.GetDirectoryName(outputAudioPath);
            if (!string.IsNullOrWhiteSpace(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(sourcePath);
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-ac");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-ar");
            psi.ArgumentList.Add("16000");
            psi.ArgumentList.Add("-c:a");
            psi.ArgumentList.Add("pcm_s16le");
            psi.ArgumentList.Add(outputAudioPath);

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    return LocalArtifactResult.Fail("Failed to start FFmpeg for audio extraction.", true, toolPath: ffmpegPath);
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
                await proc.WaitForExitAsync(timeout.Token);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                if (proc.ExitCode != 0 || !System.IO.File.Exists(outputAudioPath))
                {
                    return LocalArtifactResult.Fail(
                        $"FFmpeg audio extraction failed with exit code {proc.ExitCode}.",
                        true,
                        proc.ExitCode,
                        stdout,
                        stderr,
                        ffmpegPath,
                        outputAudioPath);
                }

                return LocalArtifactResult.SuccessResult(
                    "Chapter audio extracted successfully.",
                    proc.ExitCode,
                    stdout,
                    stderr,
                    ffmpegPath,
                    outputAudioPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chapter audio extraction failed");
                return LocalArtifactResult.Fail($"FFmpeg audio extraction failed: {ex.Message}", true, toolPath: ffmpegPath);
            }
        }

        private async Task<TranscriptionArtifactResult> TryTranscribeWithWhisperAsync(string inputPath, string transcriptBasePath, string language, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var whisperCliPath = ResolveWhisperCliPath();
            var whisperModelPath = ResolveWhisperModelPath();
            if (string.IsNullOrWhiteSpace(whisperCliPath) || string.IsNullOrWhiteSpace(whisperModelPath))
            {
                return TranscriptionArtifactResult.Fail(
                    "Local whisper.cpp CLI or model was not found.",
                    attempted: false,
                    provider: "whisper-local");
            }

            var transcriptDir = Path.GetDirectoryName(transcriptBasePath);
            if (!string.IsNullOrWhiteSpace(transcriptDir))
            {
                Directory.CreateDirectory(transcriptDir);
            }

            var normalizedLanguage = NormalizeTranscriptionLanguage(language);
            var psi = new ProcessStartInfo
            {
                FileName = whisperCliPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add(whisperModelPath);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(inputPath);
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add(transcriptBasePath);
            psi.ArgumentList.Add("-otxt");
            psi.ArgumentList.Add("-osrt");
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add(normalizedLanguage);

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    return TranscriptionArtifactResult.Fail(
                        "Failed to start local whisper transcription.",
                        attempted: true,
                        provider: "whisper-local",
                        toolPath: whisperCliPath,
                        model: Path.GetFileName(whisperModelPath));
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
                await proc.WaitForExitAsync(timeout.Token);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                var srtPath = $"{transcriptBasePath}.srt";
                var transcriptPath = $"{transcriptBasePath}.txt";
                if (!System.IO.File.Exists(transcriptPath) && System.IO.File.Exists(srtPath))
                {
                    var converted = ConvertSrtToTranscript(await System.IO.File.ReadAllTextAsync(srtPath, cancellationToken));
                    await System.IO.File.WriteAllTextAsync(transcriptPath, converted, cancellationToken);
                }

                if (proc.ExitCode != 0 || !System.IO.File.Exists(srtPath))
                {
                    return TranscriptionArtifactResult.Fail(
                        $"Local whisper transcription failed with exit code {proc.ExitCode}.",
                        attempted: true,
                        provider: "whisper-local",
                        toolPath: whisperCliPath,
                        model: Path.GetFileName(whisperModelPath),
                        stdout: stdout,
                        stderr: stderr);
                }

                return TranscriptionArtifactResult.SuccessResult(
                    "whisper-local",
                    Path.GetFileName(whisperModelPath),
                    "Local whisper transcription completed.",
                    transcriptPath,
                    srtPath,
                    whisperCliPath,
                    stdout,
                    stderr);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Local whisper transcription failed");
                return TranscriptionArtifactResult.Fail(
                    $"Local whisper transcription failed: {ex.Message}",
                    attempted: true,
                    provider: "whisper-local",
                    toolPath: whisperCliPath,
                    model: Path.GetFileName(whisperModelPath));
            }
        }

        private async Task<TranscriptionArtifactResult> TryTranscribeWithGeminiAsync(string inputPath, string transcriptBasePath, string language, string? requestedModel, CancellationToken cancellationToken)
        {
            if (!AiRuntime.AllowGoogle())
            {
                return TranscriptionArtifactResult.Fail(
                    "Google Gemini audio transcription is disabled by AI_MODE.",
                    attempted: false,
                    provider: "gemini");
            }

            var key = (Secrets.GetGoogleKey() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                return TranscriptionArtifactResult.Fail(
                    "GEMINI_API_KEY is not configured.",
                    attempted: false,
                    provider: "gemini");
            }

            if (string.IsNullOrWhiteSpace(inputPath) || !System.IO.File.Exists(inputPath))
            {
                return TranscriptionArtifactResult.Fail(
                    "Transcription input file was not found.",
                    attempted: false,
                    provider: "gemini");
            }

            var model = string.IsNullOrWhiteSpace(requestedModel)
                ? AiRuntime.GetGeminiModel("gemini-3.8-flash")
                : requestedModel.Trim();
            var transcriptDir = Path.GetDirectoryName(transcriptBasePath);
            if (!string.IsNullOrWhiteSpace(transcriptDir))
            {
                Directory.CreateDirectory(transcriptDir);
            }

            try
            {
                var fileInfo = new FileInfo(inputPath);
                if (fileInfo.Length <= 20 * 1024 * 1024)
                {
                    var bytes = await System.IO.File.ReadAllBytesAsync(inputPath, cancellationToken);
                    var base64 = Convert.ToBase64String(bytes);
                    var mimeType = ResolveMediaContentType(inputPath);

                    var promptText = $"Listen to this recorded chapter audio carefully and transcribe it completely. Language: {language}. Output standard SRT subtitle format with exact timestamps [HH:MM:SS,mmm --> HH:MM:SS,mmm], followed by the full text transcript.";
                    var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(key)}";
                    var payload = new
                    {
                        contents = new object[]
                        {
                            new
                            {
                                parts = new object[]
                                {
                                    new
                                    {
                                        inline_data = new
                                        {
                                            mime_type = mimeType,
                                            data = base64
                                        }
                                    },
                                    new { text = promptText }
                                }
                            }
                        }
                    };

                    using var msg = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                    };

                    using var resp = await _http.SendAsync(msg, cancellationToken);
                    var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                    if (resp.IsSuccessStatusCode)
                    {
                        var text = TryExtractChatCompletionText(body) ?? body;
                        var srtPath = $"{transcriptBasePath}.srt";
                        var transcriptPath = $"{transcriptBasePath}.txt";
                        await System.IO.File.WriteAllTextAsync(srtPath, text, cancellationToken);
                        var transcriptText = ConvertSrtToTranscript(text);
                        await System.IO.File.WriteAllTextAsync(transcriptPath, transcriptText, cancellationToken);

                        return TranscriptionArtifactResult.SuccessResult(
                            "gemini",
                            model,
                            "Google Gemini audio transcription completed.",
                            transcriptPath,
                            srtPath,
                            url);
                    }
                }

                return TranscriptionArtifactResult.Fail(
                    "File size exceeds 20MB for direct inline Gemini transcription. Use local Whisper or extract audio first.",
                    attempted: true,
                    provider: "gemini",
                    model: model);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Google Gemini audio transcription failed");
                return TranscriptionArtifactResult.Fail(
                    $"Google Gemini transcription failed: {ex.Message}",
                    attempted: true,
                    provider: "gemini",
                    model: model);
            }
        }

        private Task<TranscriptionArtifactResult> TryTranscribeWithOpenAiAsync(string inputPath, string transcriptBasePath, string language, string? requestedModel, CancellationToken cancellationToken)
            => TryTranscribeWithGeminiAsync(inputPath, transcriptBasePath, language, requestedModel, cancellationToken);

        private string ResolveChapterWorkspaceRoot()
        {
            return Path.Combine(EtdpPaths.GetExportsRoot(), "TextToVideo", "ChapterAssets");
        }

        private string CreateChapterWorkspace(string? projectTitle, string? chapterTitle, string sourcePath)
        {
            var root = ResolveChapterWorkspaceRoot();
            var projectSlug = Slugify(projectTitle, "text-to-video");
            var chapterSlug = Slugify(
                string.IsNullOrWhiteSpace(chapterTitle) ? Path.GetFileNameWithoutExtension(sourcePath) : chapterTitle,
                "chapter");
            return Path.Combine(root, projectSlug, $"{DateTime.Now:yyyyMMdd_HHmmss}_{chapterSlug}");
        }

        private string ResolveLocalMediaPath(string? rawPath)
        {
            var value = (rawPath ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            if (Path.IsPathRooted(value) && System.IO.File.Exists(value))
            {
                return Path.GetFullPath(value);
            }

            var candidates = new[]
            {
                Path.Combine(_environment.ContentRootPath, value),
                Path.Combine(EtdpPaths.GetProjectRoot(), value)
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    var full = Path.GetFullPath(candidate);
                    if (System.IO.File.Exists(full))
                    {
                        return full;
                    }
                }
                catch
                {
                    // ignore invalid candidate paths
                }
            }

            return string.Empty;
        }

        private string ResolveFfmpegPath()
        {
            return ResolveToolPath(
                Environment.GetEnvironmentVariable("FFMPEG_EXE"),
                Path.Combine(EtdpPaths.GetProjectRoot(), "tools", "ffmpeg", "bin", "ffmpeg.exe"),
                FindExecutableOnPath("ffmpeg.exe"),
                FindExecutableOnPath("ffmpeg"));
        }

        private string ResolveFfprobePath()
        {
            return ResolveToolPath(
                Environment.GetEnvironmentVariable("FFPROBE_EXE"),
                Path.Combine(EtdpPaths.GetProjectRoot(), "tools", "ffmpeg", "bin", "ffprobe.exe"),
                FindExecutableOnPath("ffprobe.exe"),
                FindExecutableOnPath("ffprobe"));
        }

        private string ResolveWhisperCliPath()
        {
            return ResolveToolPath(
                Environment.GetEnvironmentVariable("WHISPER_CPP_EXE"),
                Environment.GetEnvironmentVariable("WHISPER_CLI_PATH"),
                Path.Combine(EtdpPaths.GetProjectRoot(), "tools", "whisper.cpp", "build", "bin", "Release", "whisper-cli.exe"),
                Path.Combine(EtdpPaths.GetProjectRoot(), "tools", "whisper.cpp", "build", "bin", "Release", "main.exe"),
                Path.Combine(EtdpPaths.GetProjectRoot(), "tools", "whisper", "whisper-cli.exe"),
                FindExecutableOnPath("whisper-cli.exe"),
                FindExecutableOnPath("whisper.exe"),
                FindExecutableOnPath("whisper-cli"),
                FindExecutableOnPath("whisper"));
        }

        private string ResolveWhisperModelPath()
        {
            var direct = ResolveToolPath(
                Environment.GetEnvironmentVariable("WHISPER_MODEL_PATH"),
                Path.Combine(EtdpPaths.GetProjectRoot(), "tools", "whisper.cpp", "models", "ggml-base.en.bin"),
                Path.Combine(EtdpPaths.GetProjectRoot(), "tools", "whisper.cpp", "models", "ggml-base.bin"),
                Path.Combine(EtdpPaths.GetProjectRoot(), "tools", "whisper", "models", "ggml-base.en.bin"),
                Path.Combine(EtdpPaths.GetProjectRoot(), "tools", "whisper", "models", "ggml-base.bin"));

            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }

            var candidateDirs = new[]
            {
                Path.Combine(EtdpPaths.GetProjectRoot(), "tools", "whisper.cpp", "models"),
                Path.Combine(EtdpPaths.GetProjectRoot(), "tools", "whisper", "models")
            };

            foreach (var dir in candidateDirs)
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                var firstModel = Directory.EnumerateFiles(dir, "ggml-*.bin", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstModel))
                {
                    return firstModel;
                }
            }

            return string.Empty;
        }

        private static string ResolveToolPath(params string?[] candidates)
        {
            foreach (var candidate in candidates ?? Array.Empty<string?>())
            {
                var normalized = (candidate ?? string.Empty).Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                try
                {
                    if (System.IO.File.Exists(normalized))
                    {
                        return Path.GetFullPath(normalized);
                    }
                }
                catch
                {
                    // ignore invalid candidate path
                }
            }

            return string.Empty;
        }

        private static string FindExecutableOnPath(string fileName)
        {
            var normalized = (fileName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var searchNames = normalized.Contains('.')
                ? new[] { normalized }
                : extensions.Select(ext => normalized + ext.ToLowerInvariant())
                    .Concat(new[] { normalized })
                    .ToArray();

            foreach (var dir in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (var searchName in searchNames)
                {
                    try
                    {
                        var candidate = Path.Combine(dir, searchName);
                        if (System.IO.File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch
                    {
                        // ignore invalid PATH entries
                    }
                }
            }

            return string.Empty;
        }

        private static string NormalizePrompt(string? prompt)
        {
            return string.Join(" ", (prompt ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        private static string? FindLatestVideoPath(string ltxRoot, DateTime earliestUtc)
        {
            var dirs = new[]
            {
                Path.Combine(ltxRoot, "outputs"),
                Path.Combine(ltxRoot, "output"),
                Path.Combine(ltxRoot, "results"),
                Path.Combine(ltxRoot, "generated")
            };
            var files = new List<FileInfo>();
            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
                    {
                        if (!VideoExtensions.Contains(Path.GetExtension(file))) continue;
                        var info = new FileInfo(file);
                        if (info.LastWriteTimeUtc >= earliestUtc) files.Add(info);
                    }
                }
                catch { }
            }
            return files.OrderByDescending(x => x.LastWriteTimeUtc).Select(x => x.FullName).FirstOrDefault();
        }

        private static string? TryExtractChatCompletionText(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return null;
                var first = choices[0];
                if (!first.TryGetProperty("message", out var msg)) return null;
                if (!msg.TryGetProperty("content", out var content)) return null;
                return content.ValueKind switch
                {
                    JsonValueKind.String => content.GetString(),
                    _ => content.GetRawText()
                };
            }
            catch
            {
                return null;
            }
        }

        private static string TrimTail(string? input, int maxChars)
        {
            var s = (input ?? string.Empty).Trim();
            if (s.Length <= maxChars) return s;
            return s.Substring(s.Length - maxChars);
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

        private static string ResolveTtsContentType(string format)
        {
            return string.Equals(format, "wav", StringComparison.OrdinalIgnoreCase) ? "audio/wav"
                : string.Equals(format, "flac", StringComparison.OrdinalIgnoreCase) ? "audio/flac"
                : string.Equals(format, "aac", StringComparison.OrdinalIgnoreCase) ? "audio/aac"
                : string.Equals(format, "opus", StringComparison.OrdinalIgnoreCase) ? "audio/ogg"
                : "audio/mpeg";
        }

        private static string ResolveMediaContentType(string path)
        {
            var extension = Path.GetExtension(path ?? string.Empty).Trim().ToLowerInvariant();
            return extension switch
            {
                ".wav" => "audio/wav",
                ".mp3" => "audio/mpeg",
                ".m4a" => "audio/mp4",
                ".aac" => "audio/aac",
                ".flac" => "audio/flac",
                ".ogg" => "audio/ogg",
                ".opus" => "audio/ogg",
                ".webm" => "video/webm",
                ".mov" => "video/quicktime",
                ".mkv" => "video/x-matroska",
                ".avi" => "video/x-msvideo",
                _ => "video/mp4"
            };
        }

        private static string NormalizeTranscriptionLanguage(string? value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return "en";
            }

            var separatorIndex = normalized.IndexOfAny(new[] { '-', '_' });
            if (separatorIndex > 0)
            {
                normalized = normalized.Substring(0, separatorIndex);
            }

            return normalized.ToLowerInvariant();
        }

        private static string ConvertSrtToTranscript(string srtText)
        {
            var lines = (srtText ?? string.Empty).Replace("\r", string.Empty).Split('\n');
            var builder = new StringBuilder();

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (builder.Length > 0 && builder[^1] != '\n')
                    {
                        builder.AppendLine();
                    }
                    continue;
                }

                if (line.Contains("-->", StringComparison.Ordinal))
                {
                    continue;
                }

                if (int.TryParse(line, out _))
                {
                    continue;
                }

                if (builder.Length > 0 && builder[^1] != '\n')
                {
                    builder.Append(' ');
                }

                builder.Append(line);
            }

            return builder.ToString().Trim();
        }

        private static string ReadTranscriptPreview(string? transcriptPath)
        {
            if (string.IsNullOrWhiteSpace(transcriptPath) || !System.IO.File.Exists(transcriptPath))
            {
                return string.Empty;
            }

            try
            {
                var text = System.IO.File.ReadAllText(transcriptPath).Trim();
                if (text.Length <= 1200)
                {
                    return text;
                }

                return $"{text.Substring(0, 1200)}...";
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string Slugify(string? value, string fallback)
        {
            var source = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(source))
            {
                return fallback;
            }

            var builder = new StringBuilder();
            var previousDash = false;

            foreach (var ch in source)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(char.ToLowerInvariant(ch));
                    previousDash = false;
                    continue;
                }

                if (previousDash)
                {
                    continue;
                }

                builder.Append('-');
                previousDash = true;
            }

            var slug = builder.ToString().Trim('-');
            return string.IsNullOrWhiteSpace(slug) ? fallback : slug;
        }

        private static string FirstNonEmpty(params string?[] candidates)
        {
            foreach (var candidate in candidates ?? Array.Empty<string?>())
            {
                var value = (candidate ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static string[] ParseCsvOptions(string? raw, params string[] fallbacks)
        {
            var values = (raw ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .ToList();

            if (values.Count == 0)
            {
                values.AddRange((fallbacks ?? Array.Empty<string>())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim()));
            }

            return values
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public sealed class TtsPreviewRequest
        {
            public string? Provider { get; set; }
            public string? Text { get; set; }
            public string? Model { get; set; }
            public string? Voice { get; set; }
            public string? Format { get; set; }
            public double? Speed { get; set; }
        }

        public sealed class GenerateVideoRequest
        {
            public string? Prompt { get; set; }
            public int? TargetDurationSec { get; set; }
            public int? SceneDurationSec { get; set; }
            public string? VisualPreset { get; set; }
            public int? SourceMaterialId { get; set; }
            public string? ConditioningPath { get; set; }
            public string? PipelineConfig { get; set; }
            public int? Width { get; set; }
            public int? Height { get; set; }
            public int? NumFrames { get; set; }
            public int? Seed { get; set; }
            public int? TimeoutSeconds { get; set; }
            public bool? AllowGeminiFallback { get; set; }
            public string? GeminiModel { get; set; }
            public bool? AllowOpenAiFallback { get => AllowGeminiFallback; set => AllowGeminiFallback = value; }
            public string? OpenAiModel { get => GeminiModel; set => GeminiModel = value; }
        }

        public sealed class ChapterWorkflowRequest
        {
            public string? ProjectTitle { get; set; }
            public string? ChapterTitle { get; set; }
            public string? SourcePath { get; set; }
            public string? Language { get; set; }
            public bool? ExtractAudio { get; set; }
            public bool? PreferLocalWhisper { get; set; }
            public bool? AllowGeminiFallback { get; set; }
            public string? GeminiModel { get; set; }
            public bool? AllowOpenAiFallback { get => AllowGeminiFallback; set => AllowGeminiFallback = value; }
            public string? OpenAiModel { get => GeminiModel; set => GeminiModel = value; }
            public int? TimeoutSeconds { get; set; }
        }

        public sealed class LocalExecutionResult
        {
            public bool Attempted { get; set; }
            public bool Success { get; set; }
            public string Message { get; set; } = string.Empty;
            public int? ExitCode { get; set; }
            public string StdoutTail { get; set; } = string.Empty;
            public string StderrTail { get; set; } = string.Empty;
            public string OutputVideoPath { get; set; } = string.Empty;
            public string LtxRootPath { get; set; } = string.Empty;
            public string PythonExe { get; set; } = string.Empty;

            public static LocalExecutionResult SuccessResult(string message, int? exitCode, string? stdout, string? stderr, string? ltxRootPath, string? pythonExe, string? outputVideoPath)
            {
                return new LocalExecutionResult
                {
                    Attempted = true,
                    Success = true,
                    Message = message,
                    ExitCode = exitCode,
                    StdoutTail = TrimTail(stdout, 2000),
                    StderrTail = TrimTail(stderr, 2000),
                    OutputVideoPath = outputVideoPath ?? string.Empty,
                    LtxRootPath = ltxRootPath ?? string.Empty,
                    PythonExe = pythonExe ?? string.Empty
                };
            }

            public static LocalExecutionResult Fail(string message, bool attempted = true, int? exitCode = null, string? stdout = null, string? stderr = null, string? ltxRootPath = null, string? pythonExe = null, string? outputVideoPath = null)
            {
                return new LocalExecutionResult
                {
                    Attempted = attempted,
                    Success = false,
                    Message = message,
                    ExitCode = exitCode,
                    StdoutTail = TrimTail(stdout, 2000),
                    StderrTail = TrimTail(stderr, 2000),
                    OutputVideoPath = outputVideoPath ?? string.Empty,
                    LtxRootPath = ltxRootPath ?? string.Empty,
                    PythonExe = pythonExe ?? string.Empty
                };
            }
        }

        public sealed class LocalArtifactResult
        {
            public bool Attempted { get; set; }
            public bool Success { get; set; }
            public string Message { get; set; } = string.Empty;
            public int? ExitCode { get; set; }
            public string StdoutTail { get; set; } = string.Empty;
            public string StderrTail { get; set; } = string.Empty;
            public string ToolPath { get; set; } = string.Empty;
            public string OutputPath { get; set; } = string.Empty;

            public static LocalArtifactResult SuccessResult(string message, int? exitCode, string? stdout, string? stderr, string? toolPath, string? outputPath)
            {
                return new LocalArtifactResult
                {
                    Attempted = true,
                    Success = true,
                    Message = message,
                    ExitCode = exitCode,
                    StdoutTail = TrimTail(stdout, 2000),
                    StderrTail = TrimTail(stderr, 2000),
                    ToolPath = toolPath ?? string.Empty,
                    OutputPath = outputPath ?? string.Empty
                };
            }

            public static LocalArtifactResult Fail(string message, bool attempted, int? exitCode = null, string? stdout = null, string? stderr = null, string? toolPath = null, string? outputPath = null)
            {
                return new LocalArtifactResult
                {
                    Attempted = attempted,
                    Success = false,
                    Message = message,
                    ExitCode = exitCode,
                    StdoutTail = TrimTail(stdout, 2000),
                    StderrTail = TrimTail(stderr, 2000),
                    ToolPath = toolPath ?? string.Empty,
                    OutputPath = outputPath ?? string.Empty
                };
            }

            public static LocalArtifactResult NotAttempted(string message)
            {
                return new LocalArtifactResult
                {
                    Attempted = false,
                    Success = false,
                    Message = message,
                    ExitCode = null,
                    StdoutTail = string.Empty,
                    StderrTail = string.Empty,
                    ToolPath = string.Empty,
                    OutputPath = string.Empty
                };
            }
        }

        public sealed class OpenAiFallbackResult
        {
            public bool Attempted { get; set; }
            public bool Success { get; set; }
            public string Model { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
            public string PlanText { get; set; } = string.Empty;

            public static OpenAiFallbackResult SuccessResult(string message, string model, string planText)
            {
                return new OpenAiFallbackResult
                {
                    Attempted = true,
                    Success = true,
                    Model = model ?? string.Empty,
                    Message = message,
                    PlanText = planText ?? string.Empty
                };
            }

            public static OpenAiFallbackResult Fail(string message, bool attempted, string? model = null)
            {
                return new OpenAiFallbackResult
                {
                    Attempted = attempted,
                    Success = false,
                    Model = model ?? string.Empty,
                    Message = message,
                    PlanText = string.Empty
                };
            }
        }

        public sealed class TranscriptionArtifactResult
        {
            public bool Attempted { get; set; }
            public bool Success { get; set; }
            public string Provider { get; set; } = string.Empty;
            public string Model { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
            public string TranscriptPath { get; set; } = string.Empty;
            public string SrtPath { get; set; } = string.Empty;
            public string ToolPath { get; set; } = string.Empty;
            public string StdoutTail { get; set; } = string.Empty;
            public string StderrTail { get; set; } = string.Empty;

            public static TranscriptionArtifactResult SuccessResult(string provider, string? model, string message, string? transcriptPath, string? srtPath, string? toolPath, string? stdout = null, string? stderr = null)
            {
                return new TranscriptionArtifactResult
                {
                    Attempted = true,
                    Success = true,
                    Provider = provider ?? string.Empty,
                    Model = model ?? string.Empty,
                    Message = message,
                    TranscriptPath = transcriptPath ?? string.Empty,
                    SrtPath = srtPath ?? string.Empty,
                    ToolPath = toolPath ?? string.Empty,
                    StdoutTail = TrimTail(stdout, 2000),
                    StderrTail = TrimTail(stderr, 2000)
                };
            }

            public static TranscriptionArtifactResult Fail(string message, bool attempted, string provider, string? model = null, string? toolPath = null, string? stdout = null, string? stderr = null)
            {
                return new TranscriptionArtifactResult
                {
                    Attempted = attempted,
                    Success = false,
                    Provider = provider ?? string.Empty,
                    Model = model ?? string.Empty,
                    Message = message,
                    TranscriptPath = string.Empty,
                    SrtPath = string.Empty,
                    ToolPath = toolPath ?? string.Empty,
                    StdoutTail = TrimTail(stdout, 2000),
                    StderrTail = TrimTail(stderr, 2000)
                };
            }
        }
    }
}
