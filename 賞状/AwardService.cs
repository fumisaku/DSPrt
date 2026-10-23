using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Documents;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;
using DSPrt.Data;
using DSPrt.印刷;

namespace DSPrt.賞状
{
    public sealed class AwardService
    {
        public const string AwardLayoutId = "AWARD_CERTIFICATE";
        private const double PreviewScale = 2.3;
        private const double PrintScale = 3.7795275591;
        private const double PreviewFontScale = 0.70;

        private readonly LOG_C _log;
        private readonly DataManager _dataManager;
        private readonly PrinterController _printerController;
        private readonly AwardLayoutRepository _repository;

        public AwardService(LOG_C log, DataManager dataManager, PrinterController printerController)
        {
            _log = log;
            _dataManager = dataManager;
            _printerController = printerController;
            _repository = new AwardLayoutRepository();
        }

        public AwardLayoutRepository Repository => _repository;

        public AwardLayout LoadCurrentLayout()
        {
            return _repository.LoadDefault();
        }

        public string SaveLayout(AwardLayout layout, string? path = null)
        {
            string saved = _repository.Save(layout, path);
            AppSettings.Instance.PrintSettings.AwardLayoutPath = _repository.GetRelativePath(saved);
            AppSettings.Save();
            return saved;
        }

        public FrameworkElement BuildPreview(AwardLayout layout)
        {
            var pageSize = GetPageSize(layout, PreviewScale);
            var root = new Grid
            {
                Width = pageSize.width,
                Height = pageSize.height,
                Background = Brushes.White
            };
            root.Children.Add(new Border
            {
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Background = Brushes.White
            });

            var canvas = new Canvas
            {
                Width = pageSize.width,
                Height = pageSize.height,
                Background = Brushes.Transparent
            };

            if (layout.MeshSizeMm > 0)
            {
                AddMeshLines(canvas, pageSize.width, pageSize.height, layout.MeshSizeMm * PreviewScale);
            }

            foreach (var item in layout.Items.Where(x => x.Enabled))
            {
                string text = string.IsNullOrWhiteSpace(item.Text) ? GetSampleText(item.ItemType, layout) : item.Text;
                AddPreviewTextElement(canvas, layout, item, text, pageSize.width, PreviewScale);
            }

            root.Children.Add(canvas);
            return root;
        }

        public void PrintTest(AwardLayout layout, string printerName)
        {
            FixedDocument doc = BuildDocument(layout, CreateSampleFields(layout));
            PrintDocument(doc, printerName, "賞状テスト印刷");
        }

        public void PrintAwardJob(PrintJob job)
        {
            var layout = LoadCurrentLayout();
            var pages = BuildFieldsFromJob(layout, job);
            if (pages.Count == 0)
                throw new InvalidOperationException("賞状印刷対象がありません。");

            string printer = string.IsNullOrWhiteSpace(AppSettings.Instance.PrintSettings.AwardPrinterName)
                ? _printerController.GetDefaultPrinterName()
                : AppSettings.Instance.PrintSettings.AwardPrinterName;
            if (!_printerController.PrinterExists(printer))
                printer = _printerController.GetDefaultPrinterName();

            foreach (var fields in pages)
            {
                FixedDocument doc = BuildDocument(layout, fields);
                PrintDocument(doc, printer, $"賞状印刷 {job.JobId}");
            }
        }

