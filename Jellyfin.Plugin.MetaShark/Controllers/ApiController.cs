using System.Threading;
using System.Net.Http;
using System.IO;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Model;

namespace Jellyfin.Plugin.MetaShark.Controllers
{
    [ApiController]
    [AllowAnonymous]
    [Route("/plugin/metashark")]
    public class ApiController : ControllerBase
    {
        private readonly DoubanApi _doubanApi;

        /// <summary>
        /// Initializes a new instance of the <see cref="ApiController"/> class.
        /// </summary>
        /// <param name="doubanApi">The <see cref="DoubanApi"/>.</param>
        public ApiController(DoubanApi doubanApi)
        {
            this._doubanApi = doubanApi;
        }


        /// <summary>
        /// 代理访问图片.
        /// </summary>
        [Route("proxy/image")]
        [HttpGet]
        public async Task<Stream> ProxyImage(string url)
        {
            // 只允许代理豆瓣图床地址，同时防止端点被滥用为开放代理
            if (string.IsNullOrEmpty(url) || !DoubanApi.IsDoubanImageUrl(url))
            {
                throw new ResourceNotFoundException();
            }

            // 统一走限速下载，避免高频请求触发豆瓣图床风控
            HttpResponseMessage response;
            MemoryStream stream;
            using (response = await this._doubanApi.GetImageAsync(url, this.HttpContext.RequestAborted).ConfigureAwait(false))
            {
                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                stream = new MemoryStream(bytes);
            }

            Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType != null)
            {
                Response.ContentType = response.Content.Headers.ContentType.ToString();
            }
            Response.ContentLength = stream.Length;

            return stream;
        }

        /// <summary>
        /// 检查豆瓣cookie是否失效.
        /// </summary>
        [Route("douban/checklogin")]
        [HttpGet]
        public async Task<ApiResult> CheckDoubanLogin()
        {
            var loginInfo = await this._doubanApi.GetLoginInfoAsync(CancellationToken.None).ConfigureAwait(false);
            return new ApiResult(loginInfo.IsLogined ? 1 : 0, loginInfo.Name);
        }
    }
}
