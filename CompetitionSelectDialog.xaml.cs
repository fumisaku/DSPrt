using System.Collections.Generic;
using System.Windows.Input;
using DSPrt.Messages;

namespace DSPrt
{
    /// <summary>
    /// 複数競技会が存在する場合に表示する競技会選択ダイアログ
    /// </summary>
    public partial class CompetitionSelectDialog : Window
    {
        /// <summary>選択された競技会番号。キャンセル時は null。</summary>
        public string? SelectedCmpNo { get; private set; }

        public CompetitionSelectDialog(List<CompetitionInfo> competitions)
        {
            InitializeComponent();
            LstCompetitions.ItemsSource = competitions;
            if (competitions.Count > 0)
                LstCompetitions.SelectedIndex = 0;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (LstCompetitions.SelectedItem is CompetitionInfo selected)
            {
                SelectedCmpNo = selected.CmpNo;
                DialogResult = true;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void LstCompetitions_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LstCompetitions.SelectedItem is CompetitionInfo)
                BtnOk_Click(sender, e);
        }
    }
}
