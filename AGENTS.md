# BASI — Agent Instructions

## Quick Reference

- **Backend**: ASP.NET Core 9, C#, EF Core, PostgreSQL
- **Frontend**: SvelteKit, TypeScript, TailwindCSS
- **Architecture**: Modular Monolith
- **AI Providers**: Provider Adapter Pattern with Mock/Real implementations

## Build & Test

```bash
# Backend
cd src/Basi.Api && dotnet build
dotnet test

# Frontend
cd src/frontend && pnpm install && pnpm dev

# Docker (PostgreSQL)
docker compose up -d postgres
```

## Key Rules

1. Backend is source of truth for all state
2. API keys: RAM only, never persisted
3. All data must have owner — enforce authorization on every endpoint
4. Mock Provider for tests, Real Provider for production
5. Do not modify `CONTRACTS.md` or `DECISIONS.md` without approval
6. Do not go beyond assigned task scope

## Project Structure

```
src/
  Basi.Api/                  — ASP.NET Core Web API (entry point)
  Basi.Core/                 — Shared domain models, interfaces, contracts
  Basi.Module.Identity/      — Authentication module (Google OAuth)
  Basi.Module.Translation/   — Translation module (AI provider integration)
  Basi.Module.StoryMemory/   — Story memory & analysis module
  frontend/                  — SvelteKit frontend app
tests/
  Basi.Tests.Unit/           — Unit tests
  Basi.Tests.Integration/    — Integration tests
docs/                        — Architecture, contracts, decisions, plans
docker-compose.yml           — PostgreSQL and supporting services
```
