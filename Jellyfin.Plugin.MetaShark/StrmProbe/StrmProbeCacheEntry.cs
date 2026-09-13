using System;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// 探针缓存条目。
/// </summary>
public sealed class StrmProbeCacheEntry
{
    /// <summary>
    /// Gets or sets 缓存 key（URL+文件大小+签名/mtime）。
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets 原始 strm 直链 URL。
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets strm 文件大小。
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// Gets or sets 签名/mtime。
    /// </summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets 探针解析出的可直连 URL（跟随跳转后的最终地址）。
    /// </summary>
    public string DirectUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets 内容类型。
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// Gets or sets 内容长度。
    /// </summary>
    public long? ContentLength { get; set; }

    /// <summary>
    /// Gets or sets 探针时间（UTC）。
    /// </summary>
    public DateTime ProbedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets 过期时间（UTC）。过期视为未命中并自动重探。
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }
}
