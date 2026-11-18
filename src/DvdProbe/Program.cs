// DvdProbe: command-line tool for probing DVDs and testing ideas while building the ripper.
// Commands that are no longer needed are marked [Obsolete] and labeled "old" in the help,
// but are kept so the history of the investigation stays visible.

using System.Reflection;

namespace DvdProbe;

public static class Program
{
    public static int Main(string[] args)
    {
        var commands = typeof(Commands)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<ProbeCommandAttribute>()))
            .Where(x => x.Attr != null)
            .ToDictionary(x => x.Attr!.Name, x => x, StringComparer.OrdinalIgnoreCase);

        if (args.Length == 0 || !commands.TryGetValue(args[0], out var cmd))
        {
            Console.WriteLine("usage: DvdProbe <command> [args]\n");
            foreach (var (name, c) in commands.OrderBy(k => k.Key))
            {
                bool old = c.Method.GetCustomAttribute<ObsoleteAttribute>() != null;
                Console.WriteLine($"  {name,-12} {(old ? "(old) " : "")}{c.Attr!.Help}");
            }
            return args.Length == 0 ? 0 : 1;
        }

        return (int)cmd.Method.Invoke(null, [args[1..]])!;
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class ProbeCommandAttribute(string name, string help) : Attribute
{
    public string Name { get; } = name;
    public string Help { get; } = help;
}

public static class Commands
{
    [ProbeCommand("drives", "List optical drives and whether a disc is loaded.")]
    public static int Drives(string[] args)
    {
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom))
        {
            string label = d.IsReady ? d.VolumeLabel : "(no disc)";
            Console.WriteLine($"{d.Name}  {label}");
        }
        return 0;
    }

    [ProbeCommand("files", "List the VIDEO_TS files on a disc. Args: <drive letter>")]
    public static int Files(string[] args)
    {
        string root = args.Length > 0 ? args[0].TrimEnd(':', '\\') + ":\\" : "E:\\";
        var dir = new DirectoryInfo(Path.Combine(root, "VIDEO_TS"));
        if (!dir.Exists) { Console.Error.WriteLine($"No VIDEO_TS folder on {root}"); return 1; }
        foreach (var f in dir.GetFiles().OrderBy(f => f.Name))
            Console.WriteLine($"{f.Name,-14} {f.Length,14:N0}");
        return 0;
    }

    [ProbeCommand("titles", "Parse the IFO files and list titles like MakeMKV. Args: <drive or VIDEO_TS path> [-v]")]
    public static int Titles(string[] args)
    {
        string videoTs = IfoProbe.ResolveVideoTs(args.FirstOrDefault(a => !a.StartsWith('-')) ?? "E:");
        bool verbose = args.Contains("-v");
        IfoProbe.PrintTitles(videoTs, verbose);
        return 0;
    }

    [ProbeCommand("scramble", "Sample sectors of VOB files and count CSS-scrambled ones. Args: <vob path>... [-n samples]")]
    public static int Scramble(string[] args)
    {
        int n = 200;
        int ni = Array.IndexOf(args, "-n");
        if (ni >= 0) n = int.Parse(args[ni + 1]);
        foreach (var path in args.Where((a, i) => !a.StartsWith('-') && (ni < 0 || i != ni + 1)))
            SectorProbe.Scan(path, n);
        return 0;
    }

    [ProbeCommand("crack", "Recover CSS title keys from VOB data and verify them. Args: <drive> [vts numbers...]")]
    public static int Crack(string[] args)
    {
        string videoTs = IfoProbe.ResolveVideoTs(args.Length > 0 ? args[0] : "E:");
        var titleSets = args.Skip(1).Select(int.Parse).ToList();
        if (titleSets.Count == 0)
            titleSets = Enumerable.Range(1, 99).TakeWhile(n => File.Exists(Path.Combine(videoTs, $"VTS_{n:00}_1.VOB"))).ToList();
        foreach (int vts in titleSets)
        {
            var key = CssProbe.CrackTitleSet(videoTs, vts, maxSectors: 200_000, votesNeeded: 5, print: true);
            if (key is not null) CssProbe.Verify(videoTs, vts, key.Value, samples: 50);
        }
        return 0;
    }
}
