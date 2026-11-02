import React, { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQualification } from '../context/QualificationContext';

const API = '/api/Qualification';

const qId = (q) => Number(q?.id ?? q?.Id ?? 0);
const qNumber = (q) => String(q?.qualificationNumber ?? q?.QualificationNumber ?? '').trim();
const qDescription = (q) => String(q?.qualificationDescription ?? q?.QualificationDescription ?? '').trim();
const qCesm = (q) => String(q?.cesmField ?? q?.CesmField ?? '').trim();

export default function MainMenuPage() {
  const navigate = useNavigate();
  const { qualificationId, setQualificationId } = useQualification() || { qualificationId: null, setQualificationId: () => {} };

  const [qualifications, setQualifications] = useState([]);
  const [selectedId, setSelectedId] = useState(String(qualificationId || localStorage.getItem('qualificationId') || ''));
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    let active = true;
    const load = async () => {
      setLoading(true);
      setError('');
      try {
        const res = await fetch(API);
        if (!res.ok) throw new Error(await res.text());
        const data = await res.json();
        const list = Array.isArray(data) ? data : [];
        if (!active) return;
        setQualifications(list);
        if (!selectedId && list.length > 0) {
          const firstId = String(qId(list[0]));
          setSelectedId(firstId);
          setQualificationId(Number(firstId));
        }
      } catch (e) {
        if (!active) return;
        setError(`Failed to load qualifications: ${e?.message || e}`);
      } finally {
        if (active) setLoading(false);
      }
    };
    load();
    return () => { active = false; };
  }, []);

  useEffect(() => {
    if (qualificationId && !selectedId) {
      setSelectedId(String(qualificationId));
    }
  }, [qualificationId, selectedId]);

  const selectedQualification = useMemo(
    () => qualifications.find((q) => String(qId(q)) === String(selectedId)) || null,
    [qualifications, selectedId]
  );

  const lookupDisplay = selectedQualification
    ? `${qNumber(selectedQualification) || '#'} - ${qDescription(selectedQualification) || 'No description'}${qCesm(selectedQualification) ? ` | CESM: ${qCesm(selectedQualification)}` : ''}`
    : 'Select a qualification first';

  const applyQualificationSelection = (value) => {
    setSelectedId(value);
    const parsed = Number(value || 0);
    if (parsed > 0) setQualificationId(parsed);
  };

  const goToLearnerRegistration = () => {
    if (!selectedId) return;
    setQualificationId(Number(selectedId));
    navigate('/learner-registration');
  };

  const goToWorkExperience = () => {
    if (!selectedId) return;
    setQualificationId(Number(selectedId));
    navigate('/work-experience-logbook');
  };

  const goToLearningMaterial = () => {
    if (!selectedId) return;
    setQualificationId(Number(selectedId));
    navigate('/learning-material');
  };

  return (
    <div className="mainpage-root">
      <h2 className="mainpage-title">Main Menu</h2>
      <p>Use this main menu to open qualification, learner registration, work experience, and learning material workflows.</p>

      <div className="form-section">
        <div style={{ display: 'grid', gridTemplateColumns: 'minmax(280px, 1fr) auto', gap: 10, alignItems: 'end' }}>
          <label>
            Lookup Qualification
            <select
              className="mainpage-input"
              value={selectedId}
              onChange={(e) => applyQualificationSelection(e.target.value)}
              disabled={loading || qualifications.length === 0}
            >
              {qualifications.length === 0 ? <option value="">No qualifications found</option> : null}
              {qualifications.map((q) => {
                const id = qId(q);
                return (
                  <option key={id} value={id}>
                    {qNumber(q) || id} - {qDescription(q) || 'No description'}
                  </option>
                );
              })}
            </select>
          </label>
          <button type="button" onClick={() => navigate('/qualifications')}>View All Qualifications</button>
        </div>

        <div style={{ marginTop: 10, color: '#3d566e' }}>
          <strong>Lookup Parameters:</strong> {lookupDisplay}
        </div>
      </div>

      {error ? <div style={{ color: '#b00020', marginBottom: 12 }}>{error}</div> : null}

      <div style={{ overflowX: 'auto' }}>
        <table className="table">
          <thead>
            <tr>
              <th style={{ width: 70 }}>No.</th>
              <th>Action</th>
              <th>Lookup Parameters</th>
              <th style={{ width: 250 }}>Open Form</th>
            </tr>
          </thead>
          <tbody>
            <tr>
              <td>1</td>
              <td>View all Qualifications</td>
              <td>-</td>
              <td><button type="button" onClick={() => navigate('/qualifications')}>Open Qualifications</button></td>
            </tr>
            <tr>
              <td>2</td>
              <td>Edit Qualification</td>
              <td>{lookupDisplay}</td>
              <td>
                <button
                  className="next-step-button"
                  type="button"
                  onClick={() => navigate(`/main?id=${selectedId}`)}
                  disabled={!selectedId}
                >
                  Go to Qualification Page
                </button>
              </td>
            </tr>
            <tr>
              <td>3</td>
              <td>Capture New Qualification</td>
              <td>-</td>
              <td><button className="next-step-button" type="button" onClick={() => navigate('/main')}>Go to Qualification Page</button></td>
            </tr>
            <tr>
              <td>4</td>
              <td>Learner Registration</td>
              <td>{lookupDisplay}</td>
              <td>
                <button className="next-step-button" type="button" onClick={goToLearnerRegistration} disabled={!selectedId}>
                  Go to Learner Registration Page
                </button>
              </td>
            </tr>
            <tr>
              <td>5</td>
              <td>Capture Work Experience Employer</td>
              <td>{lookupDisplay}</td>
              <td>
                <button className="next-step-button" type="button" onClick={goToWorkExperience} disabled={!selectedId}>
                  Go to Work Experience Logbook
                </button>
              </td>
            </tr>
            <tr>
              <td>6</td>
              <td>Capture Learner Registration Details</td>
              <td>{lookupDisplay}</td>
              <td>
                <button className="next-step-button" type="button" onClick={goToLearnerRegistration} disabled={!selectedId}>
                  Go to Learner Registration Page
                </button>
              </td>
            </tr>
            <tr>
              <td>7</td>
              <td>Learning Material Dashboard</td>
              <td>-</td>
              <td>
                <button className="next-step-button" type="button" onClick={goToLearningMaterial} disabled={!selectedId}>
                  Go to Learning Material Dashboard
                </button>
              </td>
            </tr>
            <tr>
              <td>8</td>
              <td>LLM Training and Continuous Learning</td>
              <td>{lookupDisplay}</td>
              <td>
                <button className="next-step-button" type="button" onClick={() => navigate('/training')}>
                  Go to Training Page
                </button>
              </td>
            </tr>
            <tr>
              <td>9</td>
              <td>Learning Products Delivered</td>
              <td>Pre-generated courseware, textbooks, rubrics, presentations & videos</td>
              <td>
                <button className="next-step-button" type="button" onClick={() => navigate('/products')}>
                  Browse Products Delivered
                </button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  );
}
