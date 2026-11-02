import React, { useEffect, useState, useMemo, useRef } from 'react';
import { useParams, useNavigate, useLocation } from 'react-router-dom';
import { useQualification } from '../context/QualificationContext.jsx';
import './ContentBuilderPage.css';

const API_BASE = '/api';
const API_TOOLKIT = `${API_BASE}/LecturerToolkit`;
const API_CONTENT = `${API_BASE}/Content`;
const API_QUAL = `${API_BASE}/Qualification`;
const API_SUBJECT = `${API_BASE}/Subject`;
const API_TOPIC = `${API_BASE}/Topic`;
const API_CRITERIA = `${API_BASE}/AssessmentCriteria`;

function cleanImportedText(raw) {
  const text = String(raw || '');
  return text
    .replace(/PAGEREF\s+_Toc\d+/gi, ' ')
    .replace(/\\h\s*/gi, ' ')
    .replace(/<[^>]+>/g, ' ')
    .replace(/\s{2,}/g, ' ')
    .trim();
}

function buildRelevantPassage(rawText, query) {
  const text = String(rawText || '');
  const q = String(query || '').trim().toLowerCase();
  if (!text) return '';
  if (!q) return cleanImportedText(text);

  const terms = q
    .split(/[^a-zA-Z0-9]+/)
    .map(t => t.trim().toLowerCase())
    .filter(t => t.length >= 3);
  if (terms.length === 0) return cleanImportedText(text);

  const blocks = text
    .replace(/\r\n/g, '\n')
    .split(/\n{2,}/)
    .map(b => b.trim())
    .filter(Boolean);
  if (blocks.length === 0) return cleanImportedText(text);

  let best = blocks[0];
  let bestScore = -1;
  for (const b of blocks) {
    const lower = b.toLowerCase();
    let score = 0;
    if (lower.includes(q)) score += 20;
    for (const t of terms) {
      if (lower.includes(t)) score += 4;
    }
    if (score > bestScore) {
      bestScore = score;
      best = b;
    }
  }

  const maxLen = 2200;
  const clipped = best.length > maxLen ? `${best.slice(0, maxLen)}...` : best;
  return cleanImportedText(clipped);
}

function splitParagraphBlocks(rawText) {
  return String(rawText || '')
    .replace(/\r\n/g, '\n')
    .split(/\n{2,}/)
    .map(block => block.trim())
    .filter(Boolean);
}

function extractParagraphByIndex(rawText, paragraphIndex) {
  const idx = Number(paragraphIndex || 0);
  if (!Number.isInteger(idx) || idx < 1) return '';
  const blocks = splitParagraphBlocks(rawText);
  if (blocks.length === 0) return '';
  return blocks[idx - 1] || '';
}

// Normalize API responses (backend may return PascalCase or camelCase) to a single shape for dropdowns and cascade
function normQualification(q) {
  if (!q) return null;
  const id = Number(q.Id ?? q.id ?? 0);
  if (!id) return null;
  return {
    id,
    qualificationNumber: String(q.QualificationNumber ?? q.qualificationNumber ?? '').trim(),
    qualificationDescription: String(q.QualificationDescription ?? q.qualificationDescription ?? '').trim()
  };
}
function normSubject(s) {
  if (!s) return null;
  const id = Number(s.Id ?? s.id ?? 0);
  if (!id) return null;
  return {
    id,
    qualificationId: Number(s.QualificationId ?? s.qualificationId ?? 0),
    subjectDescription: String(s.SubjectDescription ?? s.subjectDescription ?? '').trim(),
    subjectCode: String(s.SubjectCode ?? s.subjectCode ?? '').trim()
  };
}
function normTopic(t) {
  if (!t) return null;
  const id = Number(t.Id ?? t.id ?? 0);
  if (!id) return null;
  return {
    id,
    subjectId: Number(t.SubjectId ?? t.subjectId ?? 0),
    qualificationId: Number(t.QualificationId ?? t.qualificationId ?? 0),
    topicCode: String(t.TopicCode ?? t.topicCode ?? '').trim(),
    topicPurpose: String(t.TopicPurpose ?? t.topicPurpose ?? '').trim(),
    topicDescription: String(t.TopicDescription ?? t.topicDescription ?? '').trim(),
    subjectCredits: Number(t.SubjectCredits ?? t.subjectCredits ?? 0) || 0,
    notionalHours: Number(t.NotionalHours ?? t.notionalHours ?? t.NationalHours ?? t.nationalHours ?? 0) || 0,
    periodsPerTopic: Number(t.PeriodsPerTopic ?? t.periodsPerTopic ?? 0) || 0,
    assessmentCriteriaId: Number(t.AssessmentCriteriaId ?? t.assessmentCriteriaId ?? 0),
    assessmentCriteriaDescription: String(t.AssessmentCriteriaDescription ?? t.assessmentCriteriaDescription ?? '').trim()
  };
}
function entryQualificationId(entry) {
  if (!entry) return 0;
  const raw = entry.QualificationsId ?? entry.qualificationsId ?? entry.QualificationId ?? entry.qualificationId;
  return Number(raw ?? 0);
}

function normMaterial(m) {
  if (!m) return null;
  const id = Number(m.Id ?? m.id ?? 0);
  if (!id) return null;
  return {
    id,
    title: String(m.Title ?? m.title ?? `Material ${id}`),
    type: String(m.FileType ?? m.fileType ?? m.type ?? '').trim(),
    qualificationCode: String(m.QualificationCode ?? m.qualificationCode ?? '').trim(),
    qualificationDescription: String(m.QualificationDescription ?? m.qualificationDescription ?? '').trim(),
    knowledgeSourceType: String(m.KnowledgeSourceType ?? m.knowledgeSourceType ?? '').trim()
  };
}

function parseLpnValue(v) {
  const n = Number(String(v ?? '').replace(/[^\d.-]/g, ''));
  return Number.isFinite(n) ? n : Number.POSITIVE_INFINITY;
}

function sortLpnEntriesByLpn(list) {
  const arr = Array.isArray(list) ? [...list] : [];
  arr.sort((a, b) => {
    const aNum = parseLpnValue(a?.lpn ?? a?.Lpn ?? a?.LPN);
    const bNum = parseLpnValue(b?.lpn ?? b?.Lpn ?? b?.LPN);
    if (aNum !== bNum) return aNum - bNum;
    return Number(a?.id ?? a?.Id ?? 0) - Number(b?.id ?? b?.Id ?? 0);
  });
  return arr;
}

function buildCriteriaFromTopics(topicList) {
  const out = [];
  const seen = new Set();
  for (const t of Array.isArray(topicList) ? topicList : []) {
    const criteriaId = Number(t?.assessmentCriteriaId ?? 0);
    if (!criteriaId || seen.has(criteriaId)) continue;
    seen.add(criteriaId);
    out.push({
      id: criteriaId,
      description: String(t?.assessmentCriteriaDescription ?? '').trim(),
      topicId: Number(t?.id ?? 0),
      topicCode: String(t?.topicCode ?? '').trim(),
      topicDescription: String(t?.topicDescription ?? '').trim()
    });
  }
  return out;
}

function readLessonDescription(row) {
  return String(row?.lessonPlanDescription ?? row?.LessonPlanDescription ?? '').trim();
}

function readLpn(row) {
  return String(row?.lpn ?? row?.Lpn ?? row?.LPN ?? '').trim();
}

function readToolkitEntryId(row) {
  return Number(row?.id ?? row?.Id ?? 0);
}

function readToolkitQualificationId(row) {
  return Number(row?.qualificationsId ?? row?.QualificationsId ?? 0);
}

function readToolkitSubjectCode(row) {
  return String(row?.subjectCode ?? row?.SubjectCode ?? '').trim();
}

function readToolkitCriteriaId(row) {
  return Number(row?.assessmentCriteriaId ?? row?.AssessmentCriteriaId ?? 0);
}

function readToolkitCriteriaDescription(row) {
  return String(row?.assessmentCriteriaDescription ?? row?.AssessmentCriteriaDescription ?? '').trim();
}

function stripLpnPrefix(raw) {
  const text = String(raw ?? '').trim();
  if (!text) return '';
  return text.replace(/^(?:lpn[\s:.-]*)+/i, '').trim();
}

function formatLpnLabel(rawOrToken, fallback = '?') {
  const token = stripLpnPrefix(rawOrToken);
  const value = token || String(fallback ?? '?').trim() || '?';
  return `LPN ${value}`;
}

function shouldUseRelativeLpnSequence(rows) {
  const arr = Array.isArray(rows) ? rows : [];
  if (arr.length < 2) return false;
  const nums = arr.map(r => parseLpnValue(readLpn(r)));
  if (nums.some(n => !Number.isFinite(n))) return false;
  for (let i = 1; i < nums.length; i++) {
    if (nums[i] !== nums[i - 1] + 1) return false;
  }
  return nums[0] > 1;
}

function sanitizeSearchDescription(value) {
  const raw = String(value ?? '').trim();
  if (!raw) return '';

  let cleaned = raw;
  // Remove LPN-style prefixes (e.g., "LPN 12 - ...") from query text.
  cleaned = cleaned.replace(/^(?:lpn[\s:.-]*)+\d+\s*[-:)\]]*\s*/i, '');
  // Remove leading code tokens (e.g., "KT1303 - ...", "SUB-001: ...").
  cleaned = cleaned.replace(/^\[?[A-Z]{1,8}[A-Z0-9._/-]*\]?\s*[-:)\]]\s*/i, '');
  return cleaned.trim();
}

function normalizeCodeValue(value) {
  return String(value ?? '')
    .trim()
    .replace(/\s+/g, '')
    .toUpperCase();
}

function normalizeCodeKey(value) {
  return normalizeCodeValue(value).replace(/[^A-Z0-9]/g, '');
}

function normalizeLooseText(value) {
  return String(value ?? '')
    .trim()
    .replace(/\s+/g, ' ')
    .toLowerCase();
}

function pushSearchPart(parts, value) {
  const cleaned = sanitizeSearchDescription(value);
  if (cleaned) parts.push(cleaned);
}

function parseDownloadFileName(contentDisposition, fallbackName) {
  const raw = String(contentDisposition || '');
  if (!raw) return fallbackName;

  const utf8Match = raw.match(/filename\*=UTF-8''([^;]+)/i);
  if (utf8Match?.[1]) {
    try {
      return decodeURIComponent(utf8Match[1]);
    } catch {
      return utf8Match[1];
    }
  }

  const asciiMatch = raw.match(/filename="?([^\";]+)"?/i);
  if (asciiMatch?.[1]) return asciiMatch[1];
  return fallbackName;
}

