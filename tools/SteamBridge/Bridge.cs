using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Steamworks;
using Steamworks.Data;

namespace LCQuest.SteamBridge;

/// <summary>
/// One thread owns all state: it runs Steam's callbacks, polls the host connection, and runs whatever the socket
/// threads and the window post to it (async continuations too, through <see cref="LoopContext"/>). The lobby to join
/// is the one picked in the window, an invite accepted in Steam, or the only friend's lobby there is. What the bridge
/// does is reported through the events, raised on the bridge's thread.
/// </summary>
internal sealed class Bridge
{
    public const uint LethalCompany = 1966720;

    private readonly ConcurrentQueue<Action> posted = new();
    private readonly ushort port;
    private Session? session;
    private Lobby? picked;
    private List<(Friend Friend, Lobby Lobby)> friendLobbies = [];
    private string? shownLobbies;
    private DateTime nextRefresh;
    private volatile bool quit;

    public Bridge(ushort port) => this.port = port;

    /// <summary>Steam is up: the account's name, and the addresses to type on the Quest</summary>
    public event Action<string, IReadOnlyList<string>>? Started;

    /// <summary>The bridge could not start (Steam not running, port taken); it has stopped</summary>
    public event Action<string>? Failed;

    /// <summary>Friends in Lethal Company lobbies (their names), and the index of the picked one or -1</summary>
    public event Action<IReadOnlyList<string>, int>? LobbiesChanged;

    /// <summary>What the bridge is doing now, in a line</summary>
    public event Action<string>? StatusChanged;

    /// <summary>What the lobby choice is ("DmitryIgorevichKZN's lobby (invite)", or the automatic rule)</summary>
    public event Action<string>? PickChanged;

    public void Post(Action action) => posted.Enqueue(action);

    public void Stop() => quit = true;

    /// <summary>Picks a friend's lobby by its index in the last LobbiesChanged list; -1 goes back to the automatic pick</summary>
    public void PickFriend(int index) => Post(() =>
    {
        if (index >= 0 && index < friendLobbies.Count)
            Pick(friendLobbies[index].Lobby, $"{friendLobbies[index].Friend.Name}'s lobby");
        else if (picked != null)
        {
            picked = null;
            ShowPick();
            ShowLobbies(force: true);
        }
    });

    public void Run()
    {
        try
        {
            // As the game: Steam then shows this account in Lethal Company, and friends' lobbies and invites reach it
            SteamClient.Init(LethalCompany, asyncCallbacks: false);
        }
        catch (Exception e)
        {
            Fail($"Steam is not available ({e.Message}). Start Steam, log in with the account that owns Lethal Company, and try again.");
            return;
        }

        var listener = new TcpListener(IPAddress.Any, port);
        try
        {
            listener.Start();
        }
        catch (SocketException e)
        {
            SteamClient.Shutdown();
            Fail($"Cannot listen on TCP port {port}: {e.Message}. Is another bridge running?");
            return;
        }

        // Windows sleeps in 15.6 ms steps by default; the loop polls Steam every millisecond
        if (OperatingSystem.IsWindows())
            TimeBeginPeriod(1);
        try
        {
            Loop(listener);
        }
        finally
        {
            listener.Stop();
            SteamClient.Shutdown();
        }
    }

