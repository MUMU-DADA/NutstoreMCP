// 表示并比较客户端绑定指纹。
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NutstoreMcp.ClientBindingCheck;

internal static class JsonFormat
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

internal sealed record CheckIssue(string Code, string Subject, string Message);

internal sealed record CheckReport(bool Ok, string Mode, string ClientDirectory, string BaselinePath,
    List<CheckIssue> Issues, BindingSnapshot Current)
{
    public string Scope => "只读取 PE 元数据与 IL；指纹变化必须人工复核。指纹相同也不能证明服务器行为或未登记的依赖不变。";
}

internal sealed record BindingSnapshot(int SchemaVersion, string VerifiedClientVersion,
    List<AssemblyFingerprint> Assemblies, List<SymbolFingerprint> Symbols);

internal sealed record AssemblyFingerprint(string FileName, string Name, string Version,
    string Mvid, string Sha256);

internal sealed record SymbolFingerprint(string Key, string AssemblyFile, string TypeName,
    string MemberName, string Kind, string Purpose, string Effect, string TypeAttributes,
    string BaseType, List<MemberFingerprint> Members);

internal sealed record MemberFingerprint(string Name, string Kind, string Signature,
    string Attributes, string ImplementationAttributes, string? IlSha256, string? BodyStructureSha256,
    string? ConstantType, string? ConstantValue);

internal sealed record ScanResult(BindingSnapshot Snapshot, List<CheckIssue> Issues);

internal static class SnapshotComparer
{
    public static List<CheckIssue> Compare(BindingSnapshot baseline, BindingSnapshot current)
    {
        Validate(baseline, allowIncomplete: false);
        Validate(current, allowIncomplete: true);
        var issues = new List<CheckIssue>();
        void Add(string code, string subject, string message) => issues.Add(new(code, subject, message));
        if (baseline.VerifiedClientVersion != current.VerifiedClientVersion)
            Add("RegistryVersionChanged", "registry", "共享常量登记的验证版本与基线不同，请重新审查登记与基线。");
        var oldAssemblies = baseline.Assemblies.ToDictionary(x => x.FileName, StringComparer.Ordinal);
        var newAssemblies = current.Assemblies.ToDictionary(x => x.FileName, StringComparer.Ordinal);
        foreach (var name in oldAssemblies.Keys.Union(newAssemblies.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!oldAssemblies.TryGetValue(name, out var old) || !newAssemblies.TryGetValue(name, out var now))
            {
                Add("AssemblySetChanged", name, "登记的程序集集合变化或程序集缺失。");
                continue;
            }
            if (old.Name != now.Name) Add("AssemblyIdentityChanged", name, "程序集名称变化。");
            if (old.Version != now.Version) Add("AssemblyVersionChanged", name, $"程序集版本 {old.Version} → {now.Version}，需人工语义复核。");
            if (old.Mvid != now.Mvid) Add("AssemblyMvidChanged", name, "程序集 MVID 变化，需人工语义复核。");
            if (old.Sha256 != now.Sha256) Add("AssemblyContentChanged", name, "程序集 SHA256 变化，可能是未登记的依赖或实现变化，需人工语义复核。");
        }
        var oldSymbols = baseline.Symbols.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var newSymbols = current.Symbols.ToDictionary(x => x.Key, StringComparer.Ordinal);
        foreach (var key in oldSymbols.Keys.Union(newSymbols.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!oldSymbols.TryGetValue(key, out var old) || !newSymbols.TryGetValue(key, out var now))
            {
                Add("SymbolSetChanged", key, "登记符号变化或符号未找到。");
                continue;
            }
            if (old.AssemblyFile != now.AssemblyFile || old.TypeName != now.TypeName || old.MemberName != now.MemberName
                || old.Kind != now.Kind || old.Effect != now.Effect)
                Add("SymbolDefinitionChanged", key, "符号归属、名称、种类或副作用登记变化。");
            if (old.TypeAttributes != now.TypeAttributes || old.BaseType != now.BaseType)
                Add("TypeContractChanged", key, "声明类型的可见性、特征或基类型变化。");
            var oldMembers = old.Members.ToDictionary(MemberKey, StringComparer.Ordinal);
            var newMembers = now.Members.ToDictionary(MemberKey, StringComparer.Ordinal);
            foreach (var member in oldMembers.Keys.Union(newMembers.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (!oldMembers.TryGetValue(member, out var before) || !newMembers.TryGetValue(member, out var after))
                {
                    Add("MemberSignatureChanged", key, "成员/重载新增、缺失或完整签名变化：" + member);
                    continue;
                }
                if (before.Attributes != after.Attributes || before.ImplementationAttributes != after.ImplementationAttributes)
                    Add("MemberAttributesChanged", key, "成员可见性、static/ref 实现特征变化：" + member);
                if (before.IlSha256 != after.IlSha256 || before.BodyStructureSha256 != after.BodyStructureSha256)
                    Add("MethodBodyChanged", key, "成员 IL 或局部变量/异常处理结构变化；签名不变仍需人工语义复核：" + member);
                if (before.ConstantType != after.ConstantType || before.ConstantValue != after.ConstantValue)
                    Add("ConstantValueChanged", key, "枚举/常量的类型或值变化：" + member);
            }
        }
        return issues;
    }

    private static string MemberKey(MemberFingerprint member) => member.Kind + " " + member.Name + " " + member.Signature;

    private static void Validate(BindingSnapshot snapshot, bool allowIncomplete)
    {
        if (snapshot.SchemaVersion != 1) throw new InvalidDataException("不支持的基线 schemaVersion。");
        if (string.IsNullOrWhiteSpace(snapshot.VerifiedClientVersion) || snapshot.Assemblies is null || snapshot.Symbols is null
            || (!allowIncomplete && (snapshot.Assemblies.Count == 0 || snapshot.Symbols.Count == 0)))
            throw new InvalidDataException("基线缺少版本、程序集或登记符号。");
        if (snapshot.Assemblies.Any(x => x is null || string.IsNullOrWhiteSpace(x.FileName)
                || string.IsNullOrWhiteSpace(x.Sha256) || string.IsNullOrWhiteSpace(x.Mvid))
            || snapshot.Symbols.Any(x => x is null || string.IsNullOrWhiteSpace(x.Key) || x.Members is null))
            throw new InvalidDataException("基线包含不完整的程序集或符号记录。");
    }
}