        private List<Dictionary<AwardItemType, string>> BuildFieldsFromJob(AwardLayout layout, PrintJob job)
        {
            var result = new List<Dictionary<AwardItemType, string>>();
            if (job.Data is not JsonObject root) return result;
            var allRanks = root["総合結果"] as JsonArray;
            if (allRanks == null) return result;

            HashSet<string>? targets = null;
            if (job.TargetBibNos != null && job.TargetBibNos.Count > 0)
                targets = job.TargetBibNos.Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var rankNode in allRanks.OfType<JsonObject>())
            {
                string bibNo = rankNode["背番号"]?.ToString() ?? string.Empty;
                if (targets != null && !targets.Contains(bibNo))
                    continue;

                string memberMasterNo = ResolveTargetMasNo(root, rankNode);
                var names = ResolveNames(bibNo, memberMasterNo);
                string rankText = ResolveRankText(layout, rankNode);
                string division = root["区分名"]?.ToString() ?? string.Empty;

                var fields = CreateSampleFields(layout);
                string leaderFromResult = rankNode["リーダー表記名"]?.ToString() ?? rankNode["リーダー氏名"]?.ToString() ?? string.Empty;
                string partnerFromResult = rankNode["パートナー表記名"]?.ToString() ?? rankNode["パートナー氏名"]?.ToString() ?? string.Empty;
                string leaderName = string.IsNullOrWhiteSpace(leaderFromResult) ? names.leader : leaderFromResult;
                string partnerName = string.IsNullOrWhiteSpace(partnerFromResult) ? names.partner : partnerFromResult;
                _log.LogAdd($"[AwardService] bib={bibNo}, masterNo={memberMasterNo}, resultLeader='{leaderFromResult}', resultPartner='{partnerFromResult}', resolvedLeader='{names.leader}', resolvedPartner='{names.partner}', finalLeader='{leaderName}', finalPartner='{partnerName}'", _log.INFO);

                fields[AwardItemType.CompetitionDivision] = division;
                fields[AwardItemType.Rank] = rankText;
                fields[AwardItemType.LeaderName] = leaderName;
                fields[AwardItemType.PartnerName] = partnerName;
                fields[AwardItemType.LeaderHonorific] = string.IsNullOrWhiteSpace(leaderName) ? string.Empty : fields[AwardItemType.LeaderHonorific];
                fields[AwardItemType.PartnerHonorific] = string.IsNullOrWhiteSpace(partnerName) ? string.Empty : fields[AwardItemType.PartnerHonorific];
                result.Add(fields);

                bool hasPartner = !string.IsNullOrWhiteSpace(partnerName);
                if (layout.NamePrintMode == AwardNamePrintMode.SeparateTwoSheets && hasPartner && !IsSameSurname(layout, leaderName, partnerName))
                {
                    var partnerFields = new Dictionary<AwardItemType, string>(fields)
                    {
                        [AwardItemType.LeaderName] = partnerName,
                        [AwardItemType.PartnerName] = string.Empty,
                        [AwardItemType.LeaderHonorific] = string.IsNullOrWhiteSpace(partnerName) ? string.Empty : fields[AwardItemType.PartnerHonorific],
                        [AwardItemType.PartnerHonorific] = string.Empty
                    };
                    result.Add(partnerFields);
                }
                else if (layout.NamePrintMode == AwardNamePrintMode.SwapLeaderPartner && hasPartner && !IsSameSurname(layout, leaderName, partnerName))
                {
                    var swappedFields = new Dictionary<AwardItemType, string>(fields)
                    {
                        [AwardItemType.LeaderName] = partnerName,
                        [AwardItemType.PartnerName] = leaderName,
                        [AwardItemType.LeaderHonorific] = string.IsNullOrWhiteSpace(partnerName) ? string.Empty : fields[AwardItemType.PartnerHonorific],
                        [AwardItemType.PartnerHonorific] = string.IsNullOrWhiteSpace(leaderName) ? string.Empty : fields[AwardItemType.LeaderHonorific]
                    };
                    result.Add(swappedFields);
                }
            }

            return result;
        }

