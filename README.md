# Unity Debug Adapter

A production-grade Mono-based Unity debugger with **first-class agent support** via the Model Context Protocol (MCP). The only debug adapter purpose-built for AI coding agents debugging Unity in real time.

> [!IMPORTANT]
> IL2CPP debugging is not supported. This adapter targets the Mono scripting
> backend used by the Unity Editor and Mono players with debugging enabled.

## Why This Exists

Coding agents can read and write code, but they **cannot observe runtime behavior**. They guess at bugs from stack traces and log output. Unity Debug Adapter closes this gap — an agent sets a breakpoint, enters Play Mode, inspects live variables and call stacks, steps through code line by line, and detaches, all through structured MCP tool calls. No UI, no editor extension, no human in the loop.

**The result: agents that can actually debug, not just edit.**

### What Makes It Agent-First

| Capability | Why It Matters for Agents |
|-----------|--------------------------|
| **Structured Markdown output** | Every tool returns clean, parseable Markdown — tables, bold key-values, code blocks. No raw JSON dumps. Agents consume context instantly. |
| **Stateless-ish tool calls** | Each tool is self-contained: `attach`, `set breakpoint`, `enter play`, `wait`, `snapshot`, `detach`. An agent can pick up any session mid-flow without ceremony. |
| **Safe re-attach** | Detach keeps Unity alive and preserves session metadata. Re-attach in one call — breakpoints auto-sync, no manual state rebuild. |
| **Built-in guardrails** | `stepOut` from the entry frame? Warning returned, not a crash. Non-user code frame? Suggestion provided. Timeouts on every wait. The adapter protects agents from themselves. |
| **Zero-config attach** | No `unityPid` needed if only one Editor is running. The adapter finds it. `start` launches Unity from Unity Hub automatically. |
| **Compact output** | A `status` call returns ~80 tokens. A `snapshot` returns ~150. Compare that to raw DAP JSON at 500+ tokens. More debugging context per context window. |
| **UTF-8 clean** | Em-dashes, CJK paths, special characters render correctly on every platform. No encoding surprises in agent context. |
| **Diagnostic companion** | `diagnose` surfaces adapter logs, Unity logs, and the full DAP transcript — the agent can self-troubleshoot without human help. |
| **Play Mode control** | Enter Play, pause, resume, run tests — all through a TCP control channel injected into the Unity project. The agent drives Unity like a test harness. |
| **Expression evaluation** | `snapshot` accepts arbitrary C# expressions. The agent can evaluate `transform.position`, `GetComponent<T>()`, or any property on the stopped frame. |

## Quick Start

### Build

```bash
git clone --recurse-submodules https://github.com/walcht/unity-dap.git
cd unity-dap
dotnet build --configuration=Release unity-debug-adapter/unity-debug-adapter.csproj
```

If restore fails for `Microsoft.SymbolStore` or `Microsoft.FileFormats`:

```bash
dotnet nuget add source 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json' -n "OutdatedPackages"
```

### MCP Server

```bash
bin/Release/unity-debug-adapter.exe --mcp --log-level=info
```

MCP client configuration (Claude Code, Cursor, etc.):

```json
{
  "mcpServers": {
    "UnityDAP": {
      "command": "C:\\path\\to\\unity-dap\\bin\\Release\\unity-debug-adapter.exe",
      "args": ["--mcp", "--log-level=info"]
    }
  }
}
```

Session logs are written to `bin/Release/mcp-logs/<sessionId>/`.

### Standard DAP Mode

Run without `--mcp` for DAP clients (Neovim, etc.):

```bash
bin/Release/unity-debug-adapter.exe --log-level=info
```

See [neovim-unity][unity-debugger-support] for Neovim setup.

## MCP Tools

### `unity_debug_session`

Manage the debug session lifecycle.

| Action | Description |
|--------|-------------|
| `status` | Show session state: attached/detached, PID, port, breakpoints, recent events |
| `start` | Launch Unity (if needed) and attach in one call |
| `attach` | Attach to a running Unity Editor (auto-detect or by PID) |
| `detach` | Release debugger, keep Unity running, preserve session for re-attach |
| `disconnect` | Same as detach with DAP disconnect semantics |
| `prepare` | Resolve Unity process and options without attaching |
| `reset` | Detach + cleanup in one call |
| `cleanup` | Tear down session and stop owned Unity process |

### `unity_debug_breakpoints`

Set and manage source breakpoints with full conditional/logpoint support.

| Action | Description |
|--------|-------------|
| `set` | Replace all breakpoints for a source file |
| `add` | Add breakpoints without clearing existing ones |
| `remove` | Remove specific lines |
| `update` | Move a breakpoint or change its condition/hit/log |
| `clear` | Remove all breakpoints for a source file |
| `list` | Show all tracked breakpoints across all sources |

Breakpoint specs support: `line`, `column`, `condition`, `hitCondition`, `logMessage`.

Unverified breakpoints show a **Warnings** section with the DAP diagnostic message.

### `unity_debug_control`

Drive execution and capture runtime state.

