using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityDebugAdapter
{
  internal class McpServer
  {
    readonly Dictionary<string, McpDebugSession> m_Sessions = new Dictionary<string, McpDebugSession>();
    string m_ActiveSessionId;

    public void Start()
    {
      string line;
      while ((line = Console.In.ReadLine()) != null)
      {
        if (string.IsNullOrWhiteSpace(line))
          continue;

        JObject request;
        try
        {
          request = JObject.Parse(line);
        }
        catch (Exception e)
        {
          SendError(null, -32700, "Parse error: " + e.Message);
          continue;
        }

        var id = request["id"];
        var method = (string)request["method"];
        var parameters = request["params"] as JObject;

        try
        {
          if (id == null)
          {
            HandleNotification(method);
            continue;
          }

          var result = HandleRequest(method, parameters);
          SendResult(id, result);
        }
        catch (Exception e)
        {
          SendError(id, -32000, e.Message);
        }
      }

      foreach (var session in m_Sessions.Values.ToArray())
        session.Cleanup();
      m_Sessions.Clear();
    }

    void HandleNotification(string method)
    {
      if (method == "notifications/initialized")
        return;
    }

    JObject HandleRequest(string method, JObject parameters)
    {
      switch (method)
      {
        case "initialize":
          return new JObject
          {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JObject
            {
              ["tools"] = new JObject()
            },
            ["serverInfo"] = new JObject
            {
              ["name"] = "unity-debug-adapter",
              ["version"] = "0.1.0"
            }
          };
        case "ping":
          return new JObject();
        case "tools/list":
          return ListTools();
        case "tools/call":
          return CallTool(parameters ?? new JObject());
        case "resources/list":
          return new JObject { ["resources"] = new JArray() };
        case "prompts/list":
          return new JObject { ["prompts"] = new JArray() };
        default:
          throw new InvalidOperationException("Unsupported MCP method: " + method);
      }
    }

    JObject ListTools()
    {
      return new JObject
      {
        ["tools"] = new JArray
        {
          Tool("unity_debug_session", "Status, attach, detach, reset, start, or prepare Unity debug sessions.",
              Obj(
                Prop("action", "string", "status, attach, detach, reset, start, prepare, disconnect, cleanup. Default status."),
                Prop("projectPath", "string", "Unity project path. Defaults to the repository E2E fixture."),
                Prop("sourcePath", "string", "Default source file path for later breakpoint requests."),
                Prop("unityExe", "string", "Unity.exe path. Defaults to latest Unity Hub 2022.3 editor."),
                Prop("unityPid", "integer", "Existing Unity Editor process id for attach/prepare."),
                Prop("sessionId", "string", "Session id. Defaults to the active session."),
                Prop("lines", "array", "Breakpoint lines. Kept for compatibility; use breakpoints for conditions/logpoints."),
                BreakpointsProp("breakpoints", "Breakpoint specs: { line, column, condition, hitCondition, logMessage }."),
                Prop("startupTimeoutSeconds", "number", "Attach retry timeout. Default 90."),
                Prop("readyTimeoutSeconds", "number", "Target-ready wait after attach succeeds. Default 10."),
                Prop("requestTimeoutSeconds", "number", "DAP request timeout. Default 15."),
                Prop("killUnityHubLicensing", "boolean", "Stop Unity Hub and Unity.Licensing.Client before starting Unity. Use when licensing mutex blocks the editor."),
                Prop("setExceptionBreakpoints", "boolean", "Also send an empty setExceptionBreakpoints request. Default false.")
              )),
          Tool("unity_debug_breakpoints", "Set, add, remove, update, clear, or list source breakpoints.",
              Obj(
                Prop("action", "string", "set, add, remove, update, clear, list. Default set."),
                Prop("sessionId", "string", "Session id. Defaults to the active session."),
                Prop("sourcePath", "string", "Source file path. Defaults to fixture TestScript.cs."),
                Prop("lines", "array", "Breakpoint lines."),
                BreakpointsProp("breakpoints", "Breakpoint specs: { line, column, condition, hitCondition, logMessage }."),
                Prop("oldLine", "integer", "Existing breakpoint line for update."),
                Prop("newLine", "integer", "New breakpoint line for update."),
                Prop("condition", "string", "Condition expression for update."),
                Prop("hitCondition", "string", "Hit condition for update."),
                Prop("logMessage", "string", "Logpoint message/expression for update.")
              )),
          Tool("unity_debug_control", "Control execution: enter play, run tests, wait, snapshot, continue, next, step in/out, pause.",
              Obj(
                Prop("action", "string", "enterPlay, enterPlayAndStop, runTests, wait, snapshot, continue, next, stepIn, stepOut, pause, resumeUntilStopped. Default resumeUntilStopped."),
                Prop("sessionId", "string", "Session id. Defaults to the active session."),
                Prop("projectPath", "string", "Unity project path. Defaults to the session project path."),
                Prop("threadId", "integer", "Thread id. Defaults to current stopped thread."),
                Prop("timeoutSeconds", "number", "Stopped-event timeout. Default 60."),
                Prop("controlPort", "integer", "Unity MCP control port. Defaults to 57000 + unityPid % 1000."),
                Prop("controlTimeoutSeconds", "number", "Unity MCP control command timeout. Default 15."),
                ArrayProp("expressions", "string", "Expressions to evaluate in snapshots."),
                Prop("testMode", "string", "Unity Test Runner mode: EditMode, PlayMode, or All. Default EditMode."),
                Prop("testFilter", "string", "Optional Unity test name filter.")
              )),
          Tool("unity_debug_status", "Return status, breakpoints, or diagnostics for the active Unity debug session.",
              Obj(
                Prop("action", "string", "status, breakpoints, diagnose. Default status."),
                Prop("sessionId", "string", "Session id. Defaults to the active session."),
                Prop("sourcePath", "string", "Optional source path filter for breakpoints."),
                Prop("tailLines", "integer", "Number of log lines for diagnose. Default 30.")
              ))
        }
      };
    }

    JObject CallTool(JObject parameters)
    {
      var name = (string)parameters["name"];
      var args = parameters["arguments"] as JObject ?? new JObject();

      object result;
      switch (name)
      {
        case "unity_debug_session":
          result = ToolSession(args);
          break;
        case "unity_debug_breakpoints":
          result = ToolBreakpoints(args);
          break;
        case "unity_debug_control":
          result = ToolControl(args);
          break;
        case "unity_debug_start":
          result = ToolStart(args);
          break;
        case "unity_debug_attach":
          result = ToolAttach(args);
          break;
        case "unity_debug_run_flow":
          result = ToolRunFlow(args);
          break;
        case "unity_debug_prepare":
          result = ToolPrepare(args);
          break;
        case "unity_debug_enter_play_and_stop":
          result = WithSession(args, s => s.EnterPlayAndStop(args));
          break;
        case "unity_debug_resume_until_stopped":
          result = WithSession(args, s => s.ResumeUntilStopped(args));
          break;
        case "unity_debug_diagnose":
          result = WithSession(args, s => s.Diagnose(args));
          break;
        case "unity_debug_set_breakpoints":
          result = WithSession(args, s => s.SetBreakpoints(args));
          break;
        case "unity_debug_add_breakpoints":
          result = WithSession(args, s => s.AddBreakpoints(args));
          break;
        case "unity_debug_remove_breakpoints":
          result = WithSession(args, s => s.RemoveBreakpoints(args));
          break;
        case "unity_debug_update_breakpoint":
          result = WithSession(args, s => s.UpdateBreakpoint(args));
          break;
        case "unity_debug_clear_breakpoints":
          result = WithSession(args, s => s.ClearBreakpoints(args));
          break;
        case "unity_debug_list_breakpoints":
          result = WithSession(args, s => s.ListBreakpoints(args));
          break;
        case "unity_debug_enter_play_mode":
          result = WithSession(args, s => s.EnterPlayMode(args));
          break;
        case "unity_debug_wait_stopped":
          result = WithSession(args, s => s.WaitStopped(args));
          break;
        case "unity_debug_snapshot":
          result = WithSession(args, s => s.Snapshot(args));
          break;
        case "unity_debug_continue":
          result = WithSession(args, s => s.Continue(args));
          break;
        case "unity_debug_next":
        case "unity_debug_step_over":
          result = WithSession(args, s => s.Next(args));
          break;
        case "unity_debug_step_in":
          result = WithSession(args, s => s.StepIn(args));
          break;
        case "unity_debug_step_out":
          result = WithSession(args, s => s.StepOut(args));
          break;
        case "unity_debug_pause":
          result = WithSession(args, s => s.Pause(args));
          break;
        case "unity_debug_run_tests":
          result = WithSession(args, s => s.RunTests(args));
          break;
        case "unity_debug_disconnect":
          result = WithSession(args, s => s.Detach());
          break;
        case "unity_debug_status":
          result = ToolStatus(args);
          break;
        case "unity_debug_cleanup":
          result = ToolCleanup(args);
          break;
        default:
          throw new InvalidOperationException("Unknown tool: " + name);
      }

      return ToolText(result);
    }

    object ToolSession(JObject args)
    {
      var action = NormalizeAction(args, "status");
      switch (action)
      {
        case "status":
          return ToolStatus(args);
        case "start":
          return ToolStart(args);
        case "attach":
          return ToolAttach(args);
        case "prepare":
          return ToolPrepare(args);
        case "detach":
        case "disconnect":
          return WithSession(args, s => s.Detach());
        case "reset":
        case "cleanup":
          return ToolCleanup(args);
        default:
          throw new InvalidOperationException("Unknown unity_debug_session action: " + action);
      }
    }

    object ToolBreakpoints(JObject args)
    {
      var action = NormalizeAction(args, "set");
      switch (action)
      {
        case "set":
          return GetOrCreateSession(args).SetBreakpoints(args);
        case "add":
          return GetOrCreateSession(args).AddBreakpoints(args);
        case "remove":
          return GetOrCreateSession(args).RemoveBreakpoints(args);
        case "update":
          return GetOrCreateSession(args).UpdateBreakpoint(args);
        case "clear":
          return GetOrCreateSession(args).ClearBreakpoints(args);
        case "list":
          return GetOrCreateSession(args).ListBreakpoints(args);
        default:
          throw new InvalidOperationException("Unknown unity_debug_breakpoints action: " + action);
      }
    }

    object ToolControl(JObject args)
    {
      var action = NormalizeAction(args, "resumeUntilStopped");
      switch (action)
      {
        case "enterplay":
          return WithSession(args, s => s.EnterPlayMode(args));
        case "enterplayandstop":
          return WithSession(args, s => s.EnterPlayAndStop(args));
        case "runtests":
          return WithSession(args, s => s.RunTests(args));
        case "wait":
        case "waitstopped":
          return WithSession(args, s => s.WaitStopped(args));
        case "snapshot":
          return WithSession(args, s => s.Snapshot(args));
        case "continue":
          return WithSession(args, s => s.Continue(args));
        case "resumeuntilstopped":
          return WithSession(args, s => s.ResumeUntilStopped(args));
        case "next":
        case "stepover":
          return WithSession(args, s => s.Next(args));
        case "stepin":
          return WithSession(args, s => s.StepIn(args));
        case "stepout":
          return WithSession(args, s => s.StepOut(args));
        case "pause":
          return WithSession(args, s => s.Pause(args));
        default:
          throw new InvalidOperationException("Unknown unity_debug_control action: " + action);
      }
    }

    object ToolStatus(JObject args)
    {
      var action = NormalizeAction(args, "status");
      switch (action)
      {
        case "status":
          if (TryGetSession(args, out var session))
            return session.Status();
          return new { active = false, message = "no active session" };
        case "breakpoints":
          return GetOrCreateSession(args).ListBreakpoints(args);
        case "diagnose":
          return WithSession(args, s => s.Diagnose(args));
        default:
          throw new InvalidOperationException("Unknown unity_debug_status action: " + action);
      }
    }

    static string NormalizeAction(JObject args, string defaultAction)
    {
      var action = (string)args["action"];
      return (string.IsNullOrWhiteSpace(action) ? defaultAction : action.Trim()).ToLowerInvariant();
    }

    object ToolStart(JObject args)
    {
      var session = GetOrCreateSession(args);
      return session.Attach(args, startUnityIfNoPid: true);
    }

    object ToolAttach(JObject args)
    {
      var session = GetOrCreateSession(args);
      return session.Attach(args, startUnityIfNoPid: false);
    }

    object ToolRunFlow(JObject args)
    {
      var session = new McpDebugSession(args);
      var disconnectOnComplete = (bool?)args["disconnectOnComplete"] ?? true;
      var stopTimeoutSeconds = (double?)args["stopTimeoutSeconds"] ?? 60.0;
      var stopCount = Math.Max(1, (int?)args["stopCount"] ?? 2);
      var lines = args["lines"] as JArray ?? new JArray(22, 26);
      var flowArgs = (JObject)args.DeepClone();
      flowArgs["sessionId"] = session.SessionId;
      flowArgs["lines"] = lines;

      var stops = new JArray();
      object start = null;
      object breakpoints = null;
      object exceptionBreakpoints = null;
      object disconnect = null;

      try
      {
        start = session.Start();
        breakpoints = session.SetBreakpoints(flowArgs);
        exceptionBreakpoints = session.SetExceptionBreakpoints();

        for (var i = 0; i < stopCount; i++)
        {
          var waitArgs = new JObject
          {
            ["timeoutSeconds"] = stopTimeoutSeconds
          };
          var stopped = session.WaitStopped(waitArgs);
          var snapshot = session.Snapshot(flowArgs);
          stops.Add(new JObject
          {
            ["index"] = i + 1,
            ["stopped"] = JToken.FromObject(stopped),
            ["snapshot"] = JToken.FromObject(snapshot)
          });

          if (i + 1 < stopCount)
            session.Continue(new JObject());
        }

        if (!disconnectOnComplete)
        {
          m_Sessions[session.SessionId] = session;
          m_ActiveSessionId = session.SessionId;
          return new
          {
            sessionId = session.SessionId,
            start,
            breakpoints,
            exceptionBreakpoints,
            stops,
            keptSession = true,
            status = session.Status()
          };
        }

        disconnect = session.Detach();
        session.Cleanup();
        return new
        {
          sessionId = session.SessionId,
          start,
          breakpoints,
          exceptionBreakpoints,
          stops,
          disconnect
        };
      }
      catch
      {
        session.Cleanup();
        throw;
      }
    }

    object ToolPrepare(JObject args)
    {
      var session = GetOrCreateSession(args);
      try
      {
        object breakpoints = null;
        object exceptionBreakpoints = null;
        if (args["lines"] != null || args["breakpoints"] != null)
          breakpoints = session.SetBreakpoints(args);
        if ((bool?)args["setExceptionBreakpoints"] ?? false)
          exceptionBreakpoints = session.SetExceptionBreakpoints();
        var start = session.Attach(args, startUnityIfNoPid: true);

        return new
        {
          sessionId = session.SessionId,
          active = true,
          start,
          breakpoints,
          exceptionBreakpoints,
          status = session.Status()
        };
      }
      catch
      {
        session.Detach();
        throw;
      }
    }

    object ToolCleanup(JObject args)
    {
      var sessionId = (string)args["sessionId"];
      if (!string.IsNullOrWhiteSpace(sessionId))
      {
        if (!m_Sessions.TryGetValue(sessionId, out var session))
          return new { cleaned = false, sessionId, message = "session not found" };

        session.Cleanup();
        m_Sessions.Remove(sessionId);
        if (m_ActiveSessionId == sessionId)
          m_ActiveSessionId = null;
        return new { cleaned = true, sessionId };
      }

      var ids = m_Sessions.Keys.ToArray();
      foreach (var id in ids)
      {
        m_Sessions[id].Cleanup();
        m_Sessions.Remove(id);
      }
      m_ActiveSessionId = null;
      return new { cleaned = true, sessions = ids };
    }

    object WithSession(JObject args, Func<McpDebugSession, object> action)
    {
      var sessionId = (string)args["sessionId"];
      if (string.IsNullOrWhiteSpace(sessionId))
        sessionId = m_ActiveSessionId;
      if (string.IsNullOrWhiteSpace(sessionId))
        throw new InvalidOperationException("sessionId is required because there is no active session");
      if (!m_Sessions.TryGetValue(sessionId, out var session))
        throw new InvalidOperationException("session not found: " + sessionId);
      m_ActiveSessionId = sessionId;
      return action(session);
    }

    McpDebugSession GetOrCreateSession(JObject args)
    {
      var sessionId = (string)args["sessionId"];
      if (!string.IsNullOrWhiteSpace(sessionId))
      {
        if (!m_Sessions.TryGetValue(sessionId, out var explicitSession))
          throw new InvalidOperationException("session not found: " + sessionId);
        m_ActiveSessionId = explicitSession.SessionId;
        return explicitSession;
      }

      if (!string.IsNullOrWhiteSpace(m_ActiveSessionId) && m_Sessions.TryGetValue(m_ActiveSessionId, out var activeSession))
        return activeSession;

      var session = new McpDebugSession(args);
      m_Sessions[session.SessionId] = session;
      m_ActiveSessionId = session.SessionId;
      return session;
    }

    bool TryGetSession(JObject args, out McpDebugSession session)
    {
      var sessionId = (string)args["sessionId"];
      if (string.IsNullOrWhiteSpace(sessionId))
        sessionId = m_ActiveSessionId;
      if (!string.IsNullOrWhiteSpace(sessionId) && m_Sessions.TryGetValue(sessionId, out session))
      {
        m_ActiveSessionId = sessionId;
        return true;
      }

      session = null;
      return false;
    }

    static JObject ToolText(object value)
    {
      var structured = value is JToken token ? token : JToken.FromObject(value);
      return new JObject
      {
        ["content"] = new JArray
        {
          new JObject
          {
            ["type"] = "text",
            ["text"] = ToMarkdown(structured)
          }
        }
      };
    }

    // ── Markdown formatting ──────────────────────────────────────────

    static string ToMarkdown(JToken token)
    {
      if (token == null) return "";
      var obj = token as JObject;
      if (obj == null) return token.ToString(Formatting.Indented);

      // Dispatch by detected shape (most specific first)
      if (obj["cleaned"] != null) return FormatCleanup(obj);
      if (obj["detached"] != null) return FormatDetach(obj);
      if (obj["active"] != null && obj["message"] != null) return FormatNoSession(obj);
      if (obj["adapterLogTail"] != null) return FormatDiagnose(obj);
      if (obj["active"] != null && obj["start"] != null) return FormatPrepare(obj);
      if (obj["attached"] != null && obj["unityPid"] != null) return FormatAttach(obj);
      if (obj["requested"] != null && obj["testMode"] != null) return FormatRunTests(obj);
      if (obj["enterPlayMode"] != null) return FormatEnterPlayAndStop(obj);
      if (obj["continued"] != null && obj["stopped"] != null && obj["snapshot"] != null) return FormatResumeUntilStopped(obj);
      if (obj["requested"] != null && obj["response"] != null) return FormatEnterPlay(obj);
      if (obj["recentEvents"] != null) return FormatStatus(obj);
      if (obj["threadId"] != null && obj["topFrame"] != null && obj["stackFrames"] != null && obj["stopped"] == null) return FormatSnapshot(obj);
      if (obj["stopped"] != null && obj["command"] == null && obj["snapshot"] == null) return FormatWait(obj);
      if (obj["command"] != null && obj["stopped"] != null && obj["snapshot"] != null) return FormatStepWithStop(obj);
      if (obj["command"] != null) return FormatStepSimple(obj);
      if (obj["sourcePath"] != null && obj["responses"] != null) return FormatClearAll(obj);
      if (obj["sourcePath"] != null && obj["response"] != null) return FormatBreakpointSync(obj);
      if (obj["sourcePath"] != null) return FormatBreakpointList(obj);
      if (obj["breakpoints"] != null && obj["sessionId"] != null) return FormatBreakpointListAll(obj);
      return token.ToString(Formatting.Indented);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    static string BoldKV(string key, object value)
    {
      return $"**{key}:** {value}";
    }

    static string ShortPath(string path)
    {
      if (string.IsNullOrEmpty(path)) return "";
      var name = Path.GetFileName(path);
      return $"**{name}** — `{path}`";
    }

    static string FormatStatusOneLine(JToken status)
    {
      if (status == null) return "";
      var attached = BoolYesNo(status["attached"]);
      var adapter = BoolAlive(status["adapterAlive"], "alive", "dead");
      var unity = BoolAlive(status["unityAlive"], "alive", "dead");
      return $"{BoldKV("Attached", attached)} | {BoldKV("Adapter", adapter)} | {BoldKV("Unity", unity)}";
    }

    static string BoolYesNo(JToken token)
    {
      return (bool?)token == true ? "yes" : "no";
    }

    static string BoolAlive(JToken token, string ifTrue, string ifFalse)
    {
      return (bool?)token == true ? ifTrue : ifFalse;
    }

    static string FormatStoppedOneLine(JToken stopped)
    {
      if (stopped == null) return "";
      var threadId = stopped["threadId"];
      var reason = stopped["reason"];
      return $"{BoldKV("Thread", threadId)} | {BoldKV("Reason", reason)}";
    }

    static string FormatStackFrames(JArray frames)
    {
      if (frames == null || frames.Count == 0) return "";
      var sb = new StringBuilder();
      sb.AppendLine("### Stack");
      for (int i = 0; i < frames.Count; i++)
      {
        var f = frames[i];
        var name = f["name"] ?? "?";
        var sourceName = (f["source"] as JObject)?["name"] ?? "";
        var line = f["line"] ?? "";
        sb.AppendLine($"{i + 1}. `{name}` — {sourceName}:{line}");
      }
      return sb.ToString();
    }

    static string FormatVariables(JArray variables)
    {
      if (variables == null || variables.Count == 0) return "";
      var sb = new StringBuilder();
      sb.AppendLine("### Variables");
      sb.AppendLine("| Name | Value | Type |");
      sb.AppendLine("|------|-------|------|");
      foreach (var v in variables)
      {
        var name = v["name"] ?? "";
        var value = v["value"] ?? "";
        var type = v["type"] ?? "";
        sb.AppendLine($"| {EscapeMd(name)} | `{EscapeMd(value)}` | {EscapeMd(type)} |");
      }
      return sb.ToString();
    }

    static string FormatEvaluations(JObject evaluations)
    {
      if (evaluations == null || evaluations.Count == 0) return "";
      var sb = new StringBuilder();
      sb.AppendLine("### Watch");
      sb.AppendLine("| Expression | Result | Children |");
      sb.AppendLine("|------------|--------|----------|");
      foreach (var kvp in evaluations)
      {
        var body = kvp.Value as JObject;
        var result = (body as JObject)?["result"] ?? "";
        var childRef = (int?)((body as JObject)?["variablesReference"]);
        var children = childRef.HasValue && childRef.Value > 0 ? "yes" : "—";
        sb.AppendLine($"| {EscapeMd(kvp.Key)} | `{EscapeMd(result)}` | {children} |");
      }
      return sb.ToString();
    }

    static string FormatBreakpointsCompact(JArray breakpoints)
    {
      if (breakpoints == null || breakpoints.Count == 0) return "";
      var sb = new StringBuilder();
      foreach (var bp in breakpoints)
      {
        var sourcePath = (string)bp["sourcePath"];
        var fileName = string.IsNullOrEmpty(sourcePath) ? "?" : Path.GetFileName(sourcePath);
        sb.AppendLine($"**{fileName}**");

        var specs = bp["breakpoints"] as JArray;
        if (specs != null && specs.Count > 0)
        {
          foreach (var spec in specs)
          {
            var line = spec["line"];
            var parts = new List<string> { $"L{line}" };
            var condition = (string)spec["condition"];
            var hitCondition = (string)spec["hitCondition"];
            var logMessage = (string)spec["logMessage"];
            if (!string.IsNullOrEmpty(condition))
              parts.Add($"cond: {condition}");
            if (!string.IsNullOrEmpty(hitCondition))
              parts.Add($"hit: {hitCondition}");
            if (!string.IsNullOrEmpty(logMessage))
              parts.Add($"log: {logMessage}");
            sb.AppendLine($"  {string.Join(" | ", parts)}");
          }
        }
        else
        {
          var lines = bp["lines"] as JArray;
          var lineStr = lines != null && lines.Count > 0 ? string.Join(", ", lines) : "(none)";
          sb.AppendLine($"  Lines: {lineStr}");
        }
      }
      return sb.ToString();
    }

    static string FormatLogTail(string label, JArray lines, int? tailCount)
    {
      if (lines == null || lines.Count == 0)
        return $"### {label}\n(empty)\n";
      var count = tailCount ?? lines.Count;
      var sb = new StringBuilder();
      sb.AppendLine($"### {label} (last {count})");
      sb.AppendLine("```");
      foreach (var line in lines)
        sb.AppendLine((string)line);
      sb.AppendLine("```");
      return sb.ToString();
    }

    static string EscapeMd(object value)
    {
      if (value == null) return "";
      return value.ToString().Replace("|", "\\|");
    }

    // ── Formatters ────────────────────────────────────────────────────

    static string FormatAttach(JObject obj)
    {
      var sb = new StringBuilder();
      sb.AppendLine("### Attached");
      sb.AppendLine(BoldKV("Session", $"`{obj["sessionId"]}`"));
      sb.AppendLine($"{BoldKV("Unity PID", obj["unityPid"])} | {BoldKV("Port", obj["port"])}");

      var bpSync = obj["breakpointSync"] as JArray;
      if (bpSync != null && bpSync.Count > 0)
      {
        var allBps = new JArray();
        string firstSourcePath = null;
        foreach (var sync in bpSync)
        {
          var resp = (sync["response"] as JObject)?["breakpoints"] as JArray;
          if (resp != null) foreach (var bp in resp) allBps.Add(bp);
          if (firstSourcePath == null)
            firstSourcePath = (string)sync["sourcePath"];
        }
        if (allBps.Count > 0)
        {
          sb.AppendLine();
          sb.AppendLine("### Breakpoints Synced");
          if (!string.IsNullOrEmpty(firstSourcePath))
            sb.AppendLine(ShortPath(firstSourcePath));
          sb.AppendLine("| Line | Verified |");
          sb.AppendLine("|------|----------|");
          foreach (var bp in allBps)
            sb.AppendLine($"| {bp["line"]} | {BoolYesNo(bp["verified"])} |");
        }
      }

      var status = obj["status"] as JObject;
      if (status != null)
      {
        sb.AppendLine();
        sb.AppendLine(FormatStatusOneLine(status));
      }
      return sb.ToString().TrimEnd();
    }

    static string FormatPrepare(JObject obj)
    {
      var sb = new StringBuilder();
      sb.AppendLine("### Session Prepared");

      var start = obj["start"] as JObject;
      if (start != null)
      {
        sb.AppendLine(BoldKV("Session", $"`{start["sessionId"]}`"));
        sb.AppendLine($"{BoldKV("Unity PID", start["unityPid"])} | {BoldKV("Port", start["port"])}");

        var bpSync = start["breakpointSync"] as JArray;
        if (bpSync != null && bpSync.Count > 0)
        {
          var allBps = new JArray();
          string firstSourcePath = null;
          foreach (var sync in bpSync)
          {
            var resp = (sync["response"] as JObject)?["breakpoints"] as JArray;
            if (resp != null) foreach (var bp in resp) allBps.Add(bp);
            if (firstSourcePath == null)
              firstSourcePath = (string)sync["sourcePath"];
          }
          if (allBps.Count > 0)
          {
            sb.AppendLine();
            sb.AppendLine("### Breakpoints Synced");
            if (!string.IsNullOrEmpty(firstSourcePath))
              sb.AppendLine(ShortPath(firstSourcePath));
            sb.AppendLine("| Line | Verified |");
            sb.AppendLine("|------|----------|");
            foreach (var bp in allBps)
              sb.AppendLine($"| {bp["line"]} | {BoolYesNo(bp["verified"])} |");
          }
        }
      }
      else
      {
        sb.AppendLine(BoldKV("Session", $"`{obj["sessionId"]}`"));
      }

      var status = obj["status"] as JObject ?? start?["status"] as JObject;
      if (status != null)
      {
        sb.AppendLine();
        sb.AppendLine(FormatStatusOneLine(status));
      }
      return sb.ToString().TrimEnd();
    }

    static string FormatDetach(JObject obj)
    {
      var sb = new StringBuilder();
      sb.AppendLine("### Detached");
      sb.AppendLine(BoldKV("Session", $"`{obj["sessionId"]}`"));
      var detached = obj["detached"];
      if (detached != null)
        sb.AppendLine(BoldKV("DAP disconnect sent", BoolYesNo(detached)));
      var continued = obj["continued"];
      if (continued != null)
      {
        var continuedObj = continued as JObject;
        if (continuedObj != null && continuedObj["error"] != null)
          sb.AppendLine(BoldKV("Continue before detach", $"failed: {continuedObj["error"]}"));
        else
          sb.AppendLine(BoldKV("Continued before detach", "yes"));
      }
      sb.AppendLine("> Session record preserved. Use `status` to check or `attach` to reconnect.");
      return sb.ToString().TrimEnd();
    }

    static string FormatCleanup(JObject obj)
    {
      var sb = new StringBuilder();
      var cleaned = (bool?)obj["cleaned"] == true;
      sb.AppendLine(cleaned ? "### Cleaned Up" : "### Cleanup");
      var sessions = obj["sessions"] as JArray;
      if (sessions != null && sessions.Count > 0)
        sb.AppendLine(BoldKV("Sessions removed", string.Join(", ", sessions.Select(s => $"`{s}`"))));
      else if (obj["sessionId"] != null)
      {
        var msg = (string)obj["message"];
        if (!string.IsNullOrEmpty(msg))
          sb.AppendLine(msg);
        else
          sb.AppendLine(BoldKV("Session", $"`{obj["sessionId"]}`"));
      }
      return sb.ToString().TrimEnd();
    }

    static string FormatNoSession(JObject obj)
    {
      return $"### Session Status\n{(string)obj["message"]}";
    }

    static string FormatStatus(JObject obj)
    {
      var sb = new StringBuilder();
      sb.AppendLine("### Session Status");
      sb.AppendLine(BoldKV("Session", $"`{obj["sessionId"]}`"));
      sb.AppendLine($"{BoldKV("Unity PID", obj["unityPid"])} | {BoldKV("Port", obj["port"])}");
      sb.AppendLine(FormatStatusOneLine(obj));

      var lastStopped = obj["lastStopped"] as JObject;
      if (lastStopped != null)
      {
        sb.AppendLine();
        sb.AppendLine("### Last Stop");
        sb.AppendLine(FormatStoppedOneLine(lastStopped));
      }

      var breakpoints = obj["breakpoints"] as JArray;
      if (breakpoints != null && breakpoints.Count > 0)
      {
        sb.AppendLine();
        sb.AppendLine("### Breakpoints");
        sb.Append(FormatBreakpointsCompact(breakpoints));
      }

      var logPaths = new[] { "adapterLogPath", "unityLogPath", "transcriptPath" };
      var logLabels = new[] { "Adapter", "Unity", "Transcript" };
      var hasAny = false;
      for (int i = 0; i < logPaths.Length; i++)
      {
        var p = (string)obj[logPaths[i]];
        if (!string.IsNullOrEmpty(p))
        {
          if (!hasAny) { sb.AppendLine(); hasAny = true; }
          sb.AppendLine($"{BoldKV(logLabels[i], $"`{p}`")}");
        }
      }

      var recentEvents = obj["recentEvents"] as JArray;
      if (recentEvents != null && recentEvents.Count > 0)
      {
        sb.AppendLine();
        sb.AppendLine($"### Recent Events ({recentEvents.Count})");
        for (int i = 0; i < recentEvents.Count; i++)
        {
          var evt = recentEvents[i];
          var type = (string)evt["event"] ?? "?";
          var detail = "";
          if (type == "stopped")
          {
            var body = evt["body"] as JObject;
            if (body != null)
              detail = $"(thread {body["threadId"]}, {body["reason"]})";
          }
          else if (type == "output")
          {
            var body = evt["body"] as JObject;
            if (body != null)
              detail = $"({body["category"]})";
          }
          sb.AppendLine($"{i + 1}. {type} {detail}".TrimEnd());
        }
      }
      return sb.ToString().TrimEnd();
    }

    static string FormatBreakpointSync(JObject obj)
    {
      var sb = new StringBuilder();
      var attached = obj["attached"] != null && (bool?)obj["attached"] == false;
      var synced = obj["synced"] != null && (bool?)obj["synced"] == false;
      sb.AppendLine(attached || synced ? "### Breakpoints Queued" : "### Breakpoints Set");
      var sourcePath = (string)obj["sourcePath"];
      if (!string.IsNullOrEmpty(sourcePath))
        sb.AppendLine(ShortPath(sourcePath));

      var lines = obj["lines"] as JArray;
      if (lines != null && lines.Count > 0)
        sb.AppendLine(BoldKV("Lines", string.Join(", ", lines)));

      if (attached || synced)
      {
        sb.AppendLine("Will sync on attach");
      }
      else
      {
        var respBps = (obj["response"] as JObject)?["breakpoints"] as JArray;
        var trackedSpecs = obj["breakpoints"] as JArray;
        var hasDetails = trackedSpecs != null && trackedSpecs.Count > 0 &&
            trackedSpecs.Any(s => !string.IsNullOrEmpty((string)s["condition"]) ||
                                  !string.IsNullOrEmpty((string)s["hitCondition"]) ||
                                  !string.IsNullOrEmpty((string)s["logMessage"]));
        if (respBps != null && respBps.Count > 0)
        {
          sb.AppendLine();
          if (hasDetails)
          {
            sb.AppendLine("| Line | Verified | Condition | Hit | Log |");
            sb.AppendLine("|------|----------|-----------|-----|-----|");
            foreach (var bp in respBps)
            {
              var line = (int?)bp["line"] ?? 0;
              var spec = trackedSpecs?.FirstOrDefault(s => (int?)s["line"] == line);
              var cond = (string)spec?["condition"] ?? "";
              var hit = (string)spec?["hitCondition"] ?? "";
              var log = (string)spec?["logMessage"] ?? "";
              sb.AppendLine($"| {line} | {BoolYesNo(bp["verified"])} | {EscapeMd(cond)} | {EscapeMd(hit)} | {EscapeMd(log)} |");
            }
          }
          else
          {
            sb.AppendLine("| Line | Verified |");
            sb.AppendLine("|------|----------|");
            foreach (var bp in respBps)
              sb.AppendLine($"| {bp["line"]} | {BoolYesNo(bp["verified"])} |");
          }
        }
      }
      return sb.ToString().TrimEnd();
    }

    static string FormatBreakpointList(JObject obj)
    {
      var sb = new StringBuilder();
      sb.AppendLine("### Breakpoints");
      var sourcePath = (string)obj["sourcePath"];
      if (!string.IsNullOrEmpty(sourcePath))
        sb.AppendLine(ShortPath(sourcePath));
      var lines = obj["lines"] as JArray;
      sb.AppendLine(BoldKV("Lines", lines != null && lines.Count > 0 ? string.Join(", ", lines) : "(none)"));
      return sb.ToString().TrimEnd();
    }

    static string FormatBreakpointListAll(JObject obj)
    {
      var breakpoints = obj["breakpoints"] as JArray;
      if (breakpoints == null || breakpoints.Count == 0)
        return "### Breakpoints\n(none)";
      var sb = new StringBuilder();
      sb.AppendLine("### All Breakpoints");
      sb.Append(FormatBreakpointsCompact(breakpoints));
      return sb.ToString().TrimEnd();
    }

    static string FormatClearAll(JObject obj)
    {
      var sb = new StringBuilder();
      sb.AppendLine("### All Breakpoints Cleared");
      var breakpoints = obj["breakpoints"] as JArray;
      if (breakpoints != null && breakpoints.Count > 0)
        sb.Append(FormatBreakpointsCompact(breakpoints));
      else
        sb.AppendLine("(none)");
      return sb.ToString().TrimEnd();
    }

    static string FormatEnterPlay(JObject obj)
    {
      var sb = new StringBuilder();
      sb.AppendLine("### Enter Play Mode");
      var resp = obj["response"] as JObject;
      if (resp != null)
      {
        var playing = (bool?)resp["isPlaying"] == true ? "yes" : "no";
        var changing = (bool?)resp["isPlayingOrWillChangePlaymode"] == true ? " (changing)" : "";
        var controlPort = resp["controlPort"];
        sb.AppendLine($"{BoldKV("Playing", $"{playing}{changing}")} | {BoldKV("Control Port", controlPort)}");
      }
      else
      {
        sb.AppendLine(BoldKV("Requested", "yes"));
      }
      return sb.ToString().TrimEnd();
    }

    static string FormatWait(JObject obj)
    {
      var stopped = obj["stopped"] as JObject;
      var threadId = obj["threadId"] ?? stopped?["threadId"];
      var reason = stopped?["reason"];
      var topFrame = obj["topFrame"] as JObject;
      var sb = new StringBuilder();
      sb.AppendLine($"### Stopped — Thread {threadId}");

      if (topFrame != null)
      {
        var sourceName = (string)((topFrame["source"] as JObject)?["name"] ?? "");
        var line = topFrame["line"] ?? "";
        sb.AppendLine($"{BoldKV("File", $"{sourceName}:{line}")} | {BoldKV("Reason", reason)}");
      }
      else
      {
        sb.AppendLine(FormatStoppedOneLine(stopped));
      }

      var frames = obj["stackFrames"] as JArray;
      if (frames != null && frames.Count > 0)
      {
        sb.AppendLine();
        sb.Append(FormatStackFrames(frames));
      }

      var variables = obj["variables"] as JArray;
      if (variables != null && variables.Count > 0)
      {
        sb.AppendLine();
        sb.Append(FormatVariables(variables));
      }

      var evaluations = obj["evaluations"] as JObject;
      if (evaluations != null && evaluations.Count > 0)
      {
        sb.AppendLine();
        sb.Append(FormatEvaluations(evaluations));
      }

      return sb.ToString().TrimEnd();
    }

    static string FormatSnapshot(JObject obj)
    {
      var sb = new StringBuilder();
      var threadId = obj["threadId"];
      sb.AppendLine($"### Snapshot — Thread {threadId}");
      sb.AppendLine();

      var frames = obj["stackFrames"] as JArray;
      sb.Append(FormatStackFrames(frames));
      if (frames != null && frames.Count > 0)
        sb.AppendLine();

      var variables = obj["variables"] as JArray;
      if (variables != null && variables.Count > 0)
      {
        sb.Append(FormatVariables(variables));
        sb.AppendLine();
      }

      var evaluations = obj["evaluations"] as JObject;
      if (evaluations != null && evaluations.Count > 0)
      {
        sb.Append(FormatEvaluations(evaluations));
      }
      return sb.ToString().TrimEnd();
    }

    static string FormatStepWithStop(JObject obj)
    {
      var sb = new StringBuilder();
      var command = (string)obj["command"] ?? "step";
      var stopped = obj["stopped"] as JObject;
      var threadId = stopped?["threadId"] ?? obj["threadId"];
      var reason = stopped?["reason"] ?? (obj["stopped"] as JObject)?["body"]?["reason"];

      sb.AppendLine($"### Step ({command}) — Thread {threadId}");
      sb.AppendLine(BoldKV("Reason", reason));

      var snapshot = obj["snapshot"] as JObject;
      if (snapshot != null)
      {
        var frames = snapshot["stackFrames"] as JArray;
        if (frames != null && frames.Count > 0)
        {
          sb.AppendLine();
          sb.Append(FormatStackFrames(frames));
        }
        var variables = snapshot["variables"] as JArray;
        if (variables != null && variables.Count > 0)
        {
          sb.AppendLine();
          sb.Append(FormatVariables(variables));
        }
      }
      return sb.ToString().TrimEnd();
    }

    static string FormatStepSimple(JObject obj)
    {
      var sb = new StringBuilder();
      var command = (string)obj["command"] ?? "step";
      var allContinued = (obj["response"] as JObject)?["allThreadsContinued"];
      if (command == "continue")
      {
        sb.AppendLine("### Continue");
        sb.AppendLine(BoldKV("All threads continued", BoolYesNo(allContinued)));
      }
      else if (command == "pause")
      {
        sb.AppendLine($"### Pause");
        sb.AppendLine("Command sent.");
      }
      else
      {
        sb.AppendLine($"### Step ({command})");
        sb.AppendLine(BoldKV("All threads continued", BoolYesNo(allContinued)));
      }
      return sb.ToString().TrimEnd();
    }

    static string FormatRunTests(JObject obj)
    {
      var sb = new StringBuilder();
      sb.AppendLine("### Run Tests");
      sb.AppendLine($"{BoldKV("Mode", obj["testMode"])} | {BoldKV("Filter", obj["testFilter"] ?? "(none)")}");
      sb.AppendLine(BoldKV("Requested", "yes"));
      var note = (string)obj["note"];
      if (!string.IsNullOrEmpty(note))
        sb.AppendLine($"> {note}");
      return sb.ToString().TrimEnd();
    }

    static string FormatEnterPlayAndStop(JObject obj)
    {
      var sb = new StringBuilder();
      sb.AppendLine("### Enter Play Mode");
      var enterResp = obj["enterPlayMode"] as JObject;
      var resp = enterResp?["response"] as JObject;
      if (resp != null)
      {
        var playing = (bool?)resp["isPlaying"] == true ? "yes" : "no";
        var changing = (bool?)resp["isPlayingOrWillChangePlaymode"] == true ? " (changing)" : "";
        sb.AppendLine(BoldKV("Playing", $"{playing}{changing}"));
      }
      else
      {
        sb.AppendLine(BoldKV("Requested", "yes"));
      }

      var stopped = obj["stopped"] as JObject;
      if (stopped != null)
      {
        sb.AppendLine();
        sb.AppendLine("### Stopped");
        sb.AppendLine(FormatStoppedOneLine(stopped));
      }

      var snapshot = obj["snapshot"] as JObject;
      if (snapshot != null)
      {
        var frames = snapshot["stackFrames"] as JArray;
        if (frames != null && frames.Count > 0)
        {
          sb.AppendLine();
          sb.Append(FormatStackFrames(frames));
        }
        var variables = snapshot["variables"] as JArray;
        if (variables != null && variables.Count > 0)
        {
          sb.AppendLine();
          sb.Append(FormatVariables(variables));
        }
      }
      return sb.ToString().TrimEnd();
    }

    static string FormatResumeUntilStopped(JObject obj)
    {
      var sb = new StringBuilder();
      sb.AppendLine("### Continued");
      var continued = obj["continued"] as JObject;
      var allContinued = (continued?["response"] as JObject)?["allThreadsContinued"];
      sb.AppendLine(BoldKV("All threads continued", BoolYesNo(allContinued)));

      var stopped = obj["stopped"] as JObject;
      if (stopped != null)
      {
        sb.AppendLine();
        sb.AppendLine("### Stopped");
        sb.AppendLine(FormatStoppedOneLine(stopped));
      }

      var snapshot = obj["snapshot"] as JObject;
      if (snapshot != null)
      {
        var frames = snapshot["stackFrames"] as JArray;
        if (frames != null && frames.Count > 0)
        {
          sb.AppendLine();
          sb.Append(FormatStackFrames(frames));
        }
        var variables = snapshot["variables"] as JArray;
        if (variables != null && variables.Count > 0)
        {
          sb.AppendLine();
          sb.Append(FormatVariables(variables));
        }
      }
      return sb.ToString().TrimEnd();
    }

    static string FormatDiagnose(JObject obj)
    {
      var sb = new StringBuilder();
      sb.AppendLine($"### Diagnose — Session `{obj["sessionId"]}`");
      var status = obj["status"] as JObject;
      if (status != null)
        sb.AppendLine(FormatStatusOneLine(status));

      var adapterTail = obj["adapterLogTail"] as JArray;
      sb.AppendLine();
      sb.Append(FormatLogTail("Adapter Log", adapterTail, adapterTail?.Count));

      var unityTail = obj["unityLogTail"] as JArray;
      sb.AppendLine();
      sb.Append(FormatLogTail("Unity Log", unityTail, unityTail?.Count));

      var transcriptTail = obj["transcriptTail"] as JArray;
      sb.AppendLine();
      sb.Append(FormatLogTail("Transcript", transcriptTail, transcriptTail?.Count));

      return sb.ToString().TrimEnd();
    }

    // ── Tool definitions ──────────────────────────────────────────────

    static JObject Tool(string name, string description, JObject inputSchema)
    {
      return new JObject
      {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema
      };
    }

    static JObject Obj(params JObject[] properties)
    {
      var props = new JObject();
      var required = new JArray();
      foreach (var property in properties)
      {
        var name = (string)property["name"];
        var isRequired = (bool?)property["required"] ?? false;
        property.Remove("name");
        property.Remove("required");
        props[name] = property;
        if (isRequired)
          required.Add(name);
      }

      var schema = new JObject
      {
        ["type"] = "object",
        ["properties"] = props
      };
      if (required.Count > 0)
        schema["required"] = required;
      return schema;
    }

    static JObject Prop(string name, string type, string description)
    {
      var property = new JObject
      {
        ["name"] = name,
        ["type"] = type,
        ["description"] = description
      };
      if (type == "array")
        property["items"] = new JObject { ["type"] = "integer" };
      return property;
    }

    static JObject ArrayProp(string name, string itemType, string description)
    {
      return new JObject
      {
        ["name"] = name,
        ["type"] = "array",
        ["description"] = description,
        ["items"] = new JObject { ["type"] = itemType }
      };
    }

    static JObject BreakpointsProp(string name, string description)
    {
      return new JObject
      {
        ["name"] = name,
        ["type"] = "array",
        ["description"] = description,
        ["items"] = new JObject
        {
          ["type"] = "object",
          ["properties"] = new JObject
          {
            ["line"] = new JObject { ["type"] = "integer" },
            ["column"] = new JObject { ["type"] = "integer" },
            ["condition"] = new JObject { ["type"] = "string" },
            ["hitCondition"] = new JObject { ["type"] = "string" },
            ["logMessage"] = new JObject { ["type"] = "string" }
          },
          ["required"] = new JArray("line")
        }
      };
    }

    static JObject Required(string name, string type, string description)
    {
      var property = Prop(name, type, description);
      property["required"] = true;
      return property;
    }

    static void SendResult(JToken id, JObject result)
    {
      WriteJson(new JObject
      {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["result"] = result
      });
    }

    static void SendError(JToken id, int code, string message)
    {
      var response = new JObject
      {
        ["jsonrpc"] = "2.0",
        ["error"] = new JObject
        {
          ["code"] = code,
          ["message"] = message
        }
      };
      if (id != null)
        response["id"] = id.DeepClone();
      WriteJson(response);
    }

    static void WriteJson(JObject message)
    {
      Console.Out.WriteLine(message.ToString(Formatting.None));
      Console.Out.Flush();
    }
  }

  internal class McpBreakpointSpec
  {
    public int Line { get; set; }
    public int Column { get; set; }
    public string Condition { get; set; }
    public string HitCondition { get; set; }
    public string LogMessage { get; set; }

    public McpBreakpointSpec Clone()
    {
      return new McpBreakpointSpec
      {
        Line = Line,
        Column = Column,
        Condition = Condition,
        HitCondition = HitCondition,
        LogMessage = LogMessage
      };
    }

    public JObject ToDap()
    {
      var value = new JObject { ["line"] = Line };
      if (Column > 0)
        value["column"] = Column;
      if (!string.IsNullOrWhiteSpace(Condition))
        value["condition"] = Condition;
      if (!string.IsNullOrWhiteSpace(HitCondition))
        value["hitCondition"] = HitCondition;
      if (!string.IsNullOrWhiteSpace(LogMessage))
        value["logMessage"] = LogMessage;
      return value;
    }

    public JObject ToJson()
    {
      return ToDap();
    }
  }

  internal class McpDebugSession
  {
    readonly string m_Root;
    readonly string m_AdapterPath;
    string m_ProjectPath;
    string m_SourcePath;
    string m_UnityExe;
    bool m_KillUnityHubLicensing;
    double m_StartupTimeoutSeconds;
    double m_ReadyTimeoutSeconds;
    double m_RequestTimeoutSeconds;

    Process m_UnityProcess;
    DapProcessClient m_Client;
    JObject m_LastStoppedEvent;
    readonly Dictionary<string, SortedDictionary<int, McpBreakpointSpec>> m_BreakpointsBySource = new Dictionary<string, SortedDictionary<int, McpBreakpointSpec>>(StringComparer.OrdinalIgnoreCase);
    bool m_Attached;
    bool m_OwnsUnityProcess;
    bool m_SetExceptionBreakpoints;
    int m_AdapterStartCount;

    public string SessionId { get; }
    public int UnityPid { get; private set; }
    public int Port { get; private set; }
    public string AdapterLogPath { get; }
    public string UnityLogPath { get; }
    public string TranscriptPath { get; }

    public McpDebugSession(JObject args)
    {
      m_Root = FindRepositoryRoot();
      SessionId = "unity-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
      m_AdapterPath = Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
      m_ProjectPath = FullPath("unity-debug-adapter.E2ETests/unity_test_project_2022_3");
      m_SourcePath = FullPath(Path.Combine(m_ProjectPath, "Assets", "Scripts", "TestScript.cs"));
      m_UnityExe = FindUnityExe();
      m_StartupTimeoutSeconds = 90.0;
      m_ReadyTimeoutSeconds = 10.0;
      m_RequestTimeoutSeconds = 15.0;
      ApplyOptions(args);

      var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcp-logs", SessionId);
      Directory.CreateDirectory(logDir);
      AdapterLogPath = Path.Combine(logDir, "adapter.log");
      UnityLogPath = Path.Combine(logDir, "unity.log");
      TranscriptPath = Path.Combine(logDir, "transcript.json");
    }

    public object Start()
    {
      return Attach(new JObject(), startUnityIfNoPid: true);
    }

    public object Attach(JObject args, bool startUnityIfNoPid)
    {
      ApplyOptions(args);
      Logger.LogInfo("MCP Attach V2-RESETDEBUGGER codeVersion=2026-06-10b session={0}", SessionId);

      if (!File.Exists(m_AdapterPath))
        throw new FileNotFoundException("adapter executable not found", m_AdapterPath);

      var requestedUnityPid = (int?)args["unityPid"];
      if (m_Attached && m_Client?.IsRunning == true)
      {
        if (!requestedUnityPid.HasValue || requestedUnityPid.Value == UnityPid)
        {
          var breakpointSync = SyncRequestedBreakpoints(args);
          if (breakpointSync == null)
            return Status();

          return new
          {
            sessionId = SessionId,
            attached = true,
            unityPid = UnityPid,
            port = Port,
            breakpointSync = new JArray(JToken.FromObject(breakpointSync)),
            exceptionBreakpoints = (object)null,
            status = Status()
          };
        }
        Detach();
      }

      SyncRequestedBreakpoints(args);
      var injected = EnsureEditorScripts(m_ProjectPath);
      ResolveUnityProcess(requestedUnityPid, startUnityIfNoPid);

      // Always wait for the control port to be ready, not just after injection.
      // The control server may not be up yet if Unity is still initializing,
      // or if the script was injected in a previous session and Unity has
      // restarted since then.  Without this wait, ResetUnityDebugger would
      // send a command that nobody processes.
      if (IsUnityProcessAlive())
        WaitControlPortReady(UnityPid, m_StartupTimeoutSeconds);

      Port = 56000 + UnityPid % 1000;

      // Reset Unity's debugger endpoint to clear any stale session left by a
      // previous adapter crash.  Without this, the Mono soft debugger will accept
      // TCP connections but never send VMStart, making attach hang forever.
      ResetUnityDebugger();

      var deadline = DateTime.UtcNow.AddSeconds(m_StartupTimeoutSeconds);
      Exception last = null;
      var attempt = 0;
      Logger.LogInfo(
          "MCP attach begin session={0}, unityPid={1}, port={2}, startupTimeoutSeconds={3}, readyTimeoutSeconds={4}, requestTimeoutSeconds={5}",
          SessionId,
          UnityPid,
          Port,
          m_StartupTimeoutSeconds,
          m_ReadyTimeoutSeconds,
          m_RequestTimeoutSeconds);
      StartAdapterClient();
      while (DateTime.UtcNow < deadline)
      {
        if (!IsUnityProcessAlive())
          throw new InvalidOperationException($"Unity process {UnityPid} exited during startup; see {UnityLogPath}");

        try
        {
          attempt++;
          Logger.LogInfo("MCP attach attempt {0} starting for session={1}", attempt, SessionId);
          if (m_Client == null || !m_Client.IsRunning)
            StartAdapterClient();

          // Use a short timeout for the first attempt to quickly detect and
          // flush stale debugger sessions.  If a previous adapter crashed without
          // sending DAP disconnect, Unity keeps the old session alive and will
          // not send VMStart on the first connection.  The failed connection
          // triggers Unity to start cleanup; subsequent attempts succeed.
          var readyTimeout = attempt <= 2 ? 5.0 : Math.Max(20.0, m_ReadyTimeoutSeconds);
          var requestTimeout = readyTimeout + 5.0;
          EnsureSuccess(m_Client.Request("attach", new JObject
          {
            ["address"] = "127.0.0.1",
            ["request"] = "attach",
            ["name"] = $"Connect to Unity Editor instance at 127.0.0.1:{Port}",
            ["type"] = "unity",
            ["port"] = Port,
            ["attachReadyTimeoutSeconds"] = readyTimeout
          }, requestTimeout));
          m_Attached = true;
          var breakpointSync = SyncAllBreakpoints();
          object exceptionBreakpoints = null;
          if (m_SetExceptionBreakpoints)
            exceptionBreakpoints = SyncExceptionBreakpoints();
          SaveTranscript();
          return new
          {
            sessionId = SessionId,
            attached = true,
            unityPid = UnityPid,
            port = Port,
            breakpointSync,
            exceptionBreakpoints,
            status = Status()
          };
        }
        catch (Exception e)
        {
          last = e;
          Logger.LogWarn("MCP attach attempt {0} failed for session={1}: {2}", attempt, SessionId, e.Message);
          SaveTranscript();
          StopAdapterClient();
          System.Threading.Thread.Sleep(2000);
        }
      }

      var timeoutMessage = "attach timed out: " + last?.Message;
      try { Detach(); }
      catch { }
      throw new TimeoutException(timeoutMessage);
    }

    void StartAdapterClient()
    {
      StopAdapterClient();
      var logDir = Path.GetDirectoryName(AdapterLogPath);
      var logBase = Path.GetFileNameWithoutExtension(AdapterLogPath);
      var logExt = Path.GetExtension(AdapterLogPath);
      // Use attempt counter to avoid overwriting logs from previous attempts.
      var attemptLogPath = m_AdapterStartCount == 0
          ? AdapterLogPath
          : Path.Combine(logDir, $"{logBase}-{m_AdapterStartCount}{logExt}");
      m_AdapterStartCount++;
      m_Client = new DapProcessClient(m_AdapterPath, attemptLogPath, m_RequestTimeoutSeconds);
      m_Client.Start();
      EnsureSuccess(m_Client.Request("initialize", new JObject
      {
        ["adapterID"] = "unity-debug-adapter-mcp",
        ["linesStartAt1"] = true,
        ["columnsStartAt1"] = true,
        ["pathFormat"] = "path",
        ["clientName"] = "mcp",
        ["clientID"] = "mcp"
      }));
    }

    void ResetUnityDebugger()
    {
      if (!IsUnityProcessAlive())
        return;

      try
      {
        // First verify Unity's Update() loop is processing commands by sending
        // a status ping.  After domain reload the TCP listener starts immediately
        // but the main-thread Update() callback may not have fired yet.
        Logger.LogInfo("MCP pinging Unity control port before resetdebugger");
        var ping = SendUnityControlCommand(new JObject { ["command"] = "status" }, new JObject());
        Logger.LogInfo("MCP status ping response: {0}", ping.ToString(Formatting.None));

        Logger.LogInfo("MCP sending resetdebugger to Unity control port");
        var response = SendUnityControlCommand(new JObject { ["command"] = "resetdebugger" }, new JObject());
        Logger.LogInfo("MCP resetdebugger response: {0}", response.ToString(Formatting.None));

        // Give Unity a moment to process the reset before we try to attach.
        System.Threading.Thread.Sleep(1000);
      }
      catch (Exception e)
      {
        // Non-fatal: the control server might not be injected yet, or this is a
        // fresh Unity session that doesn't need reset.
        Logger.LogInfo("MCP resetdebugger skipped: {0}", e.Message);
      }
    }

    void StopAdapterClient()
    {
      if (m_Client == null)
        return;

      if (m_Client.IsRunning)
        m_Client.Disconnect();

      if (m_Client.IsRunning)
        m_Client.Stop();
      m_Client = null;
      m_Attached = false;
      m_LastStoppedEvent = null;
    }

    object SyncRequestedBreakpoints(JObject args)
    {
      if (args?["lines"] == null && args?["breakpoints"] == null)
        return null;
      return SetBreakpoints(args);
    }

    void ApplyOptions(JObject args)
    {
      if (args == null)
        return;
      if (args["projectPath"] != null)
      {
        m_ProjectPath = FullPath((string)args["projectPath"]);
        if (args["sourcePath"] == null)
          m_SourcePath = FullPath(Path.Combine(m_ProjectPath, "Assets", "Scripts", "TestScript.cs"));
      }
      if (args["sourcePath"] != null)
        m_SourcePath = SourceFullPath((string)args["sourcePath"]);
      if (args["unityExe"] != null)
        m_UnityExe = (string)args["unityExe"];
      if (args["killUnityHubLicensing"] != null)
        m_KillUnityHubLicensing = (bool?)args["killUnityHubLicensing"] ?? false;
      if (args["startupTimeoutSeconds"] != null)
        m_StartupTimeoutSeconds = (double?)args["startupTimeoutSeconds"] ?? m_StartupTimeoutSeconds;
      if (args["readyTimeoutSeconds"] != null)
        m_ReadyTimeoutSeconds = (double?)args["readyTimeoutSeconds"] ?? m_ReadyTimeoutSeconds;
      if (args["requestTimeoutSeconds"] != null)
        m_RequestTimeoutSeconds = (double?)args["requestTimeoutSeconds"] ?? m_RequestTimeoutSeconds;
    }

    void ResolveUnityProcess(int? requestedUnityPid, bool startUnityIfNoPid)
    {
      if (requestedUnityPid.HasValue)
      {
        UnityPid = requestedUnityPid.Value;
        m_UnityProcess = TryGetProcessById(UnityPid);
        if (m_UnityProcess == null)
          throw new InvalidOperationException($"Unity process {UnityPid} was not found");
        m_OwnsUnityProcess = false;
        return;
      }

      if (UnityPid > 0 && ProcessExists(UnityPid))
      {
        m_UnityProcess = TryGetProcessById(UnityPid);
        return;
      }

      var unityProcesses = FindUnityProcesses(m_ProjectPath);
      if (unityProcesses.Length == 1)
      {
        m_UnityProcess = unityProcesses[0];
        m_OwnsUnityProcess = false;
        UnityPid = m_UnityProcess.Id;
        return;
      }

      if (unityProcesses.Length > 1)
        throw new InvalidOperationException("unityPid is required because multiple Unity Editor processes are running: " + string.Join(", ", unityProcesses.Select(p => p.Id)));

      if (startUnityIfNoPid)
      {
        if (!Directory.Exists(m_ProjectPath))
          throw new DirectoryNotFoundException("Unity project not found: " + m_ProjectPath);
        if (string.IsNullOrWhiteSpace(m_UnityExe) || !File.Exists(m_UnityExe))
          throw new FileNotFoundException("Unity editor not found", m_UnityExe);
        if (m_KillUnityHubLicensing)
          KillUnityHubLicensing();
        m_UnityProcess = StartUnity();
        m_OwnsUnityProcess = true;
        UnityPid = m_UnityProcess.Id;
        if (m_UnityProcess.HasExited)
          throw new InvalidOperationException($"Unity exited during startup with code {m_UnityProcess.ExitCode}; see {UnityLogPath}");
        return;
      }

      throw new InvalidOperationException("unityPid is required because no running Unity Editor process was found");
    }

    public object SetBreakpoints(JObject args)
    {
      var sourcePath = SourceFullPath((string)args["sourcePath"] ?? m_SourcePath);
      m_BreakpointsBySource[sourcePath] = ReadBreakpointSpecs(args, requireBreakpoints: true);
      return SyncBreakpoints(sourcePath);
    }

    public object AddBreakpoints(JObject args)
    {
      var sourcePath = SourceFullPath((string)args["sourcePath"] ?? m_SourcePath);
      var tracked = GetTrackedBreakpoints(sourcePath);
      foreach (var breakpoint in ReadBreakpointSpecs(args, requireBreakpoints: true).Values)
        tracked[breakpoint.Line] = breakpoint;
      return SyncBreakpoints(sourcePath);
    }

    public object RemoveBreakpoints(JObject args)
    {
      var sourcePath = SourceFullPath((string)args["sourcePath"] ?? m_SourcePath);
      var lineSet = ReadLineSet(args, requireLines: true);
      var tracked = GetTrackedBreakpoints(sourcePath);
      foreach (var line in lineSet)
        tracked.Remove(line);
      return SyncBreakpoints(sourcePath);
    }

    public object UpdateBreakpoint(JObject args)
    {
      var sourcePath = SourceFullPath((string)args["sourcePath"] ?? m_SourcePath);
      var oldLine = (int?)args["oldLine"];
      var newLine = (int?)args["newLine"];
      if (!oldLine.HasValue || oldLine.Value <= 0)
        throw new InvalidOperationException("oldLine must be a positive integer");
      if (!newLine.HasValue || newLine.Value <= 0)
        throw new InvalidOperationException("newLine must be a positive integer");

      var tracked = GetTrackedBreakpoints(sourcePath);
      if (!tracked.TryGetValue(oldLine.Value, out var breakpoint))
        breakpoint = new McpBreakpointSpec { Line = oldLine.Value };
      else
        breakpoint = breakpoint.Clone();

      tracked.Remove(oldLine.Value);
      breakpoint.Line = newLine.Value;
      ApplyBreakpointOverrides(breakpoint, args);
      tracked[newLine.Value] = breakpoint;
      return SyncBreakpoints(sourcePath);
    }

    public object ClearBreakpoints(JObject args)
    {
      var explicitSourcePath = (string)args["sourcePath"];
      if (!string.IsNullOrWhiteSpace(explicitSourcePath))
      {
        var sourcePath = SourceFullPath(explicitSourcePath);
        m_BreakpointsBySource[sourcePath] = new SortedDictionary<int, McpBreakpointSpec>();
        return SyncBreakpoints(sourcePath);
      }

      var results = new JArray();
      foreach (var sourcePath in m_BreakpointsBySource.Keys.ToArray())
      {
        m_BreakpointsBySource[sourcePath] = new SortedDictionary<int, McpBreakpointSpec>();
        results.Add(JToken.FromObject(SyncBreakpoints(sourcePath)));
      }

      return new
      {
        sessionId = SessionId,
        breakpoints = BreakpointSnapshot(),
        responses = results
      };
    }

    public object ListBreakpoints(JObject args)
    {
      var explicitSourcePath = (string)args["sourcePath"];
      if (!string.IsNullOrWhiteSpace(explicitSourcePath))
      {
        var sourcePath = SourceFullPath(explicitSourcePath);
        return new
        {
          sessionId = SessionId,
          sourcePath,
          lines = new JArray(GetTrackedBreakpoints(sourcePath).Keys),
          breakpoints = BreakpointSpecsToJson(GetTrackedBreakpoints(sourcePath))
        };
      }

      return new
      {
        sessionId = SessionId,
        breakpoints = BreakpointSnapshot()
      };
    }

    object SyncBreakpoints(string sourcePath)
    {
      var tracked = GetTrackedBreakpoints(sourcePath);
      if (!IsAttached())
      {
        return new
        {
          sessionId = SessionId,
          sourcePath,
          attached = false,
          synced = false,
          lines = tracked.Keys.ToArray(),
          breakpoints = BreakpointSpecsToJson(tracked)
        };
      }

      var breakpoints = new JArray(tracked.Values.Select(bp => bp.ToDap()));
      var response = m_Client.Request("setBreakpoints", new JObject
      {
        ["source"] = new JObject
        {
          ["path"] = sourcePath,
          ["name"] = Path.GetFileName(sourcePath)
        },
        ["breakpoints"] = breakpoints,
        ["lines"] = new JArray(tracked.Keys),
        ["sourceModified"] = false
      });
      EnsureSuccess(response);
      m_LastStoppedEvent = null;
      SaveTranscript();
      return new
      {
        sessionId = SessionId,
        sourcePath,
        lines = tracked.Keys.ToArray(),
        breakpoints = BreakpointSpecsToJson(tracked),
        response = response["body"]
      };
    }

    JArray SyncAllBreakpoints()
    {
      var results = new JArray();
      foreach (var sourcePath in m_BreakpointsBySource.Keys.ToArray())
        results.Add(JToken.FromObject(SyncBreakpoints(sourcePath)));
      return results;
    }

    SortedDictionary<int, McpBreakpointSpec> GetTrackedBreakpoints(string sourcePath)
    {
      if (!m_BreakpointsBySource.TryGetValue(sourcePath, out var tracked))
      {
        tracked = new SortedDictionary<int, McpBreakpointSpec>();
        m_BreakpointsBySource[sourcePath] = tracked;
      }

      return tracked;
    }

    SortedDictionary<int, McpBreakpointSpec> ReadBreakpointSpecs(JObject args, bool requireBreakpoints)
    {
      var specs = new SortedDictionary<int, McpBreakpointSpec>();
      var lines = args["lines"] as JArray;
      if (lines != null)
      {
        foreach (var lineToken in lines)
        {
          var line = (int?)lineToken;
          if (!line.HasValue || line.Value <= 0)
            throw new InvalidOperationException("breakpoint lines must be positive integers");
          specs[line.Value] = new McpBreakpointSpec { Line = line.Value };
        }
      }

      var breakpoints = args["breakpoints"] as JArray;
      if (breakpoints != null)
      {
        foreach (var token in breakpoints)
        {
          var spec = ReadBreakpointSpec(token);
          specs[spec.Line] = spec;
        }
      }

      if (specs.Count == 0 && requireBreakpoints)
        throw new InvalidOperationException("lines or breakpoints is required");

      return specs;
    }

    McpBreakpointSpec ReadBreakpointSpec(JToken token)
    {
      if (token.Type == JTokenType.Integer)
      {
        var line = (int)token;
        if (line <= 0)
          throw new InvalidOperationException("breakpoint lines must be positive integers");
        return new McpBreakpointSpec { Line = line };
      }

      var obj = token as JObject;
      if (obj == null)
        throw new InvalidOperationException("breakpoints entries must be integers or objects");

      var spec = new McpBreakpointSpec
      {
        Line = (int?)obj["line"] ?? 0,
        Column = (int?)obj["column"] ?? 0,
        Condition = (string)obj["condition"],
        HitCondition = (string)obj["hitCondition"],
        LogMessage = (string)obj["logMessage"]
      };
      if (spec.Line <= 0)
        throw new InvalidOperationException("breakpoint line must be a positive integer");
      return spec;
    }

    SortedSet<int> ReadLineSet(JObject args, bool requireLines)
    {
      var lines = args["lines"] as JArray;
      var lineSet = new SortedSet<int>();
      if (lines != null)
      {
        foreach (var lineToken in lines)
        {
          var line = (int?)lineToken;
          if (!line.HasValue || line.Value <= 0)
            throw new InvalidOperationException("breakpoint lines must be positive integers");
          lineSet.Add(line.Value);
        }
      }

      var breakpoints = args["breakpoints"] as JArray;
      if (breakpoints != null)
      {
        foreach (var breakpoint in breakpoints)
          lineSet.Add(ReadBreakpointSpec(breakpoint).Line);
      }

      if (lineSet.Count == 0 && requireLines)
        throw new InvalidOperationException("lines or breakpoints is required");

      return lineSet;
    }

    void ApplyBreakpointOverrides(McpBreakpointSpec breakpoint, JObject args)
    {
      if (args["column"] != null)
        breakpoint.Column = (int?)args["column"] ?? 0;
      if (args["condition"] != null)
        breakpoint.Condition = (string)args["condition"];
      if (args["hitCondition"] != null)
        breakpoint.HitCondition = (string)args["hitCondition"];
      if (args["logMessage"] != null)
        breakpoint.LogMessage = (string)args["logMessage"];
    }

    JArray BreakpointSnapshot()
    {
      var snapshot = new JArray();
      foreach (var item in m_BreakpointsBySource.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
      {
        snapshot.Add(new JObject
        {
          ["sourcePath"] = item.Key,
          ["lines"] = new JArray(item.Value.Keys),
          ["breakpoints"] = BreakpointSpecsToJson(item.Value)
        });
      }

      return snapshot;
    }

    JArray BreakpointSpecsToJson(SortedDictionary<int, McpBreakpointSpec> breakpoints)
    {
      return new JArray(breakpoints.Values.Select(bp => bp.ToJson()));
    }

    public object SetExceptionBreakpoints()
    {
      m_SetExceptionBreakpoints = true;
      return SyncExceptionBreakpoints();
    }

    object SyncExceptionBreakpoints()
    {
      if (!IsAttached())
      {
        return new
        {
          sessionId = SessionId,
          attached = false,
          synced = false,
          filters = new JArray()
        };
      }

      var response = m_Client.Request("setExceptionBreakpoints", new JObject
      {
        ["filters"] = new JArray()
      });
      EnsureSuccess(response);
      SaveTranscript();
      return new
      {
        sessionId = SessionId,
        response = response["body"]
      };
    }

    public object EnterPlayMode(JObject args)
    {
      EnsureAttached();
      var response = SendUnityControlCommand(new JObject
      {
        ["command"] = "enterPlay"
      }, args);

      return new
      {
        sessionId = SessionId,
        requested = true,
        response
      };
    }

    public object WaitStopped(JObject args)
    {
      EnsureAttached();
      var timeout = (double?)args["timeoutSeconds"] ?? 60.0;
      m_LastStoppedEvent = m_Client.WaitEvent("stopped", timeout);
      var stoppedBody = m_LastStoppedEvent["body"];

      // Auto-snapshot: fetch stack, variables, and (optionally) evaluations
      var snapArgs = new JObject { ["sessionId"] = SessionId };
      if (args["expressions"] != null)
        snapArgs["expressions"] = args["expressions"];
      else
        snapArgs["expressions"] = new JArray(); // No evaluations unless caller asks

      var snapObj = Snapshot(snapArgs);
      var combined = JObject.FromObject(snapObj);
      combined["stopped"] = stoppedBody;
      return combined;
    }

    /// <summary>
    /// Extracts (stoppedBody, snapshotSubObject) from the combined JObject
    /// returned by WaitStopped, so callers like EnterPlayAndStop can pass
    /// the two pieces separately without calling Snapshot again.
    /// </summary>
    static (JToken, JObject) ExtractWaitResult(object waitResult)
    {
      var wr = waitResult as JObject ?? JObject.FromObject(waitResult);
      var stoppedBody = wr["stopped"];
      var snapshot = new JObject
      {
        ["sessionId"] = wr["sessionId"],
        ["threadId"] = wr["threadId"],
        ["topFrame"] = wr["topFrame"],
        ["stackFrames"] = wr["stackFrames"],
        ["variables"] = wr["variables"],
        ["evaluations"] = wr["evaluations"]
      };
      return (stoppedBody, snapshot);
    }

    public object Snapshot(JObject args)
    {
      EnsureAttached();
      var threadId = (int?)args["threadId"] ?? (int?)m_LastStoppedEvent?["body"]?["threadId"];
      if (!threadId.HasValue)
        throw new InvalidOperationException("threadId is required because no stopped event is cached");

      var expressions = args["expressions"] as JArray
          ?? new JArray("this", "m_Radius", "s_StaticBoolVar", "transform.position");
      var stack = m_Client.Request("stackTrace", new JObject
      {
        ["threadId"] = threadId.Value,
        ["startFrame"] = 0
      });
      EnsureSuccess(stack);

      var frames = (stack["body"] as JObject)?["stackFrames"] as JArray ?? new JArray();
      var scopes = new JArray();
      var variables = new JArray();
      var evaluations = new JObject();

      int? frameId = null;
      if (frames.Count > 0)
        frameId = (int?)frames[0]["id"];

      if (frameId.HasValue)
      {
        var scopesResponse = m_Client.Request("scopes", new JObject { ["frameId"] = frameId.Value });
        EnsureSuccess(scopesResponse);
        scopes = (scopesResponse["body"] as JObject)?["scopes"] as JArray ?? new JArray();

        foreach (var scope in scopes)
        {
          var reference = (int?)scope["variablesReference"] ?? 0;
          if (reference <= 0)
            continue;
          var variablesResponse = m_Client.Request("variables", new JObject { ["variablesReference"] = reference });
          EnsureSuccess(variablesResponse);
          foreach (var variable in (variablesResponse["body"] as JObject)?["variables"] as JArray ?? new JArray())
            variables.Add(variable);
        }

        foreach (var expressionToken in expressions)
        {
          var expression = (string)expressionToken;
          var response = m_Client.Request("evaluate", new JObject
          {
            ["expression"] = expression,
            ["frameId"] = frameId.Value,
            ["context"] = "watch"
          });
          evaluations[expression] = response["body"];
        }
      }

      SaveTranscript();
      return new
      {
        sessionId = SessionId,
        threadId = threadId.Value,
        topFrame = frames.Count > 0 ? frames[0] : null,
        stackFrames = frames,
        scopes,
        variables,
        evaluations
      };
    }

    public object Continue(JObject args)
    {
      EnsureAttached();
      var threadId = (int?)args["threadId"] ?? (int?)m_LastStoppedEvent?["body"]?["threadId"];
      if (!threadId.HasValue)
        throw new InvalidOperationException("threadId is required because no stopped event is cached");

      var response = m_Client.Request("continue", new JObject
      {
        ["threadId"] = threadId.Value,
        ["granularity"] = "statement"
      });
      EnsureSuccess(response);
      SaveTranscript();
      return new
      {
        sessionId = SessionId,
        command = "continue",
        response = response["body"] is JObject bodyObj ? bodyObj : new JObject()
      };
    }

    public object Next(JObject args)
    {
      return ExecuteAndMaybeStop("next", args, requireThread: true);
    }

    public object StepIn(JObject args)
    {
      return ExecuteAndMaybeStop("stepIn", args, requireThread: true);
    }

    public object StepOut(JObject args)
    {
      return ExecuteAndMaybeStop("stepOut", args, requireThread: true);
    }

    public object Pause(JObject args)
    {
      return ExecuteAndMaybeStop("pause", args, requireThread: false);
    }

    object ExecuteAndMaybeStop(string command, JObject args, bool requireThread)
    {
      EnsureAttached();
      var requestArgs = new JObject();
      var threadId = (int?)args["threadId"] ?? (int?)m_LastStoppedEvent?["body"]?["threadId"];
      if (threadId.HasValue)
        requestArgs["threadId"] = threadId.Value;
      else if (requireThread)
        throw new InvalidOperationException("threadId is required because no stopped event is cached");

      if (command != "pause")
        requestArgs["granularity"] = "statement";

      var response = m_Client.Request(command, requestArgs);
      EnsureSuccess(response);
      SaveTranscript();
      var responseBody = response["body"] is JObject bodyObj ? bodyObj : new JObject();

      var waitForStop = (bool?)args["waitForStop"] ?? true;
      if (!waitForStop)
      {
        return new
        {
          sessionId = SessionId,
          command,
          response = responseBody,
          status = Status()
        };
      }

      var wr = ExtractWaitResult(WaitStopped(args));
      return new
      {
        sessionId = SessionId,
        command,
        response = responseBody,
        stopped = wr.Item1,
        snapshot = wr.Item2,
        status = Status()
      };
    }

    public object RunTests(JObject args)
    {
      EnsureAttached();
      var testMode = (string)args["testMode"] ?? "EditMode";
      var testFilter = (string)args["testFilter"];
      var request = new JObject
      {
        ["command"] = "runTests",
        ["testMode"] = testMode
      };
      if (!string.IsNullOrWhiteSpace(testFilter))
        request["testFilter"] = testFilter;

      var response = SendUnityControlCommand(request, args);

      return new
      {
        sessionId = SessionId,
        requested = true,
        testMode,
        testFilter,
        response,
        note = "Attach and set breakpoints before runTests; Unity starts the Test Runner after this control command is accepted."
      };
    }

    JObject SendUnityControlCommand(JObject request, JObject args)
    {
      var timeoutMilliseconds = Math.Max(1000, (int)(((double?)args["controlTimeoutSeconds"] ?? 15.0) * 1000));
      var controlPort = (int?)args["controlPort"] ?? (57000 + Math.Abs(UnityPid % 1000));
      using (var client = new TcpClient())
      {
        var connect = client.BeginConnect("127.0.0.1", controlPort, null, null);
        if (!connect.AsyncWaitHandle.WaitOne(timeoutMilliseconds))
          throw new TimeoutException("timed out connecting to Unity MCP control port " + controlPort);
        client.EndConnect(connect);
        client.ReceiveTimeout = timeoutMilliseconds;
        client.SendTimeout = timeoutMilliseconds;

        var payload = Encoding.UTF8.GetBytes(request.ToString(Formatting.None) + "\n");
        var stream = client.GetStream();
        stream.Write(payload, 0, payload.Length);
        stream.Flush();

        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
          var line = reader.ReadLine();
          if (string.IsNullOrWhiteSpace(line))
            throw new InvalidOperationException("Unity MCP control port returned an empty response");
          var response = JObject.Parse(line);
          if ((bool?)response["ok"] != true)
            throw new InvalidOperationException("Unity MCP control command failed: " + response.ToString(Formatting.None));
          return response;
        }
      }
    }

    public object EnterPlayAndStop(JObject args)
    {
      var enterPlayMode = EnterPlayMode(args);
      var wr = ExtractWaitResult(WaitStopped(args));
      return new
      {
        sessionId = SessionId,
        enterPlayMode,
        stopped = wr.Item1,
        snapshot = wr.Item2,
        status = Status()
      };
    }

    public object ResumeUntilStopped(JObject args)
    {
      var continued = Continue(args);
      var wr = ExtractWaitResult(WaitStopped(args));
      return new
      {
        sessionId = SessionId,
        continued,
        stopped = wr.Item1,
        snapshot = wr.Item2,
        status = Status()
      };
    }

    public object Disconnect()
    {
      return Detach();
    }

    public object Detach()
    {
      object continued = null;
      JObject response = null;
      if (m_Client != null)
      {
        if (m_LastStoppedEvent != null && m_Client.IsRunning)
        {
          try
          {
            continued = Continue(new JObject());
          }
          catch (Exception e)
          {
            continued = new { error = e.Message };
          }
        }
        response = m_Client.Disconnect();
        if (m_Client.IsRunning)
          m_Client.Stop();
        SaveTranscript();
      }

      m_Client = null;
      m_Attached = false;
      m_LastStoppedEvent = null;
      return new
      {
        sessionId = SessionId,
        detached = response != null,
        continued,
        response
      };
    }

    public object Status()
    {
      return new
      {
        sessionId = SessionId,
        unityPid = UnityPid,
        port = Port,
        attached = m_Attached,
        adapterAlive = m_Client?.IsRunning ?? false,
        unityAlive = IsUnityProcessAlive(),
        lastStopped = m_LastStoppedEvent?["body"],
        breakpoints = BreakpointSnapshot(),
        exceptionBreakpoints = m_SetExceptionBreakpoints,
        adapterLogPath = AdapterLogPath,
        unityLogPath = UnityLogPath,
        transcriptPath = TranscriptPath,
        recentEvents = m_Client?.Events.TakeLastSafe(8).ToArray() ?? new JObject[0]
      };
    }

    public object Diagnose(JObject args)
    {
      var tailLines = Math.Max(1, (int?)args["tailLines"] ?? 30);
      return new
      {
        sessionId = SessionId,
        status = Status(),
        adapterLogTail = ReadLastLines(AdapterLogPath, tailLines),
        unityLogTail = ReadLastLines(UnityLogPath, tailLines),
        transcriptTail = ReadLastLines(TranscriptPath, tailLines)
      };
    }

    public void Cleanup()
    {
      try { Detach(); }
      catch { }

      try
      {
        if (m_OwnsUnityProcess && m_UnityProcess != null && !m_UnityProcess.HasExited)
        {
          m_UnityProcess.CloseMainWindow();
          if (!m_UnityProcess.WaitForExit(5000))
            m_UnityProcess.Kill();
        }
      }
      catch { }

      m_BreakpointsBySource.Clear();
      m_SetExceptionBreakpoints = false;
      UnityPid = 0;
      Port = 0;
      m_UnityProcess = null;
      m_OwnsUnityProcess = false;
    }

    Process StartUnity()
    {
      var process = new Process();
      process.StartInfo.FileName = m_UnityExe;
      process.StartInfo.Arguments = $"-projectPath \"{m_ProjectPath}\" -logFile \"{UnityLogPath}\"";
      process.StartInfo.UseShellExecute = false;
      process.StartInfo.CreateNoWindow = false;
      process.Start();
      return process;
    }

    void SaveTranscript()
    {
      if (m_Client == null)
        return;
      var data = new JObject
      {
        ["sessionId"] = SessionId,
        ["unityPid"] = UnityPid,
        ["port"] = Port,
        ["transcript"] = new JArray(m_Client.Transcript),
        ["stderr"] = new JArray(m_Client.StderrLines)
      };
      Directory.CreateDirectory(Path.GetDirectoryName(TranscriptPath));
      File.WriteAllText(TranscriptPath, data.ToString(Formatting.Indented), Encoding.UTF8);
    }

    string[] ReadLastLines(string path, int count)
    {
      try
      {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
          return new string[0];
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8, true))
        {
          var lines = new List<string>();
          string line;
          while ((line = reader.ReadLine()) != null)
            lines.Add(line);
          return lines.TakeLastSafe(count).ToArray();
        }
      }
      catch (Exception e)
      {
        return new[] { "failed to read " + path + ": " + e.Message };
      }
    }

    string FullPath(string path)
    {
      if (Path.IsPathRooted(path))
        return Path.GetFullPath(path);
      return Path.GetFullPath(Path.Combine(m_Root, path));
    }

    string SourceFullPath(string path)
    {
      if (string.IsNullOrEmpty(path))
        return path;
      if (Path.IsPathRooted(path))
        return Path.GetFullPath(path);
      return Path.GetFullPath(Path.Combine(m_ProjectPath, path));
    }

    bool EnsureEditorScripts(string projectPath)
    {
      try
      {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "McpPlayModeController.cs";
        string template;
        using (var stream = assembly.GetManifestResourceStream(resourceName))
        {
          if (stream == null)
          {
            Logger.LogInfo("MCP Editor template resource '{0}' not found in assembly, skipping injection", resourceName);
            return false;
          }
          using (var reader = new StreamReader(stream, Encoding.UTF8))
            template = reader.ReadToEnd();
        }

        var editorDir = Path.Combine(projectPath, "Assets", "Editor");
        var targetPath = Path.Combine(editorDir, "McpPlayModeController.cs");

        if (File.Exists(targetPath))
        {
          var existing = File.ReadAllText(targetPath, Encoding.UTF8);
          if (string.Equals(existing, template, StringComparison.Ordinal))
          {
            Logger.LogInfo("MCP Editor script already up to date: {0}", targetPath);
            return false;
          }

          File.WriteAllText(targetPath, template, Encoding.UTF8);
          Logger.LogInfo("MCP Editor script updated: {0}", targetPath);
          return true;
        }

        Directory.CreateDirectory(editorDir);
        File.WriteAllText(targetPath, template, Encoding.UTF8);
        Logger.LogInfo("MCP Editor script injected: {0}", targetPath);
        return true;
      }
      catch (Exception e)
      {
        Logger.LogInfo("MCP Editor script injection failed (non-fatal): {0}", e.Message);
        return false;
      }
    }

    void WaitControlPortReady(int pid, double timeoutSeconds)
    {
      var controlPort = 57000 + Math.Abs(pid % 1000);
      var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
      Logger.LogInfo("MCP waiting for Editor controller on port {0} (timeout {1}s)", controlPort, timeoutSeconds);

      while (DateTime.UtcNow < deadline)
      {
        try
        {
          using (var client = new TcpClient())
          {
            client.Connect("127.0.0.1", controlPort);
            client.ReceiveTimeout = 3000;
            client.SendTimeout = 3000;

            var request = Encoding.UTF8.GetBytes("{\"command\":\"status\"}\n");
            var stream = client.GetStream();
            stream.Write(request, 0, request.Length);
            stream.Flush();

            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
              var line = reader.ReadLine();
              if (!string.IsNullOrWhiteSpace(line))
              {
                var response = JObject.Parse(line);
                if ((bool?)response["ok"] == true)
                {
                  Logger.LogInfo("MCP Editor controller active on port {0}", controlPort);
                  return;
                }
              }
            }
          }
        }
        catch { }

        System.Threading.Thread.Sleep(500);
      }

      throw new TimeoutException(
          "Timed out waiting for MCP Editor controller on port " + controlPort +
          ". Unity may have compilation errors — check the Unity Console.");
    }

    bool IsUnityProcessAlive()
    {
      if (UnityPid <= 0)
        return false;

      if (m_UnityProcess == null)
        m_UnityProcess = TryGetProcessById(UnityPid);
      if (m_UnityProcess == null)
        return false;

      try
      {
        m_UnityProcess.Refresh();
        return !m_UnityProcess.HasExited;
      }
      catch
      {
        return ProcessExists(UnityPid);
      }
    }

    bool IsAttached()
    {
      return m_Attached && m_Client?.IsRunning == true;
    }

    void EnsureAttached()
    {
      if (!IsAttached())
        throw new InvalidOperationException("debug session is detached; call unity_debug_session with action=attach first");
    }

    static Process TryGetProcessById(int pid)
    {
      try
      {
        return Process.GetProcessById(pid);
      }
      catch
      {
        return null;
      }
    }

    static bool ProcessExists(int pid)
    {
      foreach (var process in Process.GetProcesses())
      {
        try
        {
          if (process.Id == pid)
            return true;
        }
        catch { }
        finally
        {
          process.Dispose();
        }
      }

      return false;
    }

    static Process[] FindUnityProcesses(string projectPath)
    {
      var candidates = Process.GetProcesses()
          .Where(IsUnityEditorProcess)
          .ToArray();
      if (candidates.Length <= 1)
        return candidates;

      var projectMatches = candidates
          .Where(process => IsUnityProcessForProject(process, projectPath))
          .ToArray();
      return projectMatches.Length > 0 ? projectMatches : candidates;
    }

    static bool IsUnityEditorProcess(Process process)
    {
      var processName = TryGetProcessName(process);
      if (IsUnityEditorProcessName(processName))
        return !HasProcessExited(process);

      if (HasProcessExited(process))
        return false;

      var mainModulePath = TryGetMainModulePath(process);
      return IsUnityEditorProcessName(Path.GetFileNameWithoutExtension(mainModulePath));
    }

    static string TryGetProcessName(Process process)
    {
      try
      {
        return process.ProcessName;
      }
      catch
      {
        return null;
      }
    }

    static bool IsUnityEditorProcessName(string processName)
    {
      return string.Equals(processName, "Unity", StringComparison.OrdinalIgnoreCase)
          || string.Equals(processName, "Unity Editor", StringComparison.OrdinalIgnoreCase);
    }

    static bool HasProcessExited(Process process)
    {
      try
      {
        process.Refresh();
        return process.HasExited;
      }
      catch
      {
        return false;
      }
    }

    static string TryGetMainModulePath(Process process)
    {
      try
      {
        return process.MainModule?.FileName;
      }
      catch
      {
        return null;
      }
    }

    static bool IsUnityProcessForProject(Process process, string projectPath)
    {
      if (string.IsNullOrWhiteSpace(projectPath))
        return false;

      var expectedProjectPath = NormalizeProjectPath(projectPath);
      if (string.IsNullOrWhiteSpace(expectedProjectPath))
        return false;

      foreach (var candidateProjectPath in ReadProjectPathsFromProcessArguments(process))
      {
        var normalizedCandidate = NormalizeProjectPath(candidateProjectPath);
        if (string.Equals(normalizedCandidate, expectedProjectPath, StringComparison.OrdinalIgnoreCase))
          return true;
      }

      return false;
    }

    static IEnumerable<string> ReadProjectPathsFromProcessArguments(Process process)
    {
      var args = TryGetProcessArguments(process);
      for (var i = 0; i < args.Length; i++)
      {
        var arg = args[i];
        if (string.Equals(arg, "-projectPath", StringComparison.OrdinalIgnoreCase))
        {
          if (i + 1 < args.Length)
            yield return args[i + 1];
          continue;
        }

        const string prefix = "-projectPath=";
        if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
          yield return arg.Substring(prefix.Length);
      }
    }

    static string[] TryGetProcessArguments(Process process)
    {
      if (Environment.OSVersion.Platform == PlatformID.Win32NT)
      {
        var commandLine = TryGetWindowsProcessCommandLine(process);
        return string.IsNullOrWhiteSpace(commandLine)
            ? Array.Empty<string>()
            : SplitCommandLine(commandLine);
      }

      try
      {
        var procCmdLine = "/proc/" + process.Id + "/cmdline";
        if (File.Exists(procCmdLine))
          return SplitNullTerminatedArguments(File.ReadAllBytes(procCmdLine));
      }
      catch
      {
      }

      return Array.Empty<string>();
    }

    static string TryGetWindowsProcessCommandLine(Process process)
    {
      try
      {
        var searcherType = Type.GetType("System.Management.ManagementObjectSearcher, System.Management");
        if (searcherType == null)
          return null;

        using (var searcher = (IDisposable)Activator.CreateInstance(
            searcherType,
            "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + process.Id))
        {
          var getMethod = searcherType.GetMethod("Get", Type.EmptyTypes);
          using (var results = (IDisposable)getMethod.Invoke(searcher, null))
          {
            var enumerable = results as IEnumerable;
            if (enumerable == null)
              return null;

            foreach (var result in enumerable)
            {
              try
              {
                var itemProperty = result.GetType().GetProperty("Item", new[] { typeof(string) });
                return itemProperty?.GetValue(result, new object[] { "CommandLine" }) as string;
              }
              finally
              {
                (result as IDisposable)?.Dispose();
              }
            }
          }
        }
      }
      catch
      {
      }

      return null;
    }

    static string[] SplitCommandLine(string commandLine)
    {
      var args = new List<string>();
      var current = new StringBuilder();
      var inQuotes = false;
      for (var i = 0; i < commandLine.Length; i++)
      {
        var ch = commandLine[i];
        if (ch == '"')
        {
          inQuotes = !inQuotes;
          continue;
        }

        if (char.IsWhiteSpace(ch) && !inQuotes)
        {
          if (current.Length > 0)
          {
            args.Add(current.ToString());
            current.Clear();
          }
          continue;
        }

        current.Append(ch);
      }

      if (current.Length > 0)
        args.Add(current.ToString());
      return args.ToArray();
    }

    static string[] SplitNullTerminatedArguments(byte[] bytes)
    {
      var args = new List<string>();
      var start = 0;
      for (var i = 0; i < bytes.Length; i++)
      {
        if (bytes[i] != 0)
          continue;

        if (i > start)
          args.Add(Encoding.UTF8.GetString(bytes, start, i - start));
        start = i + 1;
      }

      if (start < bytes.Length)
        args.Add(Encoding.UTF8.GetString(bytes, start, bytes.Length - start));
      return args.ToArray();
    }

    static string NormalizeProjectPath(string value)
    {
      if (string.IsNullOrWhiteSpace(value))
        return null;

      try
      {
        return FullPathStatic(value.Trim().Trim('"'))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace('\\', '/');
      }
      catch
      {
        return null;
      }
    }

    static string FullPathStatic(string path)
    {
      return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    static string FindRepositoryRoot()
    {
      var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
      while (directory != null)
      {
        if (Directory.Exists(Path.Combine(directory.FullName, "unity-debug-adapter")) &&
            Directory.Exists(Path.Combine(directory.FullName, "unity-debug-adapter.E2ETests")))
          return directory.FullName;
        directory = directory.Parent;
      }

      return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
    }

    static void EnsureSuccess(JObject response)
    {
      if ((bool?)response["success"] != true)
        throw new DapRequestFailedException(response);
    }

    sealed class DapRequestFailedException : InvalidOperationException
    {
      public JObject Response { get; }
      public string Command { get; }

      public DapRequestFailedException(JObject response)
          : base(FormatFriendlyMessage(response))
      {
        Response = response;
        Command = (string)response["command"];
      }

      static string FormatFriendlyMessage(JObject response)
      {
        var command = (string)response["command"] ?? "(unknown)";
        var error = response["body"]?["error"];
        var errorId = (int?)error?["id"];
        var errorFormat = (string)error?["format"];

        // Source-not-found errors (DAP error ids 3011, 7712, or message contains "source")
        if (errorId == 3011 || errorId == 7712 ||
            (errorFormat != null && errorFormat.Contains("source") && errorFormat.Contains("not")))
          return $"Source file not found (command: {command}). " +
                 "Ensure the file exists and Unity has loaded it (try entering Play Mode first).";

        // Breakpoint-set failures
        if (errorId == 1104 ||
            (errorFormat != null && errorFormat.Contains("breakpoint") && errorFormat.Contains("not")))
          return $"Breakpoint could not be set (command: {command}). " +
                 "The source location may not exist or may not be loaded by the debugger.";

        // Generic message with the error format if available
        if (!string.IsNullOrEmpty(errorFormat))
          return $"Debug adapter error in {command}: {errorFormat}";

        return $"Debug adapter request failed (command: {command})";
      }
    }

    static string FindUnityExe()
    {
      var root = @"C:\Program Files\Unity\Hub\Editor";
      if (!Directory.Exists(root))
        return null;
      return Directory.GetDirectories(root)
          .Where(path => Path.GetFileName(path).StartsWith("2022.3."))
          .OrderByDescending(path => path)
          .Select(path => Path.Combine(path, "Editor", "Unity.exe"))
          .FirstOrDefault(File.Exists);
    }

    static void KillUnityHubLicensing()
    {
      foreach (var process in Process.GetProcesses())
      {
        try
        {
          if (process.ProcessName == "Unity Hub" || process.ProcessName == "Unity.Licensing.Client")
            process.Kill();
        }
        catch { }
      }
      System.Threading.Thread.Sleep(3000);
    }
  }

  internal class DapProcessClient
  {
    readonly string m_AdapterPath;
    readonly string m_LogPath;
    readonly double m_RequestTimeoutSeconds;
    readonly BlockingCollection<JObject> m_Messages = new BlockingCollection<JObject>();
    readonly Queue<JObject> m_PendingEvents = new Queue<JObject>();
    int m_Seq = 1;
    Process m_Process;

    public List<JObject> Events { get; } = new List<JObject>();
    public List<JObject> Transcript { get; } = new List<JObject>();
    public List<string> StderrLines { get; } = new List<string>();
    public bool IsRunning => m_Process != null && !m_Process.HasExited;

    public DapProcessClient(string adapterPath, string logPath, double requestTimeoutSeconds)
    {
      m_AdapterPath = adapterPath;
      m_LogPath = logPath;
      m_RequestTimeoutSeconds = requestTimeoutSeconds;
    }

    public void Start()
    {
      Directory.CreateDirectory(Path.GetDirectoryName(m_LogPath));
      m_Process = new Process();
      m_Process.StartInfo.FileName = m_AdapterPath;
      m_Process.StartInfo.Arguments = $"--log-level=trace --log-file=\"{m_LogPath}\"";
      m_Process.StartInfo.WorkingDirectory = Path.GetDirectoryName(m_AdapterPath);
      m_Process.StartInfo.UseShellExecute = false;
      m_Process.StartInfo.RedirectStandardInput = true;
      m_Process.StartInfo.RedirectStandardOutput = true;
      m_Process.StartInfo.RedirectStandardError = true;
      m_Process.StartInfo.CreateNoWindow = true;
      m_Process.Start();

      new System.Threading.Thread(ReadStdout) { IsBackground = true }.Start();
      new System.Threading.Thread(ReadStderr) { IsBackground = true }.Start();
    }

    public JObject Request(string command, JObject arguments, double? timeoutSeconds = null)
    {
      if (m_Process == null || m_Process.HasExited)
        throw new InvalidOperationException("adapter is not running");

      var seq = m_Seq++;
      var request = new JObject
      {
        ["seq"] = seq,
        ["command"] = command,
        ["type"] = "request"
      };
      if (arguments != null)
        request["arguments"] = arguments;

      var body = Encoding.UTF8.GetBytes(request.ToString(Formatting.None));
      var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
      Transcript.Add(new JObject
      {
        ["direction"] = "send",
        ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ["message"] = request.DeepClone()
      });

      if (m_Process.HasExited)
        throw new InvalidOperationException("adapter exited before request " + command + " with code " + m_Process.ExitCode);

      m_Process.StandardInput.BaseStream.Write(header, 0, header.Length);
      m_Process.StandardInput.BaseStream.Write(body, 0, body.Length);
      m_Process.StandardInput.BaseStream.Flush();

      return WaitResponse(seq, timeoutSeconds ?? m_RequestTimeoutSeconds);
    }

    JObject WaitResponse(int requestSeq, double timeoutSeconds)
    {
      var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
      while (DateTime.UtcNow < deadline)
      {
        var remaining = deadline - DateTime.UtcNow;
        if (!m_Messages.TryTake(out var message, Math.Max(1, (int)remaining.TotalMilliseconds)))
        {
          if (m_Process.HasExited)
            throw new TimeoutException("adapter exited with code " + m_Process.ExitCode);
          continue;
        }

        Record(message);
        if ((string)message["type"] == "event" && (string)message["event"] == "terminated")
          throw new TimeoutException("debuggee terminated while waiting for response");
        if ((string)message["type"] == "response" && (int?)message["request_seq"] == requestSeq)
          return message;
      }

      throw new TimeoutException("timed out waiting for response to request seq " + requestSeq);
    }

    public JObject WaitEvent(string eventName, double timeoutSeconds)
    {
      var pendingEvent = TakePendingEvent(eventName);
      if (pendingEvent != null)
        return pendingEvent;

      var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
      while (DateTime.UtcNow < deadline)
      {
        var remaining = deadline - DateTime.UtcNow;
        if (!m_Messages.TryTake(out var message, Math.Max(1, (int)remaining.TotalMilliseconds)))
        {
          if (m_Process.HasExited)
            throw new TimeoutException("adapter exited with code " + m_Process.ExitCode);
          continue;
        }

        var isTargetEvent = (string)message["type"] == "event" && (string)message["event"] == eventName;
        Record(message, bufferEvent: !isTargetEvent);
        if ((string)message["type"] == "event" && (string)message["event"] == "terminated")
          throw new TimeoutException("debuggee terminated while waiting for event");
        if (isTargetEvent)
          return message;
      }

      throw new TimeoutException("timed out waiting for event " + eventName);
    }

    public JObject WaitOutputContains(string text, double timeoutSeconds)
    {
      var existing = Events.FirstOrDefault(IsMatchingOutput);
      if (existing != null)
        return existing;

      var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
      while (DateTime.UtcNow < deadline)
      {
        var remaining = deadline - DateTime.UtcNow;
        if (!m_Messages.TryTake(out var message, Math.Max(1, (int)remaining.TotalMilliseconds)))
        {
          if (m_Process.HasExited)
            throw new TimeoutException("adapter exited with code " + m_Process.ExitCode);
          continue;
        }

        Record(message);
        if ((string)message["type"] == "event" && (string)message["event"] == "terminated")
          throw new TimeoutException("debuggee terminated while waiting for output");
        if (IsMatchingOutput(message))
          return message;
      }

      throw new TimeoutException("timed out waiting for output containing " + text);

      bool IsMatchingOutput(JObject message)
      {
        return (string)message["type"] == "event"
            && (string)message["event"] == "output"
            && ((string)message["body"]?["output"])?.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
      }
    }

    public JObject Disconnect()
    {
      if (!IsRunning)
        return null;
      try
      {
        return Request("disconnect", new JObject
        {
          ["restart"] = false,
          ["terminateDebuggee"] = false
        }, 5.0);
      }
      catch
      {
        return null;
      }
    }

    public void Stop()
    {
      if (m_Process == null || m_Process.HasExited)
        return;
      try
      {
        m_Process.Kill();
        m_Process.WaitForExit(2000);
        m_Process.Dispose();
      }
      catch { }
    }

    void ReadStdout()
    {
      var stream = m_Process.StandardOutput.BaseStream;
      while (true)
      {
        var header = ReadUntil(stream, Encoding.ASCII.GetBytes("\r\n\r\n"));
        if (header == null || header.Length == 0)
          return;

        var headerText = Encoding.ASCII.GetString(header);
        var marker = "Content-Length: ";
        var index = headerText.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
          return;
        var start = index + marker.Length;
        var end = headerText.IndexOf("\r\n", start, StringComparison.Ordinal);
        if (end < 0)
          return;
        if (!int.TryParse(headerText.Substring(start, end - start), out var length))
          return;

        var body = ReadExact(stream, length);
        if (body == null)
          return;
        m_Messages.Add(JObject.Parse(Encoding.UTF8.GetString(body)));
      }
    }

    void ReadStderr()
    {
      string line;
      while ((line = m_Process.StandardError.ReadLine()) != null)
        StderrLines.Add(line);
    }

    void Record(JObject message, bool bufferEvent = true)
    {
      Transcript.Add(new JObject
      {
        ["direction"] = "recv",
        ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ["message"] = message.DeepClone()
      });
      if ((string)message["type"] == "event")
      {
        var eventCopy = (JObject)message.DeepClone();
        Events.Add(eventCopy);
        if (bufferEvent)
          m_PendingEvents.Enqueue((JObject)eventCopy.DeepClone());
      }
    }

    JObject TakePendingEvent(string eventName)
    {
      var skippedEvents = new List<JObject>();
      JObject matchingEvent = null;
      while (m_PendingEvents.Count > 0)
      {
        var pendingEvent = m_PendingEvents.Dequeue();
        if (matchingEvent == null && (string)pendingEvent["event"] == eventName)
        {
          matchingEvent = pendingEvent;
          break;
        }
        skippedEvents.Add(pendingEvent);
      }

      var remainingEvents = m_PendingEvents.ToArray();
      m_PendingEvents.Clear();
      foreach (var skippedEvent in skippedEvents)
        m_PendingEvents.Enqueue(skippedEvent);
      foreach (var remainingEvent in remainingEvents)
        m_PendingEvents.Enqueue(remainingEvent);

      return matchingEvent;
    }

    static byte[] ReadUntil(Stream stream, byte[] marker)
    {
      var data = new List<byte>();
      while (true)
      {
        var value = stream.ReadByte();
        if (value < 0)
          return data.Count == 0 ? null : data.ToArray();
        data.Add((byte)value);
        if (data.Count >= marker.Length)
        {
          var match = true;
          for (var i = 0; i < marker.Length; i++)
          {
            if (data[data.Count - marker.Length + i] != marker[i])
            {
              match = false;
              break;
            }
          }
          if (match)
            return data.ToArray();
        }
      }
    }

    static byte[] ReadExact(Stream stream, int length)
    {
      var data = new byte[length];
      var offset = 0;
      while (offset < length)
      {
        var read = stream.Read(data, offset, length - offset);
        if (read == 0)
          return null;
        offset += read;
      }
      return data;
    }
  }

  internal static class McpEnumerableExtensions
  {
    public static IEnumerable<T> TakeLastSafe<T>(this IEnumerable<T> source, int count)
    {
      var queue = new Queue<T>();
      foreach (var item in source)
      {
        queue.Enqueue(item);
        while (queue.Count > count)
          queue.Dequeue();
      }
      return queue;
    }
  }
}
