using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace LCQuest
{
    /// <summary>
    /// Joins a vanilla host's Online (Steam) lobby through the Steam bridge (tools/SteamBridge): a PC running Steam
    /// with this player's account joins the lobby, connects to the host over Steam networking and relays the
    /// transport's messages to and from here over TCP (BridgeProtocol). To the host this player is the bridge's
    /// Steam account. The game itself stays in LAN mode (no Steam on the Quest), so the parts of the Online join the
    /// host checks are done here: the connection data carries the bridge's Steam id, and the player reports it with
    /// SendNewPlayerValuesServerRpc once spawned (the host disconnects players whose id is not in its lobby).
    /// </summary>
    public class SteamBridgeTransport : NetworkTransport
    {
        /// <summary>
        /// Persona names of the host's lobby members by Steam id. The host sends the players' Steam ids and clients name
        /// them with Steamworks' Friend.Name, which an Android override (LCVR.Standalone) answers from this table; it
        /// lives in Facepunch.Steamworks and cannot reference our scripts, so the table is shared as AppDomain data.
        /// </summary>
        public static readonly Dictionary<ulong, string> Names = new Dictionary<ulong, string>();
        private const string NamesKey = "LCQuest.SteamNames";

        /// <summary>The bridge's Steam account, this player's id on the host; 0 until joined</summary>
        public static ulong SteamId { get; private set; }

        /// <summary>What the bridge connection is doing, for the address panel</summary>
        public static string Status { get; private set; } = "";

        public string Address = "127.0.0.1";
        public ushort Port = BridgeProtocol.DefaultPort;

        private struct Incoming
        {
            public BridgeProtocol.Kind Kind;
            public byte[] Body;
            public string Reason;  // a Closed from this side: the socket failed
        }

        private readonly ConcurrentQueue<Incoming> inbox = new ConcurrentQueue<Incoming>();
        private readonly BlockingCollection<byte[]> outbox = new BlockingCollection<byte[]>();
        private TcpClient tcp;
        private volatile bool open;
        private bool connected, disconnected, reportedSteamId;
        private string gameVersion;

        static SteamBridgeTransport() => AppDomain.CurrentDomain.SetData(NamesKey, Names);

        public override ulong ServerClientId => 0;

        public override void Initialize(NetworkManager networkManager = null)
        {
            while (inbox.TryDequeue(out _)) { }
            while (outbox.TryTake(out _)) { }
            connected = disconnected = reportedSteamId = false;
            SteamId = 0;
        }

        public override bool StartServer()
        {
            Debug.LogError("[LCQuest] the Steam bridge only joins; host in LAN mode instead");
            return false;
        }

        public override bool StartClient()
        {
            gameVersion = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.gameVersionNum.ToString() : "";
            Status = $"Connecting to the bridge at {Address}:{Port}...";
            Debug.Log($"[LCQuest] {Status}");
            open = true;
            new Thread(Run) { IsBackground = true, Name = "LCQuest bridge" }.Start();
            return true;
        }

        public override void Send(ulong clientId, ArraySegment<byte> data, NetworkDelivery delivery)
        {
            if (!open) return;
            var reliable = delivery != NetworkDelivery.Unreliable && delivery != NetworkDelivery.UnreliableSequenced;
            outbox.Add(BridgeProtocol.Data(reliable, data.Array, data.Offset, data.Count));
        }

        public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
        {
            while (inbox.TryDequeue(out var message))
                Handle(message);
            clientId = ServerClientId;
            payload = default;
            receiveTime = Time.realtimeSinceStartup;
            return NetworkEvent.Nothing;
        }

        public override void DisconnectLocalClient() => Close();

        public override void DisconnectRemoteClient(ulong clientId)
        {
        }

        public override ulong GetCurrentRtt(ulong clientId) => 0;

        public override void Shutdown() => Close();

        private void Update()
        {
            if (connected && !disconnected && !reportedSteamId) ReportSteamId();
        }

        private void Handle(Incoming message)
        {
            if (disconnected) return;
            var now = Time.realtimeSinceStartup;
            switch (message.Kind)
            {
                case BridgeProtocol.Kind.Joined:
                {
                    using var reader = BridgeProtocol.Reader(message.Body);
                    SteamId = reader.ReadUInt64();
                    var lobby = reader.ReadString();
                    // Read when Netcode sends its connection request, right after this event; an Online client sends this
                    NetworkManager.Singleton.NetworkConfig.ConnectionData = Encoding.ASCII.GetBytes($"{gameVersion},{SteamId}");
                    connected = true;
                    Status = $"Joined \"{lobby}\" through the bridge";
                    Debug.Log($"[LCQuest] {Status} as Steam user {SteamId}");
                    InvokeOnTransportEvent(NetworkEvent.Connect, ServerClientId, default, now);
                    break;
                }
                case BridgeProtocol.Kind.Data:
                    if (connected)
                        InvokeOnTransportEvent(NetworkEvent.Data, ServerClientId,
                            new ArraySegment<byte>(message.Body, 1, message.Body.Length - 1), now);
                    break;
                case BridgeProtocol.Kind.Names:
                {
                    using var reader = BridgeProtocol.Reader(message.Body);
                    for (int i = 0, count = reader.ReadInt32(); i < count; i++)
                        Names[reader.ReadUInt64()] = reader.ReadString();
                    break;
                }
                case BridgeProtocol.Kind.Closed:
                {
                    if (message.Reason != null)
                        Status = message.Reason;
                    else
                        using (var reader = BridgeProtocol.Reader(message.Body))
                            Status = reader.ReadString();
                    Debug.Log($"[LCQuest] bridge: {Status}");
                    disconnected = true;
                    Close();
                    InvokeOnTransportEvent(NetworkEvent.Disconnect, ServerClientId, default, now);
                    break;
                }
            }
        }

        /// <summary>What an Online client does as its player spawns (PlayerControllerB.ConnectClientToPlayerObject)</summary>
        private void ReportSteamId()
        {
            var player = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
            if (player == null || !player.IsOwner) return;
            reportedSteamId = true;
            try
            {
                typeof(PlayerControllerB)
                    .GetMethod("SendNewPlayerValuesServerRpc", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(player, new object[] { SteamId });
                Debug.Log($"[LCQuest] reported Steam id {SteamId} to the host");
            }
            catch (Exception e)
            {
                Debug.LogError($"[LCQuest] could not report the Steam id: {e}");
            }
        }

        private void Run()
        {
            TcpClient client = null;
            try
            {
                client = new TcpClient { NoDelay = true };
                if (!client.ConnectAsync(Address, Port).Wait(5000))
                    throw new TimeoutException("no answer");
                tcp = client;
                if (!open)
                {
                    client.Close();
                    return;
                }
                var stream = client.GetStream();
                new Thread(() => Write(stream)) { IsBackground = true, Name = "LCQuest bridge send" }.Start();
                outbox.Add(BridgeProtocol.Hello(gameVersion));
                while (open && BridgeProtocol.Read(stream, out var kind, out var body))
                    inbox.Enqueue(new Incoming { Kind = kind, Body = body });
                Lost("The bridge closed the connection");
            }
            catch (Exception e)
            {
                Lost(tcp == null ? $"No bridge at {Address}:{Port} ({e.GetBaseException().Message})" : $"Lost the bridge ({e.GetBaseException().Message})");
            }
        }

        private void Write(NetworkStream stream)
        {
            try
            {
                while (open)
                {
                    if (!outbox.TryTake(out var frame, 200)) continue;
                    stream.Write(frame, 0, frame.Length);
                }
            }
            catch (Exception e)
            {
                Lost($"Lost the bridge ({e.GetBaseException().Message})");
            }
        }

        /// <summary>From the socket threads: reported on the main thread as the bridge's own Closed would be</summary>
        private void Lost(string reason)
        {
            if (open) inbox.Enqueue(new Incoming { Kind = BridgeProtocol.Kind.Closed, Reason = reason });
        }

        private void Close()
        {
            open = false;
            try
            {
                tcp?.Close();
            }
            catch (Exception)
            {
                // Already closed
            }
            tcp = null;
        }
    }
}
