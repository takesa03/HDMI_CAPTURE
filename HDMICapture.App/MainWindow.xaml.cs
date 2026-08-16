using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HDMICapture.Core;

namespace HDMICapture.App
{
    public partial class MainWindow : Window
    {
        private IVideoSource? _videoSource;
        private NdiSender? _ndiSender;
        private AudioCapture? _audioCapture;
        private int _frameCount;
        private DateTime _lastFpsUpdate;
        private WriteableBitmap? _previewBitmap;

        private readonly MeteorDetectionController _meteorController = new();
        private readonly OverlayRenderer _overlayRenderer = new();
        private bool _meteorDetectionActive;
        private double _captureFps = 30.0;
        private int _captureHeight = 1080;
        private readonly AppSettings _appSettings = AppConfig.Load();
        private bool _isRestoringSettings;

        private string? _reviewFolder;
        private List<MeteorDetectionRecord> _reviewRecords = new();
        private MeteorDetectionRecord? _reviewSelected;

        public MainWindow()
        {
            InitializeComponent();
            _ = RefreshDevicesAsync();

            _meteorController.MeteorDetected += MeteorController_MeteorDetected;
            _meteorController.ErrorOccurred += MeteorController_ErrorOccurred;

            RestoreSettings();
        }

        private void RestoreSettings()
        {
            _isRestoringSettings = true;
            try
            {
                if (!string.IsNullOrWhiteSpace(_appSettings.MeteorOutputDirectory))
                    textBoxMeteorOutputDir.Text = _appSettings.MeteorOutputDirectory;

                if (!string.IsNullOrWhiteSpace(_appSettings.NdiName))
                    textBoxNDIName.Text = _appSettings.NdiName;

                checkBoxAudioEnabled.IsChecked = _appSettings.AudioEnabled;
                checkBoxMeteorDetection.IsChecked = _appSettings.MeteorDetectionEnabled;

                int exposureIndex = Math.Clamp(_appSettings.ExposureSeconds - 1, 0, 4);
                comboBoxExposure.SelectedIndex = exposureIndex;

                sliderOverlayFontScale.Value = _appSettings.OverlayFontScale;
                sliderOverlayMarginX.Value = _appSettings.OverlayMarginX;
                sliderOverlayMarginY.Value = _appSettings.OverlayMarginY;

                int fontIndex = 0;
                for (int i = 0; i < comboBoxOverlayFont.Items.Count; i++)
                {
                    if (((ComboBoxItem)comboBoxOverlayFont.Items[i]).Tag as string == _appSettings.OverlayFontFace)
                    {
                        fontIndex = i;
                        break;
                    }
                }
                comboBoxOverlayFont.SelectedIndex = fontIndex;

                ApplyOverlayStyle();

                if (!string.IsNullOrWhiteSpace(_appSettings.MaskFilePath))
                {
                    if (System.IO.File.Exists(_appSettings.MaskFilePath))
                    {
                        try
                        {
                            _meteorController.LoadMask(_appSettings.MaskFilePath);
                            labelMaskStatus.Text = System.IO.Path.GetFileName(_appSettings.MaskFilePath);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"マスク画像の復元に失敗: {ex.Message}");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"保存されたマスク画像が見つかりません: {_appSettings.MaskFilePath}");
                    }
                }
            }
            finally
            {
                _isRestoringSettings = false;
            }
        }

