using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Which types and members of the target assemblies are used by the scanned assemblies, and from where.
//
//   RefScan --target <assembly name> [--target ...] --scan <dll> [--scan ...] [--json <out>]
//
// Counts IL operands (calls, field access, newobj, ldtoken/castclass/...), plus fields, parameters, locals, base types
// and attributes typed with a target type. Serialized scene/prefab references are not code and are not covered here.

var opts = new Dictionary<string, List<string>>();
for (var i = 0; i < args.Length - 1; i++)
    if (args[i].StartsWith("--"))
        (opts.TryGetValue(args[i][2..], out var l) ? l : opts[args[i][2..]] = []).Add(args[++i]);

var targets = opts["target"].ToHashSet();
var uses = new Dictionary<string, Dictionary<string, HashSet<string>>>(); // member -> scanned assembly -> users

foreach (var path in opts["scan"])
{
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
    using var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver });
    var asmName = asm.Name.Name;

    foreach (var type in asm.MainModule.GetTypes())
    {
        Type(type.BaseType, $"{type.FullName} (base)");
        foreach (var a in type.CustomAttributes) Type(a.AttributeType, $"{type.FullName} (attribute)");
        foreach (var f in type.Fields) Type(f.FieldType, $"{type.FullName}.{f.Name} (field)");

        foreach (var m in type.Methods)
        {
            var user = $"{type.FullName}::{m.Name}";
            Type(m.ReturnType, user);
            foreach (var p in m.Parameters) Type(p.ParameterType, user);
            if (!m.HasBody) continue;
            foreach (var v in m.Body.Variables) Type(v.VariableType, user);
            foreach (var ins in m.Body.Instructions)
            {
                switch (ins.Operand)
                {
                    case MethodReference mr when Target(mr.DeclaringType):
                        Add($"{Name(mr.DeclaringType)}::{mr.Name}", user);
                        break;
                    case FieldReference fr when Target(fr.DeclaringType):
                        Add($"{Name(fr.DeclaringType)}.{fr.Name}", user);
                        break;
                    case TypeReference tr:
                        Type(tr, user);
                        break;
                }
            }
        }
    }

    void Type(TypeReference? t, string user)
    {
        for (; t is TypeSpecification spec and not GenericInstanceType; t = spec.ElementType) { }
        if (t is GenericInstanceType git)
        {
            foreach (var arg in git.GenericArguments) Type(arg, user);
            t = git.ElementType;
        }
        if (t is not null && Target(t))
            Add($"{Name(t)} (type)", user);
    }

    void Add(string member, string user)
    {
        if (!uses.TryGetValue(member, out var byAsm)) uses[member] = byAsm = [];
        if (!byAsm.TryGetValue(asmName, out var users)) byAsm[asmName] = users = [];
        users.Add(user);
    }
}

foreach (var (member, byAsm) in uses.OrderBy(u => u.Key))
    Console.WriteLine($"{member}\t{string.Join(", ", byAsm.OrderBy(b => b.Key).Select(b => $"{b.Key}:{b.Value.Count}"))}");

if (opts.TryGetValue("json", out var json))
    File.WriteAllText(json[0], JsonSerializer.Serialize(
        uses.OrderBy(u => u.Key).ToDictionary(u => u.Key, u => u.Value.ToDictionary(b => b.Key, b => b.Value.Order().ToList())),
        new JsonSerializerOptions { WriteIndented = true }));
return 0;

bool Target(TypeReference t)
{
    while (t.IsNested) t = t.DeclaringType;
    var scope = t.Scope switch { AssemblyNameReference a => a.Name, ModuleDefinition m => m.Assembly.Name.Name, _ => null };
    return scope is not null && targets.Contains(scope);
}

static string Name(TypeReference t) => t is GenericInstanceType g ? g.ElementType.FullName : t.FullName;
