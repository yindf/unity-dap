# Unity Debug Adapter

Unity Debug Adapter is a Mono-based Unity debugger with two entry points:

- A standard Debug Adapter Protocol (DAP) server for editors.
- A stdio MCP server for coding agents and LLM tools.

The MCP mode is the main agent-facing workflow. It lets an agent attach to a
running Unity Editor, set breakpoints, enter Play Mode, wait for a stop, inspect
stack frames and variables, continue or step, and cleanly detach without mixing
debug protocol traffic into the MCP stream.

> [!IMPORTANT]
> IL2CPP debugging is not supported. This adapter targets the Mono scripting
> backend used by the Unity Editor and Mono players with debugging enabled.

This project is a fork of the deprecated
[vscode-unity-debug][vscode-unity-debug] project, trimmed down for direct DAP
and MCP use.

## Agent Debugging With MCP

Run the adapter as an MCP server:

```bash
bin/Release/unity-debug-adapter.exe --mcp --log-level=info
```

Example MCP client configuration:

```json
{
  "mcpServers": {
    "unity-debug": {
      "command": "C:\\path\\to\\unity-dap\\bin\\Release\\unity-debug-adapter.exe",
      "args": ["--mcp", "--log-level=info"]
    }
  }
}
```

The MCP server starts a child copy of the same executable in normal DAP mode
for each debug session. That keeps MCP JSON-RPC and DAP messages on separate
stdio streams and lets the adapter process exit after `disconnect` while the
MCP server remains available for another attach.

Session logs are written to:

```text
bin/Release/mcp-logs/<sessionId>/
```

Each session directory contains adapter logs, Unity logs when Unity is started
by MCP, and a DAP transcript.

## MCP Tools

`unity_debug_session`

Manage the Unity debug session. Supported actions include `status`, `attach`,
`detach`, `disconnect`, `reset`, `cleanup`, `start`, and `prepare`.

Use `attach` with:

- `unityPid` when the target Unity process is known.
- no `unityPid` when one matching Unity Editor is already running.
- `projectPath` to prefer the Unity process opened for that project.

`unity_debug_breakpoints`

Set, add, remove, update, clear, or list source breakpoints. Breakpoint specs
support:

- `line`
- `column`
- `condition`
- `hitCondition`
- `logMessage`

`unity_debug_control`

Control the debuggee and collect state. Supported actions include `enterPlay`,
`enterPlayAndStop`, `wait`, `snapshot`, `continue`, `next`, `stepIn`,
`stepOut`, `pause`, `resumeUntilStopped`, and `runTests`.

`snapshot` returns the current stopped thread, top frame, stack frames, scopes,
locals, and optional expression evaluations.

`unity_debug_status`

Inspect the active session, breakpoint state, or diagnostic logs. Use
`action: "diagnose"` to include recent adapter and Unity log lines.

## Minimal Agent Flow

For the repository E2E fixture, an agent can attach, set a breakpoint, enter
Play Mode, and inspect variables with these tool calls.

Attach to a running Unity Editor or auto-select the single matching Editor:

```json
{
  "name": "unity_debug_session",
  "arguments": {
    "action": "attach"
  }
}
```

Set a breakpoint in the default fixture `TestScript.cs`:

```json
{
  "name": "unity_debug_breakpoints",
  "arguments": {
    "action": "set",
    "breakpoints": [{ "line": 22 }]
  }
}
```

Enter Play Mode:

```json
{
  "name": "unity_debug_control",
  "arguments": {
    "action": "enterPlay"
  }
}
```

Wait for a stopped event:

```json
{
  "name": "unity_debug_control",
  "arguments": {
    "action": "wait",
    "timeoutSeconds": 60
  }
}
```

Capture the current frame and evaluate expressions:

```json
{
  "name": "unity_debug_control",
  "arguments": {
    "action": "snapshot",
    "expressions": [
      "this",
      "m_Radius",
      "s_StaticBoolVar",
      "transform.position"
    ]
  }
}
```

Detach the DAP adapter client while keeping Unity open:

```json
{
  "name": "unity_debug_session",
  "arguments": {
    "action": "detach"
  }
}
```

Use `cleanup` when the agent owns the Unity process and should close it, or
when interrupted sessions need to be torn down explicitly.

## Agent-Oriented Behavior

The MCP server is designed for repeated tool-driven debugging loops:

- Re-attaching to an already running Unity Editor is supported.
- Detach keeps Unity process identity, port, tracked breakpoints, and session
  metadata until explicit cleanup or reset.
- The DAP child process is not reused after disconnect; a later attach starts a
  fresh DAP adapter client.
- Breakpoints can be supplied during attach or synchronized later with
  `unity_debug_breakpoints`.
- If multiple Unity Editors are running, pass `unityPid` or `projectPath` so the
  agent does not attach to the wrong process.

If Unity Hub's licensing helper is holding Unity's licensing mutex, pass
`killUnityHubLicensing: true` to session start or attach flows that launch
Unity. This kills `Unity Hub` and `Unity.Licensing.Client`, so use it only when
Unity exits during startup with a licensing conflict.

## Standard DAP Mode

Run without `--mcp` to start the standard debug adapter over stdin/stdout:

```bash
bin/Release/unity-debug-adapter.exe --log-level=info
```

This mode is intended for DAP clients such as Neovim integrations. For Neovim
setup, see [neovim-unity][unity-debugger-support].

## Build From Source

Clone the repository with submodules:

```bash
git clone --recurse-submodules https://github.com/walcht/unity-dap.git
cd unity-dap/
```

Build with dotnet:

```bash
dotnet build --configuration=Release unity-debug-adapter/unity-debug-adapter.csproj
```

If restore fails for `Microsoft.SymbolStore` or `Microsoft.FileFormats`, add
the legacy package source required by the Mono debugger dependency:

```bash
dotnet nuget add source 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json' -n "OutdatedPackages"
```

Then run the build again.

On Unix-like systems, if the generated executable is not marked executable:

```bash
chmod +x bin/Release/unity-debug-adapter.exe
```

## Developer Notes

The adapter communicates with Unity through `Mono.Debugger.Soft` from
`debugger-libs`. GDB support is not implemented.

The DAP implementation translates editor or MCP-driven DAP requests into
Mono.Debugger requests, then converts debugger events and responses back into
DAP responses.

```
    MCP client / DAP client
              |
              | stdin/stdout
              v
    Unity Debug Adapter
              |
              | TCP socket
              v
    Unity Editor / Mono runtime
```

## Why Not IL2CPP

IL2CPP is not supported because it is a C++ backend, not a managed Mono runtime.
Debugging generated C++ through C# source mapping would add substantial
complexity and is outside the scope of this adapter. Use a C++ debugger for
IL2CPP targets.

## License

MIT License. See LICENSE.txt.

[vscode-unity-debug]: https://github.com/Unity-Technologies/vscode-unity-debug
[unity-debugger-support]: https://github.com/walcht/neovim-unity#unity-debugger-support
