# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run Commands

```bash
dotnet build                    # Build the project
dotnet run                      # Run the MCP server
dotnet test                     # Run all tests
dotnet test --filter "FullyQualifiedName~MethodName"  # Run a single test
```

### Publishing self-contained binaries

```bash
dotnet publish -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o ./publish
dotnet publish -c Release -r osx-x64 --self-contained -p:PublishSingleFile=true -o ./publish
```

## Architecture

This is a **Model Context Protocol (MCP) server** that exposes PostgreSQL database operations as tools for AI assistants. It communicates over **stdio** using the MCP protocol.

### Key files

- **Program.cs** — Entry point. Builds a generic host with MCP server services, stdio transport, and tool auto-discovery from the assembly. Redirects stdout to stderr (MCP protocol uses stdout for JSON-RPC messages). Merges config from `appsettings.json` and `~/.postgres-mcp-server-creds.json`.

- **PostgresDatabaseTools.cs** — All business logic. A static class decorated with `[McpServerToolType]` containing four `[McpServerTool]` methods:
  - `ExecuteQuery` — SELECT queries, returns JSON rows
  - `ExecuteNonQuery` — INSERT/UPDATE/DELETE/DDL, returns rows-affected count
  - `TestConnection` — Validates connectivity, returns server info and pool settings
  - `ListAvailableDatabases` — Lists configured database names with resilience metadata

### Resilience pattern

All database operations go through `ExecuteWithRetryAsync<T>` which provides:
- Up to 3 retry attempts for transient errors (connection failures, deadlocks, resource exhaustion)
- Exponential backoff: 500ms → 1s → 2s with random jitter
- Connection health validation (`SELECT 1`) before each operation
- Transient classification via `IsTransientException()` using PostgreSQL SQL state codes

### Configuration

Database credentials are loaded from `~/.postgres-mcp-server-creds.json` (not in repo):
```json
{
  "ConnectionStrings": {
    "dbname": "Host=...;Port=5432;Database=...;Username=...;Password=..."
  }
}
```

Connection strings are enhanced at runtime with pooling, keepalive, timeouts, and auto-prepare settings in `GetConnectionString()`.

### Error responses

All tool methods return structured JSON with `success`, and on failure: `error`, `sqlState`, `isTransient`, and `suggestion` fields.

## Project Settings

- **.NET 9.0**, C# 12.0, nullable enabled, **implicit usings disabled** (all usings must be explicit)
- Test framework: **xunit** — tests require a real PostgreSQL instance with credentials configured
- MCP SDK: `ModelContextProtocol` 0.3.0-preview.3
- PostgreSQL driver: `Npgsql` 8.0.5

## CI/CD

`.github/workflows/release.yml` — On push to `main`, builds macOS ARM64 and x64 self-contained binaries and creates a GitHub Release with the artifacts.
