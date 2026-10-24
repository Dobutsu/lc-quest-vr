using System.Reflection;
using HarmonyLib;

namespace HarmonyBaker;

/// <summary>
/// Mirrors the mod's Source/Patches/HarmonyPatcher.cs: which patch classes get applied (and, for RepoXR, the XRRpc
/// prefixes). LCVR and RepoXR share the layout: [&lt;Mod&gt;Patch(target, dependency)] in &lt;Mod&gt;.Patches.
/// Kept in sync by hand; the names below are the contract with the mod.
/// </summary>
internal sealed class PatchSelection(Assembly mod)
{
    public enum Target { Universal = 0, VROnly = 1 }

    // Patches that target Harmony itself (a fix for old HarmonyX builds that the current one already contains)
    private static readonly HashSet<string> HarmonyInternalPatches = ["RepoXR.Patches.LeaveMyLeaveAlonePatch"];

    public List<string> Applied { get; } = [];
    public List<string> Skipped { get; } = [];
    public List<(string Name, Exception Error)> Failed { get; } = [];

    public void ApplyPatchClasses(Harmony harmony, IReadOnlySet<Target> targets)
    {
        var modName = mod.GetName().Name;
        var attributeType = mod.GetType($"{modName}.Patches.{modName}PatchAttribute", throwOnError: true)!;
        var targetProp = attributeType.GetProperty("Target")!;
        var dependencyProp = attributeType.GetProperty("Dependency")!;

        foreach (var type in AccessTools.GetTypesFromAssembly(mod).OrderBy(t => t.FullName))
        {
            var attribute = type.GetCustomAttributes(attributeType, false).FirstOrDefault();
            if (attribute is null)
                continue;

            var name = type.FullName!;
            var target = (Target)Convert.ToInt32(targetProp.GetValue(attribute));
            var dependency = (string?)dependencyProp.GetValue(attribute);

            if (HarmonyInternalPatches.Contains(name))
            {
                Skipped.Add($"{name} (patches HarmonyX internals)");
                continue;
            }

            // Compat patches only apply when another mod is present; the standalone build has no other mods
            if (dependency is not null)
            {
                Skipped.Add($"{name} (needs mod {dependency})");
                continue;
            }

            if (!targets.Contains(target))
            {
                Skipped.Add($"{name} ({target} not selected)");
                continue;
            }

            try
            {
                harmony.CreateClassProcessor(type, true).Patch();
                Applied.Add(name);
            }
            catch (Exception e)
            {
                Failed.Add((name, e));
            }
        }
    }

    /// <summary>HarmonyPatcher.ApplyNetworkRPCsForType: every [XRRpc] method gets the ExecuteRPCPrefix(Self) prefix</summary>
    public void ApplyNetworkRpcPrefixes(Harmony harmony)
    {
        // LCVR has no XRRpc: its VR networking rides on Dissonance messages, nothing to prefix
        var rpcAttribute = mod.GetType("RepoXR.Networking.XRRpcAttribute");
        if (rpcAttribute is null)
            return;

        var selfProp = rpcAttribute.GetProperty("Self")!;
        var patches = mod.GetType("RepoXR.Networking.NetworkingPatches", throwOnError: true)!;

        foreach (var type in AccessTools.GetTypesFromAssembly(mod))
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var attribute = method.GetCustomAttribute(rpcAttribute);
            if (attribute is null)
                continue;

            var prefixName = (bool)selfProp.GetValue(attribute)! ? "ExecuteRPCPrefixSelf" : "ExecuteRPCPrefix";
            var name = $"{type.FullName}::{method.Name} (XRRpc)";

            try
            {
                harmony.CreateProcessor(method).AddPrefix(new HarmonyMethod(AccessTools.Method(patches, prefixName))).Patch();
                Applied.Add(name);
            }
            catch (Exception e)
            {
                Failed.Add((name, e));
            }
        }
    }
}
