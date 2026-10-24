namespace LCQuest.Builder;

/// <summary>
/// The builder's working folder. The layout is the one the Mac checkout has always used under work/, so our own builds
/// (QuestBuilder.Cli --dev) keep their exported project and Unity Library.
/// </summary>
public sealed class Workspace
{
    public Workspace(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    /// <summary>AssetRipper's export of the game (ExportedProject/, AuxiliaryFiles/)</summary>
    public string Export => Path.Combine(Root, "export");
    public string ExportedProject => Path.Combine(Export, "ExportedProject");
    /// <summary>AssetRipper's export of LCVR's asset bundles (scripts/import_lcvr_assets.py reads it)</summary>
    public string LCVRExport => Path.Combine(Root, "lcvr-export");
    /// <summary>The minimal game folder AssetRipper reads LCVR's bundles from</summary>
    public string LCVRInput => Path.Combine(Root, "lcvr-input");

    /// <summary>Game assemblies with LCVR's patches baked in</summary>
    public string Baked => Path.Combine(Root, "baked", "all-android");
    public string Staging => Path.Combine(Root, "baked", "quest-staging");

    public string Project => Path.Combine(Root, "project");
    public string Library => Path.Combine(Project, "Library");
    /// <summary>Where the Library waits while the project is recreated from the export</summary>
    public string LibraryKeep => Path.Combine(Root, "Library.keep");

    public string Build => Path.Combine(Root, "build");
    /// <summary>ovrport's workspace (the pico step): its libraries and the key it signs Pico APKs with, kept so that a
    /// new APK installs over the old one</summary>
    public string Ovrport => Path.Combine(Root, "pico", "ovrport-ws");
    public string Logs => Path.Combine(Root, "logs");
    /// <summary>Downloaded tools: AssetRipper, uv, Python and its packages</summary>
    public string Tools => Path.Combine(Root, "tools");
    /// <summary>Downloaded third-party releases the build uses: LCVR, BepInEx, NuGet packages</summary>
    public string Deps => Path.Combine(Root, "deps");
    /// <summary>Downloaded archives; also read by the Python scripts (builtin shaders) and their package cache</summary>
    public string Downloads => Path.Combine(Root, "downloads");

    public string AddressablesEntries => Path.Combine(Root, "addressables-entries.json");
    public string ShaderStates => Path.Combine(Root, "shader-states.json");
    public string StatePath => Path.Combine(Root, "state.json");
    public string KitRoot => Path.Combine(Root, "kit");

    /// <summary>Backups of the headset's data made when reinstalling or removing the game: the player's saves</summary>
    public string Backups => Path.Combine(Root, "backups");

    /// <summary>
    /// Everything the builder puts in the workspace, which removal deletes; not <see cref="Backups"/>, and nothing the
    /// player keeps there (a workspace may be an existing folder)
    /// </summary>
    public IEnumerable<string> OwnedEntries =>
        new[] { "export", "baked", "unity", "build", "pico", "logs", "tools", "deps", "downloads", "kit" }
            .Select(name => Path.Combine(Root, name))
            .Concat([AddressablesEntries, ShaderStates, StatePath]);

    public void Create()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
    }
}
