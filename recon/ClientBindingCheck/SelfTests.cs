// 离线验证客户端版本检查器。
using System.Reflection;
using System.Text.Json;
using NutstoreMcp.ClientBindings;

namespace NutstoreMcp.ClientBindingCheck;

internal static class SelfTests
{
    public static int Run()
    {
        // Fixtures are inspected as bytes. None of their methods are invoked.
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var directory = Path.GetDirectoryName(assemblyPath)!;
        var file = Path.GetFileName(assemblyPath);
        ClientSymbol Symbol(Type type, string member = "Sample", string kind = "method") =>
            new("Fixture", file, type.FullName!, member, "离线元数据测试", "read", kind);
        ScanResult Scan(Type type, string member = "Sample", string kind = "method") =>
            MetadataScanner.Capture(directory, [Symbol(type, member, kind)]);
        var baseline = Scan(typeof(OriginalFixture));
        Assert(baseline.Issues.Count == 0, "原始 fixture 读取成功");
        Assert(baseline.Snapshot.Symbols.Single().Members.Count == 2, "保留全部同名重载");
        Assert(baseline.Snapshot.Symbols.Single().Members.All(x => x.Attributes.Contains("Public", StringComparison.Ordinal)
            && x.Attributes.Contains("Static", StringComparison.Ordinal) && x.IlSha256?.Length == 64), "读取可见性、static 和 IL 哈希");
        var roundTrip = JsonSerializer.Deserialize<BindingSnapshot>(JsonSerializer.Serialize(baseline.Snapshot, JsonFormat.Options), JsonFormat.Options)!;
        Assert(SnapshotComparer.Compare(roundTrip, baseline.Snapshot).Count == 0, "JSON 往返后无差异");
        var missing = Scan(typeof(MissingFixture));
        Assert(missing.Issues.Any(x => x.Code == "MissingMember"), "缺成员拒绝");
        Assert(SnapshotComparer.Compare(baseline.Snapshot, missing.Snapshot).Any(x => x.Code == "MemberSignatureChanged"), "缺重载差异可见");
        var changedSignature = Scan(typeof(SignatureFixture));
        Assert(SnapshotComparer.Compare(baseline.Snapshot, changedSignature.Snapshot).Any(x => x.Code == "MemberSignatureChanged"), "签名变化拒绝");
        var changedBody = Scan(typeof(BodyFixture));
        Assert(SnapshotComparer.Compare(baseline.Snapshot, changedBody.Snapshot).Any(x => x.Code == "MethodBodyChanged"), "签名不变而方法体变化拒绝");
        var changedVisibility = Scan(typeof(VisibilityFixture));
        Assert(SnapshotComparer.Compare(baseline.Snapshot, changedVisibility.Snapshot).Any(x => x.Code == "MemberAttributesChanged"), "可见性变化拒绝");
        var references = Scan(typeof(ReferenceFixture));
        var refSignature = references.Snapshot.Symbols.Single().Members.Single().Signature;
        Assert(refSignature.Contains("System.Boolean&", StringComparison.Ordinal)
            && refSignature.Contains("Out", StringComparison.Ordinal), "解析 ref/out 签名和参数属性");
        var enumeration = Scan(typeof(EnumFixture), string.Empty, "enum");
        Assert(enumeration.Issues.Count == 0 && enumeration.Snapshot.Symbols.Single().Members.Any(x => x.ConstantValue == "-1"), "读取负枚举值");
        var wrongExpectedValue = MetadataScanner.Capture(directory,
            [new ClientSymbol("ExpectedValue", file, typeof(EnumFixture).FullName!, "Value", "常量值验证", "read", "field", 42)]);
        Assert(wrongExpectedValue.Issues.Any(x => x.Code == "UnexpectedConstantValue"), "共享预期值错误时拒绝生成基线");
        var absentAssembly = MetadataScanner.Capture(directory,
            [new ClientSymbol("Fixture", "DoesNotExist.dll", typeof(OriginalFixture).FullName!, "Sample", "缺程序集验证", "read")]);
        Assert(absentAssembly.Issues.Any(x => x.Code == "MissingAssembly")
            && SnapshotComparer.Compare(baseline.Snapshot, absentAssembly.Snapshot).Any(x => x.Code == "AssemblySetChanged"), "缺程序集也输出比较差异");
        var alteredEnum = enumeration.Snapshot with
        {
            Symbols = [enumeration.Snapshot.Symbols.Single() with
            {
                Members = enumeration.Snapshot.Symbols.Single().Members.Select(x => x with { ConstantValue = "42" }).ToList(),
            }],
        };
        Assert(SnapshotComparer.Compare(enumeration.Snapshot, alteredEnum).Any(x => x.Code == "ConstantValueChanged"), "枚举变化拒绝");
        var property = Scan(typeof(PropertyFixture), "Value", "property");
        Assert(property.Snapshot.Symbols.Single().Members.Count == 3, "读取属性与 getter/setter 契约");
        var assemblyChange = baseline.Snapshot with
        {
            Assemblies = baseline.Snapshot.Assemblies.Select(x => x with { Sha256 = new string('0', 64) }).ToList(),
        };
        Assert(SnapshotComparer.Compare(baseline.Snapshot, assemblyChange).Any(x => x.Code == "AssemblyContentChanged"), "未登记依赖造成程序集变化仍拒绝");
        var tempPath = Path.Combine(AppContext.BaseDirectory, "NutstoreMcp-binding-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var output = new FileStream(tempPath, FileMode.CreateNew)) JsonSerializer.Serialize(output, baseline.Snapshot, JsonFormat.Options);
            var rejected = false;
            try { using var overwrite = new FileStream(tempPath, FileMode.CreateNew); }
            catch (IOException) { rejected = true; }
            Assert(rejected, "基线 CreateNew 拒绝覆盖");
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
        Console.WriteLine("ClientBindingCheck offline self-tests passed (metadata only; no fixture/client invocation).");
        return 0;
    }

    private static void Assert(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("离线验证失败：" + label);
    }

    private static class OriginalFixture
    {
        public static int Sample(int value) => value + 1;
        public static string Sample(string value) => value + "a";
    }
    private static class SignatureFixture
    {
        public static long Sample(int value) => value + 1L;
        public static string Sample(string value) => value + "a";
    }
    private static class BodyFixture
    {
        public static int Sample(int value) => value + 2;
        public static string Sample(string value) => value + "a";
    }
    private static class VisibilityFixture
    {
        private static int Sample(int value) => value + 1;
        public static string Sample(string value) => value + "a";
    }
    private static class MissingFixture { public static int Other(int value) => value + 1; }
    private static class ReferenceFixture
    {
        public static void Sample(ref bool value, out bool result) => result = value;
    }
    private enum EnumFixture { Undefined = -1, Value = 1 }
    private sealed class PropertyFixture { public int Value { get; set; } }
}
