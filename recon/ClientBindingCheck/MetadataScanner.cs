// 读取客户端元数据与方法体指纹。
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NutstoreMcp.ClientBindings;

namespace NutstoreMcp.ClientBindingCheck;

internal static class MetadataScanner
{
    public static ScanResult Capture(string clientDirectory, IEnumerable<ClientSymbol> definitions)
    {
        var issues = new List<CheckIssue>();
        var assemblies = new List<AssemblyFingerprint>();
        var symbols = new List<SymbolFingerprint>();
        var registered = definitions.ToList();
        if (registered.Count == 0 || registered.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != registered.Count)
            throw new InvalidDataException("共享常量登记为空或 Key 重复。");
        foreach (var group in registered.GroupBy(x => x.AssemblyFile, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (Path.GetFileName(group.Key) != group.Key)
                throw new InvalidDataException("程序集登记必须是文件名：" + group.Key);
            var path = Path.Combine(clientDirectory, group.Key);
            if (!File.Exists(path))
            {
                issues.Add(new("MissingAssembly", group.Key, "找不到登记的程序集。"));
                continue;
            }
            // One byte snapshot for both hashes and metadata, so an update cannot mix two builds.
            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes, writable: false);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) throw new BadImageFormatException("程序集没有托管元数据：" + group.Key);
            var reader = pe.GetMetadataReader();
            var assembly = reader.GetAssemblyDefinition();
            var name = reader.GetString(assembly.Name);
            assemblies.Add(new(group.Key, name, assembly.Version.ToString(),
                reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString("D"), Hash(bytes)));
            var names = new SignatureNames(name);
            var types = reader.TypeDefinitions.ToDictionary(handle => SignatureNames.DefinitionName(reader, handle),
                handle => handle, StringComparer.Ordinal);
            foreach (var symbol in group.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (!types.TryGetValue(symbol.TypeName, out var typeHandle))
                {
                    issues.Add(new("MissingType", symbol.Key, "找不到登记的类型：" + symbol.TypeName));
                    continue;
                }
                var type = reader.GetTypeDefinition(typeHandle);
                var members = CaptureMembers(pe, reader, names, type, symbol);
                if (symbol.Kind != "type" && members.Count == 0)
                    issues.Add(new("MissingMember", symbol.Key, "找不到登记的成员：" + symbol.TypeName + "." + symbol.MemberName));
                if (symbol.Kind == "enum" && names.EntityName(reader, type.BaseType) is var baseType
                    && !baseType.EndsWith("System.Enum", StringComparison.Ordinal))
                    issues.Add(new("WrongTypeKind", symbol.Key, "登记为枚举的类型不继承 System.Enum。"));
                if (symbol.ExpectedValue is long expected && members.Any(member =>
                    !long.TryParse(member.ConstantValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value != expected))
                    issues.Add(new("UnexpectedConstantValue", symbol.Key, "常量值与共享登记不符，预期 " + expected.ToString(CultureInfo.InvariantCulture)));
                symbols.Add(new(symbol.Key, symbol.AssemblyFile, symbol.TypeName, symbol.MemberName,
                    symbol.Kind, symbol.Purpose, symbol.Effect, type.Attributes.ToString(),
                    names.EntityName(reader, type.BaseType), members.OrderBy(x => x.Kind, StringComparer.Ordinal)
                        .ThenBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Signature, StringComparer.Ordinal).ToList()));
            }
        }
        return new(new(1, ClientSymbols.VerifiedClientVersion, assemblies, symbols), issues);
    }

    private static List<MemberFingerprint> CaptureMembers(PEReader pe, MetadataReader reader,
        SignatureNames names, TypeDefinition type, ClientSymbol symbol)
    {
        var members = new List<MemberFingerprint>();
        switch (symbol.Kind)
        {
            case "type": return members;
            case "method":
                foreach (var handle in type.GetMethods())
                {
                    var method = reader.GetMethodDefinition(handle);
                    if (reader.GetString(method.Name) == symbol.MemberName) members.Add(Method(pe, reader, names, handle));
                }
                break;
            case "field": case "enum":
                foreach (var handle in type.GetFields())
                {
                    var field = reader.GetFieldDefinition(handle);
                    if (symbol.Kind == "enum" ? (field.Attributes & FieldAttributes.Literal) != 0
                        : reader.GetString(field.Name) == symbol.MemberName)
                    {
                        var (constantType, value) = Constant(reader, field.GetDefaultValue());
                        members.Add(new(reader.GetString(field.Name), "field", field.DecodeSignature(names, null),
                            field.Attributes.ToString(), string.Empty, null, null, constantType, value));
                    }
                }
                break;
            case "property":
                foreach (var handle in type.GetProperties())
                {
                    var property = reader.GetPropertyDefinition(handle);
                    if (reader.GetString(property.Name) != symbol.MemberName) continue;
                    var (constantType, value) = Constant(reader, property.GetDefaultValue());
                    members.Add(new(reader.GetString(property.Name), "property", SignatureNames.Format(property.DecodeSignature(names, null)),
                        property.Attributes.ToString(), string.Empty, null, null, constantType, value));
                    var accessors = property.GetAccessors();
                    if (!accessors.Getter.IsNil) members.Add(Method(pe, reader, names, accessors.Getter));
                    if (!accessors.Setter.IsNil) members.Add(Method(pe, reader, names, accessors.Setter));
                    foreach (var accessor in accessors.Others) members.Add(Method(pe, reader, names, accessor));
                }
                break;
            default: throw new InvalidDataException("未知登记 Kind：" + symbol.Kind);
        }
        return members;
    }

    private static MemberFingerprint Method(PEReader pe, MetadataReader reader, SignatureNames names, MethodDefinitionHandle handle)
    {
        var method = reader.GetMethodDefinition(handle);
        var signature = SignatureNames.Format(method.DecodeSignature(names, null));
        // Parameter attributes distinguish ref from out, optional arguments, and default values.
        var parameterContract = new List<object>();
        foreach (var parameterHandle in method.GetParameters())
        {
            var parameter = reader.GetParameter(parameterHandle);
            var (constantType, value) = Constant(reader, parameter.GetDefaultValue());
            parameterContract.Add(new { index = parameter.SequenceNumber, attributes = parameter.Attributes.ToString(), constantType, value });
        }
        signature += ";parameters=" + JsonSerializer.Serialize(parameterContract);
        string? ilHash = null;
        string? structureHash = null;
        if (method.RelativeVirtualAddress != 0)
        {
            var body = pe.GetMethodBody(method.RelativeVirtualAddress);
            ilHash = Hash(body.GetILBytes() ?? throw new BadImageFormatException("方法缺少 IL 字节。"));
            var locals = body.LocalSignature.IsNil ? []
                : reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(names, null).ToArray();
            var regions = body.ExceptionRegions.Select(region => new
            {
                kind = region.Kind.ToString(), region.TryOffset, region.TryLength, region.HandlerOffset, region.HandlerLength,
                filterOffset = region.Kind == ExceptionRegionKind.Filter ? region.FilterOffset : (int?)null,
                catchType = region.Kind == ExceptionRegionKind.Catch ? names.EntityName(reader, region.CatchType) : null,
            });
            structureHash = Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                body.MaxStack, body.LocalVariablesInitialized, locals, regions,
            })));
        }
        return new(reader.GetString(method.Name), "method", signature, method.Attributes.ToString(),
            method.ImplAttributes.ToString(), ilHash, structureHash, null, null);
    }

    private static (string? Type, string? Value) Constant(MetadataReader reader, ConstantHandle handle)
    {
        if (handle.IsNil) return (null, null);
        var constant = reader.GetConstant(handle);
        var value = reader.GetBlobReader(constant.Value);
        var text = constant.TypeCode switch
        {
            ConstantTypeCode.Boolean => value.ReadBoolean().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.Char => ((int)value.ReadChar()).ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.SByte => value.ReadSByte().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.Byte => value.ReadByte().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.Int16 => value.ReadInt16().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.UInt16 => value.ReadUInt16().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.Int32 => value.ReadInt32().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.UInt32 => value.ReadUInt32().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.Int64 => value.ReadInt64().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.UInt64 => value.ReadUInt64().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.Single => value.ReadSingle().ToString("R", CultureInfo.InvariantCulture),
            ConstantTypeCode.Double => value.ReadDouble().ToString("R", CultureInfo.InvariantCulture),
            ConstantTypeCode.String => Encoding.Unicode.GetString(reader.GetBlobBytes(constant.Value)),
            ConstantTypeCode.NullReference => "null",
            _ => throw new BadImageFormatException("未知常量类型：" + constant.TypeCode),
        };
        return (constant.TypeCode.ToString(), text);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
