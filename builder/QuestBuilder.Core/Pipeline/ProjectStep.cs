using System.Text.RegularExpressions;

namespace LCQuest.Builder.Pipeline;

/// <summary>
/// Turns the export into the Unity project for Quest: what scripts/make-project.sh does (the VR build), in the same
/// order and with the same results. The project is recreated from the export every time; its Library (Unity's import
/// cache, the slow part) is kept.
/// </summary>
public sealed class ProjectStep : BuildStep
{
    /// <summary>Game assemblies with LCVR's patches baked in; same file names, so the export's .meta stays</summary>
    private static readonly string[] BakedAssemblies =
        ["Assembly-CSharp", "DissonanceVoip", "Facepunch.Steamworks.Win64", "Unity.Netcode.Runtime", "Unity.ProBuilder"];

    /// <summary>BepInEx assemblies LCVR uses at runtime: the plugin base class, logging and config, MonoMod</summary>
    private static readonly string[] BepInExAssemblies = ["BepInEx", "MonoMod.Utils", "MonoMod.RuntimeDetour", "Mono.Cecil"];

    private const int Parts = 16;
    private int done;

    public override string Id => "project";

    public override string Stamp(BuildContext context, IReadOnlyDictionary<string, string> earlierStamps)
    {
        var pins = context.Kit.Manifest.Downloads;
        return Hashing.Combine(Id, earlierStamps.GetValueOrDefault("export"), BakeStep.OutputHash(context),
            context.Kit.ContentHash, context.Kit.Manifest.Unity.Version, pins.BepInEx.Sha256, pins.LCVR.Sha256,
            pins.BuiltinShaders.Sha256);
    }

    public override bool ResultExists(BuildContext context) =>
        File.Exists(Path.Combine(context.Workspace.Project, "Assets", "LCQuest", "link.xml"));

    public override async Task RunAsync(BuildContext context, CancellationToken ct)
    {
        done = 0;
        var workspace = context.Workspace;
        var kit = context.Kit;
        var project = workspace.Project;
        var assets = Path.Combine(project, "Assets");
        var plugins = Path.Combine(assets, "Plugins");

        Report(context, null, "download");
        var bepInExCore = await context.ThirdParty.BepInExCoreAsync(ct);
        var lcvr = await context.ThirdParty.LCVRPluginAsync(ct);
        await context.ThirdParty.BuiltinShadersAsync(ct);

        Step(context, "copy");
        RecreateFromExport(workspace);
        FileOps.WriteText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"),
            $"m_EditorVersion: {kit.Manifest.Unity.Version}\n" +
            $"m_EditorVersionWithRevision: {kit.Manifest.Unity.Version} ({kit.Manifest.Unity.Changeset})\n");
        EditSettings(project);

        Step(context, "baked");
        foreach (var dll in BakedAssemblies)
            FileOps.CopyFile(Path.Combine(workspace.Baked, dll + ".dll"), Path.Combine(plugins, dll + ".dll"));

        await Script(context, ct, "import_lcvr_assets.py", Path.Combine(workspace.LCVRExport, "ExportedProject"), project,
            Path.Combine(lcvr, "lethalcompanyvr"), Path.Combine(lcvr, "lethalcompanyvr-levels"));
        Step(context, "lcvr");
        var lcvrPlugins = Path.Combine(plugins, "LCVR");
        // The .meta comes from the bundle export: LCVR's scripts keep their GUID
        FileOps.CopyFile(Path.Combine(workspace.Baked, "LCVR.dll"), Path.Combine(lcvrPlugins, "LCVR.dll"));
        // Runtime only (Android): the bootstrap, BepInEx, and HarmonyX with the IL2CPP-safe invoker baked in
        FileOps.CopyFile(kit.StandaloneDll, Path.Combine(lcvrPlugins, "LCVR.Standalone.dll"));
        FileOps.CopyFile(Path.Combine(workspace.Baked, "0Harmony.dll"), Path.Combine(lcvrPlugins, "0Harmony.dll"));
        foreach (var name in BepInExAssemblies)
            FileOps.CopyFile(Path.Combine(bepInExCore, name + ".dll"), Path.Combine(lcvrPlugins, name + ".dll"));
        var managedMeta = File.ReadAllText(Path.Combine(kit.UnityTools, "Plugins", "android-managed.meta.template"));
        foreach (var name in new[] { "LCVR.Standalone", "0Harmony" }.Concat(BepInExAssemblies))
            WriteMeta(Path.Combine(lcvrPlugins, name + ".dll.meta"), managedMeta, $"lcquest-lcvr:{name}.dll");

        await Script(context, ct, "package_swap.py", project, "--vr");
        await Script(context, ct, "lcvr_shaders.py", project);
        await Script(context, ct, "patch_packages.py", project);

        // Our runtime code is scanned too (compiled outside Unity against the original HDRP, make-kit.sh): the shim must
        // keep what HdrpBridge uses, or the project's scripts do not compile. The scan needs only its HDRP references,
        // so it works on a first build, before Unity has compiled the packages (make-project.sh compiles it itself and
        // so waits for an earlier import)
        await Script(context, ct, "hdrp_shim.py", project, "--scan", Path.Combine(lcvrPlugins, "LCVR.dll"),
            "--scan", kit.RuntimeScanDll, "--search", bepInExCore);
        await Script(context, ct, "rename_game_assemblies.py", project, Path.Combine(lcvrPlugins, "LCVR.dll"));
        await Script(context, ct, "convert_scenes.py", project);
        await Script(context, ct, "fix_textures.py", project);

