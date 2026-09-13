using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// 取流补身份：`GET /Videos/{id}/stream` 按 mediaSourceId 解析虚拟源时的兜底。
/// 背景：部分第三方客户端（Lenna）的 PlaybackInfo 带客户端身份（放行正常），
/// 但切源后的取流请求是纯 api_key（无鉴权头/DeviceId），`StrmClientResolver` 解不出
/// Client → 按未知=原生 → provider 不下发虚拟源 → core 查无此源 → 400。
/// 兜底规则（只读缓存、不写库）：当前请求是 `/Videos/…` 取流、查询串的 mediaSourceId
/// 恰好等于本条目缓存 key 派生的虚拟 Guid、且缓存命中，才返回虚拟源。
/// 能说出该 Guid 即证明 PlaybackInfo 阶段已通过白名单门控见过双源（Guid 由缓存 key
/// 经 MD5 派生，不可猜），故此处不再做客户端门控；缓存未命中一律返回 null（走原生回退）。
/// </summary>
public static class StrmStreamFallback
{
    /// <summary>
    /// 尝试按取流请求的 mediaSourceId 解析虚拟源。条件不满足返回 null。
    /// </summary>
    /// <param name="request">当前 HTTP 请求（可为空）。</param>
    /// <param name="item">当前条目。</param>
    /// <param name="store">探针缓存（只读）。</param>
    /// <param name="nowUtc">当前 UTC 时间。</param>
    /// <returns>虚拟源或 null。</returns>
    public static MediaSourceInfo? TryResolve(
        HttpRequest? request,
        BaseItem? item,
        IStrmProbeCacheStore store,
        DateTime nowUtc)
    {
        if (request == null || item == null || !StrmFileHelper.IsStrmPath(item.Path))
        {
            return null;
        }

        if (!request.Path.StartsWithSegments(new PathString("/Videos"), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var requestedId = request.Query["mediaSourceId"].ToString();
        if (string.IsNullOrWhiteSpace(requestedId))
        {
            return null;
        }

        if (!StrmFileHelper.TryReadStrmLink(item.Path, out var url, out var fileSize, out var signature))
        {
            return null;
        }

        var key = StrmProbeCacheKey.Compute(url, fileSize, signature);
        if (!string.Equals(requestedId.Trim(), StrmVirtualSourceFactory.DeriveStableId(key), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var cached = store.TryGet(key, nowUtc);
        if (cached == null)
        {
            return null;
        }

        return StrmVirtualSourceFactory.Build(key, cached.Url, cached.ContentLength, cached.ContentType);
    }
}
