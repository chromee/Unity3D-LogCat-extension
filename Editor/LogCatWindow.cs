#if PLATFORM_ANDROID
using UnityEngine;
using System.Collections;
using UnityEditor;
using UnityEditor.Android;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
#if UNITY_2017_3_OR_NEWER
using UnityEditor.Compilation;
#endif

public class LogCatWindow : EditorWindow
{
    // How many log entries to store in memory. Keep it low for better performance.
    private const int memoryLimit = 2000;
    
    // How many log entries to show in unity3D editor. Keep it low for better performance.
    private const int showLimit = 200;
    
    // Filters
    private bool prefilterOnlyUnity = true;
    private bool filterOnlyError = false;
    private bool filterOnlyWarning = false;
    private bool filterOnlyDebug = false;
    private bool filterOnlyInfo = false;
    private bool filterOnlyVerbose = false;
    private string filterByString = String.Empty;
    
    // Android adb logcat process
    private Process logCatProcess;
    
    // Log entries
    private List<LogCatLog> logsList = new List<LogCatLog>();
    private List<LogCatLog> filteredList = new List<LogCatLog>(memoryLimit);
    private const string LogcatPattern = @"([0-1][0-9]-[0-3][0-9] [0-2][0-9]:[0-5][0-9]:[0-5][0-9]\.[0-9]{3}) ([WIEDV])/(.*)";
    private static readonly Regex LogcatRegex = new Regex(LogcatPattern, RegexOptions.Compiled);

    // Filtered GUI list scroll position
    private Vector2 scrollPosition = new Vector2(0, 0);
    
    // Android device list
    private const string PropPattern = @"\[(.+?)\]:\s*?\[(.*?)\].*";
    private readonly Regex PropRegex = new Regex(PropPattern, RegexOptions.Compiled);
    private readonly DeviceInfo emptyDeviceInfo = new DeviceInfo {Id = string.Empty, Detail = string.Empty};
    private DeviceInfo selectedDevice;
    private List<DeviceInfo> deviceInfos = new List<DeviceInfo>();
    private int selectedDeviceIndex;
    private IEnumerator devicesEnumerator = null;
    private bool initialized = false;
    
    // Add menu item named "LogCat" to the Window menu
    [MenuItem("Window/LogCat - Android Logger")]
    public static void ShowWindow()
    {
        // Show existing window instance. If one doesn't exist, make one.
        LogCatWindow window = EditorWindow.GetWindow<LogCatWindow>(false, "Logcat");
        window.Initialize();
    }
    
    void Initialize()
    {
        if (!initialized)
        {
            devicesEnumerator = UpdateAndroidDevices();
            initialized = true;
        }
    }

    void Update()
    {
        if (devicesEnumerator != null)
        {
            if (!devicesEnumerator.MoveNext())
            {
                devicesEnumerator = null;
            }
            Repaint();
        }

        if (logsList.Count == 0)
            return;
        
        lock (logsList)
        {
            // Filter
            filteredList = logsList.Where(log => (filterByString.Length <= 2 || log.Message.ToLower().Contains(filterByString.ToLower())) &&
                                          ((!filterOnlyError && !filterOnlyWarning && !filterOnlyDebug && !filterOnlyInfo && !filterOnlyVerbose) 
             || filterOnlyError && log.Type == 'E' 
             || filterOnlyWarning && log.Type == 'W' 
             || filterOnlyDebug && log.Type == 'D' 
             || filterOnlyInfo && log.Type == 'I' 
             || filterOnlyVerbose && log.Type == 'V')).ToList();
        }

        if (logCatProcess != null)
        {
            Repaint();
        }
    }
    
