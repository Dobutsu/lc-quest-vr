using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// Strips an assembly down to what other code and the assets need, keeping its name, namespaces, type names and
// serialized fields, so precompiled code that calls it and scene/prefab data that references its scripts still bind.
//
//   ShimMaker --input <dll> --out <dll> --search <dir> [--search ...]
//             [--roots <file with full type names, one per line>] [--scan <dll> ...]
//             [--drop <assembly name> ...] [--stub-type <full type name> ...] [--drop-attribute <full name> ...]
//             [--report <file>]
//
// Kept: the root types (with all their serialized fields), every type and member the scanned assemblies reference,
// and what those need: base types, field and signature types, overrides of abstract members.
// A kept method keeps its body when the body touches no dropped assembly and no --stub-type; the members it calls are
// kept in turn. Every other kept method gets a stub body that returns default. Unity messages (Awake, OnEnable...)
// and [RuntimeInitializeOnLoadMethod] methods always get a stub: the shim must not run the original's systems.

var opts = new Dictionary<string, List<string>>();
for (var i = 0; i < args.Length - 1; i++)
    if (args[i].StartsWith("--"))
        (opts.TryGetValue(args[i][2..], out var l) ? l : opts[args[i][2..]] = []).Add(args[++i]);
List<string> Opt(string name) => opts.TryGetValue(name, out var v) ? v : [];

var resolver = new Resolver();
foreach (var dir in Opt("search")) resolver.AddSearchDirectory(dir);
var input = opts["input"][0];
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input))!);
var assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false });
var module = assembly.MainModule;
resolver.Register(assembly);
var targetName = assembly.Name.Name;
var dropped = Opt("drop").ToHashSet();
var stubTypes = Opt("stub-type").ToHashSet();
var droppedAttributes = Opt("drop-attribute").ToHashSet();

var types = new HashSet<TypeDefinition>();
var fields = new HashSet<FieldDefinition>();
var methods = new HashSet<MethodDefinition>();
var methodQueue = new Queue<MethodDefinition>();
var stubbed = new HashSet<MethodDefinition>();
// A stubbed property setter keeps the start of its body up to the first store into a field of its own type
// (`if (m_X == value) return; m_X = Mathf.Clamp(value, 0, 16);`), so the value it was given still reads back
var backingStores = new Dictionary<MethodDefinition, int>();
var unresolved = new SortedSet<string>();

string[] unityMessages =
[
    "Awake", "Start", "OnEnable", "OnDisable", "OnDestroy", "Update", "LateUpdate", "FixedUpdate", "OnValidate", "Reset",
    "OnDrawGizmos", "OnDrawGizmosSelected", "OnGUI", "OnApplicationQuit", "OnBecameVisible", "OnBecameInvisible",
    "OnPreCull", "OnPreRender", "OnPostRender", "OnRenderImage", "OnRenderObject", "OnWillRenderObject",
    "OnTransformParentChanged", "OnDidApplyAnimationProperties",
];

// --- roots ---------------------------------------------------------------------------------------------------------
foreach (var file in Opt("roots"))
    foreach (var line in File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
    {
        var type = module.GetType(line) ?? module.GetTypes().FirstOrDefault(t => t.FullName == line);
        if (type is null) { Console.Error.WriteLine($"root type not found: {line}"); continue; }
        MarkType(type);
    }

foreach (var path in Opt("scan"))
{
    using var scanned = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver });
    foreach (var type in scanned.MainModule.GetTypes())
    {
        MarkRef(type.BaseType);
        foreach (var i in type.Interfaces) MarkRef(i.InterfaceType);
        foreach (var a in type.CustomAttributes) MarkAttribute(a);
        foreach (var f in type.Fields) { MarkRef(f.FieldType); foreach (var a in f.CustomAttributes) MarkAttribute(a); }
        foreach (var p in type.Properties) MarkRef(p.PropertyType);
        foreach (var m in type.Methods)
        {
            MarkRef(m.ReturnType);
            foreach (var p in m.Parameters) MarkRef(p.ParameterType);
            foreach (var o in m.Overrides) MarkMember(o);
            if (!m.HasBody) continue;
            foreach (var v in m.Body.Variables) MarkRef(v.VariableType);
            foreach (var ins in m.Body.Instructions)
                switch (ins.Operand)
                {
                    case MemberReference member: MarkMember(member); break;
                }
        }
    }
}

