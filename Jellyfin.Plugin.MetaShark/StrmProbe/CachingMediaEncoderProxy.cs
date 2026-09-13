using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// <c>IMediaEncoder</c> 缓存装饰器：拦截 <c>GetMediaInfo</c>，仅对 http/https 远程探测结果做本地缓存，
/// 使 strm 条目重复的远程内容探测不再发起真实 ffprobe。本地文件探测一律直通。
/// 缓存 key 仅由请求 URL 派生：URL 变化即天然未命中；TTL 仅作同 URL 换内容的安全阀。
/// </summary>
public class CachingMediaEncoderProxy : DispatchProxy
{
    private IMediaEncoder _inner = null!;
    private IMediaInfoProbeCacheStore _store = null!;
    private ILogger<CachingMediaEncoderProxy> _logger = null!;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _flights = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

    /// <summary>
    /// 用装饰器包装已有的 <c>IMediaEncoder</c> 实例。
    /// </summary>
    /// <param name="inner">core 的真实编码器。</param>
    /// <param name="store">ffprobe 结果缓存。</param>
    /// <param name="logger">日志。</param>
    /// <returns>装饰后的编码器。</returns>
    public static IMediaEncoder Create(IMediaEncoder inner, IMediaInfoProbeCacheStore store, ILogger<CachingMediaEncoderProxy> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        var proxy = Create<IMediaEncoder, CachingMediaEncoderProxy>();
        var decorator = (CachingMediaEncoderProxy)(object)proxy;
        decorator._inner = inner;
        decorator._store = store;
        decorator._logger = logger;
        return proxy;
    }

    /// <summary>
    /// 把容器中已注册的 <c>IMediaEncoder</c> 替换为缓存装饰版本（插件 ServiceRegistrator 在 core 注册之后执行）。
    /// </summary>
    /// <param name="services">服务集合。</param>
    public static void Decorate(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        ServiceDescriptor? innerDescriptor = null;
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(IMediaEncoder))
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
        services.AddSingleton<IMediaEncoder>((sp) =>
        {
            IMediaEncoder inner =
                captured.ImplementationInstance as IMediaEncoder
                ?? (captured.ImplementationFactory != null
                    ? (IMediaEncoder)captured.ImplementationFactory(sp)
                    : (IMediaEncoder)ActivatorUtilities.CreateInstance(sp, captured.ImplementationType!));
            return Create(
                inner,
                sp.GetRequiredService<IMediaInfoProbeCacheStore>(),
                sp.GetRequiredService<ILogger<CachingMediaEncoderProxy>>());
        });
    }

    /// <summary>
    /// 从探测请求中提取可缓存的远程 URL。非 http/https 一律返回 false（直通）。
    /// </summary>
    /// <param name="request">探测请求（可为空）。</param>
    /// <param name="url">提取到的 URL。</param>
    /// <returns>可缓存返回 true。</returns>
    internal static bool TryGetProbeUrl(MediaInfoRequest? request, out string url)
    {
        url = string.Empty;
        var path = request?.MediaSource?.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        path = path.Trim();
        if (!path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        url = path;
        return true;
    }

    /// <summary>
    /// 由请求 URL 计算缓存 key（SHA256 十六进制小写）。
    /// </summary>
    /// <param name="url">请求 URL。</param>
    /// <returns>缓存 key。</returns>
    internal static string ComputeKey(string url)
    {
        var bytes = Encoding.UTF8.GetBytes((url ?? string.Empty).Trim());
#if NET9_0_OR_GREATER
        var hash = SHA256.HashData(bytes);
#else
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(bytes);
#endif
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod != null
            && string.Equals(targetMethod.Name, nameof(IMediaEncoder.GetMediaInfo), StringComparison.Ordinal)
            && args is { Length: 2 }
            && args[0] is MediaInfoRequest request
            && args[1] is CancellationToken cancellationToken)
        {
            return InterceptGetMediaInfoAsync(request, cancellationToken);
        }

        return targetMethod!.Invoke(_inner, args);
    }

    private async Task<MediaInfo> InterceptGetMediaInfoAsync(MediaInfoRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetProbeUrl(request, out var url))
        {
            return await _inner.GetMediaInfo(request, cancellationToken).ConfigureAwait(false);
        }

        var key = ComputeKey(url);
        if (TryReadCached(key, out var cached) && cached != null)
        {
            _logger.LogDebug("ffprobe 缓存命中（装饰器） key={CacheKey}", key);
            return cached;
        }

        // 单飞：同一 URL 的并发探测只放行一个，避免客户端双发 PlaybackInfo 时重复 ffprobe。
        var gate = _flights.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryReadCached(key, out cached) && cached != null)
            {
                return cached;
            }

            var info = await _inner.GetMediaInfo(request, cancellationToken).ConfigureAwait(false);
            var now = DateTime.UtcNow;
            _store.Set(new MediaInfoProbeCacheEntry
            {
                Key = key,
                Url = url,
                MediaInfoJson = JsonSerializer.Serialize(info),
                ProbedAtUtc = now,
                ExpiresAtUtc = now.Add(StrmProbeConstants.DefaultTtl),
            });
            return info;
        }
        finally
        {
            gate.Release();
            _flights.TryRemove(new KeyValuePair<string, SemaphoreSlim>(key, gate));
        }
    }

    private bool TryReadCached(string key, out MediaInfo? mediaInfo)
    {
        mediaInfo = null;
        MediaInfoProbeCacheEntry? entry;
        try
        {
            entry = _store.TryGet(key, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ffprobe 缓存读取失败，按未命中处理 key={CacheKey}", key);
            return false;
        }

        if (entry == null || string.IsNullOrWhiteSpace(entry.MediaInfoJson))
        {
            return false;
        }

        try
        {
            mediaInfo = JsonSerializer.Deserialize<MediaInfo>(entry.MediaInfoJson);
            return mediaInfo != null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ffprobe 缓存反序列化失败，按未命中处理 key={CacheKey}", key);
            return false;
        }
    }
}