        private (string leader, string partner) ResolveNames(string bibNo, string memberMasterNo)
        {
            var members = _dataManager.DA_Master?["DM_MEMBERs"] as JsonArray;
            if (members == null) return (bibNo, string.Empty);

            JsonObject? fallbackMaster = null;
            foreach (var member in members.OfType<JsonObject>())
            {
                var masters = member["DM_MASTERs"] as JsonArray;
                if (masters == null) continue;
                foreach (var master in masters.OfType<JsonObject>())
                {
                    if (!string.Equals(master["DM_No"]?.ToString(), bibNo, StringComparison.OrdinalIgnoreCase))
                        continue;

                    fallbackMaster ??= master;
                    if (!string.IsNullOrWhiteSpace(memberMasterNo)
                        && !string.Equals(master["DM_MasNo"]?.ToString(), memberMasterNo, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return GetDisplayNames(master, bibNo);
                }
            }

            return fallbackMaster != null ? GetDisplayNames(fallbackMaster, bibNo) : (bibNo, string.Empty);
        }

        private static (string leader, string partner) GetDisplayNames(JsonObject master, string bibNo)
        {
            string leader = master["DM_LDispName"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(leader)) leader = master["DM_LName"]?.ToString() ?? bibNo;
            string partner = master["DM_PDispName"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(partner)) partner = master["DM_PName"]?.ToString() ?? string.Empty;
            return (leader, partner);
        }

        private bool IsSameSurname(AwardLayout layout, string leader, string partner)
        {
            if (!layout.SurnameSinglePrintEnabled) return false;
            return string.Equals(GetSurname(leader), GetSurname(partner), StringComparison.OrdinalIgnoreCase);
        }

        private static string GetSurname(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            int half = name.IndexOf(' ');
            int full = name.IndexOf('　');
            int index = half >= 0 && full >= 0 ? Math.Min(half, full) : Math.Max(half, full);
            return index > 0 ? name.Substring(0, index) : name;
        }

        private string ResolveRankText(AwardLayout layout, JsonObject rankNode)
        {
            string roundName = rankNode["ラウンド名"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(roundName))
                roundName = rankNode["RoundName"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(roundName))
                roundName = rankNode["RName"]?.ToString() ?? string.Empty;

            if (IsSemiFinalRound(roundName) && layout.SemiFinalRankMode == AwardSemiFinalRankMode.FixedText)
                return layout.SemiFinalText;

            string display = rankNode["総合順位表記"]?.ToString() ?? string.Empty;
            string normalizedDisplay = NormalizeRankValue(display);
            string no = NormalizeRankValue(rankNode["総合順位番号"]?.ToString() ?? normalizedDisplay);
            int rankNumber = ParseRankNumber(no);
            int baseRank = GetBaseRank(rankNode);
            if (rankNumber > 0 && baseRank > 0)
                rankNumber += baseRank - 1;

            if (!string.IsNullOrWhiteSpace(display) && !int.TryParse(normalizedDisplay, out _))
                return display;

            string rankKey = rankNumber > 0 ? rankNumber.ToString() : normalizedDisplay;
            if (layout.RankTexts.TryGetValue(rankKey, out var rankText) && !string.IsNullOrWhiteSpace(rankText))
                return rankText;

            if (rankNumber > 0)
                return $"第{rankNumber}位";

            return string.IsNullOrWhiteSpace(no) ? string.Empty : $"第{no}位";
        }

        private static string NormalizeRankValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string normalized = value.Trim();
            if (normalized.EndsWith("位", StringComparison.Ordinal))
                normalized = normalized.Substring(0, normalized.Length - 1);
            return normalized.Trim();
        }

        private string ResolveTargetMasNo(JsonObject root, JsonObject rankNode)
        {
            string explicitMasNo = rankNode["選手マスタ番号"]?.ToString() ?? root["選手マスタ番号"]?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(explicitMasNo))
                return explicitMasNo;

            string kbnNo = root["KbnNo"]?.ToString()
                ?? root["区分No"]?.ToString()
                ?? root["区分番号"]?.ToString()
                ?? rankNode["区分No"]?.ToString()
                ?? rankNode["区分番号"]?.ToString()
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(kbnNo))
                return string.Empty;

            var kubuns = _dataManager.DA_Master?["DB_KUBUNs"] as JsonArray;
            if (kubuns == null)
                return string.Empty;

            foreach (var kubun in kubuns.OfType<JsonObject>())
            {
                if (!string.Equals(kubun["DB_KbnNo"]?.ToString(), kbnNo, StringComparison.OrdinalIgnoreCase))
                    continue;

                return kubun["DB_KbnSenM"]?.ToString() ?? string.Empty;
            }

