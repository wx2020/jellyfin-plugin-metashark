using Jellyfin.Plugin.MetaShark.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jellyfin.Plugin.MetaShark.Test
{
    /// <summary>
    /// 图片代理地址构造策略的离线确定性测试。
    /// 只有豆瓣图床走插件图片代理，其它来源（MoviePilot 的 TMDB 图片）必须直出原地址，
    /// 否则会被代理端点的豆瓣白名单校验拒绝（404，识别/演员缩略图空白）。
    /// </summary>
    [TestClass]
    public class ImageProxyUrlPolicyTests
    {
        private const string BaseUrl = "https://jellyfin.example.com:5001";

        [TestMethod]
        public void Build_DoubanImage_UsesPluginProxy()
        {
            var url = "https://img3.doubanio.com/view/photo/s_ratio_poster/public/p2905021483.jpg";

            var result = ImageProxyUrlPolicy.Build(BaseUrl, url);

            Assert.AreEqual(
                $"{BaseUrl}/plugin/metashark/proxy/image/?url={System.Web.HttpUtility.UrlEncode(url)}",
                result);
        }

        [TestMethod]
        public void Build_DoubanImageWithSubdomain_UsesPluginProxy()
        {
            var result = ImageProxyUrlPolicy.Build(BaseUrl, "https://img1.doubanio.com/a.jpg");

            StringAssert.StartsWith(result, $"{BaseUrl}/plugin/metashark/proxy/image/?url=");
        }

        [TestMethod]
        public void Build_TmdbAbsoluteImage_ReturnsOriginalUrl()
        {
            var url = "https://image.tmdb.org/t/p/original/2LqaLgk4Z226KkgPJuiOQ58wvrm.jpg";

            Assert.AreEqual(url, ImageProxyUrlPolicy.Build(BaseUrl, url));
        }

        [TestMethod]
        public void Build_TmdbRelativePath_NormalizesToAbsoluteImageUrl()
        {
            Assert.AreEqual(
                ImageProxyUrlPolicy.TmdbImageBaseUrl + "/2LqaLgk4Z226KkgPJuiOQ58wvrm.jpg",
                ImageProxyUrlPolicy.Build(BaseUrl, "/2LqaLgk4Z226KkgPJuiOQ58wvrm.jpg"));
        }

        [TestMethod]
        public void Build_ProtocolRelativeUrl_NormalizesToHttps()
        {
            Assert.AreEqual(
                "https://image.tmdb.org/t/p/original/abc.jpg",
                ImageProxyUrlPolicy.Build(BaseUrl, "//image.tmdb.org/t/p/original/abc.jpg"));
        }

        [TestMethod]
        public void Build_OtherHost_ReturnsOriginalUrl()
        {
            var url = "https://example.com/poster.jpg";

            Assert.AreEqual(url, ImageProxyUrlPolicy.Build(BaseUrl, url));
        }

        [TestMethod]
        public void Build_EmptyUrl_ReturnsEmpty()
        {
            // 原实现会拼出一个 url 为空的代理地址（必然 404），现在直接返回空表示无图
            Assert.AreEqual(string.Empty, ImageProxyUrlPolicy.Build(BaseUrl, null));
            Assert.AreEqual(string.Empty, ImageProxyUrlPolicy.Build(BaseUrl, string.Empty));
            Assert.AreEqual(string.Empty, ImageProxyUrlPolicy.Build(BaseUrl, "   "));
        }

        [TestMethod]
        public void Build_TmdbImage_DoesNotGetProxyPrefix()
        {
            var result = ImageProxyUrlPolicy.Build(BaseUrl, "https://image.tmdb.org/t/p/w500/abc.jpg");

            Assert.IsFalse(result.Contains("/plugin/metashark/proxy/image/"));
        }
    }
}
