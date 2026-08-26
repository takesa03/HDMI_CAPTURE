using HDMICapture.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace HDMICapture.App
{
    public partial class NumberedExportWindow : Window
    {
        private readonly List<MeteorDetectionRecord> _records;
        private readonly string _outputDir;
        private readonly string? _previewSourcePath;
        private readonly AppSettings _appSettings;
        private List<string> _fontNames = new();
        private bool _isLoadingSettings;
        private bool _isExporting;

        public NumberedExportWindow(IReadOnlyList<MeteorDetectionRecord> records, string reviewFolder, AppSettings appSettings)
        {
            // InitializeComponent()はXAML上のSlider既定値(Value="1.0"等)を適用する過程で
            // ValueChangedハンドラ(Slider_ValueChanged→SaveSettings)を発火させることがある。
            // その時点で_appSettingsが未代入だとNullReferenceExceptionになるため、
            // フィールドの代入をInitializeComponent()より先に済ませておく
            // (MainWindowの_isRestoringSettingsと同じ考え方)。
            _appSettings = appSettings;
            _records = new List<MeteorDetectionRecord>(records);
            _outputDir = Path.Combine(reviewFolder, "_num");
            _previewSourcePath = _records.Count > 0 && File.Exists(_records[0].PhotoPath)
                ? _records[0].PhotoPath : null;
            _isLoadingSettings = true;

            InitializeComponent();

            labelTargetCount.Text = $"{_records.Count}件";
            labelOutputDir.Text = _outputDir;

            PopulateFonts();
            RestoreSettings();
            _isLoadingSettings = false;

            UpdatePreview();
        }

        // OSにインストールされているフォント一覧をComboBoxに列挙する。
        private void PopulateFonts()
        {
            _fontNames = System.Windows.Media.Fonts.SystemFontFamilies
                .Select(f => f.Source)
                .Distinct()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            comboBoxFont.ItemsSource = _fontNames;
        }

        // 前回保存した設定を復元する。保存されたフォントが現在のOSに無い場合は
        // 日本語UIでよく使われるフォント→先頭要素の順にフォールバックする。
        private void RestoreSettings()
        {
            string? savedFont = _appSettings.NumberedExportFontFamily;
            if (!string.IsNullOrWhiteSpace(savedFont) && _fontNames.Contains(savedFont))
            {
                comboBoxFont.SelectedItem = savedFont;
            }
            else
            {
                string[] preferred = { "Yu Gothic UI", "Meiryo UI", "Meiryo", "Segoe UI" };
                comboBoxFont.SelectedItem = preferred.FirstOrDefault(p => _fontNames.Contains(p)) ?? _fontNames.FirstOrDefault();
            }

            sliderFontScale.Value = _appSettings.NumberedExportFontScale;
            sliderMarginX.Value = _appSettings.NumberedExportMarginX;
            sliderMarginY.Value = _appSettings.NumberedExportMarginY;

            labelFontScale.Text = $"{sliderFontScale.Value:F1}x";
            labelMarginX.Text = $"{(int)sliderMarginX.Value}px";
            labelMarginY.Text = $"{(int)sliderMarginY.Value}px";
        }

        private void SaveSettings()
        {
            if (_isLoadingSettings) return;

            _appSettings.NumberedExportFontFamily = comboBoxFont.SelectedItem as string ?? _appSettings.NumberedExportFontFamily;
            _appSettings.NumberedExportFontScale = sliderFontScale.Value;
            _appSettings.NumberedExportMarginX = (int)sliderMarginX.Value;
            _appSettings.NumberedExportMarginY = (int)sliderMarginY.Value;
            AppConfig.Save(_appSettings);
        }

        private NumberedExportOptions BuildOptions()
        {
            string fontName = comboBoxFont.SelectedItem as string ?? "Segoe UI";
            return new NumberedExportOptions
            {
                FontFamilyName = fontName,
                FontScaleMultiplier = sliderFontScale.Value,
                MarginX = (int)sliderMarginX.Value,
                MarginY = (int)sliderMarginY.Value
            };
        }

        private void ComboBoxFont_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdatePreview();
            SaveSettings();
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // XAML解析中(スライダーの既定値設定時)にラベル未生成の状態で発火することがある。
            if (labelFontScale == null || labelMarginX == null || labelMarginY == null) return;

            labelFontScale.Text = $"{sliderFontScale.Value:F1}x";
            labelMarginX.Text = $"{(int)sliderMarginX.Value}px";
            labelMarginY.Text = $"{(int)sliderMarginY.Value}px";
            UpdatePreview();
            SaveSettings();
        }

        private void UpdatePreview()
        {
            if (imagePreview == null) return;

            try
            {
                var options = BuildOptions();
                byte[] png = NumberedExporter.RenderPreviewPng(_previewSourcePath, 1, options);

                var bmp = new BitmapImage();
                using var ms = new MemoryStream(png);
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                imagePreview.Source = bmp;
            }
            catch
            {
                // プレビュー生成の失敗は致命的ではないため無視する(実行時には改めてエラー表示する)。
            }
        }

        private async void ButtonExecute_Click(object sender, RoutedEventArgs e)
        {
            if (_isExporting) return;

            if (_records.Count == 0)
            {
                MessageBox.Show("出力対象がありません。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _isExporting = true;
            buttonExecute.IsEnabled = false;
            buttonClose.IsEnabled = false;
            progressExport.Visibility = Visibility.Visible;
            labelProgress.Visibility = Visibility.Visible;
            progressExport.Value = 0;
            labelProgress.Text = $"処理中: 0/{_records.Count}";

            var options = BuildOptions();
            var records = _records;
            string outputDir = _outputDir;

            try
            {
                await Task.Run(() =>
                {
                    NumberedExporter.ExportAll(records, outputDir, options, (done, total) =>
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            progressExport.Value = total > 0 ? 100.0 * done / total : 0;
                            labelProgress.Text = $"処理中: {done}/{total}";
                        });
                    });
                });

                MessageBox.Show($"出力が完了しました。\n{outputDir}", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"出力に失敗しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isExporting = false;
                buttonExecute.IsEnabled = true;
                buttonClose.IsEnabled = true;
                progressExport.Visibility = Visibility.Collapsed;
                labelProgress.Visibility = Visibility.Collapsed;
            }
        }

        private void ButtonClose_Click(object sender, RoutedEventArgs e)
        {
            if (_isExporting) return;
            Close();
        }
    }
}
