using System.Collections.Specialized;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.MetaShark.Model
{
    public class ParseNameResult : ItemLookupInfo
    {
        public string? ChineseName { get; set; } = null;

        /// <summary>
        /// 可能会解析不对，最好只在动画SP中才使用
        /// </summary>
        public string? EpisodeName { get; set; } = null;

        /// <summary>
        /// 是否是动画
        /// </summary>
        public bool IsAnime { get; set; } = false;

        private string _animeType = string.Empty;
        public string AnimeType
        {
            get
            {
                return _animeType.ToUpper();
            }
            set
            {
                _animeType = value;
            }
        }

        public bool IsSpecial
        {
            get
            {
                return !string.IsNullOrEmpty(AnimeType) && AnimeType.ToUpper() == "SP";
            }
        }

        public bool IsExtra
        {
            get
            {
                if (string.IsNullOrEmpty(AnimeType))
                {
                    return false;
                }

                var type = AnimeType.ToUpperInvariant();
                if (type == "SP" || type == "OVA" || type == "TV" || type == "MOVIE")
                {
                    return false;
                }

                // 仅明确的特典/附属类型才算 Extra，避免正片因 Anitomy 误解析 AnimeType 而被丢进 S00/Extra。
                // 线上孤儿集复现：正片 S01E01/E01 不应命中此处。
                string[] extraMarkers = { "CM", "MENU", "MENUS", "NCED", "NCOP", "NCOD", "NCOPED", "PV", "DRAMA", "VOICE", "MESSAGE", "BONUS", "EXTRA" };
                return extraMarkers.Any(m => type.Contains(m, StringComparison.Ordinal));
            }
        }

        public string? PaddingZeroIndexNumber
        {
            get
            {
                if (!IndexNumber.HasValue)
                {
                    return null;
                }

                return $"{IndexNumber:00}";
            }
        }

        public string ExtraName
        {
            get
            {
                if (IndexNumber.HasValue)
                {
                    return $"{AnimeType} {PaddingZeroIndexNumber}";
                }
                else
                {
                    return $"{AnimeType}";
                }
            }
        }

        public string SpecialName
        {
            get
            {
                if (!string.IsNullOrEmpty(EpisodeName) && IndexNumber.HasValue)
                {
                    return $"{EpisodeName} {IndexNumber}";
                }
                else if (!string.IsNullOrEmpty(EpisodeName))
                {
                    return EpisodeName;
                }

                return Name;
            }
        }
    }
}
