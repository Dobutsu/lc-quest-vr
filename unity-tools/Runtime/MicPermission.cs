using System;
using System.IO;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.SceneManagement;

namespace LCQuest
{
    /// <summary>
    /// Asks for the microphone (voice chat) once per install, in place of Unity's own request at startup (switched off
    /// by Editor/SkipPermissionsManifest.cs). On Pico it asks in the main menu after the headset has shown the game
    /// steadily for SteadySeconds: in the REPO port, Unity's request, made while the OpenXR session was starting,
    /// crashed the game on Pico 4 when it got its focus back. Elsewhere (Quest) it asks right away, as Unity did.
    /// Not checked yet on Pico: whether Dissonance picks the microphone up after a late grant, or voice starts working
    /// only from the next launch. Once asked (files/mic-permission-asked), never again:
    /// the player can still allow it in the headset's settings. Log lines start with [MicPermission].
    /// </summary>
    public class MicPermission : MonoBehaviour
    {
        private const float SteadySeconds = 5f;

        private string askedPath;
        private bool inMenu;
        private float steady;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (Application.platform != RuntimePlatform.Android || Permission.HasUserAuthorizedPermission(Permission.Microphone))
                return;
            if (File.Exists(Path.Combine(Application.persistentDataPath, "mic-permission-asked")))
                return;
            var go = new GameObject("LCQuest Mic Permission");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<MicPermission>();
        }

        private void Awake()
        {
            askedPath = Path.Combine(Application.persistentDataPath, "mic-permission-asked");
            inMenu = IsPico();
        }

        private static bool IsPico()
        {
            try
            {
                using var build = new AndroidJavaClass("android.os.Build");
                return build.GetStatic<string>("BRAND").ToLowerInvariant().Contains("pico");
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void Update()
        {
            if (inMenu)
            {
                if (Application.isFocused && SceneManager.GetActiveScene().name == "MainMenu" && UnityEngine.XR.XRSettings.isDeviceActive)
                    steady += Time.unscaledDeltaTime;
                else
                    steady = 0;
                if (steady < SteadySeconds)
                    return;
            }

            try
            {
                File.WriteAllText(askedPath, DateTime.Now.ToString("u"));
            }
            catch (IOException) { }
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => Debug.Log("[MicPermission] granted");
            callbacks.PermissionDenied += _ => Debug.Log("[MicPermission] denied");
            callbacks.PermissionDeniedAndDontAskAgain += _ => Debug.Log("[MicPermission] denied, don't ask again");
            Debug.Log("[MicPermission] asking for the microphone");
            Permission.RequestUserPermission(Permission.Microphone, callbacks);
            Destroy(gameObject);
        }
    }
}