// --- closure: bodies, overrides ------------------------------------------------------------------------------------
var changed = true;
while (changed)
{
    changed = false;
    while (methodQueue.TryDequeue(out var method))
    {
        if (!method.HasBody) continue;
        if (MustStub(method))
        {
            stubbed.Add(method);
            if (StorePrefix(method) is { } end)
            {
                backingStores[method] = end;
                for (var i = 0; i <= end; i++)
                    if (method.Body.Instructions[i].Operand is MemberReference member) MarkMember(member);
            }
            continue;
        }
        foreach (var v in method.Body.Variables) MarkRef(v.VariableType);
        foreach (var ins in method.Body.Instructions)
            if (ins.Operand is MemberReference member) MarkMember(member);
        foreach (var h in method.Body.ExceptionHandlers) MarkRef(h.CatchType);
    }

    // A kept type must implement every kept abstract member it inherits, and every interface member it declares
    foreach (var type in types.ToList())
    {
        if (type.IsInterface) continue;
        foreach (var method in type.Methods)
        {
            if (methods.Contains(method) || !method.IsVirtual) continue;
            if (Implements(method)) { MarkMethod(method); changed = true; }
        }
    }
    changed |= methodQueue.Count > 0;
}

// --- rewrite -------------------------------------------------------------------------------------------------------
foreach (var type in module.GetTypes().ToList())
{
    if (!types.Contains(type))
    {
        if (type.DeclaringType is { } outer) outer.NestedTypes.Remove(type); else module.Types.Remove(type);
        continue;
    }
    for (var i = type.Interfaces.Count - 1; i >= 0; i--)
        if (!Usable(type.Interfaces[i].InterfaceType)) type.Interfaces.RemoveAt(i);
    foreach (var f in type.Fields.Where(f => !fields.Contains(f)).ToList()) type.Fields.Remove(f);
    foreach (var p in type.Properties.ToList())
    {
        if (p.GetMethod is { } g && !methods.Contains(g)) p.GetMethod = null;
        if (p.SetMethod is { } s && !methods.Contains(s)) p.SetMethod = null;
        p.OtherMethods.Clear();
        if (p.GetMethod is null && p.SetMethod is null) type.Properties.Remove(p);
    }
    foreach (var e in type.Events.ToList())
        if (e.AddMethod is null || !methods.Contains(e.AddMethod) || e.RemoveMethod is null || !methods.Contains(e.RemoveMethod))
            type.Events.Remove(e);
    foreach (var m in type.Methods.Where(m => !methods.Contains(m)).ToList()) type.Methods.Remove(m);
    foreach (var m in type.Methods)
    {
        for (var i = m.Overrides.Count - 1; i >= 0; i--)
            if (!Usable(m.Overrides[i].DeclaringType) || m.Overrides[i].SafeResolve() is { } d && d.Module == module && !methods.Contains(d))
                m.Overrides.RemoveAt(i);
        if (stubbed.Contains(m)) Stub(m);
        CleanAttributes(m.CustomAttributes);
        foreach (var p in m.Parameters) CleanAttributes(p.CustomAttributes);
        CleanAttributes(m.MethodReturnType.CustomAttributes);
    }
    CleanAttributes(type.CustomAttributes);
    foreach (var f in type.Fields) CleanAttributes(f.CustomAttributes);
    foreach (var p in type.Properties) CleanAttributes(p.CustomAttributes);
    foreach (var e in type.Events) CleanAttributes(e.CustomAttributes);
}
CleanAttributes(assembly.CustomAttributes);
CleanAttributes(module.CustomAttributes);
module.Resources.Clear();
foreach (var r in module.AssemblyReferences.Where(r => dropped.Contains(r.Name)).ToList()) module.AssemblyReferences.Remove(r);

var report = new List<string>
{
    $"types {types.Count} (of {module.GetTypes().Count() + 0}), methods {methods.Count}, stubbed {stubbed.Count}, fields {fields.Count}",
};
report.AddRange(types.OrderBy(t => t.FullName).Select(t => "type " + t.FullName));
report.AddRange(stubbed.OrderBy(m => m.FullName).Select(m => "stub " + m.FullName));
report.AddRange(unresolved.Select(u => "unresolved " + u));
if (opts.TryGetValue("report", out var reportPath)) File.WriteAllLines(reportPath[0], report);
Console.WriteLine(report[0] + (unresolved.Count > 0 ? $", unresolved {unresolved.Count}" : ""));

