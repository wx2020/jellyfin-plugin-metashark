using System.Net;
using System.Reflection;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MetaShark.Configuration;


/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public const int MAX_CAST_MEMBERS = 15;
    public const int MAX_SEARCH_RESULT = 5;

    /// <summary>
    /// 插件版本
    /// </summary>
    public string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty;

    public string DoubanCookies { get; set; } = string.Empty;
    /// <summary>
    /// 豆瓣开启防封禁
    /// </summary>
    public bool EnableDoubanAvoidRiskControl { get; set; } = false;
    /// <summary>
    /// 豆瓣海报使用大图
    /// </summary>
    public bool EnableDoubanLargePoster { get; set; } = true;
    /// <summary>
    /// 豆瓣背景图使用原图
    /// </summary>
    public bool EnableDoubanBackdropRaw { get; set; } = false;
    /// <summary>
    /// 豆瓣图片代理地址
    /// </summary>
    public string DoubanImageProxyBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// 启用获取tmdb元数据
    /// </summary>
    public bool EnableTmdb { get; set; } = true;

    /// <summary>
    /// 启用显示tmdb搜索结果
    /// </summary>
    public bool EnableTmdbSearch { get; set; } = false;

    /// <summary>
    /// 启用tmdb自动匹配
    /// </summary>
    public bool EnableTmdbMatch { get; set; } = true;

    /// <summary>
    /// 启用tmdb获取背景图
    /// </summary>
    public bool EnableTmdbBackdrop { get; set; } = true;

    /// <summary>
    /// 启用tmdb获取商标
    /// </summary>
    public bool EnableTmdbLogo { get; set; } = true;
    
    /// <summary>
    /// 是否根据电影系列自动创建合集
    /// </summary>
    public bool EnableTmdbCollection { get; set; } = true;
    /// <summary>
    /// 启用tmdb获取成人内容
    /// </summary>
    public bool EnableTmdbAdult { get; set; } = false;
    /// <summary>
    /// 是否获取tmdb分级信息
    /// </summary>
    public bool EnableTmdbOfficialRating { get; set; } = true;
    /// <summary>
    /// tmdb api key
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;
    /// <summary>
    /// tmdb api host
    /// </summary>
    public string TmdbHost { get; set; } = string.Empty;
    /// <summary>
    /// 代理服务器类型，0-禁用，1-http，2-https，3-socket5
    /// </summary>
    public string TmdbProxyType { get; set; } = string.Empty;
    /// <summary>
    /// 代理服务器host
    /// </summary>
    public string TmdbProxyPort { get; set; } = string.Empty;
    /// <summary>
    /// 代理服务器端口
    /// </summary>
    public string TmdbProxyHost { get; set; } = string.Empty;


    public IWebProxy GetTmdbWebProxy()
    {

        if (!string.IsNullOrEmpty(TmdbProxyType))
        {
            return new WebProxy($"{TmdbProxyType}://{TmdbProxyHost}:{TmdbProxyPort}", true);
        }

        return null;
    }

    /// <summary>
    /// 第三方客户端名单（取流 302 直跳生效范围），逗号/分号/换行分隔，大小写不敏感。默认仅 Yamby。
    /// </summary>
    public string StrmProbeClientWhitelist { get; set; } = "Yamby";

    /// <summary>
    /// 入库媒体探测：新增 strm 入库去抖后在后台做一次完整远程探测，把流信息写入媒体库并预填 ffprobe 缓存。默认关闭。与取流 302 直跳相互独立。
    /// 同时是"每日扫描缺失流信息"定时任务的执行开关。
    /// </summary>
    public bool EnableStrmProbeLibraryRefresh { get; set; } = false;

    /// <summary>
    /// 虚拟季孤儿集修复总开关：统一控制"扁平 strm 剧集虚拟季归位"的全部行为——
    /// ① 入库解析期把无季文件夹的虚拟季季号兜底为 1（对所有元数据来源生效）；
    /// ② 每集元数据修正时同款兜底，避免先建 null 季再删造成孤儿；
    /// ③ 每次完整扫描结束后，把仍没挂到季的 strm 剧集重绑到对应虚拟季（纯本地，无网络）。
    /// 关闭后回退到上游原始行为。默认开启。修改即时生效。
    /// </summary>
    public bool EnableVirtualSeasonOrphanFix { get; set; } = true;

    /// <summary>
    /// 每日定时探测时间（HH:mm，24 小时制）。留空则每日任务不自动执行（仍可手动运行）。修改后需重启生效。
    /// </summary>
    public string StrmProbeDailyScanTime { get; set; } = string.Empty;

    /// <summary>
    /// ffprobe 缓存有效期（天）。缓存 key 由直链 URL 派生，URL 变化即天然失效，本值仅作同 URL 换内容的安全阀。
    /// 默认 90；填 0 或负数表示永不过期。命中后会自动滑动续期。修改即时生效。
    /// </summary>
    public int StrmProbeCacheTtlDays { get; set; } = 90;

    /// <summary>
    /// 取流 302 直跳：白名单第三方客户端的纯静态取流（/Videos/…/stream.mkv?Static=true）直接 302
    /// 到本地 .strm 文件首行的直链，播放器直连该地址、服务端不再代理字节。默认关闭；转码/混流请求不受影响。
    /// </summary>
    public bool EnableStrmDirectRedirect { get; set; } = false;

    /// <summary>
    /// 已刮削 strm 条目（电影/剧集）在 PlaybackInfo（详情页）触发的刷新中短路元数据提供者：不再查询豆瓣/TMDB，
    /// 直接返回空结果由 core 保留库内数据。仅作用于 PlaybackInfo 路径，手动刷新元数据不受影响。默认关闭。
    /// </summary>
    public bool EnableSkipOnlineMetadataOnPlaybackInfo { get; set; } = false;

    /// <summary>
    /// 启用 MoviePilot 豆瓣数据优先通道。关闭或未配置时走原有豆瓣直连链路。
    /// </summary>
    public bool EnableMoviePilot { get; set; } = false;

    /// <summary>
    /// MoviePilot 实例地址，如 http://192.168.5.10:3001。
    /// </summary>
    public string MoviePilotBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// MoviePilot API_TOKEN（管理员级 secret，仅保存在本地配置中，不要提交到仓库）。
    /// </summary>
    public string MoviePilotApiToken { get; set; } = string.Empty;

    /// <summary>
    /// 启动画面媒体库白名单总开关：开启后 Jellyfin 自动生成的启动画面（/Branding/Splashscreen）
    /// 只会使用白名单媒体库中的电影/剧集海报与横图。默认关闭（保持 core 原始行为）。
    /// </summary>
    public bool EnableSplashscreenLibraryFilter { get; set; } = false;

    /// <summary>
    /// 允许出现在自动启动画面中的媒体库白名单（库名称或库 GUID，分号/逗号/换行分隔）。
    /// 开启过滤但此处为空或全部无法匹配时，按 fail-safe 不生成启动画面（避免泄露未授权库内容）。
    /// </summary>
    public string SplashscreenLibraryWhitelist { get; set; } = string.Empty;
}
