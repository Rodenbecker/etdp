using System.Text;
using System.Text.Json;
using ETD.Api.Data;
using ETD.Api.Models;
using ETD.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ETD.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DiagnosticsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly OcrExtractionService _ocrExtractionService;
        private readonly CodexContinuityService _codexContinuityService;
        private readonly WorkspaceBackupService _workspaceBackupService;

        public DiagnosticsController(
            ApplicationDbContext context,
            OcrExtractionService ocrExtractionService,
            CodexContinuityService codexContinuityService,
            WorkspaceBackupService workspaceBackupService)
        {
            _context = context;
            _ocrExtractionService = ocrExtractionService;
            _codexContinuityService = codexContinuityService;
            _workspaceBackupService = workspaceBackupService;
        }

        [HttpPost("client-error")]
        public IActionResult CaptureClientError([FromBody] ClientErrorRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Message))
                return BadRequest("message is required.");

            var correlationId = HttpContext.Items.TryGetValue("CorrelationId", out var cidObj)
                ? cidObj?.ToString()
                : null;

            var extra = new
            {
                req.Url,
                req.ComponentStack,
                req.Source,
                req.Metadata
            };

            _context.SystemErrorLogs.Add(new SystemErrorLog
            {
                Source = "client",
                Severity = string.IsNullOrWhiteSpace(req.Severity) ? "error" : req.Severity!,
                Message = req.Message!,
                StackTrace = req.Stack,
                Path = req.Url,
                Method = "CLIENT",
                StatusCode = null,
                CorrelationId = correlationId,
                ClientCorrelationId = req.ClientCorrelationId ?? Request.Headers["X-Client-Correlation-Id"].FirstOrDefault(),
                UserAgent = Request.Headers.UserAgent.ToString(),
                MachineName = Environment.MachineName,
                ExtraJson = JsonSerializer.Serialize(extra)
            });
            _context.SaveChanges();

            return Ok(new { captured = true });
        }

        [HttpGet("recent")]
        public IActionResult Recent([FromQuery] int take = 100)
        {
            if (take < 1) take = 1;
            if (take > 500) take = 500;

            var rows = _context.SystemErrorLogs
                .AsNoTracking()
                .OrderByDescending(x => x.Id)
                .Take(take)
                .Select(x => new
                {
                    x.Id,
                    x.CreatedAtUtc,
                    x.Source,
                    x.Severity,
                    x.Message,
                    x.Path,
                    x.Method,
                    x.StatusCode,
                    x.CorrelationId,
                    x.ClientCorrelationId,
                    x.UserAgent,
                    x.MachineName
                })
                .ToList();
            return Ok(rows);
        }

        [HttpGet("server-info")]
        public IActionResult ServerInfo()
        {
            var scheme = string.IsNullOrWhiteSpace(Request?.Scheme) ? "http" : Request.Scheme;
            var host = Request?.Host.HasValue == true ? Request.Host.Value : "localhost";
            var pathBase = Request?.PathBase.HasValue == true ? Request.PathBase.Value : string.Empty;
            var baseUrl = $"{scheme}://{host}{pathBase}".TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = "http://localhost:5299";

            return Ok(new
            {
                baseUrl,
                apiBase = $"{baseUrl}/api",
                host,
                scheme,
                environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Unknown",
                machineName = Environment.MachineName,
                processId = Environment.ProcessId,
                checkedAtUtc = DateTime.UtcNow
            });
        }

        [HttpGet("ocr-status")]
        public IActionResult OcrStatus()
        {
            var snapshot = _ocrExtractionService.GetHealthSnapshot();
            return Ok(snapshot);
        }

        [HttpGet("gemini-continuity-status")]
        [HttpGet("codex-continuity-status")]
        public IActionResult ContinuityStatus()
        {
            var status = _codexContinuityService.GetStatus();
            return Ok(status);
        }

        [HttpPost("gemini-continuity-refresh")]
        [HttpPost("codex-continuity-refresh")]
        public async Task<IActionResult> ContinuityRefresh([FromQuery] string? reason = null)
        {
            var tag = string.IsNullOrWhiteSpace(reason) ? "manual-refresh" : reason!.Trim();
            var status = await _codexContinuityService.RefreshNowAsync(tag);
            return Ok(status);
        }

        [HttpGet("gemini-continuity-latest")]
        [HttpGet("codex-continuity-latest")]
        public IActionResult ContinuityLatest()
        {
            var status = _codexContinuityService.GetStatus();
            var markdown = _codexContinuityService.GetLatestMarkdown();
            return Ok(new
            {
                status,
                content = markdown
            });
        }

        [HttpGet("backup-status")]
        public IActionResult BackupStatus()
        {
            var status = _workspaceBackupService.GetStatus();
            return Ok(status);
        }

        [HttpPost("run-backup")]
        public async Task<IActionResult> RunBackup([FromQuery] string? reason = null)
        {
            var tag = string.IsNullOrWhiteSpace(reason) ? "manual-run" : reason!.Trim();
            var status = await _workspaceBackupService.RunBackupNowAsync(tag);
            return Ok(status);
        }

        [HttpGet("entry/{id:int}")]
        public IActionResult Entry(int id)
        {
            var row = _context.SystemErrorLogs.AsNoTracking().FirstOrDefault(x => x.Id == id);
            if (row == null) return NotFound();
            return Ok(row);
        }

        [HttpGet("download")]
        public IActionResult Download([FromQuery] int hours = 24)
        {
            if (hours < 1) hours = 1;
            if (hours > 24 * 30) hours = 24 * 30;

            var since = DateTime.UtcNow.AddHours(-hours);
            var rows = _context.SystemErrorLogs
                .AsNoTracking()
                .Where(x => x.CreatedAtUtc >= since)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(5000)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("Id,CreatedAtUtc,Source,Severity,Message,Path,Method,StatusCode,CorrelationId,ClientCorrelationId,MachineName");
            foreach (var x in rows)
            {
                sb.AppendLine(string.Join(",",
                    Escape(x.Id.ToString()),
                    Escape(x.CreatedAtUtc.ToString("o")),
                    Escape(x.Source),
                    Escape(x.Severity),
                    Escape(x.Message),
                    Escape(x.Path),
                    Escape(x.Method),
                    Escape(x.StatusCode?.ToString() ?? ""),
                    Escape(x.CorrelationId),
                    Escape(x.ClientCorrelationId),
                    Escape(x.MachineName)
                ));
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            return File(bytes, "text/csv", $"diagnostics_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
        }

        private static string Escape(string? value)
        {
            var v = value ?? string.Empty;
            if (v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r'))
                return "\"" + v.Replace("\"", "\"\"") + "\"";
            return v;
        }

        public sealed class ClientErrorRequest
        {
            public string? Severity { get; set; }
            public string? Message { get; set; }
            public string? Stack { get; set; }
            public string? Url { get; set; }
            public string? ComponentStack { get; set; }
            public string? Source { get; set; }
            public string? ClientCorrelationId { get; set; }
            public Dictionary<string, object>? Metadata { get; set; }
        }
    }
}