assembly.Write(opts["out"][0]);

// IL2CPP refuses a type reference scoped to the wrong assembly ("Unresolved type reference : System.Void ... with
// scope UnityEngine.CoreModule"); Cecil writes such references when an import goes wrong, so check the output
using (var written = ModuleDefinition.ReadModule(opts["out"][0]))
{
    var corlib = new HashSet<string> { "netstandard", "mscorlib", "System", "System.Core", "System.Runtime" };
    var wrong = written.GetTypeReferences()
        .Where(t => t.Namespace == "System" && t.Scope is AssemblyNameReference a && !corlib.Contains(a.Name))
        .Select(t => $"{t.FullName} -> {t.Scope.Name}").Distinct().ToList();
    if (wrong.Count > 0)
    {
        Console.Error.WriteLine("type references scoped to the wrong assembly:\n  " + string.Join("\n  ", wrong));
        return 1;
    }
}
return 0;

// --- marking -------------------------------------------------------------------------------------------------------

bool InTarget(TypeReference t)
{
    while (t.IsNested) t = t.DeclaringType;
    return t.Scope switch
    {
        AssemblyNameReference a => a.Name == targetName,
        ModuleDefinition m => m == module || m.Assembly.Name.Name == targetName,
        _ => false,
    };
}

TypeDefinition? Local(TypeReference? t)
{
    if (t is null) return null;
    while (t is TypeSpecification spec) t = spec.ElementType;
    if (t is GenericParameter || !InTarget(t)) return null;
    if (t is TypeDefinition d && d.Module == module) return d;
    var found = module.GetType(t.FullName);
    if (found is null) unresolved.Add(t.FullName);
    return found;
}

static TypeReference DeclaringRoot(TypeReference t) { while (t.IsNested) t = t.DeclaringType; return t; }

void MarkRef(TypeReference? t)
{
    if (t is null) return;
    switch (t)
    {
        case GenericInstanceType git:
            foreach (var a in git.GenericArguments) MarkRef(a);
            MarkRef(git.ElementType);
            return;
        case TypeSpecification spec:
            MarkRef(spec.ElementType);
            return;
        case GenericParameter:
            return;
    }
    if (Local(t) is { } d) MarkType(d);
}

void MarkType(TypeDefinition type)
{
    if (!types.Add(type)) return;
    if (type.DeclaringType is { } outer) MarkType(outer);
    MarkRef(type.BaseType);
    foreach (var i in type.Interfaces) MarkRef(i.InterfaceType);
    foreach (var gp in type.GenericParameters) foreach (var c in gp.Constraints) MarkRef(c.ConstraintType);
    foreach (var a in type.CustomAttributes) MarkAttribute(a);
    // Data layout: everything Unity serializes, and every field of a value type (its size and layout)
    foreach (var f in type.Fields)
        if (type.IsValueType || type.IsEnum || IsSerialized(f) || f.IsLiteral) MarkField(f);
    if (type.IsEnum) return;
    // A delegate is its runtime-implemented .ctor/Invoke/BeginInvoke/EndInvoke
    if (type.BaseType?.FullName == "System.MulticastDelegate")
        foreach (var m in type.Methods) MarkMethod(m);
    // Unity and the serializer create instances through the parameterless constructor
    if (type.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0) is { } ctor) MarkMethod(ctor);
    if (type.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic) is { } cctor) MarkMethod(cctor);
    if (type.IsInterface) return;
    foreach (var m in type.Methods)
        if (m.IsVirtual && Implements(m)) MarkMethod(m);
}

bool IsSerialized(FieldDefinition f)
{
    if (f.IsStatic || f.IsInitOnly && !f.DeclaringType.IsValueType) return false;
    if (f.CustomAttributes.Any(a => a.AttributeType.Name is "NonSerializedAttribute")) return false;
    return f.IsPublic || f.CustomAttributes.Any(a => a.AttributeType.Name is "SerializeField" or "SerializeReference");
}

