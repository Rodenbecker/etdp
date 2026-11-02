import React from 'react';
import { Routes, Route, Navigate } from 'react-router-dom';

import ErrorBoundary from './components/ErrorBoundary';
import ErrorFallback from './components/ErrorFallback';
import RequireWorkflow from './components/RequireWorkflow';
import GlossaryAutoTagger from './components/GlossaryAutoTagger';
import ScriptErrorWarning from './components/ScriptErrorWarning';

import RequireQualification from './pages/RequireQualification';

import Dashboard from './pages/Dashboard';
import MainMenuPage from './pages/MainMenuPage';
import MainPage from './pages/MainPage';
import DemographicsPage from './pages/DemographicsPage';
import CurriculumPhasesPage from './pages/CurriculumPhasesPage';
import SubjectsPage from './pages/SubjectsPage';
import SubjectsListPage from './pages/SubjectsListPage';
import SubjectsCapturePage from './pages/SubjectsCapturePage';
import TopicsPage from './pages/TopicsPage';
import OutcomesPage from './pages/OutcomesPage';
import TopicsListPage from './pages/TopicsListPage';
import LecturerToolkitPage from './pages/LecturerToolkitPage';
import GraphsPage from './pages/GraphsPage';
import LearningMaterialPage from './pages/LearningMaterialPage';
import AIAgent from './pages/AIAgent';
import AgentPlaygroundPage from './pages/AgentPlaygroundPage';
import LearningMaterialSchedulePage from './pages/LearningMaterialSchedulePage';
import LearningMaterialRolloutPage from './pages/LearningMaterialRolloutPage';
import LearningMaterialLearnerGuidePage from './pages/LearningMaterialLearnerGuidePage';
import LearningMaterialSummativeAssessmentPage from './pages/LearningMaterialSummativeAssessmentPage';
import LearningMaterialWorkbookPage from './pages/LearningMaterialWorkbookPage';
import LearningMaterialSummativeMemorandaPage from './pages/LearningMaterialSummativeMemorandaPage';
import LearningMaterialWorkbookMemorandaPage from './pages/LearningMaterialWorkbookMemorandaPage';
import LearningMaterialLearnerRegistrationPage from './pages/LearningMaterialLearnerRegistrationPage';
import LearningMaterialTemplateUploadsPage from './pages/LearningMaterialTemplateUploadsPage';
import LearningMaterialProgressReportPage from './pages/LearningMaterialProgressReportPage';
import LearningMaterialLogbookPage from './pages/LearningMaterialLogbookPage';
import LearningMaterialFlowDiagramsPage from './pages/LearningMaterialFlowDiagramsPage';
import LearningMaterialSlidesPage from './pages/LearningMaterialSlidesPage';
import QualificationsPage from './pages/QualificationsPage';
import QualificationReview from './pages/QualificationReview';
import DemographicsReview from './pages/DemographicsReview';
import PhasesReview from './pages/PhasesReview';
import SubjectsReview from './pages/SubjectsReview';
import TopicsReview from './pages/TopicsReview';
import ContentBuilderPage from './pages/ContentBuilderPage';
import LibraryManagerPage from './pages/LibraryManagerPage';
import LessonPlanReview from './pages/LessonPlanReview';

import LearnerGuidePage from './pages/LearnerGuidePage';
import WorkbookPage from './pages/WorkbookPage';
import PowerPointSlidesPage from './pages/PowerPointSlidesPage';
import TextToVideoEditorPage from './pages/TextToVideoEditorPage';
import Wan21Page from './pages/Wan21Page';
import LecturerAssistancePage from './pages/LecturerAssistancePage';
import ProjectRolloutPlanPage from './pages/ProjectRolloutPlanPage';
import AssessmentCompliancePage from './pages/AssessmentCompliancePage';
import LearnerRegistrationPage from './pages/LearnerRegistrationPage';
import LearnerProgressReportPage from './pages/LearnerProgressReportPage';
import WorkExperienceLogbookPage from './pages/WorkExperienceLogbookPage';
import AutomationJobsPage from './pages/AutomationJobsPage';
import SystemDiagnosticsPage from './pages/SystemDiagnosticsPage';
import TrainingPage from './pages/TrainingPage';
import UserGuidePage from './pages/UserGuidePage';
import QualityCouncilCurriculaPage from './pages/QualityCouncilCurriculaPage';
import ActivationPage from './pages/ActivationPage';
import RequireActivation from './pages/RequireActivation';
import AgentGovernancePage from './pages/AgentGovernancePage';
import ProductsPage from './pages/ProductsPage';

