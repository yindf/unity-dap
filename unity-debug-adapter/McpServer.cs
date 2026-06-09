using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
          Tool("unity_debug_start", "Start a Unity Editor process, then initialize and attach the DAP adapter.",
              Obj(
                Prop("projectPath", "string", "Unity project path. Defaults to the repository E2E fixture."),
                Prop("sourcePath", "string", "Default source file path for later breakpoint requests."),
                Prop("unityExe", "string", "Unity.exe path. Defaults to latest Unity Hub 2022.3 editor."),
                Prop("startupTimeoutSeconds", "number", "Attach retry timeout. Default 90."),
                Prop("requestTimeoutSeconds", "number", "DAP request timeout. Default 15."),
                Prop("killUnityHubLicensing", "boolean", "Stop Unity Hub and Unity.Licensing.Client before starting Unity. Use when licensing mutex blocks the editor.")
              )),
          Tool("unity_debug_attach", "Attach to an existing Unity Editor process, then initialize and attach the DAP adapter.",
              Obj(
                Required("unityPid", "integer", "Existing Unity Editor process id."),
                Prop("projectPath", "string", "Unity project path. Defaults to the repository E2E fixture."),
                Prop("sourcePath", "string", "Default source file path for later breakpoint requests."),
                Prop("startupTimeoutSeconds", "number", "Attach retry timeout. Default 90."),
                Prop("requestTimeoutSeconds", "number", "DAP request timeout. Default 15.")
              )),
          Tool("unity_debug_run_flow", "Run a bounded attach, breakpoint, stopped-event, snapshot, continue, and disconnect flow.",
              Obj(
                Prop("projectPath", "string", "Unity project path. Defaults to the repository E2E fixture."),
                Prop("sourcePath", "string", "Source file path. Defaults to fixture TestScript.cs."),
                Prop("unityExe", "string", "Unity.exe path. Defaults to latest Unity Hub 2022.3 editor."),
                Prop("unityPid", "integer", "Attach an existing Unity Editor process instead of starting one."),
                Prop("lines", "array", "Breakpoint lines. Defaults to 22 and 26 for the fixture."),
                ArrayProp("expressions", "string", "Expressions to evaluate at each stop."),
                Prop("stopCount", "integer", "Number of stopped events to collect. Default 2."),
                Prop("startupTimeoutSeconds", "number", "Attach retry timeout. Default 90."),
                Prop("stopTimeoutSeconds", "number", "Stopped-event timeout. Default 60."),
                Prop("requestTimeoutSeconds", "number", "DAP request timeout. Default 15."),
                Prop("disconnectOnComplete", "boolean", "Disconnect and close Unity at the end. Default true."),
                Prop("killUnityHubLicensing", "boolean", "Stop Unity Hub and Unity.Licensing.Client before starting Unity. Use when licensing mutex blocks the editor.")
              )),
          Tool("unity_debug_prepare", "Start or attach, make the session active, and optionally set breakpoints.",
              Obj(
                Prop("projectPath", "string", "Unity project path. Defaults to the repository E2E fixture."),
                Prop("sourcePath", "string", "Default source file path for later breakpoint requests."),
                Prop("unityExe", "string", "Unity.exe path. Defaults to latest Unity Hub 2022.3 editor."),
                Prop("unityPid", "integer", "Attach an existing Unity Editor process instead of starting one."),
                Prop("lines", "array", "Breakpoint lines. Kept for compatibility; use breakpoints for conditions/logpoints."),
                BreakpointsProp("breakpoints", "Breakpoint specs: { line, column, condition, hitCondition, logMessage }."),
                Prop("startupTimeoutSeconds", "number", "Attach retry timeout. Default 90."),
                Prop("requestTimeoutSeconds", "number", "DAP request timeout. Default 15."),
                Prop("killUnityHubLicensing", "boolean", "Stop Unity Hub and Unity.Licensing.Client before starting Unity. Use when licensing mutex blocks the editor."),
                Prop("setExceptionBreakpoints", "boolean", "Also send an empty setExceptionBreakpoints request. Default false.")
              )),
          Tool("unity_debug_enter_play_and_stop", "Enter Play Mode, wait for a stopped event, and return a snapshot.",
              Obj(
                Prop("sessionId", "string", "Session id. Defaults to the active session."),
                Prop("projectPath", "string", "Unity project path. Defaults to the session project path."),
                Prop("timeoutSeconds", "number", "Stopped-event timeout. Default 60."),
                ArrayProp("expressions", "string", "Expressions to evaluate in the snapshot.")
              )),
          Tool("unity_debug_resume_until_stopped", "Continue, wait for the next stopped event, and return a snapshot.",
              Obj(
                Prop("sessionId", "string", "Session id. Defaults to the active session."),
                Prop("threadId", "integer", "Thread id. Defaults to current stopped thread."),
                Prop("timeoutSeconds", "number", "Stopped-event timeout. Default 60."),
                ArrayProp("expressions", "string", "Expressions to evaluate in the snapshot.")
              )),
          Tool("unity_debug_diagnose", "Return status plus recent adapter/transcript log tail for a session.",
              Obj(
                Prop("sessionId", "string", "Session id. Defaults to the active session."),
                Prop("tailLines", "integer", "Number of log lines to return. Default 80.")
              )),
          Tool("unity_debug_set_breakpoints", "Set source breakpoints in the active Unity debug session.",
              Obj(
                Prop("sessionId", "string", "Session id returned by unity_debug_start or unity_debug_attach. Defaults to the active session."),
                Prop("sourcePath", "string", "Source file path. Defaults to fixture TestScript.cs."),
                Prop("lines", "array", "Breakpoint lines."),
                BreakpointsProp("breakpoints", "Breakpoint specs: { line, column, condition, hitCondition, logMessage }.")
              )),
          Tool("unity_debug_add_breakpoints", "Add source breakpoints and synchronize the full source breakpoint set to the debug session.",
              Obj(
                Prop("sessionId", "string", "Session id returned by unity_debug_start or unity_debug_attach. Defaults to the active session."),
                Prop("sourcePath", "string", "Source file path. Defaults to fixture TestScript.cs."),
                Prop("lines", "array", "Breakpoint lines to add."),
                BreakpointsProp("breakpoints", "Breakpoint specs to add: { line, column, condition, hitCondition, logMessage }.")
              )),
          Tool("unity_debug_remove_breakpoints", "Remove source breakpoints and synchronize the full source breakpoint set to the debug session.",
              Obj(
                Prop("sessionId", "string", "Session id returned by unity_debug_start or unity_debug_attach. Defaults to the active session."),
                Prop("sourcePath", "string", "Source file path. Defaults to fixture TestScript.cs."),
                Required("lines", "array", "Breakpoint lines to remove.")
              )),
          Tool("unity_debug_update_breakpoint", "Update one source breakpoint line and synchronize the full source breakpoint set to the debug session.",
              Obj(
                Prop("sessionId", "string", "Session id returned by unity_debug_start or unity_debug_attach. Defaults to the active session."),
                Prop("sourcePath", "string", "Source file path. Defaults to fixture TestScript.cs."),
                Required("oldLine", "integer", "Existing breakpoint line to replace."),
                Required("newLine", "integer", "New breakpoint line."),
                Prop("condition", "string", "Optional condition expression for the updated breakpoint."),
                Prop("hitCondition", "string", "Optional hit condition for the updated breakpoint."),
                Prop("logMessage", "string", "Optional logpoint message/expression for the updated breakpoint.")
              )),
          Tool("unity_debug_clear_breakpoints", "Clear breakpoints for one source, or for all tracked sources when sourcePath is omitted.",
              Obj(
                Prop("sessionId", "string", "Session id returned by unity_debug_start or unity_debug_attach. Defaults to the active session."),
                Prop("sourcePath", "string", "Source file path. Defaults to all tracked sources.")
              )),
          Tool("unity_debug_list_breakpoints", "List breakpoints currently tracked by this MCP debug session.",
              Obj(
                Prop("sessionId", "string", "Session id returned by unity_debug_start or unity_debug_attach. Defaults to the active session."),
                Prop("sourcePath", "string", "Optional source path filter.")
              )),
          Tool("unity_debug_enter_play_mode", "Ask the Unity Editor project for this session to enter Play Mode.",
              Obj(
                Prop("sessionId", "string", "Session id. Defaults to the active session."),
                Prop("projectPath", "string", "Unity project path. Defaults to the session project path.")
              )),
          Tool("unity_debug_wait_stopped", "Wait for a stopped event with a hard timeout.",
              Obj(
                Prop("sessionId", "string", "Session id. Defaults to the active session."),
                Prop("timeoutSeconds", "number", "Wait timeout. Default 60.")
              )),
          Tool("unity_debug_snapshot", "Read stack, scopes, variables, and evaluate expressions at the current stop.",
              Obj(
                Prop("sessionId", "string", "Session id. Defaults to the active session."),
                Prop("threadId", "integer", "Thread id from stopped event. Defaults to current stopped thread."),
                ArrayProp("expressions", "string", "Expressions to evaluate. Defaults to this, m_Radius, s_StaticBoolVar, transform.position.")
              )),
          Tool("unity_debug_continue", "Continue the stopped Unity debuggee.",
              Obj(
                Prop("sessionId", "string", "Session id. Defaults to the active session."),
                Prop("threadId", "integer", "Thread id. Defaults to current stopped thread.")
              )),
          Tool("unity_debug_disconnect", "Disconnect the adapter and clean the session. Safe to call more than once.",
              Obj(Prop("sessionId", "string", "Session id. Defaults to the active session."))),
          Tool("unity_debug_status", "Return session status, latest events, and log paths.",
              Obj(Prop("sessionId", "string", "Session id. Defaults to the active session."))),
          Tool("unity_debug_cleanup", "Force cleanup a session, or all sessions when no sessionId is provided.",
              Obj(Prop("sessionId", "string", "Session id.")))
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
        case "unity_debug_disconnect":
          result = WithSession(args, s =>
          {
            var r = s.Disconnect();
            m_Sessions.Remove(s.SessionId);
            if (m_ActiveSessionId == s.SessionId)
              m_ActiveSessionId = null;
            return r;
          });
          break;
        case "unity_debug_status":
          result = WithSession(args, s => s.Status());
          break;
        case "unity_debug_cleanup":
          result = ToolCleanup(args);
          break;
        default:
          throw new InvalidOperationException("Unknown tool: " + name);
      }

      return ToolText(result);
    }

    object ToolStart(JObject args)
    {
      var session = new McpDebugSession(args);
      try
      {
        var result = session.Start();
        m_Sessions[session.SessionId] = session;
        m_ActiveSessionId = session.SessionId;
        return result;
      }
      catch
      {
        session.Cleanup();
        throw;
      }
    }

    object ToolAttach(JObject args)
    {
      if ((int?)args["unityPid"] == null)
        throw new InvalidOperationException("unityPid is required for unity_debug_attach");

      return ToolStart(args);
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

        disconnect = session.Disconnect();
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
      var session = new McpDebugSession(args);
      try
      {
        var start = session.Start();
        m_Sessions[session.SessionId] = session;
        m_ActiveSessionId = session.SessionId;

        object breakpoints = null;
        object exceptionBreakpoints = null;
        if (args["lines"] != null || args["breakpoints"] != null)
          breakpoints = session.SetBreakpoints(args);
        if ((bool?)args["setExceptionBreakpoints"] ?? false)
          exceptionBreakpoints = session.SetExceptionBreakpoints();

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
        session.Cleanup();
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
            ["text"] = structured.ToString(Formatting.Indented)
          }
        },
        ["structuredContent"] = structured
      };
    }

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
    readonly string m_ProjectPath;
    readonly string m_SourcePath;
    readonly string m_UnityExe;
    readonly int? m_UnityPid;
    readonly bool m_KillUnityHubLicensing;
    readonly double m_StartupTimeoutSeconds;
    readonly double m_RequestTimeoutSeconds;

    Process m_UnityProcess;
    DapProcessClient m_Client;
    JObject m_LastStoppedEvent;
    readonly Dictionary<string, SortedDictionary<int, McpBreakpointSpec>> m_BreakpointsBySource = new Dictionary<string, SortedDictionary<int, McpBreakpointSpec>>(StringComparer.OrdinalIgnoreCase);
    bool m_Attached;
    bool m_OwnsUnityProcess;

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
      m_ProjectPath = FullPath((string)args["projectPath"] ?? "unity-debug-adapter.E2ETests/unity_test_project_2022_3");
      m_SourcePath = FullPath((string)args["sourcePath"] ?? Path.Combine(m_ProjectPath, "Assets", "Scripts", "TestScript.cs"));
      m_UnityExe = (string)args["unityExe"] ?? FindUnityExe();
      m_UnityPid = (int?)args["unityPid"];
      m_KillUnityHubLicensing = (bool?)args["killUnityHubLicensing"] ?? false;
      m_StartupTimeoutSeconds = (double?)args["startupTimeoutSeconds"] ?? 90.0;
      m_RequestTimeoutSeconds = (double?)args["requestTimeoutSeconds"] ?? 15.0;

      var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcp-logs", SessionId);
      Directory.CreateDirectory(logDir);
      AdapterLogPath = Path.Combine(logDir, "adapter.log");
      UnityLogPath = Path.Combine(logDir, "unity.log");
      TranscriptPath = Path.Combine(logDir, "transcript.json");
    }

    public object Start()
    {
      if (!File.Exists(m_AdapterPath))
        throw new FileNotFoundException("adapter executable not found", m_AdapterPath);
      if (!Directory.Exists(m_ProjectPath))
        throw new DirectoryNotFoundException("Unity project not found: " + m_ProjectPath);

      if (m_KillUnityHubLicensing)
        KillUnityHubLicensing();

      if (m_UnityPid.HasValue)
      {
        UnityPid = m_UnityPid.Value;
        m_UnityProcess = TryGetProcessById(UnityPid);
        if (m_UnityProcess == null)
          throw new InvalidOperationException($"Unity process {UnityPid} was not found");
      }
      else
      {
        if (string.IsNullOrWhiteSpace(m_UnityExe) || !File.Exists(m_UnityExe))
          throw new FileNotFoundException("Unity editor not found", m_UnityExe);
        m_UnityProcess = StartUnity();
        m_OwnsUnityProcess = true;
        UnityPid = m_UnityProcess.Id;
        if (m_UnityProcess.HasExited)
          throw new InvalidOperationException($"Unity exited during startup with code {m_UnityProcess.ExitCode}; see {UnityLogPath}");
      }

      Port = 56000 + UnityPid % 1000;
      m_Client = new DapProcessClient(m_AdapterPath, AdapterLogPath, m_RequestTimeoutSeconds);
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

      var deadline = DateTime.UtcNow.AddSeconds(m_StartupTimeoutSeconds);
      Exception last = null;
      while (DateTime.UtcNow < deadline)
      {
        if (!IsUnityProcessAlive())
          throw new InvalidOperationException($"Unity process {UnityPid} exited during startup; see {UnityLogPath}");

        try
        {
          EnsureSuccess(m_Client.Request("attach", new JObject
          {
            ["address"] = "127.0.0.1",
            ["request"] = "attach",
            ["name"] = $"Connect to Unity Editor instance at 127.0.0.1:{Port}",
            ["type"] = "unity",
            ["port"] = Port
          }, 8.0));
          var readyTimeout = Math.Max(1.0, (deadline - DateTime.UtcNow).TotalSeconds);
          m_Client.WaitOutputContains("UnityDebugAdapter: target ready", readyTimeout);
          m_Attached = true;
          SaveTranscript();
          return Status();
        }
        catch (Exception e)
        {
          last = e;
          if (e is TimeoutException && last.Message.IndexOf("target ready", StringComparison.OrdinalIgnoreCase) >= 0)
            throw new TimeoutException("attach completed but target ready was not observed: " + e.Message, e);
          System.Threading.Thread.Sleep(2000);
        }
      }

      throw new TimeoutException("attach timed out: " + last?.Message);
    }

    public object SetBreakpoints(JObject args)
    {
      var sourcePath = FullPath((string)args["sourcePath"] ?? m_SourcePath);
      m_BreakpointsBySource[sourcePath] = ReadBreakpointSpecs(args, requireBreakpoints: true);
      return SyncBreakpoints(sourcePath);
    }

    public object AddBreakpoints(JObject args)
    {
      var sourcePath = FullPath((string)args["sourcePath"] ?? m_SourcePath);
      var tracked = GetTrackedBreakpoints(sourcePath);
      foreach (var breakpoint in ReadBreakpointSpecs(args, requireBreakpoints: true).Values)
        tracked[breakpoint.Line] = breakpoint;
      return SyncBreakpoints(sourcePath);
    }

    public object RemoveBreakpoints(JObject args)
    {
      var sourcePath = FullPath((string)args["sourcePath"] ?? m_SourcePath);
      var lineSet = ReadLineSet(args, requireLines: true);
      var tracked = GetTrackedBreakpoints(sourcePath);
      foreach (var line in lineSet)
        tracked.Remove(line);
      return SyncBreakpoints(sourcePath);
    }

    public object UpdateBreakpoint(JObject args)
    {
      var sourcePath = FullPath((string)args["sourcePath"] ?? m_SourcePath);
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
        var sourcePath = FullPath(explicitSourcePath);
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
        var sourcePath = FullPath(explicitSourcePath);
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
      var projectPath = FullPath((string)args["projectPath"] ?? m_ProjectPath);
      if (!Directory.Exists(projectPath))
        throw new DirectoryNotFoundException("Unity project not found: " + projectPath);

      var tempPath = Path.Combine(projectPath, "Temp");
      Directory.CreateDirectory(tempPath);
      var triggerPath = Path.Combine(tempPath, "mcp-enter-play-mode");
      File.WriteAllText(triggerPath, DateTimeOffset.UtcNow.ToString("O"), Encoding.UTF8);

      return new
      {
        sessionId = SessionId,
        projectPath,
        triggerPath,
        requested = true
      };
    }

    public object WaitStopped(JObject args)
    {
      var timeout = (double?)args["timeoutSeconds"] ?? 60.0;
      m_LastStoppedEvent = m_Client.WaitEvent("stopped", timeout);
      SaveTranscript();
      return new
      {
        sessionId = SessionId,
        stopped = m_LastStoppedEvent["body"]
      };
    }

    public object Snapshot(JObject args)
    {
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

      var frames = stack["body"]?["stackFrames"] as JArray ?? new JArray();
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
        scopes = scopesResponse["body"]?["scopes"] as JArray ?? new JArray();

        foreach (var scope in scopes)
        {
          var reference = (int?)scope["variablesReference"] ?? 0;
          if (reference <= 0)
            continue;
          var variablesResponse = m_Client.Request("variables", new JObject { ["variablesReference"] = reference });
          EnsureSuccess(variablesResponse);
          foreach (var variable in variablesResponse["body"]?["variables"] as JArray ?? new JArray())
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
        response = response["body"]
      };
    }

    public object EnterPlayAndStop(JObject args)
    {
      var enterPlayMode = EnterPlayMode(args);
      var stopped = WaitStopped(args);
      var snapshot = Snapshot(args);
      return new
      {
        sessionId = SessionId,
        enterPlayMode,
        stopped,
        snapshot,
        status = Status()
      };
    }

    public object ResumeUntilStopped(JObject args)
    {
      var continued = Continue(args);
      var stopped = WaitStopped(args);
      var snapshot = Snapshot(args);
      return new
      {
        sessionId = SessionId,
        continued,
        stopped,
        snapshot,
        status = Status()
      };
    }

    public object Disconnect()
    {
      JObject response = null;
      if (m_Client != null)
        response = m_Client.Disconnect();
      Cleanup();
      return new
      {
        sessionId = SessionId,
        disconnected = response != null,
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
        adapterLogPath = AdapterLogPath,
        unityLogPath = UnityLogPath,
        transcriptPath = TranscriptPath,
        recentEvents = m_Client?.Events.TakeLastSafe(8).ToArray() ?? new JObject[0]
      };
    }

    public object Diagnose(JObject args)
    {
      var tailLines = Math.Max(1, (int?)args["tailLines"] ?? 80);
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
      try
      {
        if (m_Client != null)
        {
          m_Client.Stop();
          SaveTranscript();
        }
      }
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
    }

    Process StartUnity()
    {
      var process = new Process();
      process.StartInfo.FileName = m_UnityExe;
      process.StartInfo.Arguments = $"-projectPath \"{m_ProjectPath}\" -logFile \"{UnityLogPath}\" -executeMethod UnityEditor.EditorApplication.EnterPlaymode";
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
        throw new InvalidOperationException("DAP request failed: " + response.ToString(Formatting.None));
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
          throw new TimeoutException("debuggee terminated while waiting for event");
        if ((string)message["type"] == "event" && (string)message["event"] == eventName)
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
          ["terminateDebuggee"] = true
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

    void Record(JObject message)
    {
      Transcript.Add(new JObject
      {
        ["direction"] = "recv",
        ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ["message"] = message.DeepClone()
      });
      if ((string)message["type"] == "event")
        Events.Add((JObject)message.DeepClone());
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
