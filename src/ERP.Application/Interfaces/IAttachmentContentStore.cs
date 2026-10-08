namespace ERP.Application.Interfaces;

/// <summary>
/// 附件内容存储接缝（ERP-061）：仓库内**唯一**的附件二进制内容访问抽象。
/// <para>审计结论：本仓库既有的文件 / 图片存储代码只有 <c>OssStorageService</c>
/// （阿里云 OSS V1 签名 PUT，仅被商品图片上传使用；无接口、无下载、无删除），
/// 没有任何二进制附件子系统。因此本接缝是「一个统一模型」而不是第二套文件存储：</para>
/// <list type="bullet">
/// <item>**开发与测试**阶段只允许使用隔离的非生产本地提供程序
/// （<c>IsolatedLocalAttachmentContentStore</c>，写入应用目录之外的隔离目录）；</item>
/// <item>**生产对象存储**（既有 <c>OssStorageService</c>）在本阶段**不实现、不注册、不激活**：
/// 它的实现与启用属于「生产 OSS 变更」，必须走既有的生产 OSS Human Gate；</item>
/// <item>实现方必须保证：键由服务端生成（不透明、不接受客户端输入）、内容按不可信文件对待、
/// 不把内容路径暴露给客户端。</item>
/// </list>
/// </summary>
public interface IAttachmentContentStore
{
    /// <summary>提供程序编码（隔离非生产本地存储为 <c>local-isolated</c>）</summary>
    string ProviderCode { get; }

    /// <summary>提供程序中文文案（用于接口 / 界面同源显示，避免前端硬编码）</summary>
    string ProviderText { get; }

    /// <summary>
    /// 是否为**生产**对象存储提供程序：为 true 时附件证据模块一律拒绝写入
    /// （生产 OSS 激活属于 Human Gate 活动，本阶段不提供该实现）。
    /// </summary>
    bool IsProductionProvider { get; }

    /// <summary>
    /// 保存内容并返回**服务端生成**的不透明存储键（形如 <c>yyyyMMdd/{guid}{ext}</c>）。
    /// <para>调用方只传内容与由服务端判定的扩展名：不传文件名、不传路径、不传键。</para>
    /// </summary>
    Task<string> SaveAsync(Stream content, string extension, CancellationToken cancellationToken = default);

    /// <summary>
    /// 打开内容读取流（不可用 / 不存在时返回 null 或抛错，由调用方转成明确的业务提示）。
    /// <para>返回的流**不**携带客户端可见的路径信息。</para>
    /// </summary>
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default);

    /// <summary>读取对象长度（元数据，用于下载前的一致性校验；不可用时返回 null）</summary>
    Task<long?> GetLengthAsync(string storageKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// 有界补偿接缝（ERP-409）：删除<strong>本次请求新建</strong>的不透明内容键（形如
    /// <c>yyyyMMdd/{32hex}{.pdf|.png|.jpg|.jpeg}</c>），<strong>仅</strong>用于「元数据写入已被确认回滚」之后的
    /// 一次性补偿。
    /// <para>调用契约（调用方必须遵守）：只传<strong>同一次登记操作内</strong>刚刚由 <see cref="SaveAsync"/>
    /// 返回的键；<strong>绝不</strong>传既有键、已受理证据键、用户输入或任何路径。</para>
    /// <para>实现契约（实现方必须遵守）：校验键形态；解析并确认落在隔离根目录之内；只删除那一个文件，
    /// <strong>绝不</strong>做目录扫荡、通配删除或任意路径删除；生产提供程序与不支持该能力的实现一律返回
    /// <c>false</c>（上层据此判定「清理未成功」并保留可恢复的内部证据，绝不谎报已清理）。</para>
    /// <para>默认实现返回 <c>false</c>（不支持补偿）：这让既有实现无需改动即可继续编译，同时保证
    /// 「默认不删除任何内容」是最安全的失败姿态。</para>
    /// </summary>
    Task<bool> TryRemoveRequestOwnedAsync(string? storageKey, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
