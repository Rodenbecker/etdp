import React, { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQualification } from '../context/QualificationContext';

const API_ROLE_CONTRACT = '/api/Knowledge/mira-smi-role-contract';
const API_MIRA_CHARACTER = '/api/Knowledge/mira-character';
const API_MIRA_ADVANCED_RULES = '/api/Knowledge/mira-advanced-rules';
const API_SMI_RULES = '/api/Knowledge/smi-compare-compile-rules';
const API_MIRA_REVIEW_FEEDBACK = '/api/Knowledge/mira-review-feedback';
const API_MIRA_CHAT = '/api/Knowledge/chat';
const SPECIALIST_NAME = 'Gemma 4:26B';

const DEFAULT_ROLE_CONTRACT = {
  miraPrimaryRole: '',
  miraReviewRole: '',
  miraReviewBoundaries: '',
  smiPrimaryRole: '',
  handoffWorkflow: '',
  feedbackLoggingRules: '',
  operatorVisibilityRules: '',
  updatedAtUtc: '',
  path: '',
  promptBlock: ''
};

const DEFAULT_MIRA_PROFILE = {
  profileName: 'Mira Your Lecturer',
  purpose: '',
  mentorIdentity: '',
  experienceLegacy: '',
  teachingTrademarks: '',
  iopKnowledgeCore: '',
  deliveryStandards: '',
  signaturePhrases: ''
};

const DEFAULT_SMI_RULES = {
  purpose: '',
  compareRules: '',
  compileRules: '',
  parseRules: '',
  guardrails: '',
  outputFormatRules: '',
  updatedAtUtc: '',
  path: '',
  promptBlock: ''
};

const createDefaultFeedbackForm = () => ({
  sourceAgent: SPECIALIST_NAME,
  reviewContext: 'agent-governance',
  artifactType: `${SPECIALIST_NAME} output`,
  artifactReference: '',
  severity: 'medium',
  status: 'new',
  title: '',
  summary: '',
  details: '',
  recommendedAction: '',
  sourceExcerpt: '',
  operatorNotes: ''
});

const ROLE_FIELDS = [
  { key: 'miraPrimaryRole', label: 'Mira Primary Role', rows: 4 },
  { key: 'miraReviewRole', label: 'Mira Review Role', rows: 4 },
  { key: 'miraReviewBoundaries', label: 'Mira Review Boundaries', rows: 4 },
  { key: 'smiPrimaryRole', label: `${SPECIALIST_NAME} Primary Role`, rows: 4 },
  { key: 'handoffWorkflow', label: 'Handoff Workflow', rows: 5 },
  { key: 'feedbackLoggingRules', label: 'Feedback Logging Rules', rows: 4 },
  { key: 'operatorVisibilityRules', label: 'Operator Visibility Rules', rows: 4 }
];

const SMI_FIELDS = [
  { key: 'purpose', label: 'Purpose', rows: 3 },
  { key: 'compareRules', label: 'Compare Rules', rows: 6 },
  { key: 'compileRules', label: 'Compile Rules', rows: 6 },
  { key: 'parseRules', label: 'Parse Rules', rows: 6 },
  { key: 'guardrails', label: 'Guardrails', rows: 5 },
  { key: 'outputFormatRules', label: 'Output Format Rules', rows: 4 }
];

const MIRA_FIELDS = [
  { key: 'profileName', label: 'Profile Name', kind: 'input' },
  { key: 'purpose', label: 'Purpose', rows: 6 },
  { key: 'mentorIdentity', label: 'Mentor Identity', rows: 3 },
  { key: 'experienceLegacy', label: 'Experience Legacy', rows: 3 },
  { key: 'teachingTrademarks', label: 'Teaching Trademarks', rows: 4 },
  { key: 'iopKnowledgeCore', label: 'IOP Knowledge Core', rows: 4 },
  { key: 'deliveryStandards', label: 'Delivery Standards', rows: 4 },
  { key: 'signaturePhrases', label: 'Signature Phrases', rows: 3 }
];

const replaceSpecialistMentions = (value, fallback = '') => {
  const raw = String(value ?? '').trim();
  const base = raw || fallback;
  return String(base)
    .replace(/\bSMI\b/gi, SPECIALIST_NAME)
    .replace(/\bSMi\b/g, SPECIALIST_NAME);
};

const cardStyle = {
  background: '#ffffff',
  border: '1px solid #d6e2ec',
  borderRadius: 12,
  padding: 16,
  boxShadow: '0 8px 24px rgba(31, 63, 91, 0.06)'
};

const textareaStyle = {
  width: '100%',
  border: '1px solid #b8cede',
  borderRadius: 8,
  padding: 10,
  resize: 'vertical',
  fontFamily: 'inherit',
  fontSize: 14,
  lineHeight: 1.5,
  minHeight: 92
};

const selectStyle = {
  width: '100%',
  border: '1px solid #b8cede',
  borderRadius: 8,
  padding: '10px 12px',
  fontFamily: 'inherit',
  fontSize: 14,
  background: '#ffffff'
};

const buttonPrimary = (bg) => ({
  border: `1px solid ${bg}`,
  borderRadius: 8,
  background: bg,
  color: '#ffffff',
  padding: '9px 14px',
  fontWeight: 700
});

const buttonSecondary = {
  border: '1px solid #b8cede',
  borderRadius: 8,
  background: '#ffffff',
  color: '#1f3f5b',
  padding: '9px 14px',
  fontWeight: 700
};

const messageCard = (role) => ({
  background: role === 'user' ? '#f5faff' : '#f9fff8',
  border: role === 'user' ? '1px solid #cfe0ef' : '1px solid #cfe7d2',
  borderRadius: 10,
  padding: 12
});

const normalizeRoleContract = (raw) => ({
  miraPrimaryRole: String(raw?.miraPrimaryRole ?? raw?.MiraPrimaryRole ?? ''),
  miraReviewRole: String(raw?.miraReviewRole ?? raw?.MiraReviewRole ?? ''),
  miraReviewBoundaries: String(raw?.miraReviewBoundaries ?? raw?.MiraReviewBoundaries ?? ''),
  smiPrimaryRole: String(raw?.smiPrimaryRole ?? raw?.SmiPrimaryRole ?? ''),
  handoffWorkflow: String(raw?.handoffWorkflow ?? raw?.HandoffWorkflow ?? ''),
  feedbackLoggingRules: String(raw?.feedbackLoggingRules ?? raw?.FeedbackLoggingRules ?? ''),
  operatorVisibilityRules: String(raw?.operatorVisibilityRules ?? raw?.OperatorVisibilityRules ?? ''),
  updatedAtUtc: String(raw?.updatedAtUtc ?? raw?.UpdatedAtUtc ?? ''),
  path: String(raw?.path ?? raw?.Path ?? ''),
  promptBlock: String(raw?.promptBlock ?? raw?.PromptBlock ?? '')
});

