# ETDP Repo Instructions

Read these files at the start of every session in this repo:
1. `gemini-startup.md`
2. `development.readme.md`
3. `SystemData/CodexContinuity/codex-continuity-latest.md` (or `SystemData/GeminiContinuity/gemini-continuity-latest.md`)

Repo and runtime:
- Active workspace: `D:\ETDP` (app root: `D:\ETDP\ETDP`)
- Frontend: `frontend` (React + Vite, bundled into `frontend/dist`)
- Backend: ASP.NET Core (.NET 8) + EF Core + SQLite
- Default standalone server URL: `http://localhost:5299`
- Persona & AI Engines:
  - Local Agent: Google Gemma 4:26B (`gemma4:26b`)
  - Cloud Platform: Google Gemini (`gemini-3.8-flash`, `gemini-3.8-flash-tts`, `imagen-3.0-generate-002`)
  - Engineering Partner: Google Gemini & Antigravity

Startup checklist:
1. Run `git status --short --branch` before editing.
2. Read the three files above before changing workflow or export logic.
3. For learner-guide work, inspect:
   - `Controllers/LearnerGuideController.cs`
   - `frontend/src/pages/LearnerGuidePage.jsx`
   - `frontend/src/pages/LearningMaterialLearnerGuidePage.jsx`
4. For knowledge-questionnaire work, inspect:
   - `Controllers/KnowledgeQuestionnaireController.cs`
   - `Services/KnowledgeQuestionnaireV1Service.cs`
   - `frontend/src/pages/KnowledgeQuestionnairePage.jsx`
   - `frontend/src/utils/knowledgeQuestionnaireV1.js`
5. For shared export/table formatting work, inspect:
   - `Controllers/WorkbookController.cs`
6. After code changes, run `dotnet build ETDP.csproj -nologo -p:UseAppHost=false`.
7. If routes, workflow, export formatting, or architecture changes, update:
   - `development.readme.md`
   - `gemini-startup.md`

Documentation intent:
- `development.readme.md` is the detailed operational knowledge base.
- `gemini-startup.md` is the concise app map for fast session pickup.
- `SystemData/CodexContinuity/codex-continuity-latest.md` is the generated runtime continuity snapshot.
