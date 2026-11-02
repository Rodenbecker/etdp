import React, { useEffect, useMemo, useState } from 'react';
import { useQualification } from '../context/QualificationContext';

const qIdOf = (q) => Number(q?.id ?? q?.Id ?? 0);
const qNumberOf = (q) => String(q?.qualificationNumber ?? q?.QualificationNumber ?? '').trim();
const qDescriptionOf = (q) => String(q?.qualificationDescription ?? q?.QualificationDescription ?? '').trim();
const topicIdOf = (t) => Number(t?.id ?? t?.Id ?? 0);
const subjectIdOf = (t) => Number(t?.subjectId ?? t?.SubjectId ?? 0);
const subjectCodeOf = (t) => String(t?.subjectCode ?? t?.SubjectCode ?? '').trim();
const subjectDescriptionOf = (t) => String(t?.subjectDescription ?? t?.SubjectDescription ?? '').trim();
const topicCodeOf = (t) => String(t?.topicCode ?? t?.TopicCode ?? '').trim();
const topicDescriptionOf = (t) => String(t?.topicDescription ?? t?.TopicDescription ?? '').trim();

const defaultTopicParams = {
  titleOverride: '',
  bulletsPerSlide: 8,
  includeCoverSlide: true,
  includeVisualResourceSlides: false,
  maxVisualSlides: 3,
  includeGeneratedImageSlides: false,
  maxGeneratedImageSlides: 2,
  generatedImageModel: 'imagen-3.0-generate-002',
  generatedImageSize: '1024x1024',
  generatedImageStyle: ''
};

const clampBulletsPerSlide = (value) => Math.max(1, Math.min(20, Number(value || 8)));
const clampVisualSlideCount = (value) => Math.max(1, Math.min(8, Number(value || 3)));
const clampGeneratedSlideCount = (value) => Math.max(1, Math.min(4, Number(value || 2)));

const readErrorMessage = async (res) => {
  const raw = await res.text();
  if (!raw) return `Request failed (${res.status}).`;
  try {
    const parsed = JSON.parse(raw);
    if (typeof parsed === 'string') return parsed;
    return parsed?.message || parsed?.error || parsed?.title || raw;
  } catch {
    return raw;
  }
};

const normalizePreviewResponse = (payload) => {
  const rawSlides = Array.isArray(payload?.slides)
    ? payload.slides
    : Array.isArray(payload?.Slides)
      ? payload.Slides
      : [];
  const rawVisualResources = Array.isArray(payload?.visualResources)
    ? payload.visualResources
    : Array.isArray(payload?.VisualResources)
      ? payload.VisualResources
      : [];
  const rawWarnings = Array.isArray(payload?.warnings)
    ? payload.warnings
    : Array.isArray(payload?.Warnings)
      ? payload.Warnings
      : [];

  return {
    topicId: Number(payload?.topicId ?? payload?.TopicId ?? 0),
    topicCode: String(payload?.topicCode ?? payload?.TopicCode ?? '').trim(),
    title: String(payload?.title ?? payload?.Title ?? '').trim(),
    includeCoverSlide: Boolean(payload?.includeCoverSlide ?? payload?.IncludeCoverSlide),
    bulletsPerSlide: clampBulletsPerSlide(payload?.bulletsPerSlide ?? payload?.BulletsPerSlide ?? 8),
    visualResourcesMatched: Number(payload?.visualResourcesMatched ?? payload?.VisualResourcesMatched ?? rawVisualResources.length),
    localVisualResourcesMatched: Number(payload?.localVisualResourcesMatched ?? payload?.LocalVisualResourcesMatched ?? 0),
    generatedVisualResourcesMatched: Number(payload?.generatedVisualResourcesMatched ?? payload?.GeneratedVisualResourcesMatched ?? 0),
    visualResources: rawVisualResources.map((resource) => ({
      materialId: Number(resource?.materialId ?? resource?.MaterialId ?? 0),
      fileName: String(resource?.fileName ?? resource?.FileName ?? '').trim(),
      caption: String(resource?.caption ?? resource?.Caption ?? '').trim(),
      source: String(resource?.source ?? resource?.Source ?? 'local').trim().toLowerCase() || 'local'
    })),
    warnings: rawWarnings
      .map((w) => String(w || '').trim())
      .filter((w) => w.length > 0),
    slides: rawSlides.map((slide, index) => {
      const rawBullets = Array.isArray(slide?.bullets)
        ? slide.bullets
        : Array.isArray(slide?.Bullets)
          ? slide.Bullets
          : [];
      return {
        number: Number(slide?.number ?? slide?.Number ?? index + 1),
        type: String(slide?.type ?? slide?.Type ?? 'content').trim().toLowerCase(),
        title: String(slide?.title ?? slide?.Title ?? '').trim(),
        subtitle: String(slide?.subtitle ?? slide?.Subtitle ?? '').trim(),
        imageFileName: String(slide?.imageFileName ?? slide?.ImageFileName ?? '').trim(),
        imageSource: String(slide?.imageSource ?? slide?.ImageSource ?? 'local').trim().toLowerCase() || 'local',
        imageCaption: String(slide?.imageCaption ?? slide?.ImageCaption ?? '').trim(),
        bullets: rawBullets
          .map((line) => String(line ?? '').trim())
          .filter((line) => line.length > 0)
      };
    })
  };
};

