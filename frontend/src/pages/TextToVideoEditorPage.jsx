import React, { useEffect, useMemo, useRef, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { useQualification } from '../context/QualificationContext';
import { speak, stopSpeech } from '../utils/tts';
import './TextToVideoEditorPage.css';

const API_ROOT = '/api';
const MIRA_TTV_SEED_PREFIX = 'etdp:mira-ttv-seed:';
const DEFAULT_SOURCE_TEXT = [
  'By the end of the learning topic the learner must be able to identify and explain screw thread terminology.',
  'The learner must also explain how the profile of a thread is drawn with accurate resemblance to the original object in terms of dimensions, shape and size.'
].join(' ');

const transitions = ['cut', 'crossfade', 'wipe', 'zoom'];
const visualPresets = [
  { value: 'technical-whiteboard', label: 'Technical Whiteboard' },
  { value: 'workshop-realism', label: 'Workshop Realism' },
  { value: 'clean-infographics', label: 'Clean Infographics' }
];

const asInt = (value, fallback = 0) => {
  const n = Number(value);
  return Number.isFinite(n) ? Math.trunc(n) : fallback;
};

const clamp = (value, min, max) => Math.min(max, Math.max(min, value));

const slugify = (value) =>
  String(value || 'project')
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/(^-|-$)+/g, '')
    || 'project';

const toTimestamp = (seconds) => {
  const totalMs = Math.max(0, Math.round(Number(seconds || 0) * 1000));
  const hrs = Math.floor(totalMs / 3600000);
  const mins = Math.floor((totalMs % 3600000) / 60000);
  const secs = Math.floor((totalMs % 60000) / 1000);
  const ms = totalMs % 1000;
  const hh = String(hrs).padStart(2, '0');
  const mm = String(mins).padStart(2, '0');
  const ss = String(secs).padStart(2, '0');
  const mmm = String(ms).padStart(3, '0');
  return `${hh}:${mm}:${ss},${mmm}`;
};

const downloadFile = (fileName, body, mimeType = 'text/plain;charset=utf-8') => {
  const blob = body instanceof Blob ? body : new Blob([body], { type: mimeType });
  const href = window.URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = href;
  anchor.download = fileName;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  window.URL.revokeObjectURL(href);
};

const toCsvValue = (value) => {
  const raw = String(value ?? '');
  if (raw.includes('"') || raw.includes(',') || raw.includes('\n')) {
    return `"${raw.replace(/"/g, '""')}"`;
  }
  return raw;
};

const wait = (ms) => new Promise((resolve) => window.setTimeout(resolve, Math.max(0, Number(ms) || 0)));

const resolveVideoDimensions = (aspectRatio) => {
  if (String(aspectRatio) === '9:16') return { width: 720, height: 1280 };
  if (String(aspectRatio) === '1:1') return { width: 960, height: 960 };
  return { width: 1280, height: 720 };
};

const wrapCanvasText = (ctx, text, maxWidth, maxLines) => {
  const source = String(text || '').replace(/\r/g, '').trim();
  if (!source) return [];

  const paragraphs = source.split('\n').map((p) => p.trim()).filter(Boolean);
  const lines = [];

  for (const paragraph of paragraphs) {
    const words = paragraph.split(/\s+/).filter(Boolean);
    if (!words.length) continue;

    let current = words[0];
    for (let i = 1; i < words.length; i += 1) {
      const candidate = `${current} ${words[i]}`;
      if (ctx.measureText(candidate).width <= maxWidth) {
        current = candidate;
      } else {
        lines.push(current);
        current = words[i];
        if (lines.length >= maxLines) return lines;
      }
    }
    lines.push(current);
    if (lines.length >= maxLines) return lines;
  }

  return lines;
};

const drawStoryboardFrame = ({
  ctx,
  width,
  height,
  scene,
  sceneIndex,
  sceneCount,
  progress,
  image
}) => {
  // 1. Premium Dark Technical Background with subtle blueprint grid
  const gradient = ctx.createLinearGradient(0, 0, width, height);
  gradient.addColorStop(0, '#09111e');
  gradient.addColorStop(0.5, '#0f172a');
  gradient.addColorStop(1, '#020617');
  ctx.fillStyle = gradient;
  ctx.fillRect(0, 0, width, height);

  // Subtle grid
  ctx.strokeStyle = 'rgba(56, 189, 248, 0.04)';
  ctx.lineWidth = 1;
  const gridSize = 40;
  for (let x = 0; x < width; x += gridSize) {
    ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, height); ctx.stroke();
  }
  for (let y = 0; y < height; y += gridSize) {
    ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(width, y); ctx.stroke();
  }

  // Top header bar
  const padX = Math.round(width * 0.05);
  const padY = Math.round(height * 0.05);
  const contentWidth = width - (padX * 2);

  // Badge
  const badgeText = `SCENE ${sceneIndex + 1} OF ${sceneCount}`.toUpperCase();
  ctx.font = `700 13px -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif`;
  const badgeW = ctx.measureText(badgeText).width + 20;
  const badgeH = 26;
  ctx.fillStyle = '#0284c7';
  if (ctx.roundRect) {
    ctx.beginPath(); ctx.roundRect(padX, padY, badgeW, badgeH, 6); ctx.fill();
  } else {
    ctx.fillRect(padX, padY, badgeW, badgeH);
  }
  ctx.fillStyle = '#ffffff';
  ctx.textBaseline = 'middle';
  ctx.fillText(badgeText, padX + 10, padY + (badgeH / 2));

  // Qualification / Topic tag top right
  const topicTag = "QCTO OCCUPATIONAL CURRICULUM";
  ctx.font = `600 12px -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif`;
  ctx.fillStyle = '#64748b';
  ctx.textAlign = 'right';
  ctx.fillText(topicTag, width - padX, padY + (badgeH / 2));
  ctx.textAlign = 'left';

  // Title
  ctx.fillStyle = '#ffffff';
  ctx.textBaseline = 'top';
  ctx.font = `800 ${Math.round(width * 0.026)}px -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif`;
  const titleY = padY + badgeH + 14;
  const title = String(scene?.title || `Scene ${sceneIndex + 1}`).trim();
  const titleLines = wrapCanvasText(ctx, title, contentWidth, 2);
  titleLines.forEach((line, i) => {
    ctx.fillText(line, padX, titleY + (i * Math.round(width * 0.03)));
  });

  // Layout: Split 55% Left (Content & Points) and 45% Right (Illustration/Visual Schematic)
  const mainY = titleY + (titleLines.length * Math.round(width * 0.03)) + 16;
  const mainH = height - mainY - 60;
  const colGap = 24;
  const leftW = Math.round((contentWidth - colGap) * 0.58);
  const rightW = contentWidth - leftW - colGap;
  const rightX = padX + leftW + colGap;

  // Left card - Teaching Content & Takeaways
  ctx.fillStyle = 'rgba(15, 23, 42, 0.75)';
  ctx.strokeStyle = 'rgba(51, 65, 85, 0.8)';
  ctx.lineWidth = 1.5;
  if (ctx.roundRect) {
    ctx.beginPath(); ctx.roundRect(padX, mainY, leftW, mainH, 12); ctx.fill(); ctx.stroke();
  } else {
    ctx.fillRect(padX, mainY, leftW, mainH); ctx.strokeRect(padX, mainY, leftW, mainH);
  }

  // Key Learning Points header inside left card
  ctx.fillStyle = '#38bdf8';
  ctx.font = `700 13px -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif`;
  ctx.fillText("CORE LEARNING & INSTRUCTION", padX + 20, mainY + 18);

  // Body text
  ctx.fillStyle = '#e2e8f0';
  ctx.font = `400 ${Math.round(width * 0.015)}px -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif`;
  const bodyText = String(scene?.onScreenText || scene?.narration || '').trim();
  const bodyLines = wrapCanvasText(ctx, bodyText, leftW - 40, 8);
  const lineH = Math.round(width * 0.015 * 1.5);
  bodyLines.forEach((line, i) => {
    ctx.fillText(line, padX + 20, mainY + 44 + (i * lineH));
  });

  // Visual / Cue box at bottom of left card
  if (scene?.visualPrompt) {
    const cueBoxY = mainY + mainH - 52;
    ctx.fillStyle = 'rgba(2, 132, 199, 0.15)';
    ctx.strokeStyle = 'rgba(56, 189, 248, 0.3)';
    ctx.lineWidth = 1;
    if (ctx.roundRect) {
      ctx.beginPath(); ctx.roundRect(padX + 16, cueBoxY, leftW - 32, 38, 6); ctx.fill(); ctx.stroke();
    } else {
      ctx.fillRect(padX + 16, cueBoxY, leftW - 32, 38);
    }
    ctx.fillStyle = '#7dd3fc';
    ctx.font = `italic 12px -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif`;
    const promptShort = 'Visual Direction: ' + String(scene.visualPrompt).slice(0, 75) + (scene.visualPrompt.length > 75 ? '...' : '');
    ctx.fillText(promptShort, padX + 26, cueBoxY + 12);
  }

  // Right card - Technical Diagram / Illustration
  ctx.fillStyle = 'rgba(15, 23, 42, 0.75)';
  ctx.strokeStyle = 'rgba(51, 65, 85, 0.8)';
  ctx.lineWidth = 1.5;
  if (ctx.roundRect) {
    ctx.beginPath(); ctx.roundRect(rightX, mainY, rightW, mainH, 12); ctx.fill(); ctx.stroke();
  } else {
    ctx.fillRect(rightX, mainY, rightW, mainH); ctx.strokeRect(rightX, mainY, rightW, mainH);
  }

  // Header inside right card
  ctx.fillStyle = '#38bdf8';
  ctx.font = `700 13px -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif`;
  ctx.fillText("TECHNICAL SCHEMATIC & WORKSHOP FOCUS", rightX + 20, mainY + 18);

  // If image provided: draw image with aspect containment
  if (image && image.complete && image.naturalWidth > 0) {
    const imgPad = 16;
    const destX = rightX + imgPad;
    const destY = mainY + 40;
    const destW = rightW - (imgPad * 2);
    const destH = mainH - 56;
    const scale = Math.min(destW / image.naturalWidth, destH / image.naturalHeight);
    const renderW = image.naturalWidth * scale;
    const renderH = image.naturalHeight * scale;
    const offsetX = destX + (destW - renderW) / 2;
    const offsetY = destY + (destH - renderH) / 2;
    ctx.drawImage(image, offsetX, offsetY, renderW, renderH);
  } else {
    // Technical schematic blueprint visual
    const cx = rightX + (rightW / 2);
    const cy = mainY + 44 + (mainH - 56) / 2;
    
    // Draw trade blueprint graphic
    ctx.strokeStyle = 'rgba(56, 189, 248, 0.25)';
    ctx.lineWidth = 1.5;
    ctx.beginPath(); ctx.arc(cx, cy, 60, 0, Math.PI * 2); ctx.stroke();
    ctx.beginPath(); ctx.arc(cx, cy, 85, 0, Math.PI * 2); ctx.stroke();
    ctx.setLineDash([4, 4]);
    ctx.beginPath(); ctx.arc(cx, cy, 110, 0, Math.PI * 2); ctx.stroke();
    ctx.setLineDash([]);

    // Crosshairs
    ctx.strokeStyle = 'rgba(56, 189, 248, 0.2)';
    ctx.beginPath(); ctx.moveTo(cx - 130, cy); ctx.lineTo(cx + 130, cy); ctx.stroke();
    ctx.beginPath(); ctx.moveTo(cx, cy - 110); ctx.lineTo(cx, cy + 110); ctx.stroke();

    // Center icon & label
    ctx.fillStyle = '#ffffff';
    ctx.font = `700 24px -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif`;
    ctx.textAlign = 'center';
    ctx.fillText("⚙️", cx, cy - 12);
    ctx.font = `600 13px -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif`;
    ctx.fillStyle = '#38bdf8';
    ctx.fillText("APPLIED TRADE SPECIFICATION", cx, cy + 22);
    ctx.font = `400 11px -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif`;
    ctx.fillStyle = '#94a3b8';
    ctx.fillText("QCTO Competence Standard", cx, cy + 38);
    ctx.textAlign = 'left';
  }

  // Footer bar
  const footerY = height - 28;
  ctx.fillStyle = '#64748b';
  ctx.font = `500 12px -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif`;
  ctx.fillText(`Scene ${sceneIndex + 1} of ${sceneCount}  •  ${Math.max(1, Number(scene?.durationSec || 0))}s  •  Transition: ${scene?.transition || 'cut'}`, padX, footerY);

  // Smooth animated progress bar at bottom
  const barY = height - 8;
  const barHeight = 4;
  ctx.fillStyle = 'rgba(255, 255, 255, 0.1)';
  ctx.fillRect(padX, barY, contentWidth, barHeight);
  ctx.fillStyle = '#0ea5e9';
  ctx.fillRect(padX, barY, Math.round(contentWidth * Math.max(0, Math.min(1, Number(progress) || 0))), barHeight);
};

