using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DSPrt
{
    public partial class FontSelectDialog : Window
    {
        private readonly List<string> _allFonts;

        public FontSelectDialog(string? initialFontFamily)
        {
            InitializeComponent();
            _allFonts = Fonts.SystemFontFamilies
                .Select(x => x.Source)
                .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            LstFonts.ItemsSource = _allFonts;

            if (!string.IsNullOrWhiteSpace(initialFontFamily))
            {
                LstFonts.SelectedItem = _allFonts.FirstOrDefault(x => string.Equals(x, initialFontFamily, StringComparison.CurrentCultureIgnoreCase));
                LstFonts.ScrollIntoView(LstFonts.SelectedItem);
                TxtFilter.Text = initialFontFamily;
                TxtFilter.SelectAll();
            }
            else if (_allFonts.Count > 0)
            {
                LstFonts.SelectedIndex = 0;
                LstFonts.ScrollIntoView(LstFonts.SelectedItem);
            }
        }

        public string? SelectedFontFamily => LstFonts.SelectedItem as string;

        private void TxtFilter_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            string filter = TxtFilter.Text.Trim();
            var filteredFonts = string.IsNullOrWhiteSpace(filter)
                ? _allFonts
                : _allFonts.Where(x => x.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();

            LstFonts.ItemsSource = filteredFonts;
            if (filteredFonts.Count > 0)
            {
                LstFonts.SelectedIndex = 0;
                LstFonts.ScrollIntoView(LstFonts.SelectedItem);
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (LstFonts.SelectedItem == null)
            {
                MessageBox.Show("フォントを選択してください。", "フォント選択", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void LstFonts_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LstFonts.SelectedItem != null)
                DialogResult = true;
        }
    }
}
