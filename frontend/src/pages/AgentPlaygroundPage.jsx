import React, { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQualification } from '../context/QualificationContext';

const API_CHAT = '/api/Knowledge/chat';

const normalizeAgentMode = (value) => { const v = String(value || '').trim().toLowerCase(); return (v === 'gemma' || v === 'qwen') ? 'gemma' : 'mira'; };

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

const parseJsonSafe = async (response) => response.json().catch(() => ({}));

const cardStyle = {
  background: '#ffffff',
  border: '1px solid #d6e2ec',
  borderRadius: 12,
  padding: 16,
  boxShadow: '0 8px 24px rgba(31, 63, 91, 0.06)'
};

const buttonStyle = (background, color = '#ffffff') => ({
  border: `1px solid ${background}`,
  borderRadius: 8,
  background,
  color,
  padding: '8px 12px',
  fontWeight: 700
});

const secondaryButtonStyle = {
  border: '1px solid #b8cede',
  borderRadius: 8,
  background: '#ffffff',
  color: '#1f3f5b',
  padding: '8px 12px',
  fontWeight: 700
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
  minHeight: 120
};

const messageCard = (role) => ({
  background: role === 'user' ? '#f5faff' : '#f9fff8',
  border: role === 'user' ? '1px solid #cfe0ef' : '1px solid #cfe7d2',
  borderRadius: 10,
  padding: 12
});

