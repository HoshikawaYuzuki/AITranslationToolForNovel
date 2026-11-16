# BASI — Architecture Decision Records

## ADR-001: Modular Monolith over Microservices

**Status**: Accepted  
**Date**: 2024-01-01

**Context**: BASI is a small-team project that needs clear module boundaries without operational complexity.

**Decision**: Use a Modular Monolith — separate .NET projects per domain module, composed in a single deployable API.

**Rationale**:
- Single deployment unit simplifies hosting and debugging
- Module boundaries enforced by project references
- Can be extracted to microservices later if needed
- Shared database with per-module table ownership

---

## ADR-002: PostgreSQL as Single Database

**Status**: Accepted  
**Date**: 2024-01-01

**Context**: Need a reliable database for user data, translations, and job queue.

**Decision**: Use PostgreSQL as the sole database engine.

**Rationale**:
- Mature, well-supported by EF Core via Npgsql
- JSONB columns for flexible data (story memory, glossaries)
- Can serve as job queue backend (avoids adding Redis/RabbitMQ)
- Free and widely available (Docker, managed services)

---

## ADR-003: Provider Adapter Pattern for AI

**Status**: Accepted  
**Date**: 2024-01-01

**Context**: BASI must support multiple AI providers (Gemini, OpenAI, possibly others) and a mock for testing.

**Decision**: Define an `IAiProvider` interface in `Basi.Core`. Each provider implements it. Selection is via DI registration.

**Rationale**:
- Clean abstraction — modules depend on interface, not implementation
- MockAiProvider enables deterministic unit testing
- New providers added without touching existing code
- Provider can be swapped per-request in the future

---

## ADR-004: BYOK with RAM-Only Key Storage

**Status**: Accepted  
**Date**: 2024-01-01

**Context**: Users must provide their own API keys. Storing keys in a database creates security liability.

**Decision**: API keys are held in server memory only (e.g., in-memory cache or session). Never persisted to disk, database, or logs.

**Rationale**:
- Eliminates stored-credential breach risk
- Users re-enter keys per session (acceptable UX tradeoff)
- Simplifies compliance — no encrypted-at-rest key management needed
- Keys are scoped to the user's session lifetime

---

## ADR-005: Background Jobs with PostgreSQL-Backed Queue

**Status**: Accepted  
**Date**: 2024-01-01

**Context**: Translation jobs are long-running and must survive server restarts. Need a job queue without adding infrastructure.

**Decision**: Use PostgreSQL as the job queue backend. Jobs are rows in a table with state machine transitions.

**Rationale**:
- No additional infrastructure (Redis, RabbitMQ) required
- Jobs persist across restarts
- State machine (Created → Queued → Processing → Completed/Failed) is queryable
- PostgreSQL advisory locks can prevent double-processing
- Good enough for expected scale (tens of concurrent jobs, not thousands)
