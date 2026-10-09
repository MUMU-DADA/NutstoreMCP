// 集中登记客户端混淆符号及其用途。
namespace NutstoreMcp.ClientBindings;

internal static class ClientSymbols
{
    public const string VerifiedClientVersion = "7.2.12.0";
    public const string BaselineFileName = "baseline-7.2.12-upload.json";
    public const string CloudOpsAssembly = "NutstoreClient.Legacy.dll";
    public const string CloudOpsType = "A.c3795a089d76f103f11c4fe70f38ac8ca";
    public const string CommonAssembly = "Nutstore.Common.dll";
    public const string LibraryAssembly = "NutstoreLib.dll";
    public const string StringUtilsType = "NutstoreLib.Utils.StringUtils";
    public const string NutstorePathType = "NutstoreClient.Common.NutstorePath";
    public const string NSSandboxType = "NutstoreClient.Common.NSSandbox";
    public const string NutstoreObjectType = "NutstoreClient.Common.NutstoreObject";
    public const string NutstoreObjectBuilderType = NutstoreObjectType + "+Builder";
    public const string SandboxIdentifierType = "Nutstore.Common.SandboxIdentifier";
    public const string SandboxAclType = "Nutstore.Common.SandboxACL";
    public const string DownstreamCommandType = "NutstoreClient.Commands.DownstreamObjectCommand";
    public const string SandboxDaoType = "NutstoreClient.DAO.NSSandboxDAO";
    public const string CapsOnPathType = "NutstoreClient.Common.CapsOnPathBean";
    public const string CreateFileParamType = "NutstoreClient.Helpers.CreateFileParam";
    public const string CompressionMethodType = "NutstoreClient.Connection.CompressionMethod";
    public const string HashAlgorithmsType = "Nutstore.Common.Helpers.HashAlgorithms";
    public const string RequestExceptionType = "NutstoreClient.Connection.RequestException";
    public const int CloudPathMaxUtf8Bytes = 767;
    public const int SyncErrorUnknown = 0;
    public const int SyncErrorFileSizeLimit = 1;
    public const int SyncErrorPotentialMalware = 2;
    public const int SyncErrorIllegalPathCharacters = 3;
    public const int SyncErrorWindowsReservedNames = 4;

    public const string CreateSandbox = "ccdd0d7373903386c87fb2d3077bf8ced";
    public const string ListDirectory = "c05097d346f069f32352068d6ff359701";
    public const string CreateDirectory = "cfd1df8451110086567f3d56edf4697e8";
    public const string Search = "c7a517b89b338a47fe91fa3ec2a0823c3";
    public const string ReadUsage = "cf22046b2931cec246e3a4f4b994e6c84";
    public const string ReadUserInfo = "cc244cf7442842e41dd4ffb0830aeb3f3";
    public const string ReadSandboxDetail = "c083e3da23c3bbf0c707145cd9efea279";
    public const string ReadSandboxPermissions = "c3c96725a92962d165d900554f645b220";
    public const string ListFileVersions = "c2654043f830c74ecf5925237786dccea";
    public const string ReadPublishInfo = "c9a9142ba46c6a4cdb6c70a448962ca9e";
    public const string ReadContacts = "c915cc8f3fd71da2191b3d1dff9bbda1f";
    public const string ReadObjectDetail = "c55ca68332481ffab4014efe97582ac93";
    public const string ReadLatestEventId = "cbb28d79821068cd4efb5de6f9109afff";
    public const string DownloadContent = "c089b7574cb46db5e6d9bba4e0e0a6002";
    public const string MoveFile = "cfa4bff2b588c3bab781ef7189ef94b90";
    public const string ReadPathAcl = "c5c78b21a991a876d4bd8093b70f39202";
    public const string UploadFullContent = "c6e98b16ca153095711ef04f770d2ca53";
    public const string UploadDeltaContent = "cd20b0bb262184cd692f5b3bba1477f24";
    public const string CreateFileMetadata = "cf02e04aba958d38757ce778e8f94ae50";

