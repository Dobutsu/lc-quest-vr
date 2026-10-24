using System.IO;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LCQuest
{
    /// <summary>
    /// The Quest has no Steam client, so the game always runs in LAN mode: the launch screen's Online choice loads
    /// InitSceneLANMode instead of InitScene. The LAN scene's transport is serialized with 127.0.0.1 (vanilla LAN
    /// joins only its own machine); the host to join is kept in &lt;persistentDataPath&gt;/lan_host.txt
    /// ("192.168.1.84" or "192.168.1.84:7777"), typed in the main menu's address panel (LanJoinPanel).
    /// With "Via Steam bridge" on (bridge.txt), that address is a Steam bridge's instead, and the join goes through
    /// SteamBridgeTransport to the Online lobby the bridge picked; hosting always uses the LAN transport.
    /// </summary>
    public static class LanBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            Debug.Log($"[LCQuest] LAN bootstrap; host file: {HostFile}");
        }

        private static string HostFile => Path.Combine(Application.persistentDataPath, "lan_host.txt");
        private static string BridgeFile => Path.Combine(Application.persistentDataPath, "bridge.txt");

        /// <summary>Whether "Join LAN session" goes through a Steam bridge at the saved address</summary>
        public static bool ViaBridge
        {
            get => File.Exists(BridgeFile);
            set
            {
                if (value) File.WriteAllText(BridgeFile, "on");
                else if (File.Exists(BridgeFile)) File.Delete(BridgeFile);
            }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == "InitScene")
            {
                Debug.Log("[LCQuest] Online mode needs Steam; loading LAN mode instead");
                SceneManager.LoadScene("InitSceneLANMode");
                return;
            }

            var host = ReadHost();
            if (host != null) Apply(host);
            if (scene.name == "MainMenu")
            {
                UseTransport(false);
                foreach (var menu in Object.FindObjectsOfType<MenuManager>())
                    if (!menu.isInitScene) LanJoinPanel.Attach(menu);
            }
        }

        /// <summary>The saved host address, or null</summary>
        public static string ReadHost()
        {
            if (!File.Exists(HostFile)) return null;
            var text = File.ReadAllText(HostFile).Trim();
            return text.Length > 0 ? text : null;
        }

        /// <summary>Saves the address and sets it on the transport for the next join</summary>
        public static void SetHost(string host)
        {
            File.WriteAllText(HostFile, host);
            if (host.Length > 0) Apply(host);
        }

        /// <summary>Called as "Join LAN session" is clicked, before the game starts the client</summary>
        public static void PrepareJoin() => UseTransport(ViaBridge);

        private static void UseTransport(bool bridge)
        {
            var manager = NetworkManager.Singleton;
            if (manager == null) return;
            NetworkTransport transport = manager.GetComponent<UnityTransport>();
            if (bridge)
            {
                var steam = manager.GetComponent<SteamBridgeTransport>();
                if (steam == null) steam = manager.gameObject.AddComponent<SteamBridgeTransport>();
                var parts = (ReadHost() ?? "").Split(':');
                steam.Address = parts[0].Length > 0 ? parts[0] : "127.0.0.1";
                steam.Port = parts.Length > 1 && ushort.TryParse(parts[1], out var p) ? p : BridgeProtocol.DefaultPort;
                transport = steam;
            }
            if (transport != null && manager.NetworkConfig.NetworkTransport != transport)
            {
                manager.NetworkConfig.NetworkTransport = transport;
                Debug.Log($"[LCQuest] transport: {transport.GetType().Name}");
            }
        }

        private static void Apply(string host)
        {
            var manager = NetworkManager.Singleton;
            var transport = manager != null ? manager.GetComponent<UnityTransport>() : null;
            if (transport == null) return;
            var parts = host.Split(':');
            var port = parts.Length > 1 && ushort.TryParse(parts[1], out var p) ? p : transport.ConnectionData.Port;
            transport.ConnectionData.Address = parts[0];
            transport.ConnectionData.Port = port;
            Debug.Log($"[LCQuest] LAN host {parts[0]}:{port}");
        }
    }
}