export default function ContentBuilderPage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const location = useLocation();
  const { qualificationId, setQualificationId } = useQualification() || { qualificationId: null, setQualificationId: () => {} };
  const [entry, setEntry] = useState(null);
  const [provider, setProvider] = useState('wikipedia');
  const [results, setResults] = useState([]);
  const [queryText, setQueryText] = useState('');
  const [fetchedText, setFetchedText] = useState('');
  const [draftText, setDraftText] = useState('');
  const [materials, setMaterials] = useState([]);
  const [uploading, setUploading] = useState(false);
  const [status, setStatus] = useState('');
  const [qualifications, setQualifications] = useState([]);
  const [subjects, setSubjects] = useState([]);
  const [topics, setTopics] = useState([]);
  const [criteria, setCriteria] = useState([]);
  const [selectedQualificationId, setSelectedQualificationId] = useState(0);
  const [selectedSubjectId, setSelectedSubjectId] = useState(0);
  const [selectedTopicId, setSelectedTopicId] = useState(0);
  const [selectedCriteriaId, setSelectedCriteriaId] = useState(0);
  const [lpnEntries, setLpnEntries] = useState([]);
  const [selectedLpnEntryId, setSelectedLpnEntryId] = useState(null);
  const [selectedLessonDescription, setSelectedLessonDescription] = useState('');
  const [cite, setCite] = useState(false);
  const [localFirst, setLocalFirst] = useState(true);
  const [useOpenAI, setUseOpenAI] = useState(false);
  const [localScanning, setLocalScanning] = useState(false);
  const [scanProgress, setScanProgress] = useState({ current: 0, total: 0 });
  const [scanDetailsVisible, setScanDetailsVisible] = useState(false);
  const [scannedTitles, setScannedTitles] = useState([]);
  const [currentTitle, setCurrentTitle] = useState('');
  const [includeQualification, setIncludeQualification] = useState(false);
  const [includeSubject, setIncludeSubject] = useState(false);
  const [includeTopic, setIncludeTopic] = useState(false);
  const [includeCriteria, setIncludeCriteria] = useState(false);
  const [includeLesson, setIncludeLesson] = useState(true);
  const [highlightMatches, setHighlightMatches] = useState(true);
  const [offlineMode, setOfflineMode] = useState(false);
  const [runtimeConfig, setRuntimeConfig] = useState(null);
  const [runtimeForm, setRuntimeForm] = useState({
    aiMode: 'offline',
    openAiModel: 'gemini-3.8-flash',
    openAiApiKey: '',
    localLlmEndpoint: '',
    localLlmModel: ''
  });
  const [runtimeSaving, setRuntimeSaving] = useState(false);
  const [googleCx, setGoogleCx] = useState('');
  const [googleKey, setGoogleKey] = useState('');
  const [googleKeyPresent, setGoogleKeyPresent] = useState(false);
  const [selectedMaterialId, setSelectedMaterialId] = useState(null);
  const [activeResultIndex, setActiveResultIndex] = useState(-1);
  const [lastInsertedResultKey, setLastInsertedResultKey] = useState('');
  const [searching, setSearching] = useState(false);
  const [autoMapping, setAutoMapping] = useState(false);
  const [autoMapSummary, setAutoMapSummary] = useState(null);
  const [autoLoadTopResult, setAutoLoadTopResult] = useState(true);
  const [previewModalOpen, setPreviewModalOpen] = useState(false);
  const [previewContext, setPreviewContext] = useState('');
  const [previewMeta, setPreviewMeta] = useState(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [previewSaving, setPreviewSaving] = useState(false);
  const [finalizeModalOpen, setFinalizeModalOpen] = useState(false);
  const [finalizeSaving, setFinalizeSaving] = useState(false);
  const [finalizeText, setFinalizeText] = useState('');
  const [finalizeTargetEntryId, setFinalizeTargetEntryId] = useState(0);
  const [finalizedEntryIds, setFinalizedEntryIds] = useState([]);
  const [errors, setErrors] = useState([]);
  const [exportingGuide, setExportingGuide] = useState(false);
  const [exportProgress, setExportProgress] = useState(0);
  const [exportForce, setExportForce] = useState(false);
  const [exportUseParaphrase, setExportUseParaphrase] = useState(true);
  const [exportStatus, setExportStatus] = useState('');
  const [exportError, setExportError] = useState('');
  const [exportSummary, setExportSummary] = useState(null);
  const initialHydrationKeyRef = useRef('');
  const routeQualificationId = Number(location.state?.qualificationId || 0);
  const addError = (msg) => setErrors(prev => [...prev, msg]);
  const backendOfflineEnforced = Boolean(runtimeConfig?.offlineMode);
  const localLibraryPath = String(runtimeConfig?.localLibraryPath || '');
  const localLibraryExists = Boolean(runtimeConfig?.localLibraryExists);
  const filteredCriteria = useMemo(() => {
    if (!selectedTopicId) return criteria;
    return criteria.filter(c => Number(c?.topicId ?? 0) === Number(selectedTopicId));
  }, [criteria, selectedTopicId]);
  const sortedLpnEntries = useMemo(() => sortLpnEntriesByLpn(lpnEntries), [lpnEntries]);
  const selectedSubjectRow = useMemo(
    () => subjects.find(s => Number(s?.id ?? 0) === Number(selectedSubjectId || 0)) || null,
    [subjects, selectedSubjectId]
  );
  const selectedCriteriaRow = useMemo(
    () => criteria.find(c => Number(c?.id ?? 0) === Number(selectedCriteriaId || 0)) || null,
    [criteria, selectedCriteriaId]
  );
  const activeSelectionQualificationId = Number(selectedQualificationId || entryQualificationId(entry) || routeQualificationId || qualificationId || 0);
  const useRelativeLpnSequence = useMemo(
    () => shouldUseRelativeLpnSequence(sortedLpnEntries),
    [sortedLpnEntries]
  );
  const lpnLabelByEntryId = useMemo(() => {
    const map = new Map();
    for (let i = 0; i < sortedLpnEntries.length; i++) {
      const row = sortedLpnEntries[i];
      const entryId = readToolkitEntryId(row);
      if (!entryId) continue;
      const rawLpn = readLpn(row);
      const token = useRelativeLpnSequence ? String(i + 1) : (stripLpnPrefix(rawLpn) || '?');
      map.set(entryId, formatLpnLabel(token));
    }
    return map;
  }, [sortedLpnEntries, useRelativeLpnSequence]);

  const resultKey = (result, index = -1) => {
    const materialId = Number(result?.materialId || 0);
    const paragraphIndex = Number(result?.paragraphIndex || 0);
    const url = String(result?.url || '').trim();
    if (materialId > 0 && paragraphIndex > 0) return `m:${materialId}:p:${paragraphIndex}`;
    if (materialId > 0) return `m:${materialId}:i:${index}`;
    if (url) return `u:${url}`;
    const title = String(result?.title || '').trim();
    return `t:${title}:i:${index}`;
  };

  const findNextApplicableResultIndex = (arr, fromIndex) => {
    const list = Array.isArray(arr) ? arr : [];
    const start = Number(fromIndex || 0);
    if (list.length === 0 || start < 0) return -1;
    for (let i = start + 1; i < list.length; i++) {
      const item = list[i];
      if (item?.materialId || item?.url || String(item?.snippet || '').trim()) {
        return i;
      }
    }
    return -1;
  };

  const pickAutoLoadResultIndex = (arr) => {
    const list = Array.isArray(arr) ? arr : [];
    if (list.length === 0) return -1;
    if (!lastInsertedResultKey) return 0;
    const currentIndex = list.findIndex((item, idx) => resultKey(item, idx) === lastInsertedResultKey);
    if (currentIndex >= 0 && currentIndex < list.length - 1) return currentIndex + 1;
    return 0;
  };

  const lessonOptions = useMemo(() => {
    const options = [];
    const seen = new Set();
    for (let i = 0; i < sortedLpnEntries.length; i++) {
      const row = sortedLpnEntries[i];
      const desc = readLessonDescription(row);
      if (!desc) continue;
      const entryId = readToolkitEntryId(row);
      const rawLpn = readLpn(row);
      const displayLpnToken = useRelativeLpnSequence ? String(i + 1) : stripLpnPrefix(rawLpn);
      const value = entryId > 0 ? `id:${entryId}` : `desc:${desc}`;
      if (seen.has(value)) continue;
      seen.add(value);
      options.push({
        value,
        entryId,
        description: desc,
        lpn: rawLpn,
        label: displayLpnToken ? `${formatLpnLabel(displayLpnToken)} - ${desc}` : desc
      });
    }
    if (options.length === 0) {
      const fallback = String(entry?.LessonPlanDescription ?? entry?.lessonPlanDescription ?? '').trim();
      if (fallback) {
        options.push({
          value: `desc:${fallback}`,
          entryId: 0,
          description: fallback,
          lpn: '',
          label: fallback
        });
      }
    }
    return options;
  }, [sortedLpnEntries, entry, useRelativeLpnSequence]);

  const selectedLessonOptionValue = useMemo(() => {
    const selectedEntryId = Number(selectedLpnEntryId ?? 0);
    if (selectedEntryId > 0) return `id:${selectedEntryId}`;
    const desc = String(selectedLessonDescription || '').trim();
    if (!desc) return '';
    const exact = lessonOptions.find(opt => opt.description === desc);
    return exact?.value ?? `desc:${desc}`;
  }, [selectedLpnEntryId, selectedLessonDescription, lessonOptions]);

  const selectedToolkitEntry = useMemo(() => {
    const targetId = Number(selectedLpnEntryId || 0);
    if (!targetId) return null;
    return sortedLpnEntries.find(row => readToolkitEntryId(row) === targetId)
      || (readToolkitEntryId(entry) === targetId ? entry : null)
      || null;
  }, [selectedLpnEntryId, sortedLpnEntries, entry]);

  const entryMatchesSelectionContext = (row) => {
    if (!row) return false;

    const rowQualificationId = readToolkitQualificationId(row);
    if (activeSelectionQualificationId > 0 && rowQualificationId !== activeSelectionQualificationId) {
      return false;
    }

    const selectedSubjectCodeKey = normalizeCodeKey(selectedSubjectRow?.subjectCode ?? '');
    const rowSubjectCodeKey = normalizeCodeKey(readToolkitSubjectCode(row));
    if (selectedSubjectCodeKey && rowSubjectCodeKey !== selectedSubjectCodeKey) {
      return false;
    }

    const selectedCriteriaIdValue = Number(selectedCriteriaRow?.id ?? selectedCriteriaId ?? 0);
    if (selectedCriteriaIdValue > 0) {
      const rowCriteriaId = readToolkitCriteriaId(row);
      const selectedCriteriaDescriptionKey = normalizeLooseText(selectedCriteriaRow?.description ?? '');
      const rowCriteriaDescriptionKey = normalizeLooseText(readToolkitCriteriaDescription(row));
      const criteriaIdMatch = rowCriteriaId > 0 && rowCriteriaId === selectedCriteriaIdValue;
      const criteriaDescriptionMatch = selectedCriteriaDescriptionKey
        && rowCriteriaDescriptionKey
        && rowCriteriaDescriptionKey === selectedCriteriaDescriptionKey;
      if (!criteriaIdMatch && !criteriaDescriptionMatch) {
        return false;
      }
    }

    return true;
  };

  const activeToolkitTargetId = useMemo(() => {
    const selectedEntryId = readToolkitEntryId(selectedToolkitEntry);
    if (selectedEntryId > 0 && entryMatchesSelectionContext(selectedToolkitEntry)) {
      return selectedEntryId;
    }

    const routeEntryId = readToolkitEntryId(entry);
    if (routeEntryId > 0 && entryMatchesSelectionContext(entry)) {
      return routeEntryId;
    }

    if (!selectedSubjectRow && !selectedCriteriaRow && routeEntryId > 0) {
      const routeQualificationId = readToolkitQualificationId(entry);
      if (activeSelectionQualificationId <= 0 || routeQualificationId === activeSelectionQualificationId) {
        return routeEntryId;
      }
    }

    return 0;
  }, [selectedToolkitEntry, entry, selectedSubjectRow, selectedCriteriaRow, activeSelectionQualificationId, selectedCriteriaId]);

  const hasActiveToolkitTarget = activeToolkitTargetId > 0;

  const handleLessonSelection = (value) => {
    const raw = String(value || '').trim();
    if (!raw) {
      setSelectedLpnEntryId(null);
      setSelectedLessonDescription('');
      return;
    }
    if (raw.startsWith('id:')) {
      const entryId = Number(raw.slice(3));
      const match = (Array.isArray(sortedLpnEntries) ? sortedLpnEntries : []).find(e2 => readToolkitEntryId(e2) === entryId) || null;
      const desc = readLessonDescription(match);
      setSelectedLpnEntryId(entryId > 0 ? entryId : null);
      if (desc) setSelectedLessonDescription(desc);
      return;
    }
    const desc = raw.startsWith('desc:') ? raw.slice(5) : raw;
    const normalizedDesc = String(desc || '').trim();
    const match = (Array.isArray(sortedLpnEntries) ? sortedLpnEntries : []).find(e2 => readLessonDescription(e2) === normalizedDesc) || null;
    setSelectedLessonDescription(normalizedDesc);
    setSelectedLpnEntryId(match ? readToolkitEntryId(match) : null);
  };

  const clearFetchedPanel = () => {
    setFetchedText('');
    setActiveResultIndex(-1);
    setSelectedMaterialId(null);
  };

  const lpnLabelForEntryId = (entryId) => {
    const numericId = Number(entryId || 0);
    if (!numericId) return 'LPN ?';
    return lpnLabelByEntryId.get(numericId) || `LPN ${numericId}`;
  };

  const markEntryFinalized = (entryId) => {
    const numericId = Number(entryId || 0);
    if (!numericId) return;
    setFinalizedEntryIds(prev => (
      prev.includes(numericId) ? prev : [...prev, numericId]
    ));
  };

  const findNextLpnEntryId = (currentEntryId) => {
    const currentId = Number(currentEntryId || 0);
    if (!currentId) return 0;
    const idx = sortedLpnEntries.findIndex(row => readToolkitEntryId(row) === currentId);
    if (idx < 0) return 0;
    for (let i = idx + 1; i < sortedLpnEntries.length; i++) {
      const nextId = readToolkitEntryId(sortedLpnEntries[i]);
      if (nextId > 0) return nextId;
    }
    return 0;
  };

  const syncToolkitEntryInState = (updatedEntry) => {
    const targetId = readToolkitEntryId(updatedEntry);
    if (!targetId) return;
    const mergedLessonPlanContent = String(updatedEntry?.LessonPlanContent ?? updatedEntry?.lessonPlanContent ?? '');
    const merged = {
      ...updatedEntry,
      LessonPlanContent: mergedLessonPlanContent,
      lessonPlanContent: mergedLessonPlanContent
    };
    setLpnEntries(prev => (
      Array.isArray(prev)
        ? prev.map(row => (readToolkitEntryId(row) === targetId ? { ...row, ...merged } : row))
        : prev
    ));
    setEntry(prev => {
      const prevId = readToolkitEntryId(prev);
      if (!prev || prevId !== targetId) return prev;
      return { ...prev, ...merged };
    });
  };

  const fetchToolkitEntryById = async (entryId) => {
    const targetId = Number(entryId || 0);
    if (!targetId) return null;
    const res = await fetch(`${API_TOOLKIT}/${targetId}`);
    if (!res.ok) return null;
    const json = await res.json().catch(() => null);
    if (json) syncToolkitEntryInState(json);
    return json;
  };

  const refreshToolkitEntryContext = async (entryId, options = {}) => {
    const targetId = Number(entryId || 0);
    const preloadExisting = Boolean(options?.preloadExisting);
    if (!targetId) return null;

    const res = await fetch(`${API_TOOLKIT}/${targetId}`, { cache: 'no-store' });
    if (!res.ok) {
      const msg = await res.text().catch(() => '');
      throw new Error(msg || `Could not refresh toolkit entry #${targetId}.`);
    }

    const data = await res.json();
    setEntry(data);
    const normalizedId = readToolkitEntryId(data);
    if (normalizedId > 0) setSelectedLpnEntryId(normalizedId);
    const lessonDesc = readLessonDescription(data);
    if (lessonDesc) setSelectedLessonDescription(lessonDesc);

    if (preloadExisting) {
      const existingContent = String(data?.LessonPlanContent ?? data?.lessonPlanContent ?? '').trim();
      const source = existingContent || lessonDesc || '';
      setFetchedText(source);
      if (source) {
        setStatus(`Loaded existing lesson content for ${formatLpnLabel(readLpn(data) || String(normalizedId || '?'))}.`);
      } else {
        setStatus(`No saved lesson content found for ${formatLpnLabel(readLpn(data) || String(normalizedId || '?'))}.`);
      }
    }

    await loadMaterials({
      qualificationId: Number(data?.QualificationsId ?? data?.qualificationsId ?? 0),
      qualificationDescription: String(data?.QualificationDescription ?? data?.qualificationDescription ?? '').trim()
    });

    return data;
  };

  const loadExistingLessonContent = async () => {
    const targetId = Number(activeToolkitTargetId || 0);
    if (!targetId) {
      setStatus('Select an LPN that matches the current subject and criteria first.');
      return;
    }
    setStatus('');
    const row = await fetchToolkitEntryById(targetId);
    if (!row) {
      setStatus(`Could not load ${lpnLabelForEntryId(targetId)}.`);
      return;
    }
    const existingContent = String(row?.LessonPlanContent ?? row?.lessonPlanContent ?? '').trim();
    const existingDescription = String(row?.LessonPlanDescription ?? row?.lessonPlanDescription ?? '').trim();
    const source = existingContent || existingDescription;
    if (!source) {
      setFetchedText('');
      setStatus(`${lpnLabelForEntryId(targetId)} has no saved lesson content yet.`);
      return;
    }
    setFetchedText(source);
    setStatus(`Loaded existing lesson content for ${lpnLabelForEntryId(targetId)}.`);
  };

  const updateToolkitEntryContent = async (entryId, content) => {
    const targetId = Number(entryId || 0);
    if (!targetId) throw new Error('Toolkit entry id is required.');
    const currentRes = await fetch(`${API_TOOLKIT}/${targetId}`);
    if (!currentRes.ok) {
      const msg = await currentRes.text().catch(() => '');
      throw new Error(msg || `Unable to load toolkit entry #${targetId}.`);
    }
    const current = await currentRes.json();
    const body = {
      ...current,
      LessonPlanContent: String(content || '')
    };
    const saveRes = await fetch(`${API_TOOLKIT}/${targetId}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    if (!saveRes.ok) {
      const msg = await saveRes.text().catch(() => '');
      throw new Error(msg || `Unable to update toolkit entry #${targetId}.`);
    }
    const updated = await saveRes.json().catch(() => body);
    syncToolkitEntryInState(updated);
    return updated;
  };

  const loadLpnEntriesForCriteria = async (criteriaId, opts = {}) => {
    const cid = Number(criteriaId || 0);
    const preferredToolkitEntryId = Number(opts?.preferredToolkitEntryId || 0);
    const preferredLessonDescription = String(opts?.preferredLessonDescription || '').trim();
    const preferredCriteriaDescription = String(opts?.preferredCriteriaDescription || '').trim();
    const activeQualificationId = Number(selectedQualificationId || entryQualificationId(entry) || routeQualificationId || qualificationId || 0);
    const activeSubjectId = Number(opts?.preferredSubjectId || selectedSubjectId || 0);
    const activeSubjectFromState = subjects.find((s) => Number(s?.id ?? 0) === activeSubjectId) || null;
    const activeSubjectCodeKey = normalizeCodeKey(
      opts?.preferredSubjectCode
      || activeSubjectFromState?.subjectCode
      || ''
    );

    if (!cid) {
      setLpnEntries([]);
      setSelectedLpnEntryId(null);
      setSelectedLessonDescription(preferredLessonDescription || '');
      return;
    }

    const all = await fetch(API_TOOLKIT).then(r => r.json()).catch((err) => {
      addError(`[Toolkit] List fetch failed: ${err?.message || 'error'}`);
      return [];
    });
    const allRows = Array.isArray(all) ? all : [];
    const byQualification = allRows.filter((e2) => (
      activeQualificationId <= 0 || readToolkitQualificationId(e2) === activeQualificationId
    ));
    const bySubject = activeSubjectCodeKey
      ? byQualification.filter((e2) => normalizeCodeKey(readToolkitSubjectCode(e2)) === activeSubjectCodeKey)
      : byQualification;

    let filtered = bySubject.filter(
      (e2) => readToolkitCriteriaId(e2) === cid
    );

    if (filtered.length === 0) {
      const selectedCriteria = criteria.find((c) => Number(c?.id ?? 0) === cid) || null;
      const criteriaDescriptionNorm = normalizeLooseText(
        preferredCriteriaDescription || selectedCriteria?.description || ''
      );
      if (criteriaDescriptionNorm) {
        filtered = bySubject.filter((e2) => (
          normalizeLooseText(readToolkitCriteriaDescription(e2)) === criteriaDescriptionNorm
        ));
      }
    }

    if (filtered.length === 0 && preferredToolkitEntryId > 0) {
      filtered = bySubject.filter((e2) => readToolkitEntryId(e2) === preferredToolkitEntryId);
    }

    if (filtered.length === 0 && preferredLessonDescription) {
      const lessonNorm = normalizeLooseText(preferredLessonDescription);
      filtered = bySubject.filter((e2) => normalizeLooseText(readLessonDescription(e2)) === lessonNorm);
    }

    filtered = sortLpnEntriesByLpn(filtered);
    setLpnEntries(filtered);

    if (filtered.length === 0) {
      setSelectedLpnEntryId(null);
      setSelectedLessonDescription(preferredLessonDescription || '');
      addError(`[Toolkit] No LPN entries found for qualificationId=${activeQualificationId || 0}, subjectCode=${activeSubjectCodeKey || '-'}, criteriaId=${cid}`);
      return;
    }

    const currentSelectedId = Number(selectedLpnEntryId || 0);
    const byPreferredId = preferredToolkitEntryId > 0
      ? filtered.find(e2 => readToolkitEntryId(e2) === preferredToolkitEntryId)
      : null;
    const byCurrentId = currentSelectedId > 0
      ? filtered.find(e2 => readToolkitEntryId(e2) === currentSelectedId)
      : null;
    const byDescription = preferredLessonDescription
      ? filtered.find(e2 => readLessonDescription(e2) === preferredLessonDescription)
      : null;
    const selectedRow = byPreferredId || byCurrentId || byDescription || filtered[0];

    const selectedRowId = readToolkitEntryId(selectedRow);
    const selectedRowDesc = readLessonDescription(selectedRow);
    setSelectedLpnEntryId(selectedRowId > 0 ? selectedRowId : null);
    setSelectedLessonDescription(selectedRowDesc || preferredLessonDescription || '');
  };

  const loadCriteriaForTopic = async (topicId, opts = {}, topicPool = null) => {
    const tid = Number(topicId || 0);
    const preferredCriteriaId = Number(opts?.preferredCriteriaId || 0);
    const preferredCriteriaDescriptionNorm = normalizeLooseText(opts?.preferredCriteriaDescription || '');

    if (!tid) {
      setCriteria([]);
      setSelectedCriteriaId(0);
      await loadLpnEntriesForCriteria(0, opts);
      return;
    }

    const rawCriteria = await fetch(`${API_CRITERIA}/byTopic?topicId=${tid}`).then(r => r.json()).catch((err) => {
      addError(`[AssessmentCriteria] byTopic failed for topicId=${tid}: ${err?.message || 'error'}`);
      return [];
    });

    const topicsSource = Array.isArray(topicPool) ? topicPool : topics;
    const sourceTopic = (Array.isArray(topicsSource) ? topicsSource : []).find(t => Number(t?.id ?? 0) === tid) || null;
    const effectiveSubjectId = Number((sourceTopic?.subjectId ?? opts?.preferredSubjectId ?? selectedSubjectId) || 0);
    const effectiveSubject = subjects.find(s => Number(s?.id ?? 0) === effectiveSubjectId) || null;
    const effectiveOpts = {
      ...opts,
      preferredSubjectId: effectiveSubjectId,
      preferredSubjectCode: String(opts?.preferredSubjectCode || effectiveSubject?.subjectCode || '').trim(),
      preferredSubjectDescription: String(opts?.preferredSubjectDescription || effectiveSubject?.subjectDescription || '').trim()
    };
    const criteriaRows = (Array.isArray(rawCriteria) ? rawCriteria : [])
      .map((c) => {
        const idValue = Number(c?.Id ?? c?.id ?? 0);
        if (!idValue) return null;
        return {
          id: idValue,
          description: String(c?.Description ?? c?.description ?? '').trim(),
          topicId: Number(c?.TopicId ?? c?.topicId ?? tid),
          topicCode: String(sourceTopic?.topicCode ?? '').trim(),
          topicDescription: String(sourceTopic?.topicDescription ?? '').trim()
        };
      })
      .filter(Boolean);

    setCriteria(criteriaRows);

    const currentSelectedCriteria = Number(selectedCriteriaId || 0);
    const hasCurrent = currentSelectedCriteria > 0 && criteriaRows.some(c => c.id === currentSelectedCriteria);
    const byDescription = preferredCriteriaDescriptionNorm
      ? criteriaRows.find(c => normalizeLooseText(c?.description ?? '') === preferredCriteriaDescriptionNorm)
      : null;
    const nextCriteriaId = preferredCriteriaId > 0 && criteriaRows.some(c => c.id === preferredCriteriaId)
      ? preferredCriteriaId
      : (byDescription ? Number(byDescription.id || 0) : (hasCurrent ? currentSelectedCriteria : Number(criteriaRows[0]?.id || 0)));

    setSelectedCriteriaId(nextCriteriaId || 0);
    const nextCriteria = criteriaRows.find((c) => c.id === nextCriteriaId) || null;
    await loadLpnEntriesForCriteria(nextCriteriaId, {
      ...effectiveOpts,
      preferredCriteriaDescription: String(nextCriteria?.description || '')
    });
  };

  const loadTopicsForSubject = async (subjectId, opts = {}) => {
    const sid = Number(subjectId || 0);
    const preferredTopicId = Number(opts?.preferredTopicId || 0);
    const sourceSubject = subjects.find(s => Number(s?.id ?? 0) === sid) || null;
    const effectiveOpts = {
      ...opts,
      preferredSubjectId: sid,
      preferredSubjectCode: String(opts?.preferredSubjectCode || sourceSubject?.subjectCode || '').trim(),
      preferredSubjectDescription: String(opts?.preferredSubjectDescription || sourceSubject?.subjectDescription || '').trim()
    };

    if (!sid) {
      setTopics([]);
      setCriteria([]);
      setSelectedTopicId(0);
      setSelectedCriteriaId(0);
      setLpnEntries([]);
      setSelectedLpnEntryId(null);
      return;
    }

    const rawTlist = await fetch(`${API_TOPIC}/bySubject?subjectId=${sid}`).then(r => r.json()).catch((err) => {
      addError(`[Topic] bySubject failed for subjectId=${sid}: ${err?.message || 'error'}`);
      return [];
    });
    const topicList = (Array.isArray(rawTlist) ? rawTlist : []).map(normTopic).filter(Boolean);
    setTopics(topicList);

    if (topicList.length === 0) {
      setCriteria([]);
      setSelectedTopicId(0);
      setSelectedCriteriaId(0);
      setLpnEntries([]);
      setSelectedLpnEntryId(null);
      return;
    }

    const hasSelectedTopic = Number(selectedTopicId || 0) > 0 && topicList.some(t => t.id === Number(selectedTopicId));
    const nextTopicId = preferredTopicId > 0 && topicList.some(t => t.id === preferredTopicId)
      ? preferredTopicId
      : (hasSelectedTopic ? Number(selectedTopicId) : topicList[0]?.id || 0);
    setSelectedTopicId(nextTopicId || 0);
    await loadCriteriaForTopic(nextTopicId, effectiveOpts, topicList);
  };

  const loadSubjectsForQualification = async (qualificationValue, opts = {}) => {
    const qualId = Number(qualificationValue || 0);
    const preferredSubjectId = Number(opts?.preferredSubjectId || 0);
    const preferredSubjectCode = String(opts?.preferredSubjectCode || '').trim();
    const preferredSubjectDescription = String(opts?.preferredSubjectDescription || '').trim();

    if (!qualId) {
      setSubjects([]);
      setSelectedSubjectId(0);
      setTopics([]);
      setCriteria([]);
      setSelectedTopicId(0);
      setSelectedCriteriaId(0);
      setLpnEntries([]);
      setSelectedLpnEntryId(null);
      return;
    }

    const rawSubs = await fetch(`${API_SUBJECT}/byQualification?qualificationId=${qualId}`).then(r => r.json()).catch((err) => {
      addError(`[Subject] byQualification failed for qualificationId=${qualId}: ${err?.message || 'error'}`);
      return [];
    });
    const subs = (Array.isArray(rawSubs) ? rawSubs : []).map(normSubject).filter(Boolean);
    setSubjects(subs);

    if (subs.length === 0) {
      addError(`[Subject] No subjects for qualificationId=${qualId}.`);
      setSelectedSubjectId(0);
      setTopics([]);
      setCriteria([]);
      setSelectedTopicId(0);
      setSelectedCriteriaId(0);
      setLpnEntries([]);
      setSelectedLpnEntryId(null);
      return;
    }

    const rawTopicsByQualification = await fetch(`${API_TOPIC}/byQualification?qualificationId=${qualId}`).then(r => r.json()).catch((err) => {
      addError(`[Topic] byQualification failed for qualificationId=${qualId}: ${err?.message || 'error'}`);
      return [];
    });
    const topicRowsByQualification = (Array.isArray(rawTopicsByQualification) ? rawTopicsByQualification : []).map(normTopic).filter(Boolean);
    const topicCountBySubjectId = new Map();
    for (const t of topicRowsByQualification) {
      const sid = Number(t?.subjectId || 0);
      if (!sid) continue;
      topicCountBySubjectId.set(sid, Number(topicCountBySubjectId.get(sid) || 0) + 1);
    }

    const topicCountForSubject = (s) => Number(topicCountBySubjectId.get(Number(s?.id || 0)) || 0);
    const pickBestSubject = (candidates) => {
      const arr = (Array.isArray(candidates) ? candidates : []).filter(Boolean);
      if (arr.length === 0) return null;
      if (arr.length === 1) return arr[0];
      return [...arr].sort((a, b) => {
        const topicDiff = topicCountForSubject(b) - topicCountForSubject(a);
        if (topicDiff !== 0) return topicDiff;
        return Number(b?.id || 0) - Number(a?.id || 0);
      })[0];
    };

    const byId = preferredSubjectId > 0 ? subs.find(s => s.id === preferredSubjectId) : null;
    const preferredSubjectCodeNorm = normalizeCodeValue(preferredSubjectCode);
    const byCodeMatches = preferredSubjectCodeNorm
      ? subs.filter(s => normalizeCodeValue(s?.subjectCode) === preferredSubjectCodeNorm)
      : [];
    const byCode = pickBestSubject(byCodeMatches);
    const preferredSubjectDescriptionNorm = String(preferredSubjectDescription || '').trim().toLowerCase();
    const byDescriptionMatches = preferredSubjectDescriptionNorm
      ? subs.filter(s => String(s?.subjectDescription || '').trim().toLowerCase() === preferredSubjectDescriptionNorm)
      : [];
    const byDescription = pickBestSubject(byDescriptionMatches);
    const hasSelectedSubject = Number(selectedSubjectId || 0) > 0 && subs.some(s => s.id === Number(selectedSubjectId));
    // Prefer entry code/description over id hints, because some imports carry legacy ids.
    const subject = byCode || byDescription || byId || (hasSelectedSubject ? subs.find(s => s.id === Number(selectedSubjectId)) : null) || pickBestSubject(subs) || subs[0];
    const sid = Number(subject?.id || 0);
    setSelectedSubjectId(sid || 0);
    await loadTopicsForSubject(sid, opts);
  };

  const autoLoadFromState = Boolean(location.state?.autoLoadExistingContent);
  const refreshOnLoad = Boolean(location.state?.refreshOnLoad);

  useEffect(() => {
    if (!id) return;
    const hydrationKey = `${location.key || 'no-key'}:${id}`;
    if (initialHydrationKeyRef.current === hydrationKey) return;
    initialHydrationKeyRef.current = hydrationKey;

    const preloadExisting = autoLoadFromState || refreshOnLoad;
    refreshToolkitEntryContext(id, { preloadExisting })
      .catch((err) => {
        addError(`[Toolkit] Failed to load entry id=${id}: ${err?.message || 'error'}`);
        setEntry(null);
      });
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id, location.key, autoLoadFromState, refreshOnLoad]);

  useEffect(() => {
    const selectedEntryId = Number(selectedLpnEntryId || 0);
    if (!selectedEntryId) return;
    if (String(fetchedText || '').trim()) return;

    const current = sortedLpnEntries.find(row => readToolkitEntryId(row) === selectedEntryId) || null;
    if (!current) return;

    const existingContent = String(current?.LessonPlanContent ?? current?.lessonPlanContent ?? '').trim();
    const lessonDesc = readLessonDescription(current);
    const source = existingContent || lessonDesc || '';
    if (source) {
      setFetchedText(source);
    }
  }, [selectedLpnEntryId, sortedLpnEntries, fetchedText]);

  useEffect(() => {
    fetch(API_QUAL)
      .then(r => r.json())
      .then(list => {
        const arr = Array.isArray(list) ? list : [];
        const normalized = arr.map(normQualification).filter(Boolean);
        setQualifications(normalized);
        if (normalized.length === 0 && arr.length > 0) addError('[Qualification] Could not normalize qualification list');
        else if (arr.length === 0) addError('[Qualification] No qualifications returned from API');
      })
      .catch((err) => {
        addError(`[Qualification] Failed to load list: ${err?.message || 'error'}`);
        setQualifications([]);
      });
  }, []);

  useEffect(() => {
    let active = true;
    fetch(`${API_CONTENT}/runtime-config`)
      .then(r => (r.ok ? r.json() : null))
      .then(cfg => {
        if (!active || !cfg) return;
        setRuntimeConfig(cfg);
        setRuntimeForm(prev => ({
          ...prev,
          aiMode: String(cfg?.aiMode || 'offline'),
          openAiModel: String(cfg?.openAiModel || 'gemini-3.8-flash'),
          localLlmEndpoint: String(cfg?.localLlmEndpoint || ''),
          localLlmModel: String(cfg?.localLlmModel || '')
        }));
        if (cfg.offlineMode) {
          setOfflineMode(true);
          setProvider('none');
          setLocalFirst(true);
          setUseOpenAI(false);
        }
      })
      .catch(() => {});
    return () => { active = false; };
  }, []);

  const saveRuntimeConfig = async () => {
    if (runtimeSaving) return;
    setRuntimeSaving(true);
    setStatus('');
    try {
      const res = await fetch(`${API_CONTENT}/runtime-config`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(runtimeForm)
      });
      const text = await res.text();
      const data = text ? JSON.parse(text) : {};
      if (!res.ok) {
        throw new Error(data?.error || data?.message || `Runtime save failed (${res.status})`);
      }
      const refreshed = await fetch(`${API_CONTENT}/runtime-config`).then(r => (r.ok ? r.json() : data));
      setRuntimeConfig(refreshed);
      setRuntimeForm(prev => ({ ...prev, openAiApiKey: '' }));
      if (refreshed?.offlineMode) {
        setOfflineMode(true);
        setProvider('none');
        setUseOpenAI(false);
      } else {
        setOfflineMode(false);
        if (runtimeForm.aiMode === 'cloud') {
          setUseOpenAI(true);
          setLocalFirst(false);
        }
      }
      setStatus(`AI mode saved as ${String(refreshed?.aiMode || runtimeForm.aiMode)}. Restart the backend if an environment variable still forces offline mode.`);
    } catch (err) {
      setStatus(`AI mode save failed: ${err?.message || err}`);
    } finally {
      setRuntimeSaving(false);
    }
  };

  useEffect(() => {
    fetch(`${API_CONTENT}/google-config`)
      .then(r => (r.ok ? r.json() : null))
      .then(cfg => {
        if (!cfg) return;
        const cx = String(cfg?.google_cx || '').trim();
        if (cx) setGoogleCx(cx);
        setGoogleKeyPresent(Boolean(cfg?.google_key_present));
      })
      .catch(() => {});
  }, []);

  // Run cascade when entry loads. Only depend on entry so we don't re-run when setQualificationId updates context.
  useEffect(() => {
    if (!entry) return;
    const numericFromEntry = entryQualificationId(entry);
    const fromRouteState = Number(routeQualificationId || 0);
    const fromContext = typeof qualificationId === 'number' ? qualificationId : Number(qualificationId);
    const preferredCriteriaId = Number(entry?.AssessmentCriteriaId ?? entry?.assessmentCriteriaId ?? 0);
    const preferredCriteriaDescription = String(entry?.AssessmentCriteriaDescription ?? entry?.assessmentCriteriaDescription ?? '').trim();
    const preferredSubjectCode = String(entry?.SubjectCode ?? entry?.subjectCode ?? '').trim();
    const preferredSubjectDescription = String(entry?.SubjectDescription ?? entry?.subjectDescription ?? '').trim();
    const preferredTopicIdFromEntry = Number(entry?.TopicId ?? entry?.topicId ?? 0);
    const preferredEntryId = readToolkitEntryId(entry);
    const preferredLessonDescription = readLessonDescription(entry);

    const bootstrapCascade = async (qualId, topicIdHint = 0, subjectIdHint = 0) => {
      if (!qualId) return;
      setSelectedQualificationId(qualId);
      try { setQualificationId(qualId); } catch (_) {}
      await loadSubjectsForQualification(qualId, {
        preferredSubjectId: Number(subjectIdHint || 0),
        preferredSubjectCode,
        preferredSubjectDescription,
        preferredTopicId: Number(topicIdHint || preferredTopicIdFromEntry || 0),
        preferredCriteriaId,
        preferredCriteriaDescription,
        preferredToolkitEntryId: preferredEntryId,
        preferredLessonDescription
      });
    };

    (async () => {
      let resolvedQualificationId = numericFromEntry > 0 ? numericFromEntry : 0;
      let resolvedTopicId = Number(preferredTopicIdFromEntry || 0);
      let resolvedSubjectId = 0;

      if (preferredCriteriaId > 0) {
        try {
          const criteriaRes = await fetch(`${API_CRITERIA}/${preferredCriteriaId}`);
          if (criteriaRes.ok) {
            const criteriaJson = await criteriaRes.json();
            const criteriaTopicId = Number(criteriaJson?.topicId ?? criteriaJson?.TopicId ?? 0);
            if (!resolvedTopicId && criteriaTopicId > 0) {
              resolvedTopicId = criteriaTopicId;
            }
            if (criteriaTopicId > 0) {
              const topicRes = await fetch(`${API_TOPIC}/${criteriaTopicId}`);
              if (topicRes.ok) {
                const topicJson = await topicRes.json();
                const topicSubjectId = Number(topicJson?.subjectId ?? topicJson?.SubjectId ?? 0) || 0;
                if (topicSubjectId > 0) {
                  resolvedSubjectId = topicSubjectId;
                }
                const topicQualificationId = Number(topicJson?.qualificationId ?? topicJson?.QualificationId ?? 0) || 0;
                if (topicQualificationId > 0) {
                  resolvedQualificationId = topicQualificationId;
                }
              }
            }
          }
        } catch {}
      }

      if (!resolvedQualificationId && preferredSubjectCode) {
        try {
          const allSubjectsRes = await fetch(API_SUBJECT);
          if (allSubjectsRes.ok) {
            const allSubjectsJson = await allSubjectsRes.json();
            const allSubjects = (Array.isArray(allSubjectsJson) ? allSubjectsJson : []).map(normSubject).filter(Boolean);
            const preferredSubjectCodeNorm = normalizeCodeValue(preferredSubjectCode);
            const byCode = allSubjects.filter(s => normalizeCodeValue(s?.subjectCode) === preferredSubjectCodeNorm);
            const preferredDescriptionNorm = String(preferredSubjectDescription || '').trim().toLowerCase();
            const byDescription = preferredDescriptionNorm
              ? byCode.find(s => String(s?.subjectDescription || '').trim().toLowerCase() === preferredDescriptionNorm)
              : null;
            const chosenSubject = byDescription || byCode[0] || null;
            const chosenSubjectId = Number(chosenSubject?.id || 0);
            if (chosenSubjectId > 0) {
              resolvedSubjectId = chosenSubjectId;
            }
            const uniqueQualificationIds = [...new Set(byCode.map(s => Number(s?.qualificationId || 0)).filter(n => n > 0))];
            if (uniqueQualificationIds.length === 1) {
              resolvedQualificationId = uniqueQualificationIds[0];
            }
          }
        } catch {}
      }

      if (!resolvedQualificationId && fromRouteState > 0) {
        resolvedQualificationId = fromRouteState;
      }

      if (!resolvedQualificationId && fromContext > 0) {
        resolvedQualificationId = fromContext;
      }

      if (resolvedQualificationId > 0) {
        await bootstrapCascade(resolvedQualificationId, resolvedTopicId, resolvedSubjectId);
        return;
      }

      const qualificationRef = routeQualificationId || qualificationId;
      if (qualificationRef && String(qualificationRef).trim()) {
        const code = String(qualificationRef).trim();
        try {
          const res = await fetch(`${API_QUAL}/search?text=${encodeURIComponent(code)}`);
          const arr = await res.json().catch(() => []);
          const list = Array.isArray(arr) ? arr : [];
          const exact = list.find(x => String(x?.QualificationNumber ?? x?.qualificationNumber ?? '').trim() === code);
          const resolvedId = exact ? Number(exact.Id ?? exact.id ?? 0) : 0;
          if (resolvedId) {
            await bootstrapCascade(resolvedId, resolvedTopicId, resolvedSubjectId);
          } else {
            addError(`[Qualification] Could not resolve Id for code="${code}"`);
          }
        } catch (err) {
          addError(`[Qualification] Search failed for code="${code}": ${err?.message || 'error'}`);
        }
      }
    })();

    const parts = [];
    pushSearchPart(parts, entry.SubjectDescription ?? entry.subjectDescription);
    pushSearchPart(parts, entry.AssessmentCriteriaDescription ?? entry.assessmentCriteriaDescription);
    pushSearchPart(parts, entry.LessonPlanDescription ?? entry.lessonPlanDescription);
    setQueryText(parts.join(' '));
    // eslint-disable-next-line react-hooks/exhaustive-deps -- intentionally only when entry changes to avoid loop with setQualificationId
  }, [entry]);

  useEffect(() => {
    const activeEntry = Number(selectedLpnEntryId || 0) > 0
      ? (sortedLpnEntries.find(row => readToolkitEntryId(row) === Number(selectedLpnEntryId || 0)) || entry)
      : entry;
    if (!activeEntry || !Array.isArray(subjects) || subjects.length === 0) return;
    const entrySubjectCodeNorm = normalizeCodeValue(activeEntry?.SubjectCode ?? activeEntry?.subjectCode);
    if (!entrySubjectCodeNorm) return;

    const selectedSubject = subjects.find(s => Number(s?.id ?? 0) === Number(selectedSubjectId || 0)) || null;
    const selectedSubjectCodeNorm = normalizeCodeValue(selectedSubject?.subjectCode);
    if (selectedSubjectCodeNorm === entrySubjectCodeNorm) return;

    const preferredSubject = subjects.find(s => normalizeCodeValue(s?.subjectCode) === entrySubjectCodeNorm) || null;
    const preferredSubjectId = Number(preferredSubject?.id || 0);
    if (!preferredSubjectId) return;

    setSelectedSubjectId(preferredSubjectId);
    loadTopicsForSubject(preferredSubjectId, {
      preferredTopicId: Number(activeEntry?.TopicId ?? activeEntry?.topicId ?? 0),
      preferredCriteriaId: Number(activeEntry?.AssessmentCriteriaId ?? activeEntry?.assessmentCriteriaId ?? 0),
      preferredCriteriaDescription: String(activeEntry?.AssessmentCriteriaDescription ?? activeEntry?.assessmentCriteriaDescription ?? '').trim(),
      preferredToolkitEntryId: readToolkitEntryId(activeEntry),
      preferredLessonDescription: readLessonDescription(activeEntry)
    }).catch(() => {});
  }, [entry, subjects, selectedLpnEntryId, sortedLpnEntries]);

  useEffect(() => {
    if (offlineMode) {
      try {
        setProvider('none');
      } catch {}
      try {
        setUseOpenAI(false);
      } catch {}
      try {
        setLocalFirst(true);
      } catch {}
    }
    const parts = [];
    if (includeQualification) {
      const qname = (qualifications.find(q => q.id === selectedQualificationId)?.qualificationDescription) || (entry?.QualificationDescription ?? entry?.qualificationDescription ?? '');
      pushSearchPart(parts, qname);
    }
    if (includeSubject) {
      const s = subjects.find(x => x.id === selectedSubjectId);
      const sdesc = s ? s.subjectDescription : (entry?.SubjectDescription ?? entry?.subjectDescription);
      pushSearchPart(parts, sdesc);
    }
    if (includeTopic) {
      const t = topics.find(x => x.id === selectedTopicId);
      const tpurpose = t ? t.topicPurpose : '';
      const tdesc = t ? t.topicDescription : '';
      const tcredits = t?.subjectCredits ? `Subject Credits ${t.subjectCredits}` : '';
      const thours = t?.notionalHours ? `Notional Hours ${t.notionalHours}` : '';
      const tperiods = t?.periodsPerTopic ? `Periods per Topic ${t.periodsPerTopic}` : '';
      pushSearchPart(parts, tpurpose);
      pushSearchPart(parts, tdesc);
      pushSearchPart(parts, tcredits);
      pushSearchPart(parts, thours);
      pushSearchPart(parts, tperiods);
    }
    if (includeCriteria) {
      const c = criteria.find(x => x.id === selectedCriteriaId);
      const cdesc = c ? c.description : (entry?.AssessmentCriteriaDescription ?? entry?.assessmentCriteriaDescription);
      pushSearchPart(parts, cdesc);
    }
    if (includeLesson) {
      const ldesc = selectedLessonDescription || (entry?.LessonPlanDescription ?? entry?.lessonPlanDescription);
      pushSearchPart(parts, ldesc);
    }
    if (parts.length === 0) {
      const ldesc = sanitizeSearchDescription(entry?.LessonPlanDescription ?? entry?.lessonPlanDescription ?? '');
      setQueryText(ldesc);
    } else {
      setQueryText(parts.join(' '));
    }
  }, [includeQualification, includeSubject, includeTopic, includeCriteria, includeLesson, subjects, topics, criteria, selectedSubjectId, selectedTopicId, selectedCriteriaId, entry, qualifications, selectedQualificationId, selectedLessonDescription, offlineMode]);

  const codeSanity = useMemo(() => {
    const activeEntry = Number(selectedLpnEntryId || 0) > 0
      ? (sortedLpnEntries.find(row => readToolkitEntryId(row) === Number(selectedLpnEntryId || 0)) || entry)
      : entry;
    const selectedSubject = subjects.find(s => Number(s?.id ?? 0) === Number(selectedSubjectId || 0)) || null;
    const selectedTopic = topics.find(t => Number(t?.id ?? 0) === Number(selectedTopicId || 0)) || null;
    const selectedCriteria = criteria.find(c => Number(c?.id ?? 0) === Number(selectedCriteriaId || 0)) || null;
    const entrySubjectCode = String(activeEntry?.SubjectCode ?? activeEntry?.subjectCode ?? '').trim();
    const selectedSubjectCode = String(selectedSubject?.subjectCode ?? '').trim();
    const entrySubjectCodeNorm = normalizeCodeKey(entrySubjectCode);
    const selectedSubjectCodeNorm = normalizeCodeKey(selectedSubjectCode);
    const entryCriteriaId = Number(activeEntry?.AssessmentCriteriaId ?? activeEntry?.assessmentCriteriaId ?? 0);
    const selectedCriteriaIdValue = Number(selectedCriteria?.id ?? selectedCriteriaId ?? 0);
    const entryCriteriaDescription = String(activeEntry?.AssessmentCriteriaDescription ?? activeEntry?.assessmentCriteriaDescription ?? '').trim();
    const selectedCriteriaDescription = String(selectedCriteria?.description ?? '').trim();
    const entryCriteriaDescriptionNorm = normalizeLooseText(entryCriteriaDescription);
    const selectedCriteriaDescriptionNorm = normalizeLooseText(selectedCriteriaDescription);
    const entryCriteriaExistsInSelectedTopicList = entryCriteriaId > 0
      && criteria.some(c => Number(c?.id ?? 0) === entryCriteriaId);
    const criteriaTopicId = Number(selectedCriteria?.topicId ?? 0);
    const selectedTopicIdValue = Number(selectedTopic?.id ?? selectedTopicId ?? 0);

    const subjectCodeMatch = !entrySubjectCodeNorm || !selectedSubjectCodeNorm || entrySubjectCodeNorm === selectedSubjectCodeNorm;
    const criteriaDescriptionMatch = entryCriteriaDescriptionNorm
      && selectedCriteriaDescriptionNorm
      && entryCriteriaDescriptionNorm === selectedCriteriaDescriptionNorm;
    const criteriaIdMatch =
      !entryCriteriaId
      || !selectedCriteriaIdValue
      || entryCriteriaId === selectedCriteriaIdValue
      // Some template imports use legacy criteria numbers instead of DB ids.
      || (!entryCriteriaExistsInSelectedTopicList && criteriaDescriptionMatch);
    const criteriaTopicMatch = !criteriaTopicId || !selectedTopicIdValue || criteriaTopicId === selectedTopicIdValue;

    const warnings = [];
    if (!subjectCodeMatch) warnings.push(`Subject code mismatch (entry=${entrySubjectCode}, selected=${selectedSubjectCode})`);
    if (!criteriaIdMatch) warnings.push(`Criteria mismatch (entry=${entryCriteriaId}, selected=${selectedCriteriaIdValue})`);
    if (!criteriaTopicMatch) warnings.push('Selected topic does not match selected criteria topic.');

    return {
      ok: warnings.length === 0,
      warnings
    };
  }, [entry, selectedLpnEntryId, sortedLpnEntries, subjects, selectedSubjectId, topics, selectedTopicId, criteria, selectedCriteriaId]);

  const runContentExporting = async () => {
    const qid = Number(selectedQualificationId || entryQualificationId(entry) || routeQualificationId || qualificationId || 0);
    const sid = Number(selectedSubjectId || 0);

    setExportError('');
    setExportStatus('');
    setExportSummary(null);

    if (!qid) {
      setExportError('Select a qualification before exporting learner guide content.');
      return;
    }
    if (!sid) {
      setExportError('Select a subject before exporting learner guide content.');
      return;
    }

    setExportingGuide(true);
    setExportProgress(8);

    try {
      setExportStatus('Checking lesson content readiness...');
      const readinessRes = await fetch(`${API_BASE}/LearnerGuide/export-readiness?qualificationId=${qid}&subjectId=${sid}`);
      const readinessText = await readinessRes.text().catch(() => '');
      let readinessJson = null;
      try { readinessJson = readinessText ? JSON.parse(readinessText) : null; } catch {}
      if (!readinessRes.ok) {
        throw new Error((readinessJson && (readinessJson.error || readinessJson.message)) || readinessText || `Readiness check failed (${readinessRes.status})`);
      }

      setExportSummary(readinessJson);
      setExportProgress(35);

      const ready = Boolean(readinessJson?.ready);
      if (!ready && !exportForce) {
        throw new Error('No mapped lesson content is ready for export. Enable "Force Export" to continue.');
      }

      if (exportUseParaphrase) {
        setExportStatus(exportForce
          ? 'Force refresh enabled: rebuilding paraphrase workflow cache...'
          : 'Refreshing paraphrase workflow cache...');
        setExportProgress(55);
        const workflowRes = await fetch(`${API_BASE}/LearnerGuide/paraphrase-workflow`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            qualificationId: qid,
            style: 'educational',
            preserveTerminology: true,
            forceRefresh: exportForce
          })
        });
        if (!workflowRes.ok) {
          const msg = await workflowRes.text().catch(() => '');
          throw new Error(msg || `Paraphrase refresh failed (${workflowRes.status})`);
        }
      }

      setExportStatus('Finalizing export readiness state...');
      setExportProgress(74);

      const finalReadinessRes = await fetch(`${API_BASE}/LearnerGuide/export-readiness?qualificationId=${qid}&subjectId=${sid}`);
      const finalReadinessText = await finalReadinessRes.text().catch(() => '');
      let finalReadinessJson = null;
      try { finalReadinessJson = finalReadinessText ? JSON.parse(finalReadinessText) : null; } catch {}
      if (!finalReadinessRes.ok) {
        throw new Error(
          (finalReadinessJson && (finalReadinessJson.error || finalReadinessJson.message))
          || finalReadinessText
          || `Final readiness check failed (${finalReadinessRes.status})`
        );
      }
      if (finalReadinessJson && typeof finalReadinessJson === 'object') {
        setExportSummary(finalReadinessJson);
      }

      setExportProgress(100);
      setExportStatus('Content export data prepared successfully. No file was downloaded. Review on-screen first, then use Learner Guide Export or Learning Material Dashboard.');
    } catch (err) {
      setExportError(String(err?.message || err));
    } finally {
      setExportingGuide(false);
      setTimeout(() => {
        setExportProgress(0);
      }, 1200);
    }
  };

  const fetchMaterialTextById = async (mid, opts = {}) => {
    const { silent = false, fallbackSnippet = '', paragraphIndex = 0 } = opts;
    if (!silent) {
      setStatus('');
      setFetchedText('');
    }
    setSelectedMaterialId(mid);
    const res = await fetch(`${API_CONTENT}/materials/${mid}/text`);
    if (!res.ok) {
      if (!silent) setStatus(await res.text());
      return '';
    }
    const json = await res.json();
    const raw = String(json.text || '');
    const indexedParagraph = extractParagraphByIndex(raw, paragraphIndex);
    const best = indexedParagraph || buildRelevantPassage(raw, queryText);
    const cleaned = cleanImportedText(best || raw || fallbackSnippet);
    setFetchedText(cleaned);
    return cleaned;
  };

  const searchLocal = async () => {
    const q = String(queryText || '').trim();
    if (!q) return [];
    const selectedSubject = subjects.find(s => s.id === selectedSubjectId);
    const selectedTopic = topics.find(t => t.id === selectedTopicId);
    const selectedCriteria = criteria.find(c => c.id === selectedCriteriaId);
    const selectedQualification = qualifications.find(qf => qf.id === selectedQualificationId);
    const basePayload = {
      Query: q,
      Limit: 20,
      SnippetLength: 360,
      QualificationCode: selectedQualification?.qualificationNumber || '',
      QualificationDescription: selectedQualification?.qualificationDescription || entry?.QualificationDescription || entry?.qualificationDescription || '',
      SubjectDescription: selectedSubject?.subjectDescription || entry?.SubjectDescription || entry?.subjectDescription || '',
      SubjectCode: selectedSubject?.subjectCode || entry?.SubjectCode || entry?.subjectCode || '',
      TopicDescription: selectedTopic?.topicDescription || '',
      AssessmentCriteriaDescription: selectedCriteria?.description || entry?.AssessmentCriteriaDescription || entry?.assessmentCriteriaDescription || ''
    };

    const mapLocalResults = (arr) => (Array.isArray(arr) ? arr : []).map(r => ({
      title: r?.title || 'Untitled',
      materialId: Number(r?.materialId || 0),
      snippet: String(r?.snippet || ''),
      score: Number(r?.score || 0),
      paragraphIndex: Number(r?.paragraphIndex || 0),
      knowledgePool: String(r?.knowledgePool || ''),
      knowledgeSourceType: String(r?.knowledgeSourceType || ''),
      knowledgeNumber: Number(r?.knowledgeNumber || 0),
      qualificationCode: String(r?.qualificationCode || ''),
      url: String(r?.url || '')
    }));

    const searchBySourceType = async (knowledgeSourceType, limitValue) => {
      try {
        const res = await fetch(`${API_CONTENT}/search-local`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            ...basePayload,
            Limit: Math.max(1, Number(limitValue || 0)),
            KnowledgeSourceType: knowledgeSourceType
          })
        });
        if (!res.ok) return [];
        const json = await res.json();
        return mapLocalResults(json?.results);
      } catch {
        return [];
      }
    };

    const dedupeLocalResults = (items) => {
      const out = [];
      const seen = new Set();
      for (const item of Array.isArray(items) ? items : []) {
        const key = [
          Number(item?.materialId || 0),
          Number(item?.paragraphIndex || 0),
          String(item?.url || '').trim().toLowerCase(),
          String(item?.title || '').trim().toLowerCase()
        ].join('|');
        if (seen.has(key)) continue;
        seen.add(key);
        out.push(item);
      }
      return out;
    };

    const developerResults = await searchBySourceType('developer_knowledge_base', basePayload.Limit);
    const remaining = Math.max(0, basePayload.Limit - developerResults.length);
    const localResults = remaining > 0
      ? await searchBySourceType('local_source_upload', remaining)
      : [];

    const combinedPrioritized = dedupeLocalResults([...developerResults, ...localResults]).slice(0, basePayload.Limit);
    if (combinedPrioritized.length > 0) return combinedPrioritized;

    // Fallback to unfiltered local search in case legacy records miss source metadata.
    try {
      const res = await fetch(`${API_CONTENT}/search-local`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(basePayload)
      });
      if (res.ok) {
        const json = await res.json();
        const mapped = mapLocalResults(json?.results);
        if (mapped.length > 0) return mapped;
      }
    } catch {}

    // Ensure folder library is ingested before scanning (e.g., C:\ETDP\ETDP\Imports\{QualificationNumber})
    try {
      let qualNumber = '';
      const qid = entryQualificationId(entry);
      if (qid > 0) {
        const qres = await fetch(`${API_QUAL}/${qid}`);
        if (qres.ok) {
          const qjson = await qres.json();
          qualNumber = String(qjson?.QualificationNumber ?? qjson?.qualificationNumber ?? '').trim();
        }
      }
      if (qualNumber) {
        await fetch(`${API_CONTENT}/import-folder`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ QualificationNumber: qualNumber })
        });
      }
    } catch {}
    const res = await fetch(buildMaterialsUrl());
    if (!res.ok) return [];
    const list = await res.json();
    const arr = Array.isArray(list) ? list : [];
    setLocalScanning(true);
    setScanProgress({ current: 0, total: arr.length });
    setScannedTitles([]);
    setCurrentTitle('');
    const results = [];
    let i = 0;
    for (const m of arr) {
      const id = Number(m?.id ?? m?.Id ?? 0);
      i += 1;
      setScanProgress({ current: i, total: arr.length });
      const title = String(m?.Title ?? m?.title ?? `Material ${id}`);
      setCurrentTitle(title);
      setScannedTitles(prev => prev.length < 200 ? [...prev, title] : prev);
      if (!id) continue;
      try {
        const tres = await fetch(`${API_CONTENT}/materials/${id}/text`);
        if (!tres.ok) continue;
        const tjson = await tres.json();
        const text = String(tjson?.text || '');
        const idx = text.toLowerCase().indexOf(q.toLowerCase());
        if (idx >= 0) {
          const start = Math.max(0, idx - 100);
          const end = Math.min(text.length, idx + 200);
          const snippet = text.slice(start, end);
          results.push({
            title,
            materialId: id,
            snippet,
            score: 1,
            paragraphIndex: 0,
            knowledgePool: '',
            knowledgeSourceType: '',
            knowledgeNumber: 0,
            qualificationCode: '',
            url: ''
          });
        }
      } catch {}
      await new Promise(r => setTimeout(r, 50));
    }
    setLocalScanning(false);
    return results.slice(0, 20);
  };

  const escapeRegex = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const previewHtml = useMemo(() => {
    const q = String(queryText || '').trim();
    if (!highlightMatches || !fetchedText || !q) return '';
    const esc = (t) => t.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    const src = esc(fetchedText);
    const re = new RegExp(escapeRegex(q), 'gi');
    return src.replace(re, (m) => `<mark style="background:#fff59d;color:#000;border-radius:3px;padding:0 2px">${esc(m)}</mark>`);
  }, [highlightMatches, fetchedText, queryText]);

  const saveGoogleConfig = async () => {
    try {
      const payload = {};
      if (googleCx.trim()) payload.Cx = googleCx.trim();
      if (googleKey.trim()) payload.Key = googleKey.trim();
      if (!payload.Cx && !payload.Key) { setStatus('Enter CX or Key'); return; }
      const res = await fetch(`${API_CONTENT}/store-google`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      if (!res.ok) {
        setStatus(await res.text());
        return;
      }
      if (payload.Key) setGoogleKeyPresent(true);
      setStatus('Google configuration saved');
    } catch (e) {
      setStatus(String(e?.message || e));
    }
  };

  const testGoogle = async () => {
    try {
      if (backendOfflineEnforced) {
        setStatus('Backend offline mode is enforced. Google test is disabled.');
        return;
      }
      setOfflineMode(false);
      setProvider('google');
      setLocalFirst(false);
      await search();
    } catch (e) {
      setStatus(String(e?.message || e));
    }
  };

  const loadTopResultText = async (result, opts = {}) => {
    const { index = -1, silent = true } = opts;
    if (!result) return '';
    if (index >= 0) setActiveResultIndex(index);
    if (result.materialId) {
      return await fetchMaterialTextById(result.materialId, {
        silent,
        fallbackSnippet: result.snippet || '',
        paragraphIndex: Number(result?.paragraphIndex || 0)
      });
    }
    if (result.url) {
      setSelectedMaterialId(null);
      return await fetchUrl(result.url, { silent });
    }
    if (result.snippet) {
      setSelectedMaterialId(null);
      const cleaned = cleanImportedText(result.snippet);
      setFetchedText(cleaned);
      return cleaned;
    }
    setActiveResultIndex(-1);
    return '';
  };

  const confirmInternetFallback = () => {
    const prompt = 'No content found in local library, do you want to search the internet?';
    if (typeof window !== 'undefined' && typeof window.confirm === 'function') {
      return window.confirm(prompt);
    }
    return true;
  };

  const search = async () => {
    setSearching(true);
    setStatus('');
    setAutoMapSummary(null);
    setResults([]);
    setActiveResultIndex(-1);
    try {
      const selectedQualification = qualifications.find(qf => qf.id === selectedQualificationId);
      const selectedSubject = subjects.find(s => s.id === selectedSubjectId);
      const selectedTopic = topics.find(t => t.id === selectedTopicId);
      const selectedCriteria = criteria.find(c => c.id === selectedCriteriaId);
      const lessonDescription = selectedLessonDescription || entry?.LessonPlanDescription || entry?.lessonPlanDescription || '';

      if (offlineMode) {
        const localResults = await searchLocal();
        setResults(localResults);
        const developerCount = localResults.filter(r => String(r?.knowledgeSourceType || '').toLowerCase() === 'developer_knowledge_base').length;
        const localUploadCount = localResults.filter(r => String(r?.knowledgeSourceType || '').toLowerCase() === 'local_source_upload').length;
        let loaded = '';
        if (autoLoadTopResult && localResults.length > 0) {
          const pickIndex = pickAutoLoadResultIndex(localResults);
          loaded = await loadTopResultText(localResults[pickIndex], { index: pickIndex, silent: true });
        }
        setStatus(localResults.length
          ? `Found ${localResults.length} local result(s) [Offline] • Developer KB: ${developerCount} • Local Upload: ${localUploadCount}${loaded ? ' • top match loaded' : ''}`
          : 'No local results found [Offline]');
        return loaded;
      }
      if (localFirst) {
        const localResults = await searchLocal();
        if (localResults.length > 0) {
          setResults(localResults);
          const developerCount = localResults.filter(r => String(r?.knowledgeSourceType || '').toLowerCase() === 'developer_knowledge_base').length;
          const localUploadCount = localResults.filter(r => String(r?.knowledgeSourceType || '').toLowerCase() === 'local_source_upload').length;
          let loaded = '';
          if (autoLoadTopResult) {
            const pickIndex = pickAutoLoadResultIndex(localResults);
            loaded = await loadTopResultText(localResults[pickIndex], { index: pickIndex, silent: true });
          }
          setStatus(`Found ${localResults.length} local result(s) • Developer KB: ${developerCount} • Local Upload: ${localUploadCount}${loaded ? ' • top match loaded' : ''}`);
          return loaded;
        }
        if (provider === 'none') {
          setStatus('No content found in local library. Internet search is disabled because Provider is set to None.');
          return '';
        }

        const proceedWithInternet = confirmInternetFallback();
        if (!proceedWithInternet) {
          setStatus('No content found in local library. Internet search cancelled.');
          return '';
        }
        setStatus('No content found in local library. Searching the internet with current Content Builder parameters...');
      }

      // Unified search implementation (local-first, non-Microsoft web fallback)
      if (useOpenAI && provider === 'openai') {
        const unifiedPayload = {
          Query: queryText,
          QualificationCode: selectedQualification?.qualificationNumber || '',
          QualificationDescription: selectedQualification?.qualificationDescription || entry?.QualificationDescription || entry?.qualificationDescription || '',
          SubjectName: selectedSubject?.subjectCode || entry?.SubjectCode || entry?.subjectCode || '',
          SubjectDescription: selectedSubject?.subjectDescription || entry?.SubjectDescription || entry?.subjectDescription || '',
          TopicDescription: selectedTopic?.topicDescription || '',
          TopicPurpose: selectedTopic?.topicPurpose || '',
          LessonPlanDescription: lessonDescription,
          AssessmentCriteriaDescription: selectedCriteria?.description || (entry?.AssessmentCriteriaDescription ?? entry?.assessmentCriteriaDescription ?? ''),
          Provider: 'openai',
          UseOpenAI: true,
          ImportParams: {
            qualificationId: selectedQualificationId,
            subjectId: selectedSubjectId,
            topicId: selectedTopicId,
            criteriaId: selectedCriteriaId,
            lessonDescription: selectedLessonDescription
          }
        };

        const unifiedRes = await fetch(`${API_CONTENT}/search-unified`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(unifiedPayload)
        });

        if (!unifiedRes.ok) {
          setStatus(`Unified search failed: ${await unifiedRes.text()}`);
          return '';
        }

        const unifiedJson = await unifiedRes.json();
        const unifiedResults = Array.isArray(unifiedJson?.results) ? unifiedJson.results : [];
        setResults(unifiedResults);
        
        let loaded = '';
        if (autoLoadTopResult && unifiedResults.length > 0) {
          loaded = await loadTopResultText(unifiedResults[0], { index: 0, silent: true });
        }

        setStatus(unifiedResults.length
          ? `Found ${unifiedResults.length} unified result(s)${loaded ? ' • top match loaded' : ''}`
          : 'No unified results found.');
        return loaded;
      }

      // Fallback to generic search
      const payload = {
        Query: queryText,
        QualificationCode: selectedQualification?.qualificationNumber || '',
        SubjectName: selectedSubject?.subjectCode || entry?.SubjectCode || entry?.subjectCode || '',
        SubjectDescription: selectedSubject?.subjectDescription || entry?.SubjectDescription || entry?.subjectDescription || '',
        TopicDescription: selectedTopic?.topicDescription || '',
        TopicPurpose: selectedTopic?.topicPurpose || '',
        LessonPlanDescription: lessonDescription,
        AssessmentCriteriaDescription: selectedCriteria?.description || (entry?.AssessmentCriteriaDescription ?? entry?.assessmentCriteriaDescription ?? ''),
        QualificationDescription: selectedQualification?.qualificationDescription || entry?.QualificationDescription || entry?.qualificationDescription || '',
        Provider: provider,
        UseOpenAI: useOpenAI
      };
      const res = await fetch(`${API_CONTENT}/search`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      if (!res.ok) {
        setStatus(await res.text());
        return '';
      }
      const json = await res.json();
      const out = Array.isArray(json?.results) ? json.results : [];
      setResults(out);
      let loaded = '';
      if (autoLoadTopResult && out.length > 0) {
        loaded = await loadTopResultText(out[0], { index: 0, silent: true });
      }
      setStatus(out.length
        ? `Found ${out.length} web result(s)${loaded ? ' • top match loaded' : ''}`
        : 'No web results found.');
      return loaded;
    } finally {
      setSearching(false);
    }
  };

  const autoMapSources = async (opts = {}) => {
    const { insertTop = false } = opts;
    setAutoMapping(true);
    setStatus('');
    setAutoMapSummary(null);
    setResults([]);
    setActiveResultIndex(-1);
    try {
      const selectedQualification = qualifications.find(qf => qf.id === selectedQualificationId);
      const selectedSubject = subjects.find(s => s.id === selectedSubjectId);
      const selectedTopic = topics.find(t => t.id === selectedTopicId);
      const selectedCriteria = criteria.find(c => c.id === selectedCriteriaId);

      const payload = {
        QualificationCode: selectedQualification?.qualificationNumber || '',
        QualificationDescription: selectedQualification?.qualificationDescription || entry?.QualificationDescription || entry?.qualificationDescription || '',
        SubjectCode: selectedSubject?.subjectCode || entry?.SubjectCode || entry?.subjectCode || '',
        SubjectDescription: selectedSubject?.subjectDescription || entry?.SubjectDescription || entry?.subjectDescription || '',
        TopicDescription: selectedTopic?.topicDescription || entry?.TopicDescription || entry?.topicDescription || '',
        AssessmentCriteriaDescription: selectedCriteria?.description || entry?.AssessmentCriteriaDescription || entry?.assessmentCriteriaDescription || '',
        Limit: 20,
        SnippetLength: 420,
        IncludeDeveloperKnowledgeBase: true,
        IncludeLocalUploads: true,
        IncludeOtherLocalPools: true,
        RemoveBoilerplate: true
      };

      const res = await fetch(`${API_CONTENT}/auto-map-sources`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      if (!res.ok) {
        setStatus(`Auto-map failed: ${await res.text()}`);
        return '';
      }

      const json = await res.json();
      const mapped = Array.isArray(json?.results) ? json.results : [];
      setResults(mapped);
      setAutoMapSummary(json?.summary || null);

      if (mapped.length === 0) {
        setStatus('Auto-map found no matching local source material for the selected qualification/subject/topic.');
        return '';
      }

      let loaded = '';
      if (autoLoadTopResult || insertTop) {
        const pickIndex = pickAutoLoadResultIndex(mapped);
        loaded = await loadTopResultText(mapped[pickIndex], { index: pickIndex, silent: true });
      }

      if (insertTop) {
        const toInsert = String(loaded || fetchedText || '').trim();
        if (!toInsert) {
          setStatus('Auto-map found results but no insertable text was loaded.');
          return '';
        }
        await insertIntoLessonPlan(toInsert, { autoAdvance: true });
        return toInsert;
      }

      const summary = json?.summary || {};
      setStatus(
        `Auto-map found ${mapped.length} result(s) • ` +
        `Developer KB: ${Number(summary?.developerKnowledgeBase || 0)} • ` +
        `Local Upload: ${Number(summary?.localSourceUpload || 0)} • ` +
        `Other: ${Number(summary?.other || 0)}` +
        (loaded ? ' • top match loaded' : '')
      );
      return loaded;
    } finally {
      setAutoMapping(false);
    }
  };

  const fetchUrl = async (url, opts = {}) => {
    const { silent = false } = opts;
    if (!silent) {
      setStatus('');
      setFetchedText('');
    }
    const res = await fetch(`${API_CONTENT}/fetch-url`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ Url: url })
    });
    if (!res.ok) {
      if (!silent) setStatus(await res.text());
      return '';
    }
    const json = await res.json();
    const raw = String(json.text || '');
    const best = buildRelevantPassage(raw, queryText);
    const cleaned = cleanImportedText(best || raw);
    setFetchedText(cleaned);
    return cleaned;
  };

  const buildMaterialsUrl = (overrides = {}) => {
    const overrideQualificationId = Number(overrides?.qualificationId || 0);
    const fallbackQualificationId = Number(selectedQualificationId || entryQualificationId(entry) || routeQualificationId || qualificationId || 0);
    const resolvedQualificationId = overrideQualificationId || fallbackQualificationId;

    const selectedQualification = qualifications.find(
      (qf) => Number(qf?.id ?? 0) === resolvedQualificationId
    ) || null;

    const overrideQualificationCode = String(overrides?.qualificationCode || '').trim();
    const overrideQualificationDescription = String(overrides?.qualificationDescription || '').trim();
    const resolvedQualificationCode = overrideQualificationCode || String(selectedQualification?.qualificationNumber || '').trim();
    const resolvedQualificationDescription = overrideQualificationDescription
      || String(selectedQualification?.qualificationDescription || entry?.QualificationDescription || entry?.qualificationDescription || '').trim();

    const params = new URLSearchParams();
    if (resolvedQualificationId > 0) {
      params.set('qualificationId', String(resolvedQualificationId));
    } else if (resolvedQualificationCode) {
      params.set('qualificationCode', resolvedQualificationCode);
    } else if (resolvedQualificationDescription) {
      params.set('qualificationDescription', resolvedQualificationDescription);
    }

    const query = params.toString();
    return query ? `${API_CONTENT}/materials?${query}` : `${API_CONTENT}/materials`;
  };

  const uploadMaterialsBulk = async (e) => {
    const files = Array.from(e.target.files || []);
    if (!files.length) return;
    setUploading(true);
    setStatus('');
    try {
      // Fetch existing materials to avoid duplicate uploads by title (filename)
      const existing = await fetch(buildMaterialsUrl()).then(r => r.json()).catch(() => []);
      const existingTitles = new Set((Array.isArray(existing) ? existing : []).map(x => String(x.Title ?? x.title ?? '').toLowerCase()));
      let created = 0, failed = 0, skipped = 0;
      for (const file of files) {
        if (!file) { failed++; continue; }
        const ext = (file.name || '').toLowerCase();
        if (!ext.endsWith('.txt') && !ext.endsWith('.md') && !ext.endsWith('.docx') && !ext.endsWith('.pdf') && !ext.endsWith('.pptx')) { skipped++; continue; }
        if (existingTitles.has(String(file.name).toLowerCase())) { skipped++; continue; }
        // Sequential per-file upload reduces request size; backend limits raised to allow large PDFs
        const fd = new FormData();
        fd.append('file', file);
        fd.append('meta.Title', file.name);
        const activeUploadQualificationId = Number(selectedQualificationId || entryQualificationId(entry) || routeQualificationId || qualificationId || 0);
        const activeUploadQualification = qualifications.find(
          (qf) => Number(qf?.id ?? 0) === activeUploadQualificationId
        ) || null;
        const activeUploadQualificationDescription =
          activeUploadQualification?.qualificationDescription || entry?.QualificationDescription || entry?.qualificationDescription || '';
        if (activeUploadQualificationId <= 0 || !String(activeUploadQualificationDescription || '').trim()) {
          setStatus('Select a qualification before uploading local materials.');
          skipped++;
          continue;
        }
        if (activeUploadQualificationId > 0) {
          fd.append('meta.QualificationId', String(activeUploadQualificationId));
        }
        fd.append(
          'meta.QualificationDescription',
          activeUploadQualificationDescription
        );
        fd.append('meta.SubjectDescription', entry?.SubjectDescription || entry?.subjectDescription || '');
        fd.append('meta.TopicDescription', entry?.TopicDescription || entry?.topicDescription || '');
        fd.append('meta.AssessmentCriteriaDescription', entry?.AssessmentCriteriaDescription || entry?.assessmentCriteriaDescription || '');
        const res = await fetch(`${API_CONTENT}/upload-material`, { method: 'POST', body: fd });
        if (!res.ok) {
          const msg = await res.text();
          if ((res.status === 409) || /already uploaded/i.test(msg)) { skipped++; continue; }
          failed++; continue;
        }
        created++;
        await new Promise(r => setTimeout(r, 50));
      }
      setStatus(`Uploaded ${created} materials${failed ? `, ${failed} failed` : ''}${skipped ? `, ${skipped} skipped` : ''}.`);
      await loadMaterials();
    } finally {
      setUploading(false);
      e.target.value = '';
    }
  };

  const loadMaterials = async (overrides = {}) => {
    const res = await fetch(buildMaterialsUrl(overrides));
    if (!res.ok) return;
    const json = await res.json();
    const arr = Array.isArray(json) ? json : [];
    setMaterials(arr.map(normMaterial).filter(Boolean));
  };

  useEffect(() => {
    const activeMaterialQualificationId = Number(selectedQualificationId || entryQualificationId(entry) || routeQualificationId || qualificationId || 0);
    if (activeMaterialQualificationId <= 0 && !entry) return;
    loadMaterials({
      qualificationId: activeMaterialQualificationId,
      qualificationDescription: String(entry?.QualificationDescription ?? entry?.qualificationDescription ?? '').trim()
    }).catch(() => {});
    // eslint-disable-next-line react-hooks/exhaustive-deps -- fetch should rerun only when qualification context changes
  }, [selectedQualificationId, qualificationId, entry?.Id, entry?.id]);

  const importFromQualificationFolder = async () => {
    if (!entry) return;
    setUploading(true);
    setStatus('');
    try {
      let qualNumber = '';
      const qid = entryQualificationId(entry);
      if (qid > 0) {
        const qres = await fetch(`${API_QUAL}/${qid}`);
        if (qres.ok) {
          const q = await qres.json();
          qualNumber = String(q?.QualificationNumber ?? q?.qualificationNumber ?? '').trim();
        }
      }
      const body = { QualificationNumber: qualNumber || String(entry?.SubjectCode ?? entry?.subjectCode ?? '').replace(/[^\w\- ]+/g, '').trim() };
      const res = await fetch(`${API_CONTENT}/import-folder`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body)
      });
      if (!res.ok) {
        setStatus(await res.text());
        return;
      }
      const data = await res.json();
      setStatus(`Imported ${data.created} materials${data.skipped ? `, ${data.skipped} skipped` : ''}.`);
      await loadMaterials({
        qualificationId: Number(qid || 0),
        qualificationDescription: String(entry?.QualificationDescription ?? entry?.qualificationDescription ?? '').trim()
      });
    } finally {
      setUploading(false);
    }
  };

  const insertIntoLessonPlan = async (contentOverride = '', opts = {}) => {
    const { autoAdvance = false } = opts;
    const source = String(contentOverride || fetchedText || '');
    if (!hasActiveToolkitTarget || !source.trim()) return false;
    const targetId = Number(activeToolkitTargetId || 0);
    const normalized = source
      .replace(/\r\n/g, '\n')
      .replace(/\n(?!\n)/g, '\n\n')
      .trim();
    const res = await fetch(`${API_CONTENT}/assemble`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        LecturerToolkitEntryId: targetId,
        Content: cite ? `${normalized}\n\n[CITE]` : normalized
      })
    });
    const body = await res.json().catch(async () => {
      const text = await res.text().catch(() => '');
      return text ? { message: text } : null;
    });
    if (!res.ok) {
      const msg = (body && (body.error || body.message)) || 'Insert failed.';
      setStatus(msg);
      return false;
    }

    const appended = body?.appended !== false;
    if (appended && targetId > 0) {
      await fetchToolkitEntryById(targetId).catch(() => {});
    }
    const reason = String(body?.reason || '').trim();
    const activeResult = activeResultIndex >= 0 ? results[activeResultIndex] : null;
    const activeKey = activeResult ? resultKey(activeResult, activeResultIndex) : '';
    if (activeKey) setLastInsertedResultKey(activeKey);

    let nextLoaded = false;
    let nextIndex = -1;
    if (autoAdvance && activeResultIndex >= 0) {
      nextIndex = findNextApplicableResultIndex(results, activeResultIndex);
      if (nextIndex >= 0) {
        const loaded = await loadTopResultText(results[nextIndex], { index: nextIndex, silent: true });
        nextLoaded = Boolean(String(loaded || '').trim());
      }
    }
    if (autoAdvance && !nextLoaded) {
      clearFetchedPanel();
    }

    if (appended) {
      setStatus(nextLoaded
        ? `Content inserted into Lesson Plan. Loaded next applicable paragraph (${nextIndex + 1}/${results.length}).`
        : 'Content inserted into Lesson Plan.');
      return true;
    }

    if (reason === 'duplicate_exact' || reason === 'duplicate_segment') {
      setStatus(nextLoaded
        ? 'Selected content already exists in Lesson Plan. Loaded next applicable paragraph for insertion.'
        : 'Selected content already exists in Lesson Plan.');
      return false;
    }

    setStatus('Content saved.');
    return appended;
  };

  const openFinalizeEditor = async () => {
    const targetId = Number(activeToolkitTargetId || 0);
    if (!targetId) {
      setStatus('Select an LPN that matches the current subject and criteria first.');
      return;
    }
    setStatus('');
    const row = await fetchToolkitEntryById(targetId);
    if (!row) {
      setStatus(`Could not load ${lpnLabelForEntryId(targetId)}.`);
      return;
    }
    const existing = String(row?.LessonPlanContent ?? row?.lessonPlanContent ?? '').trim();
    const fallback = String(fetchedText || '').trim();
    setFinalizeTargetEntryId(targetId);
    setFinalizeText(existing || fallback);
    setFinalizeModalOpen(true);
  };

  const confirmFinalizeCurrentLpn = async () => {
    const targetId = Number(finalizeTargetEntryId || activeToolkitTargetId || 0);
    const content = String(finalizeText || '').trim();
    if (!targetId) {
      setStatus('Select an LPN that matches the current subject and criteria first.');
      return;
    }
    if (!content) {
      setStatus('Finalised Lesson Plan content cannot be empty.');
      return;
    }
    const confirmationText = `Confirm ${lpnLabelForEntryId(targetId)} as finalised and move to the next LPN?`;
    const proceed = (typeof window !== 'undefined' && typeof window.confirm === 'function')
      ? window.confirm(confirmationText)
      : true;
    if (!proceed) return;

    setFinalizeSaving(true);
    try {
      await updateToolkitEntryContent(targetId, content);
      markEntryFinalized(targetId);
      setFinalizeModalOpen(false);
      clearFetchedPanel();

      const nextId = findNextLpnEntryId(targetId);
      if (nextId > 0) {
        handleLessonSelection(`id:${nextId}`);
        setStatus(`${lpnLabelForEntryId(targetId)} finalised. Moved to ${lpnLabelForEntryId(nextId)}.`);
      } else {
        setStatus(`${lpnLabelForEntryId(targetId)} finalised. No next LPN available.`);
      }
    } catch (err) {
      setStatus(`Finalise failed: ${String(err?.message || err)}`);
    } finally {
      setFinalizeSaving(false);
    }
  };

  const searchAndInsertTopMatch = async () => {
    const loaded = await search();
    const toInsert = String(loaded || fetchedText || '').trim();
    if (!toInsert) {
      setStatus('No content loaded from search to insert.');
      return;
    }
    await insertIntoLessonPlan(toInsert, { autoAdvance: true });
  };

  const moderatorInsertBestContext = async () => {
    const targetId = Number(activeToolkitTargetId || 0);
    if (!targetId) {
      setStatus('Select an LPN that matches the current subject and criteria first.');
      return;
    }
    setStatus('');
    setPreviewLoading(true);
    try {
      const selectedQualification = qualifications.find(qf => qf.id === selectedQualificationId);
      const selectedSubject = subjects.find(s => s.id === selectedSubjectId);
      const selectedTopic = topics.find(t => t.id === selectedTopicId);
      const selectedCriteria = criteria.find(c => c.id === selectedCriteriaId);

      const payload = {
        LecturerToolkitEntryId: targetId,
        Query: queryText,
        QualificationCode: selectedQualification?.qualificationNumber || '',
        QualificationDescription: selectedQualification?.qualificationDescription || entry?.QualificationDescription || entry?.qualificationDescription || '',
        SubjectDescription: selectedSubject?.subjectDescription || entry?.SubjectDescription || entry?.subjectDescription || '',
        SubjectCode: selectedSubject?.subjectCode || entry?.SubjectCode || entry?.subjectCode || '',
        TopicDescription: selectedTopic?.topicDescription || '',
        AssessmentCriteriaDescription: selectedCriteria?.description || entry?.AssessmentCriteriaDescription || entry?.assessmentCriteriaDescription || '',
        LessonPlanDescription: selectedLessonDescription || entry?.LessonPlanDescription || entry?.lessonPlanDescription || '',
        Cite: false,
        CandidateLimit: 8,
        SnippetLength: 1800,
        DryRun: true,
        UseHierarchicalCascade: true
      };

      const res = await fetch(`${API_CONTENT}/moderator-insert-best-context`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      const body = await res.json().catch(() => null);
      if (!res.ok) {
        setStatus((body && (body.error || body.message)) || 'Context insert failed.');
        return;
      }
      const proposed = String(body?.proposedContent || '').trim();
      if (!proposed) {
        setStatus('Context selector returned empty content.');
        return;
      }
      setPreviewContext(proposed);
      setPreviewMeta({
        selectedTitle: body?.selectedTitle || '',
        selectionBackend: body?.selectionBackend || '',
        selectedScore: body?.selectedScore ?? null
      });
      setPreviewModalOpen(true);
      setStatus('Preview ready. Review and confirm insert.');
    } catch (err) {
      setStatus(`Context insert failed: ${String(err?.message || err)}`);
    } finally {
      setPreviewLoading(false);
    }
  };

  const confirmModeratorInsert = async () => {
    const txt = String(previewContext || '').trim();
    if (!txt) {
      setStatus('Preview is empty.');
      return;
    }
    setPreviewSaving(true);
    try {
      const ok = await insertIntoLessonPlan(txt, { autoAdvance: false });
      if (ok) {
        const source = previewMeta?.selectedTitle ? ` Source: ${previewMeta.selectedTitle}.` : '';
        setStatus(`Content inserted into Lesson Plan.${source}`);
        setPreviewModalOpen(false);
      }
    } finally {
      setPreviewSaving(false);
    }
  };

  const draftWithGemini = async () => {
    if (!entry) return;
    setStatus('');
    setDraftText('');
    const sources = [];
    if (fetchedText) sources.push(fetchedText);
    const res = await fetch(`${API_CONTENT}/draft`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        SubjectName: entry.subjectCode,
        SubjectDescription: entry.subjectDescription,
        TopicDescription: '',
        TopicPurpose: '',
        LessonPlanDescription: entry.lessonPlanDescription,
        AssessmentCriteriaDescription: entry.assessmentCriteriaDescription || '',
        LecturerActions: entry.lecturerActions || '',
        LearnerActions: entry.learnerActions || '',
        Sources: sources,
        Length: '800–1200 words',
        Level: 'Grade 11 TVET'
      })
    });
    if (!res.ok) {
      setStatus(await res.text());
      return;
    }
    const json = await res.json();
    setDraftText(json.content || '');
    const backend = String(json?.backend || '').trim();
    if (backend) {
      setStatus(`Draft generated via ${backend}.`);
    }
  };

  const insertDraft = async () => {
    const targetId = Number(activeToolkitTargetId || 0);
    if (!targetId || !draftText) return;
    const normalized = String(draftText)
      .replace(/\r\n/g, '\n')
      .replace(/\n(?!\n)/g, '\n\n')
      .trim();
    const res = await fetch(`${API_CONTENT}/assemble`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ LecturerToolkitEntryId: targetId, Content: normalized })
    });
    if (!res.ok) {
      setStatus(await res.text());
      return;
    }
    await fetchToolkitEntryById(targetId).catch(() => {});
    setStatus('Draft inserted into Lesson Plan.');
  };

  const selectedQualificationName = (qualifications.find(q => q.id === selectedQualificationId)?.qualificationDescription) || (entry?.QualificationDescription ?? entry?.qualificationDescription ?? '—');
  const selectedSubject = subjects.find(s => s.id === selectedSubjectId);
  const selectedTopic = topics.find(t => t.id === selectedTopicId);
  const selectedSubjectName = selectedSubject
    ? `${selectedSubject.subjectCode ? `${selectedSubject.subjectCode} - ` : ''}${selectedSubject.subjectDescription}`
    : (entry?.SubjectDescription ?? entry?.subjectDescription ?? '—');
  const selectedTopicName = selectedTopic
    ? `${selectedTopic.topicCode ? `${selectedTopic.topicCode} - ` : ''}${selectedTopic.topicDescription}`
    : '—';
  const selectedCriteriaName = (criteria.find(c => c.id === selectedCriteriaId)?.description) || (entry?.AssessmentCriteriaDescription ?? entry?.assessmentCriteriaDescription ?? '—');
  const activeWorkflowQualificationId = Number(selectedQualificationId || routeQualificationId || qualificationId || entryQualificationId(entry) || 0);
  const workflowState = activeWorkflowQualificationId > 0 ? { qualificationId: activeWorkflowQualificationId } : undefined;
  const entryLpnLabel = (() => {
    const raw = readLpn(entry);
    return raw ? formatLpnLabel(raw) : '';
  })();
  const draftButtonLabel = backendOfflineEnforced
    ? 'Draft with Local AI (Offline)'
    : (runtimeConfig?.localLlmConfigured ? 'Draft with Local AI' : 'Draft with Google Gemini');
  const statusIsSuccess = /(inserted|uploaded|imported|saved|found|loaded|processed|draft|finalised|finalized|moved|prepared)/i.test(String(status || ''));

  return (
    <div className="page-container">
      {errors.length > 0 && (
        <div className="cb-diagnostics">
          <div className="cb-diagnostics-title">Diagnostics</div>
          {errors.map((e, i) => (<div key={i} className="cb-diagnostics-row">{e}</div>))}
        </div>
      )}
      <h2>Content Builder</h2>
      <p>Search web or upload local sources, then insert selected content into Lesson Plan Content.</p>
      <p className="cb-flow-note">Workflow: Engine (final authoring step after Library Manager and Lecturer Toolkit)</p>
      {runtimeConfig && (
        <div className="cb-entry-card">
          <div><strong>Runtime:</strong> {String(runtimeConfig?.aiMode || 'offline')}</div>
          <div><strong>Google Gemini:</strong> {runtimeConfig?.openAiConfigured ? `${runtimeConfig?.openAiModel || 'configured'} ready` : (runtimeConfig?.openAiEnabled ? 'enabled, key not stored' : 'disabled by AI mode')}</div>
          <div>
            <strong>Local Library:</strong> {localLibraryExists ? 'Available' : 'Missing'} {localLibraryPath ? `• ${localLibraryPath}` : ''}
          </div>
          <div className="cb-grid-3" style={{ marginTop: 10 }}>
            <label>
              AI Mode
              <select
                className="mainpage-input"
                value={runtimeForm.aiMode}
                onChange={e => setRuntimeForm(prev => ({ ...prev, aiMode: e.target.value }))}
              >
                <option value="offline">offline</option>
                <option value="hybrid">hybrid</option>
                <option value="cloud">cloud</option>
              </select>
            </label>
            <label>
              Google Gemini Model
              <input
                className="mainpage-input"
                value={runtimeForm.openAiModel}
                onChange={e => setRuntimeForm(prev => ({ ...prev, openAiModel: e.target.value }))}
                placeholder="gpt-5-mini"
              />
            </label>
            <label>
              Google Gemini API Key
              <input
                className="mainpage-input"
                type="password"
                value={runtimeForm.openAiApiKey}
                onChange={e => setRuntimeForm(prev => ({ ...prev, openAiApiKey: e.target.value }))}
                placeholder={runtimeConfig?.openAiConfigured ? 'Stored' : 'Paste key to enable'}
              />
            </label>
          </div>
          <div className="cb-grid-3" style={{ marginTop: 8 }}>
            <label>
              Local LLM Endpoint
              <input
                className="mainpage-input"
                value={runtimeForm.localLlmEndpoint}
                onChange={e => setRuntimeForm(prev => ({ ...prev, localLlmEndpoint: e.target.value }))}
                placeholder="http://127.0.0.1:11434/api/chat"
              />
            </label>
            <label>
              Local LLM Model
              <input
                className="mainpage-input"
                value={runtimeForm.localLlmModel}
                onChange={e => setRuntimeForm(prev => ({ ...prev, localLlmModel: e.target.value }))}
                placeholder="llama3.1:8b"
              />
            </label>
            <div style={{ display: 'flex', alignItems: 'end' }}>
              <button type="button" onClick={saveRuntimeConfig} disabled={runtimeSaving}>
                {runtimeSaving ? 'Saving...' : 'Save AI Mode'}
              </button>
            </div>
          </div>
        </div>
      )}
      <div className="cb-top-actions">
        <button onClick={() => navigate('/lecturer-toolkit', { state: workflowState })}>Back to Lecturer Toolkit</button>
        <button
          type="button"
          onClick={async () => {
            const targetId = Number(activeToolkitTargetId || 0);
            if (!targetId) {
              setStatus('Select an LPN that matches the current subject and criteria first.');
              return;
            }
            try {
              await refreshToolkitEntryContext(targetId, { preloadExisting: true });
            } catch (err) {
              setStatus(`Refresh failed: ${String(err?.message || err)}`);
            }
          }}
        >
          Refresh Entry Data
        </button>
        <button onClick={() => navigate('/lesson-plan-review', { state: workflowState })}>Open Lesson Plan Review</button>
        <button onClick={() => navigate('/qualia/mira')}>Open Mira Qualia</button>
      </div>
      {entry ? (
        <div className="cb-entry-card">
          <div>
            <strong>Toolkit Entry:</strong> #{entry?.id ?? entry?.Id ?? '-'} • {entry?.subjectCode ?? entry?.SubjectCode ?? '—'} • {entry?.subjectDescription ?? entry?.SubjectDescription ?? '—'}
          </div>
          <div>
            <strong>Lesson Plan:</strong> {entryLpnLabel ? `${entryLpnLabel} - ` : ''}{entry?.lessonPlanDescription ?? entry?.LessonPlanDescription ?? '—'}
          </div>
          <div><strong>Assessment Criteria:</strong> {entry?.assessmentCriteriaDescription ?? entry?.AssessmentCriteriaDescription ?? '—'}</div>
        </div>
      ) : <div>Loading entry...</div>}

      <div className="cb-controls">
        <div className="cb-controls-title">Engine Controls • Build your Learner Guide</div>
        <div className="cb-controls-row">
          <label className="cb-control-field">Curriculum Code
            <select value={selectedQualificationId} onChange={async (e) => {
              const val = Number(e.target.value);
              setSelectedQualificationId(val);
              try { setQualificationId(val); } catch (_) {}
              await loadSubjectsForQualification(val);
            }} className="mainpage-input">
              <option value={0}>Select…</option>
              {qualifications.map(q => (
                <option key={q.id} value={q.id}>{q.qualificationNumber ? `${q.qualificationNumber} - ${q.qualificationDescription}` : q.qualificationDescription}</option>
              ))}
            </select>
          </label>
          <label className="cb-control-field">Subject Code / Description
            <select value={selectedSubjectId} onChange={async (e) => {
              const sid = Number(e.target.value);
              setSelectedSubjectId(sid);
              await loadTopicsForSubject(sid);
            }} className="mainpage-input">
              <option value={0}>Select…</option>
              {subjects.map(s => (
                <option key={s.id} value={s.id}>
                  {s.subjectCode ? `${s.subjectCode} - ${s.subjectDescription}` : s.subjectDescription}
                </option>
              ))}
            </select>
          </label>
          <label className="cb-control-field">Topic Code / Description
            <select value={selectedTopicId} onChange={async (e) => {
              const tid = Number(e.target.value);
              const topicFromCurrentList = topics.find(t => Number(t?.id ?? 0) === tid) || null;
              const topicSubjectId = Number(topicFromCurrentList?.subjectId ?? 0);
              if (tid > 0 && topicSubjectId > 0 && topicSubjectId !== Number(selectedSubjectId || 0)) {
                setSelectedSubjectId(topicSubjectId);
                await loadTopicsForSubject(topicSubjectId, {
                  preferredTopicId: tid,
                  preferredCriteriaId: Number(selectedCriteriaId || 0)
                });
                return;
              }
              setSelectedTopicId(tid || 0);
              await loadCriteriaForTopic(tid, { preferredCriteriaId: Number(selectedCriteriaId || 0) });
            }} className="mainpage-input">
              <option value={0}>Select…</option>
              {topics.map(t => (
                <option key={t.id} value={t.id}>
                  {t.topicCode ? `${t.topicCode} - ${t.topicDescription}` : t.topicDescription}
                </option>
              ))}
            </select>
          </label>
          <label className="cb-control-field">Assessment Criteria
            <select value={selectedCriteriaId} onChange={async (e) => {
              const cid = Number(e.target.value);
              setSelectedCriteriaId(cid || 0);
              await loadLpnEntriesForCriteria(cid);
            }} className="mainpage-input">
              <option value={0}>Select…</option>
              {filteredCriteria.map(c => (
                <option key={c.id} value={c.id}>{c.topicCode ? `${c.topicCode} - ${c.description}` : c.description}</option>
              ))}
            </select>
          </label>
          <label className="cb-inline-check cb-inline-check-spaced">
            <input type="checkbox" checked={cite} onChange={e => setCite(e.target.checked)} />
            <span className="cb-inline-check-text">Cite</span>
          </label>
        </div>
        <div className="cb-muted cb-top-desc">
          {(() => {
            const topic = topics.find(t => t.id === selectedTopicId) || topics[0] || null;
            if (!topic) return 'Topic Description: —';
            const bits = [
              `Topic Description: ${topic.topicDescription || '—'}`,
              topic.periodsPerTopic ? `Periods/Topic: ${topic.periodsPerTopic}` : '',
              topic.subjectCredits ? `Subject Credits: ${topic.subjectCredits}` : '',
              topic.notionalHours ? `Notional Hours: ${topic.notionalHours}` : ''
            ].filter(Boolean);
            return bits.join(' • ');
          })()}
        </div>
      </div>

      <div className="cb-layout">
        <div className="cb-col">
          <div className="cb-block">
            <label className="cb-control-field">
              Provider:
              <select value={provider} onChange={e => setProvider(e.target.value)} className="mainpage-input" disabled={offlineMode || backendOfflineEnforced}>
                <option value="none">None (disable fallback)</option>
                <option value="wikipedia">Wikipedia (keyless)</option>
                <option value="openaip">OpenAIP / Figshare (keyless public)</option>
                <option value="searx">Searx (self-hosted)</option>
                <option value="google">Google</option>
                <option value="google">Google Gemini Search</option>
              </select>
            </label>
            <div className="cb-muted cb-help-text">
              Local documents are always searched first. If Provider is 'None' the search will not use any web fallback.
            </div>
          </div>
          <div className="cb-block">
            <label className="cb-inline-check">
              <input type="checkbox" checked={localFirst} onChange={e => setLocalFirst(e.target.checked)} disabled={offlineMode || backendOfflineEnforced} />
              <span className="cb-inline-check-text cb-check-strong">Local Research first{offlineMode ? ' (enforced in Offline Mode)' : ''}</span>
            </label>
          </div>
          <div className="cb-block">
            <label className="cb-inline-check">
              <input type="checkbox" checked={useOpenAI} onChange={e => setUseOpenAI(e.target.checked)} disabled={offlineMode || backendOfflineEnforced} />
              <span className="cb-inline-check-text">Use Google Gemini for search</span>
            </label>
          </div>
          <div className="cb-block">
            <label className="cb-inline-check">
              <input type="checkbox" checked={offlineMode} onChange={e => setOfflineMode(e.target.checked)} disabled={backendOfflineEnforced} />
              <span className="cb-inline-check-text cb-check-strong">Offline Mode{backendOfflineEnforced ? ' (backend enforced)' : ''}</span>
            </label>
            {offlineMode && <div className="cb-muted cb-help-text">Provider disabled; Local search only</div>}
          </div>
          <div className="cb-block">
            <div className="cb-section-title">Google Config & Test</div>
            <div className="cb-grid-2">
              <input className="mainpage-input" placeholder="Google CX" value={googleCx} onChange={e => setGoogleCx(e.target.value)} disabled={offlineMode} />
              <input className="mainpage-input" placeholder="Google API Key" value={googleKey} onChange={e => setGoogleKey(e.target.value)} disabled={offlineMode} />
            </div>
            <div className="cb-muted cb-help-text">
              Google key status: {googleKeyPresent ? 'Configured' : 'Not configured'}
            </div>
            <div className="cb-button-row">
              <button type="button" onClick={saveGoogleConfig} disabled={offlineMode}>Save Google Config</button>
              <button type="button" onClick={testGoogle} disabled={offlineMode}>Test Google</button>
            </div>
          </div>
          <div className="cb-block">
            <div className="cb-section-title cb-section-title-compact">Compose Query from</div>
            <div className="cb-grid-2 cb-checkbox-grid">
              <label className="cb-inline-check"><input type="checkbox" checked={includeQualification} onChange={e => setIncludeQualification(e.target.checked)} /> <span className="cb-inline-check-text">Qualification</span></label>
              <label className="cb-inline-check"><input type="checkbox" checked={includeSubject} onChange={e => setIncludeSubject(e.target.checked)} /> <span className="cb-inline-check-text">Subject</span></label>
              <label className="cb-inline-check"><input type="checkbox" checked={includeTopic} onChange={e => setIncludeTopic(e.target.checked)} /> <span className="cb-inline-check-text">Topic</span></label>
              <label className="cb-inline-check"><input type="checkbox" checked={includeCriteria} onChange={e => setIncludeCriteria(e.target.checked)} /> <span className="cb-inline-check-text">Assessment Criteria</span></label>
              <label className="cb-inline-check"><input type="checkbox" checked={includeLesson} onChange={e => setIncludeLesson(e.target.checked)} /> <span className="cb-inline-check-text">Lesson Plan</span></label>
            </div>
          </div>
          <div className="cb-block">
            <label className="cb-control-field">Lesson Plan Lookup
              <select value={selectedLessonOptionValue} onChange={(e) => handleLessonSelection(e.target.value)} className="mainpage-input">
                <option value="">Select…</option>
                {lessonOptions.map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </select>
            </label>
            <div className="cb-muted cb-help-text">Included in Query when "Lesson Plan" is checked.</div>
            <div className="cb-muted cb-help-text">To view saved text for the selected lesson, click "Load Existing LPN Content".</div>
          </div>
          <div className="cb-block">
            <button onClick={importFromQualificationFolder} disabled={uploading}>Import From Qualification Folder</button>
          </div>
          <div className="cb-block">
            <label className="cb-control-field">Query
            <input className="mainpage-input" value={queryText} onChange={e => setQueryText(e.target.value)} />
            </label>
          </div>
          <div className="cb-block">
            <label className="cb-inline-check">
              <input type="checkbox" checked={highlightMatches} onChange={e => setHighlightMatches(e.target.checked)} />
              <span className="cb-inline-check-text">Highlight matches in loaded text</span>
            </label>
          </div>
          <div className="cb-block">
            <label className="cb-inline-check">
              <input type="checkbox" checked={autoLoadTopResult} onChange={e => setAutoLoadTopResult(e.target.checked)} />
              <span className="cb-inline-check-text">Auto-load top result text after search</span>
            </label>
          </div>
          <div className="cb-button-row">
            <button onClick={search} disabled={searching}>{searching ? 'Searching...' : 'Search'}</button>
            <button type="button" onClick={searchAndInsertTopMatch} disabled={searching || !hasActiveToolkitTarget}>
              {searching ? 'Working...' : 'Search and Insert Top Match'}
            </button>
            <button type="button" onClick={() => autoMapSources({ insertTop: false })} disabled={searching || autoMapping}>
              {autoMapping ? 'Auto-mapping...' : 'Auto-Map Sources'}
            </button>
            <button type="button" onClick={() => autoMapSources({ insertTop: true })} disabled={searching || autoMapping || !hasActiveToolkitTarget}>
              {autoMapping ? 'Auto-mapping...' : 'Auto-Map + Insert'}
            </button>
            <button type="button" onClick={moderatorInsertBestContext} disabled={searching || previewLoading || !hasActiveToolkitTarget}>
              {previewLoading ? 'Preparing Preview...' : 'Context Preview & Insert'}
            </button>
          </div>
          {localScanning && (
            <div className="cb-scan-wrap">
              <div>Scanning local library… {scanProgress.current}/{scanProgress.total}{currentTitle ? ` • Current: ${currentTitle}` : ''}</div>
              <div className="cb-progress-track">
                {(() => {
                  const pct = scanProgress.total > 0 ? Math.round((scanProgress.current / scanProgress.total) * 100) : 0;
                  return <div className="cb-progress-fill" style={{ width: `${pct}%` }} />;
                })()}
              </div>
              <div className="cb-scan-actions">
                <button type="button" onClick={() => setScanDetailsVisible(v => !v)}>
                  {scanDetailsVisible ? 'Hide scanned titles' : 'Show scanned titles'}
                </button>
              </div>
              {scanDetailsVisible && (
                <div className="cb-scan-details">
                  {scannedTitles.length === 0 ? <div>Starting…</div> : (
                    <ul className="cb-scan-list">
                      {scannedTitles.map((t, idx) => (<li key={idx}>{t}</li>))}
                    </ul>
                  )}
                </div>
              )}
            </div>
          )}
          <div className="cb-results-wrap">
            {autoMapSummary ? (
              <div className="cb-auto-map-summary">
                <div><strong>Auto-Map Summary:</strong> Total {Number(autoMapSummary?.total || 0)}</div>
                <div>Developer KB: {Number(autoMapSummary?.developerKnowledgeBase || 0)} • Local Upload: {Number(autoMapSummary?.localSourceUpload || 0)} • Other: {Number(autoMapSummary?.other || 0)}</div>
              </div>
            ) : null}
            {results.length === 0 ? <div>No results.</div> : (
              <ul className="cb-results-list">
                {results.map((r, idx) => {
                  const isLocal = !!r.materialId;
                  const paragraphLabel = Number(r?.paragraphIndex || 0) > 0 ? ` • Paragraph #${Number(r.paragraphIndex)}` : '';
                  const sourceTypeLabel = String(r?.knowledgeSourceType || '').trim();
                  const localMeta = `Material #${r.materialId}${paragraphLabel}${sourceTypeLabel ? ` • ${sourceTypeLabel}` : ''}`;
                  return (
                    <li key={resultKey(r, idx)} className="cb-result-item">
                      <div className="cb-result-title">{isLocal ? `[Local] ${r.title}` : r.title}</div>
                      <div className="cb-result-meta">{isLocal ? localMeta : (r.url || '')}</div>
                      <div className="cb-result-snippet">{r.snippet}</div>
                      <button onClick={() => loadTopResultText(r, { index: idx, silent: false })} className="cb-result-button">
                        {isLocal ? 'Load Local Content' : 'Fetch Content'}
                      </button>
                    </li>
                  );
                })}
              </ul>
            )}
          </div>
        </div>
        <div className="cb-col">
          <div>
            <label className="cb-control-field">Fetched/Uploaded Text</label>
            {highlightMatches && fetchedText ? (
              <div
                className="cb-preview"
                dangerouslySetInnerHTML={{ __html: previewHtml }}
              />
            ) : null}
            <textarea className="mainpage-input cb-fetched-textarea" value={fetchedText} onChange={e => setFetchedText(e.target.value)} rows={34} />
            {selectedMaterialId && (
              <div className="cb-button-row">
                <button type="button" onClick={() => window.open(`${API_CONTENT}/materials/${selectedMaterialId}/export/md`, '_blank')}>Export as .md</button>
                <button type="button" onClick={() => window.open(`${API_CONTENT}/materials/${selectedMaterialId}/export/txt`, '_blank')}>Export as .txt</button>
                <button type="button" onClick={() => window.open(`${API_CONTENT}/materials/${selectedMaterialId}/export/csv`, '_blank')}>Export as .csv</button>
                <button type="button" onClick={async () => {
                  const res = await fetch(`${API_CONTENT}/process-material/${selectedMaterialId}`, { method: 'POST' });
                  if (!res.ok) { setStatus(await res.text()); return; }
                  const json = await res.json();
                  setStatus(`Processed and ingested: #${json.createdId}`);
                  await loadMaterials();
                }}>Process PDF (MD) and ingest</button>
              </div>
            )}
          </div>
          <div className="cb-button-row cb-button-row-tight">
            <button onClick={() => insertIntoLessonPlan('', { autoAdvance: true })} disabled={!fetchedText || !hasActiveToolkitTarget}>Insert into Lesson Plan Content</button>
            <button onClick={loadExistingLessonContent} disabled={!hasActiveToolkitTarget}>Load Existing LPN Content</button>
            <button onClick={clearFetchedPanel}>Clear Text</button>
            <button onClick={openFinalizeEditor} disabled={!hasActiveToolkitTarget}>Max View + Finalise LPN</button>
            <button onClick={async () => { if (fetchedText && hasActiveToolkitTarget) { await insertIntoLessonPlan('', { autoAdvance: true }); } navigate('/lecturer-toolkit'); }}>Save and Return to Toolkit</button>
            <button onClick={() => navigate('/library')}>Back to Library Manager</button>
          </div>
          <hr className="cb-separator" />
          <div>
            <button onClick={draftWithGemini}>{draftButtonLabel}</button>
          </div>
          <div className="cb-block">
            <label className="cb-control-field">Draft Output</label>
            <textarea className="mainpage-input" value={draftText} onChange={e => setDraftText(e.target.value)} rows={18} />
          </div>
          <div className="cb-block">
            <button onClick={insertDraft} disabled={!draftText}>Insert Draft into Lesson Plan</button>
          </div>
        </div>
      </div>

      {sortedLpnEntries.length > 0 && (
        <div className="cb-lpn-dock">
          <div className="cb-section-title">LPN Sequence</div>
          <div className="cb-muted cb-help-text">Left to right in numeric order. Use Max View + Finalise to lock and move to next LPN.</div>
          <div className="cb-lpn-dock-row">
            {sortedLpnEntries.map((e2, idx) => {
              const entryId = Number(e2.id ?? e2.Id ?? 0);
              const done = String(e2.lessonPlanContent ?? e2.LessonPlanContent ?? '').trim().length > 0;
              const finalisedInSession = finalizedEntryIds.includes(entryId);
              const selected = Number(selectedLpnEntryId ?? 0) === entryId;
              const rawLpn = readLpn(e2);
              const displayLpnToken = useRelativeLpnSequence
                ? String(idx + 1)
                : (stripLpnPrefix(rawLpn) || '?');
              const lpnLabel = formatLpnLabel(displayLpnToken);
              const lpnTitleSuffix = useRelativeLpnSequence && rawLpn ? ` (stored: ${rawLpn})` : '';
              return (
                <div key={entryId > 0 ? entryId : `${displayLpnToken}-${idx}`} className="cb-lpn-dock-item">
                  <button
                    type="button"
                    onClick={() => handleLessonSelection(`id:${entryId}`)}
                    className={`cb-lpn-chip ${done ? 'is-done' : 'is-pending'} ${selected ? 'is-selected' : ''} ${finalisedInSession ? 'is-finalised' : ''}`}
                    title={`Select ${lpnLabel}${lpnTitleSuffix}`}
                  >
                    {lpnLabel}
                  </button>
                  <label className="cb-lpn-done-label" title={done ? `${lpnLabel} done` : `Mark ${lpnLabel} done`}>
                    <input
                      className="cb-lpn-done-circle"
                      type="checkbox"
                      checked={done}
                      onChange={async (ev) => {
                        const checked = ev.target.checked;
                        if (!checked || done) return;
                        try {
                          const current = await fetchToolkitEntryById(entryId);
                          const nextContent = (current?.LessonPlanContent ?? current?.lessonPlanContent ?? '').trim()
                            ? (current?.LessonPlanContent ?? current?.lessonPlanContent ?? '')
                            : '[DONE]';
                          await updateToolkitEntryContent(entryId, nextContent);
                        } catch (err) {
                          addError(`Mark Done failed for ${lpnLabel || entryId}: ${String(err?.message || err)}`);
                        }
                      }}
                    />
                    <span className="cb-lpn-done-text">{finalisedInSession ? 'Finalised' : (done ? 'Done' : 'NYD')}</span>
                  </label>
                </div>
              );
            })}
          </div>
          <div className="cb-lesson-select-wrap">
            <label className="cb-control-field cb-control-field-compact">Lesson Plan Lookup
              <select value={selectedLessonOptionValue} onChange={(e) => handleLessonSelection(e.target.value)} className="mainpage-input">
                <option value="">Select…</option>
                {lessonOptions.map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </select>
            </label>
          </div>
        </div>
      )}

      <div className="cb-params-panel">
        <div className="cb-section-title">Params</div>
        <div className="cb-muted">
          qualificationId={activeWorkflowQualificationId} • subjectId={selectedSubjectId} • topicId={selectedTopicId} • criteriaId={selectedCriteriaId}
        </div>
        <div className="cb-muted cb-help-text">
          Qualification: {selectedQualificationName} • Subject: {selectedSubjectName} • Topic: {selectedTopicName} • Criteria: {selectedCriteriaName}
        </div>
        <div className="cb-muted cb-help-text" style={codeSanity.ok ? undefined : { color: '#b00020' }}>
          Code Sanity Check: {codeSanity.ok ? 'OK' : codeSanity.warnings.join(' | ')}
        </div>
        <div className="cb-muted cb-help-text">
          Next workflow: Lesson Plan Review -&gt; Learning Material Dashboard -&gt; Learner Guide / Workbook exports.
        </div>
        <div className="cb-export-panel">
          <div className="cb-section-title cb-section-title-compact">Content Exporting</div>
          <div className="cb-muted cb-help-text">
            Confirms mapped lesson content for the selected subject and prepares export data only. No file download is triggered from this action.
          </div>
          <div className="cb-export-options">
            <label className="cb-inline-check">
              <input
                type="checkbox"
                checked={exportUseParaphrase}
                onChange={(e) => setExportUseParaphrase(e.target.checked)}
                disabled={exportingGuide}
              />
              <span className="cb-inline-check-text">Use Paraphrase</span>
            </label>
            <label className="cb-inline-check">
              <input
                type="checkbox"
                checked={exportForce}
                onChange={(e) => setExportForce(e.target.checked)}
                disabled={exportingGuide}
              />
              <span className="cb-inline-check-text">Force Refresh</span>
            </label>
          </div>
          <div className="cb-button-row">
            <button
              className="next-step-button"
              type="button"
              onClick={runContentExporting}
              disabled={exportingGuide || !activeWorkflowQualificationId || !selectedSubjectId}
            >
              {exportingGuide ? 'Preparing Export Data...' : 'Run Content Exporting'}
            </button>
          </div>
          {exportProgress > 0 ? (
            <div className="cb-progress-track">
              <div className="cb-progress-fill" style={{ width: `${Math.max(0, Math.min(100, exportProgress))}%` }} />
            </div>
          ) : null}
          {exportSummary ? (
            <div className="cb-export-summary">
              <div>
                Readiness: <strong>{exportSummary?.ready ? 'Ready' : 'Not Ready'}</strong> • Coverage: {Number(exportSummary?.criteriaCoveragePercent || 0)}%
              </div>
              <div>
                Toolkit Rows (subject): {Number(exportSummary?.toolkitRowsForSubject || 0)} • With Lesson Content: {Number(exportSummary?.toolkitRowsWithLessonContent || 0)}
              </div>
              <div>
                Criteria: {Number(exportSummary?.criteria || 0)} • With Content: {Number(exportSummary?.criteriaWithAnyContent || 0)} • Missing: {Number(exportSummary?.missingCriteriaCount || 0)}
              </div>
            </div>
          ) : null}
          {exportStatus ? <div className="cb-status is-success">{exportStatus}</div> : null}
          {exportError ? <div className="cb-status is-error">{exportError}</div> : null}
        </div>
        <div className="cb-button-row">
          <button type="button" onClick={() => navigate('/lecturer-toolkit', { state: workflowState })}>Back</button>
          <button type="button" onClick={() => navigate('/library', { state: workflowState })}>Back to Library Manager</button>
          <button type="button" onClick={() => navigate('/lesson-plan-review', { state: workflowState })}>Open Lesson Plan Review</button>
          <button className="next-step-button" type="button" onClick={() => navigate('/learning-material', { state: workflowState })}>Open Learning Material Dashboard</button>
          <button className="next-step-button" type="button" onClick={() => navigate('/learner-guide-export', { state: workflowState })}>Learner Guide Export</button>
          <button className="next-step-button" type="button" onClick={() => navigate('/graphs')}>Go to Graphs</button>
        </div>
      </div>

      {previewModalOpen && (
        <div className="cb-modal-overlay" onClick={() => !previewSaving && setPreviewModalOpen(false)}>
          <div className="cb-modal" onClick={(e) => e.stopPropagation()}>
            <div className="cb-modal-title">Context Preview</div>
            <div className="cb-modal-meta">
              {previewMeta?.selectedTitle ? `Source: ${previewMeta.selectedTitle}` : 'Source: not provided'}
              {previewMeta?.selectionBackend ? ` | Selector: ${previewMeta.selectionBackend}` : ''}
            </div>
            <textarea
              className="mainpage-input cb-modal-textarea"
              value={previewContext}
              onChange={(e) => setPreviewContext(e.target.value)}
              rows={16}
            />
            <div className="cb-button-row">
              <button type="button" onClick={() => setPreviewModalOpen(false)} disabled={previewSaving}>Cancel</button>
              <button type="button" onClick={confirmModeratorInsert} disabled={previewSaving || !String(previewContext || '').trim()}>
                {previewSaving ? 'Saving...' : 'Confirm Insert'}
              </button>
            </div>
          </div>
        </div>
      )}

      {finalizeModalOpen && (
        <div className="cb-modal-overlay" onClick={() => !finalizeSaving && setFinalizeModalOpen(false)}>
          <div className="cb-modal cb-modal-max" onClick={(e) => e.stopPropagation()}>
            <div className="cb-modal-title">{lpnLabelForEntryId(finalizeTargetEntryId || activeToolkitTargetId || 0)} - Finalise Lesson Plan</div>
            <div className="cb-modal-meta">Review full content, make edits, then confirm finalised to move automatically to the next LPN.</div>
            <textarea
              className="mainpage-input cb-modal-textarea cb-modal-textarea-max"
              value={finalizeText}
              onChange={(e) => setFinalizeText(e.target.value)}
            />
            <div className="cb-button-row">
              <button type="button" onClick={() => setFinalizeModalOpen(false)} disabled={finalizeSaving}>Cancel</button>
              <button
                type="button"
                onClick={confirmFinalizeCurrentLpn}
                disabled={finalizeSaving || !String(finalizeText || '').trim()}
              >
                {finalizeSaving ? 'Finalising...' : 'Confirm Finalised + Move to Next LPN'}
              </button>
            </div>
          </div>
        </div>
      )}

      {status && <div className={`cb-status ${statusIsSuccess ? 'is-success' : 'is-error'}`}>{status}</div>}
    </div>
  );
}
