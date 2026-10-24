using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Steamworks;
using Steamworks.Data;

namespace LCQuest.SteamBridge;

/// <summary>
/// One headset: on its Hello, the bridge does what the game's Online join does (LobbySlot.JoinLobbyAfterVerifying,
/// GameNetworkManager.JoinLobby): checks the lobby's data, joins the lobby and connects to its owner over Steam
/// networking, like FacepunchTransport.StartClient. Then each message goes through unchanged, reliable or not as the
/// game sent it. The host only lets in players who are in its lobby, so the bridge stays there until the end.
/// </summary>
internal sealed class Session(Bridge bridge, TcpClient client)
{
    private const int ConnectTimeoutMs = 20000;
    private const int LobbyDataTimeoutMs = 5000;

    private readonly BlockingCollection<byte[]> outbox = new();
    private HostConnection? host;
    private Lobby? lobby;
    private string lobbyName = "", hostName = "";
    private (SteamId Id, TaskCompletionSource<bool> Done)? pendingRefresh;
    private bool hello, closed;

    public static void Refuse(TcpClient client, string reason)
    {
        try
        {
            client.GetStream().Write(BridgeProtocol.Closed(reason));
        }
        catch (Exception)
        {
            // It left already
        }
        client.Close();
    }

    public void Start()
    {
        Log.Write($"Headset connected from {client.Client.RemoteEndPoint}");
        bridge.Status("Headset connected: joining the lobby...");
        var stream = client.GetStream();
        new Thread(() => ReadHeadset(stream)) { IsBackground = true }.Start();
        new Thread(() => WriteHeadset(stream)) { IsBackground = true }.Start();
    }

    public void Poll() => host?.Receive();

    public void LobbyChanged(Lobby changed)
    {
        if (lobby is { } current && current.Id == changed.Id)
            SendNames(current);
    }

    public void LobbyDataChanged(Lobby changed)
    {
        if (pendingRefresh is { } pending && pending.Id == changed.Id)
            pending.Done.TrySetResult(true);
    }

    public void Close(string reason, bool tellHeadset = true)
    {
        if (closed)
            return;
        closed = true;
        Log.Write(reason);
        bridge.Status($"{Bridge.Waiting}. Last session: {reason}");
        if (tellHeadset)
            outbox.Add(BridgeProtocol.Closed(reason));
        outbox.CompleteAdding();
        if (host != null)
        {
            host.Connection.Flush();
            host.Close();
            host = null;
        }
        lobby?.Leave();
        lobby = null;
        bridge.Ended(this);
    }

    private void OnFrame(BridgeProtocol.Kind kind, byte[] body)
    {
        if (closed)
            return;
        switch (kind)
        {
            case BridgeProtocol.Kind.Hello when !hello:
            {
                hello = true;
                using var reader = BridgeProtocol.Reader(body);
                if (reader.ReadUInt32() != BridgeProtocol.Magic || reader.ReadUInt16() != BridgeProtocol.Version)
                {
                    Close("The Quest build and the bridge do not match: update both");
                    return;
                }
                Join(reader.ReadString());
                break;
            }
            case BridgeProtocol.Kind.Data when host is { Connected: true }:
                host.Connection.SendMessage(body, 1, body.Length - 1, body[0] == 1 ? SendType.Reliable : SendType.Unreliable);
                break;
        }
    }

    private async void Join(string gameVersion)
    {
        if (bridge.LobbyToJoin(out var problem) is not { } candidate)
        {
            Close(problem);
            return;
        }

        if (!await Refresh(candidate))
        {
            Close("Could not read the lobby's data. Is the host still in it?");
            return;
        }
        if (closed)
            return;
        var version = candidate.GetData("vers");
        if (version != gameVersion)
        {
            Close($"The host plays version {version}, the Quest version {gameVersion}");
            return;
        }
        if (candidate.GetData("joinable") == "false")
        {
            Close("The host has landed or is still loading: join while the ship is in orbit");
            return;
        }
        if (candidate.MemberCount >= 4)
        {
            Close("The lobby is full");
            return;
        }

        var entered = await candidate.Join();
        if (closed)
        {
            if (entered == RoomEnter.Success)
                candidate.Leave();
            return;
        }
        if (entered != RoomEnter.Success)
        {
            Close($"Steam did not let the bridge into the lobby ({entered})");
            return;
        }
        lobby = candidate;
        lobbyName = candidate.GetData("name");
        SendNames(candidate);
        var owner = candidate.Owner;
        hostName = owner.Name;
        Log.Write($"In {owner.Name}'s lobby \"{lobbyName}\"; connecting to the host...");
        bridge.Status($"In {owner.Name}'s lobby: connecting to the host...");
        var connection = SteamNetworkingSockets.ConnectRelay<HostConnection>(owner.Id);
        connection.Session = this;
        host = connection;

        await Task.Delay(ConnectTimeoutMs);
        if (!closed && host == connection && !connection.Connected)
            Close("The host did not answer over Steam");
    }

    private async Task<bool> Refresh(Lobby candidate)
    {
        var done = new TaskCompletionSource<bool>();
        pendingRefresh = (candidate.Id, done);
        if (!candidate.Refresh())
            return false;
        var answered = await Task.WhenAny(done.Task, Task.Delay(LobbyDataTimeoutMs)) == done.Task;
        pendingRefresh = null;
        return answered;
    }

    private void SendNames(Lobby current)
    {
        if (!closed)
            outbox.Add(BridgeProtocol.Names(current.Members.Select(m => new KeyValuePair<ulong, string>(m.Id.Value, m.Name))));
    }

    private void HostConnected()
    {
        Log.Write("Connected to the host. Playing.");
        bridge.Status($"Playing in {hostName}'s lobby \"{lobbyName}\"");
        outbox.Add(BridgeProtocol.Joined(SteamClient.SteamId.Value, lobbyName));
    }

    private void FromHost(IntPtr data, int size)
    {
        if (closed)
            return;
        var bytes = new byte[size];
        Marshal.Copy(data, bytes, 0, size);
        outbox.Add(BridgeProtocol.Data(true, bytes, 0, size));
    }

    private void ReadHeadset(NetworkStream stream)
    {
        try
        {
            while (BridgeProtocol.Read(stream, out var kind, out var body))
                bridge.Post(() => OnFrame(kind, body));
            bridge.Post(() => Close("The headset left", tellHeadset: false));
        }
        catch (Exception e)
        {
            bridge.Post(() => Close($"Lost the headset ({e.GetBaseException().Message})", tellHeadset: false));
        }
    }

    private void WriteHeadset(NetworkStream stream)
    {
        try
        {
            foreach (var frame in outbox.GetConsumingEnumerable())
                stream.Write(frame);
        }
        catch (Exception e)
        {
            bridge.Post(() => Close($"Lost the headset ({e.GetBaseException().Message})", tellHeadset: false));
        }
        finally
        {
            client.Close();
        }
    }

    /// <summary>The Steam networking connection to the lobby's owner, the host</summary>
    private sealed class HostConnection : ConnectionManager
    {
        public Session? Session;

        public override void OnConnected(ConnectionInfo info)
        {
            base.OnConnected(info);
            Session?.HostConnected();
        }

        public override void OnDisconnected(ConnectionInfo info)
        {
            base.OnDisconnected(info);
            Session?.Close($"The host ended the connection ({info.EndReason})");
        }

        public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel) =>
            Session?.FromHost(data, size);
    }
}
