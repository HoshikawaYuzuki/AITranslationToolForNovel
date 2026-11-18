# BASI — AI Translation Tool for Novels

BASI is a web application that helps translate novels using AI providers. Users bring their own API keys (BYOK) to translate content with customizable prompts, story memory, and context-aware processing.

## Prerequisites

| Tool        | Version  | Notes                      |
| ----------- | -------- | -------------------------- |
| .NET SDK    | 9.0+     | Backend runtime            |
| Node.js     | 20 LTS+  | Frontend build             |
| pnpm        | 9+       | Package manager            |
| PostgreSQL  | 16+      | Database (or via Docker)   |
| Docker      | 24+      | Optional, for DB container |

## Quick Start

```bash
# 1. Clone the repository
git clone <repo-url> && cd AITranslationToolForNovel

# 2. Start PostgreSQL (via Docker)
docker compose up -d postgres

# 3. Run the backend
cd src/Basi.Api
dotnet run

# 4. Run the frontend (in a separate terminal)
cd src/frontend
pnpm install
pnpm dev
```

The API will be available at `http://localhost:5000` and the frontend at `http://localhost:5173`.

## Development Setup

### Backend

```bash
# Restore and build
dotnet build

# Run unit tests
dotnet test

# Database setup (EF Core migrations will be introduced in Task 2)
# cd src/Basi.Api && dotnet ef database update
```

### Frontend

```bash
cd src/frontend
pnpm install
pnpm dev        # Development server with HMR
pnpm build      # Production build
```

### Docker

```bash
docker compose up -d          # Start all services
docker compose up -d postgres # Start only PostgreSQL (maps host port 5433 -> container 5432)
docker compose down           # Stop all services
```

> **Note on PostgreSQL Port:** `docker-compose.yml` publishes PostgreSQL to host port `5433` (`5433:5432`) to prevent conflicts with any native PostgreSQL running on host port `5432`. Backend development configuration connects to `Host=127.0.0.1;Port=5433`.

## Architecture Overview

BASI uses a **Modular Monolith** architecture:

- **Basi.Api** — ASP.NET Core Web API entry point
- **Basi.Core** — Shared domain models and interfaces
- **Basi.Module.Identity** — Google OAuth authentication
- **Basi.Module.Translation** — AI provider integration & translation engine
- **Basi.Module.StoryMemory** — Story context & analysis
- **Frontend** — SvelteKit with TypeScript and TailwindCSS

AI providers are abstracted behind an `IAiProvider` interface using the **Provider Adapter Pattern**, allowing easy swapping between mock (testing) and real (production) implementations.

## License

TBD
