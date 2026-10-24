using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace LCQuest.SteamBridge;

/// <summary>
/// The bridge in a window: the account, the address to type on the Quest, the friends' lobbies to pick from, what
/// the bridge is doing, and its log. The bridge runs on its own thread; its events come back here on the UI thread.
/// </summary>
public partial class MainWindow : Window
{
    private const int LogLines = 400;

    private readonly List<string> log = [];
    private Bridge? bridge;
    private Thread? thread;
    private bool showingLobbies;

    public MainWindow()
    {
        InitializeComponent();
        Log.Written += line => Dispatcher.UIThread.Post(() => AddLog(line));
        LobbyList.SelectionChanged += (_, _) =>
        {
            if (!showingLobbies && LobbyList.SelectedIndex >= 0)
                bridge?.PickFriend(LobbyList.SelectedIndex);
        };
        AutoButton.Click += (_, _) => bridge?.PickFriend(-1);
        RetryButton.Click += (_, _) => Start();
        Closing += (_, _) =>
        {
            bridge?.Stop();
            thread?.Join(2000);
        };

        if (Program.ScreenshotFolder is { } folder)
            Opened += async (_, _) => await SaveScreenshotsAsync(folder);
        else
            Start();
    }

    private void Start()
    {
        RetryButton.IsVisible = false;
        AccountText.Text = "Starting Steam...";
        StatusText.Text = "Starting...";
        var started = new Bridge(Program.Port);
        started.Started += (name, addresses) => Ui(() =>
        {
            AccountText.Text = name;
            AddressText.Text = string.Join("\n", addresses);
        });
        started.Failed += message => Ui(() =>
        {
            AccountText.Text = "Not connected";
            StatusText.Text = message;
            RetryButton.IsVisible = true;
            bridge = null;
        });
        started.LobbiesChanged += (names, picked) => Ui(() => ShowLobbies(names, picked));
        started.StatusChanged += text => Ui(() => StatusText.Text = text);
        started.PickChanged += text => Ui(() => PickText.Text = text);
        bridge = started;
        thread = new Thread(started.Run) { IsBackground = true, Name = "Bridge" };
        thread.Start();
    }

    private void ShowLobbies(IReadOnlyList<string> names, int picked)
    {
        showingLobbies = true;
        LobbyList.ItemsSource = names.ToList();
        LobbyList.SelectedIndex = picked;
        NoLobbiesText.IsVisible = names.Count == 0;
        showingLobbies = false;
    }

    private void AddLog(string line)
    {
        log.Add(line);
        if (log.Count > LogLines)
            log.RemoveRange(0, log.Count - LogLines);
        LogBox.Text = string.Join("\n", log);
        LogBox.CaretIndex = LogBox.Text.Length;
    }

    private static void Ui(Action action) => Dispatcher.UIThread.Post(action);

    /// <summary>--screenshot: the window's states with sample data, for checking the layout without Steam</summary>
    private async Task SaveScreenshotsAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        AccountText.Text = "Dobutsu";
        AddressText.Text = "192.168.1.84";
        StatusText.Text = Bridge.Waiting;
        ShowLobbies([], -1);
        PickText.Text = "Not picked: a joining headset goes to the only friend's lobby there is, or accept an invite in Steam";
        await Render("waiting");

        ShowLobbies(["DmitryIgorevichKZN", "Friend Two"], 0);
        PickText.Text = "Picked: DmitryIgorevichKZN's lobby (invite)";
        StatusText.Text = "Playing in DmitryIgorevichKZN's lobby \"DmitryIgorevichKZN's Crew\"";
        for (var i = 0; i < 6; i++)
            AddLog($"15:2{i}:00  sample log line {i}");
        await Render("playing");

        AccountText.Text = "Not connected";
        StatusText.Text = "Steam is not available (Steam API init failed). Start Steam, log in with the account that owns Lethal Company, and try again.";
        RetryButton.IsVisible = true;
        ShowLobbies([], -1);
        await Render("failed");
        Close();

        async Task Render(string name)
        {
            await Task.Delay(500);
            var size = new PixelSize((int)Math.Ceiling(Bounds.Width), (int)Math.Ceiling(Bounds.Height));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
            bitmap.Render(this);
            bitmap.Save(Path.Combine(folder, $"bridge-{name}.png"), new PngBitmapEncoderOptions());
        }
    }
}