const App = () => (
    <ErrorBoundary fallback={ErrorFallback}>
        <GlossaryAutoTagger />
        <ScriptErrorWarning />
        <Routes>
            <Route path="/activation" element={<ActivationPage />} />
            <Route path="/training" element={<TrainingPage />} />
            <Route path="/llm-training" element={<TrainingPage />} />
            <Route path="/llm-assessment" element={<TrainingPage initialPanel="assessment" />} />
            <Route path="/playground/training" element={<TrainingPage />} />
            <Route path="/playground/assessment" element={<TrainingPage initialPanel="assessment" />} />
            <Route path="/" element={<RequireActivation><Dashboard /></RequireActivation>}>
                <Route path="main-menu" element={<MainMenuPage />} />
                <Route path="main" element={<MainPage />} />
                <Route path="qualification-review" element={<QualificationReview />} />

                <Route path="demographics" element={
                    <RequireQualification><DemographicsPage /></RequireQualification>
                } />
                <Route path="quality-council-curricula" element={
                    <RequireQualification><QualityCouncilCurriculaPage /></RequireQualification>
                } />
                <Route path="demographics-review" element={
                    <RequireQualification><DemographicsReview /></RequireQualification>
                } />

                <Route path="phases" element={
                    <RequireQualification><RequireWorkflow pageKey="phases"><CurriculumPhasesPage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="phases-review" element={
                    <RequireQualification><RequireWorkflow pageKey="phases"><PhasesReview /></RequireWorkflow></RequireQualification>
                } />

                <Route path="subjects" element={
                    <RequireQualification><RequireWorkflow pageKey="subjects"><SubjectsPage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="subjects/capture" element={
                    <RequireQualification><RequireWorkflow pageKey="subjects-capture"><SubjectsCapturePage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="subjects-review" element={
                    <RequireQualification><RequireWorkflow pageKey="subjects"><SubjectsReview /></RequireWorkflow></RequireQualification>
                } />

                <Route path="topics" element={
                    <RequireQualification><RequireWorkflow pageKey="topics"><TopicsPage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="outcomes" element={
                    <RequireQualification><RequireWorkflow pageKey="outcomes"><OutcomesPage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="topics-review" element={
                    <RequireQualification><RequireWorkflow pageKey="topics-review"><TopicsReview /></RequireWorkflow></RequireQualification>
                } />


                <Route path="qualifications" element={<QualificationsPage />} />
                <Route path="subjects-list" element={
                    <RequireQualification><RequireWorkflow pageKey="subjects-list"><SubjectsListPage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="topics-list" element={
                    <RequireQualification><RequireWorkflow pageKey="topics-list"><TopicsListPage /></RequireWorkflow></RequireQualification>
                } />

                <Route path="lecturer-toolkit" element={
                    <RequireQualification><RequireWorkflow pageKey="lecturer-toolkit"><LecturerToolkitPage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="library" element={
                    <RequireQualification><RequireWorkflow pageKey="library"><LibraryManagerPage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="content-builder/:id" element={
                    <RequireQualification><RequireWorkflow pageKey="content-builder"><ContentBuilderPage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="lesson-plan-review" element={
                    <RequireQualification><RequireWorkflow pageKey="lesson-plan-review"><LessonPlanReview /></RequireWorkflow></RequireQualification>
                } />
                <Route path="graphs" element={<GraphsPage />} />
                <Route path="learning-material" element={
                    <RequireQualification><RequireWorkflow pageKey="learning-material"><LearningMaterialPage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="learning-material/schedule" element={
                    <RequireQualification><RequireWorkflow pageKey="learning-material"><LearningMaterialSchedulePage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="learning-material/rollout-plan" element={
                    <RequireQualification><RequireWorkflow pageKey="learning-material"><LearningMaterialRolloutPage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="learning-material/learner-guide" element={
                    <RequireQualification><RequireWorkflow pageKey="learning-material"><LearningMaterialLearnerGuidePage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="learning-material/summative-assessment" element={
                    <RequireQualification><RequireWorkflow pageKey="learning-material"><LearningMaterialSummativeAssessmentPage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="learning-material/workbook" element={
                    <RequireQualification><RequireWorkflow pageKey="learning-material"><LearningMaterialWorkbookPage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="learning-material/summative-memoranda" element={
                    <RequireQualification><RequireWorkflow pageKey="learning-material"><LearningMaterialSummativeMemorandaPage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="learning-material/workbook-memoranda" element={
                    <RequireQualification><RequireWorkflow pageKey="learning-material"><LearningMaterialWorkbookMemorandaPage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="learning-material/learner-registration" element={
                    <RequireQualification><RequireWorkflow pageKey="learning-material"><LearningMaterialLearnerRegistrationPage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="learning-material/progress-report" element={
                    <RequireQualification><RequireWorkflow pageKey="learning-material"><LearningMaterialProgressReportPage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="learning-material/logbook" element={
                    <RequireQualification><RequireWorkflow pageKey="learning-material"><LearningMaterialLogbookPage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="learning-material/template-uploads" element={
                    <RequireQualification><RequireWorkflow pageKey="learning-material"><LearningMaterialTemplateUploadsPage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="learning-material/flow-diagrams" element={
                    <RequireQualification><RequireWorkflow pageKey="learning-material"><LearningMaterialFlowDiagramsPage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="learning-material/slides" element={
                    <RequireQualification><RequireWorkflow pageKey="learning-material"><LearningMaterialSlidesPage /></RequireWorkflow></RequireQualification>
                } />
                <Route path="print-menu" element={<Navigate to="/learning-material" replace />} />
                <Route path="exports" element={<Navigate to="/learning-material" replace />} />
                <Route path="ai-agent" element={<Navigate to="/qualia/mira" replace />} />
                <Route path="qualia" element={<Navigate to="/qualia/mira" replace />} />
                <Route path="qualia/mira" element={<AIAgent agentMode="mira" />} />
                <Route path="qualia/gemma" element={<AIAgent agentMode="gemma" />} />
                <Route path="qualia/qwen" element={<Navigate to="/qualia/gemma" replace />} />
                <Route path="playground" element={<Navigate to="/playground/mira" replace />} />
                <Route path="playground/mira" element={<AgentPlaygroundPage agentMode="mira" />} />
                <Route path="playground/gemma" element={<AgentPlaygroundPage agentMode="gemma" />} />
                <Route path="playground/qwen" element={<Navigate to="/playground/gemma" replace />} />
                <Route path="playground/training" element={<TrainingPage />} />
                <Route path="playground/assessment" element={<TrainingPage initialPanel="assessment" />} />

                <Route path="learner-guide-export" element={<LearnerGuidePage />} />
                <Route path="workbook-export" element={<WorkbookPage />} />
                <Route path="powerpoint-slides-export" element={<PowerPointSlidesPage />} />
                <Route path="electric-book-export" element={<Navigate to="/learning-material" replace />} />
                <Route path="lecturer-assistance" element={
                    <RequireQualification><LecturerAssistancePage /></RequireQualification>
                } />
                <Route path="text-to-video-editor" element={
                    <RequireQualification><TextToVideoEditorPage /></RequireQualification>
                } />
                <Route path="wan-2-1" element={
                    <RequireQualification><Wan21Page /></RequireQualification>
                } />
                <Route path="repo-integration-hub" element={<Navigate to="/learning-material" replace />} />
                <Route path="project-rollout-plan" element={<ProjectRolloutPlanPage />} />
                <Route path="assessment-compliance" element={<AssessmentCompliancePage />} />
                <Route path="learner-registration" element={<LearnerRegistrationPage />} />
                <Route path="learner-progress-report" element={
                    <RequireQualification><LearnerProgressReportPage /></RequireQualification>
                } />
                <Route path="work-experience-logbook" element={<WorkExperienceLogbookPage />} />
                <Route path="automation-jobs" element={<AutomationJobsPage />} />
                <Route path="system-diagnostics" element={<SystemDiagnosticsPage />} />
                <Route path="training" element={<TrainingPage />} />
                <Route path="llm-training" element={<TrainingPage />} />
                <Route path="llm-assessment" element={<TrainingPage initialPanel="assessment" />} />
                <Route path="user-guide" element={<UserGuidePage />} />
                <Route path="agent-governance" element={<AgentGovernancePage />} />
                <Route path="products" element={<ProductsPage />} />
                <Route path="learning-products" element={<Navigate to="/products" replace />} />
            </Route>
        </Routes>
    </ErrorBoundary>
);

export default App;
