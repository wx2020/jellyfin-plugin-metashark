using System;
using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// MetaShark 代理源端点 URL 构造（相对路径 + 基于当前请求的绝对路径）。
/// </summary>
public static class StrmProxyUrl
{
    /// <summary>代理端点路由前缀（与 <c>StrmProxyController</c> 一致）。</summary>
    public const string RoutePrefix = "/plugin/metashark/strm/proxy";

    /// <summary>票据过期时间查询参数名。</summary>
    public const string ExpParam = "exp";

    /// <summary>票据签名查询参数名。</summary>
    public const string SigParam = "sig";

    /// <summary>
    /// 构造查询串。
    /// </summary>
    /// <param name="expiresAtUnix">过期时间（Unix 秒）。</param>
    /// <param name="signature">签名。</param>
    /// <returns>查询串（不含前导 ?）。</returns>
    public static string BuildQuery(long expiresAtUnix, string signature)
    {
        return ExpParam + "=" + expiresAtUnix.ToString(CultureInfo.InvariantCulture)
            + "&" + SigParam + "=" + Uri.EscapeDataString(signature ?? string.Empty);
    }

    /// <summary>
    /// 构造相对路径（无请求上下文时的回退）。
    /// </summary>
    /// <param name="itemId">条目 Id。</param>
    /// <param name="expiresAtUnix">过期时间（Unix 秒）。</param>
    /// <param name="signature">签名。</param>
    /// <returns>相对路径。</returns>
    public static string BuildRelativePath(Guid itemId, long expiresAtUnix, string signature)
    {
        return RoutePrefix + "/" + itemId.ToString("N") + "?" + BuildQuery(expiresAtUnix, signature);
    }

    /// <summary>
    /// 构造绝对 URL：优先用当前请求的 scheme/host/pathBase（反向代理下同样可达）；
    /// 请求缺失时回退相对路径。
    /// </summary>
    /// <param name="request">当前 HTTP 请求（可为空）。</param>
    /// <param name="itemId">条目 Id。</param>
    /// <param name="expiresAtUnix">过期时间（Unix 秒）。</param>
    /// <param name="signature">签名。</param>
    /// <returns>端点 URL。</returns>
    public static string BuildAbsoluteUrl(HttpRequest? request, Guid itemId, long expiresAtUnix, string signature)
    {
        var relative = BuildRelativePath(itemId, expiresAtUnix, signature);
        if (request == null)
        {
            return relative;
        }

        var scheme = request.Scheme;
        var host = request.Host.Value;
        if (string.IsNullOrWhiteSpace(scheme) || string.IsNullOrWhiteSpace(host))
        {
            return relative;
        }

        var pathBase = request.PathBase.Value;
        return scheme + "://" + host + (pathBase ?? string.Empty) + relative;
    }
}
