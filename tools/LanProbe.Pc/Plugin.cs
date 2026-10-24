using System;
using System.Linq;
using BepInEx;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LcQuest;

/// <summary>
/// Command line: -lanprobe-host            choose LAN, host with remote connections allowed (listens on 0.0.0.0:7777)
///               -lanprobe-client=&lt;ip&gt;  choose LAN, join &lt;ip&gt;:7777 (vanilla LAN clients always use 127.0.0.1)
/// Every step is logged as "[LanProbe] ..." in the -logFile given to the game.
/// </summary>
[BepInPlugin("lcquest.lanprobe", "LanProbe", "0.1.0")]
public class Plugin : BaseUnityPlugin
{
    private string role;
    private string address;
    private bool choseLaunch, started;
    private float sceneTime, nextStatus;

    private void Awake()
    {
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (arg == "-lanprobe-host")
                role = "host";
            else if (arg.StartsWith("-lanprobe-client="))
            {
                role = "client";
                address = arg.Substring("-lanprobe-client=".Length);
            }
        }

        Log($"role={role ?? "none"} address={address}");
        if (role is null)
            return;

        SceneManager.sceneLoaded += (scene, _) =>
        {
            sceneTime = Time.realtimeSinceStartup;
            Log($"scene loaded: {scene.name}");
        };
        DontDestroyOnLoad(gameObject);
    }

    private void Update()
    {
        if (role is null)
            return;

        var scene = SceneManager.GetActiveScene().name;
        var elapsed = Time.realtimeSinceStartup - sceneTime;

        if (scene == "InitSceneLaunchOptions" && !choseLaunch && elapsed > 2f && IngamePlayerSettings.Instance != null)
        {
            var pre = FindObjectOfType<PreInitSceneScript>();
            if (pre != null)
            {
                choseLaunch = true;
                Log("choosing LAN launch mode");
                pre.ChooseLaunchOption(false);
            }
        }

        if (scene == "MainMenu" && !started && elapsed > 3f)
        {
            var menu = FindObjectsOfType<MenuManager>().FirstOrDefault(m => !m.isInitScene);
            if (menu != null && GameNetworkManager.Instance != null && NetworkManager.Singleton != null)
                Start(menu);
        }

        if (started && Time.realtimeSinceStartup > nextStatus)
        {
            nextStatus = Time.realtimeSinceStartup + 10f;
            var nm = NetworkManager.Singleton;
            if (nm != null)
                Log($"status: scene={scene} server={nm.IsServer} connectedClient={nm.IsConnectedClient} " +
                    $"clients={(nm.IsServer ? nm.ConnectedClientsIds.Count : -1)} players={GameNetworkManager.Instance?.connectedPlayers}");
        }
    }

    private void Start(MenuManager menu)
    {
        started = true;
        var nm = NetworkManager.Singleton;
        var transport = nm.GetComponent<UnityTransport>();
        Log($"disableSteam={GameNetworkManager.Instance.disableSteam} version={GameNetworkManager.Instance.gameVersionNum} " +
            $"protocol={nm.NetworkConfig.ProtocolVersion} transport={transport?.ConnectionData.Address}:{transport?.ConnectionData.Port}");

        nm.OnClientConnectedCallback += id => Log($"client connected: {id}");
        nm.OnClientDisconnectCallback += id => Log($"client disconnected: {id} reason='{nm.DisconnectReason}'");

        if (role == "host")
        {
            transport.ConnectionData.ServerListenAddress = "0.0.0.0";
            GameNetworkManager.Instance.lobbyHostSettings = new HostSettings("lanprobe", false);
            Log("starting host on 0.0.0.0");
            GameNetworkManager.Instance.StartHost();
        }
        else
        {
            transport.ConnectionData.Address = address;
            Log($"starting client to {address}:{transport.ConnectionData.Port}");
            menu.StartAClient();
        }
    }

    private static void Log(string message) => Debug.Log($"[LanProbe] {message}");
}
