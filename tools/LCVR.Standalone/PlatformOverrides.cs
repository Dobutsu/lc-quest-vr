using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Steamworks;

namespace LCVR.Standalone
{
    /// <summary>
    /// Marks a method whose body is copied over an LCVR (or game, or library) method when the patches are baked
    /// (HarmonyBaker --overrides). Same parameters, plus a leading instance parameter for instance targets.
    /// LCVR's own Android changes are source patches (patches/lcvr); this is for code we do not build ourselves.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ReplacesAttribute : Attribute
    {
        public ReplacesAttribute(string type, string method, string platform = "any")
        {
            Type = type;
            Method = method;
            Platform = platform;
        }

        public string Type { get; }
        public string Method { get; }

        /// <summary>Assembly holding the target (default: LCVR). The game is "Assembly-CSharp" at bake time.</summary>
        public string Assembly { get; set; } = "LCVR";

        /// <summary>"any", or the bake --platform this applies to ("android")</summary>
        public string Platform { get; }
    }

    public static class PlatformOverrides
    {
        /// <summary>
        /// HarmonyX compiles a fast invoker with DynamicMethodDefinition (Reflection.Emit), which IL2CPP cannot do.
        /// AccessTools' static constructor builds one, so without this every AccessTools call at runtime fails with a
        /// TypeInitializationException. Reflection invoke instead.
        /// </summary>
        [Replaces("HarmonyLib.MethodInvoker", "GetHandler", "android", Assembly = "0Harmony")]
        public static FastInvokeHandler GetHandler(MethodInfo methodInfo, bool directBoxValueAccess)
        {
            // A direct method-group delegate (ldftn MethodBase.Invoke), so IL2CPP's linker keeps what it calls
            return methodInfo.Invoke;
        }

        /// <summary>
        /// The game's launch screen (PreInitSceneScript.Awake) checks Dissonance's native libraries. AudioPluginDissonance
        /// (capture preprocessing: noise suppression, echo cancellation) exists only for Windows, so the check threw,
        /// Unity skipped the script's Start, and LCVR's VR launch scene (a postfix on Start) never loaded. libopus is
        /// in the APK (scripts/build-opus-android.sh); the preprocessor is left for the voice work.
        /// </summary>
        [Replaces("Dissonance.DissonanceComms", "TestDependencies", "android", Assembly = "DissonanceVoip")]
        public static void TestDissonanceDependencies()
        {
        }

        /// <summary>
        /// An Online host names its players by Steam id (PlayerControllerB.SendNewPlayerValuesClientRpc reads
        /// Friend.Name); with no Steam client the interface is null and the RPC threw, before it started voice tracking.
        /// Joined through the Steam bridge, the names come from the bridge: LCQuest.SteamBridgeTransport keeps them as
        /// AppDomain data, since this body is copied into Facepunch.Steamworks, which cannot reference our scripts.
        /// </summary>
        [Replaces("Steamworks.Friend", "get_Name", "android", Assembly = "Facepunch.Steamworks.Win64")]
        public static string FriendName(ref Friend friend)
        {
            return AppDomain.CurrentDomain.GetData("LCQuest.SteamNames") is Dictionary<ulong, string> names &&
                   names.TryGetValue(friend.Id.Value, out var name) ? name : "Player";
        }
    }
}
