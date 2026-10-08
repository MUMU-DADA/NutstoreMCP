using ModelContextProtocol;

namespace NutstoreMcp;

/// <summary>
/// 所有业务异常的基类。
///
/// **必须派生自 <see cref="McpException"/>**：MCP C# SDK 对普通异常只回一句
/// 通用的 "An error occurred invoking ..."，只有 <see cref="McpException"/> 的
/// Message 才会原样透传给客户端。派生自它，调用方才能看到「该改什么」。
///
/// 因此 Message 是**组合出来的** —— 包含 code 和 hint，而不只是人读的那句话。
/// </summary>
public class NutstoreException : McpException
{
    public string Code { get; }
    public string? Hint { get; }
    public Exception? Cause { get; }

    public NutstoreException(string message, string code, string? hint = null, Exception? cause = null)
        : base(Render(message, code, hint, cause))
    {
        Code = code;
        Hint = hint;
        Cause = cause;
    }

    private static string Render(string message, string code, string? hint, Exception? cause)
    {
        var text = $"\u274c {message}\ncode: {code}";
        if (!string.IsNullOrEmpty(hint))
        {
            text += $"\nhint: {hint}";
        }

        if (cause is not null && !string.IsNullOrEmpty(cause.Message))
        {
            text += $"\ncause: {cause.Message}";
        }

        return text;
    }
}

/// <summary>配置缺失或非法。</summary>
public sealed class ConfigException : NutstoreException
{
    public ConfigException(string message, string? hint = null, Exception? cause = null)
        : base(message, "CONFIG_ERROR", hint, cause) { }
}

/// <summary>路径越界、含非法片段，或不在白名单内。</summary>
public sealed class PathNotAllowedException : NutstoreException
{
    public PathNotAllowedException(string message, string? hint = null)
        : base(message, "PATH_NOT_ALLOWED", hint) { }
}

/// <summary>只读模式下尝试写操作。</summary>
public sealed class ReadOnlyException : NutstoreException
{
    public ReadOnlyException(string message, string? hint = null)
        : base(message, "READ_ONLY", hint) { }
}

/// <summary>WebDAV 认证失败。</summary>
public sealed class AuthException : NutstoreException
{
    public AuthException(string message, string? hint = null)
        : base(message, "AUTH_FAILED", hint) { }
}

/// <summary>远端资源不存在。</summary>
public sealed class RemoteNotFoundException : NutstoreException
{
    public RemoteNotFoundException(string message, string? hint = null)
        : base(message, "NOT_FOUND", hint) { }
}

/// <summary>本地配额用尽（自建限流器触发）。</summary>
public sealed class RateLimitedException : NutstoreException
{
    public RateLimitedException(string message, string? hint = null)
        : base(message, "RATE_LIMITED", hint) { }
}

/// <summary>本地坚果云数据不可用（未安装、数据库缺失、格式不识别）。</summary>
public sealed class LocalDataException : NutstoreException
{
    public LocalDataException(string message, string? hint = null, Exception? cause = null)
        : base(message, "LOCAL_DATA_UNAVAILABLE", hint, cause) { }
}

/// <summary>远端返回了非预期状态。</summary>
public sealed class RemoteException : NutstoreException
{
    public int? Status { get; }

    public RemoteException(string message, int? status = null, string? hint = null)
        : base(message, "REMOTE_ERROR", hint)
    {
        Status = status;
    }
}
