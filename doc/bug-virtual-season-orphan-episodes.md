# Bug：电视剧虚拟季重建后大量集变为孤儿（季下看不到集）

## 1. 问题现状（只读核查，未改动媒体库）

* 服务器：`https://jellyfin-home.wx2020.cn:5001`，`ServerName=homeserver - Jellyfin`，版本 `10.11.11`，插件 `MetaShark 2.3.6.0 Active`。
* 媒体库 `电视剧 ItemId=fc8fa12ee6bc083abb484b1b0bbc420f`，路径 `/strm/189_tv`，`EnableAutomaticSeriesGrouping=true`。
* `Series` 本体未丢失：非递归 85 个，全是 `Series/FileSystem`。
* `Season` 共 110 个：`Virtual/Path=null` 55 个全部 0 集，`FileSystem` 55 个全部正常。
* `Episode` 共 1761 个：`SeasonId=null/ParentId=null/SeasonName=Season Unknown` 1068 个（60.6%），正常挂载仅 693 个。
* 按 `SeriesName` 聚合，孤儿最严重：
  * `灌篮高手 101/101`：`/strm/189_tv/灌篮高手.Slam.Dunk.../Slam.Dunk.E01...strm`
  * `武林外传 82/82`、`爱情公寓 68/68`、`人民的名义 55/55`、`伪装者 48/48`
  * `梦华录 40/40`、`去有风的地方 40/40`、`狂飙 39/39`、`白夜追凶 32/32`、`三体 30/30`
* 对照组全正常：`D.P：逃兵追缉令 12/12`、`三大队 24/24`，路径均为 `.../Season 1/E01...strm`。
* 用户视角：如 `白夜追凶 SeriesId=f004fb29...`，`UserItems` 能查到 32 集，但 `/Shows/{id}/Episodes?UserId=...` 返回 0，季 `UserData: Unplayed=0/Played=true`，点进季看不到任何集，即“具体的集不存在”。
* 日志（`log_20260904.log`，16271 行，`MetaShark` 10021 行）：
  * `Removing virtual season null` 82 次，`Creating Season` 138 次。
  * `找不到tmdb剧集数据` 89 次（如 `70626 S1E1`、`299154 S4E7/E22`），`缺少元数据` 109 次。
  * 典型序列（`黑话律师 Tmdb 155226 / Douban 35465741`）：
    `Creating Season "未知季"` -> 16 个 `GetEpisodeMetadata ParentIndex:1` -> `Removing virtual season null` -> `Creating Season "第1季"`，最终虚拟 `S01` 空留，16 集孤儿。

## 2. 问题原因

1. 物理布局扁平：49 个剧无季子目录，文件直接在 `Series/` 下（如 `/白夜追凶/Day.And.Night.S01E01...strm`，或 `Slam.Dunk.E01` 无 `S01`），依赖 Jellyfin 虚拟季。
2. Jellyfin 10.11 取消虚拟季默认 `1`（插件注释引用的 `72911501` 变更），初始扫描先建 `未知季(null)`。
3. `Providers/EpisodeProvider.cs:187-205 FixParseInfo` 在 `GetMetadata` 阶段才把虚拟集 `ParentIndex` 修正为 `1`，触发 `SeriesMetadataService` 删除 `null` 季重建 `S01`。
4. `Providers/SeasonProvider.cs:60-63,197-209` 对 `Path=null/Index=null` 直接返回空，加剧重建。
5. 元数据刷新（刮削）不重做 `Episode.ParentId/SeasonId` 迁移，已入库集仍指向已删除 `null` 季 ID，永久 `SeasonId=null`。完整扫描才会重建链接，这就是为何有季目录的 `FileSystem` 季不受影响。
6. 次要：`BaseProvider.cs:79-122 GetEpisodeAsync` 按 `Episodes[No-1]` 索引而非按集号匹配，TMDB 集数不一致即返回 `null`；`EpisodeProvider.cs:247-300 HandleAnimeExtras` 误判则丢到 `S00`；`SeriesProvider.cs:113 RemoveSeasonSuffix` 致 `爱情公寓x3` 等同名。

## 3. 解决方案

### 3.1 临时恢复（不改 `.strm` 文件，需先备份 `library.db`）

1. 备份 `jellyfin data/data/library.db`。
2. 对 `电视剧` 执行一次完整 `扫描媒体库`（非 `刷新元数据/替换所有元数据`），让 `LibraryManager` 按 `ParentIndex=1` 重挂 1068 个孤儿集到现有虚拟 `S01`。
3. 验证：`白夜追凶 /Shows/{id}/Episodes?UserId=` 从 0 恢复到 32，`Unplayed>0`；全量 `orphaned` 从 1068 降为 0；`Removing null` 不再新增。

### 3.2 根治（插件发版）

1. `FixParseInfo` 前移：解析期即保证虚拟集 `ParentIndex=1`，避免先建 `null` 季；`SeasonProvider` 对 `Path=null` 虚拟季复用 `Episode` 的 `ParentIndex` 而非返回空。
2. `GetEpisodeAsync` 改按 `EpisodeNumber/SeasonNumber` 匹配，兼容 `E01无S01`、`S01EP01`、绝对集号与 `DVD` 排序差异。
3. 收紧 `IsExtra/IsSpecial` 判定，避免正片进 `S00/Extra`。
4. 规范入库：扁平剧统一加 `Season 1` 子目录（参考 `三大队/D.P`），从源头避开虚拟季。

## 4. 验证方法（只读）

```text
GET /Users/{uid}/Items?ParentId={tvId}&Recursive=true&IncludeItemTypes=Episode&Limit=2000
-> 统计 SeasonId=null 占比应为 0

GET /Shows/{seriesId}/Seasons?UserId={uid}
GET /Shows/{seriesId}/Episodes?UserId={uid}
-> 虚拟 S01 下集数应与文件数一致，Unplayed>0
```
