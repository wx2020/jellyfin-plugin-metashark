using System;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// ffprobe 探测结果缓存条目（装饰 <c>IMediaEncoder.GetMediaInfo</c> 用）。
/// key 仅由请求 URL 派生：URL 变化即天然未命中；TTL 仅作同 URL 换内容的安全阀。
/// </summary>
public sealed class MediaInfoProbeCacheEntry
{
    /// <summary>
    /// Gets or sets 缓存 key（请求 URL 的 SHA256）。
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets 被探测的完整请求 URL。
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets 探测结果 <c>MediaInfo</c> 的 JSON 序列化。
    /// </summary>
    public string MediaInfoJson { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets 探针时间（UTC）。
    /// </summary>
    public DateTime ProbedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets 过期时间（UTC）。过期视为未命中并自动重探。
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }
}
