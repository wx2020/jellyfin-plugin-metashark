using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.Splashscreen;

/// <summary>
/// <c>IImageEncoder</c> 装饰器：拦截 <c>CreateSplashscreen</c>，让 Jellyfin 自动生成的启动画面
/// 只使用白名单媒体库中的电影/剧集图片。其余方法（含转码、trickplay、拼图等）全部直通 core 实现。
/// <para>
/// core 的 <c>SplashscreenPostScanTask</c> 只给图片路径、且没有任何"按库筛选"的配置（源码两处 TODO 未实现），
/// 也无法通过插件移除该任务（它由反射 + ActivatorUtilities 实例化）。因此这里在过滤开启时忽略入参，
/// 用带 <c>TopParentIds</c> 的查询自行取料后交给内层编码器；过滤关闭时原样转发，行为与 core 完全一致。
/// </para>
/// <para>
/// 此外 <see cref="Generate"/> 提供"不等完整库扫描、立即重新生成启动画面"的入口，由
/// <c>RefreshSplashscreenTask</c> 计划任务调用；core 原有的"扫描媒体库后自动生成"逻辑保留不变。
/// </para>
/// </summary>
public class SplashscreenLibraryFilterProxy : DispatchProxy
{
    private IImageEncoder _inner = null!;
    private ILogger _logger = null!;
    private Func<ILibraryManager>? _libraryManagerFactory;

    /// <summary>
    /// 测试用配置覆盖（沿用项目 <c>TestConfigOverride</c> 模式，保证单测离线确定性）。
    /// </summary>
    internal (bool Enabled, string? Raw)? TestConfigOverride { get; set; }

    /// <summary>
    /// 测试用媒体库列表覆盖。
    /// </summary>
    internal Func<List<VirtualFolderInfo>>? FoldersOverride { get; set; }

    /// <summary>
    /// 测试用取料覆盖。
    /// </summary>
    internal Func<ImageType, Guid[]?, List<string>>? PathSourceOverride { get; set; }

    /// <summary>
    /// 用装饰器包装已有的 <c>IImageEncoder</c> 实例。
    /// </summary>
    /// <param name="inner">core 的真实编码器。</param>
    /// <param name="libraryManagerFactory">惰性解析 <c>ILibraryManager</c> 的工厂（避免构造环）。</param>
    /// <param name="logger">日志。</param>
    /// <returns>装饰后的编码器。</returns>
    public static IImageEncoder Create(IImageEncoder inner, Func<ILibraryManager> libraryManagerFactory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(libraryManagerFactory);
        ArgumentNullException.ThrowIfNull(logger);

        var proxy = Create<IImageEncoder, SplashscreenLibraryFilterProxy>();
        var decorator = (SplashscreenLibraryFilterProxy)(object)proxy;
        decorator._inner = inner;
        decorator._libraryManagerFactory = libraryManagerFactory;
        decorator._logger = logger;
        return proxy;
    }

