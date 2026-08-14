using System;

namespace DSPrt.賞状
{
    public static class AwardDisplayNames
    {
        public static string GetLabel<T>(T value) where T : struct, Enum
        {
            return value switch
            {
                AwardOrientation.Portrait => "縦",
                AwardOrientation.Landscape => "横",
                AwardTextAlign.Left => "左寄せ",
                AwardTextAlign.Center => "中央寄せ",
                AwardTextAlign.Right => "右寄せ",
                AwardTextAlign.Justify => "均等割り",
                AwardNamePrintMode.SinglePerPair => "1枚づつ印刷",
                AwardNamePrintMode.SeparateTwoSheets => "2枚づつ印刷",
                AwardNamePrintMode.SwapLeaderPartner => "リーダーとパートナーを入れ替えて1枚づつ印刷",
                AwardOverflowMode.None => "なし",
                AwardOverflowMode.AutoShrink => "自動縮小",
                AwardOverflowMode.AutoExpandLeft => "左方向へ自動拡大",
                AwardSemiFinalRankMode.Rank => "順位を表示",
                AwardSemiFinalRankMode.FixedText => "固定文言を表示",
                AwardItemType.Title => "タイトル",
                AwardItemType.SubTitle => "サブタイトル",
                AwardItemType.CompetitionDivision => "競技区分名",
                AwardItemType.Rank => "順位",
                AwardItemType.LeaderName => "リーダー氏名",
                AwardItemType.PartnerName => "パートナー氏名",
                AwardItemType.LeaderHonorific => "リーダー敬称",
                AwardItemType.PartnerHonorific => "パートナー敬称",
                AwardItemType.Body => "本文",
                AwardItemType.Date => "日付",
                AwardItemType.Organizer => "主催者",
                AwardItemType.CompetitionName => "競技会名称",
                AwardItemType.OrganizerTitle1 => "主催者#1 肩書き",
                AwardItemType.OrganizerName1 => "主催者#1 氏名",
                AwardItemType.OrganizerTitle2 => "主催者#2 肩書き",
                AwardItemType.OrganizerName2 => "主催者#2 氏名",
                AwardItemType.OrganizerTitle3 => "主催者#3 肩書き",
                AwardItemType.OrganizerName3 => "主催者#3 氏名",
                AwardItemType.Extra1 => "予備1",
                AwardItemType.Extra2 => "予備2",
                AwardItemType.Extra3 => "予備3",
                _ => value.ToString() ?? string.Empty
            };
        }
    }
}
