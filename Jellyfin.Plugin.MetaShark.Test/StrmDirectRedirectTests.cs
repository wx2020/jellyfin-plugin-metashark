using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MetaShark.StrmProbe;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class StrmDirectRedirectTests
    {
        private static readonly Guid ItemId = Guid.Parse("c9e3b155-d11a-63e3-5949-d45d0decd90c");
        private const string StrmUrl = "https://openlist.example.com:5001/d/189/movie/test.mkv?sign=abc123";
        private const long FileSize = 288;
        private const string Signature = "639244000000000000";

        private static Dictionary<string, object?> BaseArgs(string? mediaSourceId = null)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["itemId"] = ItemId,
                ["container"] = "mkv",
                ["static"] = true,
                ["mediaSourceId"] = mediaSourceId ?? ItemId.ToString("D"),
            };
        }

        private static IReadOnlyList<string> Whitelist(params string[] names)
        {
            return StrmClientPolicy.ParseWhitelist(string.Join(";", names));
        }

        [TestMethod]
        public void Decide_NativeId_DashedAndCompact_BothRedirect()
        {
            var whitelist = Whitelist("Yamby", "Lenna");

            var dashed = StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStream", BaseArgs(ItemId.ToString("D")),
                "Lenna", whitelist, true, ItemId, StrmUrl, FileSize, Signature);
            Assert.IsNotNull(dashed);
            Assert.AreEqual(StrmUrl, dashed.TargetUrl);
            Assert.AreEqual("native", dashed.SourceKind);

            var compact = StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStream", BaseArgs(ItemId.ToString("N")),
                "lenna", whitelist, true, ItemId, StrmUrl, FileSize, Signature);
            Assert.IsNotNull(compact);
            Assert.AreEqual("native", compact.SourceKind);
        }

        [TestMethod]
        public void Decide_VirtualId_RedirectsAsVirtual()
        {
            var key = StrmProbeCacheKey.Compute(StrmUrl, FileSize, Signature);
            var virtualId = StrmVirtualSourceFactory.DeriveStableId(key);
            var whitelist = Whitelist("Lenna");

            var decision = StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStream", BaseArgs(virtualId),
                "Lenna", whitelist, true, ItemId, StrmUrl, FileSize, Signature);
            Assert.IsNotNull(decision);
            Assert.AreEqual(StrmUrl, decision.TargetUrl);
            Assert.AreEqual("virtual", decision.SourceKind);
        }

        [TestMethod]
        public void Decide_MasterSwitchOff_NoRedirect()
        {
            var decision = StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStream", BaseArgs(),
                "Lenna", Whitelist("Lenna"), false, ItemId, StrmUrl, FileSize, Signature);
            Assert.IsNull(decision);
        }

        [TestMethod]
        public void Decide_NonVideoOrWrongActionOrMethod_NoRedirect()
        {
            var whitelist = Whitelist("Lenna");
            Assert.IsNull(StrmDirectRedirectFilter.Decide(
                "GET", "Images", "GetVideoStream", BaseArgs(),
                "Lenna", whitelist, true, ItemId, StrmUrl, FileSize, Signature));
            Assert.IsNull(StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetMasterHlsPlaylist", BaseArgs(),
                "Lenna", whitelist, true, ItemId, StrmUrl, FileSize, Signature));
            Assert.IsNull(StrmDirectRedirectFilter.Decide(
                "POST", "Videos", "GetVideoStream", BaseArgs(),
                "Lenna", whitelist, true, ItemId, StrmUrl, FileSize, Signature));
        }

        [TestMethod]
        public void Decide_StaticFalseOrMissing_NoRedirect()
        {
            var whitelist = Whitelist("Lenna");
            var missing = BaseArgs();
            missing.Remove("static");
            Assert.IsNull(StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStream", missing,
                "Lenna", whitelist, true, ItemId, StrmUrl, FileSize, Signature));

            var notStatic = BaseArgs();
            notStatic["static"] = false;
            Assert.IsNull(StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStream", notStatic,
                "Lenna", whitelist, true, ItemId, StrmUrl, FileSize, Signature));
        }

        [TestMethod]
        public void Decide_TranscodeArgsPresent_NoRedirect()
        {
            var whitelist = Whitelist("Lenna");

            foreach (var veto in new (string Name, object Value)[]
            {
                ("videoCodec", "h264"),
                ("audioCodec", "aac"),
                ("transcodeReasons", "ContainerNotSupported"),
                ("liveStreamId", "abc"),
                ("width", 1920),
                ("videoBitRate", 8000000),
                ("enableMpegtsM2TsMode", true),
                ("params", "tag=1"),
            })
            {
                var args = BaseArgs();
                args[veto.Name] = veto.Value;
                Assert.IsNull(
                    StrmDirectRedirectFilter.Decide(
                        "GET", "Videos", "GetVideoStream", args,
                        "Lenna", whitelist, true, ItemId, StrmUrl, FileSize, Signature),
                    "veto arg should block redirect: " + veto.Name);
            }

            // 无害参数（取流身份/播放会话）不阻断
            var harmless = BaseArgs();
            harmless["deviceId"] = "5EC1259C-EC8B-438C-9AD4-8805F4ED5337";
            harmless["playSessionId"] = "xyz";
            harmless["tag"] = "38d1b77d";
            Assert.IsNotNull(StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStream", harmless,
                "Lenna", whitelist, true, ItemId, StrmUrl, FileSize, Signature));
        }

        [TestMethod]
        public void Decide_NativeOrUnknownOrUnlistedClient_NoRedirect()
        {
            var whitelist = Whitelist("Yamby", "Lenna");
            Assert.IsNull(StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStream", BaseArgs(),
                null, whitelist, true, ItemId, StrmUrl, FileSize, Signature));
            Assert.IsNull(StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStream", BaseArgs(),
                "Jellyfin Web", whitelist, true, ItemId, StrmUrl, FileSize, Signature));
            Assert.IsNull(StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStream", BaseArgs(),
                "Infuse", whitelist, true, ItemId, StrmUrl, FileSize, Signature));
        }

        [TestMethod]
        public void Decide_MismatchedOrMissingMediaSourceId_NoRedirect()
        {
            var whitelist = Whitelist("Lenna");
            Assert.IsNull(StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStream", BaseArgs(Guid.NewGuid().ToString("N")),
                "Lenna", whitelist, true, ItemId, StrmUrl, FileSize, Signature));

            var missing = BaseArgs();
            missing["mediaSourceId"] = null;
            Assert.IsNull(StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStream", missing,
                "Lenna", whitelist, true, ItemId, StrmUrl, FileSize, Signature));
        }

        [TestMethod]
        public void Decide_NonHttpTarget_NoRedirect()
        {
            var whitelist = Whitelist("Lenna");
            Assert.IsNull(StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStream", BaseArgs(),
                "Lenna", whitelist, true, ItemId, "file:///strm/movie.mkv", FileSize, Signature));
            Assert.IsNull(StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStream", BaseArgs(),
                "Lenna", whitelist, true, ItemId, "  ", FileSize, Signature));
        }

        [TestMethod]
        public void Decide_ByContainerAction_Redirects()
        {
            // /Videos/{id}/stream.mkv 走的是 GetVideoStreamByContainer（core 10.11 实测），不是 GetVideoStream。
            var decision = StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStreamByContainer", BaseArgs(),
                "Lenna", Whitelist("Lenna"), true, ItemId, StrmUrl, FileSize, Signature);
            Assert.IsNotNull(decision);
            Assert.AreEqual(StrmUrl, decision.TargetUrl);
            Assert.AreEqual("native", decision.SourceKind);
        }

        [TestMethod]
        public void GetClaim_FromClaimsPrincipal_Normalizes()
        {
            var identity = new System.Security.Claims.ClaimsIdentity("TestAuth");
            identity.AddClaim(new System.Security.Claims.Claim("Jellyfin-Client", "  Lenna  "));
            identity.AddClaim(new System.Security.Claims.Claim("Jellyfin-UserId", ItemId.ToString("N")));
            var principal = new System.Security.Claims.ClaimsPrincipal(identity);

            Assert.AreEqual("Lenna", StrmDirectRedirectFilter.GetClaim(principal, "Jellyfin-Client"));
            Assert.AreEqual(ItemId.ToString("N"), StrmDirectRedirectFilter.GetClaim(principal, "Jellyfin-UserId"));
            Assert.IsNull(StrmDirectRedirectFilter.GetClaim(principal, "Jellyfin-DeviceId"));
            Assert.IsNull(StrmDirectRedirectFilter.GetClaim(new System.Security.Claims.ClaimsPrincipal(), "Jellyfin-Client"));
            Assert.IsNull(StrmDirectRedirectFilter.GetClaim(null, "Jellyfin-Client"));
        }

        [TestMethod]
        public void Decide_HeadMethod_Redirects()
        {
            var decision = StrmDirectRedirectFilter.Decide(
                "HEAD", "Videos", "GetVideoStream", BaseArgs(),
                "Yamby", Whitelist("Yamby"), true, ItemId, StrmUrl, FileSize, Signature);
            Assert.IsNotNull(decision);
        }

        [TestMethod]
        public void RedactUrl_StripsQueryAndFragment()
        {
            Assert.AreEqual(
                "https://openlist.example.com:5001/d/189/movie/test.mkv",
                StrmDirectRedirectFilter.RedactUrl("https://openlist.example.com:5001/d/189/movie/test.mkv?sign=abc123"));
            Assert.AreEqual(
                "https://openlist.example.com:5001/d/189/movie/test.mkv",
                StrmDirectRedirectFilter.RedactUrl("https://openlist.example.com:5001/d/189/movie/test.mkv?sign=abc#frag"));
            Assert.AreEqual("<empty>", StrmDirectRedirectFilter.RedactUrl("  "));
            Assert.AreEqual("<empty>", StrmDirectRedirectFilter.RedactUrl(null));
        }

        [TestMethod]
        public void MediaSourceIdMatches_NormalizesDashesAndCase()
        {
            var key = StrmProbeCacheKey.Compute(StrmUrl, FileSize, Signature);
            var virtualId = StrmVirtualSourceFactory.DeriveStableId(key);

            Assert.IsTrue(StrmDirectRedirectFilter.MediaSourceIdMatches(ItemId.ToString("D"), ItemId, virtualId));
            Assert.IsTrue(StrmDirectRedirectFilter.MediaSourceIdMatches(ItemId.ToString("N").ToUpperInvariant(), ItemId, virtualId));
            Assert.IsTrue(StrmDirectRedirectFilter.MediaSourceIdMatches(virtualId.ToUpperInvariant(), ItemId, virtualId));
            Assert.IsFalse(StrmDirectRedirectFilter.MediaSourceIdMatches(Guid.NewGuid().ToString("N"), ItemId, virtualId));
            Assert.IsFalse(StrmDirectRedirectFilter.MediaSourceIdMatches(null, ItemId, virtualId));
        }
    }
}
