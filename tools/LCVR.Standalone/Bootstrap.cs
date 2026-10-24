using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LCVR.Standalone
{
    /// <summary>
    /// Does the jobs BepInEx did for LCVR once its patches are baked into the game assemblies: setting up BepInEx's
    /// paths and logging, telling the plugin where its files are, and creating the plugin component.
    /// On Android LCVR's asset bundles ship in the APK under assets/LCVR (StreamingAssets/LCVR in the project).
    /// </summary>
    public static class Bootstrap
    {
        private const string PluginTypeName = "LCVR.Plugin, LCVR";
        private const string ModFolder = "LCVR";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void StartPlugin()
        {
            try
            {
                InitializeBepInExPaths();
                BepInEx.Logging.Logger.Listeners.Add(new BepInEx.Logging.UnityLogListener());

                var pluginType = Type.GetType(PluginTypeName, true);
                RegisterPluginLocation(pluginType, Application.platform == RuntimePlatform.Android
                    ? ExtractModFiles()
                    : Path.Combine(Application.streamingAssetsPath, ModFolder, "LCVR.dll"));

                // Items.LoadConfig lists *.lcvr-cfg.json files in here (after the extraction, which clears the folder)
                Directory.CreateDirectory(Paths.PluginPath);

                var host = new GameObject("LCVR (standalone)");
                Object.DontDestroyOnLoad(host);
                host.AddComponent(pluginType);

                Debug.Log("[LCVR.Standalone] plugin started");
            }
            catch (Exception e)
            {
                Debug.LogError($"[LCVR.Standalone] failed to start LCVR: {e}");
            }
        }

        /// <summary>BaseUnityPlugin reads Paths.ConfigPath; normally the preloader sets these</summary>
        private static void InitializeBepInExPaths()
        {
            var gameRoot = Path.GetDirectoryName(Application.dataPath);
            var executable = Path.Combine(gameRoot, Path.GetFileName(Application.dataPath).Replace("_Data", ".exe"));
            var bepinRoot = Path.Combine(Application.persistentDataPath, ModFolder);

            typeof(Paths).GetMethod("SetExecutablePath", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { executable, bepinRoot, Path.Combine(Application.dataPath, "Managed"), null });

            Directory.CreateDirectory(Paths.ConfigPath);
        }

        /// <summary>
        /// LCVR loads its asset bundles from next to its DLL. In the APK they are not in a directory the file APIs can
        /// read, so they are copied out to persistent storage once per installed APK. Returns the path LCVR should
        /// believe it was loaded from.
        /// </summary>
        private static string ExtractModFiles()
        {
            const string prefix = "assets/" + ModFolder + "/";
            var target = Path.Combine(Application.persistentDataPath, ModFolder);
            var apk = new FileInfo(Application.dataPath);
            var stampFile = Path.Combine(target, ".apk-stamp");
            var stamp = $"{apk.Length}:{apk.LastWriteTimeUtc.Ticks}";

            if (!File.Exists(stampFile) || File.ReadAllText(stampFile) != stamp)
            {
                // Replace what the previous APK extracted, but keep config/ (BepInEx keeps LCVR's settings there)
                if (Directory.Exists(target))
                {
                    foreach (var dir in Directory.GetDirectories(target).Where(d => Path.GetFileName(d) != "config"))
                        Directory.Delete(dir, true);
                    foreach (var file in Directory.GetFiles(target))
                        File.Delete(file);
                }

                using (var zip = ZipFile.OpenRead(apk.FullName))
                {
                    foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(prefix) && !e.FullName.EndsWith("/")))
                    {
                        var destination = Path.Combine(target, entry.FullName.Substring(prefix.Length));
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        entry.ExtractToFile(destination, true);
                    }
                }

                File.WriteAllText(stampFile, stamp);
                Debug.Log($"[LCVR.Standalone] extracted mod files to {target}");
            }

            // LCVR preloads every DLL in here; under IL2CPP they are compiled into the player, so it stays empty
            Directory.CreateDirectory(Path.Combine(target, "RuntimeDeps"));
            return Path.Combine(target, "LCVR.dll");
        }

        /// <summary>
        /// BaseUnityPlugin takes Info from the chainloader's table when the plugin is listed there, and otherwise
        /// uses Assembly.Location, which IL2CPP leaves empty
        /// </summary>
        private static void RegisterPluginLocation(Type pluginType, string location)
        {
            var info = new PluginInfo();
            SetInternal(info, nameof(PluginInfo.Metadata), MetadataHelper.GetMetadata(pluginType));
            SetInternal(info, nameof(PluginInfo.Processes), MetadataHelper.GetAttributes<BepInProcess>(pluginType));
            SetInternal(info, nameof(PluginInfo.Dependencies), MetadataHelper.GetDependencies(pluginType));
            SetInternal(info, nameof(PluginInfo.Location), location);
            Chainloader.PluginInfos[info.Metadata.GUID] = info;
        }

        private static void SetInternal(object target, string property, object value) =>
            target.GetType().GetProperty(property).GetSetMethod(true).Invoke(target, new[] { value });
    }
}
