using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace LCQuest.Builder.Pipeline;

/// <summary>
/// Exports the game into a Unity project with AssetRipper (the free GUI build, driven headless through its local web
/// API: settings, load the game folder, export), then LCVR's asset bundles the same way (scripts/export.sh and
/// scripts/export-lcvr.sh). Both commands answer only when they are finished.
/// </summary>
public sealed partial class ExportStep : BuildStep
{
    public override string Id => "export";

    /// <summary>
    /// The export settings our project has always been made with (work/assetripper.log of the first export). Boolean
    /// settings left out of the form are switched off, as they were then (EnableStaticMeshSeparation,
    /// PreferOriginalTextureExtension, ...).
    /// </summary>
    private static readonly (string Key, string Value)[] Settings =
    [
        ("ScriptContentLevel", "Level2"),
        ("StreamingAssetsMode", "Extract"),
        ("BundledAssetsExportMode", "DirectExport"),
        ("AudioExportFormat", "Default"),
        ("ImageExportFormat", "Png"),
        ("LightmapTextureExportFormat", "Yaml"),
        ("ScriptExportMode", "DllExportWithoutRenaming"),
        ("ScriptLanguageVersion", "AutoSafe"),
        ("ShaderExportMode", "Decompile"),
        ("SpriteExportMode", "Yaml"),
        ("TextExportMode", "Parse"),
    ];

    public override string Stamp(BuildContext context, IReadOnlyDictionary<string, string> earlierStamps) =>
        Hashing.Combine(Id, context.Game.DataFingerprint(), context.Kit.Manifest.Downloads.AssetRipper.Version,
            context.Kit.Manifest.Downloads.LCVR.Sha256, string.Join(';', Settings.Select(s => $"{s.Key}={s.Value}")));

    public override bool ResultExists(BuildContext context) =>
        File.Exists(Path.Combine(context.Workspace.ExportedProject, "ProjectSettings", "ProjectSettings.asset")) &&
        File.Exists(Path.Combine(context.Workspace.LCVRExport, "ExportedProject", "Assets", "Plugins", "LCVR.dll.meta"));

    public override async Task RunAsync(BuildContext context, CancellationToken ct)
    {
        var workspace = context.Workspace;
        Report(context, null, "download");
        var assetRipper = await context.ThirdParty.AssetRipperAsync(ct);
        var lcvr = await context.ThirdParty.LCVRPluginAsync(ct);

        await ExportAsync(context, assetRipper, context.Game.Path, workspace.Export, "assetripper", 0, 0.85, ct);
        if (!Directory.Exists(Path.Combine(workspace.ExportedProject, "Assets", "Plugins")))
            throw new BuildException("AssetRipper did not export the game (no project in the export folder)",
                Path.Combine(workspace.Logs, "assetripper.log")) { Code = "export-empty" };

        // LCVR's asset bundles (scripts/export-lcvr.sh): AssetRipper reads their MonoBehaviours with the scripts'
        // assemblies, so it gets a minimal game folder: the game's managers and Managed DLLs, plus LCVR.dll, XRI's
        // DLL and the two bundles
        var input = workspace.LCVRInput;
        FileOps.DeleteDirectory(input);
        var data = Path.Combine(input, "Lethal Company_Data");
        Directory.CreateDirectory(Path.Combine(data, "StreamingAssets", "LCVR"));
        FileOps.CopyFile(Path.Combine(context.Game.Path, "Lethal Company.exe"), Path.Combine(input, "Lethal Company.exe"));
        foreach (var file in new[] { "app.info", "boot.config", "globalgamemanagers", "globalgamemanagers.assets", "globalgamemanagers.assets.resS" })
            FileOps.CopyFile(Path.Combine(context.Game.Data, file), Path.Combine(data, file));
        FileOps.CopyDirectory(Path.Combine(context.Game.Data, "Resources"), Path.Combine(data, "Resources"));
        FileOps.CopyDirectory(context.Game.Managed, Path.Combine(data, "Managed"));
        FileOps.CopyFile(Path.Combine(lcvr, "LCVR.dll"), Path.Combine(data, "Managed", "LCVR.dll"));
        FileOps.CopyFile(Path.Combine(lcvr, "RuntimeDeps", "Unity.XR.Interaction.Toolkit.dll"),
            Path.Combine(data, "Managed", "Unity.XR.Interaction.Toolkit.dll"));
        foreach (var bundle in new[] { "lethalcompanyvr", "lethalcompanyvr-levels" })
            FileOps.CopyFile(Path.Combine(lcvr, bundle), Path.Combine(data, "StreamingAssets", "LCVR", bundle));

        await ExportAsync(context, assetRipper, input, workspace.LCVRExport, "assetripper-lcvr", 0.85, 1, ct);
        FileOps.DeleteDirectory(input);
        if (!ResultExists(context))
            throw new BuildException("AssetRipper did not export LCVR's asset bundles",
                Path.Combine(workspace.Logs, "assetripper-lcvr.log")) { Code = "export-empty" };
    }

