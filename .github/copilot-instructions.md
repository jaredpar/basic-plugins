# Copilot Instructions

## Build

```powershell
dotnet build Pipeline.slnx
```

Both `Pipeline` and `Pipeline.Mcp` are packaged as `dotnet` tools (`PackAsTool`). Use `./dogfood.ps1` to build, pack, install the plugin locally, and launch Copilot CLI with it loaded.

## Architecture

This repo produces a **Copilot CLI plugin** for triaging .NET pipeline failures across AzDO and Helix. There are three projects in the solution:

- **Pipeline.Core** — Shared client library. Contains `AzdoClient` (Azure DevOps REST API) and `HelixClient` (Kusto queries against the `engineeringdata` database on `engsrvprod`). Both clients defer authentication until first use via `TokenCredential`.
- **Pipeline.Mcp** — MCP stdio server that exposes the clients as MCP tools. This is what the Copilot CLI plugin runs. The server must start without blocking on auth — client construction is synchronous and token acquisition happens lazily on first tool invocation.
- **Pipeline** — Standalone CLI with subcommands (`helix workitems`, `helix console`, `azdo builds`, etc.) for direct terminal use.

The plugin definition lives in `plugins/basic-triage-mcp/` and includes:
- `plugin.json` — MCP server configuration (launched via `dotnet dnx`)
- `agents/` — Prompt-driven agents (e.g., `roslyn-health-report`)
- `skills/` — Domain knowledge documents teaching the AI about AzDO/Helix relationships

## Conventions

- **Auth**: All Azure auth goes through `PipelineUtils.CreateCredential()` which returns a `DefaultAzureCredential` scoped to the Microsoft tenant. Users authenticate via `az login`. VPN is required for Kusto access.
- **MCP server startup**: The MCP server must not block on auth or network calls during startup. Register clients synchronously in DI; defer token acquisition to first use.
- **Client factory pattern**: Both `AzdoClient` and `HelixClient` expose a sync `Create(TokenCredential)` method (preferred for MCP DI registration) and an async `CreateAsync` (convenience wrapper returning `Task.FromResult`).
- **Target framework**: `net10.0`.

## Coding Conventions

- **Private fields**: Start the field name with an underscore (_).
- **Sealed classes**: Prefer `sealed` for classes that are not intended to be inherited.
- **Async methods**: Use `Async` suffix for methods returning `Task` or `Task<T>`.
- **JSON serialization**: Use `JsonPropertyName` attributes to map JSON properties to C# properties.
- **Immutable types**: Use `init` accessors for properties that should be set only during object initialization.

