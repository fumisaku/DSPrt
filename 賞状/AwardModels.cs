using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DSPrt.賞状
{
    public enum AwardPaperSize
    {
        A4,
        B4,
        A3
    }

    public enum AwardOrientation
    {
        Portrait,
        Landscape
    }

    public enum AwardTextAlign
    {
        Left,
        Center,
        Right,
        Justify
    }

    public enum AwardItemType
    {
        Title,
        SubTitle,
        CompetitionDivision,
        Rank,
        LeaderName,
        PartnerName,
        LeaderHonorific,
        PartnerHonorific,
        Body,
        Date,
        Organizer,
        CompetitionName,
        OrganizerTitle1,
        OrganizerName1,
        OrganizerTitle2,
        OrganizerName2,
        OrganizerTitle3,
        OrganizerName3,
        Extra1,
        Extra2,
        Extra3
    }

    public enum AwardNamePrintMode
    {
        SinglePerPair,
        SeparateTwoSheets,
        SwapLeaderPartner
    }

    public enum AwardOverflowMode
    {
        None,
        AutoShrink,
        AutoExpandLeft
    }

    public enum AwardSemiFinalRankMode
    {
        Rank,
        FixedText
    }

    public sealed class AwardLayout
    {
        public string LayoutName { get; set; } = "新規賞状レイアウト";
        public AwardPaperSize PaperSize { get; set; } = AwardPaperSize.A4;
        public AwardOrientation Orientation { get; set; } = AwardOrientation.Portrait;
        public int MeshSizeMm { get; set; }
        public double MinimumAutoFontSizePt { get; set; } = 10;
        public AwardSemiFinalRankMode SemiFinalRankMode { get; set; } = AwardSemiFinalRankMode.Rank;
        public string SemiFinalText { get; set; } = "準決勝";
        public bool SurnameSinglePrintEnabled { get; set; }
        public AwardNamePrintMode NamePrintMode { get; set; } = AwardNamePrintMode.SinglePerPair;
        public Dictionary<string, string> RankTexts { get; set; } = CreateDefaultRankTexts();
        public List<AwardLayoutItem> Items { get; set; } = AwardLayoutItem.CreateDefaults();

        [JsonIgnore]
        public string DisplayFileName => string.IsNullOrWhiteSpace(LayoutName) ? "layout" : LayoutName;

        private static Dictionary<string, string> CreateDefaultRankTexts()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int rank = 1; rank <= 16; rank++)
            {
                result[rank.ToString()] = $"第{rank}位";
            }
            return result;
        }
    }

    public sealed class AwardLayoutItem
    {
        public AwardItemType ItemType { get; set; }
        public bool Enabled { get; set; } = true;
        public double XMm { get; set; }
        public double YMm { get; set; }
        public bool Centered { get; set; }
        public double WidthMm { get; set; } = 80;
        public string FontFamily { get; set; } = "MS 明朝";
        public double FontSizePt { get; set; } = 14;
        public bool Bold { get; set; }
        public AwardTextAlign TextAlign { get; set; } = AwardTextAlign.Center;
        public double LineSpacingMm { get; set; }
        public string Text { get; set; } = string.Empty;
        public AwardOverflowMode OverflowMode { get; set; } = AwardOverflowMode.None;

        public static List<AwardLayoutItem> CreateDefaults()
        {
            return new List<AwardLayoutItem>
            {
                new AwardLayoutItem { ItemType = AwardItemType.Title, Text = "賞状", Centered = true, XMm = 20, YMm = 20, WidthMm = 170, FontSizePt = 28, Bold = true },
                new AwardLayoutItem { ItemType = AwardItemType.SubTitle, Text = "", Centered = true, XMm = 30, YMm = 40, WidthMm = 150, FontSizePt = 14 },
                new AwardLayoutItem { ItemType = AwardItemType.CompetitionDivision, Text = "", Centered = true, XMm = 20, YMm = 60, WidthMm = 170, FontSizePt = 18, Bold = true },
                new AwardLayoutItem { ItemType = AwardItemType.Rank, Text = "", Centered = true, XMm = 60, YMm = 80, WidthMm = 90, FontSizePt = 22, Bold = true },
                new AwardLayoutItem { ItemType = AwardItemType.LeaderName, Text = "", Centered = true, XMm = 30, YMm = 105, WidthMm = 150, FontSizePt = 20, OverflowMode = AwardOverflowMode.AutoShrink },
                new AwardLayoutItem { ItemType = AwardItemType.PartnerName, Text = "", Centered = true, XMm = 30, YMm = 120, WidthMm = 150, FontSizePt = 20, OverflowMode = AwardOverflowMode.AutoShrink },
                new AwardLayoutItem { ItemType = AwardItemType.LeaderHonorific, Text = "殿", Centered = false, XMm = 182, YMm = 105, WidthMm = 12, FontSizePt = 16 },
                new AwardLayoutItem { ItemType = AwardItemType.PartnerHonorific, Text = "殿", Centered = false, XMm = 182, YMm = 120, WidthMm = 12, FontSizePt = 16 },
                new AwardLayoutItem { ItemType = AwardItemType.Body, Text = "貴殿は優秀な成績を収められましたのでここに賞します。", Centered = true, XMm = 20, YMm = 145, WidthMm = 170, FontSizePt = 13 },
                new AwardLayoutItem { ItemType = AwardItemType.Date, Text = DateTime.Today.ToString("yyyy年M月d日"), Centered = true, XMm = 40, YMm = 185, WidthMm = 130, FontSizePt = 12 },
                new AwardLayoutItem { ItemType = AwardItemType.Organizer, Text = "主催", XMm = 20, YMm = 210, WidthMm = 30, FontSizePt = 12, TextAlign = AwardTextAlign.Left },
                new AwardLayoutItem { ItemType = AwardItemType.CompetitionName, Text = "競技会名称", Centered = true, XMm = 20, YMm = 225, WidthMm = 170, FontSizePt = 12 },
                new AwardLayoutItem { ItemType = AwardItemType.OrganizerTitle1, Text = "大会会長", XMm = 25, YMm = 245, WidthMm = 40, FontSizePt = 12, TextAlign = AwardTextAlign.Left },
                new AwardLayoutItem { ItemType = AwardItemType.OrganizerName1, Text = "主催者#1", XMm = 70, YMm = 245, WidthMm = 60, FontSizePt = 12, TextAlign = AwardTextAlign.Left },
                new AwardLayoutItem { ItemType = AwardItemType.OrganizerTitle2, Text = "", XMm = 25, YMm = 257, WidthMm = 40, FontSizePt = 12, TextAlign = AwardTextAlign.Left },
                new AwardLayoutItem { ItemType = AwardItemType.OrganizerName2, Text = "", XMm = 70, YMm = 257, WidthMm = 60, FontSizePt = 12, TextAlign = AwardTextAlign.Left },
                new AwardLayoutItem { ItemType = AwardItemType.OrganizerTitle3, Text = "", XMm = 25, YMm = 269, WidthMm = 40, FontSizePt = 12, TextAlign = AwardTextAlign.Left },
                new AwardLayoutItem { ItemType = AwardItemType.OrganizerName3, Text = "", XMm = 70, YMm = 269, WidthMm = 60, FontSizePt = 12, TextAlign = AwardTextAlign.Left },
                new AwardLayoutItem { ItemType = AwardItemType.Extra1, Enabled = false, Text = "", XMm = 20, YMm = 280, WidthMm = 170, FontSizePt = 10 },
                new AwardLayoutItem { ItemType = AwardItemType.Extra2, Enabled = false, Text = "", XMm = 20, YMm = 288, WidthMm = 170, FontSizePt = 10 },
                new AwardLayoutItem { ItemType = AwardItemType.Extra3, Enabled = false, Text = "", XMm = 20, YMm = 296, WidthMm = 170, FontSizePt = 10 }
            };
        }
    }
}