            return string.Empty;
        }

        private Dictionary<AwardItemType, string> CreateSampleFields(AwardLayout layout)
        {
            var result = new Dictionary<AwardItemType, string>();
            foreach (var item in layout.Items)
                result[item.ItemType] = string.IsNullOrWhiteSpace(item.Text) ? GetSampleText(item.ItemType, layout) : item.Text;
            return result;
        }

        private string GetSampleText(AwardItemType itemType, AwardLayout layout)
        {
            return itemType switch
            {
                AwardItemType.CompetitionDivision => "スタンダード",
                AwardItemType.Rank => layout.RankTexts.TryGetValue("1", out var rank) ? rank : "第1位",
                AwardItemType.LeaderName => "山田 太郎",
                AwardItemType.PartnerName => "山田 花子",
                AwardItemType.CompetitionName => "競技会名称",
                _ => string.Empty
            };
        }

        private FixedDocument BuildDocument(AwardLayout layout, Dictionary<AwardItemType, string> fields)
        {
            var pageSize = GetPageSize(layout, PrintScale);
            var document = new FixedDocument();
            var pageContent = new PageContent();
            var fixedPage = new FixedPage { Width = pageSize.width, Height = pageSize.height, Background = Brushes.White };

            foreach (var item in layout.Items.Where(x => x.Enabled))
            {
                string text = fields.TryGetValue(item.ItemType, out var value) ? value : item.Text;
                var bounds = CalculateTextBounds(layout, item, text, pageSize.width, PrintScale);
                fixedPage.Children.Add(CreateTextElement(item, text, bounds.width, bounds.fontSize, bounds.left, item.YMm * PrintScale));
            }

            ((IAddChild)pageContent).AddChild(fixedPage);
            document.Pages.Add(pageContent);
            return document;
        }

        private void PrintDocument(FixedDocument document, string printerName, string description)
        {
            var dialog = new PrintDialog();
            if (!string.IsNullOrWhiteSpace(printerName))
            {
                var server = new System.Printing.LocalPrintServer();
                dialog.PrintQueue = server.GetPrintQueue(printerName);
            }
            dialog.PrintDocument(document.DocumentPaginator, description);
            _log.LogAdd($"[AwardService] 印刷完了: printer={printerName}, desc={description}", _log.INFO);
        }

        private void AddPreviewTextElement(Canvas canvas, AwardLayout layout, AwardLayoutItem item, string text, double pageWidth, double scale)
        {
            var bounds = CalculateTextBounds(layout, item, text, pageWidth, scale, PreviewFontScale);
            canvas.Children.Add(CreatePreviewFrame(bounds.left, item.YMm * scale, bounds.width));
 
            var element = CreateTextElement(item, text, bounds.width, bounds.fontSize, bounds.left, item.YMm * scale);
            canvas.Children.Add(element);
        }

        private UIElement CreateTextElement(AwardLayoutItem item, string text, double width, double fontSize, double left, double top)
        {
            bool isName = item.ItemType == AwardItemType.LeaderName || item.ItemType == AwardItemType.PartnerName;
            if (isName)
            {
                return CreateSingleLineNameElement(item, text, width, fontSize, left, top);
            }

            FrameworkElement element = item.TextAlign == AwardTextAlign.Justify
                ? CreateJustifiedTextBlock(item, text, width, fontSize)
                : CreateStandardTextBlock(item, text, width, fontSize, false);

            Canvas.SetLeft(element, left);
            Canvas.SetTop(element, top);
            FixedPage.SetLeft(element, left);
            FixedPage.SetTop(element, top);
            return element;
        }

        private UIElement CreateSingleLineNameElement(AwardLayoutItem item, string text, double width, double fontSize, double left, double top)
        {
            var typeface = new Typeface(new FontFamily(item.FontFamily), FontStyles.Normal, item.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            var dpi = VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip;
            var formatted = new FormattedText(text ?? string.Empty, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black, dpi)
            {
                MaxTextWidth = width,
                Trimming = TextTrimming.None,
                TextAlignment = ToTextAlignment(item.TextAlign)
            };
            _log.LogAdd($"[AwardService][NameFormatted] text='{text}', width={width}, fontSize={fontSize}, formattedWidth={formatted.WidthIncludingTrailingWhitespace}, formattedHeight={formatted.Height}", _log.INFO);

            var drawing = new GeometryDrawing(Brushes.Black, null, formatted.BuildGeometry(new Point(0, 0)));
            var image = new Image
            {
                Source = new DrawingImage(drawing),
                Width = width,
                Height = Math.Max(formatted.Height, fontSize * 1.2),
                Stretch = Stretch.None
            };

            Canvas.SetLeft(image, left);
            Canvas.SetTop(image, top);
            FixedPage.SetLeft(image, left);
            FixedPage.SetTop(image, top);
            return image;
        }

        private (double left, double width, double fontSize) CalculateTextBounds(AwardLayout layout, AwardLayoutItem item, string text, double pageWidth, double scale, double fontScale = 1.0)
        {
            double width = Math.Max(20, item.WidthMm * scale);
            double fontSize = item.FontSizePt * fontScale;
            double left = item.Centered ? (pageWidth - width) / 2 : item.XMm * scale;
            bool isName = item.ItemType == AwardItemType.LeaderName || item.ItemType == AwardItemType.PartnerName;

            if (isName)
            {
                double measuredWidth = MeasureTextWidth(text, item, fontSize);
                if (item.OverflowMode == AwardOverflowMode.AutoShrink || item.OverflowMode == AwardOverflowMode.AutoExpandLeft)
                {
                    ResolveNameBounds(layout, item, text, pageWidth, scale, ref width, ref left, ref fontSize, measuredWidth);
                }
                _log.LogAdd($"[AwardService][NameBounds] item={item.ItemType}, text='{text}', scale={scale}, width={width}, left={left}, fontSize={fontSize}, measuredWidth={MeasureTextWidth(text, item, fontSize)}, overflowMode={item.OverflowMode}, centered={item.Centered}", _log.INFO);
            }

            return (left, width, fontSize);
        }

        private static FrameworkElement CreatePreviewFrame(double left, double top, double width)
        {
            var rect = new Rectangle
            {
                Width = width,
                Height = 28,
                Stroke = Brushes.SteelBlue,
                StrokeThickness = 1,
                Fill = Brushes.Transparent,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect, top);
            return rect;
        }

        private FrameworkElement CreateStandardTextBlock(AwardLayoutItem item, string text, double width, double fontSize, bool isName)
        {
            if (isName)
            {
                var block = new TextBlock
                {
                    Text = text,
                    FontFamily = new FontFamily(item.FontFamily),
                    FontSize = fontSize,
                    FontWeight = item.Bold ? FontWeights.Bold : FontWeights.Normal,
                    TextAlignment = ToTextAlignment(item.TextAlign),
                    Width = width,
                    MaxWidth = width,
                    TextWrapping = TextWrapping.NoWrap,
                    Foreground = Brushes.Black,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight
                };
                block.Measure(new Size(width, double.PositiveInfinity));
                _log.LogAdd($"[AwardService][NameTextBlock] text='{text}', width={width}, fontSize={fontSize}, desiredWidth={block.DesiredSize.Width}, desiredHeight={block.DesiredSize.Height}", _log.INFO);
                var viewbox = new Viewbox
                {
                    Width = width,
                    Stretch = Stretch.Fill,
                    StretchDirection = StretchDirection.DownOnly,
                    Child = block
                };
                viewbox.Measure(new Size(width, double.PositiveInfinity));
                _log.LogAdd($"[AwardService][NameViewbox] text='{text}', width={width}, desiredWidth={viewbox.DesiredSize.Width}, desiredHeight={viewbox.DesiredSize.Height}", _log.INFO);
                return viewbox;
            }

            var normalBlock = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily(item.FontFamily),
                FontSize = fontSize,
                FontWeight = item.Bold ? FontWeights.Bold : FontWeights.Normal,
                TextAlignment = ToTextAlignment(item.TextAlign),
                Width = width,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Black
            };
            if (item.LineSpacingMm > 0)
                normalBlock.LineHeight = Math.Max(normalBlock.FontSize, item.LineSpacingMm * 3.7795275591);
            return normalBlock;
        }

        private FrameworkElement CreateJustifiedTextBlock(AwardLayoutItem item, string text, double width, double fontSize)
        {
            double displayFontSize = fontSize;
            var block = new TextBlock
            {
                FontFamily = new FontFamily(item.FontFamily),
                FontSize = displayFontSize,
                FontWeight = item.Bold ? FontWeights.Bold : FontWeights.Normal,
                Width = width,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Black
            };

            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length <= 1)
                {
                    block.Inlines.Add(new Run(line));
                }
                else
                {
                    double totalWidth = line.Sum(ch => MeasureTextWidth(ch.ToString(), item, fontSize));
                    double spacing = Math.Max(0, (width - totalWidth) / (line.Length - 1));
                    for (int j = 0; j < line.Length; j++)
                    {
                        block.Inlines.Add(new Run(line[j].ToString()));
                        if (j < line.Length - 1)
                        {
                            block.Inlines.Add(new Run(" ") { FontSize = Math.Max(1, spacing) });
                        }
                    }
                }

                if (i < lines.Length - 1)
                    block.Inlines.Add(new LineBreak());
            }

            if (item.LineSpacingMm > 0)
                block.LineHeight = Math.Max(displayFontSize, item.LineSpacingMm * 3.7795275591);

            return block;
        }

        private void ResolveNameBounds(AwardLayout layout, AwardLayoutItem item, string text, double pageWidth, double scale, ref double width, ref double left, ref double fontSize, double measuredWidth)
        {
            if (string.IsNullOrWhiteSpace(text) || measuredWidth <= width)
                return;

            if (item.OverflowMode == AwardOverflowMode.AutoShrink)
            {
                while (fontSize > layout.MinimumAutoFontSizePt)
                {
                    fontSize -= 0.5;
                    measuredWidth = MeasureTextWidth(text, item, fontSize);
                    if (measuredWidth <= width)
                        return;
                }

                double expandedWidth = measuredWidth + (4 * scale);
                double expandedLeft = item.Centered ? (pageWidth - expandedWidth) / 2 : Math.Max(0, left - (expandedWidth - width));
                if (expandedLeft >= 0)
                {
                    width = expandedWidth;
                    left = expandedLeft;
                }
            }
            else if (item.OverflowMode == AwardOverflowMode.AutoExpandLeft)
            {
                double expandedWidth = measuredWidth + (4 * scale);
                double expandedLeft = item.Centered ? (pageWidth - expandedWidth) / 2 : left - (expandedWidth - width);
                if (expandedLeft >= 0)
                {
                    width = expandedWidth;
                    left = expandedLeft;
                    return;
                }

                while (fontSize > layout.MinimumAutoFontSizePt)
                {
                    fontSize -= 0.5;
                    measuredWidth = MeasureTextWidth(text, item, fontSize);
                    if (measuredWidth <= width)
                        return;
                }
            }
        }

        private static double MeasureTextWidth(string text, AwardLayoutItem item, double fontSize)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily(item.FontFamily), FontStyles.Normal, item.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
                fontSize,
                Brushes.Black,
                VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip);
            return formatted.WidthIncludingTrailingWhitespace;
        }

        private static (double width, double height) GetPageSize(AwardLayout layout, double scale)
        {
            (double widthMm, double heightMm) = layout.PaperSize switch
            {
                AwardPaperSize.A3 => (297, 420),
                AwardPaperSize.B4 => (257, 364),
                _ => (210, 297)
            };

            if (layout.Orientation == AwardOrientation.Landscape)
                return (heightMm * scale, widthMm * scale);

            return (widthMm * scale, heightMm * scale);
        }

        private static void AddMeshLines(Canvas canvas, double width, double height, double step)
        {
            if (step <= 0) return;
            for (double x = step; x < width; x += step)
            {
                canvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = x,
                    Y1 = 0,
                    X2 = x,
                    Y2 = height,
                    Stroke = Brushes.Gainsboro,
                    StrokeThickness = 0.5
                });
            }
            for (double y = step; y < height; y += step)
            {
                canvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = 0,
                    Y1 = y,
                    X2 = width,
                    Y2 = y,
                    Stroke = Brushes.Gainsboro,
                    StrokeThickness = 0.5
                });
            }
        }

        private static TextAlignment ToTextAlignment(AwardTextAlign align)
        {
            return align switch
            {
                AwardTextAlign.Left => TextAlignment.Left,
                AwardTextAlign.Right => TextAlignment.Right,
                _ => TextAlignment.Center
            };
        }

        private static bool IsSemiFinalRound(string roundName)
        {
            return roundName.Contains("準決勝", StringComparison.OrdinalIgnoreCase)
                || roundName.Contains("準々決勝", StringComparison.OrdinalIgnoreCase)
                || roundName.Contains("準決", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseRankNumber(string value)
        {
            return int.TryParse(value, out var rank) ? rank : 0;
        }

        private static int GetBaseRank(JsonObject rankNode)
        {
            foreach (var key in new[] { "賞状開始順位", "開始順位", "基準順位", "BaseRank" })
            {
                if (int.TryParse(rankNode[key]?.ToString(), out var rank) && rank > 0)
                    return rank;
            }
            return 0;
        }

    }
}