const normalizeMiraProfile = (raw) => ({
  profileName: String(raw?.profileName ?? raw?.ProfileName ?? 'Mira Your Lecturer').trim() || 'Mira Your Lecturer',
  purpose: String(raw?.purpose ?? raw?.Purpose ?? ''),
  mentorIdentity: String(raw?.mentorIdentity ?? raw?.MentorIdentity ?? ''),
  experienceLegacy: String(raw?.experienceLegacy ?? raw?.ExperienceLegacy ?? ''),
  teachingTrademarks: String(raw?.teachingTrademarks ?? raw?.TeachingTrademarks ?? ''),
  iopKnowledgeCore: String(raw?.iopKnowledgeCore ?? raw?.IopKnowledgeCore ?? ''),
  deliveryStandards: String(raw?.deliveryStandards ?? raw?.DeliveryStandards ?? ''),
  signaturePhrases: String(raw?.signaturePhrases ?? raw?.SignaturePhrases ?? '')
});

const normalizeSmiRules = (raw) => ({
  purpose: String(raw?.purpose ?? raw?.Purpose ?? ''),
  compareRules: String(raw?.compareRules ?? raw?.CompareRules ?? ''),
  compileRules: String(raw?.compileRules ?? raw?.CompileRules ?? ''),
  parseRules: String(raw?.parseRules ?? raw?.ParseRules ?? ''),
  guardrails: String(raw?.guardrails ?? raw?.Guardrails ?? ''),
  outputFormatRules: String(raw?.outputFormatRules ?? raw?.OutputFormatRules ?? ''),
  updatedAtUtc: String(raw?.updatedAtUtc ?? raw?.UpdatedAtUtc ?? ''),
  path: String(raw?.path ?? raw?.Path ?? ''),
  promptBlock: String(raw?.promptBlock ?? raw?.PromptBlock ?? '')
});

const normalizeFeedbackItem = (raw) => ({
  id: Number(raw?.id ?? raw?.Id ?? 0),
  qualificationCode: String(raw?.qualificationCode ?? raw?.QualificationCode ?? ''),
  qualificationDescription: String(raw?.qualificationDescription ?? raw?.QualificationDescription ?? ''),
  reportedBy: String(raw?.reportedBy ?? raw?.ReportedBy ?? 'Mira'),
  sourceAgent: replaceSpecialistMentions(raw?.sourceAgent ?? raw?.SourceAgent ?? SPECIALIST_NAME, SPECIALIST_NAME),
  reviewContext: String(raw?.reviewContext ?? raw?.ReviewContext ?? ''),
  artifactType: replaceSpecialistMentions(raw?.artifactType ?? raw?.ArtifactType ?? '', ''),
  artifactReference: String(raw?.artifactReference ?? raw?.ArtifactReference ?? ''),
  severity: String(raw?.severity ?? raw?.Severity ?? 'medium'),
  status: String(raw?.status ?? raw?.Status ?? 'new'),
  title: String(raw?.title ?? raw?.Title ?? ''),
  summary: String(raw?.summary ?? raw?.Summary ?? ''),
  details: String(raw?.details ?? raw?.Details ?? ''),
  recommendedAction: String(raw?.recommendedAction ?? raw?.RecommendedAction ?? ''),
  sourceExcerpt: String(raw?.sourceExcerpt ?? raw?.SourceExcerpt ?? ''),
  operatorNotes: String(raw?.operatorNotes ?? raw?.OperatorNotes ?? ''),
  createdAtUtc: String(raw?.createdAtUtc ?? raw?.CreatedAtUtc ?? ''),
  updatedAtUtc: String(raw?.updatedAtUtc ?? raw?.UpdatedAtUtc ?? ''),
  reviewedAtUtc: String(raw?.reviewedAtUtc ?? raw?.ReviewedAtUtc ?? ''),
  closedAtUtc: String(raw?.closedAtUtc ?? raw?.ClosedAtUtc ?? '')
});

const buildPreview = (text, fallback) => {
  const normalized = String(text || '').replace(/\s+/g, ' ').trim();
  if (!normalized) return fallback;
  return normalized.length <= 180 ? normalized : `${normalized.slice(0, 180).trimEnd()}...`;
};

const parseJsonSafe = async (response) => response.json().catch(() => ({}));

const createLocalId = (storageKey, prefix) => {
  try {
    const existing = String(window.localStorage.getItem(storageKey) || '').trim();
    if (existing) return existing;
    const generated = typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
      ? `${prefix}-${crypto.randomUUID()}`
      : `${prefix}-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;
    window.localStorage.setItem(storageKey, generated);
    return generated;
  } catch {
    return `${prefix}-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;
  }
};

const createEphemeralSessionId = () => (
  typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
    ? `session-${crypto.randomUUID()}`
    : `session-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`
);

const formatUtc = (value) => {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? 'Not saved yet' : date.toLocaleString();
};

const toneByValue = (value, kind) => {
  const key = String(value || '').toLowerCase();
  if (kind === 'severity') {
    if (key === 'critical') return { background: '#ffe3e1', border: '#f0b2ad', color: '#9e241d' };
    if (key === 'high') return { background: '#fff0e3', border: '#f2cdac', color: '#9a5410' };
    if (key === 'low') return { background: '#edf8ed', border: '#c8e3ca', color: '#1b6c36' };
    return { background: '#eef5ff', border: '#c9d9ee', color: '#245787' };
  }
  if (key === 'resolved') return { background: '#e7f7ec', border: '#b4e1c3', color: '#196b34' };
  if (key === 'change_requested') return { background: '#fff2df', border: '#f1d4a9', color: '#8d5612' };
  if (key === 'reviewed') return { background: '#edf3ff', border: '#c9d7ef', color: '#2a527c' };
  return { background: '#f6f7fa', border: '#d8dbe2', color: '#49586a' };
};

const badgeStyle = (tone) => ({
  display: 'inline-flex',
  alignItems: 'center',
  borderRadius: 999,
  border: `1px solid ${tone.border}`,
  background: tone.background,
  color: tone.color,
  padding: '5px 10px',
  fontSize: 12,
  fontWeight: 700
});

const createFeedbackEditState = (items) => items.reduce((acc, item) => {
  acc[item.id] = { status: item.status, severity: item.severity, operatorNotes: item.operatorNotes };
  return acc;
}, {});

