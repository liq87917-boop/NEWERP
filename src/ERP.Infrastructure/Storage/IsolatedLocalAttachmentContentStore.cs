using ERP.Application.Interfaces;
using ERP.Application.Services;

namespace ERP.Infrastructure.Storage;

/// <summary>
/// 隔离的非生产本地附件内容存储（ERP-061）：唯一在开发与测试中启用的内容提供程序。
/// <para>安全口径：</para>
/// <list type="bullet">
/// <item>存储键**只由服务端生成**（<c>yyyyMMdd/{guid}{ext}</c>），不接受客户端输入，
/// 也不包含客户端文件名，因此不存在「可直接猜测的文件路径」；</item>
/// <item>根目录解析后**必须**位于 Web 根目录（wwwroot）之外：内容不会被静态文件中间件暴露；</item>
/// <item>键在使用前复核：拒绝绝对路径、路径穿越、盘符与反斜杠，解析后再校验未越出根目录；</item>
/// <item>扩展名只接受 <c>.pdf / .png / .jpg / .jpeg</c>（内容类型已由上层按文件签名判定）。</item>
/// </list>
/// <para>本类<strong>不</strong>接触任何生产对象存储：既有 <c>OssStorageService</c> 未被本模块引用或激活
/// （其实现与启用属于生产 OSS Human Gate）。</para>
/// </summary>
public sealed class IsolatedLocalAttachmentContentStore : IAttachmentContentStore
{
    private const int CopyBufferSize = 81920;

    private readonly string _rootPath;

    public IsolatedLocalAttachmentContentStore(AttachmentStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _rootPath = options.ResolveRootPath();
        if (ContainsWebRootSegment(_rootPath))
            throw new InvalidOperationException(
                $"附件内容存储根目录不能位于 Web 根目录（wwwroot）之内：{_rootPath}；"
                + "上传内容必须存放在静态文件服务范围之外（配置键 Attachments:LocalRootPath）");

        Directory.CreateDirectory(_rootPath);
    }

    /// <summary>提供程序编码：隔离的非生产本地存储</summary>
    public string ProviderCode => AttachmentEvidenceRules.ProviderIsolatedLocal;

    /// <summary>提供程序文案（与规则层同源）</summary>
    public string ProviderText => AttachmentEvidenceRules.ProviderText(ProviderCode);

    /// <summary>隔离的非生产提供程序 → 恒为 false（生产 OSS 未在此实现）</summary>
    public bool IsProductionProvider => false;

    /// <summary>隔离存储根目录（测试与诊断用；**不经任何接口暴露给客户端**）</summary>
    public string RootPath => _rootPath;

    /// <summary>保存内容并返回服务端生成的不透明存储键（不含客户端文件名与任何路径）</summary>
    public async Task<string> SaveAsync(
        Stream content, string extension, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var normalizedExtension = NormalizeExtension(extension);
        var storageKey = $"{DateTime.UtcNow:yyyyMMdd}/{Guid.NewGuid():N}{normalizedExtension}";
        var fullPath = ResolvePath(storageKey);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var target = new FileStream(
            fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await content.CopyToAsync(target, CopyBufferSize, cancellationToken);
        await target.FlushAsync(cancellationToken);

        return storageKey;
    }

    /// <summary>打开只读内容流（不存在时返回 null；返回的流不携带任何客户端可见路径）</summary>
    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolvePath(storageKey);
        if (!File.Exists(fullPath)) return Task.FromResult<Stream?>(null);

        Stream stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult<Stream?>(stream);
    }

    /// <summary>读取对象长度（元数据；不存在时返回 null）</summary>
    public Task<long?> GetLengthAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(ResolvePath(storageKey));
        return Task.FromResult<long?>(info.Exists ? info.Length : null);
    }

    /// <summary>扩展名白名单（隔离存储只接受 PDF / PNG / JPEG）</summary>
    private static string NormalizeExtension(string? extension)
    {
        var normalized = (extension ?? string.Empty).Trim().ToLowerInvariant();
        return AttachmentEvidenceRules.SupportedExtensions.Contains(normalized)
            ? normalized
            : throw new InvalidOperationException(
                $"隔离本地附件存储只接受 {AttachmentEvidenceRules.SupportedFormatsText} 扩展名"
                + $"（当前「{extension}」）：内容类型必须已按文件签名判定");
    }

    /// <summary>
    /// 把不透明存储键解析为根目录内的绝对路径：拒绝空键、超长键、绝对路径、路径穿越、盘符与反斜杠，
    /// 并在解析后再次确认未越出根目录（两层防护）。
    /// </summary>
    private string ResolvePath(string? storageKey)
    {
        var key = (storageKey ?? string.Empty).Trim();
        if (key.Length == 0)
            throw new InvalidOperationException("附件存储键不能为空");
        if (key.Length > AttachmentEvidenceRules.MaxStorageKeyLength)
            throw new InvalidOperationException("附件存储键超出长度上限");
        if (Path.IsPathRooted(key)
            || key.Contains("..", StringComparison.Ordinal)
            || key.Contains(':', StringComparison.Ordinal)
            || key.Contains('\\', StringComparison.Ordinal))
            throw new InvalidOperationException("附件存储键必须是不透明相对键（拒绝绝对路径、盘符与路径穿越）");

        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, key));
        var prefix = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("附件存储键解析后越出隔离存储根目录：已拒绝");

        return fullPath;
    }

    /// <summary>路径是否位于任意 Web 根目录（wwwroot）之内（上传内容必须落在静态文件服务范围之外）</summary>
    private static bool ContainsWebRootSegment(string path)
        => path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, "wwwroot", StringComparison.OrdinalIgnoreCase));
}