    // 调查过的入口也参与升级检查；登记不代表允许执行。
    public const string ReadEventsWithLocalWrites = "c524be38afabc83eacc7d38aceea8700c";
    public const string DeleteSandbox = "cc62a4576fdf6f6721f7496c5b2d04e30";
    public const string DeleteObject = "cd9ad552f9198292967c88e3266e162cc";

    public static string[] ForbiddenMethodNames => new[]
    {
        "RemoveSandbox", "MigrateSandboxPath", "MigrateSandboxObjectTableToUnicodeNoCaseCollation",
        "UnregisterAllNutstoreSyncRoots", "UnregisterSyncRootsOfAccount", "UnregisterSyncRoot",
        "DisconnectSyncRoot", "DisableSyncRootOnDemandPopulation", "RemoveAUMIDToSyncRoots",
        "DeleteObject", "RemoveObject", DeleteSandbox, "MakeDeleteSandboxRequest", DeleteObject,
        "GetFieldValue", "SetFieldValue", "GetPropertyValue", "SetPropertyValue",
    };

    public static ClientSymbol[] Definitions => new[]
    {
        Entry(nameof(CreateSandbox), CreateSandbox, "创建云端沙箱", "write"),
        Entry(nameof(ListDirectory), ListDirectory, "列目录（按重载区分返回）", "read"),
        Entry(nameof(CreateDirectory), CreateDirectory, "创建单层云端目录", "write"),
        Entry(nameof(Search), Search, "云端搜索", "read"),
        Entry(nameof(ReadUsage), ReadUsage, "容量与流量用量", "read"),
        Entry(nameof(ReadUserInfo), ReadUserInfo, "用户信息", "read"),
        Entry(nameof(ReadSandboxDetail), ReadSandboxDetail, "沙箱详情", "read"),
        Entry(nameof(ReadSandboxPermissions), ReadSandboxPermissions, "沙箱权限", "read"),
        Entry(nameof(ListFileVersions), ListFileVersions, "文件历史版本列表", "read"),
        Entry(nameof(ReadPublishInfo), ReadPublishInfo, "发布状态", "read"),
        Entry(nameof(ReadContacts), ReadContacts, "协作联系人", "read"),
        Entry(nameof(ReadObjectDetail), ReadObjectDetail, "对象详情", "read"),
        Entry(nameof(ReadLatestEventId), ReadLatestEventId, "最新事件游标", "read"),
        Entry(nameof(DownloadContent), DownloadContent, "下载内容（返回流）", "read"),
        Entry(nameof(MoveFile), MoveFile, "移动普通文件", "write"),
        Entry(nameof(ReadPathAcl), ReadPathAcl, "路径权限查询", "read"),
        Entry(nameof(UploadFullContent), UploadFullContent, "上传全量内容；不等于创建文件", "write"),
        Entry(nameof(UploadDeltaContent), UploadDeltaContent, "增量内容上传；未开放", "write"),
        Entry(nameof(CreateFileMetadata), CreateFileMetadata, "创建文件元数据；碰撞语义待在线核实", "write"),
        new ClientSymbol("CompressionMethodValues", CloudOpsAssembly, CompressionMethodType,
            "", "上传压缩枚举", "read", "enum"),
        Support("UploadMd5", CommonAssembly, HashAlgorithmsType, "GetMd5Digest"),
        Support("UploadMd5Encoding", LibraryAssembly, StringUtilsType, "BytesToURLSafeBase64String"),
        Support("RequestErrorCode", CloudOpsAssembly, RequestExceptionType, "ErrorCode", "property"),
        Support("RequestHttpStatus", CloudOpsAssembly, RequestExceptionType, "ReturnCode", "property"),
        Support("CreateFileParamPath", CloudOpsAssembly, CreateFileParamType, "path", "field"),
        Support("CreateFileParamSize", CloudOpsAssembly, CreateFileParamType, "size", "field"),
        Support("CreateFileParamHash", CloudOpsAssembly, CreateFileParamType, "hashBase64", "field"),
        Support("CreateFileParamFullObject", CloudOpsAssembly, CreateFileParamType, "fullObjectNameBase64", "field"),
        Support("CreateFileParamBlockList", CloudOpsAssembly, CreateFileParamType, "blockListNameBase64", "field"),
        Support("CreateFileParamCompressible", CloudOpsAssembly, CreateFileParamType, "compressible", "field"),
        Support("CreateFileParamExAttr", CloudOpsAssembly, CreateFileParamType, "exAttr", "field"),
        Support("ReflectionReadField", CloudOpsAssembly, "NutstoreClient.Helpers.ReflectionHelper", "GetFieldValue", effect: "forbidden"),
        Support("ReflectionWriteField", CloudOpsAssembly, "NutstoreClient.Helpers.ReflectionHelper", "SetFieldValue", effect: "forbidden"),
        Support("ReflectionReadProperty", CloudOpsAssembly, "NutstoreClient.Helpers.ReflectionHelper", "GetPropertyValue", effect: "forbidden"),
        Support("ReflectionWriteProperty", CloudOpsAssembly, "NutstoreClient.Helpers.ReflectionHelper", "SetPropertyValue", effect: "forbidden"),
        Entry(nameof(ReadEventsWithLocalWrites), ReadEventsWithLocalWrites,
            "读取事件且写 EventDb、未读数和最近页；未开放", "local-write"),
        Entry(nameof(DeleteSandbox), DeleteSandbox, "删除整个云端沙箱；禁止调用", "forbidden"),
        Entry(nameof(DeleteObject), DeleteObject, "删除云端对象；禁止调用", "forbidden"),
        new ClientSymbol(nameof(CloudPathMaxUtf8Bytes), CloudOpsAssembly, NutstorePathType,
            "MAX_UTF8BYTES_LEN", "云端路径最大 UTF-8 字节数", "read", "field", CloudPathMaxUtf8Bytes),
        new ClientSymbol("PathPermissionFlags", CommonAssembly, "Nutstore.Common.SandboxACL+Caps",
            "", "路径权限位标志", "read", "enum"),
        new ClientSymbol("SandboxPermissionValues", CommonAssembly, "Nutstore.Common.SandboxACL+SandboxPermission",
            "", "沙箱权限等级", "read", "enum"),
        new ClientSymbol("ObjectTypeValues", CommonAssembly, "Nutstore.Common.ObjectType",
            "", "文件与目录类型", "read", "enum"),
        new ClientSymbol("SyncErrorReasonValues", CloudOpsAssembly, "NutstoreClient.Common.SyncErrorReason",
            "", "同步错误原因码", "read", "enum"),
        new ClientSymbol("SandboxSyncStateValues", CommonAssembly, "Nutstore.Common.SandboxSyncState",
            "", "沙箱同步状态", "read", "enum"),
        SyncError(nameof(SyncErrorUnknown), "Unknown", SyncErrorUnknown),
        SyncError(nameof(SyncErrorFileSizeLimit), "ExceedSingleFileSizeLimit", SyncErrorFileSizeLimit),
        SyncError(nameof(SyncErrorPotentialMalware), "PotentialMalwares", SyncErrorPotentialMalware),
        SyncError(nameof(SyncErrorIllegalPathCharacters), "IllegalCharactersInPath", SyncErrorIllegalPathCharacters),
        SyncError(nameof(SyncErrorWindowsReservedNames), "WindowsReservedNames", SyncErrorWindowsReservedNames),
        Support("SandboxConstructor", CloudOpsAssembly, NSSandboxType, ".ctor"),
        Support("IdentifierConstructor", CommonAssembly, SandboxIdentifierType, ".ctor"),
        Support("PathFromAbsolute", CloudOpsAssembly, NutstorePathType, "FromAbsolutePath"),
        Support("PathFromCloud", CloudOpsAssembly, NutstorePathType, "FromNutstorePath"),
        Support("PathToCloud", CloudOpsAssembly, NutstorePathType, "ToNutstorePath"),
        Support("FileBuilder", CloudOpsAssembly, NutstoreObjectBuilderType, "FileBuilder"),
        Support("BuildFile", CloudOpsAssembly, NutstoreObjectBuilderType, "Build"),
        Support("ParseObjectType", CloudOpsAssembly, DownstreamCommandType, "GetObjectTypeFromStr"),
        Support("ReadSandboxBindings", CloudOpsAssembly, SandboxDaoType, "ReadAllSandboxesFromDb"),
        Support("BindSandbox", CloudOpsAssembly, SandboxDaoType, "InitSandbox", effect: "write"),
        Support("CapsBeanConstructor", CloudOpsAssembly, CapsOnPathType, ".ctor"),
        Support("CapsBeanPath", CloudOpsAssembly, CapsOnPathType, "Path", "property"),
        Support("CapsBeanCaps", CloudOpsAssembly, CapsOnPathType, "Caps", "property"),
        Support("DefaultSandboxAcl", CommonAssembly, SandboxAclType, "GetDefaultPerm"),
        Support("AclSigned", CommonAssembly, SandboxAclType, "signed", "field"),
        Support("AclUsers", CommonAssembly, SandboxAclType, "users", "field"),
        Support("AclUserNicks", CommonAssembly, SandboxAclType, "userNicks", "field"),
        Support("AclGroups", CommonAssembly, SandboxAclType, "groups", "field"),
        Support("GroupId", CommonAssembly, "Nutstore.Common.GroupEntity", "Id", "property"),
        Support("GroupName", CommonAssembly, "Nutstore.Common.GroupEntity", "Name", "property"),
        Support("GroupPermission", CommonAssembly, "Nutstore.Common.GroupEntity", "Permission", "property"),
        Support("MutationResultVersion", CloudOpsAssembly, "NutstoreClient.Helpers.UpdateMetaDataResult", "version", "field"),
        Support("MutationResultPath", CloudOpsAssembly, "NutstoreClient.Helpers.UpdateMetaDataResult", "path", "field"),
        Support("PathAclInheritance", CloudOpsAssembly, "NutstoreClient.Helpers.SubPathAcl", "isInherit", "field"),
        Support("PathAclValue", CloudOpsAssembly, "NutstoreClient.Helpers.SubPathAcl", "acl", "field"),
    };

