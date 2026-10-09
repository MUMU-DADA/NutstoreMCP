// 检查客户端绑定与版本基线的差异。
using System.Text.Json;
using NutstoreMcp.ClientBindings;
using NutstoreMcp.ClientBindingCheck;

return Run(args);

static int Run(string[] arguments)
{
    try
    {
        var options = Options.Parse(arguments);
        if (options.Help)
        {
            Console.WriteLine("ClientBindingCheck [--client-dir <dir>] [--baseline <json>] [--report <json>] [--record]");
            Console.WriteLine("ClientBindingCheck --self-test");
            Console.WriteLine("默认仅比较 PE 元数据与 IL 指纹，不加载或执行客户端。--record 仅新建基线，拒绝覆盖。退出码：0一致，1需复核，2输入/读取失败。");
            return 0;
        }
        if (options.SelfTest) return SelfTests.Run();

        var clientDirectory = Path.GetFullPath(options.ClientDirectory ?? FindClientDirectory());
        var baselinePath = Path.GetFullPath(options.Baseline ?? FindDefaultBaseline());
        if (options.Report is not null)
        {
            var reportPath = Path.GetFullPath(options.Report);
            if (string.Equals(reportPath, baselinePath, StringComparison.OrdinalIgnoreCase)
                || reportPath.StartsWith(clientDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("报告不能覆盖基线或写入待检查的客户端目录。");
        }
        var scan = MetadataScanner.Capture(clientDirectory, ClientSymbols.Definitions);
        var core = scan.Snapshot.Assemblies.FirstOrDefault(x => x.FileName == ClientSymbols.CloudOpsAssembly);
        if (core is not null && core.Version != ClientSymbols.VerifiedClientVersion)
            scan.Issues.Add(new("UnreviewedClientVersion", core.FileName,
                $"客户端版本 {core.Version} 与共享登记的验证版本 {ClientSymbols.VerifiedClientVersion} 不同，必须先人工复核。"));
        if (options.Record)
        {
            if (scan.Issues.Count != 0)
            {
                Emit(new CheckReport(false, "record", clientDirectory, baselinePath, scan.Issues, scan.Snapshot), options.Report);
                return 1;
            }
            using (var output = new FileStream(baselinePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(output, scan.Snapshot, JsonFormat.Options);
            Emit(new CheckReport(true, "record", clientDirectory, baselinePath, [], scan.Snapshot), options.Report);
            return 0;
        }

        using var input = File.OpenRead(baselinePath);
        var baseline = JsonSerializer.Deserialize<BindingSnapshot>(input, JsonFormat.Options)
            ?? throw new InvalidDataException("基线不是有效 JSON 对象。");
        var changes = SnapshotComparer.Compare(baseline, scan.Snapshot);
        changes.InsertRange(0, scan.Issues);
        Emit(new CheckReport(changes.Count == 0, "compare", clientDirectory, baselinePath, changes, scan.Snapshot), options.Report);
        return changes.Count == 0 ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.GetType().Name, reason = ex.Message }, JsonFormat.Options));
        return 2;
    }
}

static void Emit(CheckReport report, string? reportPath)
{
    var text = JsonSerializer.Serialize(report, JsonFormat.Options);
    if (reportPath is not null) File.WriteAllText(Path.GetFullPath(reportPath), text + Environment.NewLine);
    Console.WriteLine(text);
}

static string FindClientDirectory()
{
    const string root = @"C:\Program Files\Nutstore";
    if (!Directory.Exists(root)) throw new DirectoryNotFoundException("找不到客户端目录，请指定 --client-dir。");
    return Directory.GetDirectories(root, "bin-*")
        .Where(path => File.Exists(Path.Combine(path, "NutstoreClient.exe")))
        .OrderByDescending(path => Version.TryParse(Path.GetFileName(path).Substring(4), out var version) ? version : new Version(0, 0))
        .FirstOrDefault() ?? throw new DirectoryNotFoundException("找不到含 NutstoreClient.exe 的客户端目录，请指定 --client-dir。");
}

static string FindDefaultBaseline()
{
    foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            var bindings = Path.Combine(directory.FullName, "src", "NutstoreMcp.ClientBindings");
            if (Directory.Exists(bindings)) return Path.Combine(bindings, ClientSymbols.BaselineFileName);
        }
    throw new FileNotFoundException("找不到共享基线目录，请指定 --baseline。");
}

internal sealed class Options
{
    public string? ClientDirectory { get; private set; }
    public string? Baseline { get; private set; }
    public string? Report { get; private set; }
    public bool Record { get; private set; }
    public bool SelfTest { get; private set; }
    public bool Help { get; private set; }

    public static Options Parse(string[] arguments)
    {
        var result = new Options();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < arguments.Length; i++)
        {
            var name = arguments[i];
            if (!seen.Add(name)) throw new ArgumentException("重复参数：" + name);
            string Value() => ++i < arguments.Length && !arguments[i].StartsWith("--", StringComparison.Ordinal)
                ? arguments[i] : throw new ArgumentException("参数缺少值：" + name);
            switch (name)
            {
                case "--client-dir": result.ClientDirectory = Value(); break;
                case "--baseline": result.Baseline = Value(); break;
                case "--report": result.Report = Value(); break;
                case "--record": result.Record = true; break;
                case "--self-test": result.SelfTest = true; break;
                case "--help": case "-h": result.Help = true; break;
                default: throw new ArgumentException("未知参数：" + name);
            }
        }
        if (result.SelfTest && seen.Count > 1) throw new ArgumentException("--self-test 不能与其他参数合用。");
        return result;
    }
}