    void OnGUI()
    {
        GUILayout.BeginHorizontal();
        
        bool stoppedLogcat = logCatProcess == null;
        bool stoppedDevices = devicesEnumerator == null;
        bool existsDevice = deviceInfos.Count > 0;

        // Enable pre-filter if process is not started
        GUI.enabled = stoppedLogcat;
        prefilterOnlyUnity = GUILayout.Toggle(prefilterOnlyUnity, "Only Unity", "Button", GUILayout.Width(80));
        
        // Enable button if process is not started
        GUI.enabled = stoppedLogcat && existsDevice;
        if (GUILayout.Button("Start", GUILayout.Width(60)))
        {
            string adbPath = GetAdbPath();
            string optionId = string.IsNullOrEmpty(selectedDevice.Id) ? string.Empty : "-s " + selectedDevice.Id + " ";

            // Start `adb logcat -c` to clear the log buffer
            ProcessStartInfo clearProcessInfo = new ProcessStartInfo();
            clearProcessInfo.WindowStyle = ProcessWindowStyle.Hidden;
            clearProcessInfo.CreateNoWindow = true;
            clearProcessInfo.UseShellExecute = false;
            clearProcessInfo.FileName = adbPath;
            clearProcessInfo.Arguments = optionId + @"logcat -c";
            using (Process clearProcess = Process.Start(clearProcessInfo))
            {
                clearProcess.WaitForExit();
            }
            
            // Start `adb logcat` (with additional optional arguments) process for filtering
            ProcessStartInfo logProcessInfo = new ProcessStartInfo();
            logProcessInfo.CreateNoWindow = true;
            logProcessInfo.UseShellExecute = false;
            logProcessInfo.RedirectStandardOutput = true;
            logProcessInfo.RedirectStandardError = true;
            logProcessInfo.StandardOutputEncoding = Encoding.UTF8;
            logProcessInfo.FileName = adbPath;
            logProcessInfo.WindowStyle = ProcessWindowStyle.Hidden;
            
            // Add additional -s argument for filtering by Unity tag.
            logProcessInfo.Arguments = optionId + "logcat -v time"+(prefilterOnlyUnity ? " -s  \"Unity\"": "");
            
            logCatProcess = Process.Start(logProcessInfo);  
            
            logCatProcess.ErrorDataReceived += (sender, errorLine) => { 
                if (errorLine.Data != null && errorLine.Data.Length > 2)
                    AddLog(new LogCatLog(errorLine.Data)); 
            };
            logCatProcess.OutputDataReceived += (sender, outputLine) => { 
                if (outputLine.Data != null && outputLine.Data.Length > 2)
                    AddLog(new LogCatLog(outputLine.Data)); 
            };
            logCatProcess.BeginErrorReadLine();
            logCatProcess.BeginOutputReadLine();
        }
        
        // Disable button if process is already started
        GUI.enabled = !stoppedLogcat;
        if (GUILayout.Button("Stop", GUILayout.Width(60)))
        {
            StopLogCatProcess();
        }
        
        GUI.enabled = true;
        if (GUILayout.Button("Clear", GUILayout.Width(60)))
        {
            lock (logsList)
            {
                logsList.Clear();
                filteredList.Clear();
            }
        }
        
        GUILayout.Label(filteredList.Count + " matching logs", GUILayout.Height(20));
        
        // Create filters
        filterByString = GUILayout.TextField(filterByString, GUILayout.Height(20));
        GUI.color = new Color(0.75f, 0.5f, 0.5f, 1f);
        filterOnlyError = GUILayout.Toggle(filterOnlyError, "Error", "Button", GUILayout.Width(80));
        GUI.color = new Color(0.95f, 0.95f, 0.3f, 1f);
        filterOnlyWarning = GUILayout.Toggle(filterOnlyWarning, "Warning", "Button", GUILayout.Width(80));
        GUI.color = new Color(0.5f, 0.5f, 0.75f, 1f);
        filterOnlyDebug = GUILayout.Toggle(filterOnlyDebug, "Debug", "Button", GUILayout.Width(80));
        GUI.color = new Color(0.5f, 0.75f, 0.5f, 1f);
        filterOnlyInfo = GUILayout.Toggle(filterOnlyInfo, "Info", "Button", GUILayout.Width(80));
        GUI.color = Color.white;
        filterOnlyVerbose = GUILayout.Toggle(filterOnlyVerbose, "Verbose", "Button", GUILayout.Width(80));
        
        GUILayout.EndHorizontal(); 
        
        GUILayout.BeginHorizontal();

        string[] options = deviceInfos.Select(d => d.Detail).ToArray();
        GUI.enabled = stoppedLogcat && stoppedDevices && existsDevice;
        selectedDeviceIndex = EditorGUILayout.Popup(selectedDeviceIndex, options, GUILayout.Width(300));
        if (selectedDeviceIndex >= 0 && selectedDeviceIndex < deviceInfos.Count)
        {
            selectedDevice = deviceInfos[selectedDeviceIndex];
        }
        else
        {
            selectedDevice = emptyDeviceInfo;
        }

        GUI.enabled = stoppedLogcat && stoppedDevices;
        if (GUILayout.Button("Devices Update", GUILayout.Width(120)))
        {
            devicesEnumerator = UpdateAndroidDevices();
        }

        GUILayout.EndHorizontal();

        GUI.enabled = true;
        GUIStyle lineStyle = new GUIStyle();
        lineStyle.normal.background = MakeTexture(600, 1, new Color(1.0f, 1.0f, 1.0f, 0.1f));
        
        scrollPosition = GUILayout.BeginScrollView(scrollPosition, GUILayout.Height(Screen.height - 45));
        
        // Show only top `showingLimit` log entries
        int fromIndex = filteredList.Count - showLimit;
        if (fromIndex < 0)
            fromIndex = 0;
        
        for (int i = fromIndex; i < filteredList.Count; i++)
        {
            LogCatLog log = filteredList[i];
            GUI.backgroundColor = log.GetBgColor();
            GUILayout.BeginHorizontal(lineStyle);
            EditorGUILayout.SelectableLabel(log.CreationDate + " | " + log.Message, GUILayout.Height(20));
            GUILayout.EndHorizontal(); 
        }
        
        GUILayout.EndScrollView();
    }
    