void MarkField(FieldDefinition f)
{
    if (!fields.Add(f)) return;
    MarkType(f.DeclaringType);
    MarkRef(f.FieldType);
    foreach (var a in f.CustomAttributes) MarkAttribute(a);
}

void MarkMethod(MethodDefinition m)
{
    if (!methods.Add(m)) return;
    MarkType(m.DeclaringType);
    MarkRef(m.ReturnType);
    foreach (var p in m.Parameters) MarkRef(p.ParameterType);
    foreach (var gp in m.GenericParameters) foreach (var c in gp.Constraints) MarkRef(c.ConstraintType);
    foreach (var o in m.Overrides) MarkMember(o);
    // An accessor keeps its property, and the property's other accessor (a property with one accessor reads oddly)
    foreach (var p in m.DeclaringType.Properties.Where(p => p.GetMethod == m || p.SetMethod == m))
    {
        MarkRef(p.PropertyType);
        if (p.GetMethod is { } g) MarkMethod(g);
        if (p.SetMethod is { } s) MarkMethod(s);
    }
    foreach (var e in m.DeclaringType.Events.Where(e => e.AddMethod == m || e.RemoveMethod == m))
    {
        MarkRef(e.EventType);
        if (e.AddMethod is { } a) MarkMethod(a);
        if (e.RemoveMethod is { } r) MarkMethod(r);
    }
    methodQueue.Enqueue(m);
}

void MarkMember(MemberReference member)
{
    switch (member)
    {
        case TypeReference t:
            MarkRef(t);
            break;
        case GenericInstanceMethod gim:
            foreach (var a in gim.GenericArguments) MarkRef(a);
            MarkMember(gim.ElementMethod);
            break;
        case MethodReference mr:
            MarkRef(mr.DeclaringType);
            if (Local(mr.DeclaringType) is not null)
            {
                if (mr.SafeResolve() is { } md && md.Module == module) MarkMethod(md);
                else unresolved.Add(mr.FullName);
            }
            break;
        case FieldReference fr:
            MarkRef(fr.DeclaringType);
            if (Local(fr.DeclaringType) is not null)
            {
                if (fr.SafeResolve() is { } fd && fd.Module == module) MarkField(fd);
                else unresolved.Add(fr.FullName);
            }
            break;
    }
}

void MarkAttribute(CustomAttribute a)
{
    if (!AttributeUsable(a, mark: false)) return;
    MarkMember(a.Constructor);
    foreach (var arg in a.ConstructorArguments.Concat(a.Properties.Select(p => p.Argument)).Concat(a.Fields.Select(f => f.Argument)))
        if (arg.Value is TypeReference tr) MarkRef(tr);
}

// Overrides an abstract member it must implement, or implements an interface member
bool Implements(MethodDefinition m)
{
    if (m.HasOverrides) return m.Overrides.Any(o => o.SafeResolve() is { } d && (d.Module != module || methods.Contains(d)));
    if (m.IsNewSlot && !m.DeclaringType.Interfaces.Any()) return false;
    for (var baseRef = m.DeclaringType.BaseType; baseRef is not null;)
    {
        var baseType = baseRef.SafeResolve();
        if (baseType is null) break;
        var match = baseType.Methods.FirstOrDefault(b => b.IsVirtual && b.Name == m.Name && b.Parameters.Count == m.Parameters.Count);
        if (match is not null)
            return match.Module != module ? match.IsAbstract || IsInterfaceImpl(m) : methods.Contains(match);
        baseRef = baseType.BaseType;
    }
    return IsInterfaceImpl(m);
}

bool IsInterfaceImpl(MethodDefinition m) =>
    m.DeclaringType.Interfaces.Select(i => i.InterfaceType.SafeResolve()).Where(i => i is not null)
        .Any(i => i!.Methods.Any(im => im.Name == m.Name && im.Parameters.Count == m.Parameters.Count && (i.Module != module || methods.Contains(im))));

// --- bodies --------------------------------------------------------------------------------------------------------