const escapeHtml = (value) => String(value ?? '')
  .replace(/&/g, '&amp;')
  .replace(/</g, '&lt;')
  .replace(/>/g, '&gt;')
  .replace(/"/g, '&quot;')
  .replace(/'/g, '&#39;');

const buildSlidesPrintHtml = (preview) => {
  const title = escapeHtml(preview?.title || 'Slides');
  const topicCode = escapeHtml(preview?.topicCode || '');
  const slideHtml = (preview?.slides || []).map((slide) => {
    const bullets = (slide?.bullets || [])
      .map((bullet) => `<li>${escapeHtml(bullet).replace(/\n/g, '<br/>')}</li>`)
      .join('');
    const subtitleHtml = slide?.subtitle
      ? `<div class="slide-subtitle">${escapeHtml(slide.subtitle)}</div>`
      : '';
    return `
      <section class="slide">
        <header>
          <div class="slide-title">${escapeHtml(slide?.title || title)}</div>
          ${subtitleHtml}
        </header>
        ${slide?.type === 'visual'
      ? `<div class="empty-note">Visual Resource: ${escapeHtml(slide?.imageFileName || slide?.imageCaption || 'Image')}</div>${bullets ? `<ul>${bullets}</ul>` : ''}`
      : bullets ? `<ul>${bullets}</ul>` : '<div class="empty-note">Cover slide</div>'}
      </section>
    `;
  }).join('');

  return `<!doctype html>
<html>
<head>
  <meta charset="utf-8" />
  <title>${title}</title>
  <style>
    @page { size: A4 landscape; margin: 12mm; }
    body { margin: 0; font-family: Calibri, Arial, sans-serif; color: #0f1f3a; }
    .doc-header { margin: 0 0 8mm; border-bottom: 1px solid #d0d7e2; padding-bottom: 3mm; }
    .doc-title { font-size: 20pt; font-weight: 700; margin: 0; }
    .doc-subtitle { font-size: 12pt; margin: 1mm 0 0; color: #3d566e; }
    .slide { page-break-after: always; min-height: 170mm; box-sizing: border-box; border: 2px solid #dbe4ef; border-radius: 6px; padding: 8mm; }
    .slide:last-of-type { page-break-after: auto; }
    .slide-title { font-size: 24pt; font-weight: 700; margin-bottom: 3mm; }
    .slide-subtitle { font-size: 13pt; color: #3d566e; margin-bottom: 4mm; }
    ul { margin: 0; padding-left: 9mm; font-size: 14pt; line-height: 1.45; }
    li { margin-bottom: 2.2mm; }
    .empty-note { margin-top: 6mm; font-size: 13pt; color: #3d566e; }
  </style>
</head>
<body>
  <div class="doc-header">
    <h1 class="doc-title">${title}</h1>
    <p class="doc-subtitle">${topicCode}</p>
  </div>
  ${slideHtml}
  <script>window.onload = () => { window.focus(); window.print(); };</script>
</body>
</html>`;
};

const getDownloadName = (res, fallbackName) => {
  const disposition = String(res.headers.get('Content-Disposition') || '');
  const match = disposition.match(/filename\*?=(?:UTF-8''|\")?([^\";]+)/i);
  if (!match?.[1]) return fallbackName;
  return decodeURIComponent(match[1].replace(/\"/g, '').trim());
};

const downloadResponse = async (res, fallbackName) => {
  const blob = await res.blob();
  const url = window.URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = getDownloadName(res, fallbackName);
  document.body.appendChild(a);
  a.click();
  a.remove();
  window.URL.revokeObjectURL(url);
};

const PowerPointSlidesPage = () => {
  const { qualificationId, setQualificationId } = useQualification() || { qualificationId: null, setQualificationId: () => {} };
  const [qualifications, setQualifications] = useState([]);
  const [selectedQualificationId, setSelectedQualificationId] = useState(String(qualificationId || localStorage.getItem('qualificationId') || ''));
  const [topics, setTopics] = useState([]);
  const [loadingQualifications, setLoadingQualifications] = useState(false);
  const [loadingTopics, setLoadingTopics] = useState(false);
  const [searchText, setSearchText] = useState('');
  const [subjectFilter, setSubjectFilter] = useState('');
  const [subjectFromId, setSubjectFromId] = useState('');
  const [subjectToId, setSubjectToId] = useState('');
  const [expandedTopicId, setExpandedTopicId] = useState(null);
  const [paramsByTopic, setParamsByTopic] = useState({});
  const [busyTopicId, setBusyTopicId] = useState(null);
  const [previewBusyTopicId, setPreviewBusyTopicId] = useState(null);
  const [busyBatch, setBusyBatch] = useState(false);
  const [status, setStatus] = useState('');
  const [error, setError] = useState('');
  const [topicMessageById, setTopicMessageById] = useState({});
  const [previewOpen, setPreviewOpen] = useState(false);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [previewError, setPreviewError] = useState('');
  const [previewData, setPreviewData] = useState(null);
  const [previewTopic, setPreviewTopic] = useState(null);
  const [previewZoom, setPreviewZoom] = useState(1);

  useEffect(() => {
    const qid = Number(qualificationId || 0);
    if (!qid) return;
    if (String(qid) !== String(selectedQualificationId || '')) {
      setSelectedQualificationId(String(qid));
    }
  }, [qualificationId]);

  useEffect(() => {
    let active = true;
    const loadQualifications = async () => {
      setLoadingQualifications(true);
      setError('');
      try {
        const res = await fetch('/api/Qualification');
        if (!res.ok) throw new Error(await res.text());
        const data = await res.json();
        if (!active) return;
        const list = Array.isArray(data) ? data : [];
        setQualifications(list);

        if (!selectedQualificationId && list.length > 0) {
          const firstId = String(qIdOf(list[0]));
          setSelectedQualificationId(firstId);
          setQualificationId(Number(firstId));
        }
      } catch (e) {
        if (!active) return;
        setError(`Failed to load qualifications: ${String(e?.message || e)}`);
      } finally {
        if (active) setLoadingQualifications(false);
      }
    };

    loadQualifications();
    return () => { active = false; };
  }, []);

  useEffect(() => {
    let active = true;
    const qid = Number(selectedQualificationId || 0);
    if (!qid) {
      setTopics([]);
      return () => { active = false; };
    }

    setLoadingTopics(true);
    setError('');
    setStatus('');
    setTopicMessageById({});
    setQualificationId(qid);
    setSubjectFilter('');

    const loadTopics = async () => {
      try {
        const res = await fetch(`/api/Topic/byQualification?qualificationId=${qid}`);
        if (!res.ok) throw new Error(await res.text());
        const data = await res.json();
        if (!active) return;
        const list = Array.isArray(data) ? data : [];
        const sorted = [...list].sort((a, b) => {
          const s = subjectCodeOf(a).localeCompare(subjectCodeOf(b), undefined, { sensitivity: 'base', numeric: true });
          if (s !== 0) return s;
          const t = topicCodeOf(a).localeCompare(topicCodeOf(b), undefined, { sensitivity: 'base', numeric: true });
          if (t !== 0) return t;
          return topicDescriptionOf(a).localeCompare(topicDescriptionOf(b), undefined, { sensitivity: 'base' });
        });
        setTopics(sorted);
        const rangeOptions = [];
        const seenSubjects = new Set();
        for (const topic of sorted) {
          const sid = subjectIdOf(topic);
          if (sid <= 0 || seenSubjects.has(sid)) continue;
          seenSubjects.add(sid);
          rangeOptions.push(sid);
        }
        if (rangeOptions.length > 0) {
          setSubjectFromId(String(rangeOptions[0]));
          setSubjectToId(String(rangeOptions[rangeOptions.length - 1]));
        } else {
          setSubjectFromId('');
          setSubjectToId('');
        }
      } catch (e) {
        if (!active) return;
        setError(`Failed to load topics: ${String(e?.message || e)}`);
        setTopics([]);
      } finally {
        if (active) setLoadingTopics(false);
      }
    };

    loadTopics();
    return () => { active = false; };
  }, [selectedQualificationId, setQualificationId]);

  const filteredTopics = useMemo(() => {
    const q = searchText.trim().toLowerCase();
    const scoped = subjectFilter
      ? topics.filter((t) => subjectCodeOf(t).toLowerCase() === String(subjectFilter).trim().toLowerCase())
      : topics;

    if (!q) return scoped;

    return scoped.filter((t) => {
      const haystack = [
        subjectCodeOf(t),
        subjectDescriptionOf(t),
        topicCodeOf(t),
        topicDescriptionOf(t)
      ].join(' ').toLowerCase();
      return haystack.includes(q);
    });
  }, [topics, searchText, subjectFilter]);

  const subjectFilterOptions = useMemo(() => {
    const map = new Map();
    for (const t of topics) {
      const code = subjectCodeOf(t);
      if (!code) continue;
      if (!map.has(code.toLowerCase())) {
        map.set(code.toLowerCase(), { code, description: subjectDescriptionOf(t) });
      }
    }
    return Array.from(map.values()).sort((a, b) =>
      a.code.localeCompare(b.code, undefined, { sensitivity: 'base', numeric: true })
    );
  }, [topics]);

  const subjectRangeOptions = useMemo(() => {
    const map = new Map();
    for (const t of topics) {
      const sid = subjectIdOf(t);
      if (sid <= 0 || map.has(sid)) continue;
      map.set(sid, {
        id: sid,
        code: subjectCodeOf(t),
        description: subjectDescriptionOf(t)
      });
    }
    return Array.from(map.values()).sort((a, b) =>
      String(a.code || '').localeCompare(String(b.code || ''), undefined, { sensitivity: 'base', numeric: true })
    );
  }, [topics]);

  const selectedQualificationLabel = useMemo(() => {
    const q = qualifications.find((x) => String(qIdOf(x)) === String(selectedQualificationId));
    if (!q) return '';
    return `${qNumberOf(q) || qIdOf(q)} - ${qDescriptionOf(q) || 'No description'}`;
  }, [qualifications, selectedQualificationId]);

  const getTopicParams = (topicId) => {
    const saved = paramsByTopic[topicId];
    if (!saved) return defaultTopicParams;
    return {
      titleOverride: saved.titleOverride ?? '',
      bulletsPerSlide: Number(saved.bulletsPerSlide || 8),
      includeCoverSlide: Boolean(saved.includeCoverSlide),
      includeVisualResourceSlides: Boolean(saved.includeVisualResourceSlides),
      maxVisualSlides: clampVisualSlideCount(saved.maxVisualSlides || 3),
      includeGeneratedImageSlides: Boolean(saved.includeGeneratedImageSlides),
      maxGeneratedImageSlides: clampGeneratedSlideCount(saved.maxGeneratedImageSlides || 2),
      generatedImageModel: String(saved.generatedImageModel || 'gpt-image-1'),
      generatedImageSize: String(saved.generatedImageSize || '1024x1024'),
      generatedImageStyle: String(saved.generatedImageStyle || '')
    };
  };

  const patchTopicParams = (topicId, patch) => {
    setParamsByTopic((prev) => ({
      ...prev,
      [topicId]: {
        ...getTopicParams(topicId),
        ...patch
      }
    }));
  };

  const setTopicMessage = (topicId, message, isError = false) => {
    setTopicMessageById((prev) => ({
      ...prev,
      [topicId]: { message, isError }
    }));
  };

  const buildTopicPayload = (topicId) => {
    const topicParams = getTopicParams(topicId);
    return {
      TopicId: topicId,
      TitleOverride: String(topicParams.titleOverride || '').trim() || null,
      BulletsPerSlide: clampBulletsPerSlide(topicParams.bulletsPerSlide),
      IncludeCoverSlide: Boolean(topicParams.includeCoverSlide),
      IncludeVisualResourceSlides: Boolean(topicParams.includeVisualResourceSlides),
      MaxVisualSlides: clampVisualSlideCount(topicParams.maxVisualSlides),
      IncludeGeneratedImageSlides: Boolean(topicParams.includeGeneratedImageSlides),
      MaxGeneratedImageSlides: clampGeneratedSlideCount(topicParams.maxGeneratedImageSlides),
      GeneratedImageModel: String(topicParams.generatedImageModel || '').trim() || null,
      GeneratedImageSize: String(topicParams.generatedImageSize || '').trim() || null,
      GeneratedImageStyle: String(topicParams.generatedImageStyle || '').trim() || null
    };
  };

  const handleDownloadAllSlides = async () => {
    const qid = Number(selectedQualificationId || 0);
    if (!qid) {
      setError('Select a qualification first.');
      return;
    }
    setBusyBatch(true);
    setError('');
    setStatus('Saving all qualification slides...');
    try {
      const res = await fetch('/api/Content/export-slides-batch-save', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ QualificationId: qid })
      });
      if (!res.ok) throw new Error(await readErrorMessage(res));
      const data = await res.json();
      setStatus(`Saved ${data?.fileName || 'qualification slides'} to ${data?.savedPath || data?.folderPath || 'the qualification SlideShows folder'}.`);
    } catch (e) {
      setError(`Batch export failed: ${String(e?.message || e)}`);
      setStatus('');
    } finally {
      setBusyBatch(false);
    }
  };

  const handleDownloadRangeSlides = async () => {
    const qid = Number(selectedQualificationId || 0);
    if (!qid) {
      setError('Select a qualification first.');
      return;
    }
    if (!subjectFromId || !subjectToId) {
      setError('Select Subject From and Subject To first.');
      return;
    }
    setBusyBatch(true);
    setError('');
    setStatus('Saving subject-range slides...');
    try {
      const res = await fetch('/api/Content/export-slides-batch-save', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          QualificationId: qid,
          SubjectFromId: Number(subjectFromId),
          SubjectToId: Number(subjectToId)
        })
      });
      if (!res.ok) throw new Error(await readErrorMessage(res));
      const data = await res.json();
      setStatus(`Saved ${data?.fileName || 'subject-range slides'} to ${data?.savedPath || data?.folderPath || 'the qualification SlideShows folder'}.`);
    } catch (e) {
      setError(`Subject-range export failed: ${String(e?.message || e)}`);
      setStatus('');
    } finally {
      setBusyBatch(false);
    }
  };

  const handleDownloadTopicSlides = async (topic) => {
    const id = topicIdOf(topic);
    if (!id) return;
    setBusyTopicId(id);
    setTopicMessage(id, 'Generating slides...');
    setError('');
    setStatus('');

    try {
      const payload = buildTopicPayload(id);

      const res = await fetch('/api/Content/export-slides-topic-save', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      if (!res.ok) throw new Error(await readErrorMessage(res));

      const data = await res.json();
      setTopicMessage(id, `Saved ${data?.fileName || 'slides'} to ${data?.savedPath || data?.folderPath || 'the qualification SlideShows folder'}.`);
    } catch (e) {
      setTopicMessage(id, String(e?.message || e), true);
    } finally {
      setBusyTopicId(null);
    }
  };

  const handlePreviewTopicSlides = async (topic) => {
    const id = topicIdOf(topic);
    if (!id) return;

    setPreviewBusyTopicId(id);
    setTopicMessage(id, 'Building preview...');
    setError('');
    setStatus('');
    setPreviewTopic(topic);
    setPreviewOpen(true);
    setPreviewLoading(true);
    setPreviewError('');
    setPreviewData(null);
    setPreviewZoom(1);

    try {
      const res = await fetch('/api/Content/preview-slides-topic', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(buildTopicPayload(id))
      });
      if (!res.ok) throw new Error(await readErrorMessage(res));
      const data = normalizePreviewResponse(await res.json());
      if (!Array.isArray(data.slides) || data.slides.length === 0) {
        throw new Error('Preview endpoint returned no slides.');
      }
      setPreviewData(data);
      setTopicMessage(id, `Preview ready (${data.slides.length} slides).`);
    } catch (e) {
      const msg = String(e?.message || e);
      setPreviewError(msg);
      setTopicMessage(id, msg, true);
    } finally {
      setPreviewLoading(false);
      setPreviewBusyTopicId(null);
    }
  };

  const handlePrintPreview = () => {
    if (!previewData || !Array.isArray(previewData.slides) || previewData.slides.length === 0) {
      setPreviewError('No preview slides available to print.');
      return;
    }

    const popup = window.open('', '_blank', 'width=1400,height=900');
    if (!popup) {
      setPreviewError('Popup blocked. Allow popups to print preview.');
      return;
    }

    popup.document.open();
    popup.document.write(buildSlidesPrintHtml(previewData));
    popup.document.close();
  };

  return (
    <div className="mainpage-root">
      <h2 className="mainpage-title">PowerPoint Slides Export</h2>
      <p>Select a qualification, filter subject-to-subject, then preview or save slides per topic. You can optionally inject local visual resource slides from <code>Imports/SlideAssets</code> and/or generate AI image slides from topic text.</p>

      <div className="form-section">
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))', gap: 10 }}>
          <label>
            Qualification
            <select
              className="mainpage-input"
              value={selectedQualificationId}
              onChange={(e) => setSelectedQualificationId(e.target.value)}
              disabled={loadingQualifications || qualifications.length === 0}
            >
              {qualifications.length === 0 ? <option value="">No qualifications found</option> : null}
              {qualifications.map((q) => (
                <option key={qIdOf(q)} value={qIdOf(q)}>
                  {qNumberOf(q) || qIdOf(q)} - {qDescriptionOf(q) || 'No description'}
                </option>
              ))}
            </select>
          </label>
          <label>
            Subject Filter
            <select
              className="mainpage-input"
              value={subjectFilter}
              onChange={(e) => setSubjectFilter(e.target.value)}
              disabled={loadingTopics || subjectFilterOptions.length === 0}
            >
              <option value="">All Subjects</option>
              {subjectFilterOptions.map((s) => (
                <option key={s.code} value={s.code}>
                  {s.code}{s.description ? ` - ${s.description}` : ''}
                </option>
              ))}
            </select>
          </label>
          <label>
            Search Topics
            <input
              className="mainpage-input"
              placeholder="Search inside selected subject (code/description/topic)"
              value={searchText}
              onChange={(e) => setSearchText(e.target.value)}
            />
          </label>
          <label>
            Subject From
            <select
              className="mainpage-input"
              value={subjectFromId}
              onChange={(e) => setSubjectFromId(e.target.value)}
              disabled={loadingTopics || subjectRangeOptions.length === 0}
            >
              {subjectRangeOptions.length === 0 ? <option value="">No subjects</option> : null}
              {subjectRangeOptions.map((s) => (
                <option key={`from-${s.id}`} value={s.id}>
                  {s.code}{s.description ? ` - ${s.description}` : ''}
                </option>
              ))}
            </select>
          </label>
          <label>
            Subject To
            <select
              className="mainpage-input"
              value={subjectToId}
              onChange={(e) => setSubjectToId(e.target.value)}
              disabled={loadingTopics || subjectRangeOptions.length === 0}
            >
              {subjectRangeOptions.length === 0 ? <option value="">No subjects</option> : null}
              {subjectRangeOptions.map((s) => (
                <option key={`to-${s.id}`} value={s.id}>
                  {s.code}{s.description ? ` - ${s.description}` : ''}
                </option>
              ))}
            </select>
          </label>
        </div>
        {selectedQualificationLabel ? (
          <div style={{ marginTop: 8, color: '#3d566e' }}>
            <strong>Selected:</strong> {selectedQualificationLabel}
          </div>
        ) : null}
        <div style={{ marginTop: 4, color: '#3d566e' }}>
          <strong>Subject Scope:</strong> {subjectFilter || 'All Subjects'} | <strong>Topics:</strong> {filteredTopics.length}
        </div>
        <div className="button-row">
          <button type="button" onClick={handleDownloadAllSlides} disabled={busyBatch || !selectedQualificationId}>
            {busyBatch ? 'Saving...' : 'Save All Qualification Slides'}
          </button>
          <button type="button" onClick={handleDownloadRangeSlides} disabled={busyBatch || !selectedQualificationId || !subjectFromId || !subjectToId}>
            {busyBatch ? 'Saving...' : 'Save Subject Range Slides'}
          </button>
        </div>
      </div>

      {error ? <div style={{ color: '#b00020', marginBottom: 10 }}>{error}</div> : null}
      {status ? <div style={{ color: '#185a3a', marginBottom: 10 }}>{status}</div> : null}

      {loadingTopics ? <div>Loading topics...</div> : null}

      {!loadingTopics && filteredTopics.length === 0 ? (
        <div style={{ color: '#3d566e' }}>No topics found for the selected qualification.</div>
      ) : null}

      {!loadingTopics && filteredTopics.length > 0 ? (
        <table className="mainpage-table">
          <thead>
            <tr>
              <th>Subject</th>
              <th>Topic Code</th>
              <th>Topic Description</th>
              <th style={{ width: 420 }}>Actions</th>
            </tr>
          </thead>
          <tbody>
            {filteredTopics.map((topic) => {
              const id = topicIdOf(topic);
              const expanded = expandedTopicId === id;
              const params = getTopicParams(id);
              const topicMessage = topicMessageById[id];
              const topicBusy = busyTopicId === id;
              const topicPreviewBusy = previewBusyTopicId === id;
              return (
                <React.Fragment key={id}>
                  <tr>
                    <td>{subjectCodeOf(topic) || '-'}</td>
                    <td>{topicCodeOf(topic) || '-'}</td>
                    <td>{topicDescriptionOf(topic) || '-'}</td>
                    <td>
                      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
                        <button
                          type="button"
                          onClick={() => setExpandedTopicId(expanded ? null : id)}
                        >
                          {expanded ? 'Hide Params' : 'Slide Params'}
                        </button>
                        <button
                          type="button"
                          onClick={() => handlePreviewTopicSlides(topic)}
                          disabled={topicPreviewBusy}
                        >
                          {topicPreviewBusy ? 'Previewing...' : 'Preview'}
                        </button>
                        <button
                          type="button"
                          onClick={() => handleDownloadTopicSlides(topic)}
                          disabled={topicBusy}
                        >
                          {topicBusy ? 'Saving...' : 'Save .pptx'}
                        </button>
                      </div>
                      {topicMessage?.message ? (
                        <div style={{ marginTop: 6, color: topicMessage.isError ? '#b00020' : '#185a3a' }}>
                          {topicMessage.message}
                        </div>
                      ) : null}
                    </td>
                  </tr>
                  {expanded ? (
                    <tr>
                      <td colSpan={4} style={{ background: '#f8fbff' }}>
                        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(210px, 1fr))', gap: 10, alignItems: 'end' }}>
                          <label style={{ margin: 0 }}>
                            Slide Title Override (Optional)
                            <input
                              className="mainpage-input"
                              value={params.titleOverride}
                              onChange={(e) => patchTopicParams(id, { titleOverride: e.target.value })}
                              placeholder="Leave empty to use topic description"
                            />
                          </label>
                          <label style={{ margin: 0 }}>
                            Bullets Per Slide
                            <input
                              className="mainpage-input"
                              type="number"
                              min={1}
                              max={20}
                              value={params.bulletsPerSlide}
                              onChange={(e) => patchTopicParams(id, { bulletsPerSlide: Number(e.target.value || 8) })}
                            />
                          </label>
                          <label style={{ margin: 0, display: 'flex', alignItems: 'center', gap: 8, fontWeight: 500 }}>
                            <input
                              type="checkbox"
                              checked={params.includeCoverSlide}
                              onChange={(e) => patchTopicParams(id, { includeCoverSlide: e.target.checked })}
                              style={{ width: 'auto' }}
                            />
                            Include Cover Slide
                          </label>
                          <label style={{ margin: 0, display: 'flex', alignItems: 'center', gap: 8, fontWeight: 500 }}>
                            <input
                              type="checkbox"
                              checked={params.includeVisualResourceSlides}
                              onChange={(e) => patchTopicParams(id, { includeVisualResourceSlides: e.target.checked })}
                              style={{ width: 'auto' }}
                            />
                            Include Local Visual Slides
                          </label>
                          <label style={{ margin: 0, display: 'flex', alignItems: 'center', gap: 8, fontWeight: 500 }}>
                            <input
                              type="checkbox"
                              checked={params.includeGeneratedImageSlides}
                              onChange={(e) => patchTopicParams(id, { includeGeneratedImageSlides: e.target.checked })}
                              style={{ width: 'auto' }}
                            />
                            Include AI Generated Image Slides
                          </label>
                          <label style={{ margin: 0 }}>
                            Max Visual Slides
                            <input
                              className="mainpage-input"
                              type="number"
                              min={1}
                              max={8}
                              value={params.maxVisualSlides}
                              onChange={(e) => patchTopicParams(id, { maxVisualSlides: Number(e.target.value || 3) })}
                              disabled={!params.includeVisualResourceSlides}
                            />
                          </label>
                          <label style={{ margin: 0 }}>
                            Max AI Image Slides
                            <input
                              className="mainpage-input"
                              type="number"
                              min={1}
                              max={4}
                              value={params.maxGeneratedImageSlides}
                              onChange={(e) => patchTopicParams(id, { maxGeneratedImageSlides: Number(e.target.value || 2) })}
                              disabled={!params.includeGeneratedImageSlides}
                            />
                          </label>
                          <label style={{ margin: 0 }}>
                            AI Image Model
                            <input
                              className="mainpage-input"
                              value={params.generatedImageModel}
                              onChange={(e) => patchTopicParams(id, { generatedImageModel: e.target.value })}
                              placeholder="imagen-3.0-generate-002"
                              disabled={!params.includeGeneratedImageSlides}
                            />
                          </label>
                          <label style={{ margin: 0 }}>
                            AI Image Size
                            <select
                              className="mainpage-input"
                              value={params.generatedImageSize}
                              onChange={(e) => patchTopicParams(id, { generatedImageSize: e.target.value })}
                              disabled={!params.includeGeneratedImageSlides}
                            >
                              <option value="1024x1024">1024x1024</option>
                              <option value="1536x1024">1536x1024 (Landscape)</option>
                              <option value="1024x1536">1024x1536 (Portrait)</option>
                            </select>
                          </label>
                          <label style={{ margin: 0, gridColumn: '1 / -1' }}>
                            AI Image Style Guidance (Optional)
                            <input
                              className="mainpage-input"
                              value={params.generatedImageStyle}
                              onChange={(e) => patchTopicParams(id, { generatedImageStyle: e.target.value })}
                              placeholder="e.g. clean engineering infographic, neutral background, realistic tooling"
                              disabled={!params.includeGeneratedImageSlides}
                            />
                          </label>
                        </div>
                      </td>
                    </tr>
                  ) : null}
                </React.Fragment>
              );
            })}
          </tbody>
        </table>
      ) : null}

      {previewOpen ? (
        <div
          style={{
            position: 'fixed',
            inset: 0,
            background: 'rgba(10, 18, 32, 0.65)',
            zIndex: 2000,
            padding: 8
          }}
          onClick={() => setPreviewOpen(false)}
        >
          <div
            style={{
              position: 'absolute',
              inset: 8,
              background: '#ffffff',
              borderRadius: 10,
              display: 'flex',
              flexDirection: 'column',
              boxShadow: '0 10px 36px rgba(10,18,32,0.35)'
            }}
            onClick={(e) => e.stopPropagation()}
          >
            <div
              style={{
                padding: '10px 14px',
                borderBottom: '1px solid #d8dfeb',
                display: 'flex',
                flexWrap: 'wrap',
                gap: 8,
                alignItems: 'center',
                justifyContent: 'space-between'
              }}
            >
              <div style={{ minWidth: 280 }}>
                <div style={{ fontSize: 25, fontWeight: 700, color: '#17375f' }}>
                  Slides Preview
                </div>
                <div style={{ color: '#3d566e' }}>
                  {previewData?.title || topicDescriptionOf(previewTopic) || 'Topic'} {previewData?.topicCode ? `- ${previewData.topicCode}` : ''}
                </div>
              </div>
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
                <button type="button" onClick={() => setPreviewZoom((z) => Math.max(0.6, Number(z || 1) - 0.1))}>Zoom Out</button>
                <button type="button" onClick={() => setPreviewZoom((z) => Math.min(1.8, Number(z || 1) + 0.1))}>Zoom In</button>
                <button type="button" onClick={() => setPreviewZoom(1)}>Reset</button>
                <button
                  type="button"
                  onClick={() => previewTopic && handleDownloadTopicSlides(previewTopic)}
                  disabled={!previewData || busyTopicId === topicIdOf(previewTopic)}
                >
                  {busyTopicId === topicIdOf(previewTopic) ? 'Saving...' : 'Save .pptx'}
                </button>
                <button type="button" onClick={handlePrintPreview} disabled={!previewData}>Print Preview</button>
                <button type="button" onClick={() => setPreviewOpen(false)}>Close</button>
              </div>
            </div>

            <div style={{ padding: '8px 14px', borderBottom: '1px solid #eef2f7', color: '#3d566e' }}>
              Slides: {previewData?.slides?.length || 0} | Visual resources: {previewData?.visualResourcesMatched || 0} (Local {previewData?.localVisualResourcesMatched || 0}, AI {previewData?.generatedVisualResourcesMatched || 0}) | Bullets per slide: {previewData?.bulletsPerSlide || '-'} | Zoom: {Math.round(previewZoom * 100)}%
            </div>
            {Array.isArray(previewData?.visualResources) && previewData.visualResources.length > 0 ? (
              <div style={{ padding: '8px 14px', borderBottom: '1px solid #eef2f7', color: '#2f4f74', fontSize: 14 }}>
                Visual files: {previewData.visualResources.map((v) => `${v.source === 'generated' ? 'AI' : 'Local'}: ${v.fileName || v.caption}`).filter(Boolean).join(' | ')}
              </div>
            ) : null}
            {Array.isArray(previewData?.warnings) && previewData.warnings.length > 0 ? (
              <div style={{ padding: '8px 14px', borderBottom: '1px solid #eef2f7', color: '#8a3f00', background: '#fff6ed' }}>
                {previewData.warnings.join(' | ')}
              </div>
            ) : null}

            {previewError ? <div style={{ color: '#b00020', padding: '10px 14px' }}>{previewError}</div> : null}
            {previewLoading ? <div style={{ padding: '14px' }}>Loading preview...</div> : null}

            {!previewLoading ? (
              <div style={{ flex: 1, overflow: 'auto', padding: 16, background: '#f2f5fa' }}>
                {(previewData?.slides || []).map((slide, idx) => (
                  <div
                    key={`slide-${slide.number}-${idx}`}
                    style={{
                      width: `${Math.round(960 * previewZoom)}px`,
                      minHeight: `${Math.round(540 * previewZoom)}px`,
                      margin: '0 auto 16px',
                      borderRadius: 10,
                      border: '1px solid #d8dfeb',
                      background: slide.type === 'visual' ? '#eef4fb' : 'linear-gradient(180deg, #17375f 0%, #0f2645 100%)',
                      color: slide.type === 'visual' ? '#123053' : '#fff',
                      boxShadow: '0 8px 22px rgba(17,35,58,0.22)',
                      padding: `${Math.max(12, Math.round(22 * previewZoom))}px`,
                      boxSizing: 'border-box'
                    }}
                  >
                    <div style={{ fontSize: Math.max(13, Math.round(16 * previewZoom)), opacity: 0.85 }}>
                      Slide {slide.number} {slide.type === 'cover' ? '(Cover)' : slide.type === 'visual' ? '(Visual)' : ''}
                    </div>
                    <div style={{ fontSize: Math.max(20, Math.round(34 * previewZoom)), fontWeight: 700, margin: '6px 0 6px', lineHeight: 1.15 }}>
                      {slide.title || '-'}
                    </div>
                    {slide.subtitle ? (
                      <div style={{ fontSize: Math.max(14, Math.round(19 * previewZoom)), marginBottom: Math.max(10, Math.round(20 * previewZoom)), opacity: 0.9 }}>
                        {slide.subtitle}
                      </div>
                    ) : null}
                    {slide.type === 'visual' ? (
                      <div style={{ marginTop: Math.max(10, Math.round(18 * previewZoom)) }}>
                        <div
                          style={{
                            height: `${Math.round(280 * previewZoom)}px`,
                            borderRadius: 8,
                            border: '2px dashed #7a94b3',
                            background: '#ffffff',
                            display: 'flex',
                            alignItems: 'center',
                            justifyContent: 'center',
                            color: '#406389',
                            fontSize: Math.max(13, Math.round(18 * previewZoom)),
                            marginBottom: Math.max(12, Math.round(18 * previewZoom))
                          }}
                        >
                          {slide.imageSource === 'generated' ? 'AI image slide' : 'Local image slide'}: {slide.imageFileName || 'Visual resource'}
                        </div>
                        <div style={{ fontSize: Math.max(13, Math.round(17 * previewZoom)), fontWeight: 600 }}>
                          {slide.imageCaption || (slide.bullets?.[0] || 'Local visual resource')}
                        </div>
                      </div>
                    ) : Array.isArray(slide.bullets) && slide.bullets.length > 0 ? (
                      <ul style={{ margin: 0, paddingLeft: Math.max(16, Math.round(26 * previewZoom)), fontSize: Math.max(12, Math.round(20 * previewZoom)), lineHeight: 1.32 }}>
                        {slide.bullets.map((line, lineIdx) => (
                          <li key={`slide-${slide.number}-line-${lineIdx}`} style={{ marginBottom: Math.max(6, Math.round(11 * previewZoom)) }}>
                            {line}
                          </li>
                        ))}
                      </ul>
                    ) : (
                      <div style={{ fontSize: Math.max(13, Math.round(17 * previewZoom)), opacity: 0.92 }}>Cover slide</div>
                    )}
                  </div>
                ))}
                {!previewError && (!previewData?.slides || previewData.slides.length === 0) ? (
                  <div style={{ color: '#3d566e', textAlign: 'center', paddingTop: 16 }}>
                    No slides to preview.
                  </div>
                ) : null}
              </div>
            ) : null}
          </div>
        </div>
      ) : null}
    </div>
  );
};

export default PowerPointSlidesPage;
