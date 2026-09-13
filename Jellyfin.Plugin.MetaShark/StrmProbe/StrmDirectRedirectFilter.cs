using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// 取流 302 直跳：白名单第三方客户端（如 Lenna）的纯静态取流劫持为直链跳转。
/// 背景：这类客户端取流拼的是服务端地址（<c>GET /Videos/{id}/stream.mkv?Static=true</c>），
/// 服务端以 Static 管道代理字节，占服务端带宽；直连 <c>MediaSource.Path</c> 的客户端（如 Yamby）
/// 则完全不经过服务端。本过滤器在鉴权之后介入：条件全中时直接 <c>302 Found</c> 到
/// 本地 <c>.strm</c> 文件首行的 openlist 直链，让手机直连网盘，服务端只剩一条 302 日志。
/// 任一条件不满足即放行（走原生管道），fail-closed，永不抛异常。
/// 用全局 ActionFilter 而不用 <c>IStartupFilter</c> 中间件：后者包在 core 鉴权外层，
/// 执行时 <c>HttpContext.User</c> 尚未认证，做不了"已认证用户 + 条目可见"的放行门控。
/// </summary>
public sealed class StrmDirectRedirectFilter : IAsyncActionFilter
{
    private static readonly HashSet<string> TranscodeVetoArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "videoCodec",
        "audioCodec",
        "subtitleCodec",
        "subtitleMethod",
        "transcodeReasons",
        "liveStreamId",
        "segmentContainer",
        "segmentLength",
        "minSegments",
        "width",
        "height",
        "maxWidth",
        "maxHeight",
        "videoBitRate",
        "audioBitRate",
        "audioSampleRate",
        "maxAudioBitDepth",
        "maxAudioChannels",
        "transcodingMaxAudioChannels",
        "framerate",
        "maxFramerate",
        "profile",
        "level",
        "copyTimestamps",
        "enableMpegtsM2TsMode",
        "params",
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<StrmDirectRedirectFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmDirectRedirectFilter"/> class.
    /// 仅依赖单例服务，全局注册常驻安全。
    /// </summary>
    /// <param name="libraryManager">媒体库（按 Id 取条目）。</param>
    /// <param name="userManager">用户管理（鉴权用户回查）。</param>
    /// <param name="logger">日志。</param>
    public StrmDirectRedirectFilter(
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<StrmDirectRedirectFilter> logger)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        RedirectDecision? decision = null;
        try
        {
            decision = TryResolveTarget(context);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "strm 302 直跳解析失败，回退原生管道");
            decision = null;
        }

        if (decision == null)
        {
            await next().ConfigureAwait(false);
            return;
        }

        _logger.LogInformation(
            "strm 取流 302 直跳 client={Client} item={Item} source={Source} target={Target}",
            decision.ClientName,
            decision.ItemName,
            decision.SourceKind,
            RedactUrl(decision.TargetUrl));
        context.Result = new RedirectResult(decision.TargetUrl, permanent: false);
    }

    /// <summary>
    /// 解析本次 Action 是否命中直跳。命中返回决策，否则返回 null（调用方继续管道）。
    /// 内部 try/catch 已由调用方兜底；此处仍保持 fail-closed。
    /// </summary>
    /// <param name="context">当前 Action 上下文。</param>
    /// <returns>直跳决策或 null。</returns>
    internal RedirectDecision? TryResolveTarget(ActionExecutingContext context)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.EnableStrmDirectRedirect)
        {
            return null;
        }

        var httpContext = context.HttpContext;
        var method = httpContext.Request.Method;
        string? controllerName = null;
        string? actionName = null;
        if (context.ActionDescriptor is ControllerActionDescriptor cad)
        {
            controllerName = cad.ControllerName;
            actionName = cad.ActionName;
        }

        var args = context.ActionArguments;
        var clientName = StrmClientResolver.Resolve(httpContext.Request, httpContext.Items);
        var whitelist = StrmClientPolicy.ParseWhitelist(config.StrmProbeClientWhitelist);

        if (!TryGetItemId(args, out var itemId))
        {
            return null;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item == null || !StrmFileHelper.IsStrmPath(item.Path))
        {
            return null;
        }

        if (!TryResolveVisibleUser(httpContext.Items, item, out _))
        {
            return null;
        }

        if (!StrmFileHelper.TryReadStrmLink(item.Path, out var url, out var fileSize, out var signature))
        {
            return null;
        }

        var decision = Decide(
            method,
            controllerName,
            actionName,
            args,
            clientName,
            whitelist,
            masterEnabled: true,
            itemId,
            url,
            fileSize,
            signature);
        if (decision != null)
        {
            decision.ItemName = item.Name;
        }

        return decision;
    }

    /// <summary>
    /// 纯决策函数（无服务依赖，可单测）：给定请求要素判定是否直跳。
    /// </summary>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="controllerName">控制器名（如 Videos）。</param>
    /// <param name="actionName">Action 名（如 GetVideoStream）。</param>
    /// <param name="args">已绑定的 Action 参数。</param>
    /// <param name="clientName">客户端名称（可为空，未知按原生处理）。</param>
    /// <param name="whitelist">已解析白名单。</param>
    /// <param name="masterEnabled">直跳总开关。</param>
    /// <param name="itemId">路由/参数中的条目 Id。</param>
    /// <param name="strmUrl">实时读出的 .strm 直链。</param>
    /// <param name="fileSize">strm 文件大小。</param>
    /// <param name="signature">strm 文件 mtime 签名。</param>
    /// <returns>直跳决策或 null。</returns>
    internal static RedirectDecision? Decide(
        string? method,
        string? controllerName,
        string? actionName,
        IDictionary<string, object?> args,
        string? clientName,
        IReadOnlyList<string> whitelist,
        bool masterEnabled,
        Guid itemId,
        string? strmUrl,
        long fileSize,
        string signature)
    {
        if (!masterEnabled)
        {
            return null;
        }

        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.Equals(controllerName, "Videos", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(actionName, "GetVideoStream", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (args == null
            || !args.TryGetValue("static", out var staticValue)
            || !(staticValue is true))
        {
            return null;
        }

        if (HasTranscodeArgs(args))
        {
            return null;
        }

        if (!StrmClientPolicy.IsWhitelistedThirdParty(clientName, whitelist))
        {
            return null;
        }

        if (itemId == Guid.Empty)
        {
            return null;
        }

        if (!IsHttpUrl(strmUrl))
        {
            return null;
        }

        var requested = args.TryGetValue("mediaSourceId", out var mediaSourceValue)
            ? mediaSourceValue?.ToString()
            : null;
        var key = StrmProbeCacheKey.Compute(strmUrl!, fileSize, signature);
        var virtualId = StrmVirtualSourceFactory.DeriveStableId(key);
        if (!MediaSourceIdMatches(requested, itemId, virtualId))
        {
            return null;
        }

        var kind = IsNativeId(requested, itemId) ? "native" : "virtual";
        return new RedirectDecision(strmUrl!, clientName, string.Empty, kind);
    }

    /// <summary>
    /// 日志用 URL 脱敏：去掉 query/fragment（sign 在 query 里），只留 scheme://host/path。
    /// </summary>
    /// <param name="url">原始 URL。</param>
    /// <returns>脱敏后的 URL。</returns>
    internal static string RedactUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "<empty>";
        }

        var trimmed = url.Trim();
        var cut = trimmed.IndexOfAny(new[] { '?', '#' });
        return cut < 0 ? trimmed : trimmed.Substring(0, cut);
    }

    /// <summary>
    /// mediaSourceId 是否为本条目的原生 Id 或虚拟 Guid（大小写/连字符不敏感）。
    /// </summary>
    internal static bool MediaSourceIdMatches(string? requested, Guid itemId, string virtualIdN)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return false;
        }

        var norm = requested.Trim().Replace("-", string.Empty, StringComparison.Ordinal);
        if (string.Equals(norm, itemId.ToString("N"), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(norm, virtualIdN, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsNativeId(string? requested, Guid itemId)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return false;
        }

        return string.Equals(
            requested.Trim().Replace("-", string.Empty, StringComparison.Ordinal),
            itemId.ToString("N"),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsHttpUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var trimmed = url.Trim();
        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasTranscodeArgs(IDictionary<string, object?> args)
    {
        foreach (var name in TranscodeVetoArgs)
        {
            if (!args.TryGetValue(name, out var value) || value == null)
            {
                continue;
            }

            switch (value)
            {
                case string s when !string.IsNullOrWhiteSpace(s):
                    return true;
                case bool b when b:
                    return true;
                case int i when i != 0:
                    return true;
                case long l when l != 0:
                    return true;
                case float f when f != 0:
                    return true;
                case double d when d != 0:
                    return true;
            }
        }

        return false;
    }

    private static bool TryGetItemId(IDictionary<string, object?> args, out Guid itemId)
    {
        itemId = Guid.Empty;
        if (args == null || !args.TryGetValue("itemId", out var value) || value == null)
        {
            return false;
        }

        if (value is Guid guid)
        {
            itemId = guid;
            return itemId != Guid.Empty;
        }

        return Guid.TryParse(value.ToString(), out itemId) && itemId != Guid.Empty;
    }

    private bool TryResolveVisibleUser(
        IDictionary<object, object?> items,
        MediaBrowser.Controller.Entities.BaseItem item,
        out object? user)
    {
        user = null;
        try
        {
            if (items == null
                || !items.TryGetValue(StrmClientResolver.AuthorizationInfoItemsKey, out var cached)
                || cached is not AuthorizationInfo authInfo)
            {
                return false;
            }

            if (authInfo.User != null)
            {
                user = authInfo.User;
                return item.IsVisible(authInfo.User, false);
            }

            if (authInfo.UserId == Guid.Empty)
            {
                return false;
            }

            var resolved = _userManager.GetUserById(authInfo.UserId);
            if (resolved == null || !item.IsVisible(resolved, false))
            {
                return false;
            }

            user = resolved;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "strm 302 直跳用户可见性校验失败，回退原生管道");
            user = null;
            return false;
        }
    }
}

/// <summary>
/// 直跳决策：目标直链 + 诊断信息（日志用，不含 sign 原文）。
/// </summary>
internal sealed class RedirectDecision
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RedirectDecision"/> class.
    /// </summary>
    /// <param name="targetUrl">跳转目标直链。</param>
    /// <param name="clientName">客户端名称。</param>
    /// <param name="itemName">条目名称（解析时回填）。</param>
    /// <param name="sourceKind">源种类：native 或 virtual。</param>
    public RedirectDecision(string targetUrl, string? clientName, string itemName, string sourceKind)
    {
        TargetUrl = targetUrl;
        ClientName = clientName;
        ItemName = itemName;
        SourceKind = sourceKind;
    }

    /// <summary>Gets 跳转目标直链。</summary>
    public string TargetUrl { get; }

    /// <summary>Gets 客户端名称。</summary>
    public string? ClientName { get; }

    /// <summary>Gets or sets 条目名称。</summary>
    public string ItemName { get; set; }

    /// <summary>Gets 源种类：native 或 virtual。</summary>
    public string SourceKind { get; }
}
