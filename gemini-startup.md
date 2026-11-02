# Google Gemini & Antigravity Startup Guide

Updated: November 2026.

## Purpose

This document provides the architectural and operational map for Google Gemini and Antigravity sessions in the ETDP repository.

## Application Architecture

- **Workspace Root**: `D:\ETDP`
- **Application Root**: `D:\ETDP\ETDP`
- **Backend Project**: `D:\ETDP\ETDP\ETDP.csproj` (ASP.NET Core / .NET 8)
- **Frontend App**: `D:\ETDP\ETDP\frontend` (React + Vite SPA)
- **Database**: SQLite (`D:\ETDP\ETDP\etdp.db`)
- **System Continuity**: `D:\ETDP\ETDP\SystemData\GeminiContinuity`

## Runtime Configuration

- **Backend**: ASP.NET Core (`http://localhost:5299`)
- **Frontend**: React SPA (`http://localhost:5173` in dev mode; served directly by ASP.NET Core on port `5299` in unified mode)
- **AI Stack**:
  - Google Gemini (`gemini-3.8-flash` / `gemini-3.5-flash-lite`)
  - Gemma 4:26B (`gemma4:26b` / `gemma-4-26b-a4b-it`)
  - Mira (Workflow & Lecturer Companion)

## Core Capabilities

1. **Curriculum Hierarchy**: SANS-compliant qualification breakdown, subjects, topics, outcomes, lesson plans.
2. **Text-To-Video Studio**: AI storyboard synthesis with Google Gemini, canvas video encoding, animated scene cards, subtitle export (`.srt`), and shotlist (`.csv`).
3. **Unified Server Distribution**:
   - `launch_server.bat`: One-click unified server boot (API + React frontend on `http://localhost:5299`).
   - `publish_distribution.bat`: Builds self-contained portable distribution.
