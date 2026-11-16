# BASI — Master Plan

## Vision

BASI is an AI-powered novel translation tool. Users provide their own API keys (BYOK) to translate novels with context-aware processing, story memory, and customizable prompts — all through a modern web interface.

## Roadmap

| Task | Name | Description | Status |
| ---- | ---- | ----------- | ------ |
| 1 | **Foundation & Repository Bootstrap** | Solution structure, health check, mock AI provider, frontend scaffold, Docker Compose, documentation | ✅ Completed |
| 2 | **Authentication & User Management** | Google OAuth login, session management, user profiles, authorization middleware | ⏳ Planned |
| 3 | **AI Provider Integration & Translation** | Real AI provider adapters (Gemini/OpenAI), BYOK key management (RAM-only), translation engine with segmentation | ⏳ Planned |
| 4 | **Story Memory & Analysis** | Character/term extraction, glossary management, context injection into translation prompts | ⏳ Planned |
| 5 | **Project Management & UI** | Novel project CRUD, chapter management, translation workflow UI, progress tracking | ⏳ Planned |
| 6 | **Polish & Production Readiness** | Error handling, rate limiting, export, performance optimization, deployment config | ⏳ Planned |

## Current Focus

**Task 1 — Foundation** has been completed and verified. The solution skeleton, core contracts, CI-ready test suite, and development environment are established. Ready for **Task 2 — Authentication & TXT Upload**.