    /// <summary>
    /// 把容器中已注册的 <c>IImageEncoder</c> 替换为白名单过滤装饰版本
    /// （插件 ServiceRegistrator 在 core 注册之后执行）。
    /// <para>
    /// 同时以具体类型 <see cref="SplashscreenLibraryFilterProxy"/> 注册同一个实例，
    /// 供"刷新启动画面"计划任务注入并直接调用 <see cref="Generate"/>。
    /// </para>
    /// </summary>
    /// <param name="services">服务集合。</param>
    public static void Decorate(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        ServiceDescriptor? innerDescriptor = null;
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(IImageEncoder))
            {
                innerDescriptor = descriptor;
            }
        }

        if (innerDescriptor == null)
        {
            return;
        }

        services.Remove(innerDescriptor);
        var captured = innerDescriptor;
        services.AddSingleton<SplashscreenLibraryFilterProxy>((sp) =>
        {
            IImageEncoder inner =
                captured.ImplementationInstance as IImageEncoder
                ?? (captured.ImplementationFactory != null
                    ? (IImageEncoder)captured.ImplementationFactory(sp)
                    : (IImageEncoder)ActivatorUtilities.CreateInstance(sp, captured.ImplementationType!));
            var encoder = Create(
                inner,
                () => sp.GetRequiredService<ILibraryManager>(),
                sp.GetRequiredService<ILogger<SplashscreenLibraryFilterProxy>>());
            return (SplashscreenLibraryFilterProxy)(object)encoder;
        });
        services.AddSingleton<IImageEncoder>(sp => (IImageEncoder)sp.GetRequiredService<SplashscreenLibraryFilterProxy>());
    }

    /// <summary>
    /// 测试用工厂：不访问真实容器。
    /// </summary>
    /// <param name="inner">内层编码器。</param>
    /// <param name="logger">日志。</param>
    /// <returns>装饰后的编码器（可强转为 <see cref="SplashscreenLibraryFilterProxy"/> 设置测试覆盖）。</returns>
    internal static IImageEncoder CreateForTest(IImageEncoder inner, ILogger logger)
    {
        return Create(inner, () => throw new InvalidOperationException("测试不应访问真实 ILibraryManager"), logger);
    }

    /// <summary>
    /// 拆分白名单字符串（分号/逗号/换行分隔，去空白）。
    /// </summary>
    /// <param name="raw">原始配置值。</param>
    /// <returns>非空 token 列表。</returns>
    internal static string[] Tokenize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw
            .Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 0)
            .ToArray();
    }

    /// <summary>
    /// 把白名单 token（库 GUID 或库名称）解析为库的 <c>ItemId</c>（即条目的 TopParentId）。
    /// GUID 优先且必须能在现有库中命中；名称不区分大小写。无法匹配的 token 静默忽略。
    /// </summary>
    /// <param name="raw">白名单配置值。</param>
    /// <param name="folders">当前媒体库列表。</param>
    /// <returns>去重后的库 GUID 数组；空表示没有可用库（fail-safe 不生成）。</returns>
    internal static Guid[] ResolveWhitelist(string? raw, IReadOnlyList<VirtualFolderInfo> folders)
    {
        var tokens = Tokenize(raw);
        if (tokens.Length == 0 || folders.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        var byGuid = new Dictionary<Guid, VirtualFolderInfo>();
        var byName = new Dictionary<string, VirtualFolderInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (Guid.TryParse(folder.ItemId, out var folderId))
            {
                byGuid[folderId] = folder;
            }

            if (!string.IsNullOrEmpty(folder.Name))
            {
                byName[folder.Name] = folder;
            }
        }

        var result = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var token in tokens)
        {
            VirtualFolderInfo? match = null;
            if (Guid.TryParse(token, out var tokenId))
            {
                byGuid.TryGetValue(tokenId, out match);
            }
            else
            {
                byName.TryGetValue(token, out match);
            }

            if (match != null
                && Guid.TryParse(match.ItemId, out var matchedId)
                && seen.Add(matchedId))
            {
                result.Add(matchedId);
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// 构造与 core <c>SplashscreenPostScanTask</c> 同款的取料查询；<paramref name="topParentIds"/>
    /// 非空时用 <c>TopParentIds</c> 限制到白名单库，为 null 时不限库（用于"刷新启动画面"任务在过滤关闭时的路径）。
    /// 刻意不设 <c>MaxParentalRating</c>：只认白名单，避免"未分级放行"导致白名单内容被误滤。
    /// </summary>
    /// <param name="imageType">图片类型。</param>
    /// <param name="topParentIds">允许的库 ID；null 表示不限库。</param>
    /// <returns>查询对象。</returns>
    internal static InternalItemsQuery BuildQuery(ImageType imageType, Guid[]? topParentIds)
    {
        return new InternalItemsQuery
        {
            CollapseBoxSetItems = false,
            Recursive = true,
            DtoOptions = new DtoOptions(false),
            ImageTypes = new[] { imageType },
            Limit = 30,
            OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            TopParentIds = topParentIds ?? Array.Empty<Guid>(),
        };
    }

    /// <summary>
    /// 立即按当前配置重新生成启动画面（供"刷新启动画面"计划任务调用，无需跑完整库扫描）。
    /// 过滤开启时只取白名单库图片（空白名单 fail-safe 不生成）；关闭时取全库图片（core 同款选料）。
    /// </summary>
    public void Generate()
    {
        var (enabled, raw) = ResolveConfig();
        Guid[]? allowed = null;
        if (enabled)
        {
            var folders = FoldersOverride?.Invoke()
                ?? _libraryManagerFactory?.Invoke().GetVirtualFolders()
                ?? new List<VirtualFolderInfo>();
            allowed = ResolveWhitelist(raw, folders);
            if (allowed.Length == 0)
            {
                _logger.LogInformation(
                    "启动画面媒体库白名单为空或全部无法匹配（配置：{Raw}），按 fail-safe 不生成启动画面",
                    raw);
                _inner.CreateSplashscreen(Array.Empty<string>(), Array.Empty<string>());
                return;
            }
        }

        var posters = CollectPaths(ImageType.Primary, allowed);
        var backdrops = CollectPaths(ImageType.Thumb, allowed);
        if (backdrops.Count == 0)
        {
            backdrops = CollectPaths(ImageType.Backdrop, allowed);
        }

        if (enabled)
        {
            _logger.LogInformation(
                "启动画面媒体库白名单生效：允许 {LibraryCount} 个库，海报 {PosterCount} 张、横图 {BackdropCount} 张",
                allowed!.Length,
                posters.Count,
                backdrops.Count);
        }
        else
        {
            _logger.LogInformation(
                "启动画面已刷新：海报 {PosterCount} 张、横图 {BackdropCount} 张",
                posters.Count,
                backdrops.Count);
        }

        _inner.CreateSplashscreen(posters, backdrops);
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod != null
            && string.Equals(targetMethod.Name, nameof(IImageEncoder.CreateSplashscreen), StringComparison.Ordinal))
        {
            InterceptCreateSplashscreen(
                args![0] as IReadOnlyList<string> ?? Array.Empty<string>(),
                args[1] as IReadOnlyList<string> ?? Array.Empty<string>());
            return null;
        }

        return targetMethod!.Invoke(_inner, args);
    }

    private (bool Enabled, string Raw) ResolveConfig()
    {
        if (TestConfigOverride.HasValue)
        {
            return (TestConfigOverride.Value.Enabled, TestConfigOverride.Value.Raw ?? string.Empty);
        }

        var config = Plugin.Instance?.Configuration;
        return (
            config?.EnableSplashscreenLibraryFilter ?? false,
            config?.SplashscreenLibraryWhitelist ?? string.Empty);
    }

    private void InterceptCreateSplashscreen(IReadOnlyList<string> posters, IReadOnlyList<string> backdrops)
    {
        var (enabled, _) = ResolveConfig();
        if (!enabled)
        {
            _inner.CreateSplashscreen(posters, backdrops);
            return;
        }

        // 过滤开启时忽略 core 传入的图片路径，按白名单重新取料。
        Generate();
    }

    private List<string> CollectPaths(ImageType imageType, Guid[]? topParentIds)
    {
        if (PathSourceOverride != null)
        {
            return PathSourceOverride(imageType, topParentIds);
        }

        var manager = _libraryManagerFactory!.Invoke();
        var items = manager.GetItemList(BuildQuery(imageType, topParentIds));
        var paths = new List<string>(items.Count);
        foreach (var item in items)
        {
            var path = item.GetImages(imageType).FirstOrDefault()?.Path;
            if (!string.IsNullOrEmpty(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }
}
