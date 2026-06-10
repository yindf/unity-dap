# Auto-Inject MCP Editor Scripts into Unity Projects

## Problem

The MCP debug adapter communicates with Unity Editor through a TCP control server (`McpPlayModeController.cs`). When the adapter targets a **new Unity project** that lacks this Editor script, control commands (`enterPlay`, `runTests`, `status`) fail because no TCP server is listening.

Today, `McpPlayModeController.cs` only exists in the E2E test fixture at `unity-debug-adapter.E2ETests/unity_test_project_2022_3/Assets/Editor/`. It must be manually copied into each target project.

## Solution

Automatically inject `McpPlayModeController.cs` into the target Unity project's `Assets/Editor/` directory during the `Attach` flow, then wait for Unity to compile and activate the controller before proceeding.

## Design Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Injection trigger | Automatic, inside `Attach()` before `ResolveUnityProcess()` | Zero manual steps; AI agent just calls prepare/start/attach as before |
| Cleanup | Do not remove on detach/cleanup | Avoids re-injection overhead on re-attach; file is small and harmless |
| Conflict handling | Content comparison + skip | Avoids duplicate injection; overwrites if content differs (version upgrade) |
| Injection scope | Only `McpPlayModeController.cs` | Minimal — only the file needed for MCP control commands |
| Template storage | Embedded resource in adapter assembly | Version-consistent, zero deployment dependencies |

## Architecture

### Flow

```
Attach() called
  → EnsureEditorScripts(projectPath)
    → File missing    → create Assets/Editor/, write template, return true
    → File exists, same content → return false
    → File exists, different    → overwrite + log, return true
  → ResolveUnityProcess()
  → if (injected && Unity is alive):
      WaitControlPortReady(pid)
        → Poll TCP connect to 57000 + pid%1000
        → On connect: send {"command":"status"}, verify {"ok":true,...}
        → Interval 500ms, timeout = startupTimeoutSeconds (default 90s)
        → Timeout → throw with hint to check Unity compilation errors
  → StartAdapterClient()
  → ...existing attach retry loop...
```

### File Organization

```
unity-debug-adapter/
├── EditorTemplates/
│   └── McpPlayModeController.cs    ← single source of truth
├── McpServer.cs
├── unity-debug-adapter.csproj
└── ...
```

### csproj Change

```xml
<EmbeddedResource Include="EditorTemplates\McpPlayModeController.cs">
  <LogicalName>McpPlayModeController.cs</LogicalName>
</EmbeddedResource>
```

`EmbeddedResource` items are excluded from compilation. The template is read at runtime via `Assembly.GetManifestResourceStream("McpPlayModeController.cs")`.

## Code Changes

### 1. `McpDebugSession.EnsureEditorScripts(string projectPath) : bool`

- Read embedded resource `McpPlayModeController.cs` into a string.
- Compute target path: `projectPath/Assets/Editor/McpPlayModeController.cs`.
- If file does not exist: create `Assets/Editor/` directory, write file, `Logger.LogInfo`, return `true`.
- If file exists: read and compare content (exact string equality). Same → return `false`. Different → overwrite, `Logger.LogInfo("MCP Editor script updated: {path}")`, return `true`.
- On IO errors: log warning and return `false` (non-fatal — the attach flow can still proceed if the controller is already present from a prior run).

### 2. `McpDebugSession.WaitControlPortReady(int pid, double timeoutSeconds)`

- Compute `controlPort = 57000 + Math.Abs(pid % 1000)`.
- Loop until `DateTime.UtcNow` exceeds deadline:
  - Try `new TcpClient().Connect("127.0.0.1", controlPort)`.
  - On success: send `{"command":"status"}\n`, read response line, parse JSON, check `ok == true`.
  - If verified: `Logger.LogInfo("MCP Editor controller active on port {port}")`, return.
  - On any failure: `Thread.Sleep(500)`, retry.
- On timeout: throw `TimeoutException` with message indicating the port and suggesting checking Unity console for compilation errors.

### 3. `McpDebugSession.Attach()` modification

Insert before `ResolveUnityProcess`:
```csharp
var injected = EnsureEditorScripts(m_ProjectPath);
```

Insert after `ResolveUnityProcess`, before `StartAdapterClient`:
```csharp
if (injected && IsUnityProcessAlive())
  WaitControlPortReady(UnityPid, m_StartupTimeoutSeconds);
```

If Unity was not running before attach (started by `ResolveUnityProcess`), the controller will be compiled during Unity's first load — no extra wait needed because the existing startup retry loop already handles this.

### 4. `unity-debug-adapter.csproj` modification

Add `EmbeddedResource` entry for `EditorTemplates\McpPlayModeController.cs` with `LogicalName`.

### 5. E2E test project

The manual copy at `unity_test_project_2022_3/Assets/Editor/McpPlayModeController.cs` should be removed. The E2E tests' `prepare` step will trigger automatic injection, exercising the real injection path. If existing tests depend on the file being present before Unity starts, add a pre-test setup step that calls the injection logic directly, or copy from `EditorTemplates/` during build.

### 6. Template synchronization

`EditorTemplates/McpPlayModeController.cs` is the **single source of truth**. The E2E fixture no longer maintains a separate copy. Any future changes to the controller are made in `EditorTemplates/` only.

## Error Handling

| Scenario | Behavior |
|---|---|
| Template resource not found in assembly | Log error, skip injection (non-fatal). Attach proceeds; control commands may fail later. |
| `Assets/Editor/` directory creation fails | Log warning, return `false`. |
| File write fails (permissions, disk) | Log warning, return `false`. |
| Unity already running, injection triggers recompile but fails | `WaitControlPortReady` times out → clear error message pointing to Unity compilation errors. |
| File exists with identical content | Skip (no-op). |
| File exists with different content | Overwrite + log. If Unity is running, this triggers a recompile. |

## Testing

- Unit-level: test `EnsureEditorScripts` with a temp directory (file creation, skip on match, overwrite on diff).
- E2E: existing test flow exercises injection implicitly after removing the manual copy from the fixture.
- Manual verification: point MCP at a fresh Unity project, call `prepare`, confirm controller activates and `enterPlay` works.
