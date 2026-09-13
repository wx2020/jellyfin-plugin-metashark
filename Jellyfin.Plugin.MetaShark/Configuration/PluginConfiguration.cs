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
    /// strm 探针缓存预热总开关。关闭时整个加速链路（缓存读取、虚拟直连、后台预热）全部停用。
    /// </summary>
    public bool EnableStrmProbeWarmup { get; set; } = false;

    /// <summary>
    /// 无缓存时返回直链（仅白名单第三方客户端生效）。关闭时缓存未命中走原生行为（但仍会后台预热写缓存）。
    /// </summary>
    public bool EnableStrmProbeDirectOnCacheMiss { get; set; } = true;

    /// <summary>
    /// 第三方客户端名单，逗号/分号/换行分隔，大小写不敏感。默认仅 Yamby。
    /// </summary>
    public string StrmProbeClientWhitelist { get; set; } = "Yamby";

    /// <summary>
    /// 入库真探写入库：新增 strm 入库去抖后在后台做一次完整远程探测，把流信息写入媒体库并预填 ffprobe 缓存。默认关闭。
    /// </summary>
    public bool EnableStrmProbeLibraryRefresh { get; set; } = false;

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
}