        private async void ButtonRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDevicesAsync();
        }

        private void TextBoxNDIName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isRestoringSettings) return;
            _appSettings.NdiName = textBoxNDIName.Text;
            AppConfig.Save(_appSettings);
        }

        private void CheckBoxAudioEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_isRestoringSettings) return;
            _appSettings.AudioEnabled = checkBoxAudioEnabled.IsChecked == true;
            AppConfig.Save(_appSettings);
        }

        private void CheckBoxMeteorDetection_Changed(object sender, RoutedEventArgs e)
        {
            if (_isRestoringSettings) return;
            _appSettings.MeteorDetectionEnabled = checkBoxMeteorDetection.IsChecked == true;
            AppConfig.Save(_appSettings);
        }

        private void ComboBoxExposure_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRestoringSettings) return;
            _appSettings.ExposureSeconds = comboBoxExposure.SelectedIndex + 1;
            AppConfig.Save(_appSettings);
        }

        private void OverlayStyle_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            ApplyOverlayStyle();
        }

        private void OverlayFont_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyOverlayStyle();
        }

        private void ApplyOverlayStyle()
        {
            // XAML解析中(コントロールの初期値設定時)にも発火するため、
            // 未生成のコントロールへのアクセスを避けるガードを入れる。
            if (labelOverlayFontScale == null || labelOverlayMarginX == null || labelOverlayMarginY == null
                || comboBoxOverlayFont == null || comboBoxOverlayFont.SelectedItem == null)
                return;

            double fontScale = sliderOverlayFontScale.Value;
            int marginX = (int)sliderOverlayMarginX.Value;
            int marginY = (int)sliderOverlayMarginY.Value;
            string fontTag = (string)((ComboBoxItem)comboBoxOverlayFont.SelectedItem).Tag;
            var fontFace = Enum.Parse<OpenCvSharp.HersheyFonts>(fontTag);

            labelOverlayFontScale.Text = $"{fontScale:F1}x";
            labelOverlayMarginX.Text = $"{marginX}px";
            labelOverlayMarginY.Text = $"{marginY}px";

            _overlayRenderer.UpdateStyle(fontScale, marginX, marginY, fontFace);
            _meteorController.TimecodeFontFace = fontFace;
            _meteorController.TimecodeFontScaleMultiplier = fontScale;

            if (_isRestoringSettings) return;
            _appSettings.OverlayFontScale = fontScale;
            _appSettings.OverlayMarginX = marginX;
            _appSettings.OverlayMarginY = marginY;
            _appSettings.OverlayFontFace = fontTag;
            AppConfig.Save(_appSettings);
        }

        private async System.Threading.Tasks.Task RefreshDevicesAsync()
        {
            comboBoxDevices.Items.Clear();
            comboBoxDevices.IsEnabled = false;
            buttonRefreshDevices.IsEnabled = false;

            try
            {
                var devices = await OpenCvVideoCapture.EnumerateAvailableDevicesAsync();

                if (devices.Count == 0)
                {
                    comboBoxDevices.Items.Add("(デバイスなし)");
                    comboBoxDevices.SelectedIndex = 0;
                }
                else
                {
                    foreach (var device in devices)
                    {
                        comboBoxDevices.Items.Add(device);
                    }

                    comboBoxDevices.SelectedIndex = 0;
                    comboBoxDevices.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                comboBoxDevices.Items.Add("(エラー)");
                comboBoxDevices.SelectedIndex = 0;
                System.Diagnostics.Debug.WriteLine($"Device enumeration error: {ex}");
            }
            finally
            {
                buttonRefreshDevices.IsEnabled = true;
            }
        }

        private void ButtonStart_Click(object sender, RoutedEventArgs e)
        {
            if (comboBoxDevices.SelectedItem == null)
            {
                MessageBox.Show("デバイスを選択してください", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                string deviceName = comboBoxDevices.SelectedItem.ToString() ?? "Unknown";

                // Initialize video capture (using OpenCV backend)
                _videoSource = new OpenCvVideoCapture(deviceName);
                _videoSource.FrameReceived += VideoSource_FrameReceived;
                _videoSource.ErrorOccurred += VideoSource_ErrorOccurred;

                // Start capture
                _videoSource.Start();

                // Wait briefly for device to initialize and get actual resolution/FPS
                System.Threading.Thread.Sleep(500);

                // Initialize NDI sender with actual device resolution and FPS
                string ndiName = textBoxNDIName.Text;
                if (string.IsNullOrWhiteSpace(ndiName))
                    ndiName = "HDMI Capture";

                int width = _videoSource is OpenCvVideoCapture ocv && ocv.ActualWidth > 0 ? ocv.ActualWidth : 1920;
                int height = _videoSource is OpenCvVideoCapture ocv2 && ocv2.ActualHeight > 0 ? ocv2.ActualHeight : 1080;
                double fps = _videoSource is OpenCvVideoCapture ocv3 && ocv3.ActualFrameRate > 0 ? ocv3.ActualFrameRate : 30.0;

                _ndiSender = new NdiSender(ndiName, width, height, fps);
                labelCaptureInfo.Text = $"キャプチャー: {width}x{height} @ {fps:F1} fps";
                _captureFps = fps;
                _captureHeight = height;

                // 流星検出はON/OFFチェックボックスで制御。開始時に一度だけ状態を確定する
                // (VideoSource_FrameReceivedはキャプチャスレッドから呼ばれるため、
                //  実行中にチェックボックスへ直接アクセスするとクロススレッドエラーになる)。
                _meteorDetectionActive = checkBoxMeteorDetection.IsChecked == true;
                if (_meteorDetectionActive)
                {
                    _meteorController.Exposure = comboBoxExposure.SelectedIndex + 1;
                    _meteorController.OutputDirectory = string.IsNullOrWhiteSpace(textBoxMeteorOutputDir.Text)
                        ? "meteor_output" : textBoxMeteorOutputDir.Text;
                    _meteorController.Reset();
                    labelMeteorCount.Text = "検出: 0件";
                    _overlayRenderer.UpdateText("Start detection", height);
                }
                else
                {
                    _overlayRenderer.Clear();
                }

                // チェックボックスがONの場合のみ、選択した映像デバイスと同じ機器のオーディオインターフェースを探して音声キャプチャを開始
                if (checkBoxAudioEnabled.IsChecked == true)
                {
                    try
                    {
                        _audioCapture = new AudioCapture();
                        _audioCapture.AudioFrameReceived += AudioCapture_AudioFrameReceived;
                        _audioCapture.ErrorOccurred += AudioCapture_ErrorOccurred;
                        _audioCapture.Start(deviceName);
                    }
                    catch (Exception audioEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"音声キャプチャの開始に失敗: {audioEx.Message}");
                    }
                }

                labelStatus.Text = "● 動作中";
                statusBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)); // Green
                buttonStart.IsEnabled = false;
                buttonStop.IsEnabled = true;
                comboBoxDevices.IsEnabled = false;
                checkBoxAudioEnabled.IsEnabled = false;
                checkBoxMeteorDetection.IsEnabled = false;
                comboBoxExposure.IsEnabled = false;
                buttonSelectOutputDir.IsEnabled = false;
                textBoxNDIName.IsReadOnly = true;

                _frameCount = 0;
                _lastFpsUpdate = DateTime.Now;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"エラーが発生しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                Cleanup();
            }
        }

        private void ButtonStop_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _videoSource?.Stop();
                Cleanup();

                // Clear preview
                imagePreview.Source = null;
                labelInfo.Text = "フレームレート: 0 FPS";
                labelCaptureInfo.Text = "キャプチャー: 待機中";

                labelStatus.Text = "● 停止中";
                statusBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54)); // Red
                buttonStart.IsEnabled = true;
                buttonStop.IsEnabled = false;
                comboBoxDevices.IsEnabled = true;
                checkBoxAudioEnabled.IsEnabled = true;
                checkBoxMeteorDetection.IsEnabled = true;
                comboBoxExposure.IsEnabled = true;
                buttonSelectOutputDir.IsEnabled = true;
                textBoxNDIName.IsReadOnly = false;

                _meteorDetectionActive = false;
                _meteorController.Reset();
                _overlayRenderer.Clear();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"停止中にエラーが発生しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void VideoSource_FrameReceived(object? sender, VideoFrameEventArgs e)
        {
            var src = sender as IVideoSource;

            // Stop() 呼び出し直後に、停止直前に取得済みだった最後のフレームが
            // 非同期で届くことがある。停止済みならUI更新・NDI送信は行わない。
            if (src != null && !src.IsRunning)
                return;

            _frameCount++;

            // Update FPS display
            var now = DateTime.Now;
            if ((now - _lastFpsUpdate).TotalSeconds >= 1.0)
            {
                // BeginInvokeは非同期実行のため、_frameCountをそのまま参照すると
                // ラムダが実際に実行される頃には下でリセットされた後の値になってしまう。
                // 値をローカル変数にスナップショットしてクロージャに渡す。
                int fpsSnapshot = _frameCount;

                // Dispatcher.Invoke(同期)は使わない: Stop()側のTask.WaitがUIスレッドを
                // ブロックしている間にここで待ち合わせるとデッドロックする。
                Dispatcher.BeginInvoke(() =>
                {
                    labelInfo.Text = $"フレームレート: {fpsSnapshot} FPS";
                });
                _frameCount = 0;
                _lastFpsUpdate = now;
            }

            // 流星検出(ONの場合のみ)。重い処理はコントローラ内でTask.Run実行するため、
            // ここではフレームのコピー渡しのみで、キャプチャループをブロックしない。
            // オーバーレイ焼き込みより前に行う: 検出アルゴリズムには
            // オーバーレイの写っていない生のフレームを渡すため。
            if (_meteorDetectionActive && e.FrameData.Length > 0)
            {
                try
                {
                    _meteorController.AddFrame(e.FrameData, e.Width, e.Height, _captureFps);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"流星検出エラー: {ex.Message}");
                }

                // 検出件数・時刻のオーバーレイをフレームバッファへ直接焼き込む。
                // NDI送信・プレビュー表示の両方が同じバッファを参照するため、両方に反映される。
                _overlayRenderer.ApplyToFrame(e.FrameData, e.Width, e.Height);
            }

            // Send frame via NDI if initialized
            if (_ndiSender != null && e.FrameData.Length > 0)
            {
                try
                {
                    _ndiSender.SendVideoFrame(e.FrameData, e.Width, e.Height);
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        labelInfo.Text = $"NDI送信エラー: {ex.Message}";
                    });
                }
            }

            // Update preview
            try
            {
                if (e.Width > 0 && e.Height > 0 && e.FrameData.Length > 0)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        // キューに積まれた時点では動作中でも、実行時には既に停止済み
                        // (Stop側の黒画面クリアより後)の可能性があるため再チェックする。
                        if (src != null && !src.IsRunning) return;
                        DisplayFrame(e.FrameData, e.Width, e.Height);
                    });
                }
            }
            catch { }
        }

        private void VideoSource_ErrorOccurred(object? sender, string error)
        {
            Dispatcher.Invoke(() =>
            {
                labelInfo.Text = $"エラー: {error}";
            });
        }

        private void MeteorController_MeteorDetected(object? sender, MeteorDetectedEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                labelMeteorCount.Text = $"検出: {e.Count}件";

                // レビューパネルが同じフォルダを開いたまま表示中なら、
                // プレビューと並行して一覧にも新しい検出を反映する。
                if (borderReviewPanel.Visibility == Visibility.Visible &&
                    _reviewFolder != null &&
                    string.Equals(_reviewFolder, _meteorController.OutputDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    LoadReviewFolder(_reviewFolder);
                }
            });

            _overlayRenderer.UpdateText(
                $"Last detected:  {e.Record.ExposureStartTime:yyyy/MM/dd HH:mm:ss}  No.{e.Count}",
                _captureHeight);

            System.Diagnostics.Debug.WriteLine($"流星検出: {e.Record.PhotoPath} / {e.Record.DiffPath} / {e.Record.MoviePath}");
        }

        private void MeteorController_ErrorOccurred(object? sender, string error)
        {
            System.Diagnostics.Debug.WriteLine($"流星検出エラー: {error}");
        }

        private void ButtonSelectMask_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "マスク画像を選択",
                Filter = "画像ファイル|*.png;*.jpg;*.jpeg;*.bmp|すべてのファイル|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    _meteorController.LoadMask(dlg.FileName);
                    labelMaskStatus.Text = System.IO.Path.GetFileName(dlg.FileName);

                    _appSettings.MaskFilePath = dlg.FileName;
                    AppConfig.Save(_appSettings);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"マスク画像の読み込みに失敗しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ButtonMeteorReview_Click(object sender, RoutedEventArgs e)
        {
            if (borderReviewPanel.Visibility == Visibility.Visible)
            {
                borderReviewPanel.Visibility = Visibility.Collapsed;
                return;
            }

            string folder = string.IsNullOrWhiteSpace(textBoxMeteorOutputDir.Text)
                ? "meteor_output" : textBoxMeteorOutputDir.Text;

            borderReviewPanel.Visibility = Visibility.Visible;
            LoadReviewFolder(folder);
        }

        private void ButtonReviewClose_Click(object sender, RoutedEventArgs e)
        {
            borderReviewPanel.Visibility = Visibility.Collapsed;
        }

        private void ButtonReviewSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "検出リストのあるフォルダを選択" };
            if (dlg.ShowDialog() == true)
                LoadReviewFolder(dlg.FolderName);
        }

        private void ButtonReviewRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (_reviewFolder != null) LoadReviewFolder(_reviewFolder);
        }

        private void LoadReviewFolder(string folder)
        {
            _reviewFolder = folder;
            labelReviewFolder.Text = folder;

            // 検出日時 = 露出開始時刻(処理完了時刻ではない)を基準に、昇順・No.1からの連番で表示する。
            // 番号はリスト内の位置から都度算出するため、削除後の再読み込みで自動的に振り直される。
            _reviewRecords = MeteorDetectionListStore.Load(folder)
                .OrderBy(r => r.ExposureStartTime)
                .ToList();

            listBoxDetections.ItemsSource = _reviewRecords
                .Select((r, i) => $"No.{i + 1}  {r.ExposureStartTime:yyyy/MM/dd HH:mm:ss}")
                .ToList();

            _reviewSelected = null;
            StopMovie();
            UpdateReviewView();
        }

        private void ListBoxDetections_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int idx = listBoxDetections.SelectedIndex;
            _reviewSelected = (idx >= 0 && idx < _reviewRecords.Count) ? _reviewRecords[idx] : null;
            UpdateReviewView();
        }

        // 選択中と同じ項目を再クリックした場合はSelectionChangedが発火しないため、
        // 明示的に再生し直す(動画再生を最初からやり直したい場合の入口)。
        private void ListBoxDetections_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject src)
            {
                var item = ItemsControl.ContainerFromElement(listBoxDetections, src) as ListBoxItem;
                if (item != null && item.IsSelected)
                    UpdateReviewView();
            }
        }

        private void ViewMode_Changed(object sender, RoutedEventArgs e)
        {
            UpdateReviewView();
        }

        private void UpdateReviewView()
        {
            // XAML解析中、RadioButtonのIsChecked="True"の適用によってCheckedイベント
            // (ViewMode_Changed)がInitializeComponentの途中で発火することがある。
            // その時点ではまだ後方で宣言されているimageReview等が未生成のためガードする。
            if (imageReview == null || mediaReview == null) return;

            imageReview.Source = null;
            StopMovie();

            if (_reviewSelected == null) return;

            if (radioViewMovie.IsChecked == true)
            {
                imageReview.Visibility = Visibility.Collapsed;
                mediaReview.Visibility = Visibility.Visible;

                if (System.IO.File.Exists(_reviewSelected.MoviePath))
                {
                    try
                    {
                        mediaReview.Source = new Uri(_reviewSelected.MoviePath, UriKind.Absolute);
                        mediaReview.Play();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"動画読み込みエラー: {ex.Message}");
                    }
                }
            }
            else
            {
                mediaReview.Visibility = Visibility.Collapsed;
                imageReview.Visibility = Visibility.Visible;

                string path = radioViewDiff.IsChecked == true ? _reviewSelected.DiffPath : _reviewSelected.PhotoPath;
                if (System.IO.File.Exists(path))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    imageReview.Source = bmp;
                }
            }
        }

        private void MediaReview_MediaOpened(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("動画を開きました");
        }

        // このマシンではメディア系コンポーネントで問題が起きたことがあるため、
        // 再生失敗時はアプリを巻き込まずエラーメッセージのみ表示する。
        private void MediaReview_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"動画再生エラー: {e.ErrorException?.Message}");
            MessageBox.Show($"動画の再生に失敗しました:\n{e.ErrorException?.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void StopMovie()
        {
            if (mediaReview == null) return;
            try
            {
                mediaReview.Stop();
                mediaReview.Source = null;
            }
            catch { }
        }

        private void ButtonMarkFalsePositive_Click(object sender, RoutedEventArgs e)
        {
            if (_reviewSelected == null || _reviewFolder == null) return;

            var result = MessageBox.Show(
                "この検出を誤検知として削除しますか?\n(画像・動画ファイルは _del フォルダへ移動されます)",
                "確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            try
            {
                MeteorDetectionListStore.MarkAsFalsePositive(_reviewFolder, _reviewSelected);
                LoadReviewFolder(_reviewFolder);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"削除に失敗しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ButtonClearMask_Click(object sender, RoutedEventArgs e)
        {
            _meteorController.ClearMask();
            labelMaskStatus.Text = "なし";

            _appSettings.MaskFilePath = null;
            AppConfig.Save(_appSettings);
        }

        private void ButtonSelectOutputDir_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "流星検出の出力先フォルダを選択"
            };

            if (dlg.ShowDialog() == true)
            {
                textBoxMeteorOutputDir.Text = dlg.FolderName;

                _appSettings.MeteorOutputDirectory = dlg.FolderName;
                AppConfig.Save(_appSettings);
            }
        }

        private void AudioCapture_AudioFrameReceived(object? sender, AudioFrameEventArgs e)
        {
            if (_ndiSender == null || e.Samples.Length == 0) return;

            try
            {
                _ndiSender.SendAudioFrame(e.Samples, e.SampleRate, e.Channels);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"音声送信エラー: {ex.Message}");
            }
        }

        private void AudioCapture_ErrorOccurred(object? sender, string error)
        {
            System.Diagnostics.Debug.WriteLine($"音声キャプチャエラー: {error}");
        }

        private void DisplayFrame(byte[] frameData, int width, int height)
        {
            try
            {
                // BMPエンコード等の変換を挟まず、BGRAデータを直接WriteableBitmapへ書き込む
                if (_previewBitmap == null || _previewBitmap.PixelWidth != width || _previewBitmap.PixelHeight != height)
                {
                    _previewBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                }

                // Stop時に imagePreview.Source がクリアされるため、毎回参照を確認して再設定する
                if (!ReferenceEquals(imagePreview.Source, _previewBitmap))
                {
                    imagePreview.Source = _previewBitmap;
                }

                _previewBitmap.Lock();
                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(frameData, 0, _previewBitmap.BackBuffer, frameData.Length);
                    _previewBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                }
                finally
                {
                    _previewBitmap.Unlock();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error displaying frame: {ex.Message}");
            }
        }

        private void Cleanup()
        {
            _videoSource?.Dispose();
            _audioCapture?.Dispose();
            _ndiSender?.Dispose();
            _videoSource = null;
            _audioCapture = null;
            _ndiSender = null;
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _videoSource?.Stop();
                Cleanup();
                _meteorController.Dispose();
                StopMovie();
            }
            catch { }
            base.OnClosed(e);
        }
    }
}
