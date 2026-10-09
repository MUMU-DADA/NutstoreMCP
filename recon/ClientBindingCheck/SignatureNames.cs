// 解码客户端成员的完整类型签名。
using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace NutstoreMcp.ClientBindingCheck;

// Signature decoding resolves metadata handles only. It never resolves or loads CLR types.
internal sealed class SignatureNames(string assemblyName) : ISignatureTypeProvider<string, object?>
{
    public string GetArrayType(string elementType, ArrayShape shape) =>
        elementType + "[rank=" + shape.Rank + ";sizes=" + string.Join(",", shape.Sizes)
        + ";lower=" + string.Join(",", shape.LowerBounds) + "]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr(" + Format(signature) + ")";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        genericType + "<" + string.Join(",", typeArguments) + ">";
    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
        (isRequired ? "modreq(" : "modopt(") + modifier + ") " + unmodifiedType;
    public string GetPinnedType(string elementType) => "pinned " + elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Void => "System.Void",
        PrimitiveTypeCode.Boolean => "System.Boolean",
        PrimitiveTypeCode.Char => "System.Char",
        PrimitiveTypeCode.SByte => "System.SByte",
        PrimitiveTypeCode.Byte => "System.Byte",
        PrimitiveTypeCode.Int16 => "System.Int16",
        PrimitiveTypeCode.UInt16 => "System.UInt16",
        PrimitiveTypeCode.Int32 => "System.Int32",
        PrimitiveTypeCode.UInt32 => "System.UInt32",
        PrimitiveTypeCode.Int64 => "System.Int64",
        PrimitiveTypeCode.UInt64 => "System.UInt64",
        PrimitiveTypeCode.Single => "System.Single",
        PrimitiveTypeCode.Double => "System.Double",
        PrimitiveTypeCode.String => "System.String",
        PrimitiveTypeCode.IntPtr => "System.IntPtr",
        PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
        PrimitiveTypeCode.Object => "System.Object",
        PrimitiveTypeCode.TypedReference => "System.TypedReference",
        _ => throw new BadImageFormatException("未知基础签名类型：" + typeCode),
    };
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        KindPrefix(rawTypeKind) + "[" + assemblyName + "]" + DefinitionName(reader, handle);
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var reference = reader.GetTypeReference(handle);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
            return GetTypeFromReference(reader, (TypeReferenceHandle)reference.ResolutionScope, rawTypeKind)
                + "+" + reader.GetString(reference.Name);
        var scope = reference.ResolutionScope.Kind == HandleKind.AssemblyReference
            ? reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name)
            : assemblyName;
        var ns = reader.GetString(reference.Namespace);
        return KindPrefix(rawTypeKind) + "[" + scope + "]" + (ns.Length == 0 ? string.Empty : ns + ".") + reader.GetString(reference.Name);
    }
    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext,
        TypeSpecificationHandle handle, byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string EntityName(MetadataReader reader, EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0),
        HandleKind.TypeReference => GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0),
        HandleKind.TypeSpecification => GetTypeFromSpecification(reader, null, (TypeSpecificationHandle)handle, 0),
        _ when handle.IsNil => string.Empty,
        _ => throw new BadImageFormatException("未知类型元数据句柄：" + handle.Kind),
    };

    public static string DefinitionName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var parent = definition.GetDeclaringType();
        if (!parent.IsNil) return DefinitionName(reader, parent) + "+" + reader.GetString(definition.Name);
        var ns = reader.GetString(definition.Namespace);
        return (ns.Length == 0 ? string.Empty : ns + ".") + reader.GetString(definition.Name);
    }

    public static string Format(MethodSignature<string> signature) =>
        $"{signature.Header.CallingConvention};instance={signature.Header.IsInstance};explicitThis={signature.Header.HasExplicitThis};"
        + $"generic={signature.GenericParameterCount};required={signature.RequiredParameterCount};"
        + signature.ReturnType + "(" + string.Join(",", signature.ParameterTypes) + ")";

    private static string KindPrefix(byte rawTypeKind) => rawTypeKind switch
    {
        (byte)SignatureTypeKind.Class => "class ",
        (byte)SignatureTypeKind.ValueType => "valuetype ",
        _ => string.Empty,
    };
}
