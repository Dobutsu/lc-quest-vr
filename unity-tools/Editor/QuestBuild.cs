using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace LCQuest.Editor
{
    /// <summary>
    /// Batch build of the exported Lethal Company project for Meta Quest (Android, ARM64, IL2CPP), in two editor runs:
    /// settings changes schedule a domain reload, and BuildPlayer refuses to run until it has happened.
    ///   1. -executeMethod LCQuest.Editor.QuestBuild.Prepare
    ///   2. -executeMethod LCQuest.Editor.QuestBuild.BuildApk -apkPath &lt;file&gt; [-developmentBuild]
    ///      [-versionName &lt;x.y.z&gt;] [-versionCode &lt;n&gt;]
    /// scripts/build-apk.sh runs both.
    /// </summary>
    public static class QuestBuild
    {
        private const string PackageName = "io.lcvr.quest";

        public static void Prepare()
        {
            ConfigurePlayer();
            UrpSetup.Apply();
            HdrpMaterialConverter.Apply();
            HdrpMaterialConverter.LitVariant(Environment.GetEnvironmentVariable("LCQUEST_LIT") == "simple");
            TmpFontAtlasFix.Apply();
            QuestAudio.Apply();
            QuestTextures.Apply();
            // VR build only: unity-tools/EditorXR is copied in with the XR packages
            InvokeIfPresent("LCQuest.Editor.QuestXRSetup", "Apply");
            AssetDatabase.SaveAssets();
            EditorApplication.Exit(0);
        }

        public static void BuildApk()
        {
            var apkPath = ArgumentValue("-apkPath") ?? Path.GetFullPath("Builds/lc-quest.apk");
            var development = Environment.GetCommandLineArgs().Contains("-developmentBuild");

            var versionName = ArgumentValue("-versionName");
            if (versionName != null)
                PlayerSettings.bundleVersion = versionName;
            if (int.TryParse(ArgumentValue("-versionCode"), out var versionCode))
                PlayerSettings.Android.bundleVersionCode = versionCode;

            // LCVR's bundles, built for Android here; their manifest tells code stripping which types they use
            var bundleManifest = (string)InvokeIfPresent("LCQuest.Editor.LcvrBundles", "Build");

            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            Debug.Log($"[QuestBuild] building {scenes.Length} scenes -> {apkPath} (development: {development})");
            Directory.CreateDirectory(Path.GetDirectoryName(apkPath)!);

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = apkPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = development ? BuildOptions.Development : BuildOptions.None,
                assetBundleManifestPath = bundleManifest,
            });

            var summary = report.summary;
            Debug.Log($"[QuestBuild] result: {summary.result}, size: {summary.totalSize / (1024 * 1024)} MB, " +
                      $"time: {summary.totalTime}, errors: {summary.totalErrors}, warnings: {summary.totalWarnings}");
            foreach (var step in report.steps)
            foreach (var message in step.messages.Where(m => m.type is LogType.Error or LogType.Exception))
                Debug.Log($"[QuestBuild] error in '{step.name}': {message.content}");
            foreach (var step in report.steps.Where(s => s.duration.TotalSeconds >= 1))
                Debug.Log($"[QuestBuild] step {new string(' ', 2 * step.depth)}{step.name}: {step.duration.TotalSeconds:F0} s");

            EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        private static void ConfigurePlayer()
        {
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
            // The subtarget alone did not reach the player build (the game's textures came out ETC2, RGBA at 8 bits a
            // pixel): Player Settings' default format is what it reads, public only from Unity 2023. ASTC (6x6 by
            // default): 3.56 bits
            var formatType = typeof(PlayerSettings).Assembly.GetType("UnityEditor.TextureCompressionFormat");
            typeof(PlayerSettings).GetMethod("SetDefaultTextureCompressionFormat", BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(BuildTargetGroup), formatType }, null)!
                .Invoke(null, new[] { (object)BuildTargetGroup.Android, Enum.Parse(formatType, "ASTC") });

            var android = NamedBuildTarget.Android;
            PlayerSettings.SetApplicationIdentifier(android, PackageName);
            PlayerSettings.Android.bundleVersionCode = 1;  // exported settings carry 0, which Gradle rejects
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
            PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)34;

            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.colorSpace = ColorSpace.Linear;

            // The game's assemblies are precompiled and reached through reflection (Netcode, Dissonance)
            PlayerSettings.SetManagedStrippingLevel(android, ManagedStrippingLevel.Minimal);
            PlayerSettings.stripEngineCode = false;
            // Netcode, Transport and Collections stay the game's DLLs, built against slightly different package versions
            PlayerSettings.assemblyVersionValidation = false;

            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.SetMobileMTRendering(android, true);
            PlayerSettings.gcIncremental = true;
            // CPU/GPU frame times for QuestDiagnostics (FrameTimingManager)
            PlayerSettings.enableFrameTimingStats = true;
            // Shader code stays compressed in chunks, decompressed as variants are needed: without a limit every chunk
            // stays decompressed (URP Lit alone took 82 MB on the Quest)
            PlayerSettings.SetDefaultShaderChunkSizeInMB(4);
            PlayerSettings.SetDefaultShaderChunkCount(4);
        }

        private static object InvokeIfPresent(string typeName, string method)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName)).FirstOrDefault(t => t != null);
            return type?.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
        }

        internal static string ArgumentValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}