| Action | Description |
|--------|-------------|
| `enterPlay` | Enter Unity Play Mode via MCP control channel |
| `enterPlayAndStop` | Enter Play Mode + wait for breakpoint/stop in one call |
| `wait` | Wait for the next stopped event (breakpoint, step, pause) |
| `snapshot` | Capture stack frames, variables, and expression evaluations |
| `continue` | Resume all threads |
| `next` | Step over |
| `stepIn` | Step into |
| `stepOut` | Step out (with entry-frame protection) |
| `pause` | Pause execution |
| `resumeUntilStopped` | Continue then wait for next stop |
| `runTests` | Trigger Unity Test Runner (EditMode/PlayMode/All) |

`snapshot` accepts an `expressions` array to evaluate arbitrary C# on the stopped frame.

### `unity_debug_status`

Read-only diagnostic companion.

| Action | Description |
|--------|-------------|
| `status` | Same as `unity_debug_session status` |
| `breakpoints` | Breakpoint summary across all sources |
| `diagnose` | Full diagnostics: status + adapter log tail + Unity log tail + DAP transcript |

## Agent Workflow

A minimal debugging loop an agent can follow:

```
start → set breakpoints → enterPlayAndStop → snapshot → step/continue → detach
```

### Example: Attach, Break, Inspect, Detach

Attach to the running Unity Editor (auto-detected):

```json
{ "name": "unity_debug_session", "arguments": { "action": "start" } }
```

Set a breakpoint:

```json
{ "name": "unity_debug_breakpoints", "arguments": {
    "action": "set",
    "breakpoints": [{ "line": 22 }]
} }
```

Enter Play Mode and wait for the breakpoint to hit:

```json
{ "name": "unity_debug_control", "arguments": {
    "action": "enterPlayAndStop",
    "timeoutSeconds": 120
} }
```

Inspect variables and evaluate expressions:

```json
{ "name": "unity_debug_control", "arguments": {
    "action": "snapshot",
    "expressions": ["this", "m_Radius", "transform.position"]
} }
```

Detach (Unity keeps running, session preserved for re-attach):

```json
{ "name": "unity_debug_session", "arguments": { "action": "detach" } }
```

### Re-attach Without Re-entering Play

```json
{ "name": "unity_debug_session", "arguments": { "action": "attach" } }
```

Breakpoints auto-sync on re-attach. No need to set them again.

## Agent-Oriented Design

### Re-attach Resilience

The MCP server is designed for repeated attach/detach cycles against the same Unity Editor:

- **Detach ≠ destroy.** Unity PID, port, tracked breakpoints, and session metadata survive detach.
- **Auto-sync on re-attach.** All previously tracked breakpoints are sent to the new DAP adapter automatically.
- **Fresh DAP process each time.** The adapter child process exits after disconnect; a new one starts on the next attach. No stale state leaks across sessions.
- **Handshake timeout.** Stale Mono debugger sessions (from a previous adapter crash) are detected and retried automatically.

### Safety Guardrails

Agents operate without human supervision. The adapter protects against common mistakes:

- **stepOut from entry frame** → Returns a warning instead of terminating the debuggee.
- **Non-user code frames** → Suggests `continue` + `pause` instead of stepping into runtime internals.
- **Timeouts on every wait** → No hung sessions. Every blocking call has a configurable timeout.
- **UTF-8 everywhere** → Console encoding is forced to UTF-8 on Windows. No garbled paths or variable names.

### Token-Efficient Output

Every tool returns structured Markdown, not raw JSON. Typical token costs:

| Tool | Output | Est. Tokens |
|------|--------|------------|
| `status` | Session state, one-liner | ~60 |
| `set breakpoints` | Verified table | ~80 |
| `enterPlayAndStop` | Play + stopped + stack + variables | ~120 |
| `snapshot` | Stack + variables + expressions | ~150 |
| `stepIn / stepOut / next` | New position + stack | ~100 |
| `continue` | One-liner | ~15 |
| `diagnose` | Logs + transcript (configurable) | ~300–500 |

Compare to raw DAP JSON responses at 500–1000+ tokens each. The adapter does the formatting so the agent doesn't have to.

## Architecture

```
    MCP client / DAP client
              |
              | stdin/stdout
              v
    Unity Debug Adapter
              |
              | TCP socket (Mono Debugger Protocol)
              v
    Unity Editor / Mono runtime
```

The adapter communicates with Unity through `Mono.Debugger.Soft` from `debugger-libs`. The MCP server spawns a child DAP adapter process for each attach, keeping MCP and DAP traffic on separate stdio streams.

In MCP mode, a TCP control channel is injected into the Unity project (via `Assets/Editor/` scripts) for Play Mode entry, test execution, and other editor commands that require Unity API access.

## Developer Notes

- `debugger-libs/` contains the Mono debugger dependency tree (submodule).
- GDB support is not implemented.
- The DAP implementation translates editor or MCP-driven DAP requests into Mono.Debugger requests, then converts debugger events and responses back into DAP responses.

## Why Not IL2CPP

IL2CPP is a C++ backend, not a managed Mono runtime. Debugging generated C++ through C# source mapping would add substantial complexity and is outside the scope of this adapter. Use a C++ debugger for IL2CPP targets.

## License

MIT License. See LICENSE.txt.

[vscode-unity-debug]: https://github.com/Unity-Technologies/vscode-unity-debug
[unity-debugger-support]: https://github.com/walcht/neovim-unity#unity-debugger-support
