# BASI — Test Plan

## Testing Strategy

| Layer | Framework | Database? | Description |
| ----- | --------- | --------- | ----------- |
| Unit | xUnit + Moq | No | Core logic, providers, models |
| Integration | xUnit + WebApplicationFactory | Yes (test container) | API endpoints, EF Core queries |
| E2E | Playwright (future) | Yes | Full user flows through UI |

## Unit Test Categories

### MockAiProvider Tests (`Basi.Tests.Unit`)

| Test | Scenario |
| ---- | -------- |
| `Test_Success_ReturnsValidResponse` | Mock returns well-formed AiResponse with realistic token counts |
| `Test_Error_ThrowsAiProviderError` | Mock throws AiProviderError with specified ErrorCode |
| `Test_Timeout_ThrowsTimeoutException` | Mock delays then throws TimeoutException |
| `Test_RateLimited_ThrowsWithCorrectCode` | Mock throws AiProviderError(RateLimited) |
| `Test_InvalidOutput_ReturnsUnexpectedContent` | Mock returns malformed/unexpected content |
| `Test_PartialResponse_MissingSegmentIds` | Mock returns response missing expected segment IDs |

### Health Check Tests

| Test | Scenario |
| ---- | -------- |
| Health endpoint returns correct JSON shape | Verify `status`, `timestamp`, `version`, `database` fields |
| Health endpoint returns `"disconnected"` when DB is down | No database connection available |

### Future Unit Tests

- Translation segmentation logic
- Job state machine transitions
- Authorization policy checks
- Request validation

## Integration Test Approach

Integration tests use `WebApplicationFactory<Program>` with a test PostgreSQL instance:

1. **Setup**: Spin up a test database (Testcontainers or dedicated test DB)
2. **Seed**: Apply migrations and insert test data
3. **Execute**: Make HTTP requests through the test server
4. **Assert**: Verify responses and database state
5. **Cleanup**: Reset database between tests

### Integration Test Scenarios (Future)

- Health check with live database returns `"connected"`
- CRUD operations on projects/chapters
- Authentication flow with test tokens
- Translation job lifecycle end-to-end

## What Needs a Database

| Needs DB | Doesn't Need DB |
| -------- | ---------------- |
| EF Core repository tests | MockAiProvider tests |
| Health check (connected) | AiRequest/AiResponse validation |
| Job queue operations | State machine logic |
| User data queries | Provider selection logic |
| Migration verification | Utility/helper functions |

## Running Tests

```bash
# All tests
dotnet test

# Unit tests only
dotnet test tests/Basi.Tests.Unit

# Integration tests (requires PostgreSQL)
dotnet test tests/Basi.Tests.Integration

# With coverage
dotnet test --collect:"XPlat Code Coverage"
```
