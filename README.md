# Education Training Development Platform (ETDP)

An intelligent, full-stack educational and vocational training management platform built with ASP.NET Core, React, and Google AI technologies.

## AI Intelligence Architecture

- **Google Gemini**: Powers multimodal reasoning, automated video storyboard generation, lesson content generation, and curriculum alignment.
- **Gemma 4:26B**: The primary specialist AI persona for deep subject-matter compilation, curriculum analysis, and step-by-step topic teaching.
- **Mira**: The in-app lecturer companion, workflow navigator, and operator assistant.
- **Antigravity AI Continuity**: Long-term state tracking, qualification mapping, and structured system continuity logs.

## Core Capabilities

- **Interactive Curriculum Lifecycle**: Qualifications, Subjects, Topics, Outcomes, and Lesson Plans capture and verification against QCTO benchmarks.
- **Learning Products Delivered Portfolio**:
  - Interactive in-app gallery at `/products` and standalone website showcase.
  - Full suite of delivered courseware artifacts: Learner Guides, Student Workbooks, Summative Assessments & Memoranda, Workplace Logbooks, Lesson Plans, Classroom Presentation Slides, Alignment Matrices, and System Overviews.
- **Text-to-Video Instructional Studio**:
  - AI-assisted pedagogical storyboard generation with Google Gemini.
  - High-performance, accelerated client-side video encoding (`.webm`).
  - Slide frame rendering with topic badges, technical diagrams, visual direction cues, and live progress bars.
  - Synchronized subtitle (`.srt`) and production shot list (`.csv`) export.
  - Voiceover narration preview with Google Gemini TTS and browser speech synthesis.
- **Lecturer Toolkit**: Document generation, learner guides, workbooks, summative assessments, and PowerPoint slides.
- **Unified Self-Contained Server**: Hosts both the REST API and the React SPA on a single port for zero-dependency distribution.

## Web Presentation & Documentation

- **Plain Simple Website**: A clean, accessible, lightweight website is included in [`index.html`](./index.html) at the root of the repository.
- **GitHub Pages Ready**: Because this repository's branch is `etd.github.io`, pushing to GitHub allows instant hosting on GitHub Pages at `https://ongalali.github.io/etdp` or `https://etd.github.io`.

## Quick Start & Distribution

### Option 1: Standalone Unified Server (Recommended for Distribution)
Run the unified server launcher without needing Node.js or Vite at runtime:
```cmd
launch_server.bat
```
This automatically boots the standalone server at `http://localhost:5299` and opens `http://localhost:5299/qualifications` in your default browser.

### Option 2: Developer Mode (Hot-Reloading)
Run backend and frontend in separate developer terminals:
```cmd
launch_etdp.bat
```

### Option 3: Package Portable Distribution
To produce a portable distribution package ready for zip or deployment:
```cmd
publish_distribution.bat
```
Output will be generated in `distribution\etdp_server\`.

## Repository

- Remote: `https://github.com/ONGALALI/etdp.git`
- Settings: `https://github.com/ONGALALI/etdp/settings`
