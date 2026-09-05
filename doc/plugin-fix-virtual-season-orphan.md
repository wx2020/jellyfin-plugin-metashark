# 插件修复方案：虚拟季孤儿集（ToDo 落地说明）

> 对应 Bug：`doc/bug-virtual-season-orphan-episodes.md`
> 现象：电视剧 1761 集中 1068 个 `SeasonId=null/Season Unknown`，55 个虚拟季全空，`Shows/Episodes?UserId` 为 0。

## 改动清单

1. `Model/ParseNameResult.cs:IsExtra`
   - 由 `非空且 !=SP/OVA/TV 即 Extra` 改为明确白名单：`CM/MENU/NCED/NCOP/NCOD/PV/DRAMA/VOICE/MESSAGE/BONUS/EXTRA` 才算 Extra，`SP/OVA/TV/MOVIE/空` 均非 Extra。
   - 目的：正片 `S01E01/E01/TV` 不再误入 `S00/Extra`。

2. `Providers/BaseProvider.cs:GetEpisodeAsync`
   - 入口加 `seasonNumber/episodeNumber null/0` 守卫。
   - 季组与常规路径均由 `Episodes[No-1]` 索引改为按 `EpisodeNumber` 查找，找不到才回退索引，越界返回 `null`。
   - 目的：修复 `299154 S4E7/E22`、`70626 S1E1` 等 `找不到tmdb剧集数据 89次` 的错位/丢失。

3. `Providers/SeasonProvider.cs:GetMetadata`
   - `GuessSeasonNumber` 仍空且 `info.Path` 为空时，默认 `seasonNumber=1`（S00 特典 `Index=0` 不受影响），并打日志。
   - 目的：扁平 `/白夜追凶/E01.strm` 不再返回空，避免先建 `未知季(null)` 再删除重建，与 `EpisodeProvider` 虚拟季修正一致。

4. `Providers/EpisodeProvider.cs:FixParseInfo`
   - 虚拟季默认 S01 增加 `!isSpecialOrExtra` 守卫（`IsSpecial/IsExtra/特典/花絮目录` 除外），避免 SP 被收进 S01。
   - 保留：`SXXEPXX` 按 Anitomy 修正、无季目录按季文件夹猜、纯虚拟 `null->1`、特典 `null->0`、集号按 Anitomy 修正。

5. `Jellyfin.Plugin.MetaShark.Test/ParseNameTest.cs:TestVirtualSeasonOrphanRegression`
   - 新增回归：`Day.And.Night.S01E01`（季1集1非Extra）、`Slam.Dunk.E01`（集1非Extra）、`Detective.Dee.S01EP01`（季1集1）、`Shigurui.TV EP01`（非Extra）。

## 验证

* 本机仅有 `.NET 8.0.424`，项目目标 `net9.0`，`dotnet build` 报 `NETSDK1045`，按环境规范未另装 SDK，故未执行编译/单测，需在 `.NET 9 SDK` 环境补跑：
  `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet test`
* 线上只读验证（扫描 `Running` 时未改配置）：
  `GET /Users/{uid}/Items?ParentId={tvId}&Recursive=true&IncludeItemTypes=Episode` 中 `SeasonId=null` 应从 1068 降为 0；
  `GET /Shows/{白夜追凶Id}/Episodes?UserId={uid}` 应从 0 恢复到 32。

## 风险

* 虚拟季默认 S01 仅覆盖 `Path` 空且号空场景，多季同年等豆瓣季 ID 仍走原有 `GuestDoubanSeasonByYear/ByName` 逻辑。
* `GetEpisodeAsync` 回退索引保留，TMDB 完全缺季时仍返回 `null`，集保留裸元数据但不丢失。
