using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

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
