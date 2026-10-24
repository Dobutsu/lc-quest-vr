using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LcQuest;

/// <summary>
/// Join a LAN host on another machine: vanilla LAN clients always join 127.0.0.1. Adds a "Host IP" field beside the
/// main menu's "Join LAN session" (in LAN mode); the address is kept in BepInEx/config/lcquest.lanjoin.cfg. Blank
/// joins this PC, as vanilla does. Nothing else changes: the host and the other players need no mod.
/// </summary>
[BepInPlugin("lcquest.lanjoin", "LanJoin", "0.1.0")]
public class Plugin : BaseUnityPlugin
{
    private ConfigEntry<string> host;

    private void Awake()
    {
        host = Config.Bind("Join", "HostAddress", "", "IP (or IP:port) of the LAN host to join; blank: this PC");
        SceneManager.sceneLoaded += (scene, _) =>
        {
            Apply();
            if (scene.name == "MainMenu") AddField();
        };
        Logger.LogInfo($"LAN join address: '{host.Value}'");
    }

    private void Apply()
    {
        var transport = NetworkManager.Singleton != null ? NetworkManager.Singleton.GetComponent<UnityTransport>() : null;
        if (transport == null) return;
        var text = host.Value.Trim();
        var parts = text.Split(':');
        transport.ConnectionData.Address = text.Length > 0 ? parts[0] : "127.0.0.1";
        if (parts.Length > 1 && ushort.TryParse(parts[1], out var port)) transport.ConnectionData.Port = port;
    }

    /// <summary>A copy of the host settings' lobby name field, to the right of "Join LAN session"</summary>
    private void AddField()
    {
        var menu = FindObjectsOfType<MenuManager>().FirstOrDefault(m => !m.isInitScene);
        var join = FindObjectsOfType<Button>(true).FirstOrDefault(b => b.name == "StartLAN");
        if (menu == null || menu.lobbyNameInputField == null || join == null)
        {
            Logger.LogWarning("no menu, lobby name field or StartLAN button: no address field");
            return;
        }

        var joinRect = (RectTransform)join.transform;
        var go = Instantiate(menu.lobbyNameInputField.gameObject, joinRect.parent, false);
        go.name = "LanJoin HostAddress";
        go.SetActive(true);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = joinRect.anchorMin;
        rect.anchorMax = joinRect.anchorMax;
        rect.pivot = new Vector2(0f, 0.5f);
        rect.sizeDelta = new Vector2(260f, 30f);
        rect.anchoredPosition = joinRect.anchoredPosition + new Vector2(joinRect.rect.width * (1f - joinRect.pivot.x) + 10f, 0f);

        var field = go.GetComponent<TMP_InputField>();
        field.onValueChanged = new TMP_InputField.OnChangeEvent();
        field.onEndEdit = new TMP_InputField.SubmitEvent();
        field.onSubmit = new TMP_InputField.SubmitEvent();
        field.onSelect = new TMP_InputField.SelectionEvent();
        field.onDeselect = new TMP_InputField.SelectionEvent();
        field.contentType = TMP_InputField.ContentType.Standard;
        field.characterLimit = 21;
        field.text = host.Value;
        if (field.placeholder is TMP_Text placeholder) placeholder.text = "Host IP (blank: this PC)";
        field.onEndEdit.AddListener(value =>
        {
            host.Value = value.Trim();
            Apply();
            Logger.LogInfo($"LAN join address: '{host.Value}'");
        });
    }
}