const splitSentences = (text) => {
  const normalized = String(text || '').replace(/\r/g, '').trim();
  if (!normalized) return [];

  const lines = normalized
    .split(/\n+/)
    .map((line) => line.trim())
    .filter(Boolean);

  const sentences = lines
    .flatMap((line) => line.split(/(?<=[.!?])\s+/))
    .map((piece) => piece.trim())
    .filter(Boolean);

  return sentences.length ? sentences : [normalized];
};

const sentenceTitle = (sentence, index) => {
  const words = String(sentence || '')
    .replace(/[^\w\s]/g, ' ')
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 6);

  if (!words.length) return `Scene ${index}`;
  const title = words.map((w) => w.charAt(0).toUpperCase() + w.slice(1).toLowerCase()).join(' ');
  return `Scene ${index}: ${title}`;
};

const createScene = (scene) => ({
  id: String(scene?.id || `scene-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`),
  title: String(scene?.title || ''),
  narration: String(scene?.narration || ''),
  onScreenText: String(scene?.onScreenText || ''),
  visualPrompt: String(scene?.visualPrompt || ''),
  durationSec: clamp(asInt(scene?.durationSec, 20), 4, 180),
  transition: transitions.includes(String(scene?.transition || '')) ? String(scene.transition) : 'cut'
});

const resolveSceneNarrationText = (scene) => String(
  scene?.narration
  || scene?.onScreenText
  || scene?.title
  || ''
).trim();

const normalizeQualification = (row) => ({
  id: asInt(row?.id ?? row?.Id, 0),
  code: String(row?.qualificationNumber ?? row?.QualificationNumber ?? '').trim(),
  description: String(row?.qualificationDescription ?? row?.QualificationDescription ?? '').trim()
});

const normalizeTopic = (row) => ({
  id: asInt(row?.id ?? row?.Id, 0),
  code: String(row?.topicCode ?? row?.TopicCode ?? '').trim(),
  description: String(row?.topicDescription ?? row?.TopicDescription ?? '').trim(),
  order: asInt(row?.order ?? row?.Order, 0),
  subjectCode: String(row?.subjectCode ?? row?.SubjectCode ?? '').trim()
});

const normalizeMaterial = (row) => ({
  id: asInt(row?.id ?? row?.Id, 0),
  title: String(row?.title ?? row?.Title ?? '').trim(),
  fileName: String(row?.fileName ?? row?.FileName ?? '').trim(),
  filePath: String(row?.filePath ?? row?.FilePath ?? '').trim(),
  url: String(row?.url ?? row?.Url ?? '').trim(),
  fileType: String(row?.fileType ?? row?.FileType ?? '').trim(),
  knowledgeSourceType: String(row?.knowledgeSourceType ?? row?.KnowledgeSourceType ?? '').trim(),
  subjectDescription: String(row?.subjectDescription ?? row?.SubjectDescription ?? '').trim(),
  topicDescription: String(row?.topicDescription ?? row?.TopicDescription ?? '').trim()
});

const normalizeTtsOptions = (payload) => {
  const providersRaw = Array.isArray(payload?.providers) ? payload.providers : [];
  const providers = providersRaw
    .map((row) => ({
      id: String(row?.id || '').trim().toLowerCase(),
      label: String(row?.label || '').trim() || String(row?.id || '').trim(),
      available: Boolean(row?.available),
      reason: String(row?.reason || '').trim(),
      models: (Array.isArray(row?.models) ? row.models : [])
        .map((v) => String(v || '').trim())
        .filter(Boolean),
      voices: (Array.isArray(row?.voices) ? row.voices : [])
        .map((v) => String(v || '').trim())
        .filter(Boolean),
      defaultModel: String(row?.defaultModel || '').trim(),
      defaultVoice: String(row?.defaultVoice || '').trim()
    }))
    .filter((row) => row.id);

  const defaults = payload?.defaults || {};

  return {
    aiMode: String(payload?.aiMode || '').trim(),
    providers,
    defaults: {
      provider: String(defaults?.provider || 'browser').trim().toLowerCase() || 'browser',
      browserLanguage: String(defaults?.browserLanguage || 'en-ZA').trim() || 'en-ZA',
      browserPreferredVoice: String(defaults?.browserPreferredVoice || 'Microsoft').trim() || 'Microsoft',
      geminiModel: String(defaults?.geminiModel || '').trim(),
      geminiVoice: String(defaults?.geminiVoice || '').trim(),
      format: String(defaults?.format || 'mp3').trim().toLowerCase() || 'mp3',
      speed: clamp(Number(defaults?.speed || 1), 0.25, 4)
    }
  };
};

const normalizeChapterTool = (row, fallbackReason) => ({
  available: Boolean(row?.available),
  path: String(row?.path || '').trim(),
  cliPath: String(row?.cliPath || '').trim(),
  modelPath: String(row?.modelPath || '').trim(),
  model: String(row?.model || '').trim(),
  reason: String(row?.reason || fallbackReason || '').trim()
});

const normalizeChapterTools = (payload) => ({
  aiMode: String(payload?.aiMode || '').trim(),
  workspaceRoot: String(payload?.workspaceRoot || '').trim(),
  defaults: {
    language: String(payload?.defaults?.language || 'en').trim() || 'en',
    preferLocalWhisper: Boolean(payload?.defaults?.preferLocalWhisper),
    allowGeminiFallback: Boolean(payload?.defaults?.allowGeminiFallback),
    geminiModel: String(payload?.defaults?.geminiModel || '').trim()
  },
  tools: {
    ffmpeg: normalizeChapterTool(payload?.tools?.ffmpeg, 'FFmpeg not detected.'),
    ffprobe: normalizeChapterTool(payload?.tools?.ffprobe, 'FFprobe not detected.'),
    whisper: normalizeChapterTool(payload?.tools?.whisper, 'Local whisper not detected.'),
    gemini: normalizeChapterTool(payload?.tools?.gemini || payload?.tools?.openAi, 'Google Gemini transcription not configured.')
  }
});

const normalizeChapterResult = (payload) => ({
  success: Boolean(payload?.success),
  message: String(payload?.message || '').trim(),
  workspacePath: String(payload?.workspacePath || '').trim(),
  manifestPath: String(payload?.manifestPath || '').trim(),
  sourcePath: String(payload?.sourcePath || '').trim(),
  sourceKind: String(payload?.sourceKind || '').trim(),
  extractedAudioPath: String(payload?.extractedAudioPath || '').trim(),
  transcriptPath: String(payload?.transcriptPath || '').trim(),
  srtPath: String(payload?.srtPath || '').trim(),
  transcriptPreview: String(payload?.transcriptPreview || '').trim(),
  transcriptionProvider: String(payload?.transcription?.provider || '').trim(),
  transcriptionModel: String(payload?.transcription?.model || '').trim(),
  warnings: (Array.isArray(payload?.warnings) ? payload.warnings : [])
    .map((entry) => String(entry || '').trim())
    .filter(Boolean),
  extraction: payload?.extraction || null,
  transcription: payload?.transcription || null
});

const normalizeGeminiFallbackScene = (scene, index) => {
  const transition = String(scene?.transition || '').trim().toLowerCase();
  return createScene({
    title: String(scene?.title || `Scene ${index + 1}`).trim(),
    narration: String(scene?.narration || '').trim(),
    onScreenText: String(scene?.onScreenText || '').trim(),
    visualPrompt: String(scene?.visualPrompt || '').trim(),
    durationSec: clamp(asInt(scene?.durationSec, 14), 4, 180),
    transition: transitions.includes(transition) ? transition : 'cut'
  });
};

const parseGeminiFallbackPlan = (planText) => {
  const raw = String(planText || '').trim();
  if (!raw) return null;

  let jsonText = raw;
  const fencedMatch = raw.match(/```(?:json)?\s*([\s\S]*?)```/i);
  if (fencedMatch?.[1]) {
    jsonText = fencedMatch[1].trim();
  } else {
    const firstBrace = raw.indexOf('{');
    const lastBrace = raw.lastIndexOf('}');
    if (firstBrace >= 0 && lastBrace > firstBrace) {
      jsonText = raw.slice(firstBrace, lastBrace + 1);
    }
  }

  try {
    const parsed = JSON.parse(jsonText);
    return parsed && typeof parsed === 'object' ? parsed : null;
  } catch {
    return null;
  }
};

const buildStoryboard = ({
  briefText,
  outcomeLabel,
  visualPreset,
  sceneDurationSec,
  targetDurationSec,
  includeQuizScene
}) => {
  const sentences = splitSentences(briefText);
  if (!sentences.length) return [];

  const target = clamp(asInt(targetDurationSec, 150), 30, 900);
  const baseSceneDuration = clamp(asInt(sceneDurationSec, 24), 8, 90);
  const desiredScenes = clamp(Math.ceil(target / baseSceneDuration), 3, 14);
  const chunkSize = clamp(Math.ceil(sentences.length / Math.max(1, desiredScenes - 1)), 1, 3);

  const generated = [];
  generated.push(createScene({
    title: 'Scene 1: Learning Objective',
    narration: `In this lesson, we focus on ${outcomeLabel}.`,
    onScreenText: `Learning Objective\n${outcomeLabel}`,
    visualPrompt: `Educational opener, ${visualPreset}, clean title frame, curriculum context visible.`,
    durationSec: 12,
    transition: 'crossfade'
  }));

  for (let i = 0; i < sentences.length; i += chunkSize) {
    const chunk = sentences.slice(i, i + chunkSize);
    const narration = chunk.join(' ');
    const sceneNumber = generated.length + 1;
    generated.push(createScene({
      title: sentenceTitle(chunk[0], sceneNumber),
      narration,
      onScreenText: chunk.join('\n'),
      visualPrompt: `Instructional visual for "${chunk[0]}", ${visualPreset}, strong labels, measured dimensions, practical workshop context.`,
      durationSec: baseSceneDuration,
      transition: transitions[(sceneNumber - 1) % transitions.length]
    }));
  }

  if (includeQuizScene) {
    generated.push(createScene({
      title: `Scene ${generated.length + 1}: Knowledge Check`,
      narration: 'Pause and answer: name three key terms and explain why pitch spacing matters.',
      onScreenText: 'Knowledge Check\n1) Name three thread terms\n2) Why is pitch spacing critical?',
      visualPrompt: `Question card, ${visualPreset}, bold contrast text, timer cue.`,
      durationSec: 12,
      transition: 'crossfade'
    }));
  }

  const baseTotal = generated.reduce((sum, scene) => sum + clamp(asInt(scene.durationSec, 0), 1, 180), 0);
  if (baseTotal <= 0) return generated;

  const ratio = target / baseTotal;
  const scaled = generated.map((scene) => createScene({
    ...scene,
    durationSec: clamp(Math.round(scene.durationSec * ratio), 4, 180)
  }));

  const scaledTotal = scaled.reduce((sum, scene) => sum + scene.durationSec, 0);
  const delta = target - scaledTotal;
  if (scaled.length > 0 && delta !== 0) {
    const last = scaled[scaled.length - 1];
    last.durationSec = clamp(last.durationSec + delta, 4, 180);
  }

  return scaled;
};

