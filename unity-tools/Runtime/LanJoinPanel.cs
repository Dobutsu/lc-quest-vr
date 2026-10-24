using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LCQuest
{
    /// <summary>
    /// The main menu's address panel, beside its buttons: the host to join ("Join LAN session"), typed on a keypad of
    /// the menu's own buttons (a world-space canvas LCVR's pointers already click; the system keyboard is not reliable
    /// in VR), and this headset's own addresses for friends to join when it hosts. The address is kept in
    /// lan_host.txt (LanBootstrap) and set on the transport as it is typed. "Via Steam bridge" makes the address a
    /// Steam bridge's (SteamBridgeTransport), and the line under it shows how the bridge connection went.
    /// </summary>
    public class LanJoinPanel : MonoBehaviour
    {
        private const int MaxLength = 21;  // "255.255.255.255:65535"
        private static readonly string[] Keys = { "1", "2", "3", "4", "5", "6", "7", "8", "9", ".", "0", "<" };

        private TextMeshProUGUI titleText, addressText, statusText;
        private TMP_Text bridgeText;
        private string address = "";

        /// <summary>Beside the menu's "Join LAN session" button, built from it</summary>
        public static void Attach(MenuManager menu)
        {
            var join = menu.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name == "StartLAN") ??
                       FindObjectsOfType<Button>(true).FirstOrDefault(b => b.name == "StartLAN");
            if (join == null || join.transform.parent == null)
            {
                Debug.Log("[LCQuest] no StartLAN button: no address panel");
                return;
            }
            var buttons = (RectTransform)join.transform.parent;
            if (buttons.Find("LCQuest JoinAddress") != null) return;
            var go = new GameObject("LCQuest JoinAddress", typeof(RectTransform));
            go.layer = buttons.gameObject.layer;
            go.transform.SetParent(buttons, false);
            go.AddComponent<LanJoinPanel>().Build(buttons, join);

            // The transport is picked before the button's own handler (MenuManager.StartAClient) starts the client
            var original = join.onClick;
            join.onClick = new Button.ButtonClickedEvent();
            join.onClick.AddListener(() =>
            {
                LanBootstrap.PrepareJoin();
                original.Invoke();
            });
        }

        private void Build(RectTransform buttons, Button template)
        {
            const float width = 330f, keyWidth = 96f, keyHeight = 52f, gap = 8f;
            var rect = (RectTransform)transform;
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 0.5f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.sizeDelta = new Vector2(width, buttons.rect.height);
            rect.anchoredPosition = new Vector2(24f, 0f);

            // The menu box's frame
            var frame = buttons.GetComponent<Image>();
            var background = gameObject.AddComponent<Image>();
            if (frame != null)
            {
                background.sprite = frame.sprite;
                background.type = frame.type;
                background.color = frame.color;
                background.material = frame.material;
            }

            var label = template.GetComponentInChildren<TMP_Text>(true);
            var top = rect.sizeDelta.y / 2f;
            titleText = Text(label, "", 22f, top - 40f, width - 30f);
            addressText = Text(label, "", 30f, top - 82f, width - 30f);
            var own = string.Join("  ", LocalAddresses());
            Text(label, own.Length > 0 ? $"This headset: {own}" : "This headset: no network", 15f, top - 120f, width - 30f);

            for (var i = 0; i < Keys.Length; i++)
            {
                var key = Keys[i];
                var x = (i % 3 - 1) * (keyWidth + gap);
                var y = top - 180f - i / 3 * (keyHeight + gap);
                Key(template, key == "<" ? "Del" : key, new Vector2(x, y), new Vector2(keyWidth, keyHeight), () => Press(key));
            }
            var below = top - 180f - Keys.Length / 3 * (keyHeight + gap);
            bridgeText = Key(template, "", new Vector2(0f, below), new Vector2(3f * keyWidth + 2f * gap, keyHeight), ToggleBridge);
            bridgeText.fontSize = 24f;
            statusText = Text(label, "", 15f, below - 46f, width - 30f);
            statusText.enableWordWrapping = true;
            statusText.rectTransform.sizeDelta = new Vector2(width - 30f, 15f * 1.4f * 2f);

            address = LanBootstrap.ReadHost() ?? "";
            Show();
        }

        private TextMeshProUGUI Text(TMP_Text style, string text, float size, float y, float width)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.layer = gameObject.layer;
            go.transform.SetParent(transform, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            if (style != null)
            {
                tmp.font = style.font;
                tmp.fontSharedMaterial = style.fontSharedMaterial;
                tmp.color = style.color;
            }
            tmp.fontSize = size;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.raycastTarget = false;
            tmp.text = text;
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(width, size * 1.4f);
            rect.anchoredPosition = new Vector2(0f, y);
            return tmp;
        }

        private TMP_Text Key(Button template, string text, Vector2 position, Vector2 size, UnityEngine.Events.UnityAction action)
        {
            var go = Instantiate(template.gameObject, transform, false);
            go.name = $"Key {text}";
            go.SetActive(true);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            // The copied highlight and label fill the key
            foreach (RectTransform child in rect)
            {
                child.anchorMin = Vector2.zero;
                child.anchorMax = Vector2.one;
                child.offsetMin = child.offsetMax = Vector2.zero;
            }
            var label = go.GetComponentInChildren<TMP_Text>(true);
            if (label != null)
            {
                label.text = text;
                label.enableAutoSizing = false;
                label.fontSize = 30f;
                label.alignment = TextAlignmentOptions.Center;
            }
            var button = go.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(action);
            return label;
        }

        private void ToggleBridge()
        {
            LanBootstrap.ViaBridge = !LanBootstrap.ViaBridge;
            Show();
        }

        private void Update()
        {
            var status = LanBootstrap.ViaBridge ? SteamBridgeTransport.Status : "";
            if (statusText != null && statusText.text != status) statusText.text = status;
        }

        private void Press(string key)
        {
            if (key == "<")
            {
                if (address.Length > 0) address = address.Substring(0, address.Length - 1);
            }
            else if (address.Length < MaxLength)
                address += key;
            LanBootstrap.SetHost(address);
            Show();
        }

        private void Show()
        {
            var bridge = LanBootstrap.ViaBridge;
            titleText.text = bridge ? "Steam bridge address" : "Host address";
            addressText.text = address.Length > 0 ? address : bridge ? "<size=60%><alpha=#80>type the bridge PC's IP" : "<size=60%><alpha=#80>type the host's IP";
            if (bridgeText != null) bridgeText.text = bridge ? "Via Steam bridge: On" : "Via Steam bridge: Off";
        }

        /// <summary>The headset's IPv4 addresses: Wi-Fi, and a VPN's (Tailscale's 100.x) when one is on</summary>
        private static string[] LocalAddresses()
        {
            try
            {
                var addresses = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus != OperationalStatus.Down && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a))
                    .Select(a => a.ToString())
                    .Distinct().ToArray();
                if (addresses.Length > 0) return addresses;
            }
            catch (System.Exception e)
            {
                Debug.Log($"[LCQuest] network interfaces: {e.Message}");
            }
            // The address the default route leaves from: a UDP socket's connect sends nothing
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect("8.8.8.8", 53);
                return new[] { ((System.Net.IPEndPoint)socket.LocalEndPoint).Address.ToString() };
            }
            catch (System.Exception e)
            {
                Debug.Log($"[LCQuest] no local address: {e.Message}");
                return new string[0];
            }
        }
    }
}