    private Texture2D MakeTexture(int width, int height, Color col)
    {
        Color[] pix = new Color[width * height];
        
        for (int i = 0; i < pix.Length; i++)
            pix [i] = col;
        
        Texture2D result = new Texture2D(width, height);
        result.SetPixels(pix);
        result.Apply();
        
        return result;
    }
    
    private void AddLog(LogCatLog log)
    {
        lock (logsList)
        {
            if (logsList.Count > memoryLimit + 1)
                logsList.RemoveRange(0, logsList.Count - memoryLimit + 1);
            
            logsList.Add(log);
        }
    }

    void OnEnable()
    {
#if UNITY_2017_3_OR_NEWER
        CompilationPipeline.assemblyCompilationStarted += OnAssemblyCompilationStarted;
#endif
    }

    void OnDisable()
    {
#if UNITY_2017_3_OR_NEWER
        CompilationPipeline.assemblyCompilationStarted -= OnAssemblyCompilationStarted;
#endif
    }

    void OnDestroy()
    {
        StopLogCatProcess();
    }

    private void StopLogCatProcess()
    {
        if (logCatProcess == null)
        {
            return;
        }
        try
        {
            if (!logCatProcess.HasExited)
            {
                logCatProcess.Kill();
            }
        }
        catch(InvalidOperationException)
        {
            // Just ignore it.
        }
        finally
        {
            logCatProcess.Dispose();
            logCatProcess = null;
        }
    }

    private void OnAssemblyCompilationStarted(string _)
    {
        StopLogCatProcess();
    }

    private class LogCatLog
    {
        public LogCatLog(string data)
        {
            // First char indicates error type:
            // W - warning
            // E - error
            // D - debug
            // I - info
            // V - verbose
            Match match = LogcatRegex.Match(data);
            if (match.Success)
            {
                Type = match.Groups[2].Value[0];

                Message = match.Groups[3].Value;
                CreationDate = match.Groups[1].Value;
            }
            else
            {
                Type = 'V';

                Message = data;
                CreationDate = DateTime.Now.ToString("MM-dd HH:mm:ss.fff");
            }
        }
        
        public string CreationDate
        {
            get;
            set;
        }
        
        public char Type
        {
            get;
            set;
        }
        
        public string Message
        {
            get;
            set;
        }
        
        public Color GetBgColor()
        {
            switch (Type)
            {
                case 'W':
                    return Color.yellow;
                    
                case 'I':
                    return Color.green;
                    
                case 'E':
                    return Color.red;
                    
                case 'D':
                    return Color.blue;
                    
                case 'V':
                default:
                    return Color.grey;
            }
        }
    }

    private static string GetAdbPath()
    {
#if UNITY_2019_1_OR_NEWER
        ADB adb = ADB.GetInstance();
        return adb == null ? string.Empty : adb.GetADBPath();
#else
        string androidSdkRoot = EditorPrefs.GetString("AndroidSdkRoot");
        if (string.IsNullOrEmpty(androidSdkRoot))
        {
            return string.Empty;
        }
        return Path.Combine(androidSdkRoot, Path.Combine("platform-tools", "adb"));
#endif
    }

    [Serializable]
    private struct DeviceInfo
    {
        public string Id;
        public string Detail;
    }

    private IEnumerator UpdateAndroidDevices()
    {
        deviceInfos.Clear();

        IEnumerator<string> devicesEnumerator = GetDevices();
        while (devicesEnumerator.MoveNext())
        {
            string line = devicesEnumerator.Current;
            if (string.IsNullOrEmpty(line) || !line.EndsWith("device"))
            {
                yield return null;
                continue;
            }

            string id = line.Substring(0, line.IndexOf('\t'));

            Dictionary<string, string> properties = null;
            IEnumerator<Dictionary<string, string>> propertyEnumerator = GetProperties(id);
            while (propertyEnumerator.MoveNext())
            {
                Dictionary<string, string> props = propertyEnumerator.Current;
                if (props == null)
                {
                    yield return null;
                }

                properties = props;
            }

            string detail = id;
            if (properties != null)
            {
                string manufacturer;
                if (!properties.TryGetValue("ro.product.manufacturer", out manufacturer))
                {
                    manufacturer = string.Empty;
                }
                string model;
                if (!properties.TryGetValue("ro.product.model", out model))
                {
                    model = string.Empty;
                }
                string release;
                if (!properties.TryGetValue("ro.build.version.release", out release))
                {
                    release = string.Empty;
                }
                string sdk;
                if (!properties.TryGetValue("ro.build.version.sdk", out sdk))
                {
                    sdk = string.Empty;
                }
                detail = string.Format("{0} {1} (version: {2}, sdk: {3}, id: {4})",
                    manufacturer, model, release, sdk, id);
            }
            deviceInfos.Add(new DeviceInfo {Id = id, Detail = detail});
        }
    }

