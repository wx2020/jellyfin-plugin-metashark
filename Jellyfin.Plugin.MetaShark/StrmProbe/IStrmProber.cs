using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// 直链探针接口：对网盘直链做轻量探测，解析出可直连地址与内容元信息。
/// </summary>
public interface IStrmProber
{
    /// <summary>
    /// 探测指定 URL。
    /// </summary>
    /// <param name="url">待探测 URL。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>探针结果。</returns>
    Task<StrmProbeResult> ProbeAsync(string url, CancellationToken cancellationToken);
}