    /// <summary>One AssetRipper run: input folder -> output/ExportedProject; progress mapped onto [from, to]</summary>
    private async Task ExportAsync(BuildContext context, string assetRipper, string input, string output, string logName,
        double from, double to, CancellationToken ct)
    {
        ProcessRunner.KillLeftovers(assetRipper);
        // AssetRipper asks before deleting a non-empty export folder; ours is always replaced
        FileOps.DeleteDirectory(output);
        var port = FreePort();
        var log = Path.Combine(context.Workspace.Logs, logName + ".log");
        FileOps.DeleteFile(log);

        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Report(context, from, "load");
        var server = ProcessRunner.RunAsync(new ProcessSpec
        {
            FileName = assetRipper,
            Arguments = ["--headless", "--port", port.ToString(), "--log", "--log-path", log],
            WorkingDirectory = Path.GetDirectoryName(assetRipper),
            LogPrefix = "[assetripper] ",
        }, line =>
        {
            // Not the banner that announces this line ("... a line will appear: \"Now listening on:\" ...")
            if (line.Contains("Now listening on: http", StringComparison.Ordinal))
                listening.TrySetResult();
            if (ExportProgressRegex().Match(line) is { Success: true } m)
                Report(context, from + (to - from) * double.Parse(m.Groups[1].Value) / double.Parse(m.Groups[2].Value),
                    "export", $"{m.Groups[1].Value}/{m.Groups[2].Value}");
        }, stop.Token);

        try
        {
            var started = await Task.WhenAny(listening.Task, server, Task.Delay(TimeSpan.FromMinutes(2), ct));
            if (started != listening.Task)
            {
                ct.ThrowIfCancellationRequested();
                throw new BuildException("AssetRipper did not start", Log.FilePath) { Code = "assetripper-start" };
            }

            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}"),
                Timeout = Timeout.InfiniteTimeSpan,
            };
            await PostAsync(http, "/Settings/Update", Settings, ct);
            Log.Info($"AssetRipper: loading {input}");
            await PostAsync(http, "/LoadFolder", [("Path", input)], ct);
            Log.Info($"AssetRipper: exporting to {output}");
            await PostAsync(http, "/Export/UnityProject", [("Path", output)], ct);
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await server;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task PostAsync(HttpClient http, string path, IEnumerable<(string Key, string Value)> form,
        CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(path, content, ct);
        }
        catch (HttpRequestException e)
        {
            throw new BuildException($"AssetRipper stopped responding ({path}: {e.Message})", inner: e) { Code = "assetripper" };
        }
        using var _ = response;
        if (response.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.OK or HttpStatusCode.SeeOther))
            throw new BuildException($"AssetRipper refused {path}: {(int)response.StatusCode} {response.ReasonPhrase}",
                await response.Content.ReadAsStringAsync(ct)) { Code = "assetripper" };
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [GeneratedRegex(@"ExportProgress : \((\d+)/(\d+)\)")]
    private static partial Regex ExportProgressRegex();
}