        Step(context, "native");
        var native = Path.Combine(plugins, "Android", "arm64-v8a");
        var arm64Meta = File.ReadAllText(Path.Combine(kit.UnityTools, "Plugins", "android-arm64.meta.template"));
        foreach (var so in Directory.EnumerateFiles(kit.NativeLibs, "*.so").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(so);
            FileOps.CopyFile(so, Path.Combine(native, name + ".so"));
            WriteMeta(Path.Combine(native, name + ".so.meta"), arm64Meta, $"lcquest-native:{name}");
        }

        Step(context, "shaders");
        // Same file names (and so .meta GUIDs) and shader names as AssetRipper's placeholders: materials bind as is
        foreach (var shader in Directory.EnumerateFiles(Path.Combine(kit.UnityTools, "Shaders"), "*.shader").Order(StringComparer.Ordinal))
        {
            var target = Path.Combine(assets, "Shader", Path.GetFileName(shader));
            if (!File.Exists(target))
                throw new BuildException($"The export has no placeholder for {Path.GetFileName(shader)} (a different game version?)")
                    { Code = "export-shader" };
            FileOps.CopyFile(shader, target);
        }

        Step(context, "scripts");
        var ours = Path.Combine(assets, "LCQuest");
        var editor = Path.Combine(ours, "Editor");
        FileOps.CopyFiles(Path.Combine(kit.UnityTools, "Editor"), "*.cs", editor);
        FileOps.CopyFiles(Path.Combine(kit.UnityTools, "Reference"), "*.cs", editor);
        FileOps.CopyFiles(Path.Combine(kit.UnityTools, "EditorXR"), "*.cs", editor);
        FileOps.CopyFile(Path.Combine(kit.UnityTools, "RuntimeXR", "link.xml"), Path.Combine(ours, "link.xml"));
        FileOps.CopyFiles(Path.Combine(kit.UnityTools, "Runtime"), "*.cs", Path.Combine(ours, "Runtime"));
        // Our own shaders (Shader.Find or our materials use them)
        FileOps.CopyFiles(Path.Combine(kit.UnityTools, "Runtime", "Shaders"), "*.shader", Path.Combine(ours, "Shaders"));
        // Soft Mask's shaders (the export's are placeholders) over theirs in Resources, where the DLL loads them by name
        var softMask = Path.Combine(kit.UnityTools, "SoftMask");
        FileOps.CopyFiles(softMask, "*.shader", Path.Combine(assets, "Resources"));
        FileOps.CopyFiles(softMask, "*.hlsl", Path.Combine(ours, "Shaders"));
        FileOps.CopyFiles(softMask, "*.cginc", Path.Combine(ours, "Shaders"));
        Report(context, 1, "done");
    }

    /// <summary>A fresh copy of the export, with the previous Library moved back in</summary>
    private static void RecreateFromExport(Workspace workspace)
    {
        if (Directory.Exists(workspace.Library))
        {
            FileOps.DeleteDirectory(workspace.LibraryKeep);
            Directory.Move(workspace.Library, workspace.LibraryKeep);
        }
        FileOps.DeleteDirectory(workspace.Project);
        FileOps.CopyDirectory(workspace.ExportedProject, workspace.Project);
        // An interrupted earlier run may have left the Library waiting here as well
        if (Directory.Exists(workspace.LibraryKeep))
            Directory.Move(workspace.LibraryKeep, workspace.Library);
    }

    /// <summary>
    /// Parallel Import (textures and models import in worker processes); HDRP's global settings left out (they would
    /// pull its runtime resources, ~200 compute shaders, into the build); fog variants kept, since no scene enables
    /// fog and "Automatic" would strip them (HdrpBridge fogs at runtime)
    /// </summary>
    private static void EditSettings(string project)
    {
        var editor = Path.Combine(project, "ProjectSettings", "EditorSettings.asset");
        FileOps.WriteText(editor, Regex.Replace(File.ReadAllText(editor), @"(?m)^(  m_RefreshImportMode:) \d+$", "$1 1"));

        var graphics = Path.Combine(project, "ProjectSettings", "GraphicsSettings.asset");
        var text = File.ReadAllText(graphics);
        text = Regex.Replace(text, @"(?m)^    UnityEngine\.Rendering\.HighDefinition\.HDRenderPipeline: .*\n", "");
        text = Regex.Replace(text, @"(?m)^(  m_FogStripping:) \d+$", "$1 1");
        FileOps.WriteText(graphics, text);

        // No mouse cursor on the headset. The editor on Windows applies the game's cursor (ItStealsCursor) at startup,
        // and as an Android texture it is no valid cursor: the error it logs fails the LCVR bundle build
        var player = Path.Combine(project, "ProjectSettings", "ProjectSettings.asset");
        FileOps.WriteText(player, Regex.Replace(File.ReadAllText(player), @"(?m)^  defaultCursor: .*$", "  defaultCursor: {fileID: 0}"));
    }

    private static void WriteMeta(string path, string template, string guidSeed) =>
        FileOps.WriteText(path, template.Replace("{GUID}", Hashing.Md5Hex(guidSeed)));

    private Task<ProcessResult> Script(BuildContext context, CancellationToken ct, string script, params string[] args)
    {
        Step(context, Path.GetFileNameWithoutExtension(script));
        return context.RunScriptAsync(script, ct, args);
    }

    private void Step(BuildContext context, string detail) =>
        Report(context, Math.Min(0.99, done++ / (double)Parts), "project", detail);
}
