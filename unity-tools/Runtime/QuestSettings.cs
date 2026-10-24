using System.Linq;
using System.Reflection;
using UnityEngine;

namespace LCQuest
{
    /// <summary>
    /// The port's own options in LCVR's settings menu: LCVR builds that menu from every entry of its BepInEx config
    /// file (LCVR.Plugin.Config.File), so an entry bound there shows up with its description and is saved with LCVR's.
    /// LCVR is not referenced at compile time: found by reflection once its plugin has started.
    ///   Performance / ShorterFacilityLights: the facility's lamps light half as far (HdrpBridge.FacilityLightRange)
    /// </summary>
    public class QuestSettings : MonoBehaviour
    {
        private object shorterLights;
        private PropertyInfo value;
        private float next;
        private bool? appliedShorter;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var go = new GameObject("LCQuest Settings");
            DontDestroyOnLoad(go);
            go.AddComponent<QuestSettings>();
        }

        private void Update()
        {
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + 1f;
            if (shorterLights == null && !Bind()) return;
            // Only when the option changes: QuestTuning's static knob stays as set for measurements
            var shorter = (bool)value.GetValue(shorterLights);
            if (shorter == appliedShorter) return;
            appliedShorter = shorter;
            HdrpBridge.FacilityLightRange = shorter ? 0.5f : 1f;
        }

        private bool Bind()
        {
            var config = System.Type.GetType("LCVR.Plugin, LCVR")?.GetProperty("Config", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            var file = config?.GetType().GetProperty("File")?.GetValue(config);
            if (file == null) return false;
            // ConfigFile.Bind<T>(string section, string key, T defaultValue, string description)
            var bind = file.GetType().GetMethods().FirstOrDefault(m => m.Name == "Bind" && m.IsGenericMethodDefinition &&
                                                                       m.GetParameters().Length == 4 &&
                                                                       m.GetParameters()[3].ParameterType == typeof(string));
            if (bind == null) { enabled = false; Debug.Log("[QuestSettings] no ConfigFile.Bind"); return false; }
            shorterLights = bind.MakeGenericMethod(typeof(bool)).Invoke(file, new object[]
            {
                "Performance", "ShorterFacilityLights", true,
                "The lamps inside the facility light half as far. Faster in big rooms (about 5 ms a frame in the factory's " +
                "halls), darker between the lamps."
            });
            value = shorterLights.GetType().GetProperty("Value");
            Debug.Log($"[QuestSettings] ShorterFacilityLights = {value.GetValue(shorterLights)}");
            return true;
        }
    }
}