    private void Loop(TcpListener listener)
    {
        SynchronizationContext.SetSynchronizationContext(new LoopContext(this));
        SteamNetworkingUtils.InitRelayNetworkAccess();
        SteamFriends.OnGameLobbyJoinRequested += (lobby, friend) => Pick(lobby, $"{new Friend(friend).Name}'s lobby (invite)");
        SteamMatchmaking.OnLobbyMemberJoined += (lobby, _) => session?.LobbyChanged(lobby);
        SteamMatchmaking.OnLobbyMemberLeave += (lobby, _) => session?.LobbyChanged(lobby);
        SteamMatchmaking.OnLobbyDataChanged += lobby => session?.LobbyDataChanged(lobby);
        new Thread(() => Accept(listener)) { IsBackground = true }.Start();

        var addresses = LocalAddresses().Select(a => port == BridgeProtocol.DefaultPort ? a : $"{a}:{port}").ToList();
        Log.Write($"Steam: {SteamClient.Name} ({SteamClient.SteamId})");
        Log.Write($"On the Quest: main menu, \"Via Steam bridge: On\", address {string.Join(" or ", addresses)}, then \"Join LAN session\".");
        Started?.Invoke(SteamClient.Name, addresses);
        ShowPick();
        Status(Waiting);

        while (!quit)
        {
            SteamClient.RunCallbacks();
            session?.Poll();
            while (posted.TryDequeue(out var action))
                action();
            if (DateTime.UtcNow >= nextRefresh)
                RefreshFriendLobbies();
            Thread.Sleep(1);
        }

        session?.Close("The bridge was closed");
    }

    public const string Waiting = "Waiting for the headset to join";

    public void Status(string text) => StatusChanged?.Invoke(text);

    /// <summary>The lobby a joining headset goes to, or null with the reason</summary>
    public Lobby? LobbyToJoin(out string problem)
    {
        problem = "";
        if (picked is { } lobby)
            return lobby;
        if (friendLobbies.Count == 1)
            return friendLobbies[0].Lobby;
        problem = friendLobbies.Count == 0
            ? "No friend of the bridge's Steam account is in a Lethal Company lobby"
            : "Several friends are in lobbies: pick one in the bridge window";
        return null;
    }

    public void Ended(Session ended)
    {
        if (session == ended)
            session = null;
    }

    private void Fail(string message)
    {
        Log.Write(message);
        Failed?.Invoke(message);
    }

    private void Pick(Lobby lobby, string by)
    {
        picked = lobby;
        Log.Write($"Lobby picked: {by}. Join from the Quest now.");
        PickChanged?.Invoke($"Picked: {by}");
        ShowLobbies(force: true);
    }

    private void ShowPick()
    {
        if (picked == null)
            PickChanged?.Invoke("Not picked: a joining headset goes to the only friend's lobby there is, or accept an invite in Steam");
    }

    private void Accept(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                return;
            }
            client.NoDelay = true;
            Post(() =>
            {
                if (session != null)
                {
                    Session.Refuse(client, "The bridge is already relaying another headset");
                    return;
                }
                session = new Session(this, client);
                session.Start();
            });
        }
    }

    private void RefreshFriendLobbies()
    {
        nextRefresh = DateTime.UtcNow.AddSeconds(5);
        friendLobbies = SteamFriends.GetFriends()
            .Where(f => f.IsPlayingThisGame && f.GameInfo?.Lobby != null)
            .Select(f => (f, f.GameInfo!.Value.Lobby!.Value))
            .ToList();
        ShowLobbies(force: false);
    }

    private void ShowLobbies(bool force)
    {
        var names = friendLobbies.Select(l => l.Friend.Name).ToList();
        var shown = string.Join("\n", names.Select((n, i) => $"  {i + 1}. {n}"));
        if (shown == shownLobbies && !force)
            return;
        if (shown != shownLobbies)
            Log.Write(names.Count == 0 ? "No friends in Lethal Company lobbies right now." : "Friends in Lethal Company lobbies:\n" + shown);
        shownLobbies = shown;
        var index = picked is { } lobby ? friendLobbies.FindIndex(l => l.Lobby.Id == lobby.Id) : -1;
        LobbiesChanged?.Invoke(names, index);
    }

    private static List<string> LocalAddresses()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Select(a => a.ToString())
            .Distinct()
            .ToList();
        return addresses.Count > 0 ? addresses : ["this PC's IP"];
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    /// <summary>Async continuations (lobby joins, refreshes) resume on the bridge's loop</summary>
    private sealed class LoopContext(Bridge bridge) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => bridge.Post(() => d(state));
        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
    }
}
