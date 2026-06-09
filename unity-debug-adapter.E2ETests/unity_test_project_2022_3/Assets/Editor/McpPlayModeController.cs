using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class McpPlayModeController
{
  const string EnterWhenEditModeKey = "unity-dap.mcp.enter-when-edit-mode";

  static readonly Queue<PendingCommand> s_PendingCommands = new Queue<PendingCommand>();
  static readonly object s_Lock = new object();
  static TcpListener s_Listener;
  static Thread s_ServerThread;
  static bool s_Stopping;

  static McpPlayModeController()
  {
    EditorApplication.update += Update;
    EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    StartServer();
    Debug.Log("MCP Unity control server initialized on port " + ControlPort);
  }

  static int ControlPort
  {
    get { return 57000 + Math.Abs(System.Diagnostics.Process.GetCurrentProcess().Id % 1000); }
  }

  static void StartServer()
  {
    if (s_Listener != null)
      return;

    try
    {
      s_Stopping = false;
      s_Listener = new TcpListener(IPAddress.Loopback, ControlPort);
      s_Listener.Start();
      s_ServerThread = new Thread(ServerLoop);
      s_ServerThread.IsBackground = true;
      s_ServerThread.Name = "MCP Unity control server";
      s_ServerThread.Start();
      AssemblyReloadEvents.beforeAssemblyReload += StopServer;
      EditorApplication.quitting += StopServer;
    }
    catch (Exception e)
    {
      Debug.LogWarning("MCP Unity control server could not start: " + e.Message);
      StopServer();
    }
  }

  static void StopServer()
  {
    s_Stopping = true;
    try
    {
      if (s_Listener != null)
        s_Listener.Stop();
    }
    catch { }
    s_Listener = null;
  }

  static void ServerLoop()
  {
    while (!s_Stopping)
    {
      try
      {
        var client = s_Listener.AcceptTcpClient();
        var thread = new Thread(() => HandleClient(client));
        thread.IsBackground = true;
        thread.Name = "MCP Unity control client";
        thread.Start();
      }
      catch
      {
        if (!s_Stopping)
          Thread.Sleep(100);
      }
    }
  }

  static void HandleClient(TcpClient client)
  {
    using (client)
    {
      var pending = new PendingCommand();
      try
      {
        client.ReceiveTimeout = 15000;
        client.SendTimeout = 15000;
        string request;
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8);
        request = reader.ReadLine();

        pending.Request = JsonUtility.FromJson<ControlRequest>(request ?? "{}");
        if (pending.Request == null)
          pending.Request = new ControlRequest();

        lock (s_Lock)
          s_PendingCommands.Enqueue(pending);
        pending.WaitHandle.WaitOne(15000);
      }
      catch (Exception e)
      {
        pending.Response = ControlResponse.Error("transport-failed", e.Message);
      }

      try
      {
        var response = pending.Response ?? ControlResponse.Error("timeout", "Unity did not process the MCP control command in time.");
        var json = JsonUtility.ToJson(response);
        var data = Encoding.UTF8.GetBytes(json + "\n");
        client.GetStream().Write(data, 0, data.Length);
      }
      catch { }
      finally
      {
        pending.WaitHandle.Close();
      }
    }
  }

  static void Update()
  {
    if (SessionState.GetBool(EnterWhenEditModeKey, false) && !EditorApplication.isPlayingOrWillChangePlaymode)
    {
      SessionState.SetBool(EnterWhenEditModeKey, false);
      Debug.Log("MCP control entering Play Mode after reset");
      EditorApplication.EnterPlaymode();
    }

    while (true)
    {
      PendingCommand pending = null;
      lock (s_Lock)
      {
        if (s_PendingCommands.Count > 0)
          pending = s_PendingCommands.Dequeue();
      }

      if (pending == null)
        return;

      try
      {
        pending.Response = Execute(pending.Request);
      }
      catch (Exception e)
      {
        pending.Response = ControlResponse.Error("execute-failed", e.GetBaseException().Message);
      }
      finally
      {
        pending.WaitHandle.Set();
      }
    }
  }

  static ControlResponse Execute(ControlRequest request)
  {
    var command = string.IsNullOrWhiteSpace(request.command) ? "status" : request.command.Trim().ToLowerInvariant();
    ControlResponse response;
    switch (command)
    {
      case "status":
        response = ControlResponse.Ok("status", "ready");
        break;
      case "enterplay":
        response = EnterPlayMode();
        break;
      case "runtests":
        response = RunTests(request);
        break;
      default:
        response = ControlResponse.Error("unknown-command", "Unknown MCP Unity control command: " + request.command);
        break;
    }

    response.isPlaying = EditorApplication.isPlaying;
    response.isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode;
    return response;
  }

  static ControlResponse EnterPlayMode()
  {
    if (EditorApplication.isPlayingOrWillChangePlaymode)
    {
      SessionState.SetBool(EnterWhenEditModeKey, true);
      Debug.Log("MCP control resetting active Play Mode");
      EditorApplication.isPlaying = false;
      return ControlResponse.Ok("exit-before-reenter", "Play Mode reset requested before re-entering.");
    }

    Debug.Log("MCP control entering Play Mode");
    EditorApplication.EnterPlaymode();
    return ControlResponse.Ok("enter", "Play Mode enter requested.");
  }

  static ControlResponse RunTests(ControlRequest request)
  {
    string detail;
    if (!TryRunTests(request, out detail))
      return ControlResponse.Error("test-runner-unavailable", detail);
    return ControlResponse.Ok("tests-started", detail);
  }

  static bool TryRunTests(ControlRequest request, out string detail)
  {
    var apiType = FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi");
    var filterType = FindType("UnityEditor.TestTools.TestRunner.Api.Filter");
    var settingsType = FindType("UnityEditor.TestTools.TestRunner.Api.ExecutionSettings");
    if (apiType == null || filterType == null || settingsType == null)
    {
      detail = "Unity Test Runner API was not found. Install or enable com.unity.test-framework, then attach before triggering tests.";
      return false;
    }

    try
    {
      var filter = Activator.CreateInstance(filterType);
      var mode = string.IsNullOrWhiteSpace(request.testMode) ? "EditMode" : request.testMode.Trim();
      if (!string.Equals(mode, "All", StringComparison.OrdinalIgnoreCase))
      {
        var modeType = FindType("UnityEditor.TestTools.TestRunner.Api.TestMode");
        if (modeType == null)
        {
          detail = "Unity Test Runner TestMode type was not found.";
          return false;
        }

        SetMember(filterType, filter, "testMode", Enum.Parse(modeType, mode, true));
      }

      if (!string.IsNullOrWhiteSpace(request.testFilter))
        SetMember(filterType, filter, "testNames", new[] { request.testFilter.Trim() });

      var filterArray = Array.CreateInstance(filterType, 1);
      filterArray.SetValue(filter, 0);
      var settings = CreateExecutionSettings(settingsType, filterType, filterArray);
      var api = typeof(ScriptableObject).IsAssignableFrom(apiType)
          ? ScriptableObject.CreateInstance(apiType)
          : Activator.CreateInstance(apiType);
      var execute = apiType.GetMethod("Execute", new[] { settingsType })
          ?? apiType.GetMethod("Execute", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
      if (execute == null)
      {
        detail = "Unity Test Runner Execute method was not found.";
        return false;
      }

      execute.Invoke(api, new[] { settings });
      detail = "mode=" + mode + ", filter=" + (request.testFilter ?? "");
      return true;
    }
    catch (Exception e)
    {
      detail = e.GetBaseException().Message;
      return false;
    }
  }

  static object CreateExecutionSettings(Type settingsType, Type filterType, Array filterArray)
  {
    foreach (var constructor in settingsType.GetConstructors())
    {
      var parameters = constructor.GetParameters();
      if (parameters.Length == 1 && parameters[0].ParameterType.IsArray &&
          parameters[0].ParameterType.GetElementType() == filterType)
        return constructor.Invoke(new object[] { filterArray });
    }

    var settings = Activator.CreateInstance(settingsType);
    SetMember(settingsType, settings, "filters", filterArray);
    return settings;
  }

  static bool SetMember(Type type, object target, string name, object value)
  {
    var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
    var property = type.GetProperty(name, flags);
    if (property != null && property.CanWrite)
    {
      property.SetValue(target, value, null);
      return true;
    }

    var field = type.GetField(name, flags);
    if (field != null)
    {
      field.SetValue(target, value);
      return true;
    }

    return false;
  }

  static Type FindType(string fullName)
  {
    var type = Type.GetType(fullName);
    if (type != null)
      return type;

    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
    {
      try
      {
        type = assembly.GetType(fullName);
        if (type != null)
          return type;
      }
      catch { }
    }

    return null;
  }

  static void OnPlayModeStateChanged(PlayModeStateChange state)
  {
    Debug.Log("MCP control Play Mode state: " + state);
  }

  class PendingCommand
  {
    public ControlRequest Request;
    public ControlResponse Response;
    public readonly ManualResetEvent WaitHandle = new ManualResetEvent(false);
  }

  [Serializable]
  class ControlRequest
  {
    public string command;
    public string testMode;
    public string testFilter;
  }

  [Serializable]
  class ControlResponse
  {
    public bool ok;
    public string action;
    public string detail;
    public bool isPlaying;
    public bool isPlayingOrWillChangePlaymode;
    public int processId;
    public int controlPort;

    public static ControlResponse Ok(string action, string detail)
    {
      return Create(true, action, detail);
    }

    public static ControlResponse Error(string action, string detail)
    {
      return Create(false, action, detail);
    }

    static ControlResponse Create(bool ok, string action, string detail)
    {
      return new ControlResponse
      {
        ok = ok,
        action = action,
        detail = detail,
        processId = System.Diagnostics.Process.GetCurrentProcess().Id,
        controlPort = ControlPort
      };
    }
  }
}