bool MustStub(MethodDefinition m)
{
    var owner = m.DeclaringType;
    if (!m.IsStatic && !m.IsConstructor && unityMessages.Contains(m.Name) && m.Parameters.Count == 0 && IsUnityObject(owner)) return true;
    if (m.CustomAttributes.Any(a => a.AttributeType.Name is "RuntimeInitializeOnLoadMethodAttribute" or "InitializeOnLoadMethodAttribute")) return true;
    if (m.IsPInvokeImpl) return true;
    foreach (var v in m.Body.Variables) if (Blocked(v.VariableType)) return true;
    foreach (var ins in m.Body.Instructions)
        switch (ins.Operand)
        {
            case TypeReference t when Blocked(t): return true;
            case MethodReference mr when Blocked(mr.DeclaringType) || Blocked(mr.ReturnType) || mr.Parameters.Any(p => Blocked(p.ParameterType)):
                return true;
            case GenericInstanceMethod gim when gim.GenericArguments.Any(Blocked): return true;
            case FieldReference fr when Blocked(fr.DeclaringType) || Blocked(fr.FieldType): return true;
        }
    return false;
}

int? StorePrefix(MethodDefinition m)
{
    if (m.IsStatic || !m.IsSpecialName || !m.Name.StartsWith("set_") || m.Body.HasExceptionHandlers) return null;
    var code = m.Body.Instructions;
    for (var i = 0; i < code.Count; i++)
    {
        var ins = code[i];
        if (ins.OpCode == OpCodes.Stfld && ins.Operand is FieldDefinition f && f.DeclaringType == m.DeclaringType) return i;
        switch (ins.Operand)
        {
            case TypeReference t when Blocked(t): return null;
            case MethodReference mr when Blocked(mr.DeclaringType) || Blocked(mr.ReturnType) || mr.Parameters.Any(p => Blocked(p.ParameterType)):
                return null;
            case FieldReference fr when Blocked(fr.DeclaringType) || Blocked(fr.FieldType): return null;
        }
    }
    return null;
}

bool Blocked(TypeReference? t)
{
    if (t is null) return false;
    if (t is GenericInstanceType git && git.GenericArguments.Any(Blocked)) return true;
    while (t is TypeSpecification spec) { t = spec.ElementType; if (t is GenericInstanceType g2 && g2.GenericArguments.Any(Blocked)) return true; }
    if (t is GenericParameter) return false;
    var root = DeclaringRoot(t);
    var scope = root.Scope switch { AssemblyNameReference a => a.Name, ModuleDefinition md => md.Assembly.Name.Name, _ => "" };
    if (dropped.Contains(scope)) return true;
    for (var x = t; x is not null; x = x.DeclaringType)
        if (stubTypes.Contains(x.FullName)) return true;
    return false;
}

bool IsUnityObject(TypeDefinition t)
{
    for (TypeReference? b = t.BaseType; b is not null;)
    {
        if (b.FullName is "UnityEngine.MonoBehaviour" or "UnityEngine.ScriptableObject") return true;
        b = b.SafeResolve()?.BaseType;
    }
    return false;
}