    private static ClientSymbol Entry(string key, string member, string purpose, string effect)
        => new ClientSymbol(key, CloudOpsAssembly, CloudOpsType, member, purpose, effect);

    private static ClientSymbol SyncError(string key, string member, int value)
        => new ClientSymbol(key, CloudOpsAssembly, "NutstoreClient.Common.SyncErrorReason",
            member, "同步错误原因码", "read", "field", value);

    private static ClientSymbol Support(string key, string assembly, string type, string member,
        string kind = "method", string effect = "read")
        => new ClientSymbol(key, assembly, type, member, "客户端辅助契约：" + key, effect, kind);
}

internal sealed class ClientSymbol
{
    public ClientSymbol(string key, string assemblyFile, string typeName,
        string memberName, string purpose, string effect, string kind = "method", long? expectedValue = null)
    {
        Key = key;
        AssemblyFile = assemblyFile;
        TypeName = typeName;
        MemberName = memberName;
        Purpose = purpose;
        Effect = effect;
        Kind = kind;
        ExpectedValue = expectedValue;
    }

    public string Key { get; }
    public string AssemblyFile { get; }
    public string TypeName { get; }
    public string MemberName { get; }
    public string Purpose { get; }
    public string Effect { get; }
    public string Kind { get; }
    public long? ExpectedValue { get; }
}
