using System.Reflection;
using HarmonyLib.Public.Patching;
using MonoMod.Utils;

namespace HarmonyBaker;

/// <summary>
/// A Harmony backend that never detours. Harmony builds the full patched body (prefixes, postfixes, finalizers,
/// transpilers) on the Cecil definition we hand out in <see cref="PrepareOriginal"/>; we keep that definition.
/// Harmony rebuilds from the original IL on every patch of the same method, so the last one holds all patches.
/// </summary>
internal sealed class BakingPatcher(MethodBase original) : MethodPatcher(original)
{
    private static readonly List<BakingPatcher> All = [];

    public DynamicMethodDefinition? Latest { get; private set; }

    /// <summary>False if Harmony failed after manipulating the body (e.g. while generating the dynamic method)</summary>
    public bool Completed { get; private set; }

    public static IReadOnlyList<BakingPatcher> Instances => All;

    public static void Install()
    {
        PatchManager.ClearAllPatcherResolvers();
        PatchManager.ResolvePatcher += (_, args) =>
        {
            var patcher = new BakingPatcher(args.Original);
            All.Add(patcher);
            args.MethodPatcher = patcher;
        };
    }

    /// <summary>
    /// Assemblies whose methods are hooked at call sites like native ones (--callsite-hooks): packages the target
    /// build compiles from source, where a baked body would be lost
    /// </summary>
    public static HashSet<string> CallSiteAssemblies { get; } = [];

    /// <summary>
    /// Engine methods implemented in native code (InternalCall/extern) have no IL to bake into. Harmony still
    /// records their patches; <see cref="NativeHooks"/> turns those into wrappers at every managed call site.
    /// </summary>
    public bool IsNative { get; } =
        CallSiteAssemblies.Contains(original.Module.Assembly.GetName().Name!) ||
        (original.MethodImplementationFlags & (MethodImplAttributes.InternalCall | MethodImplAttributes.Native)) != 0 ||
        (original.Attributes & MethodAttributes.PinvokeImpl) != 0 ||
        (!original.IsAbstract && original.GetMethodBody() is null);

    public override DynamicMethodDefinition PrepareOriginal()
    {
        Completed = false;
        return Latest = IsNative ? null! : new DynamicMethodDefinition(Original);
    }

    public override MethodBase DetourTo(MethodBase? replacement)
    {
        Completed = true;
        return replacement!;
    }

    public override DynamicMethodDefinition CopyOriginal() => new(Original);
}
