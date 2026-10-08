using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Reflection.V1Alpha;
using GrpcDotNetNamedPipes;

// 探测坚果云客户端暴露的 gRPC over named pipe 接口。
//
// 背景：NutstoreClient.NamedPipeServerImpl.GrpcBadgeNamedPipeService 继承了
// NutstoreBadgeBase，说明客户端在命名管道上跑了一个 gRPC 服务；程序集里还有
// .NutstoreBadgeReflection（gRPC 反射服务宿主）。反射可用的话，就能还原出完整
// .proto 并生成强类型客户端 —— 这是「介入软件」而不是「驱动 GUI」的正路。
//
// Grpc.Reflection 包只提供服务端实现，客户端桩要自己搓（消息类型是现成的）。
//
// 用法：PipeProbe [pipeName] [serverName]

var pipeName = args.Length > 0 ? args[0] : "BadgeGrpcNamedPipe.BadgeToNutstoreV3";
var serverName = args.Length > 1 ? args[1] : "localhost";

Console.WriteLine($"管道       : {pipeName}");
Console.WriteLine($"serverName : {serverName}");
Console.WriteLine();

CallInvoker channel = new NamedPipeChannel(serverName, pipeName);

var found = await TryReflectionAsync(channel, "grpc.reflection.v1alpha.ServerReflection")
            || await TryReflectionAsync(channel, "grpc.reflection.v1.ServerReflection");

if (!found)
{
    Console.WriteLine();
    Console.WriteLine("反射不可用 —— 用已知方法名探路（空 protobuf 消息）：");
    await TryKnownMethodsAsync(channel);
}

return 0;

static Method<TReq, TRes> Unary<TReq, TRes>(
    string service, string name,
    MessageParser<TReq> reqParser, MessageParser<TRes> resParser)
    where TReq : IMessage<TReq>
    where TRes : IMessage<TRes>
    => new(
        MethodType.Unary, service, name,
        Marshallers.Create<TReq>(static m => m.ToByteArray(), bytes => reqParser.ParseFrom(bytes)),
        Marshallers.Create<TRes>(static m => m.ToByteArray(), bytes => resParser.ParseFrom(bytes)));

static async Task<bool> TryReflectionAsync(CallInvoker channel, string serviceName)
{
    Console.WriteLine($"── gRPC 反射：{serviceName} ──");
    try
    {
        var method = new Method<ServerReflectionRequest, ServerReflectionResponse>(
            MethodType.DuplexStreaming, serviceName, "ServerReflectionInfo",
            Marshallers.Create<ServerReflectionRequest>(
                static m => m.ToByteArray(), static b => ServerReflectionRequest.Parser.ParseFrom(b)),
            Marshallers.Create<ServerReflectionResponse>(
                static m => m.ToByteArray(), static b => ServerReflectionResponse.Parser.ParseFrom(b)));

        using var call = channel.AsyncDuplexStreamingCall(method, "localhost", new CallOptions());

        await call.RequestStream.WriteAsync(new ServerReflectionRequest { ListServices = string.Empty });
        await call.RequestStream.CompleteAsync();

        var services = new List<string>();
        while (await call.ResponseStream.MoveNext())
        {
            var resp = call.ResponseStream.Current;
            if (resp.ListServicesResponse is null)
            {
                continue;
            }

            services.AddRange(resp.ListServicesResponse.Service.Select(s => s.Name));
        }

        if (services.Count == 0)
        {
            Console.WriteLine("  ⚠️ 反射服务有响应，但服务列表为空");
            return false;
        }

        Console.WriteLine($"  ✅ 反射可用！暴露了 {services.Count} 个服务：");
        foreach (var s in services)
        {
            Console.WriteLine($"     · {s}");
        }

        return true;
    }
    catch (RpcException ex)
    {
        // 即便失败也有信息量：能拿到 gRPC 状态码，就说明传输层与协议帧是对的
        Console.WriteLine($"  ❌ {ex.StatusCode} — {ex.Status.Detail}");
        return false;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ❌ {ex.GetType().Name}: {ex.Message}");
        return false;
    }
}

static async Task TryKnownMethodsAsync(CallInvoker channel)
{
    // 方法名来自 GrpcBadgeNamedPipeService 的成员清单
    string[] methods =
    [
        "GetBadgeServerToken", "GetNutstoreFile", "GetLockInfo",
        "ShareFileByLink", "BrowseFileHistory", "DiscussFile", "GotoOnlineEdit",
    ];

    string[] services = [".NutstoreBadge", "NutstoreBadge", "Nutstore.Badge", "Badge", "Nutstore"];

    foreach (var svc in services)
    {
        foreach (var m in methods)
        {
            var method = new Method<IMessage, IMessage>(
                MethodType.Unary, svc, m,
                Marshallers.Create<IMessage>(
                    static x => x.ToByteArray(),
                    static b => new RawMessage(b)),
                Marshallers.Create<IMessage>(
                    static x => x.ToByteArray(),
                    static b => new RawMessage(b)));

            try
            {
                var call = channel.AsyncUnaryCall(method, "localhost", new CallOptions(), new RawMessage([]));
                await call.ResponseAsync;
                Console.WriteLine($"  ✅ /{svc}/{m} → 调用成功");
                return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                // 服务名或方法名不对，继续试
            }
            catch (RpcException ex)
            {
                // 不是 Unimplemented，说明名字对了 —— 只是参数/权限不对
                Console.WriteLine($"  🎯 /{svc}/{m} → {ex.StatusCode}: {ex.Status.Detail}");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ /{svc}/{m} → {ex.GetType().Name}: {ex.Message}");
                return;
            }
        }
    }

    Console.WriteLine("  所有候选服务名都返回 Unimplemented —— 服务名不在候选清单里。");
}

/// <summary>原始 protobuf 字节，用于在不知道 schema 的情况下探路。</summary>
internal sealed class RawMessage(byte[] data) : IMessage
{
    public byte[] Data { get; } = data;

    // 逐字节写原始内容。本项目只发空消息，所以实际是 no-op；
    // 写成循环是为了在换 protobuf 版本时也不依赖 WriteRawBytes 的具体重载。
    public void WriteTo(CodedOutputStream output)
    {
        foreach (var b in Data)
        {
            output.WriteRawTag(b);
        }
    }

    public int CalculateSize() => Data.Length;

    public void MergeFrom(CodedInputStream input) => input.SkipLastField();

    public bool Equals(IMessage? other) => other is RawMessage r && r.Data.AsSpan().SequenceEqual(Data);

    public MessageDescriptor Descriptor => throw new NotSupportedException();
}
