using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Providers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jellyfin.Plugin.MetaShark.Test
{
    /// <summary>
    /// 虚拟季孤儿集修复（总开关）的离线确定性测试：纯函数兜底逻辑与配置默认值。
    /// </summary>
    [TestClass]
    public class VirtualSeasonFallbackTests
    {
        [TestMethod]
        public void ResolveVirtualSeasonNumber_VirtualPath_DefaultsToOne()
        {
            // 无季号 + 空路径 → 兜底 1（虚拟季），对所有元数据来源生效
            Assert.AreEqual(1, SeasonProvider.ResolveVirtualSeasonNumber(null, null, false, _ => null));
            Assert.AreEqual(1, SeasonProvider.ResolveVirtualSeasonNumber(null, string.Empty, true, _ => null));
        }

        [TestMethod]
        public void ResolveVirtualSeasonNumber_NonEmptyPath_GuessesWhenAllowed()
        {
            Assert.AreEqual(2, SeasonProvider.ResolveVirtualSeasonNumber(null, "/tv/白夜追凶/第2季", true, _ => 2));

            // guessFromDirectory=false 时不猜，仅空路径兜底（豆瓣分支已自行猜过的场景）
            Assert.IsNull(SeasonProvider.ResolveVirtualSeasonNumber(null, "/tv/白夜追凶/第2季", false, _ => 2));
        }

        [TestMethod]
        public void ResolveVirtualSeasonNumber_ExistingOrUnknown_KeepsValue()
        {
            // 已有季号 → 原样保留，即使路径为空
            Assert.AreEqual(5, SeasonProvider.ResolveVirtualSeasonNumber(5, null, true, _ => 1));

            // 无季号 + 非空路径 + 猜不出 → 保持 null（不误判为 S01）
            Assert.IsNull(SeasonProvider.ResolveVirtualSeasonNumber(null, "/tv/摇曳露营", true, _ => null));
        }

        [TestMethod]
        public void Configuration_OrphanFix_Defaults_On()
        {
            Assert.IsTrue(new PluginConfiguration().EnableVirtualSeasonOrphanFix);
        }
    }
}