export default function AgentPlaygroundPage({ agentMode = 'mira' }) {
  const navigate = useNavigate();
  const { qualificationId } = useQualification() || { qualificationId: null };
  const qid = Number(qualificationId || localStorage.getItem('qualificationId') || 0);
  const resolvedAgentMode = normalizeAgentMode(agentMode);
  const isQwenMode = resolvedAgentMode === 'gemma' || resolvedAgentMode === 'qwen';
  const assistantName = isQwenMode ? 'Gemma 4:26B Specialist' : 'Mira Your Lecturer';
  const assistantShortName = isQwenMode ? 'Gemma' : 'Mira';
  const currentQualiaRoute = isQwenMode ? '/qualia/gemma' : '/qualia/mira';
  const currentPlaygroundRoute = isQwenMode ? '/playground/gemma' : '/playground/mira';
  const alternateQualiaRoute = isQwenMode ? '/qualia/mira' : '/qualia/gemma';
  const alternatePlaygroundRoute = isQwenMode ? '/playground/mira' : '/playground/gemma';
  const alternateAssistantShortName = isQwenMode ? 'Mira' : 'Gemma';

  const [qualificationCode, setQualificationCode] = useState('');
  const [qualificationDescription, setQualificationDescription] = useState('');
  const [prompt, setPrompt] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [messages, setMessages] = useState([]);
  const [sessionId, setSessionId] = useState(() => createEphemeralSessionId());
  const [userId] = useState(() => createLocalId(`etdp:${resolvedAgentMode}:playground-user-id`, 'user'));

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

  const scopeLabel = useMemo(() => {
    const qualificationLabel = [qualificationCode, qualificationDescription].filter(Boolean).join(' - ');
    if (qualificationLabel) return qualificationLabel;
    return isQwenMode ? 'Global subject-matter mode' : 'Global playground mode';
  }, [isQwenMode, qualificationCode, qualificationDescription]);

  const helperText = isQwenMode
    ? 'Use Gemma 4:26B to test detailed chapter explanations, subject matter alignment, and learner-facing teaching depth. Qwen can run with or without a selected qualification.'
    : 'Use Mira to test workflow guidance, qualification-aware sequencing, and ETDP page support. When no qualification is selected, the playground still runs in global mode.';

  const promptPlaceholder = isQwenMode
    ? 'Example: Gemma, expand this topic into a detailed learner-facing explanation and show the correct sequence step by step.'
    : 'Example: Mira, tell me the next safe ETDP workflow step and explain why it comes before the next page.';

  const startNewSession = () => {
    setMessages([]);
    setError('');
    setSessionId(createEphemeralSessionId());
  };

  const sendPrompt = async () => {
    const trimmed = String(prompt || '').trim();
    if (!trimmed || busy) return;

    setMessages((prev) => [...prev, { role: 'user', text: trimmed }]);
    setPrompt('');
    setError('');
    setBusy(true);

    try {
      const res = await fetch(API_CHAT, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          message: trimmed,
          qualificationId: qid > 0 ? qid : null,
          qualificationCode,
          qualificationDescription,
          userId,
          sessionId,
          agentMode: resolvedAgentMode,
          allowGlobalContext: true
        })
      });
      const data = await parseJsonSafe(res);
      if (!res.ok) {
        throw new Error(data?.message || data?.reply || `Playground chat failed (${res.status}).`);
      }

      setMessages((prev) => [...prev, {
        role: 'assistant',
        text: String(data?.reply || '').trim() || 'No reply returned.',
        assistant: String(data?.assistant || assistantName).trim() || assistantName,
        backend: String(data?.backend || ''),
        qualificationScoped: Boolean(data?.qualificationScoped)
      }]);
    } catch (err) {
      setError(String(err?.message || err || `Failed to reach ${assistantShortName} playground.`));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="mainpage-root">
      <h2 className="mainpage-title">{`${assistantShortName} Playground`}</h2>
      <p style={{ marginTop: 4, color: '#4b6075' }}>
        Route path: <code>{currentPlaygroundRoute}</code>
      </p>
      <p style={{ marginTop: 4, color: '#4b6075', maxWidth: 1100 }}>
        {helperText}
      </p>

      <div style={{ ...cardStyle, marginBottom: 16, background: isQwenMode ? 'linear-gradient(135deg, #f6fbff 0%, #eef5ff 100%)' : 'linear-gradient(135deg, #f9fff8 0%, #f4f8ff 100%)' }}>
        <div style={{ fontWeight: 800, color: '#1f3f5b', marginBottom: 6 }}>Current Context</div>
        <div style={{ color: '#36506b', marginBottom: 6 }}>Scope: <strong>{scopeLabel}</strong></div>
        <div style={{ color: '#36506b', marginBottom: 10 }}>Session: <code>{sessionId}</code></div>
        <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap' }}>
          <button type="button" onClick={() => navigate(currentQualiaRoute)} style={secondaryButtonStyle}>
            {`${assistantShortName} Qualia`}
          </button>
          <button type="button" disabled style={{ ...buttonStyle('#245787'), opacity: 0.78 }}>
            {`${assistantShortName} Playground`}
          </button>
          <button type="button" onClick={() => navigate(alternateQualiaRoute)} style={secondaryButtonStyle}>
            {`${alternateAssistantShortName} Qualia`}
          </button>
          <button type="button" onClick={() => navigate(alternatePlaygroundRoute)} style={secondaryButtonStyle}>
            {`${alternateAssistantShortName} Playground`}
          </button>
          <button type="button" onClick={() => navigate('/agent-governance')} style={secondaryButtonStyle}>
            Agent Governance
          </button>
          <button type="button" onClick={() => navigate('/playground/training')} style={secondaryButtonStyle}>
            LLM Training
          </button>
          <button type="button" onClick={() => navigate('/playground/assessment')} style={secondaryButtonStyle}>
            Competence Assessment
          </button>
        </div>
      </div>

      <section style={cardStyle}>
        <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12, flexWrap: 'wrap', alignItems: 'center', marginBottom: 10 }}>
          <div>
            <div style={{ fontSize: 20, fontWeight: 800, color: '#1f3f5b' }}>{`${assistantShortName} Chat Test`}</div>
            <div style={{ color: '#4b6075', marginTop: 4 }}>
              {isQwenMode
                ? 'Global context is always available here, with qualification context added when selected.'
                : 'This page tests Mira directly with playground-safe chat prompts.'}
            </div>
          </div>
          <button type="button" onClick={startNewSession} style={secondaryButtonStyle}>New Session</button>
        </div>

        <div style={{ display: 'grid', gap: 10, marginBottom: 12, maxHeight: 420, overflowY: 'auto', paddingRight: 4 }}>
          {messages.length === 0 ? (
            <div style={{ background: '#f8fbff', border: '1px dashed #b8cede', borderRadius: 10, padding: 14, color: '#4b6075' }}>
              {isQwenMode
                ? 'No playground messages yet. Ask Qwen to unpack a subject, expand a topic, or explain a procedure in full detail.'
                : 'No playground messages yet. Ask Mira for the next ETDP action, workflow guidance, or qualification-specific support.'}
            </div>
          ) : messages.map((message, index) => (
            <div key={`${message.role}-${index}`} style={messageCard(message.role)}>
              <div style={{ display: 'flex', justifyContent: 'space-between', gap: 10, marginBottom: 6, flexWrap: 'wrap' }}>
                <strong style={{ color: '#1f3f5b' }}>{message.role === 'user' ? 'You' : (message.assistant || assistantName)}</strong>
                {message.backend ? (
                  <span style={{ color: '#4b6075', fontSize: 12 }}>
                    {message.backend} | {message.qualificationScoped ? 'qualification scoped' : 'global'}
                  </span>
                ) : null}
              </div>
              <div style={{ whiteSpace: 'pre-wrap', color: '#24384e' }}>{message.text}</div>
            </div>
          ))}
        </div>

        <label style={{ display: 'grid', gap: 6 }}>
          <span style={{ fontWeight: 700 }}>Prompt</span>
          <textarea
            value={prompt}
            onChange={(e) => setPrompt(e.target.value)}
            rows={7}
            placeholder={promptPlaceholder}
            style={textareaStyle}
          />
        </label>

        <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap', marginTop: 12 }}>
          <button type="button" onClick={sendPrompt} disabled={busy || !String(prompt || '').trim()} style={{ ...buttonStyle(isQwenMode ? '#245787' : '#1e7a4a'), cursor: busy ? 'not-allowed' : 'pointer' }}>
            {busy ? 'Sending...' : `Send to ${assistantShortName}`}
          </button>
        </div>

        {error ? (
          <div style={{ marginTop: 12, background: '#ffe1e1', color: '#8e2020', border: '1px solid #f2b4b4', borderRadius: 8, padding: 10 }}>
            {error}
          </div>
        ) : null}
      </section>
    </div>
  );
}
