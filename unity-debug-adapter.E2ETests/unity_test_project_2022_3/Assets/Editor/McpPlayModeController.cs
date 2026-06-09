using System;
using System.IO;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class McpPlayModeController
{
  const string TriggerFileName = "mcp-enter-play-mode";
  const string StatusFileName = "mcp-play-mode-status.json";
  const string EnterWhenEditModeKey = "unity-dap.mcp.enter-when-edit-mode";

  static readonly string s_ProjectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
  static readonly string s_TriggerPath = Path.Combine(s_ProjectPath, "Temp", TriggerFileName);
  static readonly string s_StatusPath = Path.Combine(s_ProjectPath, "Temp", StatusFileName);
  static double s_NextPollTime;

  static McpPlayModeController()
  {
    EditorApplication.update += Update;
    EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    WriteStatus("initialized");
  }

  static void Update()
  {
    if (EditorApplication.timeSinceStartup < s_NextPollTime)
      return;

    s_NextPollTime = EditorApplication.timeSinceStartup + 0.25;

    if (SessionState.GetBool(EnterWhenEditModeKey, false) && !EditorApplication.isPlayingOrWillChangePlaymode)
    {
      SessionState.SetBool(EnterWhenEditModeKey, false);
      Debug.Log("MCP play mode trigger entering Play Mode after reset");
      WriteStatus("enter-after-reset");
      EditorApplication.EnterPlaymode();
      return;
    }

    if (!File.Exists(s_TriggerPath))
      return;

    try
    {
      File.Delete(s_TriggerPath);
    }
    catch (Exception e)
    {
      Debug.LogWarning("MCP play mode trigger could not be removed: " + e.Message);
    }

    if (EditorApplication.isPlayingOrWillChangePlaymode)
    {
      SessionState.SetBool(EnterWhenEditModeKey, true);
      Debug.Log("MCP play mode trigger resetting active Play Mode");
      WriteStatus("exit-before-reenter");
      EditorApplication.isPlaying = false;
      return;
    }

    if (!EditorApplication.isPlayingOrWillChangePlaymode)
    {
      Debug.Log("MCP play mode trigger entering Play Mode");
      WriteStatus("enter");
      EditorApplication.EnterPlaymode();
    }
  }

  static void OnPlayModeStateChanged(PlayModeStateChange state)
  {
    WriteStatus("state-" + state);
  }

  static void WriteStatus(string action)
  {
    try
    {
      Directory.CreateDirectory(Path.GetDirectoryName(s_StatusPath));
      var json = "{"
          + "\"action\":\"" + Escape(action) + "\","
          + "\"utc\":\"" + Escape(DateTimeOffset.UtcNow.ToString("O")) + "\","
          + "\"isPlaying\":" + (EditorApplication.isPlaying ? "true" : "false") + ","
          + "\"isPlayingOrWillChangePlaymode\":" + (EditorApplication.isPlayingOrWillChangePlaymode ? "true" : "false") + ","
          + "\"enterWhenEditMode\":" + (SessionState.GetBool(EnterWhenEditModeKey, false) ? "true" : "false")
          + "}";
      File.WriteAllText(s_StatusPath, json);
    }
    catch (Exception e)
    {
      Debug.LogWarning("MCP play mode status could not be written: " + e.Message);
    }
  }

  static string Escape(string value)
  {
    return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
  }
}
