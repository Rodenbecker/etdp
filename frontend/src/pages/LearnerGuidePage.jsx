import React, { useEffect, useMemo, useState } from 'react';
import { useQualification } from '../context/QualificationContext';
import DocxPreviewModal from '../components/DocxPreviewModal';
import { fetchDocxPreview } from '../utils/docxPreview';
import { downloadBlobResponse } from '../utils/learningMaterialCommon';

const API = '/api';
const REVIEW_PAGE_SIZE = 140;
const ALL_SUBJECTS_VALUE = '__all__';

const safeText = (v) => (v == null ? '' : String(v));
const subjectIdOf = (s) => String(s?.id ?? s?.Id ?? '');
const subjectLabel = (s) => {
  const code = safeText(s?.subjectCode ?? s?.SubjectCode ?? s?.phasesCode ?? s?.PhasesCode).trim();
  const description = safeText(s?.subjectDescription ?? s?.SubjectDescription).trim();
  if (code && description) return `${code} - ${description}`;
  return code || description || 'Unnamed Subject';
};

const LearnerGuidePage = () => {
  const { qualificationId } = useQualification() || { qualificationId: null };
  const qid = Number(qualificationId || localStorage.getItem('qualificationId') || 0);

  const [useParaphrase, setUseParaphrase] = useState(true);
  const [useWorkflowCache, setUseWorkflowCache] = useState(true);
  const [includeIllustrations, setIncludeIllustrations] = useState(true);
  const [generateIllustrations, setGenerateIllustrations] = useState(false);
  const [maxIllustrationsPerTopic, setMaxIllustrationsPerTopic] = useState(2);
  const [ttsModel, setTtsModel] = useState('');
  const [ttsVoice, setTtsVoice] = useState('');
  const [ttsFormat, setTtsFormat] = useState('mp3');
  const [ttsSpeed, setTtsSpeed] = useState(1);
  const [downloadingAudio, setDownloadingAudio] = useState(false);
  const [downloadingGuide, setDownloadingGuide] = useState(false);
  const [savingGuide, setSavingGuide] = useState(false);
  const [reviewRows, setReviewRows] = useState([]);
  const [visibleCount, setVisibleCount] = useState(20);
  const [filterText, setFilterText] = useState('');
  const [loadingReview, setLoadingReview] = useState(false);
  const [runningWorkflow, setRunningWorkflow] = useState(false);
  const [savingReview, setSavingReview] = useState(false);
  const [status, setStatus] = useState('');
  const [error, setError] = useState('');
  const [subjects, setSubjects] = useState([]);
  const [selectedSubjectId, setSelectedSubjectId] = useState(ALL_SUBJECTS_VALUE);
  const [checkingReadiness, setCheckingReadiness] = useState(false);
  const [readiness, setReadiness] = useState(null);
  const [previewOpen, setPreviewOpen] = useState(false);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [previewError, setPreviewError] = useState('');
  const [previewHtml, setPreviewHtml] = useState('');
  const [previewWarnings, setPreviewWarnings] = useState([]);
  const [previewZoom, setPreviewZoom] = useState(1);
  const [previewTitle, setPreviewTitle] = useState('Learner Guide Preview');
  const [previewFileName, setPreviewFileName] = useState('LearnerGuide.docx');
  const [synthModel, setSynthModel] = useState('gemma4:26b');
  const [synthScope, setSynthScope] = useState('subject');
  const [synthForceRefresh, setSynthForceRefresh] = useState(false);
  const [synthBusy, setSynthBusy] = useState(false);
  const [synthResult, setSynthResult] = useState(null);
  const [synthStatusInfo, setSynthStatusInfo] = useState(null);
  const [loadingSynthStatus, setLoadingSynthStatus] = useState(false);

  useEffect(() => {
    let active = true;
    const loadSubjects = async () => {
      if (qid <= 0) {
        if (!active) return;
        setSubjects([]);
        setSelectedSubjectId('');
        setReadiness(null);
        return;
      }
      try {
        const res = await fetch(`${API}/Subject/byQualification?qualificationId=${qid}`);
        if (!res.ok) throw new Error(await res.text());
        const data = await res.json();
        const list = Array.isArray(data) ? data : [];
        if (!active) return;
        setSubjects(list);
        setSelectedSubjectId((prev) => {
          if (prev === ALL_SUBJECTS_VALUE) return ALL_SUBJECTS_VALUE;
          if (prev && list.some((s) => subjectIdOf(s) === String(prev))) return String(prev);
          return list.length > 0 ? ALL_SUBJECTS_VALUE : '';
        });
      } catch {
        if (!active) return;
        setSubjects([]);
        setSelectedSubjectId('');
        setReadiness(null);
      }
    };

    loadSubjects();
    return () => {
      active = false;
    };
  }, [qid]);

  useEffect(() => {
    let active = true;
    const loadTtsDefaults = async () => {
      try {
        const res = await fetch(`${API}/TextToVideo/tts-options`);
        if (!res.ok) return;
        const data = await res.json();
        if (!active) return;
        const defaults = data?.defaults || {};
        setTtsModel(String(defaults?.geminiModel || defaults?.openAiModel || 'gemini-3.8-flash-tts'));
        setTtsVoice(String(defaults?.geminiVoice || defaults?.openAiVoice || 'Puck'));
        setTtsFormat(String(defaults?.format || 'mp3'));
        setTtsSpeed(Number(defaults?.speed || 1));
      } catch {
        if (!active) return;
        setTtsModel('gemini-3.8-flash-tts');
        setTtsVoice('Puck');
        setTtsFormat('mp3');
        setTtsSpeed(1);
      }
    };

    loadTtsDefaults();
    return () => {
      active = false;
    };
  }, []);

  const filteredRows = useMemo(() => {
    const needle = filterText.trim().toLowerCase();
    if (!needle) return reviewRows;
    return reviewRows.filter((row) => {
      const src = safeText(row.sourceText).toLowerCase();
      const para = safeText(row.paraphrasedText).toLowerCase();
      return src.includes(needle) || para.includes(needle);
    });
  }, [filterText, reviewRows]);

  const visibleRows = useMemo(
    () => filteredRows.slice(0, Math.max(1, visibleCount)),
    [filteredRows, visibleCount]
  );

  const dirtyCount = useMemo(
    () => reviewRows.reduce((sum, row) => (row.isDirty ? sum + 1 : sum), 0),
    [reviewRows]
  );

  const isAllSubjectsSelected = selectedSubjectId === ALL_SUBJECTS_VALUE;

  const setRowParaphrase = (indexInVisible, value) => {
    const row = visibleRows[indexInVisible];
    if (!row) return;
    setReviewRows((prev) =>
      prev.map((item, idx) => {
        if (idx !== row._index) return item;
        const nextText = safeText(value);
        return {
          ...item,
          paraphrasedText: nextText,
          isDirty: nextText.trim() !== safeText(item.originalParaphrasedText).trim()
        };
      })
    );
  };

  const loadReview = async () => {
    setLoadingReview(true);
    setError('');
    setStatus('');
    try {
      const params = new URLSearchParams();
      if (qid > 0) params.set('qualificationId', String(qid));
      params.set('take', String(REVIEW_PAGE_SIZE));
      const res = await fetch(`${API}/LearnerGuide/paraphrase-review?${params.toString()}`);
      if (!res.ok) throw new Error(await res.text());
      const data = await res.json();
      const entries = Array.isArray(data?.entries) ? data.entries : [];
      const rows = entries.map((entry, idx) => ({
        _index: idx,
        sourceText: safeText(entry?.sourceText),
        paraphrasedText: safeText(entry?.paraphrasedText),
        originalParaphrasedText: safeText(entry?.paraphrasedText),
        backend: safeText(entry?.backend || 'unknown'),
        updatedAtUtc: safeText(entry?.updatedAtUtc || ''),
        isDirty: false
      }));
      setReviewRows(rows);
      setVisibleCount(20);
      setStatus(`Loaded ${rows.length} cached paraphrase rows.`);
    } catch (e) {
      setError(`Failed to load paraphrase review data: ${e?.message || e}`);
    } finally {
      setLoadingReview(false);
    }
  };

  const runWorkflow = async () => {
    setRunningWorkflow(true);
    setError('');
    setStatus('');
    try {
      const payload = {
        qualificationId: qid > 0 ? qid : null,
        style: 'educational',
        preserveTerminology: true,
        forceRefresh: false
      };
      const res = await fetch(`${API}/LearnerGuide/paraphrase-workflow`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      if (!res.ok) throw new Error(await res.text());
      const data = await res.json();
      setStatus(
        `Workflow completed. candidates=${data?.totalCandidates ?? 0}, created=${data?.created ?? 0}, refreshed=${data?.refreshed ?? 0}, reused=${data?.reused ?? 0}, failed=${data?.failed ?? 0}`
      );
      await loadReview();
    } catch (e) {
      setError(`Paraphrase workflow failed: ${e?.message || e}`);
    } finally {
      setRunningWorkflow(false);
    }
  };

  const saveReview = async () => {
    const dirtyRows = reviewRows.filter((x) => x.isDirty && x.sourceText.trim() && x.paraphrasedText.trim());
    if (dirtyRows.length === 0) {
      setStatus('No edited rows to save.');
      return;
    }
    setSavingReview(true);
    setError('');
    setStatus('');
    try {
      const payload = {
        qualificationId: qid > 0 ? qid : null,
        entries: dirtyRows.map((row) => ({
          sourceText: row.sourceText,
          paraphrasedText: row.paraphrasedText
        }))
      };
      const res = await fetch(`${API}/LearnerGuide/paraphrase-review/save`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      if (!res.ok) throw new Error(await res.text());
      const data = await res.json();
      setStatus(`Saved ${data?.saved ?? 0} manual edits to paraphrase cache.`);
      setReviewRows((prev) =>
        prev.map((row) => ({
          ...row,
          originalParaphrasedText: row.paraphrasedText,
          isDirty: false,
          backend: row.isDirty ? 'manual_review' : row.backend
        }))
      );
    } catch (e) {
      setError(`Failed to save paraphrase edits: ${e?.message || e}`);
    } finally {
      setSavingReview(false);
    }
  };

  const buildLearnerGuideDownloadUrl = () => {
    if (!selectedSubjectId) {
      setError('No subject scope is available for learner guide export.');
      return '';
    }
    const params = new URLSearchParams();
    if (qid > 0) params.set('qualificationId', String(qid));
    if (!isAllSubjectsSelected) {
      params.set('subjectId', String(selectedSubjectId));
    }
    params.set('paraphrase', useParaphrase ? 'true' : 'false');
    params.set('useWorkflowCache', useParaphrase && useWorkflowCache ? 'true' : 'false');
    params.set('includeIllustrations', includeIllustrations ? 'true' : 'false');
    params.set('generateIllustrations', includeIllustrations && generateIllustrations ? 'true' : 'false');
    params.set('maxIllustrationsPerTopic', String(Math.max(1, Number(maxIllustrationsPerTopic || 1))));
    return `${API}/LearnerGuide/download?${params.toString()}`;
  };

  const buildLearnerGuideSaveUrl = () => {
    const downloadUrl = buildLearnerGuideDownloadUrl();
    if (!downloadUrl) return '';
    return downloadUrl.replace('/download?', '/save-to-workspace?');
  };

  const handleSaveLearnerGuide = async () => {
    const url = buildLearnerGuideSaveUrl();
    if (!url) return;
    setSavingGuide(true);
    setError('');
    setStatus('');
    try {
      const response = await fetch(url, { cache: 'no-store' });
      if (!response.ok) {
        throw new Error(await response.text().catch(() => `Save failed (${response.status})`));
      }

      const data = await response.json();
      setStatus(`Saved ${data?.fileName || 'Learner Guide DOCX'} to ${data?.savedPath || data?.folderPath || 'the qualification workspace folder'}.`);
    } catch (e) {
      setError(`Learner guide DOCX save failed: ${e?.message || e}`);
    } finally {
      setSavingGuide(false);
    }
  };

  const handleDownloadLearnerGuide = async () => {
    const url = buildLearnerGuideDownloadUrl();
    if (!url) return;
    setDownloadingGuide(true);
    setError('');
    setStatus('');
    try {
      const response = await fetch(url, { cache: 'no-store' });
      if (!response.ok) {
        throw new Error(await response.text().catch(() => `Download failed (${response.status})`));
      }

      await downloadBlobResponse(
        response,
        isAllSubjectsSelected ? 'LearnerGuide_Full.docx' : 'LearnerGuide.docx'
      );
      setStatus(
        isAllSubjectsSelected
          ? 'Learner guide DOCX download started for the full qualification.'
          : 'Learner guide DOCX download started.'
      );
    } catch (e) {
      setError(`Learner guide DOCX download failed: ${e?.message || e}`);
    } finally {
      setDownloadingGuide(false);
    }
  };

  const buildLearnerGuideAudioUrl = () => {
    if (!selectedSubjectId || isAllSubjectsSelected) {
      setError('Select a single subject before downloading learner guide audio.');
      return '';
    }
    const params = new URLSearchParams();
    if (qid > 0) params.set('qualificationId', String(qid));
    params.set('subjectId', String(selectedSubjectId));
    params.set('paraphrase', useParaphrase ? 'true' : 'false');
    params.set('model', String(ttsModel || 'gemini-3.8-flash-tts'));
    params.set('voice', String(ttsVoice || 'alloy'));
    params.set('format', String(ttsFormat || 'mp3'));
    params.set('speed', String(Math.max(0.25, Math.min(2, Number(ttsSpeed || 1)))));
    return `${API}/LearnerGuide/download-audio?${params.toString()}`;
  };

  const handleDownloadLearnerGuideAudio = async () => {
    const url = buildLearnerGuideAudioUrl();
    if (!url) return;
    setDownloadingAudio(true);
    setError('');
    setStatus('');
    try {
      window.open(url, '_blank');
      setStatus('Learner guide audio export started. Download will open in a new tab when ready.');
    } catch (e) {
      setError(`Failed to start learner guide audio export: ${e?.message || e}`);
    } finally {
      setDownloadingAudio(false);
    }
  };

  const loadSynthesisStatus = async () => {
    if (qid <= 0) return;
    setLoadingSynthStatus(true);
    try {
      const p = new URLSearchParams();
      p.set('qualificationId', String(qid));
      if (selectedSubjectId && selectedSubjectId !== ALL_SUBJECTS_VALUE) {
        p.set('subjectId', String(selectedSubjectId));
      }
      const res = await fetch(`${API}/LearnerGuide/synthesis-status?${p.toString()}`);
      if (res.ok) {
        const data = await res.json();
        setSynthStatusInfo(data);
      }
    } catch {
    } finally {
      setLoadingSynthStatus(false);
    }
  };

  useEffect(() => {
    loadSynthesisStatus();
  }, [qid, selectedSubjectId]);

  const handleSynthesizeTextbook = async () => {
    if (qid <= 0) {
      setError('Please select a valid qualification first.');
      return;
    }
    setSynthBusy(true);
    setError('');
    setStatus(`Synthesizing textbook chapters with ${synthModel}... This delineates curriculum topics and assessment criteria strictly in QCTO national standard layout.`);
    setSynthResult(null);

    try {
      const payload = {
        qualificationId: qid,
        subjectId: (synthScope === 'subject' && selectedSubjectId !== ALL_SUBJECTS_VALUE) ? Number(selectedSubjectId) : null,
        model: synthModel,
        forceRefresh: synthForceRefresh,
        maxTopics: 60
      };

      const res = await fetch(`${API}/LearnerGuide/synthesize-textbook`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });

      if (!res.ok) {
        const errText = await res.text();
        throw new Error(errText || 'Textbook synthesis request failed');
      }

      const data = await res.json();
      setSynthResult(data);
      setStatus(`✨ Textbook synthesis complete! Processed ${data?.topicsProcessed ?? 0} topics (${data?.wordsGenerated ?? 0} words) using ${data?.modelUsed || synthModel}.`);
      await loadSynthesisStatus();
      await loadReview();
    } catch (e) {
      setError(`Textbook synthesis failed: ${e?.message || e}`);
    } finally {
      setSynthBusy(false);
    }
  };

  const handlePreviewLearnerGuide = async () => {
    const url = buildLearnerGuideDownloadUrl();
    if (!url) return;

    setPreviewOpen(true);
    setPreviewLoading(true);
    setPreviewError('');
    setPreviewHtml('');
    setPreviewWarnings([]);
    setPreviewZoom(1);
    setPreviewTitle(isAllSubjectsSelected ? 'Learner Guide Preview - Full Qualification' : 'Learner Guide Preview');
    setPreviewFileName(isAllSubjectsSelected ? 'LearnerGuide_Full.docx' : 'LearnerGuide.docx');

    try {
      const preview = await fetchDocxPreview(url, 'LearnerGuide.docx');
      setPreviewHtml(preview?.html || '');
      setPreviewWarnings(Array.isArray(preview?.warnings) ? preview.warnings : []);
      if (preview?.fileName) {
        setPreviewFileName(preview.fileName);
        setPreviewTitle(`Learner Guide Preview - ${preview.fileName}`);
      }
    } catch (e) {
      setPreviewError(`Failed to generate preview: ${e?.message || e}`);
    } finally {
      setPreviewLoading(false);
    }
  };

  const checkReadiness = async () => {
    if (!selectedSubjectId || isAllSubjectsSelected) {
      setError('Select a single subject before checking learner guide export readiness.');
      return;
    }

    setCheckingReadiness(true);
    setError('');
    setStatus('');
    try {
      const params = new URLSearchParams();
      if (qid > 0) params.set('qualificationId', String(qid));
      params.set('subjectId', String(selectedSubjectId));
      params.set('details', 'true');

      const res = await fetch(`${API}/LearnerGuide/export-readiness?${params.toString()}`);
      const text = await res.text().catch(() => '');
      let json = null;
      try { json = text ? JSON.parse(text) : null; } catch {}
      if (!res.ok) {
        throw new Error((json && (json.error || json.message)) || text || `Readiness check failed (${res.status})`);
      }

      setReadiness(json || {});
      setStatus(
        `Readiness checked: ${(json && json.ready) ? 'READY' : 'NOT READY'} · ` +
        `Coverage ${Number((json && json.criteriaCoveragePercent) || 0)}%`
      );
    } catch (e) {
      setReadiness(null);
      setError(`Failed to check learner guide export readiness: ${e?.message || e}`);
    } finally {
      setCheckingReadiness(false);
    }
  };

  return (
    <div className="mainpage-root">
      <h2 className="mainpage-title">Learner Guide Export</h2>
      <p style={{ marginTop: 4, color: '#4b6075' }}>
        Export follows strict flow order: Phase Sequence - Subject Code - Topic Order - Assessment Criteria - LPN.
      </p>
      <p style={{ marginTop: 4, color: '#4b6075' }}>
        Full qualification export is the default scope. Pick a single subject only when you need subject-specific readiness, preview, or audio.
      </p>

      <div style={{ marginBottom: 10, color: '#3d566e' }}>
        <strong>Qualification Id:</strong> {qid > 0 ? qid : 'Not selected (backend default will be used)'}
      </div>

      <div style={{ marginBottom: 12 }}>
        <label>
          <strong>Guide Scope:</strong>
          <select
            className="mainpage-input"
            style={{ marginTop: 6, maxWidth: 640 }}
            value={selectedSubjectId}
            onChange={(e) => setSelectedSubjectId(e.target.value)}
            disabled={subjects.length === 0}
          >
            {subjects.length === 0 ? <option value="">No subjects found</option> : null}
            {subjects.length > 0 ? (
              <option value={ALL_SUBJECTS_VALUE}>All subjects in qualification</option>
            ) : null}
            {subjects.map((s) => (
              <option key={subjectIdOf(s)} value={subjectIdOf(s)}>
                {subjectLabel(s)}
              </option>
            ))}
          </select>
        </label>
      </div>

      {/* --- QCTO TVET Textbook Synthesis Card (Gemma 4:26B & Google Gemini) --- */}
      <section style={{
        margin: '16px 0 24px',
        padding: '18px 20px',
        background: 'linear-gradient(135deg, #0b1a2e 0%, #102a45 100%)',
        border: '1px solid #1e3a5f',
        borderRadius: 10,
        color: '#e2e8f0',
        boxShadow: '0 4px 14px rgba(0,0,0,0.25)'
      }}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap', gap: 10 }}>
          <div>
            <h3 style={{ margin: 0, fontSize: '1.2rem', color: '#38bdf8', display: 'flex', alignItems: 'center', gap: 8 }}>
              <span>✨</span> QCTO TVET Textbook Synthesis Engine
            </h3>
            <p style={{ margin: '4px 0 0', fontSize: '0.88rem', color: '#94a3b8' }}>
              Delineates national curriculum specifications & assessment criteria into structured TVET textbook chapters using Gemma 4:26B & Google Gemini.
            </p>
          </div>
          {synthStatusInfo ? (
            <div style={{ display: 'flex', gap: 12, background: 'rgba(255,255,255,0.06)', padding: '6px 14px', borderRadius: 6, fontSize: '0.82rem' }}>
              <div><strong style={{ color: '#38bdf8' }}>{synthStatusInfo.synthesizedTopicsCount}</strong>/{synthStatusInfo.totalTopics} Topics</div>
              <div><strong style={{ color: '#4ade80' }}>{synthStatusInfo.coveragePercent}%</strong> Coverage</div>
              <div><strong style={{ color: '#facc15' }}>{synthStatusInfo.totalWordCount?.toLocaleString()}</strong> Words</div>
            </div>
          ) : null}
        </div>

        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))', gap: 12, marginTop: 14 }}>
          <label style={{ display: 'flex', flexDirection: 'column', gap: 4, fontSize: '0.85rem' }}>
            <span><strong>AI Model Engine:</strong></span>
            <select
              className="mainpage-input"
              style={{ background: '#0a1626', color: '#fff', borderColor: '#334155' }}
              value={synthModel}
              onChange={(e) => setSynthModel(e.target.value)}
            >
              <option value="gemma4:26b">Gemma 4:26B (Local / Ollama) [Recommended]</option>
              <option value="gemini-3.8-flash">Google Gemini 3.8 Flash (Cloud)</option>
              <option value="auto">Auto (Gemma 4:26B Local with Gemini Fallback)</option>
            </select>
          </label>

          <label style={{ display: 'flex', flexDirection: 'column', gap: 4, fontSize: '0.85rem' }}>
            <span><strong>Synthesis Scope:</strong></span>
            <select
              className="mainpage-input"
              style={{ background: '#0a1626', color: '#fff', borderColor: '#334155' }}
              value={synthScope}
              onChange={(e) => setSynthScope(e.target.value)}
            >
              <option value="subject">Currently Selected Subject</option>
              <option value="qualification">Entire Qualification (All Subjects)</option>
            </select>
          </label>

          <div style={{ display: 'flex', alignItems: 'center', marginTop: 18 }}>
            <label style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: '0.85rem', cursor: 'pointer' }}>
              <input
                type="checkbox"
                checked={synthForceRefresh}
                onChange={(e) => setSynthForceRefresh(e.target.checked)}
              />
              <span>Force Regenerate (Overwrite old fragments)</span>
            </label>
          </div>
        </div>

        <div style={{ marginTop: 14, display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
          <button
            type="button"
            onClick={handleSynthesizeTextbook}
            disabled={synthBusy || qid <= 0}
            style={{
              background: synthBusy ? '#475569' : '#0284c7',
              color: '#fff',
              border: 'none',
              padding: '10px 18px',
              borderRadius: 6,
              fontWeight: 600,
              fontSize: '0.92rem',
              cursor: synthBusy ? 'not-allowed' : 'pointer',
              display: 'flex',
              alignItems: 'center',
              gap: 8,
              boxShadow: '0 2px 8px rgba(2, 132, 199, 0.4)'
            }}
          >
            {synthBusy ? '🔄 Synthesizing TVET Textbook with AI...' : '✨ Synthesize Full TVET Textbook'}
          </button>

          <button
            type="button"
            onClick={loadSynthesisStatus}
            disabled={loadingSynthStatus || qid <= 0}
            style={{
              background: 'transparent',
              color: '#94a3b8',
              border: '1px solid #334155',
              padding: '8px 14px',
              borderRadius: 6,
              fontSize: '0.85rem',
              cursor: 'pointer'
            }}
          >
            {loadingSynthStatus ? 'Checking...' : 'Refresh Status'}
          </button>

          <span style={{ fontSize: '0.82rem', color: '#94a3b8' }}>
            Follows national standard: Subjects &rarr; Modules &rarr; Topics &rarr; Assessment Criteria &rarr; OHSA Safety &rarr; Self-Checks.
          </span>
        </div>

        {synthResult ? (
          <div style={{ marginTop: 14, background: 'rgba(0,0,0,0.3)', border: '1px solid #1e293b', borderRadius: 6, padding: 12, fontSize: '0.85rem' }}>
            <div style={{ fontWeight: 600, color: '#38bdf8', marginBottom: 6 }}>
              {synthResult.message}
            </div>
            <div style={{ color: '#94a3b8', fontSize: '0.8rem' }}>
              Qualification: {synthResult.qualificationTitle} · Model: {synthResult.modelUsed} · Words: {synthResult.wordsGenerated?.toLocaleString()}
            </div>
            {Array.isArray(synthResult.details) && synthResult.details.length > 0 ? (
              <details style={{ marginTop: 8 }}>
                <summary style={{ cursor: 'pointer', color: '#cbd5e1' }}>
                  View Synthesized Topics List ({synthResult.details.length})
                </summary>
                <div style={{ maxHeight: 200, overflowY: 'auto', marginTop: 8, paddingRight: 6 }}>
                  {synthResult.details.map((item, idx) => (
                    <div key={idx} style={{ padding: '4px 0', borderBottom: '1px solid #1e293b', display: 'flex', justifyContent: 'space-between' }}>
                      <span><strong>{item.topicCode || item.subjectCode}</strong>: {item.topicTitle}</span>
                      <span style={{ color: item.wordCount >= 200 ? '#4ade80' : '#facc15' }}>
                        {item.wordCount} words ({item.model})
                      </span>
                    </div>
                  ))}
                </div>
              </details>
            ) : null}
          </div>
        ) : null}
      </section>

      <label style={{ display: 'block', marginBottom: 10 }}>
        <input
          type="checkbox"
          checked={useParaphrase}
          onChange={(e) => setUseParaphrase(e.target.checked)}
          style={{ marginRight: 8 }}
        />
        Enable anti-plagiarism paraphrasing for lesson text
      </label>

      <label style={{ display: 'block', marginBottom: 14 }}>
        <input
          type="checkbox"
          checked={useWorkflowCache}
          disabled={!useParaphrase}
          onChange={(e) => setUseWorkflowCache(e.target.checked)}
          style={{ marginRight: 8 }}
        />
        Use reviewed paraphrase cache during export
      </label>

      <label style={{ display: 'block', marginBottom: 10 }}>
        <input
          type="checkbox"
          checked={includeIllustrations}
          onChange={(e) => setIncludeIllustrations(e.target.checked)}
          style={{ marginRight: 8 }}
        />
        Include topic illustrations in learner guide export
      </label>

      <label style={{ display: 'block', marginBottom: 10 }}>
        <input
          type="checkbox"
          checked={generateIllustrations}
          disabled={!includeIllustrations}
          onChange={(e) => setGenerateIllustrations(e.target.checked)}
          style={{ marginRight: 8 }}
        />
        Generate missing illustrations with AI
      </label>

      <div style={{ marginBottom: 14, display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))', gap: 10 }}>
        <label>
          Max Illustrations per Topic
          <input
            className="mainpage-input"
            type="number"
            min={1}
            max={4}
            value={maxIllustrationsPerTopic}
            onChange={(e) => setMaxIllustrationsPerTopic(Number(e.target.value || 2))}
          />
        </label>
        <label>
          TTS Model
          <input
            className="mainpage-input"
            type="text"
            value={ttsModel}
            onChange={(e) => setTtsModel(e.target.value)}
          />
        </label>
        <label>
          TTS Voice
          <input
            className="mainpage-input"
            type="text"
            value={ttsVoice}
            onChange={(e) => setTtsVoice(e.target.value)}
          />
        </label>
        <label>
          TTS Format
          <select className="mainpage-input" value={ttsFormat} onChange={(e) => setTtsFormat(e.target.value)}>
            <option value="mp3">mp3</option>
            <option value="wav">wav</option>
            <option value="aac">aac</option>
            <option value="flac">flac</option>
            <option value="opus">opus</option>
          </select>
        </label>
        <label>
          TTS Speed
          <input
            className="mainpage-input"
            type="number"
            min={0.25}
            max={2}
            step={0.05}
            value={ttsSpeed}
            onChange={(e) => setTtsSpeed(Number(e.target.value || 1))}
          />
        </label>
      </div>

      <div style={{ marginBottom: 16, display: 'flex', gap: 10, flexWrap: 'wrap' }}>
        <button onClick={runWorkflow} disabled={runningWorkflow || !useParaphrase}>
          {runningWorkflow ? 'Running Workflow...' : '1) Run Paraphrase Workflow'}
        </button>
        <button onClick={loadReview} disabled={loadingReview || !useParaphrase}>
          {loadingReview ? 'Loading Review...' : '2) Load Review Rows'}
        </button>
        <button onClick={saveReview} disabled={savingReview || dirtyCount === 0 || !useParaphrase}>
          {savingReview ? 'Saving...' : `3) Save Manual Edits (${dirtyCount})`}
        </button>
        <button onClick={checkReadiness} disabled={checkingReadiness || !selectedSubjectId || isAllSubjectsSelected}>
          {checkingReadiness ? 'Checking Readiness...' : '4) Check Export Readiness'}
        </button>
        <button onClick={handlePreviewLearnerGuide} disabled={!selectedSubjectId}>
          5) Preview Learner Guide (On-screen)
        </button>
        <button onClick={handleSaveLearnerGuide} disabled={!selectedSubjectId || savingGuide}>
          {savingGuide ? 'Saving Learner Guide...' : '6) Save Learner Guide To Workspace'}
        </button>
        <button onClick={handleDownloadLearnerGuide} disabled={!selectedSubjectId || downloadingGuide}>
          {downloadingGuide ? 'Downloading Learner Guide...' : '7) Download Learner Guide (.docx)'}
        </button>
        <button onClick={handleDownloadLearnerGuideAudio} disabled={!selectedSubjectId || isAllSubjectsSelected || downloadingAudio}>
          {downloadingAudio ? 'Starting Audio Export...' : '8) Download Learner Guide Audio (.zip)'}
        </button>
      </div>

      {status ? <div style={{ color: '#0b7a0b', marginBottom: 8 }}>{status}</div> : null}
      {error ? <div style={{ color: '#b00020', marginBottom: 8 }}>{error}</div> : null}
      {readiness ? (
        <div style={{ marginBottom: 14, border: '1px solid #d8dfeb', borderRadius: 8, padding: 10, background: '#f8fbff' }}>
          <div><strong>Export Readiness:</strong> {readiness?.ready ? 'READY' : 'NOT READY'}</div>
          <div>
            Qualification {safeText(readiness?.qualificationNumber)} (id {safeText(readiness?.qualificationId)}) ·
            {' '}Subject {safeText(readiness?.subjectCode)} (id {safeText(readiness?.subjectId)})
          </div>
          <div>
            Criteria Coverage: {Number(readiness?.criteriaCoveragePercent || 0)}% ·
            {' '}Criteria With Content: {Number(readiness?.criteriaWithAnyContent || 0)}/{Number(readiness?.criteria || 0)}
          </div>
          <div>
            Toolkit Rows With Guide Source: {Number(readiness?.toolkitRowsWithLessonContent || 0)} ·
            {' '}Mapped: {Number(readiness?.mappedToolkitRowsWithLessonContent || 0)} ·
            {' '}Unmapped: {Number(readiness?.unmappedToolkitRowsWithLessonContent || 0)} ·
            {' '}Other Subject Codes: {Number(readiness?.toolkitRowsWithLessonContentOtherSubjects || 0)}
          </div>
          {Array.isArray(readiness?.unmappedToolkitRows) && readiness.unmappedToolkitRows.length > 0 ? (
            <div style={{ marginTop: 8 }}>
              <strong>Unmapped Rows (top {Math.min(10, readiness.unmappedToolkitRows.length)}):</strong>
              {readiness.unmappedToolkitRows.slice(0, 10).map((row) => (
                <div key={`unmapped-${safeText(row?.rowId)}`} style={{ fontSize: 13, marginTop: 4 }}>
                  Row {safeText(row?.rowId)} · {safeText(row?.lpn)} · criteria {safeText(row?.criteriaId || '-')} · {safeText(row?.reason)}
                </div>
              ))}
            </div>
          ) : null}
          {Array.isArray(readiness?.subjectMismatchToolkitRows) && readiness.subjectMismatchToolkitRows.length > 0 ? (
            <div style={{ marginTop: 8 }}>
              <strong>Subject Code Mismatch Rows (top {Math.min(10, readiness.subjectMismatchToolkitRows.length)}):</strong>
              {readiness.subjectMismatchToolkitRows.slice(0, 10).map((row) => (
                <div key={`mismatch-${safeText(row?.rowId)}`} style={{ fontSize: 13, marginTop: 4 }}>
                  Row {safeText(row?.rowId)} · {safeText(row?.lpn)} · row subject {safeText(row?.rowSubjectCode || '-')} · selected {safeText(row?.selectedSubjectCode || '-')}
                </div>
              ))}
            </div>
          ) : null}
        </div>
      ) : null}

      <DocxPreviewModal
        open={previewOpen}
        title={previewTitle}
        loading={previewLoading}
        error={previewError}
        html={previewHtml}
        warnings={previewWarnings}
        editableDocx
        editedDocxFileName={previewFileName}
        zoom={previewZoom}
        onZoomIn={() => setPreviewZoom((z) => Math.min(2.5, Number(z || 1) + 0.1))}
        onZoomOut={() => setPreviewZoom((z) => Math.max(0.5, Number(z || 1) - 0.1))}
        onZoomReset={() => setPreviewZoom(1)}
        onClose={() => setPreviewOpen(false)}
      />

      {useParaphrase ? (
        <div style={{ marginTop: 8 }}>
          <div style={{ marginBottom: 10, display: 'flex', gap: 10, flexWrap: 'wrap', alignItems: 'center' }}>
            <input
              className="mainpage-input"
              style={{ maxWidth: 360 }}
              type="text"
              placeholder="Filter source/paraphrase..."
              value={filterText}
              onChange={(e) => setFilterText(e.target.value)}
            />
            <span style={{ color: '#3d566e' }}>
              Rows: {filteredRows.length} visible / {reviewRows.length} loaded
            </span>
          </div>

          {visibleRows.map((row, idx) => (
            <div
              key={`${row._index}-${row.updatedAtUtc}`}
              style={{
                border: '1px solid #d8dfeb',
                borderRadius: 8,
                padding: 10,
                marginBottom: 10,
                background: '#fbfcff'
              }}
            >
              <div style={{ marginBottom: 8, color: '#3d566e', fontSize: 13 }}>
                <strong>Row {idx + 1}</strong> | backend: {row.backend || 'unknown'} | updated: {row.updatedAtUtc || '-'}{' '}
                {row.isDirty ? '| edited' : ''}
              </div>
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(2, minmax(260px, 1fr))', gap: 10 }}>
                <label>
                  Original
                  <textarea
                    className="mainpage-input"
                    value={row.sourceText}
                    readOnly
                    rows={5}
                    style={{ width: '100%', resize: 'vertical' }}
                  />
                </label>
                <label>
                  Paraphrased (editable)
                  <textarea
                    className="mainpage-input"
                    value={row.paraphrasedText}
                    rows={5}
                    onChange={(e) => setRowParaphrase(idx, e.target.value)}
                    style={{ width: '100%', resize: 'vertical' }}
                  />
                </label>
              </div>
            </div>
          ))}

          {filteredRows.length > visibleRows.length ? (
            <button onClick={() => setVisibleCount((n) => n + 20)}>
              Show More ({filteredRows.length - visibleRows.length} remaining)
            </button>
          ) : null}
        </div>
      ) : null}
    </div>
  );
};

export default LearnerGuidePage;
