using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace LCQuest
{
    /// <summary>
    /// The diagnostic build's log, for testers without adb (a Pico 4): everything goes to files in
    /// &lt;persistentDataPath&gt;/logs/&lt;start time&gt;/ (on the headset Android/data/&lt;package&gt;/files/logs, copied over USB):
    ///   game.txt    every Unity log message with its stack trace for errors, the device and GPU, and every 5 s the
    ///               frame rate, frame times, memory, XR state and scene; scene loads, focus and pause changes
    ///   logcat.txt  Android's log of this process (logcat --pid), which also has native crashes, the OpenXR runtime's
    ///               messages and the graphics driver's
    /// On in development builds (scripts/build-apk.sh ... -developmentBuild), or with a file "diagnostic-log" in
    /// persistentDataPath; the last five sessions are kept. Lines are flushed as written, so a crash loses nothing.
    /// </summary>
    public class DiagnosticLog : MonoBehaviour
    {
        private const float StatsSeconds = 5f;
        private const int SessionsKept = 5;

        private static readonly object FileLock = new object();
        private static StreamWriter writer;
        private static AndroidJavaObject logcat;
        private float nextStats;
        private int frames;
        private float framesSince;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (!Debug.isDebugBuild && !File.Exists(Path.Combine(Application.persistentDataPath, "diagnostic-log")))
                return;
            try
            {
                var root = Path.Combine(Application.persistentDataPath, "logs");
                var folder = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(folder);
                foreach (var old in Directory.GetDirectories(root).OrderByDescending(d => d).Skip(SessionsKept))
                    Directory.Delete(old, true);
                writer = new StreamWriter(Path.Combine(folder, "game.txt")) { AutoFlush = true };
                StartLogcat(Path.Combine(folder, "logcat.txt"));
            }
            catch (Exception e)
            {
                Debug.LogError($"[LCQuest] diagnostic log: {e}");
                return;
            }

            Application.logMessageReceivedThreaded += OnLog;
            SceneManager.sceneLoaded += (scene, mode) => Write($"scene loaded: {scene.name} ({mode})");
            SceneManager.activeSceneChanged += (_, scene) => Write($"active scene: {scene.name}");
            Application.lowMemory += () => Write("LOW MEMORY warning from Android");
            Application.quitting += () => Write("quitting");
            WriteDevice();

            var go = new GameObject("LCQuest Diagnostic Log");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<DiagnosticLog>();
        }

        private static void StartLogcat(string path)
        {
            if (Application.platform != RuntimePlatform.Android)
                return;
            // An app may read its own process's log; -f writes the file from logcat itself, so it goes on after a crash.
            // Through Java's Runtime.exec: System.Diagnostics.Process is not dependable under IL2CPP
            using var process = new AndroidJavaClass("android.os.Process");
            var pid = process.CallStatic<int>("myPid");
            using var runtime = new AndroidJavaClass("java.lang.Runtime").CallStatic<AndroidJavaObject>("getRuntime");
            logcat = runtime.Call<AndroidJavaObject>("exec",
                new object[] { new[] { "/system/bin/logcat", $"--pid={pid}", "-v", "threadtime", "-f", path } });
        }

        private static void WriteDevice()
        {
            Write($"build {Application.version} ({Application.identifier}), Unity {Application.unityVersion}, development {Debug.isDebugBuild}");
            Write($"device {SystemInfo.deviceModel} ({DeviceBrand()}), {SystemInfo.operatingSystem}");
            Write($"cpu {SystemInfo.processorType} x{SystemInfo.processorCount}, memory {SystemInfo.systemMemorySize} MB");
            Write($"gpu {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceVendor}), {SystemInfo.graphicsDeviceVersion}, " +
                  $"{SystemInfo.graphicsMemorySize} MB, shader level {SystemInfo.graphicsShaderLevel}");
            Write($"screen {Screen.width}x{Screen.height} @ {Screen.currentResolution.refreshRateRatio.value:0.#} Hz");
        }

        private static string DeviceBrand()
        {
            if (Application.platform != RuntimePlatform.Android)
                return "editor";
            try
            {
                using var build = new AndroidJavaClass("android.os.Build");
                return $"{build.GetStatic<string>("BRAND")} {build.GetStatic<string>("DEVICE")}";
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        private void Start() => Write($"xr: loader device {XRSettings.loadedDeviceName}, active {XRSettings.isDeviceActive}, " +
                                      $"eye texture {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}, scale {XRSettings.eyeTextureResolutionScale}");

        private void Update()
        {
            frames++;
            framesSince += Time.unscaledDeltaTime;
            if (Time.unscaledTime < nextStats)
                return;
            nextStats = Time.unscaledTime + StatsSeconds;
            WriteStats();
            frames = 0;
            framesSince = 0;
        }

        private void WriteStats()
        {
            FrameTimingManager.CaptureFrameTimings();
            var timings = new FrameTiming[30];
            var count = (int)FrameTimingManager.GetLatestTimings((uint)timings.Length, timings);
            var cpu = count > 0 ? timings.Take(count).Average(t => t.cpuFrameTime) : 0;
            var gpu = count > 0 ? timings.Take(count).Average(t => t.gpuFrameTime) : 0;
            var fps = framesSince > 0 ? frames / framesSince : 0;
            var allocated = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() >> 20;
            var reserved = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() >> 20;
            var managed = GC.GetTotalMemory(false) >> 20;
            Write($"stats fps {fps:0.0}, cpu {cpu:0.0} ms, gpu {gpu:0.0} ms, memory {allocated}/{reserved} MB (managed {managed}), " +
                  $"xr active {XRSettings.isDeviceActive}, focused {Application.isFocused}, scene {SceneManager.GetActiveScene().name}, " +
                  $"battery {SystemInfo.batteryLevel:0.00} {SystemInfo.batteryStatus}");
        }

        private void OnApplicationFocus(bool focus) => Write($"focus {focus}");

        private void OnApplicationPause(bool pause) => Write($"pause {pause}");

        private void OnDestroy()
        {
            try
            {
                logcat?.Call("destroy");
            }
            catch (Exception)
            {
                // Gone already
            }
        }

        private static void OnLog(string message, string stackTrace, LogType type)
        {
            if (type == LogType.Log || type == LogType.Warning)
                Write($"{Letter(type)} {message}");
            else
                Write($"{Letter(type)} {message}\n{stackTrace.TrimEnd()}");
        }

        private static string Letter(LogType type) => type switch
        {
            LogType.Error => "E",
            LogType.Assert => "A",
            LogType.Warning => "W",
            LogType.Exception => "X",
            _ => "I",
        };

        private static void Write(string line)
        {
            lock (FileLock)
            {
                try
                {
                    writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{System.Threading.Thread.CurrentThread.ManagedThreadId}] {line}");
                }
                catch (Exception)
                {
                    // Disk full or the file gone: nothing to tell
                }
            }
        }
    }
}
