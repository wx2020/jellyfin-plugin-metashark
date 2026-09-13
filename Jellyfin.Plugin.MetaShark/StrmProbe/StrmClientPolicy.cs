using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// 播放决策：虚拟直连放行 / 原生行为 / 后台预热。
/// </summary>
public enum StrmPlaybackDecisionKind
{
    /// <summary>走原生行为，不返回虚拟直连，不触发后台探针写缓存。</summary>
    Native = 0,

    /// <summary>返回缓存命中的虚拟直连。</summary>
    VirtualCached = 1,

    /// <summary>缓存未命中但放行虚拟直连（未探针的原始直链），同时后台预热写缓存。</summary>
    VirtualUnprobed = 2,

    /// <summary>走原生行为，但后台预热写缓存（供下次命中）。</summary>
    NativeWithWarmup = 3,
}

/// <summary>
/// 播放决策结果。
/// </summary>
public sealed class StrmPlaybackDecision
{
    /// <summary>
    /// Gets or sets 决策类型。
    /// </summary>
    public StrmPlaybackDecisionKind Kind { get; set; }

    /// <summary>
    /// Gets or sets 是否应读取缓存。总开关关闭或原生客户端时为 false（加速链路整体停用）。
    /// </summary>
    public bool ShouldReadCache { get; set; }

    /// <summary>
    /// Gets or sets 是否返回虚拟直连 MediaSource。
    /// </summary>
    public bool ServeVirtual => Kind == StrmPlaybackDecisionKind.VirtualCached || Kind == StrmPlaybackDecisionKind.VirtualUnprobed;

    /// <summary>
    /// Gets or sets 是否触发后台探针写缓存。
    /// </summary>
    public bool EnqueueWarmup => Kind == StrmPlaybackDecisionKind.VirtualUnprobed || Kind == StrmPlaybackDecisionKind.NativeWithWarmup;
}

/// <summary>
/// 客户端分流策略（纯逻辑，可单元测试）。
/// 原生流程：所有 Client 以 Jellyfin 开头的官方 Web/Android/iOS/Media Player，以及未知客户端（空/缺失），一律走原生行为且不触发后台探针写缓存。
/// 白名单第三方客户端：仅名单内客户端可走虚拟直连与后台预热。
/// </summary>
public static class StrmClientPolicy
{
    private static readonly Regex AuthClientRegex = new Regex("Client\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 是否原生客户端（官方 Jellyfin 前缀或未知客户端）。
    /// </summary>
    /// <param name="clientName">客户端名称（可为空）。</param>
    /// <returns>原生返回 true。</returns>
    public static bool IsNativeClient(string? clientName)
    {
        if (string.IsNullOrWhiteSpace(clientName))
        {
            return true;
        }

        return clientName.Trim().StartsWith("Jellyfin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解析白名单配置（逗号/分号/换行分隔，大小写不敏感，去空去重）。
    /// </summary>
    /// <param name="whitelistConfig">配置原文。</param>
    /// <returns>名单。</returns>
    public static IReadOnlyList<string> ParseWhitelist(string? whitelistConfig)
    {
        if (string.IsNullOrWhiteSpace(whitelistConfig))
        {
            return Array.Empty<string>();
        }

        return whitelistConfig
            .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 是否白名单内第三方客户端（必须非原生且命中名单，大小写不敏感精确匹配）。
    /// </summary>
    /// <param name="clientName">客户端名称。</param>
    /// <param name="whitelist">已解析名单。</param>
    /// <returns>命中返回 true。</returns>
    public static bool IsWhitelistedThirdParty(string? clientName, IEnumerable<string> whitelist)
    {
        if (IsNativeClient(clientName))
        {
            return false;
        }

        var name = clientName!.Trim();
        foreach (var entry in whitelist)
        {
            if (string.Equals(name, entry.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 综合决策。
    /// </summary>
    /// <param name="masterEnabled">总开关。</param>
    /// <param name="directOnCacheMiss">子开关：无缓存时返回直链。</param>
    /// <param name="clientName">客户端名称（可为空，未知按原生处理）。</param>
    /// <param name="whitelist">已解析白名单。</param>
    /// <param name="cacheHit">缓存是否命中（调用方仅在 ShouldReadCache 为 true 时才需查库）。</param>
    /// <returns>决策结果。</returns>
    public static StrmPlaybackDecision Resolve(
        bool masterEnabled,
        bool directOnCacheMiss,
        string? clientName,
        IEnumerable<string> whitelist,
        bool cacheHit)
    {
        if (!masterEnabled)
        {
            return new StrmPlaybackDecision { Kind = StrmPlaybackDecisionKind.Native, ShouldReadCache = false };
        }

        if (!IsWhitelistedThirdParty(clientName, whitelist))
        {
            // 原生流程（含官方 Jellyfin*、未知客户端、非白名单第三方）：一律原生，且不触发后台探针。
            return new StrmPlaybackDecision { Kind = StrmPlaybackDecisionKind.Native, ShouldReadCache = false };
        }

        if (cacheHit)
        {
            return new StrmPlaybackDecision { Kind = StrmPlaybackDecisionKind.VirtualCached, ShouldReadCache = true };
        }

        if (directOnCacheMiss)
        {
            return new StrmPlaybackDecision { Kind = StrmPlaybackDecisionKind.VirtualUnprobed, ShouldReadCache = true };
        }

        return new StrmPlaybackDecision { Kind = StrmPlaybackDecisionKind.NativeWithWarmup, ShouldReadCache = true };
    }

    /// <summary>
    /// 从 X-Emby-Authorization 头解析 Client 名称。缺失返回 null（按未知客户端处理）。
    /// </summary>
    /// <param name="authorizationHeader">X-Emby-Authorization 头原文。</param>
    /// <returns>客户端名称或 null。</returns>
    public static string? ExtractClientName(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return null;
        }

        var match = AuthClientRegex.Match(authorizationHeader);
        if (!match.Success)
        {
            return null;
        }

        var name = match.Groups[1].Value.Trim();
        return name.Length == 0 ? null : name;
    }
}
