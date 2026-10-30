using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

// usage: DecompTool --list <dll>            -> type/method inventory to stdout
//        DecompTool --full <dll> <outdir>   -> one .cs file per type
string[] argv = Environment.GetCommandLineArgs();
if (argv.Length < 3)
{
    Console.Error.WriteLine("usage: DecompTool --list <dll> | --full <dll> <outdir>");
    return 1;
}

string mode = argv[1];
string dll = argv[2];

var settings = new DecompilerSettings(LanguageVersion.Latest)
{
    ThrowOnAssemblyResolveErrors = false,
    ShowDebugInfo = false,
};
var decompiler = new CSharpDecompiler(dll, settings);

static bool CompilerGenerated(IEntity e) =>
    e.GetAttributes().Any(a => a.AttributeType.FullName.Contains("CompilerGenerated"));

if (mode == "--list")
{
    var model = decompiler.TypeSystem;
    foreach (var t in model.GetTopLevelTypeDefinitions())
    {
        if (CompilerGenerated(t)) continue;
        Console.WriteLine($"{t.Kind} {t.FullName}");
        foreach (var m in t.GetMethods())
        {
            if (CompilerGenerated(m)) continue;
            Console.WriteLine($"    {m.ReturnType.FullName} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.Type.FullName + " " + p.Name))})");
        }
        foreach (var f in t.GetFields())
        {
            if (CompilerGenerated(f)) continue;
            Console.WriteLine($"    field {f.Type.FullName} {f.Name}");
        }
    }
    return 0;
}

if (mode == "--full")
{
    if (argv.Length < 4)
    {
        Console.Error.WriteLine("--full needs an output directory");
        return 1;
    }
    string outDir = argv[3];
    Directory.CreateDirectory(outDir);
    var model = decompiler.TypeSystem;
    int written = 0, failed = 0;
    foreach (var t in model.GetTopLevelTypeDefinitions())
    {
        var name = t.FullName.Replace('/', '.').Replace('<', '_').Replace('>', '_');
        try
        {
            var tree = decompiler.DecompileType(t.FullTypeName);
            File.WriteAllText(Path.Combine(outDir, name + ".cs"), tree.ToString());
            written++;
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(outDir, name + ".cs"), "// DECOMPILER ERROR: " + e.Message);
            failed++;
        }
    }
    Console.WriteLine($"types written={written} failed={failed} -> {outDir}");
    return 0;
}

Console.Error.WriteLine($"unknown mode {mode}");
return 1;