export default function AgentGovernancePage() {
  const navigate = useNavigate();
  const { qualificationId } = useQualification() || { qualificationId: null };
  const qid = Number(qualificationId || localStorage.getItem('qualificationId') || 0);

  const [qualificationCode, setQualificationCode] = useState('');
  const [qualificationDescription, setQualificationDescription] = useState('');
  const [roleContract, setRoleContract] = useState(DEFAULT_ROLE_CONTRACT);
  const [roleBusy, setRoleBusy] = useState(false);
  const [roleStatus, setRoleStatus] = useState('');
  const [roleError, setRoleError] = useState('');
  const [miraProfile, setMiraProfile] = useState(DEFAULT_MIRA_PROFILE);
  const [miraRules, setMiraRules] = useState('');
  const [miraProfilePath, setMiraProfilePath] = useState('Requests/mira-character-profile.json');
  const [miraRulesPath, setMiraRulesPath] = useState('Requests/mira-advanced-reasoning-rules.md');
  const [miraBusy, setMiraBusy] = useState(false);
  const [miraStatus, setMiraStatus] = useState('');
  const [miraError, setMiraError] = useState('');
  const [smiRules, setSmiRules] = useState(DEFAULT_SMI_RULES);
  const [smiBusy, setSmiBusy] = useState(false);
  const [smiStatus, setSmiStatus] = useState('');
  const [smiError, setSmiError] = useState('');
  const [feedbackItems, setFeedbackItems] = useState([]);
  const [feedbackEdits, setFeedbackEdits] = useState({});
  const [feedbackForm, setFeedbackForm] = useState(() => createDefaultFeedbackForm());
  const [feedbackFilterStatus, setFeedbackFilterStatus] = useState('open');
  const [feedbackLoadBusy, setFeedbackLoadBusy] = useState(false);
  const [feedbackCreateBusy, setFeedbackCreateBusy] = useState(false);
  const [feedbackRowBusy, setFeedbackRowBusy] = useState({});
  const [feedbackStatus, setFeedbackStatus] = useState('');
  const [feedbackError, setFeedbackError] = useState('');
  const [playgroundPrompt, setPlaygroundPrompt] = useState('');
  const [playgroundBusy, setPlaygroundBusy] = useState(false);
  const [playgroundError, setPlaygroundError] = useState('');
  const [playgroundMessages, setPlaygroundMessages] = useState([]);
  const [playgroundSessionId, setPlaygroundSessionId] = useState(() => createEphemeralSessionId());
  const [playgroundUserId] = useState(() => createLocalId('etdp:agent-governance-user-id', 'user'));

  const qualificationLabel = useMemo(() => {
    const code = String(qualificationCode || '').trim();
    const description = String(qualificationDescription || '').trim();
    return code && description ? `${code} - ${description}` : (code || description || 'Global playground mode');
  }, [qualificationCode, qualificationDescription]);

  const rolePreview = useMemo(
    () => buildPreview(roleContract.miraPrimaryRole || roleContract.miraReviewRole || roleContract.handoffWorkflow, 'No role contract loaded yet.'),
    [roleContract]
  );
  const miraPreview = useMemo(() => buildPreview(miraProfile.purpose, 'No Mira purpose loaded yet.'), [miraProfile.purpose]);
  const smiPreview = useMemo(
    () => buildPreview(smiRules.compareRules || smiRules.compileRules || smiRules.parseRules, `No ${SPECIALIST_NAME} compare/compile rules loaded yet.`),
    [smiRules.compareRules, smiRules.compileRules, smiRules.parseRules]
  );
  const latestMiraReply = useMemo(
    () => [...playgroundMessages].reverse().find((message) => message.role === 'assistant') || null,
    [playgroundMessages]
  );
  const feedbackCounts = useMemo(() => feedbackItems.reduce((acc, item) => {
    const key = String(item.status || 'new').toLowerCase();
    acc.total += 1;
    acc[key] = (acc[key] || 0) + 1;
    if (key !== 'resolved') acc.open += 1;
    return acc;
  }, { total: 0, open: 0, new: 0, reviewed: 0, change_requested: 0, resolved: 0 }), [feedbackItems]);

  useEffect(() => {
    if (!qid) {
      setQualificationCode('');
      setQualificationDescription('');
      return;
    }
    const controller = new AbortController();
    (async () => {
      try {
        const res = await fetch(`/api/Qualification/${qid}`, { signal: controller.signal });
        if (!res.ok) return;
        const data = await res.json();
        setQualificationCode(String(data?.qualificationNumber ?? data?.QualificationNumber ?? '').trim());
        setQualificationDescription(String(data?.qualificationDescription ?? data?.QualificationDescription ?? '').trim());
      } catch {
        if (!controller.signal.aborted) {
          setQualificationCode('');
          setQualificationDescription('');
        }
      }
    })();
    return () => controller.abort();
  }, [qid]);

  useEffect(() => {
    let active = true;
    const controller = new AbortController();
    (async () => {
      try {
        const [roleRes, profileRes, rulesRes, smiRes] = await Promise.all([
          fetch(API_ROLE_CONTRACT, { signal: controller.signal }),
          fetch(API_MIRA_CHARACTER, { signal: controller.signal }),
          fetch(API_MIRA_ADVANCED_RULES, { signal: controller.signal }),
          fetch(API_SMI_RULES, { signal: controller.signal })
        ]);
        const [roleData, profileData, rulesData, smiData] = await Promise.all([
          parseJsonSafe(roleRes),
          parseJsonSafe(profileRes),
          parseJsonSafe(rulesRes),
          parseJsonSafe(smiRes)
        ]);
        if (!active) return;
        if (roleRes.ok) setRoleContract(normalizeRoleContract(roleData)); else setRoleError(roleData?.message || `Failed to load Mira/${SPECIALIST_NAME} role contract.`);
        if (profileRes.ok) {
          setMiraProfile(normalizeMiraProfile(profileData));
          setMiraProfilePath(String(profileData?.path || 'Requests/mira-character-profile.json'));
        } else setMiraError(profileData?.message || 'Failed to load Mira profile.');
        if (rulesRes.ok) {
          setMiraRules(String(rulesData?.rules ?? rulesData?.content ?? rulesData?.addendum ?? ''));
          setMiraRulesPath(String(rulesData?.path || 'Requests/mira-advanced-reasoning-rules.md'));
        } else setMiraError((prev) => prev || rulesData?.message || 'Failed to load Mira advanced rules.');
        if (smiRes.ok) setSmiRules(normalizeSmiRules(smiData)); else setSmiError(smiData?.message || `Failed to load ${SPECIALIST_NAME} compare/compile rules.`);
      } catch (error) {
        if (active && !controller.signal.aborted) {
          const message = String(error?.message || error || 'Load failed.');
          setRoleError(`Failed to load role governance data: ${message}`);
          setMiraError(`Failed to load Mira governance data: ${message}`);
          setSmiError(`Failed to load ${SPECIALIST_NAME} governance data: ${message}`);
        }
      }
    })();
    return () => {
      active = false;
      controller.abort();
    };
  }, []);

  useEffect(() => {
    let active = true;
    const controller = new AbortController();
    (async () => {
      setFeedbackLoadBusy(true);
      setFeedbackError('');
      try {
        const params = new URLSearchParams({ status: feedbackFilterStatus, take: '60' });
        if (qid > 0) params.set('qualificationId', String(qid));
        const res = await fetch(`${API_MIRA_REVIEW_FEEDBACK}?${params.toString()}`, { signal: controller.signal });
        const data = await parseJsonSafe(res);
        if (!res.ok) throw new Error(data?.message || data?.error || `Failed to load review feedback (${res.status}).`);
        if (!active) return;
        const items = Array.isArray(data?.items) ? data.items.map(normalizeFeedbackItem) : [];
        setFeedbackItems(items);
        setFeedbackEdits(createFeedbackEditState(items));
      } catch (error) {
        if (active && !controller.signal.aborted) setFeedbackError(String(error?.message || error || 'Failed to load Mira review feedback.'));
      } finally {
        if (active) setFeedbackLoadBusy(false);
      }
    })();
    return () => {
      active = false;
      controller.abort();
    };
  }, [qid, feedbackFilterStatus]);

  const saveRoleContract = async () => {
    setRoleBusy(true);
    setRoleStatus('');
    setRoleError('');
    try {
      const res = await fetch(API_ROLE_CONTRACT, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          miraPrimaryRole: roleContract.miraPrimaryRole,
          miraReviewRole: roleContract.miraReviewRole,
          miraReviewBoundaries: roleContract.miraReviewBoundaries,
          smiPrimaryRole: roleContract.smiPrimaryRole,
          handoffWorkflow: roleContract.handoffWorkflow,
          feedbackLoggingRules: roleContract.feedbackLoggingRules,
          operatorVisibilityRules: roleContract.operatorVisibilityRules
        })
      });
      const data = await parseJsonSafe(res);
      if (!res.ok) throw new Error(data?.message || data?.error || `Failed to save role contract (${res.status}).`);
      setRoleContract(normalizeRoleContract(data));
      setRoleStatus(`Mira/${SPECIALIST_NAME} role contract saved.`);
    } catch (error) {
      setRoleError(String(error?.message || error || 'Failed to save role contract.'));
    } finally {
      setRoleBusy(false);
    }
  };

  const saveMiraGovernance = async () => {
    setMiraBusy(true);
    setMiraStatus('');
    setMiraError('');
    try {
      const profileRes = await fetch(API_MIRA_CHARACTER, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(miraProfile)
      });
      const profileData = await parseJsonSafe(profileRes);
      if (!profileRes.ok) throw new Error(profileData?.message || profileData?.error || `Failed to save Mira profile (${profileRes.status}).`);
      const rulesRes = await fetch(API_MIRA_ADVANCED_RULES, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ rules: miraRules })
      });
      const rulesData = await parseJsonSafe(rulesRes);
      if (!rulesRes.ok) throw new Error(rulesData?.message || rulesData?.error || `Failed to save Mira rules (${rulesRes.status}).`);
      setMiraProfile(normalizeMiraProfile(profileData));
      setMiraProfilePath(String(profileData?.path || miraProfilePath));
      setMiraRules(String(rulesData?.rules ?? rulesData?.content ?? rulesData?.addendum ?? ''));
      setMiraRulesPath(String(rulesData?.path || miraRulesPath));
      setMiraStatus('Mira governance rules saved.');
    } catch (error) {
      setMiraError(String(error?.message || error || 'Failed to save Mira governance.'));
    } finally {
      setMiraBusy(false);
    }
  };

  const saveSmiGovernance = async () => {
    setSmiBusy(true);
    setSmiStatus('');
    setSmiError('');
    try {
      const res = await fetch(API_SMI_RULES, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          purpose: smiRules.purpose,
          compareRules: smiRules.compareRules,
          compileRules: smiRules.compileRules,
          parseRules: smiRules.parseRules,
          guardrails: smiRules.guardrails,
          outputFormatRules: smiRules.outputFormatRules
        })
      });
      const data = await parseJsonSafe(res);
      if (!res.ok) throw new Error(data?.message || data?.error || `Failed to save ${SPECIALIST_NAME} rules (${res.status}).`);
      setSmiRules(normalizeSmiRules(data));
      setSmiStatus(`${SPECIALIST_NAME} compare/compile rules saved.`);
    } catch (error) {
      setSmiError(String(error?.message || error || `Failed to save ${SPECIALIST_NAME} rules.`));
    } finally {
      setSmiBusy(false);
    }
  };

  const refreshFeedback = async () => {
    setFeedbackLoadBusy(true);
    setFeedbackStatus('');
    setFeedbackError('');
    try {
      const params = new URLSearchParams({ status: feedbackFilterStatus, take: '60' });
      if (qid > 0) params.set('qualificationId', String(qid));
      const res = await fetch(`${API_MIRA_REVIEW_FEEDBACK}?${params.toString()}`);
      const data = await parseJsonSafe(res);
      if (!res.ok) throw new Error(data?.message || data?.error || `Failed to refresh feedback (${res.status}).`);
      const items = Array.isArray(data?.items) ? data.items.map(normalizeFeedbackItem) : [];
      setFeedbackItems(items);
      setFeedbackEdits(createFeedbackEditState(items));
      setFeedbackStatus('Mira review feedback refreshed.');
    } catch (error) {
      setFeedbackError(String(error?.message || error || 'Failed to refresh feedback.'));
    } finally {
      setFeedbackLoadBusy(false);
    }
  };

  const prefillFeedbackFromLastMiraReply = () => {
    const reply = String(latestMiraReply?.text || '').trim();
    if (!reply) {
      setFeedbackError('There is no Mira reply available to prefill yet.');
      return;
    }
    setFeedbackForm((prev) => ({
      ...prev,
      reviewContext: 'mira-playground',
      artifactType: `${SPECIALIST_NAME} review from playground`,
      artifactReference: `Playground session ${playgroundSessionId}`,
      title: prev.title || 'Mira review finding',
      summary: buildPreview(reply, '').replace(/\.\.\.$/, ''),
      details: reply,
      sourceExcerpt: reply
    }));
    setFeedbackStatus('Feedback form prefilled from the latest Mira playground reply.');
    setFeedbackError('');
  };

  const submitFeedback = async () => {
    if (feedbackCreateBusy) return;
    const title = String(feedbackForm.title || '').trim();
    const summary = String(feedbackForm.summary || '').trim();
    const details = String(feedbackForm.details || '').trim();
    if (!title && !summary && !details) {
      setFeedbackError('Add at least a title, summary, or detailed finding before logging feedback.');
      return;
    }
    setFeedbackCreateBusy(true);
    setFeedbackStatus('');
    setFeedbackError('');
    try {
      const res = await fetch(API_MIRA_REVIEW_FEEDBACK, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          qualificationId: qid > 0 ? qid : null,
          qualificationCode,
          qualificationDescription,
          sourceAgent: feedbackForm.sourceAgent,
          reviewContext: feedbackForm.reviewContext,
          artifactType: feedbackForm.artifactType,
          artifactReference: feedbackForm.artifactReference,
          severity: feedbackForm.severity,
          status: feedbackForm.status,
          title,
          summary,
          details,
          recommendedAction: feedbackForm.recommendedAction,
          sourceExcerpt: feedbackForm.sourceExcerpt,
          operatorNotes: feedbackForm.operatorNotes
        })
      });
      const data = await parseJsonSafe(res);
      if (!res.ok) throw new Error(data?.message || data?.error || `Failed to log feedback (${res.status}).`);
      const item = normalizeFeedbackItem(data?.item || {});
      setFeedbackItems((prev) => [item, ...prev]);
      setFeedbackEdits((prev) => ({ ...prev, [item.id]: { status: item.status, severity: item.severity, operatorNotes: item.operatorNotes } }));
      setFeedbackForm(createDefaultFeedbackForm());
      setFeedbackStatus('Mira review feedback logged with timestamp.');
    } catch (error) {
      setFeedbackError(String(error?.message || error || 'Failed to log Mira review feedback.'));
    } finally {
      setFeedbackCreateBusy(false);
    }
  };
  const updateFeedbackDraft = (id, patch) => setFeedbackEdits((prev) => ({ ...prev, [id]: { ...(prev[id] || {}), ...patch } }));
  const saveFeedbackReview = async (id) => {
    const draft = feedbackEdits[id];
    if (!draft) return;
    setFeedbackRowBusy((prev) => ({ ...prev, [id]: true }));
    setFeedbackStatus('');
    setFeedbackError('');
    try {
      const res = await fetch(`${API_MIRA_REVIEW_FEEDBACK}/${id}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          status: draft.status,
          severity: draft.severity,
          operatorNotes: draft.operatorNotes
        })
      });
      const data = await parseJsonSafe(res);
      if (!res.ok) throw new Error(data?.message || data?.error || `Failed to update feedback ${id} (${res.status}).`);
      const item = normalizeFeedbackItem(data?.item || {});
      setFeedbackItems((prev) => prev.map((current) => (current.id === id ? item : current)));
      setFeedbackEdits((prev) => ({ ...prev, [id]: { status: item.status, severity: item.severity, operatorNotes: item.operatorNotes } }));
      setFeedbackStatus(`Feedback #${id} updated.`);
    } catch (error) {
      setFeedbackError(String(error?.message || error || `Failed to update feedback ${id}.`));
    } finally {
      setFeedbackRowBusy((prev) => ({ ...prev, [id]: false }));
    }
  };
  const startNewPlaygroundSession = () => {
    setPlaygroundMessages([]);
    setPlaygroundError('');
    setPlaygroundSessionId(createEphemeralSessionId());
  };
  const sendPlaygroundPrompt = async () => {
    const trimmed = String(playgroundPrompt || '').trim();
    if (!trimmed || playgroundBusy) return;
    setPlaygroundMessages((prev) => [...prev, { role: 'user', text: trimmed }]);
    setPlaygroundPrompt('');
    setPlaygroundError('');
    setPlaygroundBusy(true);
    try {
      const res = await fetch(API_MIRA_CHAT, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          message: trimmed,
          qualificationId: qid > 0 ? qid : null,
          qualificationCode,
          qualificationDescription,
          userId: playgroundUserId,
          sessionId: playgroundSessionId,
          agentMode: 'mira',
          allowGlobalContext: true
        })
      });
      const data = await parseJsonSafe(res);
      if (!res.ok) throw new Error(data?.message || data?.reply || `Playground chat failed (${res.status}).`);
      setPlaygroundMessages((prev) => [...prev, {
        role: 'assistant',
        text: String(data?.reply || '').trim() || 'No reply returned.',
        assistant: String(data?.assistant || 'Mira Your Lecturer').trim() || 'Mira Your Lecturer',
        backend: String(data?.backend || ''),
        qualificationScoped: Boolean(data?.qualificationScoped)
      }]);
    } catch (error) {
      setPlaygroundError(String(error?.message || error || 'Failed to reach Mira playground.'));
    } finally {
      setPlaygroundBusy(false);
    }
  };

  return (
    <div className="mainpage-root">
      <h2 className="mainpage-title">Agent Governance and Playground</h2>
      <p style={{ marginTop: 4, color: '#4b6075' }}>Route path: <code>/agent-governance</code></p>
      <p style={{ marginTop: 4, color: '#4b6075', maxWidth: 1100 }}>
        This workspace defines the Mira and {SPECIALIST_NAME} operating boundary. Mira stays the in-app call desk and helpdesk, {SPECIALIST_NAME} remains the specialist compare/compile layer, and Mira&apos;s review findings are logged here for operator review before changes are requested.
      </p>

      <div style={{ ...cardStyle, marginBottom: 16, background: 'linear-gradient(135deg, #f9fff8 0%, #f4f8ff 100%)' }}>
        <div style={{ fontWeight: 800, color: '#1f3f5b', marginBottom: 6 }}>Current Context</div>
        <div style={{ color: '#36506b', marginBottom: 6 }}>Qualification: <strong>{qualificationLabel}</strong></div>
        <div style={{ color: '#36506b', marginBottom: 6 }}>Governance rule mode: <strong>{`Mira helpdesk first, ${SPECIALIST_NAME} reviewed by Mira`}</strong></div>
        <div style={{ color: '#36506b' }}>Playground mode: <strong>{qid > 0 ? 'Qualification-scoped when possible' : 'Global rule-training mode'}</strong></div>
        <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap', marginTop: 12 }}>
          <button type="button" onClick={() => navigate('/qualia/mira')} style={buttonSecondary}>Mira Qualia</button>
          <button type="button" onClick={() => navigate('/qualia/gemma')} style={buttonSecondary}>{`${SPECIALIST_NAME} Qualia`}</button>
          <button type="button" onClick={() => navigate('/playground/mira')} style={buttonSecondary}>Mira Playground</button>
          <button type="button" onClick={() => navigate('/playground/gemma')} style={buttonSecondary}>{`${SPECIALIST_NAME} Playground`}</button>
        </div>
      </div>

      <div style={{ display: 'grid', gap: 16 }}>
        <section style={cardStyle}>
          <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12, flexWrap: 'wrap', alignItems: 'center', marginBottom: 10 }}>
            <div>
              <div style={{ fontSize: 20, fontWeight: 800, color: '#1f3f5b' }}>{`Mira / ${SPECIALIST_NAME} Role Contract`}</div>
              <div style={{ color: '#4b6075', marginTop: 4 }}>Stored in the backend role-contract control file.</div>
            </div>
            <button type="button" onClick={saveRoleContract} disabled={roleBusy} style={{ ...buttonPrimary('#245787'), cursor: roleBusy ? 'not-allowed' : 'pointer' }}>
              {roleBusy ? 'Saving...' : 'Save Role Contract'}
            </button>
          </div>
          <div style={{ marginBottom: 10, color: '#36506b' }}>Preview: {rolePreview}</div>
          <div style={{ marginBottom: 14, color: '#36506b' }}>Last saved: <strong>{formatUtc(roleContract.updatedAtUtc)}</strong></div>
          <div style={{ display: 'grid', gap: 12 }}>
            {ROLE_FIELDS.map((field) => (
              <label key={field.key} style={{ display: 'grid', gap: 6 }}>
                <span style={{ fontWeight: 700 }}>{field.label}</span>
                <textarea
                  value={roleContract[field.key]}
                  onChange={(e) => setRoleContract((prev) => ({ ...prev, [field.key]: e.target.value }))}
                  rows={field.rows}
                  style={textareaStyle}
                />
              </label>
            ))}
          </div>
          {roleStatus ? <div style={{ marginTop: 12, background: '#e7f7ec', color: '#196b34', border: '1px solid #b4e1c3', borderRadius: 8, padding: 10 }}>{roleStatus}</div> : null}
          {roleError ? <div style={{ marginTop: 12, background: '#ffe1e1', color: '#8e2020', border: '1px solid #f2b4b4', borderRadius: 8, padding: 10 }}>{roleError}</div> : null}
          <div style={{ marginTop: 12, background: '#f8fbff', border: '1px solid #d5e4ef', borderRadius: 8, padding: 12 }}>
            <div style={{ fontWeight: 700, color: '#1f3f5b', marginBottom: 6 }}>Backend Prompt Preview</div>
            <div style={{ whiteSpace: 'pre-wrap', color: '#36506b', fontSize: 13 }}>{roleContract.promptBlock || 'Prompt preview will appear after the first successful load/save.'}</div>
          </div>
        </section>

        <section style={cardStyle}>
          <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12, flexWrap: 'wrap', alignItems: 'center', marginBottom: 10 }}>
            <div>
              <div style={{ fontSize: 20, fontWeight: 800, color: '#1f3f5b' }}>{`${SPECIALIST_NAME} Compare / Compile Rules`}</div>
              <div style={{ color: '#4b6075', marginTop: 4 }}>Stored in the backend specialist-rules control file.</div>
            </div>
            <button type="button" onClick={saveSmiGovernance} disabled={smiBusy} style={{ ...buttonPrimary('#1f6fa8'), cursor: smiBusy ? 'not-allowed' : 'pointer' }}>
              {smiBusy ? 'Saving...' : `Save ${SPECIALIST_NAME} Rules`}
            </button>
          </div>
          <div style={{ marginBottom: 10, color: '#36506b' }}>Preview: {smiPreview}</div>
          <div style={{ marginBottom: 14, color: '#36506b' }}>Last saved: <strong>{formatUtc(smiRules.updatedAtUtc)}</strong></div>
          <div style={{ display: 'grid', gap: 12 }}>
            {SMI_FIELDS.map((field) => (
              <label key={field.key} style={{ display: 'grid', gap: 6 }}>
                <span style={{ fontWeight: 700 }}>{field.label}</span>
                <textarea
                  value={smiRules[field.key]}
                  onChange={(e) => setSmiRules((prev) => ({ ...prev, [field.key]: e.target.value }))}
                  rows={field.rows}
                  style={textareaStyle}
                />
              </label>
            ))}
          </div>
          {smiStatus ? <div style={{ marginTop: 12, background: '#e7f7ec', color: '#196b34', border: '1px solid #b4e1c3', borderRadius: 8, padding: 10 }}>{smiStatus}</div> : null}
          {smiError ? <div style={{ marginTop: 12, background: '#ffe1e1', color: '#8e2020', border: '1px solid #f2b4b4', borderRadius: 8, padding: 10 }}>{smiError}</div> : null}
          <div style={{ marginTop: 12, background: '#f8fbff', border: '1px solid #d5e4ef', borderRadius: 8, padding: 12 }}>
            <div style={{ fontWeight: 700, color: '#1f3f5b', marginBottom: 6 }}>Backend Prompt Preview</div>
            <div style={{ whiteSpace: 'pre-wrap', color: '#36506b', fontSize: 13 }}>{smiRules.promptBlock || 'Prompt preview will appear after the first successful load/save.'}</div>
          </div>
        </section>

        <section style={cardStyle}>
          <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12, flexWrap: 'wrap', alignItems: 'center', marginBottom: 10 }}>
            <div>
              <div style={{ fontSize: 20, fontWeight: 800, color: '#1f3f5b' }}>Mira Rulebook</div>
              <div style={{ color: '#4b6075', marginTop: 4 }}>Profile path: <code>{miraProfilePath}</code></div>
              <div style={{ color: '#4b6075', marginTop: 4 }}>Advanced rules path: <code>{miraRulesPath}</code></div>
            </div>
            <button type="button" onClick={saveMiraGovernance} disabled={miraBusy} style={{ ...buttonPrimary('#1e7a4a'), cursor: miraBusy ? 'not-allowed' : 'pointer' }}>
              {miraBusy ? 'Saving...' : 'Save Mira Rules'}
            </button>
          </div>
          <div style={{ marginBottom: 10, color: '#36506b' }}>Preview: {miraPreview}</div>
          <div style={{ display: 'grid', gap: 12 }}>
            {MIRA_FIELDS.map((field) => (
              <label key={field.key} style={{ display: 'grid', gap: 6 }}>
                <span style={{ fontWeight: 700 }}>{field.label}</span>
                {field.kind === 'input' ? (
                  <input
                    className="mainpage-input"
                    value={miraProfile[field.key]}
                    onChange={(e) => setMiraProfile((prev) => ({ ...prev, [field.key]: e.target.value }))}
                  />
                ) : (
                  <textarea
                    value={miraProfile[field.key]}
                    onChange={(e) => setMiraProfile((prev) => ({ ...prev, [field.key]: e.target.value }))}
                    rows={field.rows}
                    style={textareaStyle}
                  />
                )}
              </label>
            ))}
            <label style={{ display: 'grid', gap: 6 }}>
              <span style={{ fontWeight: 700 }}>Advanced Rules</span>
              <textarea value={miraRules} onChange={(e) => setMiraRules(e.target.value)} rows={12} style={{ ...textareaStyle, minHeight: 220 }} />
            </label>
          </div>
          {miraStatus ? <div style={{ marginTop: 12, background: '#e7f7ec', color: '#196b34', border: '1px solid #b4e1c3', borderRadius: 8, padding: 10 }}>{miraStatus}</div> : null}
          {miraError ? <div style={{ marginTop: 12, background: '#ffe1e1', color: '#8e2020', border: '1px solid #f2b4b4', borderRadius: 8, padding: 10 }}>{miraError}</div> : null}
        </section>

        <section style={cardStyle}>
          <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12, flexWrap: 'wrap', alignItems: 'center', marginBottom: 10 }}>
            <div>
              <div style={{ fontSize: 20, fontWeight: 800, color: '#1f3f5b' }}>Mira Review Feedback Board</div>
              <div style={{ color: '#4b6075', marginTop: 4 }}>{`Mira logs ${SPECIALIST_NAME} issues here with timestamps instead of silently fixing them.`}</div>
            </div>
            <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap' }}>
              <select value={feedbackFilterStatus} onChange={(e) => setFeedbackFilterStatus(e.target.value)} style={{ ...selectStyle, width: 180 }}>
                <option value="open">Open only</option>
                <option value="all">All statuses</option>
                <option value="new">New</option>
                <option value="reviewed">Reviewed</option>
                <option value="change_requested">Change requested</option>
                <option value="resolved">Resolved</option>
              </select>
              <button type="button" onClick={refreshFeedback} disabled={feedbackLoadBusy} style={{ ...buttonSecondary, cursor: feedbackLoadBusy ? 'not-allowed' : 'pointer' }}>
                {feedbackLoadBusy ? 'Refreshing...' : 'Refresh'}
              </button>
            </div>
          </div>
          <div style={{ color: '#36506b', marginBottom: 12 }}>
            Open: <strong>{feedbackCounts.open}</strong> | New: <strong>{feedbackCounts.new}</strong> | Change requested: <strong>{feedbackCounts.change_requested}</strong> | Resolved: <strong>{feedbackCounts.resolved}</strong>
          </div>
          <div style={{ background: '#f8fbff', border: '1px solid #d5e4ef', borderRadius: 12, padding: 14, marginBottom: 16 }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12, flexWrap: 'wrap', alignItems: 'center', marginBottom: 10 }}>
              <div>
                <div style={{ fontSize: 18, fontWeight: 800, color: '#1f3f5b' }}>Log New Mira Review Feedback</div>
                <div style={{ color: '#4b6075', marginTop: 4 }}>Scope: <strong>{qid > 0 ? qualificationLabel : 'Global governance scope'}</strong></div>
              </div>
              <button type="button" onClick={prefillFeedbackFromLastMiraReply} style={{ ...buttonSecondary, cursor: 'pointer' }}>Use Latest Mira Reply</button>
            </div>
            <div style={{ display: 'grid', gap: 12 }}>
              <div style={{ display: 'grid', gap: 12, gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))' }}>
                <label style={{ display: 'grid', gap: 6 }}><span style={{ fontWeight: 700 }}>Source Agent</span><select value={feedbackForm.sourceAgent} onChange={(e) => setFeedbackForm((prev) => ({ ...prev, sourceAgent: e.target.value }))} style={selectStyle}><option value={SPECIALIST_NAME}>{SPECIALIST_NAME}</option><option value="Mira">Mira</option><option value="ETDP">ETDP</option></select></label>
                <label style={{ display: 'grid', gap: 6 }}><span style={{ fontWeight: 700 }}>Severity</span><select value={feedbackForm.severity} onChange={(e) => setFeedbackForm((prev) => ({ ...prev, severity: e.target.value }))} style={selectStyle}><option value="low">Low</option><option value="medium">Medium</option><option value="high">High</option><option value="critical">Critical</option></select></label>
                <label style={{ display: 'grid', gap: 6 }}><span style={{ fontWeight: 700 }}>Initial Status</span><select value={feedbackForm.status} onChange={(e) => setFeedbackForm((prev) => ({ ...prev, status: e.target.value }))} style={selectStyle}><option value="new">New</option><option value="reviewed">Reviewed</option><option value="change_requested">Change requested</option><option value="resolved">Resolved</option></select></label>
                <label style={{ display: 'grid', gap: 6 }}><span style={{ fontWeight: 700 }}>Review Context</span><input className="mainpage-input" value={feedbackForm.reviewContext} onChange={(e) => setFeedbackForm((prev) => ({ ...prev, reviewContext: e.target.value }))} /></label>
              </div>
              <div style={{ display: 'grid', gap: 12, gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))' }}>
                <label style={{ display: 'grid', gap: 6 }}><span style={{ fontWeight: 700 }}>Artifact Type</span><input className="mainpage-input" value={feedbackForm.artifactType} onChange={(e) => setFeedbackForm((prev) => ({ ...prev, artifactType: e.target.value }))} /></label>
                <label style={{ display: 'grid', gap: 6 }}><span style={{ fontWeight: 700 }}>Artifact Reference</span><input className="mainpage-input" value={feedbackForm.artifactReference} onChange={(e) => setFeedbackForm((prev) => ({ ...prev, artifactReference: e.target.value }))} /></label>
              </div>
              <label style={{ display: 'grid', gap: 6 }}><span style={{ fontWeight: 700 }}>Title</span><input className="mainpage-input" value={feedbackForm.title} onChange={(e) => setFeedbackForm((prev) => ({ ...prev, title: e.target.value }))} /></label>
              <label style={{ display: 'grid', gap: 6 }}><span style={{ fontWeight: 700 }}>Summary</span><textarea value={feedbackForm.summary} onChange={(e) => setFeedbackForm((prev) => ({ ...prev, summary: e.target.value }))} rows={3} style={textareaStyle} /></label>
              <label style={{ display: 'grid', gap: 6 }}><span style={{ fontWeight: 700 }}>Detailed Finding</span><textarea value={feedbackForm.details} onChange={(e) => setFeedbackForm((prev) => ({ ...prev, details: e.target.value }))} rows={6} style={textareaStyle} /></label>
              <label style={{ display: 'grid', gap: 6 }}><span style={{ fontWeight: 700 }}>Recommended Next Action</span><textarea value={feedbackForm.recommendedAction} onChange={(e) => setFeedbackForm((prev) => ({ ...prev, recommendedAction: e.target.value }))} rows={4} style={textareaStyle} /></label>
              <label style={{ display: 'grid', gap: 6 }}><span style={{ fontWeight: 700 }}>Source Excerpt</span><textarea value={feedbackForm.sourceExcerpt} onChange={(e) => setFeedbackForm((prev) => ({ ...prev, sourceExcerpt: e.target.value }))} rows={5} style={textareaStyle} /></label>
            </div>
            <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap', marginTop: 12 }}>
              <button type="button" onClick={submitFeedback} disabled={feedbackCreateBusy} style={{ ...buttonPrimary('#1e7a4a'), cursor: feedbackCreateBusy ? 'not-allowed' : 'pointer' }}>{feedbackCreateBusy ? 'Logging...' : 'Log Mira Feedback'}</button>
              <button type="button" onClick={() => setFeedbackForm(createDefaultFeedbackForm())} style={{ ...buttonSecondary, cursor: 'pointer' }}>Reset Form</button>
            </div>
          </div>
          {feedbackStatus ? <div style={{ marginTop: 12, background: '#e7f7ec', color: '#196b34', border: '1px solid #b4e1c3', borderRadius: 8, padding: 10 }}>{feedbackStatus}</div> : null}
          {feedbackError ? <div style={{ marginTop: 12, background: '#ffe1e1', color: '#8e2020', border: '1px solid #f2b4b4', borderRadius: 8, padding: 10 }}>{feedbackError}</div> : null}
          <div style={{ display: 'grid', gap: 12, marginTop: 16 }}>
            {feedbackItems.length === 0 ? (
              <div style={{ background: '#f8fbff', border: '1px dashed #b8cede', borderRadius: 10, padding: 14, color: '#4b6075' }}>No Mira review feedback items match the current filter yet.</div>
            ) : feedbackItems.map((item) => {
              const draft = feedbackEdits[item.id] || { status: item.status, severity: item.severity, operatorNotes: item.operatorNotes };
              const qLabel = item.qualificationCode && item.qualificationDescription ? `${item.qualificationCode} - ${item.qualificationDescription}` : (item.qualificationCode || item.qualificationDescription || 'Global scope');
              return (
                <div key={item.id} style={{ border: '1px solid #d6e2ec', borderRadius: 12, padding: 14 }}>
                  <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12, flexWrap: 'wrap', marginBottom: 10 }}>
                    <div>
                      <div style={{ fontSize: 18, fontWeight: 800, color: '#1f3f5b' }}>{item.title || 'Untitled feedback'}</div>
                      <div style={{ color: '#4b6075', marginTop: 4 }}>#{item.id} | {item.reportedBy} reviewing {item.sourceAgent} | {qLabel}</div>
                    </div>
                    <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
                      <span style={badgeStyle(toneByValue(item.severity, 'severity'))}>{item.severity}</span>
                      <span style={badgeStyle(toneByValue(item.status, 'status'))}>{item.status.replace(/_/g, ' ')}</span>
                    </div>
                  </div>
                  <div style={{ color: '#36506b', marginBottom: 10 }}>
                    Created: <strong>{formatUtc(item.createdAtUtc)}</strong> | Updated: <strong>{formatUtc(item.updatedAtUtc)}</strong> | Reviewed: <strong>{formatUtc(item.reviewedAtUtc)}</strong> | Closed: <strong>{formatUtc(item.closedAtUtc)}</strong>
                  </div>
                  {item.summary ? <div style={{ marginBottom: 8 }}><strong style={{ color: '#1f3f5b' }}>Summary:</strong><div style={{ whiteSpace: 'pre-wrap', color: '#24384e' }}>{item.summary}</div></div> : null}
                  {item.details ? <div style={{ marginBottom: 8 }}><strong style={{ color: '#1f3f5b' }}>Detailed Finding:</strong><div style={{ whiteSpace: 'pre-wrap', color: '#24384e' }}>{item.details}</div></div> : null}
                  {item.recommendedAction ? <div style={{ marginBottom: 8 }}><strong style={{ color: '#1f3f5b' }}>Recommended Next Action:</strong><div style={{ whiteSpace: 'pre-wrap', color: '#24384e' }}>{item.recommendedAction}</div></div> : null}
                  {item.sourceExcerpt ? <div style={{ marginBottom: 10 }}><strong style={{ color: '#1f3f5b' }}>Source Excerpt:</strong><div style={{ whiteSpace: 'pre-wrap', color: '#24384e', background: '#f8fbff', border: '1px solid #d5e4ef', borderRadius: 8, padding: 10 }}>{item.sourceExcerpt}</div></div> : null}
                  <div style={{ display: 'grid', gap: 12, gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', marginTop: 12 }}>
                    <label style={{ display: 'grid', gap: 6 }}><span style={{ fontWeight: 700 }}>Status</span><select value={draft.status} onChange={(e) => updateFeedbackDraft(item.id, { status: e.target.value })} style={selectStyle}><option value="new">New</option><option value="reviewed">Reviewed</option><option value="change_requested">Change requested</option><option value="resolved">Resolved</option></select></label>
                    <label style={{ display: 'grid', gap: 6 }}><span style={{ fontWeight: 700 }}>Severity</span><select value={draft.severity} onChange={(e) => updateFeedbackDraft(item.id, { severity: e.target.value })} style={selectStyle}><option value="low">Low</option><option value="medium">Medium</option><option value="high">High</option><option value="critical">Critical</option></select></label>
                  </div>
                  <label style={{ display: 'grid', gap: 6, marginTop: 12 }}><span style={{ fontWeight: 700 }}>Operator Notes</span><textarea value={draft.operatorNotes} onChange={(e) => updateFeedbackDraft(item.id, { operatorNotes: e.target.value })} rows={3} style={textareaStyle} /></label>
                  <div style={{ display: 'flex', gap: 10, marginTop: 12 }}>
                    <button type="button" onClick={() => saveFeedbackReview(item.id)} disabled={Boolean(feedbackRowBusy[item.id])} style={{ ...buttonPrimary('#245787'), cursor: feedbackRowBusy[item.id] ? 'not-allowed' : 'pointer' }}>{feedbackRowBusy[item.id] ? 'Saving...' : 'Save Review'}</button>
                  </div>
                </div>
              );
            })}
          </div>
        </section>

        <section style={cardStyle}>
          <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12, flexWrap: 'wrap', alignItems: 'center', marginBottom: 10 }}>
            <div>
              <div style={{ fontSize: 20, fontWeight: 800, color: '#1f3f5b' }}>Mira Governance Playground Chat</div>
              <div style={{ color: '#4b6075', marginTop: 4 }}>Session: <code>{playgroundSessionId}</code></div>
              <div style={{ color: '#4b6075', marginTop: 4 }}>Scope: <strong>{qid > 0 ? qualificationLabel : 'Global playground mode'}</strong></div>
            </div>
            <button type="button" onClick={startNewPlaygroundSession} style={{ ...buttonSecondary, cursor: 'pointer' }}>New Session</button>
          </div>
          <div style={{ marginBottom: 12, color: '#36506b' }}>{`Use this chat to test Mira after changing her rulebook and reviewing ${SPECIALIST_NAME} behavior. When no qualification is selected, the playground skips qualification-only routing and runs in global rule-training mode.`}</div>
          <div style={{ display: 'grid', gap: 10, marginBottom: 12, maxHeight: 420, overflowY: 'auto', paddingRight: 4 }}>
            {playgroundMessages.length === 0 ? (
              <div style={{ background: '#f8fbff', border: '1px dashed #b8cede', borderRadius: 10, padding: 14, color: '#4b6075' }}>No playground messages yet. Save or adjust rules above, then send a prompt here to test Mira&apos;s behavior.</div>
            ) : playgroundMessages.map((message, index) => (
              <div key={`${message.role}-${index}`} style={messageCard(message.role)}>
                <div style={{ display: 'flex', justifyContent: 'space-between', gap: 10, marginBottom: 6 }}>
                  <strong style={{ color: '#1f3f5b' }}>{message.role === 'user' ? 'You' : (message.assistant || 'Mira Your Lecturer')}</strong>
                  {message.backend ? <span style={{ color: '#4b6075', fontSize: 12 }}>{message.backend} | {message.qualificationScoped ? 'qualification scoped' : 'global'}</span> : null}
                </div>
                <div style={{ whiteSpace: 'pre-wrap', color: '#24384e' }}>{message.text}</div>
              </div>
            ))}
          </div>
          <label style={{ display: 'grid', gap: 6 }}>
            <span style={{ fontWeight: 700 }}>Prompt</span>
            <textarea value={playgroundPrompt} onChange={(e) => setPlaygroundPrompt(e.target.value)} rows={6} placeholder={`Example: Mira, explain how you should respond when ${SPECIALIST_NAME} expands a route recommendation with a mistake.`} style={textareaStyle} />
          </label>
          <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap', marginTop: 12 }}>
            <button type="button" onClick={sendPlaygroundPrompt} disabled={playgroundBusy || !String(playgroundPrompt || '').trim()} style={{ ...buttonPrimary('#1e7a4a'), cursor: playgroundBusy ? 'not-allowed' : 'pointer' }}>
              {playgroundBusy ? 'Sending...' : 'Send to Mira'}
            </button>
          </div>
          {playgroundError ? <div style={{ marginTop: 12, background: '#ffe1e1', color: '#8e2020', border: '1px solid #f2b4b4', borderRadius: 8, padding: 10 }}>{playgroundError}</div> : null}
        </section>
      </div>
    </div>
  );
}
