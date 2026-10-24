using Avalonia;

namespace LCQuest.SteamBridge;

// The bridge's window. Without arguments it opens the window; the options are for testing:
//
//   LCQuestSteamBridge [--port <n>] [--console] [--screenshot <folder>]
//
// --console: no window; the log goes to stdout, and lines on stdin pick a lobby (a number, or a for automatic) or quit (q)
// --screenshot: the window's states with sample data as PNGs, without starting the bridge
internal static class Program
{
    public static ushort Port = BridgeProtocol.DefaultPort;
    public static string? ScreenshotFolder;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            File.WriteAllText(Log.FilePath, "");
        }
        catch (Exception)
        {
            // See Log.Write
        }

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--port" && ushort.TryParse(args[i + 1], out var p))
                Port = p;
            if (args[i] == "--screenshot")
                ScreenshotFolder = Path.GetFullPath(args[i + 1]);
        }

        if (args.Contains("--console"))
            return RunConsole();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Also what the XAML previewer of an IDE calls</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static int RunConsole()
    {
        Log.Written += Console.WriteLine;
        var bridge = new Bridge(Port);
        var failed = false;
        bridge.Failed += _ => failed = true;
        bridge.Started += (_, _) => Log.Write("Lobby: invite this account from the game, or type a number from the list. a: automatic, q: quit.");
        new Thread(() =>
        {
            while (Console.ReadLine() is { } line)
            {
                line = line.Trim();
                if (line == "q")
                {
                    bridge.Stop();
                    return;
                }
                if (line == "a")
                    bridge.PickFriend(-1);
                else if (int.TryParse(line, out var n))
                    bridge.PickFriend(n - 1);
            }
        }) { IsBackground = true }.Start();
        bridge.Run();
        return failed ? 1 : 0;
    }
}

internal static class Log
{
    private static readonly object FileLock = new();

    /// <summary>Each line as written, with its time; raised on the writer's thread</summary>
    public static event Action<string>? Written;

    /// <summary>The session's log, next to the exe (what a player sends when something goes wrong)</summary>
    public static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "LCQuestSteamBridge.log");

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        lock (FileLock)
        {
            try
            {
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Read-only folder: the window still shows the log
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        Written?.Invoke(line);
    }
}