void Stub(MethodDefinition m)
{
    if (m.IsPInvokeImpl)
    {
        // Cecil's PInvokeInfo setter turns the flag back on: clear the info first
        m.PInvokeInfo = null;
        m.IsPInvokeImpl = false;
    }
    m.ImplAttributes &= ~(MethodImplAttributes.InternalCall | MethodImplAttributes.PreserveSig);
    var body = new MethodBody(m);
    var il = body.GetILProcessor();
    if (m.IsConstructor && !m.IsStatic && !m.DeclaringType.IsValueType && m.DeclaringType.BaseType is { } baseRef)
    {
        var baseType = baseRef.SafeResolve();
        var baseCtor = baseType?.Methods.FirstOrDefault(c => c.IsConstructor && !c.IsStatic && c.Parameters.Count == 0);
        if (baseCtor is not null)
        {
            il.Emit(OpCodes.Ldarg_0);
            // Built by hand: importing the resolved definition can scope System.Void to the base type's module
            il.Emit(OpCodes.Call, baseCtor.Module == module
                ? baseCtor
                : new MethodReference(".ctor", module.TypeSystem.Void, baseRef) { HasThis = true });
        }
    }
    if (backingStores.TryGetValue(m, out var end))
    {
        var old = m.Body;
        foreach (var v in old.Variables) body.Variables.Add(v);
        body.InitLocals = old.InitLocals;
        var ret0 = Instruction.Create(OpCodes.Ret);
        var copies = old.Instructions.Take(end + 1).Select(i => Instruction.Create(OpCodes.Nop)).ToList();
        var map = old.Instructions.Take(end + 1).Select((ins, n) => (ins, n)).ToDictionary(x => x.ins, x => copies[x.n]);
        for (var n = 0; n <= end; n++)
        {
            var ins = old.Instructions[n];
            copies[n].OpCode = ins.OpCode;
            copies[n].Operand = ins.Operand switch
            {
                Instruction target => map.GetValueOrDefault(target, ret0),
                Instruction[] targets => targets.Select(t => map.GetValueOrDefault(t, ret0)).ToArray(),
                var other => other,
            };
            il.Append(copies[n]);
        }
        il.Append(ret0);
        m.Body = body;
        return;
    }
    var ret = m.ReturnType;
    if (ret.MetadataType == MetadataType.Void) { }
    else if (ret.IsByReference || ret.IsPointer && false) { il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Throw); m.Body = body; return; }
    else if (ret.IsValueType || ret is GenericParameter)
    {
        var local = new VariableDefinition(ret);
        body.Variables.Add(local);
        body.InitLocals = true;
        il.Emit(OpCodes.Ldloca_S, local);
        il.Emit(OpCodes.Initobj, ret);
        il.Emit(OpCodes.Ldloc, local);
    }
    else il.Emit(OpCodes.Ldnull);
    il.Emit(OpCodes.Ret);
    m.Body = body;
}

// --- cleanup -------------------------------------------------------------------------------------------------------

bool Usable(TypeReference? t)
{
    if (t is null) return true;
    if (t is GenericInstanceType git) return Usable(git.ElementType) && git.GenericArguments.All(Usable);
    if (t is TypeSpecification spec) return Usable(spec.ElementType);
    if (t is GenericParameter) return true;
    var scope = DeclaringRoot(t).Scope switch { AssemblyNameReference a => a.Name, ModuleDefinition md => md.Assembly.Name.Name, _ => "" };
    if (dropped.Contains(scope)) return false;
    return !InTarget(t) || Local(t) is { } d && types.Contains(d);
}

bool AttributeUsable(CustomAttribute a, bool mark)
{
    try { return AttributeUsableCore(a, mark); }
    catch (Exception e) when (e is NullReferenceException or AssemblyResolutionException) { return false; }
}

bool AttributeUsableCore(CustomAttribute a, bool mark)
{
    if (a.AttributeType is null || a.Constructor is null) return false;
    if (droppedAttributes.Contains(a.AttributeType.FullName)) return false;
    if (!Usable(a.AttributeType)) return false;
    if (a.Constructor.SafeResolve() is { } c && c.Module == module && !(mark ? methods.Contains(c) : true)) return false;
    foreach (var arg in a.ConstructorArguments.Concat(a.Properties.Select(p => p.Argument)).Concat(a.Fields.Select(f => f.Argument)))
    {
        if (arg.Value is TypeReference tr && !(mark ? Usable(tr) : !Blocked(tr))) return false;
        if (!Usable(arg.Type)) return false;
    }
    return !a.AttributeType.Name.StartsWith("RuntimeInitializeOnLoadMethod");
}

void CleanAttributes(Mono.Collections.Generic.Collection<CustomAttribute> attributes)
{
    for (var i = attributes.Count - 1; i >= 0; i--)
        if (!AttributeUsable(attributes[i], mark: true)) attributes.RemoveAt(i);
}

// Resolves the target assembly to the instance being rewritten, so references from scanned assemblies land on it
sealed class Resolver : DefaultAssemblyResolver
{
    public void Register(AssemblyDefinition assembly) => RegisterAssembly(assembly);
}

static class Safe
{
    public static TypeDefinition? SafeResolve(this TypeReference t) { try { return t.Resolve(); } catch (AssemblyResolutionException) { return null; } }
    public static MethodDefinition? SafeResolve(this MethodReference m) { try { return m.Resolve(); } catch (AssemblyResolutionException) { return null; } }
    public static FieldDefinition? SafeResolve(this FieldReference f) { try { return f.Resolve(); } catch (AssemblyResolutionException) { return null; } }
}
