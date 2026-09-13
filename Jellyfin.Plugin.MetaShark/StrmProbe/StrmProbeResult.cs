using System;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// 单次探针结果（不含缓存 key）。
/// </summary>
public sealed class StrmProbeResult
{
    /// <summary>
    /// Gets or sets 原始 URL。
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets 跟随跳转后的可直连 URL。
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
    public DateTime ProbedAtUtc { get; set; } = DateTime.UtcNow;
}