    private IEnumerator<string> GetDevices()
    {
        for (int retry = 0; retry < 2; retry++)
        {
            ProcessStartInfo devicesProcessInfo = new ProcessStartInfo();
            devicesProcessInfo.CreateNoWindow = true;
            devicesProcessInfo.WindowStyle = ProcessWindowStyle.Hidden;
            devicesProcessInfo.UseShellExecute = false;
            devicesProcessInfo.StandardOutputEncoding = Encoding.UTF8;
            devicesProcessInfo.RedirectStandardOutput = true;
            devicesProcessInfo.RedirectStandardError = false;
            devicesProcessInfo.FileName = GetAdbPath();
            devicesProcessInfo.Arguments = @"devices";
            using (Process devicesProcess = new Process())
            {
                devicesProcess.StartInfo = devicesProcessInfo;
                List<string> devices = new List<string>();
                devicesProcess.OutputDataReceived += (sender, outputLine) =>
                {
                    if (string.IsNullOrEmpty(outputLine.Data))
                    {
                        return;
                    }

                    devices.Add(outputLine.Data);
                };
                devicesProcess.Start();
                devicesProcess.BeginOutputReadLine();
                do
                {
                    yield return null;
                } while (!devicesProcess.WaitForExit(100));

                // Wait for the standard output to flush
                devicesProcess.WaitForExit();
                devicesProcess.CancelOutputRead();

                if (devicesProcess.ExitCode == 0)
                {
                    foreach (string device in devices)
                    {
                        yield return device;
                    }

                    break;
                }
            }

            IEnumerator k = KillServer();
            while (k.MoveNext())
            {
                yield return null;
            }
        }
    }

    private IEnumerator<Dictionary<string, string>> GetProperties(string id)
    {
        ProcessStartInfo getPropProcessInfo = new ProcessStartInfo();
        getPropProcessInfo.CreateNoWindow = true;
        getPropProcessInfo.WindowStyle = ProcessWindowStyle.Hidden;
        getPropProcessInfo.UseShellExecute = false;
        getPropProcessInfo.StandardOutputEncoding = Encoding.UTF8;
        getPropProcessInfo.RedirectStandardOutput = true;
        getPropProcessInfo.RedirectStandardError = false;
        getPropProcessInfo.FileName = GetAdbPath();
        getPropProcessInfo.Arguments = "-s " + id + " shell getprop";
        using (Process getPropProcess = new Process())
        {
            getPropProcess.StartInfo = getPropProcessInfo;
            Dictionary<string, string> props = new Dictionary<string, string>();
            getPropProcess.OutputDataReceived += (sender, outputLine) =>
            {
                if (string.IsNullOrEmpty(outputLine.Data))
                {
                    return;
                }

                Match match = PropRegex.Match(outputLine.Data);
                if (match.Success)
                {
                    props.Add(match.Groups[1].Value, match.Groups[2].Value);
                }
            };
            getPropProcess.Start();
            getPropProcess.BeginOutputReadLine();
            do
            {
                yield return null;
            } while (!getPropProcess.WaitForExit(100));

            // Wait for the standard output to flush
            getPropProcess.WaitForExit();
            getPropProcess.CancelOutputRead();
            yield return props;
        }
    }

    private IEnumerator KillServer()
    {
        ProcessStartInfo killServerProcessInfo = new ProcessStartInfo();
        killServerProcessInfo.WindowStyle = ProcessWindowStyle.Hidden;
        killServerProcessInfo.CreateNoWindow = true;
        killServerProcessInfo.UseShellExecute = false;
        killServerProcessInfo.FileName = GetAdbPath();
        killServerProcessInfo.Arguments = @"kill-server";
        using (Process killServerProcess = Process.Start(killServerProcessInfo))
        {
            do
            {
                yield return null;
            } while (!killServerProcess.WaitForExit(100));
        }
    }
}
#endif
