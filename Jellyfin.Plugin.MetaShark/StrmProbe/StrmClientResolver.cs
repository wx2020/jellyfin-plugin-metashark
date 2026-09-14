using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// 客户端名称解析器：仅作请求头/查询串的兼容兜底。
/// 调用方（如取流 302 直跳过滤器）应优先从已认证的 <c>HttpContext.User</c> claims 取 Client；
/// core 10.11 认证链把 Client 写进 claims，且不写 <c>HttpContext.Items["AuthorizationInfo"]</c>
/// （该键在正常 API 请求里恒为空），故本类的 Items 分支只是旧版兼容。
/// Emby 系第三方客户端（如 Yamby）常用 api_key/token 鉴权，请求里可能根本没有鉴权头，
/// 此时解析不到客户端名，调用方按未知客户端=原生处理。
/// </summary>
public static class StrmClientResolver
{
    /// <summary>
    /// 服务端鉴权中间件在 HttpContext.Items 中的缓存键。
    /// </summary>
    public const string AuthorizationInfoItemsKey = "AuthorizationInfo";

    /// <summary>
    /// 解析当前请求客户端名称。解析不到返回 null（调用方按未知客户端=原生处理）。
    /// </summary>
    /// <param name="request">当前 HTTP 请求（可为空）。</param>
    /// <param name="items">当前 HttpContext.Items（可为空）。</param>
    /// <returns>客户端名称或 null。</returns>
    public static string? Resolve(HttpRequest? request, IDictionary<object, object?>? items)
    {
        // 1) 旧版兼容：鉴权中间件缓存的 AuthorizationInfo（core 10.11 正常请求里该键恒为空）。
        if (items != null
            && items.TryGetValue(AuthorizationInfoItemsKey, out var cached)
            && cached is AuthorizationInfo authInfo
            && !string.IsNullOrWhiteSpace(authInfo.Client))
        {
            return authInfo.Client.Trim();
        }

        if (request == null)
        {
            return null;
        }

        // 2) 回退：请求头（与服务端 AuthorizationContext 取值顺序一致）。
        string? auth = request.Headers["Authorization"].ToString();
        if (string.IsNullOrWhiteSpace(auth))
        {
            auth = request.Headers["X-Emby-Authorization"].ToString();
        }

        if (string.IsNullOrWhiteSpace(auth))
        {
            auth = request.Headers["X-MediaBrowser-Authorization"].ToString();
        }

        if (!string.IsNullOrWhiteSpace(auth))
        {
            return StrmClientPolicy.ExtractClientName(auth);
        }

        // 3) 补充：查询串中的鉴权值（非服务端标准，仅作客户端识别的宽松补充）。
        var queryAuth = request.Query["X-Emby-Authorization"].ToString();
        if (!string.IsNullOrWhiteSpace(queryAuth))
        {
            return StrmClientPolicy.ExtractClientName(queryAuth);
        }

        // 纯 api_key/token 请求：请求内无客户端名，按未知处理（原生行为）。
        return null;
    }
}