export default function TextToVideoEditorPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const { qualificationId, setQualificationId } = useQualification() || { qualificationId: null, setQualificationId: () => {} };
  const stateQualificationId = asInt(location?.state?.qualificationId, 0);
  const stateTopicId = asInt(location?.state?.topicId, 0);
  const stateTopicDescription = String(location?.state?.topicDescription ?? location?.state?.sourceText ?? '').trim();
  const initialQualification = String(stateQualificationId || qualificationId || localStorage.getItem('qualificationId') || '');
  const initialAutoGenerate = Boolean(location?.state?.autoGenerate);
  const initialSeed = (() => {
    if (stateTopicId > 0 || stateTopicDescription) return null;
    const stateSeed = location?.state?.miraSeed;
    if (stateSeed && typeof stateSeed === 'object') return stateSeed;
    const qid = asInt(stateQualificationId || qualificationId || localStorage.getItem('qualificationId'), 0);
    if (!qid) return null;
    try {
      const raw = localStorage.getItem(`${MIRA_TTV_SEED_PREFIX}${qid}`);
      if (!raw) return null;
      return JSON.parse(raw);
    } catch {
      return null;
    }
  })();

  const [qualifications, setQualifications] = useState([]);
  const [topics, setTopics] = useState([]);
  const [materials, setMaterials] = useState([]);
  const [form, setForm] = useState({
    projectTitle: 'Thread Terminology Lesson Video',
    qualificationId: initialQualification,
    topicId: stateTopicId > 0 ? String(stateTopicId) : '',
    sourceMaterialId: '',
    ltxSourcePath: '',
    visualPreset: visualPresets[0].value,
    voiceStyle: 'Calm instructional narrator',
    aspectRatio: '16:9',
    targetDurationSec: 150,
    sceneDurationSec: 24,
    includeQuizScene: true,
    sourceText: stateTopicDescription || DEFAULT_SOURCE_TEXT,
    ttsProvider: 'browser',
    ttsModel: '',
    ttsVoice: '',
    ttsLang: 'en-ZA',
    ttsPreferredVoice: 'Microsoft',
    ttsFormat: 'mp3',
    ttsSpeed: 1
  });
  const [scenes, setScenes] = useState([]);
  const [loading, setLoading] = useState(false);
  const [generating, setGenerating] = useState(false);
  const [renderBusy, setRenderBusy] = useState(false);
  const [status, setStatus] = useState('');
  const [error, setError] = useState('');
  const [renderResult, setRenderResult] = useState(null);
  const [ttsBusy, setTtsBusy] = useState(false);
  const [materialTextBusy, setMaterialTextBusy] = useState(false);
  const [videoExportBusy, setVideoExportBusy] = useState(false);
  const [isPlaying, setIsPlaying] = useState(false);
  const [previewSceneIndex, setPreviewSceneIndex] = useState(0);
  const [previewElapsedSec, setPreviewElapsedSec] = useState(0);
  const [pendingMiraSeed, setPendingMiraSeed] = useState(initialSeed);
  const [autoGeneratePending, setAutoGeneratePending] = useState(initialAutoGenerate);
  const [ttsOptionsBusy, setTtsOptionsBusy] = useState(false);
  const [ttsOptions, setTtsOptions] = useState({
    aiMode: '',
    providers: [],
    defaults: {
      provider: 'browser',
      browserLanguage: 'en-ZA',
      browserPreferredVoice: 'Microsoft',
      geminiModel: '',
      geminiVoice: '',
      format: 'mp3',
      speed: 1
    }
  });
  const [chapterToolsBusy, setChapterToolsBusy] = useState(false);
  const [chapterBusy, setChapterBusy] = useState(false);
  const [chapterTools, setChapterTools] = useState({
    aiMode: '',
    workspaceRoot: '',
    defaults: {
      language: 'en',
      preferLocalWhisper: false,
      allowGeminiFallback: false,
      geminiModel: ''
    },
    tools: {
      ffmpeg: normalizeChapterTool(null, 'FFmpeg not detected.'),
      ffprobe: normalizeChapterTool(null, 'FFprobe not detected.'),
      whisper: normalizeChapterTool(null, 'Local whisper not detected.'),
      gemini: normalizeChapterTool(null, 'Google Gemini transcription not configured.')
    }
  });
  const [chapterForm, setChapterForm] = useState({
    chapterTitle: stateTopicDescription || 'Chapter 1',
    sourcePath: '',
    language: 'en',
    preferLocalWhisper: false,
    allowGeminiFallback: true,
    geminiModel: ''
  });
  const [chapterResult, setChapterResult] = useState(null);

  const importFileRef = useRef(null);
  const narrationRequestRef = useRef(0);
  const lastAutoNarrationKeyRef = useRef('');

  const selectedQualification = useMemo(
    () => qualifications.find((q) => String(q.id) === String(form.qualificationId)) || null,
    [qualifications, form.qualificationId]
  );

  const selectedTopic = useMemo(
    () => topics.find((o) => String(o.id) === String(form.topicId)) || null,
    [topics, form.topicId]
  );

  const selectedMaterial = useMemo(
    () => materials.find((m) => String(m.id) === String(form.sourceMaterialId)) || null,
    [materials, form.sourceMaterialId]
  );

  useEffect(() => {
    const suggested = String(selectedTopic?.description || form.projectTitle || '').trim();
    if (!suggested) return;
    setChapterForm((prev) => {
      const current = String(prev.chapterTitle || '').trim();
      if (current && current !== 'Chapter 1') return prev;
      return { ...prev, chapterTitle: suggested };
    });
  }, [selectedTopic?.description, form.projectTitle]);

  const totalDurationSec = useMemo(
    () => scenes.reduce((sum, scene) => sum + clamp(asInt(scene.durationSec, 0), 0, 180), 0),
    [scenes]
  );

  const activeScene = scenes[previewSceneIndex] || null;
  const ttsProviderOptions = useMemo(
    () => (Array.isArray(ttsOptions?.providers) ? ttsOptions.providers : []).filter((p) => p && p.id),
    [ttsOptions]
  );
  const selectedTtsProvider = useMemo(
    () => ttsProviderOptions.find((p) => p.id === String(form.ttsProvider || '').trim().toLowerCase()) || null,
    [ttsProviderOptions, form.ttsProvider]
  );

  const buildNarrationOptions = () => {
    const provider = String(form.ttsProvider || 'browser').trim().toLowerCase();
    return {
      provider,
      lang: form.ttsLang || 'en-ZA',
      preferredVoiceName: form.ttsPreferredVoice || 'Microsoft',
      model: provider === 'gemini' ? (form.ttsModel || '') : '',
      voice: provider === 'gemini' ? (form.ttsVoice || '') : '',
      format: form.ttsFormat || 'mp3',
      speed: clamp(Number(form.ttsSpeed || 1), 0.25, 4)
    };
  };

  const buildNarrationKey = (scene) => {
    const narrationText = resolveSceneNarrationText(scene);
    if (!narrationText) return '';
    const options = buildNarrationOptions();
    return [
      String(scene?.id || ''),
      narrationText,
      options.provider,
      options.lang,
      options.preferredVoiceName,
      options.model,
      options.voice,
      options.format,
      String(options.speed)
    ].join('|');
  };

  const cancelNarration = () => {
    narrationRequestRef.current += 1;
    stopSpeech();
  };

  const playSceneNarration = async (scene, { announceStatus = false, silentIfEmpty = false } = {}) => {
    const narrationText = resolveSceneNarrationText(scene);
    if (!narrationText) {
      if (!silentIfEmpty) {
        setError('No narration text found in the active scene.');
      }
      return { ok: false, reason: 'empty_text' };
    }

    const requestId = narrationRequestRef.current + 1;
    narrationRequestRef.current = requestId;
    stopSpeech();

    if (announceStatus) {
      setError('');
      setStatus('');
      setTtsBusy(true);
    }

    try {
      const result = await speak(narrationText, buildNarrationOptions());
      if (narrationRequestRef.current !== requestId) {
        return { ok: false, reason: 'superseded' };
      }

      if (!result?.ok) {
        if (!silentIfEmpty) {
          const details = result?.reason ? ` (${result.reason})` : '';
          const message = result?.message ? ` ${String(result.message)}` : '';
          setError(`TTS playback failed${details}.${message}`);
        }
        return result;
      }

      if (announceStatus) {
        const providerLabel = String(result?.provider || buildNarrationOptions().provider || 'browser');
        const voiceText = result?.voiceName ? ` using ${result.voiceName}` : '';
        const modelText = result?.model ? ` (${result.model})` : '';
        setStatus(`Narration started via ${providerLabel}${modelText}${voiceText}.`);
      }

      return result;
    } catch (e) {
      if (narrationRequestRef.current === requestId && !silentIfEmpty) {
        setError(`TTS playback failed: ${e?.message || e}`);
      }
      return { ok: false, reason: 'speak_exception', message: String(e?.message || e) };
    } finally {
      if (announceStatus && narrationRequestRef.current === requestId) {
        setTtsBusy(false);
      }
    }
  };

  useEffect(() => {
    if (!pendingMiraSeed) return;
    const seedQualificationId = asInt(pendingMiraSeed?.qualificationId, 0);
    setForm((prev) => ({
      ...prev,
      projectTitle: String(pendingMiraSeed?.projectTitle || prev.projectTitle),
      qualificationId: seedQualificationId > 0 ? String(seedQualificationId) : prev.qualificationId,
      visualPreset: String(pendingMiraSeed?.visualPreset || prev.visualPreset),
      voiceStyle: String(pendingMiraSeed?.voiceStyle || prev.voiceStyle),
      targetDurationSec: clamp(asInt(pendingMiraSeed?.targetDurationSec, prev.targetDurationSec), 30, 900),
      sceneDurationSec: clamp(asInt(pendingMiraSeed?.sceneDurationSec, prev.sceneDurationSec), 8, 90),
      includeQuizScene: pendingMiraSeed?.includeQuizScene == null ? prev.includeQuizScene : Boolean(pendingMiraSeed.includeQuizScene),
      sourceText: String(pendingMiraSeed?.sourceText || prev.sourceText)
    }));

    if (seedQualificationId > 0) {
      try {
        localStorage.removeItem(`${MIRA_TTV_SEED_PREFIX}${seedQualificationId}`);
      } catch {
        // ignore localStorage failures
      }
    }

    setPendingMiraSeed(null);
    setStatus('Mira starter settings loaded.');
  }, [pendingMiraSeed]);

  useEffect(() => {
    let active = true;

    const loadQualifications = async () => {
      setLoading(true);
      setError('');
      try {
        const res = await fetch(`${API_ROOT}/Qualification`);
        if (!res.ok) throw new Error(await res.text());
        const data = await res.json();
        const list = (Array.isArray(data) ? data : [])
          .map(normalizeQualification)
          .filter((q) => q.id > 0);

        if (!active) return;
        setQualifications(list);

        if (!form.qualificationId && list.length > 0) {
          const first = String(list[0].id);
          setForm((prev) => ({ ...prev, qualificationId: first }));
          setQualificationId(list[0].id);
        }
      } catch (e) {
        if (!active) return;
        setError(`Failed to load qualifications: ${e?.message || e}`);
      } finally {
        if (active) setLoading(false);
      }
    };

    loadQualifications();
    return () => { active = false; };
  }, []);

  useEffect(() => {
    let active = true;

    const loadTtsOptions = async () => {
      setTtsOptionsBusy(true);
      try {
        const res = await fetch(`${API_ROOT}/TextToVideo/tts-options`);
        if (!res.ok) throw new Error(await res.text());
        const normalized = normalizeTtsOptions(await res.json());
        if (!active) return;

        setTtsOptions(normalized);
        setForm((prev) => {
          const providerIds = normalized.providers.map((p) => p.id);
          const fallbackProvider = providerIds.includes(normalized.defaults.provider)
            ? normalized.defaults.provider
            : (providerIds.includes('browser') ? 'browser' : (providerIds[0] || 'browser'));
          const chosenProvider = providerIds.includes(String(prev.ttsProvider || '').trim().toLowerCase())
            ? String(prev.ttsProvider || '').trim().toLowerCase()
            : fallbackProvider;
          const provider = normalized.providers.find((p) => p.id === chosenProvider) || null;

          const geminiModel = prev.ttsModel
            || provider?.defaultModel
            || normalized.defaults.geminiModel
            || provider?.models?.[0]
            || '';
          const geminiVoice = prev.ttsVoice
            || provider?.defaultVoice
            || normalized.defaults.geminiVoice
            || provider?.voices?.[0]
            || '';

          return {
            ...prev,
            ttsProvider: chosenProvider,
            ttsModel: chosenProvider === 'gemini' ? geminiModel : String(prev.ttsModel || 'browser-default'),
            ttsVoice: chosenProvider === 'gemini' ? geminiVoice : String(prev.ttsVoice || normalized.defaults.browserPreferredVoice || 'Microsoft'),
            ttsLang: String(prev.ttsLang || normalized.defaults.browserLanguage || 'en-ZA'),
            ttsPreferredVoice: String(prev.ttsPreferredVoice || normalized.defaults.browserPreferredVoice || 'Microsoft'),
            ttsFormat: String(prev.ttsFormat || normalized.defaults.format || 'mp3').toLowerCase(),
            ttsSpeed: clamp(Number(prev.ttsSpeed || normalized.defaults.speed || 1), 0.25, 4)
          };
        });
      } catch {
        if (!active) return;
      } finally {
        if (active) setTtsOptionsBusy(false);
      }
    };

    loadTtsOptions();
    return () => { active = false; };
  }, []);

  useEffect(() => {
    void loadChapterTools();
  }, []);

  useEffect(() => {
    const qid = asInt(form.qualificationId, 0);
    if (qid > 0) setQualificationId(qid);
  }, [form.qualificationId, setQualificationId]);

  useEffect(() => {
    let active = true;
    const qid = asInt(form.qualificationId, 0);

    if (!qid) {
      setTopics([]);
      setForm((prev) => ({ ...prev, topicId: '' }));
      return () => { active = false; };
    }

    const loadTopics = async () => {
      setError('');
      try {
        const res = await fetch(`${API_ROOT}/Topic/byQualification?qualificationId=${qid}`);
        if (!res.ok) throw new Error(await res.text());
        const data = await res.json();
        if (!active) return;

        const list = (Array.isArray(data) ? data : [])
          .map(normalizeTopic)
          .filter((o) => o.id > 0)
          .sort((a, b) => {
            const sub = String(a.subjectCode || '').localeCompare(String(b.subjectCode || ''), undefined, { sensitivity: 'base', numeric: true });
            if (sub !== 0) return sub;
            return (a.order || 0) - (b.order || 0);
          });

        setTopics(list);
        setForm((prev) => {
          if (list.some((o) => String(o.id) === String(prev.topicId))) return prev;
          return { ...prev, topicId: list[0] ? String(list[0].id) : '' };
        });
      } catch (e) {
        if (!active) return;
        setError(`Failed to load topics: ${e?.message || e}`);
        setTopics([]);
      }
    };

    loadTopics();
    return () => { active = false; };
  }, [form.qualificationId]);

  useEffect(() => {
    let active = true;
    const qid = asInt(form.qualificationId, 0);

    if (!qid) {
      setMaterials([]);
      setForm((prev) => ({ ...prev, sourceMaterialId: '', ltxSourcePath: '' }));
      return () => { active = false; };
    }

    const loadMaterials = async () => {
      try {
        const res = await fetch(`${API_ROOT}/Content/materials?qualificationId=${qid}`);
        if (!res.ok) throw new Error(await res.text());
        const data = await res.json();
        if (!active) return;

        const list = (Array.isArray(data) ? data : [])
          .map(normalizeMaterial)
          .filter((m) => m.id > 0)
          .sort((a, b) => String(a.title || a.fileName).localeCompare(String(b.title || b.fileName), undefined, { sensitivity: 'base' }));

        setMaterials(list);
        setForm((prev) => {
          const keepSelected = list.some((m) => String(m.id) === String(prev.sourceMaterialId));
          const nextSourceMaterialId = keepSelected ? String(prev.sourceMaterialId) : (list[0] ? String(list[0].id) : '');
          const selected = list.find((m) => String(m.id) === nextSourceMaterialId) || null;
          return {
            ...prev,
            sourceMaterialId: nextSourceMaterialId,
            ltxSourcePath: prev.ltxSourcePath || selected?.filePath || selected?.url || ''
          };
        });
      } catch (e) {
        if (!active) return;
        setMaterials([]);
        setError(`Failed to load source materials: ${e?.message || e}`);
      }
    };

    loadMaterials();
    return () => { active = false; };
  }, [form.qualificationId]);

  useEffect(() => {
    if (!isPlaying || scenes.length === 0) return undefined;
    const timer = window.setInterval(() => {
      setPreviewElapsedSec((prev) => {
        const current = scenes[previewSceneIndex];
        const maxDuration = clamp(asInt(current?.durationSec, 0), 1, 180);
        if (prev + 1 < maxDuration) return prev + 1;

        setPreviewSceneIndex((index) => {
          const next = index + 1;
          if (next >= scenes.length) {
            setIsPlaying(false);
            return index;
          }
          return next;
        });
        return 0;
      });
    }, 1000);

    return () => window.clearInterval(timer);
  }, [isPlaying, scenes, previewSceneIndex]);

  useEffect(() => {
    if (previewSceneIndex >= scenes.length) {
      setPreviewSceneIndex(Math.max(0, scenes.length - 1));
    }
  }, [previewSceneIndex, scenes.length]);

  useEffect(() => {
    setPreviewElapsedSec(0);
  }, [previewSceneIndex]);

  useEffect(() => () => {
    cancelNarration();
  }, []);

  useEffect(() => {
    if (!isPlaying) {
      lastAutoNarrationKeyRef.current = '';
      cancelNarration();
      return;
    }

    const scene = scenes[previewSceneIndex] || null;
    const narrationKey = buildNarrationKey(scene);
    if (!narrationKey || lastAutoNarrationKeyRef.current === narrationKey) return;

    lastAutoNarrationKeyRef.current = narrationKey;
    void playSceneNarration(scene, { silentIfEmpty: true });
  }, [
    isPlaying,
    previewSceneIndex,
    scenes,
    form.ttsProvider,
    form.ttsLang,
    form.ttsPreferredVoice,
    form.ttsModel,
    form.ttsVoice,
    form.ttsFormat,
    form.ttsSpeed
  ]);

  const setField = (name, value) => {
    setForm((prev) => ({ ...prev, [name]: value }));
    setStatus('');
    setError('');
  };

  const setChapterField = (name, value) => {
    setChapterForm((prev) => ({ ...prev, [name]: value }));
    setStatus('');
    setError('');
  };

  const loadChapterTools = async () => {
    setChapterToolsBusy(true);
    try {
      const res = await fetch(`${API_ROOT}/TextToVideo/chapter-tools`);
      if (!res.ok) throw new Error(await res.text());
      const normalized = normalizeChapterTools(await res.json());
      setChapterTools(normalized);
      setChapterForm((prev) => ({
        ...prev,
        language: String(prev.language || normalized.defaults.language || 'en').trim() || 'en',
        preferLocalWhisper: normalized.tools.whisper.available
          ? Boolean(prev.preferLocalWhisper || normalized.defaults.preferLocalWhisper)
          : false,
        allowGeminiFallback: normalized.tools.gemini.available
          ? Boolean(prev.allowGeminiFallback || normalized.defaults.allowGeminiFallback)
          : false,
        geminiModel: String(prev.geminiModel || normalized.defaults.geminiModel || '').trim()
      }));
    } catch (e) {
      setError(`Failed to load chapter tools: ${e?.message || e}`);
    } finally {
      setChapterToolsBusy(false);
    }
  };

  useEffect(() => {
    const providerId = String(form.ttsProvider || '').trim().toLowerCase();
    if (!providerId) return;
    const provider = ttsProviderOptions.find((p) => p.id === providerId) || null;
    if (!provider) return;

    setForm((prev) => {
      const nextProvider = providerId;
      const defaultModel = provider.defaultModel || provider.models?.[0] || '';
      const defaultVoice = provider.defaultVoice || provider.voices?.[0] || '';
      const nextModel = nextProvider === 'gemini'
        ? (String(prev.ttsModel || '').trim() || defaultModel)
        : 'browser-default';
      const nextVoice = nextProvider === 'gemini'
        ? (String(prev.ttsVoice || '').trim() || defaultVoice)
        : (String(prev.ttsPreferredVoice || '').trim() || ttsOptions.defaults.browserPreferredVoice || 'Microsoft');
      const nextLang = String(prev.ttsLang || '').trim() || ttsOptions.defaults.browserLanguage || 'en-ZA';
      const nextFormat = String(prev.ttsFormat || '').trim().toLowerCase() || 'mp3';
      const nextSpeed = clamp(Number(prev.ttsSpeed || 1), 0.25, 4);

      if (String(prev.ttsProvider || '').trim().toLowerCase() === nextProvider
          && String(prev.ttsModel || '') === nextModel
          && String(prev.ttsVoice || '') === nextVoice
          && String(prev.ttsLang || '') === nextLang
          && String(prev.ttsFormat || '').toLowerCase() === nextFormat
          && Number(prev.ttsSpeed || 1) === nextSpeed) {
        return prev;
      }

      return {
        ...prev,
        ttsProvider: nextProvider,
        ttsModel: nextModel,
        ttsVoice: nextVoice,
        ttsLang: nextLang,
        ttsFormat: nextFormat,
        ttsSpeed: nextSpeed
      };
    });
  }, [form.ttsProvider, ttsProviderOptions, ttsOptions.defaults.browserLanguage, ttsOptions.defaults.browserPreferredVoice]);

  const setTopicAsSource = () => {
    if (!selectedTopic?.description) {
      setError('Select a topic with a description before applying it to source text.');
      return;
    }
    setField(
      'sourceText',
      `Learning topic: ${selectedTopic.description}\nExplain the terminology first, then demonstrate the drawing process with correct dimensions, shape, and size.`
    );
  };

  const applyMaterialTextAsSource = async () => {
    if (!selectedMaterial?.id) {
      setError('Select a source material before loading text.');
      return;
    }

    setError('');
    setStatus('Loading source material text...');
    setMaterialTextBusy(true);
    try {
      const res = await fetch(`${API_ROOT}/Content/materials/${selectedMaterial.id}/text`);
      if (!res.ok) throw new Error(await res.text());
      const payload = await res.json();
      const text = String(payload?.text || '').trim();
      if (!text) {
        throw new Error('Selected material has no extracted text.');
      }

      setField('sourceText', text);
      setStatus(`Loaded source text from "${selectedMaterial.title || selectedMaterial.fileName || `Material ${selectedMaterial.id}`}".`);
    } catch (e) {
      setError(`Failed to load source material text: ${e?.message || e}`);
    } finally {
      setMaterialTextBusy(false);
    }
  };

  const applyMaterialPathForLtx = () => {
    const candidate = String(selectedMaterial?.filePath || selectedMaterial?.url || '').trim();
    if (!candidate) {
      setError('Selected material does not expose a file path or URL.');
      return;
    }
    setField('ltxSourcePath', candidate);
    setStatus('LTX conditioning path updated from selected source material.');
  };

  const applyMaterialPathForChapter = () => {
    const candidate = String(
      selectedMaterial?.filePath
      || form.ltxSourcePath
      || renderResult?.local?.outputVideoPath
      || ''
    ).trim();
    if (!candidate) {
      setError('No local source path is available yet for chapter preparation.');
      return;
    }
    setChapterField('sourcePath', candidate);
    setStatus('Chapter source path updated from the current material/video path.');
  };

  const prepareChapterAssets = async () => {
    if (chapterBusy) return;

    const sourcePath = String(chapterForm.sourcePath || '').trim();
    if (!sourcePath) {
      setError('Enter the chapter video or audio path before preparing subtitles.');
      return;
    }

    setError('');
    setStatus('Preparing chapter transcript and subtitles...');
    setChapterBusy(true);
    setChapterResult(null);

    try {
      const res = await fetch(`${API_ROOT}/TextToVideo/chapter-workflow`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          projectTitle: form.projectTitle,
          chapterTitle: chapterForm.chapterTitle || selectedTopic?.description || 'Chapter 1',
          sourcePath,
          language: chapterForm.language,
          preferLocalWhisper: Boolean(chapterForm.preferLocalWhisper),
          allowGeminiFallback: Boolean(chapterForm.allowGeminiFallback),
          geminiModel: chapterForm.geminiModel || chapterTools.defaults.geminiModel || '',
          extractAudio: true
        })
      });

      let payload = null;
      try {
        payload = await res.json();
      } catch {
        payload = null;
      }

      if (!res.ok) {
        throw new Error(String(payload?.error || payload?.message || `HTTP ${res.status}`));
      }

      const normalized = normalizeChapterResult(payload || {});
      setChapterResult(normalized);

      if (normalized.success) {
        const provider = normalized.transcriptionProvider || 'transcription';
        setStatus(`Chapter assets ready via ${provider}. Import the .srt into Camtasia and keep the workspace path for reuse.`);
      } else {
        setError(normalized.message || 'Chapter preparation finished without a usable transcription.');
      }
    } catch (e) {
      setError(`Chapter preparation failed: ${e?.message || e}`);
    } finally {
      setChapterBusy(false);
    }
  };

  const copyLtxInferenceCommand = async () => {
    const sourcePath = String(form.ltxSourcePath || selectedMaterial?.filePath || selectedMaterial?.url || '').trim();
    if (!sourcePath) {
      setError('Set an LTX source material path first.');
      return;
    }

    const promptSource = String(
      activeScene?.visualPrompt ||
      activeScene?.narration ||
      selectedTopic?.description ||
      form.projectTitle ||
      'Educational instructional video'
    )
      .replace(/\r?\n+/g, ' ')
      .replace(/\s+/g, ' ')
      .trim();

    const prompt = promptSource.replace(/"/g, '\\"');
    const conditioningPath = sourcePath.replace(/"/g, '\\"');
    const command = [
      'cd C:\\ETDP\\LTX-Video',
      '.\\.venv312\\Scripts\\python.exe inference.py --prompt "' +
        prompt +
        '" --conditioning_media_paths "' +
        conditioningPath +
        '" --conditioning_start_frames 0 --height 704 --width 1216 --num_frames 121 --seed 171198 --pipeline_config configs/ltxv-13b-0.9.8-distilled.yaml'
    ].join('\n');

    try {
      await navigator.clipboard.writeText(command);
      setStatus('LTX inference command copied to clipboard.');
    } catch (e) {
      setError(`Failed to copy LTX command: ${e?.message || e}`);
    }
  };

  const generateVideoWithFallback = async () => {
    if (renderBusy) return;

    let targetScenes = scenes;
    const promptSource = String(
      form.sourceText ||
      selectedTopic?.description ||
      form.projectTitle ||
      DEFAULT_SOURCE_TEXT
    ).trim();

    setError('');
    setStatus('Generating video with Google Gemini & ETDP Studio...');
    setRenderBusy(true);
    setRenderResult(null);

    try {
      const res = await fetch(`${API_ROOT}/TextToVideo/generate`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          prompt: promptSource,
          targetDurationSec: form.targetDurationSec,
          sceneDurationSec: form.sceneDurationSec,
          visualPreset: form.visualPreset,
          sourceMaterialId: asInt(form.sourceMaterialId, 0)
        })
      });

      let payload = null;
      try {
        payload = await res.json();
      } catch { }

      if (res.ok && payload) {
        setRenderResult(payload);
        if (Array.isArray(payload.scenes) && payload.scenes.length > 0) {
          targetScenes = payload.scenes.map(createScene);
          setScenes(targetScenes);
          setPreviewSceneIndex(0);
        }
      }
    } catch (e) {
      console.warn('Backend video generate call fallback to local:', e);
    }

    if (!targetScenes.length) {
      targetScenes = buildStoryboard({
        briefText: promptSource,
        outcomeLabel: selectedTopic?.description || form.projectTitle,
        visualPreset: form.visualPreset,
        sceneDurationSec: form.sceneDurationSec,
        targetDurationSec: form.targetDurationSec,
        includeQuizScene: form.includeQuizScene
      });
      setScenes(targetScenes);
    }

    setRenderBusy(false);
    setStatus('Storyboard ready. Encoding and exporting video...');
    await exportPreviewVideo(targetScenes);
  };

  const generateScenes = async () => {
    setGenerating(true);
    setStatus('Generating storyboard with Google Gemini...');
    setError('');
    try {
      const source = String(form.sourceText || '').trim() || String(selectedTopic?.description || '').trim();
      if (!source) {
        setError('Enter source text or select a topic before generating.');
        return;
      }

      let generated = [];
      try {
        const res = await fetch(`${API_ROOT}/TextToVideo/storyboard`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            prompt: source,
            targetDurationSec: form.targetDurationSec,
            sceneDurationSec: form.sceneDurationSec
          })
        });
        if (res.ok) {
          const payload = await res.json();
          if (Array.isArray(payload?.scenes) && payload.scenes.length > 0) {
            generated = payload.scenes.map(createScene);
          }
        }
      } catch { }

      if (!generated.length) {
        const topicLabel = selectedTopic?.description || 'the selected learning topic';
        generated = buildStoryboard({
          briefText: source,
          outcomeLabel: topicLabel,
          visualPreset: visualPresets.find((v) => v.value === form.visualPreset)?.label || form.visualPreset,
          sceneDurationSec: form.sceneDurationSec,
          targetDurationSec: form.targetDurationSec,
          includeQuizScene: Boolean(form.includeQuizScene)
        });
      }

      setScenes(generated);
      setPreviewSceneIndex(0);
      setIsPlaying(false);
      setStatus(`Generated ${generated.length} scenes (${generated.reduce((s, x) => s + x.durationSec, 0)}s total).`);
    } catch (e) {
      setError(`Generation failed: ${e?.message || e}`);
    } finally {
      setGenerating(false);
    }
  };

  useEffect(() => {
    if (!autoGeneratePending) return;
    if (loading || generating) return;
    const source = String(form.sourceText || '').trim();
    const qid = asInt(form.qualificationId, 0);
    if (!source || qid <= 0) return;
    generateScenes();
    setAutoGeneratePending(false);
  }, [autoGeneratePending, loading, generating, form.sourceText, form.qualificationId, selectedTopic]);

  const updateScene = (sceneId, field, value) => {
    setScenes((prev) => prev.map((scene) => {
      if (scene.id !== sceneId) return scene;
      if (field === 'durationSec') {
        return { ...scene, durationSec: clamp(asInt(value, scene.durationSec), 4, 180) };
      }
      if (field === 'transition') {
        return { ...scene, transition: transitions.includes(String(value)) ? String(value) : 'cut' };
      }
      return { ...scene, [field]: String(value || '') };
    }));
  };

  const moveScene = (sceneId, direction) => {
    setScenes((prev) => {
      const index = prev.findIndex((scene) => scene.id === sceneId);
      if (index < 0) return prev;
      const target = direction === 'up' ? index - 1 : index + 1;
      if (target < 0 || target >= prev.length) return prev;

      const next = [...prev];
      const temp = next[index];
      next[index] = next[target];
      next[target] = temp;
      return next;
    });
  };

  const duplicateScene = (sceneId) => {
    setScenes((prev) => {
      const index = prev.findIndex((scene) => scene.id === sceneId);
      if (index < 0) return prev;
      const clone = createScene({ ...prev[index], id: undefined, title: `${prev[index].title} (Copy)` });
      const next = [...prev];
      next.splice(index + 1, 0, clone);
      return next;
    });
  };

  const removeScene = (sceneId) => {
    setScenes((prev) => prev.filter((scene) => scene.id !== sceneId));
  };

  const addScene = () => {
    setScenes((prev) => [
      ...prev,
      createScene({
        title: `Scene ${prev.length + 1}: New Scene`,
        narration: '',
        onScreenText: '',
        visualPrompt: '',
        durationSec: clamp(asInt(form.sceneDurationSec, 20), 8, 90),
        transition: 'cut'
      })
    ]);
  };

  const stopPlayback = () => {
    setIsPlaying(false);
    setPreviewElapsedSec(0);
    setTtsBusy(false);
    lastAutoNarrationKeyRef.current = '';
    cancelNarration();
  };

  const playPause = () => {
    if (!activeScene) return;
    if (isPlaying) {
      setIsPlaying(false);
      lastAutoNarrationKeyRef.current = '';
      cancelNarration();
      return;
    }
    setError('');
    setIsPlaying(true);
  };

  const jumpToScene = (index) => {
    setPreviewSceneIndex(clamp(asInt(index, 0), 0, Math.max(0, scenes.length - 1)));
    setPreviewElapsedSec(0);
  };

  const speakScene = async () => {
    await playSceneNarration(activeScene, { announceStatus: true });
  };

  const baseName = slugify(form.projectTitle || selectedTopic?.code || 'text-to-video');

  const buildProjectPayload = () => ({
    meta: {
      createdAtUtc: new Date().toISOString(),
      projectTitle: form.projectTitle,
      qualificationId: asInt(form.qualificationId, 0),
      qualificationLabel: selectedQualification ? `${selectedQualification.code} - ${selectedQualification.description}` : '',
      topicId: asInt(form.topicId, 0),
      topicCode: selectedTopic?.code || '',
      topicDescription: selectedTopic?.description || '',
      sourceMaterialId: asInt(form.sourceMaterialId, 0),
      sourceMaterialTitle: selectedMaterial?.title || selectedMaterial?.fileName || '',
      sourceMaterialFilePath: selectedMaterial?.filePath || '',
      sourceMaterialUrl: selectedMaterial?.url || '',
      ltxSourcePath: form.ltxSourcePath,
      voiceStyle: form.voiceStyle,
      ttsProvider: form.ttsProvider,
      ttsModel: form.ttsModel,
      ttsVoice: form.ttsVoice,
      ttsLang: form.ttsLang,
      ttsPreferredVoice: form.ttsPreferredVoice,
      ttsFormat: form.ttsFormat,
      ttsSpeed: clamp(Number(form.ttsSpeed || 1), 0.25, 4),
      visualPreset: form.visualPreset,
      aspectRatio: form.aspectRatio,
      targetDurationSec: clamp(asInt(form.targetDurationSec, 0), 0, 900),
      totalDurationSec
    },
    sourceText: form.sourceText,
    scenes
  });

  const exportProjectJson = () => {
    const payload = buildProjectPayload();
    downloadFile(`${baseName}.json`, JSON.stringify(payload, null, 2), 'application/json;charset=utf-8');
    setStatus('Project JSON exported.');
  };

  const exportSrt = () => {
    if (!scenes.length) {
      setError('Generate at least one scene before exporting subtitles.');
      return;
    }
    let cursor = 0;
    const lines = [];

    scenes.forEach((scene, index) => {
      const start = cursor;
      const end = cursor + clamp(asInt(scene.durationSec, 0), 1, 180);
      const text = String(scene.onScreenText || scene.narration || scene.title).trim();
      lines.push(String(index + 1));
      lines.push(`${toTimestamp(start)} --> ${toTimestamp(end)}`);
      lines.push(text || `Scene ${index + 1}`);
      lines.push('');
      cursor = end;
    });

    downloadFile(`${baseName}.srt`, lines.join('\n'), 'text/plain;charset=utf-8');
    setStatus('Subtitles exported as SRT.');
  };

  const exportShotlistCsv = () => {
    if (!scenes.length) {
      setError('Generate at least one scene before exporting shot list.');
      return;
    }
    let cursor = 0;
    const rows = [
      ['Scene', 'StartSec', 'EndSec', 'DurationSec', 'Title', 'Narration', 'OnScreenText', 'VisualPrompt', 'Transition']
    ];

    scenes.forEach((scene, index) => {
      const start = cursor;
      const end = cursor + clamp(asInt(scene.durationSec, 0), 1, 180);
      rows.push([
        index + 1,
        start,
        end,
        end - start,
        scene.title,
        scene.narration,
        scene.onScreenText,
        scene.visualPrompt,
        scene.transition
      ]);
      cursor = end;
    });

    const csv = rows.map((row) => row.map(toCsvValue).join(',')).join('\n');
    downloadFile(`${baseName}-shotlist.csv`, csv, 'text/csv;charset=utf-8');
    setStatus('Shot list exported as CSV.');
  };

  const exportPreviewVideo = async (targetScenes) => {
    const listToRender = Array.isArray(targetScenes) && targetScenes.length > 0 ? targetScenes : scenes;
    if (!listToRender.length) {
      setError('Generate at least one scene before exporting video.');
      return;
    }

    setError('');
    setStatus('Encoding 16:9 educational presentation video (24 fps)...');
    setVideoExportBusy(true);

    let stream = null;
    try {
      const { width, height } = resolveVideoDimensions(form.aspectRatio);
      const fps = 24;
      const canvas = document.createElement('canvas');
      canvas.width = width;
      canvas.height = height;
      const ctx = canvas.getContext('2d');
      if (!ctx) throw new Error('Canvas renderer is unavailable.');

      stream = canvas.captureStream(fps);
      const preferredMime = MediaRecorder.isTypeSupported('video/webm;codecs=vp9')
        ? 'video/webm;codecs=vp9'
        : MediaRecorder.isTypeSupported('video/webm;codecs=vp8')
          ? 'video/webm;codecs=vp8'
          : 'video/webm';
      const recorder = new MediaRecorder(stream, {
        mimeType: preferredMime,
        videoBitsPerSecond: 6_000_000
      });

      const chunks = [];
      recorder.ondataavailable = (event) => {
        if (event.data && event.data.size > 0) chunks.push(event.data);
      };

      const stopPromise = new Promise((resolve, reject) => {
        recorder.onstop = () => resolve();
        recorder.onerror = (event) => reject(new Error(event?.error?.message || 'Video recorder failed.'));
      });

      recorder.start(100);

      // Paced presentation recording: 48 frames per scene = 2.0s per scene at 24fps
      const framesPerScene = 48;
      const frameDelayMs = 35;

      for (let sceneIndex = 0; sceneIndex < listToRender.length; sceneIndex += 1) {
        const scene = listToRender[sceneIndex];
        setStatus(`Encoding scene ${sceneIndex + 1}/${listToRender.length}: ${scene.title || ''}...`);

        for (let frame = 0; frame < framesPerScene; frame += 1) {
          drawStoryboardFrame({
            ctx,
            width,
            height,
            scene,
            sceneIndex,
            sceneCount: listToRender.length,
            progress: framesPerScene <= 1 ? 1 : frame / (framesPerScene - 1)
          });
          await wait(frameDelayMs);
        }
      }

      await wait(150);
      recorder.stop();
      await stopPromise;

      const blob = new Blob(chunks, { type: preferredMime || 'video/webm' });
      downloadFile(`${baseName}-video.webm`, blob, blob.type || 'video/webm');
      setStatus('Video exported successfully (.webm)! 16:9 full presentation video ready.');
    } catch (e) {
      setError(`Video export failed: ${e?.message || e}`);
    } finally {
      if (stream) {
        try {
          stream.getTracks().forEach((track) => track.stop());
        } catch { }
      }
      setVideoExportBusy(false);
    }
  };

  const exportStitchedImages = async () => {
    if (!scenes.length) {
      setError('Generate scenes before exporting slide images.');
      return;
    }

    setStatus('Rendering high-resolution presentation slide images (1280x720)...');
    try {
      const { width, height } = resolveVideoDimensions(form.aspectRatio);
      const canvas = document.createElement('canvas');
      canvas.width = width;
      canvas.height = height;
      const ctx = canvas.getContext('2d');

      for (let i = 0; i < scenes.length; i++) {
        const scene = scenes[i];
        drawStoryboardFrame({
          ctx,
          width,
          height,
          scene,
          sceneIndex: i,
          sceneCount: scenes.length,
          progress: 1.0
        });

        const dataUrl = canvas.toDataURL('image/png');
        const cleanTitle = (scene.title || `scene-${i+1}`).replace(/[^a-zA-Z0-9_-]/g, '_').slice(0, 30);
        const fileName = `${baseName}-slide-${String(i + 1).padStart(2, '0')}-${cleanTitle}.png`;
        
        const a = document.createElement('a');
        a.href = dataUrl;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        await wait(250);
      }
      setStatus(`Exported ${scenes.length} slide images successfully as PNG!`);
    } catch (e) {
      setError(`Slide images export failed: ${e?.message || e}`);
    }
  };

  const copyPromptPack = async () => {
    if (!scenes.length) {
      setError('Generate at least one scene before copying the prompt pack.');
      return;
    }

    const text = [
      `Project: ${form.projectTitle}`,
      `Topic: ${selectedTopic?.code || ''} ${selectedTopic?.description || ''}`.trim(),
      `Aspect Ratio: ${form.aspectRatio}`,
      `Voice Style: ${form.voiceStyle}`,
      `TTS Provider: ${form.ttsProvider || 'browser'}`,
      `TTS Model: ${form.ttsModel || '(browser-default)'}`,
      `TTS Voice: ${form.ttsVoice || form.ttsPreferredVoice || '(auto)'}`,
      '',
      ...scenes.map((scene, index) => [
        `Scene ${index + 1} (${scene.durationSec}s, ${scene.transition})`,
        `Title: ${scene.title}`,
        `Narration: ${scene.narration}`,
        `On Screen Text: ${scene.onScreenText}`,
        `Visual Prompt: ${scene.visualPrompt}`
      ].join('\n')),
      '',
      'Render order: scene visuals + narration + subtitles.'
    ].join('\n\n');

    try {
      await navigator.clipboard.writeText(text);
      setStatus('Prompt pack copied to clipboard.');
    } catch (e) {
      setError(`Failed to copy prompt pack: ${e?.message || e}`);
    }
  };

  const triggerImport = () => {
    if (importFileRef.current) importFileRef.current.click();
  };

  const importProjectJson = async (event) => {
    const file = event.target.files?.[0];
    if (!file) return;

    setStatus('');
    setError('');
    try {
      const raw = await file.text();
      const parsed = JSON.parse(raw);
      const importedScenes = (Array.isArray(parsed?.scenes) ? parsed.scenes : []).map(createScene);
      if (!importedScenes.length) throw new Error('No scenes found in selected JSON file.');

      const meta = parsed?.meta || {};
      setScenes(importedScenes);
      setForm((prev) => ({
        ...prev,
        projectTitle: String(meta?.projectTitle || prev.projectTitle),
        sourceMaterialId: String(meta?.sourceMaterialId || prev.sourceMaterialId),
        ltxSourcePath: String(meta?.ltxSourcePath || meta?.sourceMaterialFilePath || prev.ltxSourcePath || ''),
        voiceStyle: String(meta?.voiceStyle || prev.voiceStyle),
        ttsProvider: String(meta?.ttsProvider || prev.ttsProvider || 'browser').toLowerCase(),
        ttsModel: String(meta?.ttsModel || prev.ttsModel || ''),
        ttsVoice: String(meta?.ttsVoice || prev.ttsVoice || ''),
        ttsLang: String(meta?.ttsLang || prev.ttsLang || 'en-ZA'),
        ttsPreferredVoice: String(meta?.ttsPreferredVoice || prev.ttsPreferredVoice || 'Microsoft'),
        ttsFormat: String(meta?.ttsFormat || prev.ttsFormat || 'mp3').toLowerCase(),
        ttsSpeed: clamp(Number(meta?.ttsSpeed || prev.ttsSpeed || 1), 0.25, 4),
        visualPreset: String(meta?.visualPreset || prev.visualPreset),
        aspectRatio: String(meta?.aspectRatio || prev.aspectRatio),
        targetDurationSec: clamp(asInt(meta?.targetDurationSec, prev.targetDurationSec), 30, 900),
        sourceText: String(parsed?.sourceText || prev.sourceText)
      }));
      setPreviewSceneIndex(0);
      setIsPlaying(false);
      setStatus(`Imported ${importedScenes.length} scenes from ${file.name}.`);
    } catch (e) {
      setError(`Import failed: ${e?.message || e}`);
    } finally {
      event.target.value = '';
    }
  };

  return (
    <div className="page-container video-editor-page">
      <h2>Text-to-Video Editor</h2>
      <p>Convert curriculum topics into editable video scenes, preview timing, and export a production package.</p>
      <p className="video-hint">
        Note: this editor can export a visual preview video as <strong>.webm</strong>. This editor exports a 16:9 presentation video as <strong>.webm</strong> or high-resolution slide images. TTS (browser or Google Gemini) is preview-ready, but the chapter workflow below can turn your recorded chapter into a transcript and <strong>.srt</strong> subtitles for Camtasia.
      </p>

      <section className="video-editor-panel">
        <div className="video-preview-header">
          <h3>Chapter Narration Test</h3>
          <div>Prepare subtitle assets from a recorded chapter video or audio file.</div>
        </div>
        <div className="video-editor-grid">
          <label className="full-width">
            Chapter Source Path
            <input
              className="mainpage-input"
              value={chapterForm.sourcePath}
              onChange={(e) => setChapterField('sourcePath', e.target.value)}
              placeholder="Absolute path to your recorded chapter .mp4, .wav, .mp3, or similar file."
            />
          </label>
          <label>
            Chapter Title
            <input
              className="mainpage-input"
              value={chapterForm.chapterTitle}
              onChange={(e) => setChapterField('chapterTitle', e.target.value)}
              placeholder="Chapter 1"
            />
          </label>
          <label>
            Transcription Language
            <input
              className="mainpage-input"
              value={chapterForm.language}
              onChange={(e) => setChapterField('language', e.target.value)}
              placeholder="en"
            />
          </label>
          <label>
            Google Gemini Transcription Model
            <input
              className="mainpage-input"
              value={chapterForm.geminiModel}
              onChange={(e) => setChapterField('geminiModel', e.target.value)}
              disabled={!chapterTools.tools.gemini.available}
              placeholder={chapterTools.defaults.geminiModel || 'gemini-3.8-flash'}
            />
          </label>
          <label className="video-checkbox">
            Prefer Local Whisper
            <input
              type="checkbox"
              checked={Boolean(chapterForm.preferLocalWhisper)}
              onChange={(e) => setChapterField('preferLocalWhisper', e.target.checked)}
              disabled={!chapterTools.tools.whisper.available}
            />
          </label>
          <label className="video-checkbox">
            Allow Google Gemini Fallback
            <input
              type="checkbox"
              checked={Boolean(chapterForm.allowGeminiFallback)}
              onChange={(e) => setChapterField('allowGeminiFallback', e.target.checked)}
              disabled={!chapterTools.tools.gemini.available}
            />
          </label>
        </div>

        <div className="video-editor-actions">
          <button type="button" onClick={loadChapterTools} disabled={chapterToolsBusy}>
            {chapterToolsBusy ? 'Refreshing Tools...' : 'Refresh Chapter Tools'}
          </button>
          <button type="button" onClick={applyMaterialPathForChapter} disabled={!selectedMaterial && !form.ltxSourcePath && !renderResult?.local?.outputVideoPath}>
            Use Current Material / Video Path
          </button>
          <button type="button" onClick={prepareChapterAssets} disabled={chapterBusy}>
            {chapterBusy ? 'Preparing Chapter...' : 'Prepare Chapter Audio + Subtitles'}
          </button>
        </div>

        <div className="video-chapter-grid">
          <div className="video-hint video-path-block">
            <strong>Workspace Root</strong><br />
            {chapterTools.workspaceRoot || 'Not resolved yet.'}
          </div>
          <div className="video-hint video-path-block">
            <strong>FFmpeg</strong><br />
            {chapterTools.tools.ffmpeg.path || chapterTools.tools.ffmpeg.reason}
          </div>
          <div className="video-hint video-path-block">
            <strong>Local Whisper</strong><br />
            {chapterTools.tools.whisper.cliPath || chapterTools.tools.whisper.reason}
            {chapterTools.tools.whisper.modelPath ? <><br />Model: {chapterTools.tools.whisper.modelPath}</> : null}
          </div>
          <div className="video-hint video-path-block">
            <strong>Google Gemini Fallback</strong><br />
            {chapterTools.tools.gemini.available
              ? `${chapterTools.tools.gemini.model || chapterTools.defaults.geminiModel || 'Configured'} ready.`
              : (chapterTools.tools.gemini.reason || 'Not configured.')}
          </div>
        </div>

        {chapterResult ? (
          <div className="video-chapter-grid">
            <div className="video-hint video-path-block">
              <strong>Transcription Provider</strong><br />
              {chapterResult.transcriptionProvider || 'Unknown'}
              {chapterResult.transcriptionModel ? ` | ${chapterResult.transcriptionModel}` : ''}
            </div>
            <div className="video-hint video-path-block">
              <strong>Workspace</strong><br />
              {chapterResult.workspacePath || 'No workspace returned.'}
            </div>
            <div className="video-hint video-path-block">
              <strong>Transcript</strong><br />
              {chapterResult.transcriptPath || 'Not generated.'}
            </div>
            <div className="video-hint video-path-block">
              <strong>Subtitles (SRT)</strong><br />
              {chapterResult.srtPath || 'Not generated.'}
            </div>
            <div className="video-hint video-path-block">
              <strong>Extracted Audio</strong><br />
              {chapterResult.extractedAudioPath || 'No separate audio file was created.'}
            </div>
            {chapterResult.transcriptPreview ? (
              <label className="full-width">
                Transcript Preview
                <textarea
                  className="mainpage-input"
                  rows={8}
                  value={chapterResult.transcriptPreview}
                  readOnly
                />
              </label>
            ) : null}
            {chapterResult.warnings.length ? (
              <div className="video-hint full-width">
                <strong>Warnings</strong><br />
                {chapterResult.warnings.join(' | ')}
              </div>
            ) : null}
          </div>
        ) : null}
      </section>

      <section className="video-editor-panel">
        <div className="video-editor-grid">
          <label>
            Project Title
            <input
              className="mainpage-input"
              value={form.projectTitle}
              onChange={(e) => setField('projectTitle', e.target.value)}
            />
          </label>
          <label>
            Qualification
            <select
              className="mainpage-input"
              value={form.qualificationId}
              onChange={(e) => setField('qualificationId', e.target.value)}
              disabled={loading}
            >
              <option value="">Select qualification</option>
              {qualifications.map((q) => (
                <option key={q.id} value={q.id}>
                  {q.code || q.id} - {q.description || 'No description'}
                </option>
              ))}
            </select>
          </label>
          <label>
            Topic
            <select
              className="mainpage-input"
              value={form.topicId}
              onChange={(e) => setField('topicId', e.target.value)}
              disabled={topics.length === 0}
            >
              <option value="">Select topic</option>
              {topics.map((topic) => (
                <option key={topic.id} value={topic.id}>
                  {topic.subjectCode ? `${topic.subjectCode} | ` : ''}{topic.code || `TOP-${topic.id}`} - {topic.description || 'No description'}
                </option>
              ))}
            </select>
          </label>
          <label>
            Source Material
            <select
              className="mainpage-input"
              value={form.sourceMaterialId}
              onChange={(e) => setField('sourceMaterialId', e.target.value)}
              disabled={materials.length === 0}
            >
              <option value="">Select source material</option>
              {materials.map((material) => (
                <option key={material.id} value={material.id}>
                  {material.title || material.fileName || `Material ${material.id}`} ({material.fileType || 'file'})
                </option>
              ))}
            </select>
          </label>
          <label>
            Target Duration (seconds)
            <input
              className="mainpage-input"
              type="number"
              min={30}
              max={900}
              value={form.targetDurationSec}
              onChange={(e) => setField('targetDurationSec', e.target.value)}
            />
          </label>
          <label>
            Average Scene Length (seconds)
            <input
              className="mainpage-input"
              type="number"
              min={8}
              max={90}
              value={form.sceneDurationSec}
              onChange={(e) => setField('sceneDurationSec', e.target.value)}
            />
          </label>
          <label>
            Visual Style
            <select
              className="mainpage-input"
              value={form.visualPreset}
              onChange={(e) => setField('visualPreset', e.target.value)}
            >
              {visualPresets.map((preset) => (
                <option key={preset.value} value={preset.value}>{preset.label}</option>
              ))}
            </select>
          </label>
          <label>
            Voice Style
            <input
              className="mainpage-input"
              value={form.voiceStyle}
              onChange={(e) => setField('voiceStyle', e.target.value)}
            />
          </label>
          <label>
            TTS Provider
            <select
              className="mainpage-input"
              value={form.ttsProvider}
              onChange={(e) => setField('ttsProvider', String(e.target.value || 'browser').toLowerCase())}
              disabled={ttsOptionsBusy || ttsProviderOptions.length === 0}
            >
              {ttsProviderOptions.length === 0 ? <option value="browser">Browser speechSynthesis</option> : null}
              {ttsProviderOptions.map((provider) => (
                <option key={provider.id} value={provider.id} disabled={!provider.available}>
                  {provider.label}{provider.available ? '' : ' (Unavailable)'}
                </option>
              ))}
            </select>
          </label>
          <label>
            TTS Model
            {selectedTtsProvider?.id === 'gemini' && Array.isArray(selectedTtsProvider?.models) && selectedTtsProvider.models.length > 0 ? (
              <select
                className="mainpage-input"
                value={form.ttsModel}
                onChange={(e) => setField('ttsModel', e.target.value)}
                disabled={!selectedTtsProvider.available}
              >
                {selectedTtsProvider.models.map((model) => (
                  <option key={model} value={model}>{model}</option>
                ))}
              </select>
            ) : (
              <input
                className="mainpage-input"
                value={selectedTtsProvider?.id === 'gemini' ? form.ttsModel : 'browser-default'}
                onChange={(e) => setField('ttsModel', e.target.value)}
                disabled={selectedTtsProvider?.id !== 'gemini' || !selectedTtsProvider?.available}
              />
            )}
          </label>
          <label>
            TTS Voice
            {selectedTtsProvider?.id === 'gemini' && Array.isArray(selectedTtsProvider?.voices) && selectedTtsProvider.voices.length > 0 ? (
              <select
                className="mainpage-input"
                value={form.ttsVoice}
                onChange={(e) => setField('ttsVoice', e.target.value)}
                disabled={!selectedTtsProvider.available}
              >
                {selectedTtsProvider.voices.map((voice) => (
                  <option key={voice} value={voice}>{voice}</option>
                ))}
              </select>
            ) : (
              <input
                className="mainpage-input"
                value={selectedTtsProvider?.id === 'gemini' ? form.ttsVoice : form.ttsPreferredVoice}
                onChange={(e) => setField(selectedTtsProvider?.id === 'gemini' ? 'ttsVoice' : 'ttsPreferredVoice', e.target.value)}
                disabled={selectedTtsProvider?.id === 'gemini' ? !selectedTtsProvider?.available : false}
              />
            )}
          </label>
          <label>
            TTS Speed
            <input
              className="mainpage-input"
              type="number"
              min={0.25}
              max={4}
              step={0.05}
              value={form.ttsSpeed}
              onChange={(e) => setField('ttsSpeed', clamp(Number(e.target.value || 1), 0.25, 4))}
            />
          </label>
          <label>
            Browser TTS Language
            <input
              className="mainpage-input"
              value={form.ttsLang}
              onChange={(e) => setField('ttsLang', e.target.value)}
              disabled={selectedTtsProvider?.id === 'gemini'}
              placeholder="en-ZA"
            />
          </label>
          <label>
            Audio Format
            <select
              className="mainpage-input"
              value={form.ttsFormat}
              onChange={(e) => setField('ttsFormat', String(e.target.value || 'mp3').toLowerCase())}
              disabled={selectedTtsProvider?.id !== 'gemini'}
            >
              <option value="mp3">mp3</option>
              <option value="wav">wav</option>
              <option value="aac">aac</option>
              <option value="flac">flac</option>
              <option value="opus">opus</option>
            </select>
          </label>
          {selectedTtsProvider && !selectedTtsProvider.available ? (
            <div className="video-hint">
              TTS provider unavailable: {selectedTtsProvider.reason || 'Not configured.'}
            </div>
          ) : null}
          <label>
            Aspect Ratio
            <select
              className="mainpage-input"
              value={form.aspectRatio}
              onChange={(e) => setField('aspectRatio', e.target.value)}
            >
              <option value="16:9">16:9 (Landscape)</option>
              <option value="9:16">9:16 (Vertical)</option>
              <option value="1:1">1:1 (Square)</option>
            </select>
          </label>
          <label className="video-checkbox">
            Include Knowledge Check Scene
            <input
              type="checkbox"
              checked={Boolean(form.includeQuizScene)}
              onChange={(e) => setField('includeQuizScene', e.target.checked)}
            />
          </label>
          <label className="full-width">
            LTX Conditioning Source Path
            <input
              className="mainpage-input"
              value={form.ltxSourcePath}
              onChange={(e) => setField('ltxSourcePath', e.target.value)}
              placeholder="Absolute file path (or URL) to conditioning image/video for LTX inference."
            />
          </label>
        </div>

        <div className="video-editor-actions">
          <button type="button" onClick={setTopicAsSource} disabled={!selectedTopic}>Use Topic as Source Text</button>
          <button type="button" onClick={applyMaterialTextAsSource} disabled={!selectedMaterial || materialTextBusy}>
            {materialTextBusy ? 'Loading Material Text...' : 'Use Source Material Text'}
          </button>
          <button type="button" onClick={applyMaterialPathForLtx} disabled={!selectedMaterial}>
            Use Source Material Path for LTX
          </button>
          <button type="button" onClick={generateScenes} disabled={generating}>
            {generating ? 'Generating...' : 'Generate Storyboard (Gemini AI)'}
          </button>
          <button type="button" onClick={copyLtxInferenceCommand} disabled={!form.ltxSourcePath && !selectedMaterial}>
            Copy LTX Inference Command
          </button>
          <button type="button" onClick={generateVideoWithFallback} disabled={renderBusy}>
            {renderBusy ? 'Generating Video...' : 'Generate Video & Export (WebM)'}
          </button>
          <button type="button" onClick={addScene}>Add Empty Scene</button>
        </div>
      </section>

      <section className="video-editor-panel">
        <label>
          Source Text / Lesson Brief
          <textarea
            className="mainpage-input"
            rows={6}
            value={form.sourceText}
            onChange={(e) => setField('sourceText', e.target.value)}
            placeholder="Paste the lesson brief, topic description, or script outline."
          />
        </label>
        {selectedTopic ? (
          <div className="video-hint">
            Selected Topic: <strong>{selectedTopic.code || 'Topic'}</strong> {selectedTopic.description}
          </div>
        ) : null}
        {selectedMaterial ? (
          <div className="video-hint">
            Selected Source Material: <strong>{selectedMaterial.title || selectedMaterial.fileName || `Material ${selectedMaterial.id}`}</strong>
            {selectedMaterial.filePath ? ` | Path: ${selectedMaterial.filePath}` : ''}
            {!selectedMaterial.filePath && selectedMaterial.url ? ` | Url: ${selectedMaterial.url}` : ''}
          </div>
        ) : null}
      </section>

      {error ? <div className="video-message video-error">{error}</div> : null}
      {status ? <div className="video-message video-status">{status}</div> : null}

      {renderResult ? (
        <section className="video-editor-panel">
          <h3>Generation Result</h3>
          <div className="video-hint">
            Provider: <strong>{String(renderResult?.provider || 'unknown')}</strong> | Success: <strong>{String(Boolean(renderResult?.success))}</strong>
          </div>
          {renderResult?.local?.outputVideoPath ? (
            <div className="video-hint">
              Local output: <strong>{String(renderResult.local.outputVideoPath)}</strong>
            </div>
          ) : null}
          {renderResult?.gemini?.planText ? (
            <label>
              Google Gemini Plan
              <textarea
                className="mainpage-input"
                rows={8}
                value={String(renderResult?.gemini?.planText || renderResult?.openAi?.planText || '')}
                readOnly
              />
            </label>
          ) : null}
          {(renderResult?.local?.stdoutTail || renderResult?.local?.stderrTail) ? (
            <details>
              <summary>Local LTX Logs</summary>
              <pre style={{ whiteSpace: 'pre-wrap', marginTop: '0.65rem' }}>{String(renderResult?.local?.stdoutTail || '')}</pre>
              <pre style={{ whiteSpace: 'pre-wrap' }}>{String(renderResult?.local?.stderrTail || '')}</pre>
            </details>
          ) : null}
        </section>
      ) : null}

      <section className="video-editor-panel">
        <div className="video-preview-header">
          <h3>Timeline Preview</h3>
          <div>
            {scenes.length} scenes | {totalDurationSec}s total
          </div>
        </div>

        <div className="video-editor-timeline">
          {scenes.length === 0 ? <span className="video-empty">No scenes yet. Generate a storyboard to start.</span> : null}
          {scenes.map((scene, index) => (
            <button
              key={scene.id}
              type="button"
              className={`timeline-scene ${index === previewSceneIndex ? 'active' : ''}`}
              style={{ flexGrow: Math.max(1, scene.durationSec) }}
              onClick={() => jumpToScene(index)}
            >
              <span>{index + 1}</span>
              <small>{scene.durationSec}s</small>
            </button>
          ))}
        </div>

        <div className="video-preview-stage">
          <div className={`video-canvas ratio-${form.aspectRatio.replace(':', '-')}`}>
            <div className="video-canvas-overlay">
              <div className="video-canvas-title">{activeScene?.title || 'No Scene Selected'}</div>
              <div className="video-canvas-text">{activeScene?.onScreenText || 'Generate scenes to preview on-screen text.'}</div>
              <div className="video-canvas-progress">
                <div
                  className="video-canvas-progress-fill"
                  style={{
                    width: `${activeScene ? Math.round((previewElapsedSec / Math.max(1, activeScene.durationSec)) * 100) : 0}%`
                  }}
                />
              </div>
            </div>
          </div>

          <div className="video-preview-controls">
            <button type="button" onClick={playPause} disabled={!activeScene}>{isPlaying ? 'Pause' : 'Play'}</button>
            <button type="button" onClick={stopPlayback} disabled={!activeScene}>Stop</button>
            <button type="button" onClick={speakScene} disabled={!activeScene || ttsBusy}>
              {ttsBusy ? 'Starting TTS...' : `Speak Narration (${String(form.ttsProvider || 'browser')})`}
            </button>
            <button type="button" onClick={() => jumpToScene(previewSceneIndex - 1)} disabled={previewSceneIndex <= 0}>Previous Scene</button>
            <button type="button" onClick={() => jumpToScene(previewSceneIndex + 1)} disabled={previewSceneIndex >= scenes.length - 1}>Next Scene</button>
          </div>
        </div>
      </section>

      <section className="video-editor-panel">
        <div className="video-preview-header">
          <h3>Scene Editor</h3>
          <div>Drag order with Up/Down and fine tune each shot.</div>
        </div>

        {scenes.length === 0 ? (
          <div className="video-empty">No scene data available yet.</div>
        ) : (
          <div className="video-scene-list">
            {scenes.map((scene, index) => (
              <article key={scene.id} className={`video-scene-card ${index === previewSceneIndex ? 'active' : ''}`}>
                <header>
                  <strong>{index + 1}. {scene.title || `Scene ${index + 1}`}</strong>
                  <div className="video-scene-actions">
                    <button type="button" onClick={() => jumpToScene(index)}>Preview</button>
                    <button type="button" onClick={() => moveScene(scene.id, 'up')} disabled={index === 0}>Up</button>
                    <button type="button" onClick={() => moveScene(scene.id, 'down')} disabled={index === scenes.length - 1}>Down</button>
                    <button type="button" onClick={() => duplicateScene(scene.id)}>Duplicate</button>
                    <button type="button" onClick={() => removeScene(scene.id)}>Delete</button>
                  </div>
                </header>

                <div className="video-scene-grid">
                  <label>
                    Title
                    <input
                      className="mainpage-input"
                      value={scene.title}
                      onChange={(e) => updateScene(scene.id, 'title', e.target.value)}
                    />
                  </label>
                  <label>
                    Duration (seconds)
                    <input
                      className="mainpage-input"
                      type="number"
                      min={4}
                      max={180}
                      value={scene.durationSec}
                      onChange={(e) => updateScene(scene.id, 'durationSec', e.target.value)}
                    />
                  </label>
                  <label>
                    Transition
                    <select
                      className="mainpage-input"
                      value={scene.transition}
                      onChange={(e) => updateScene(scene.id, 'transition', e.target.value)}
                    >
                      {transitions.map((transition) => (
                        <option key={transition} value={transition}>{transition}</option>
                      ))}
                    </select>
                  </label>
                  <label className="full-width">
                    Narration
                    <textarea
                      className="mainpage-input"
                      rows={3}
                      value={scene.narration}
                      onChange={(e) => updateScene(scene.id, 'narration', e.target.value)}
                    />
                  </label>
                  <label className="full-width">
                    On-Screen Text
                    <textarea
                      className="mainpage-input"
                      rows={3}
                      value={scene.onScreenText}
                      onChange={(e) => updateScene(scene.id, 'onScreenText', e.target.value)}
                    />
                  </label>
                  <label className="full-width">
                    Visual Prompt / Shot Instruction
                    <textarea
                      className="mainpage-input"
                      rows={3}
                      value={scene.visualPrompt}
                      onChange={(e) => updateScene(scene.id, 'visualPrompt', e.target.value)}
                    />
                  </label>
                </div>
              </article>
            ))}
          </div>
        )}
      </section>

      <section className="video-editor-panel">
        <div className="video-preview-header">
          <h3>Export</h3>
          <div>Use these outputs directly, or with your rendering pipeline.</div>
        </div>
        <div className="video-editor-actions">
          <button type="button" onClick={exportPreviewVideo} disabled={scenes.length === 0 || videoExportBusy}>
            {videoExportBusy ? "Rendering Video..." : "Export Video (.webm)"}
          </button>
          <button type="button" onClick={exportStitchedImages} disabled={scenes.length === 0 || videoExportBusy}>
            Export Stitched Slide Images (.png)
          </button>
          <button type="button" onClick={exportStitchedImages} disabled={scenes.length === 0 || videoExportBusy}>
            Export Stitched Slide Images (.png)
          </button>
          <button type="button" onClick={exportProjectJson} disabled={scenes.length === 0}>Export Project JSON</button>
          <button type="button" onClick={exportSrt} disabled={scenes.length === 0}>Export Subtitles (SRT)</button>
          <button type="button" onClick={exportShotlistCsv} disabled={scenes.length === 0}>Export Shot List (CSV)</button>
          <button type="button" onClick={copyPromptPack} disabled={scenes.length === 0}>Copy Prompt Pack</button>
          <button type="button" onClick={triggerImport}>Import Project JSON</button>
          <input
            ref={importFileRef}
            type="file"
            accept=".json,application/json"
            style={{ display: 'none' }}
            onChange={importProjectJson}
          />
        </div>
      </section>
    </div>
  );
}

