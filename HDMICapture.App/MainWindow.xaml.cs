using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HDMICapture.Core;

namespace HDMICapture.App
{
    public partial class MainWindow : Window
    {
        // タイトルバー(OS描画部分)をダークモード化するためのDWM API。
        // WPF自体にはタイトルバーの配色を変える管理APIが無いため、Win32を直接呼ぶ。
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

        // Windows 11および一部のWindows 10(1809+)ビルドでの属性値。ビルドにより
        // 20/19のどちらが有効か異なることがあるため両方試す。
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int enabled = 1;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref enabled, sizeof(int));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"タイトルバーのダークモード化に失敗: {ex.Message}");
            }
        }

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

        private bool _isFileMode;
        private DateTime _lastPreviewUpdate = DateTime.MinValue;

        private string? _reviewFolder;
        private List<MeteorDetectionRecord> _reviewRecords = new();
        private MeteorDetectionRecord? _reviewSelected;

        // 検出一覧の表示項目。新しい検出で作り直さず差分更新することで、操作者の選択を保つ。
        private readonly ObservableCollection<DetectionListItem> _reviewItems = new();
        // 複数選択のうち、プレビューに表示している項目。
        private DetectionListItem? _reviewPrimary;

        private Window? _enlargedPreviewWindow;
        private Image? _enlargedPreviewImage;
        private MediaElement? _enlargedPreviewMedia;

        public MainWindow()
        {
            // InitializeComponent()はXAML上のText/IsChecked/SelectedIndex等の既定値を
            // 各コントロールへ設定する過程で、対応するTextChanged/Checked/SelectionChanged
            // ハンドラを発火させる。それらのハンドラは設定変更のたびにsettings.jsonへ保存する
            // ため、ガードを立てずにいるとXAML既定値(例: RTSP URLの"rtsp://")で保存済みの値を
            // 即座に上書きしてしまう。RestoreSettings()側のガードだけでは
            // InitializeComponent()中の発火を防げないため、ここで先に立てておく。
            _isRestoringSettings = true;

            InitializeComponent();

            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (ver != null)
                Title = $"{Title}  v{ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}";

            _ = RefreshDevicesAsync();

            listBoxDetections.ItemsSource = _reviewItems;

            _meteorController.MeteorDetected += MeteorController_MeteorDetected;
            // 連続性が途切れたら、飛行機の疑いで黄色にしたオーバーレイを通常色へ戻す。
            _meteorController.AircraftSuspicionCleared += (s, e) => _overlayRenderer.SetHighlight(false);
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

                if (!string.IsNullOrWhiteSpace(_appSettings.ReviewFolder))
                    _reviewFolder = _appSettings.ReviewFolder;

                if (!string.IsNullOrWhiteSpace(_appSettings.NdiName))
                    textBoxNDIName.Text = _appSettings.NdiName;

                if (!string.IsNullOrWhiteSpace(_appSettings.RtspUrl))
                    textBoxRtspUrl.Text = _appSettings.RtspUrl;

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

        private void TextBoxRtspUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isRestoringSettings) return;
            _appSettings.RtspUrl = textBoxRtspUrl.Text;
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

        private void SourceMode_Changed(object sender, RoutedEventArgs e)
        {
            // XAML解析中にIsChecked="True"の初期適用で発火することがあるため、
            // まだ生成されていないコントロールへのアクセスを避けるガードを入れる。
            if (panelFileSource == null || panelNdiName == null || panelDeviceSelect == null || panelRtspSource == null
                || panelAtomCamSource == null) return;

            bool fileMode = radioModeFile.IsChecked == true;
            bool streamingMode = radioModeStreaming.IsChecked == true;
            bool atomCamMode = radioModeAtomCam.IsChecked == true;
            bool captureMode = !fileMode && !streamingMode && !atomCamMode;

            // NDI出力はキャプチャモード・ストリーミングモードの両方で使うため、
            // ファイルモード・ATOMCamモードのときだけ隠す。
            panelNdiName.Visibility = (fileMode || atomCamMode) ? Visibility.Collapsed : Visibility.Visible;
            panelDeviceSelect.Visibility = captureMode ? Visibility.Visible : Visibility.Collapsed;
            panelFileSource.Visibility = fileMode ? Visibility.Visible : Visibility.Collapsed;
            panelRtspSource.Visibility = streamingMode ? Visibility.Visible : Visibility.Collapsed;
            panelAtomCamSource.Visibility = atomCamMode ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ButtonSelectAtomCamFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "ATOM Camの録画フォルダ(yyyyMMddフォルダの親)を選択"
            };

            if (dlg.ShowDialog() == true)
                textBoxAtomCamFolder.Text = dlg.FolderName;
        }

        private void ButtonSelectSourceFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "検出対象の映像ファイルを選択",
                Filter = "映像ファイル|*.mp4;*.m2ts;*.mov;*.avi;*.mkv;*.ts;*.wmv|すべてのファイル|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                textBoxSourceFile.Text = dlg.FileName;

                DateTime guessed = GuessRecordingStartTime(dlg.FileName);
                datePickerStartTime.SelectedDate = guessed.Date;
                textBoxStartTimeOfDay.Text = guessed.ToString("HH:mm:ss");
            }
        }

        // ファイル名に「yyyyMMddHHmmss」形式の14桁が含まれる場合はそれを撮影開始日時として
        // 優先する(このユーザーの運用ではATOM Cam2等の出力ファイル名がこの形式のため、
        // ファイルシステムの日時属性より信頼できる)。見つからない場合は、ファイルの
        // 更新日時と作成日時のうち古い方(コピー等で作成日時だけ新しくなっている場合が
        // 多いため)を撮影開始日時の推定値として使う。
        private static DateTime GuessRecordingStartTime(string path)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            var match = System.Text.RegularExpressions.Regex.Match(name, @"(?<!\d)(\d{14})(?!\d)");
            if (match.Success && DateTime.TryParseExact(match.Value, "yyyyMMddHHmmss",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var fromName))
            {
                return fromName;
            }

            try
            {
                var created = System.IO.File.GetCreationTime(path);
                var modified = System.IO.File.GetLastWriteTime(path);
                return created < modified ? created : modified;
            }
            catch
            {
                return DateTime.Now;
            }
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
            if (radioModeFile.IsChecked == true)
                StartFileMode();
            else if (radioModeStreaming.IsChecked == true)
                StartStreamingMode();
            else if (radioModeAtomCam.IsChecked == true)
                StartAtomCamMode();
            else
                StartCaptureMode();
        }

        private void StartCaptureMode()
        {
            if (comboBoxDevices.SelectedItem == null)
            {
                MessageBox.Show("デバイスを選択してください", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _isFileMode = false;
                _meteorController.DrawTimecodeOverlay = true;
                string deviceName = comboBoxDevices.SelectedItem.ToString() ?? "Unknown";

                // Initialize video capture (using OpenCV backend)
                _videoSource = new OpenCvVideoCapture(deviceName);
                _videoSource.FrameReceived += VideoSource_FrameReceived;
                _videoSource.ErrorOccurred += VideoSource_ErrorOccurred;

                // Start capture
                _videoSource.Start();

                int width = _videoSource is OpenCvVideoCapture ocv && ocv.ActualWidth > 0 ? ocv.ActualWidth : 1920;
                int height = _videoSource is OpenCvVideoCapture ocv2 && ocv2.ActualHeight > 0 ? ocv2.ActualHeight : 1080;
                double declaredFps = _videoSource is OpenCvVideoCapture ocv3 && ocv3.ActualFrameRate > 0 ? ocv3.ActualFrameRate : 30.0;

                // デバイスが申告するFPS(MSMFのプロパティ取得値)は、実際にHDMIから
                // 入力されている映像のフレームレートと一致しないことがある
                // (キャプチャカードやドライバによっては常に既定値を返す等)。
                // 開始直後の実フレーム到着間隔を実測し、その値を内部処理(流星検出の
                // 露出ウィンドウ・タイムコード)・NDI出力・プレビューで共通の基準FPSとして使う。
                double fps = MeasureInputFps(_videoSource, declaredFps);

                // Initialize NDI sender with actual device resolution and measured FPS
                string ndiName = textBoxNDIName.Text;
                if (string.IsNullOrWhiteSpace(ndiName))
                    ndiName = "HDMI Capture";

                _ndiSender = new NdiSender(ndiName, width, height, fps);
                labelCaptureInfo.Text = $"キャプチャー: {width}x{height} @ {fps:F1} fps (申告値 {declaredFps:F1} fps)";
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
                radioModeCapture.IsEnabled = false;
                radioModeFile.IsEnabled = false;
                radioModeStreaming.IsEnabled = false;

                _frameCount = 0;
                _lastFpsUpdate = DateTime.Now;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"エラーが発生しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                Cleanup();
            }
        }

        // キャプチャデバイスが対応する標準フレームレート。実測値・申告値はいずれも
        // このうち最も近い値へ丸めて使用する(デバイスによっては開始直後の
        // フレーム間隔にジッターが乗り、実測値が29.6や30.4のように微妙にぶれる
        // ことがあるため、機器間の揺らぎを吸収してNDI側でも扱いやすくする)。
        private static readonly double[] StandardFrameRates = { 10.0, 15.0, 24.0, 30.0, 60.0 };

        // 開始直後、実際にFrameReceivedが発火する間隔からFPSを実測する。
        // 呼び出し時点で既に本来のVideoSource_FrameReceivedも購読済みのため、
        // プレビュー表示等はこの計測中も止まらず継続する(計測用ハンドラは
        // カウントするだけの副次的な購読)。
        // デバイスによっては開始直後は映像モードのロックが安定するまで数百ms
        // かかることがあるため、まずウォームアップとして待ってから計測を始める。
        // 明らかに異常な値(フレームがほとんど来ない、あるいはあり得ないほど
        // 高頻度)は信頼せず、申告値(fallbackFps)にフォールバックする。
        private double MeasureInputFps(IVideoSource source, double fallbackFps, int warmupMs = 800, int calibrationMs = 2000)
        {
            System.Threading.Thread.Sleep(warmupMs);

            int frameCount = 0;
            var counterLock = new object();

            void Handler(object? s, VideoFrameEventArgs e)
            {
                lock (counterLock) frameCount++;
            }

            source.FrameReceived += Handler;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                System.Threading.Thread.Sleep(calibrationMs);
            }
            finally
            {
                source.FrameReceived -= Handler;
                sw.Stop();
            }

            int counted;
            lock (counterLock) counted = frameCount;

            double elapsedSeconds = sw.Elapsed.TotalSeconds;
            double raw = (counted >= 2 && elapsedSeconds > 0) ? counted / elapsedSeconds : fallbackFps;
            if (raw < 1.0 || raw > 240.0)
                raw = fallbackFps;

            return SnapToStandardFrameRate(raw);
        }

        private static double SnapToStandardFrameRate(double fps)
        {
            double best = StandardFrameRates[0];
            double bestDiff = Math.Abs(fps - best);
            foreach (double rate in StandardFrameRates)
            {
                double diff = Math.Abs(fps - rate);
                if (diff < bestDiff)
                {
                    best = rate;
                    bestDiff = diff;
                }
            }
            return best;
        }

        private void StartFileMode()
        {
            string path = textBoxSourceFile.Text;
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                MessageBox.Show("映像ファイルを選択してください", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DateTime startDate = (datePickerStartTime.SelectedDate ?? DateTime.Today).Date;
            if (!TimeSpan.TryParse(textBoxStartTimeOfDay.Text, out var timeOfDay))
            {
                MessageBox.Show("開始時刻の形式が正しくありません (例: 23:14:04)", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DateTime startTime = startDate + timeOfDay;

            try
            {
                _isFileMode = true;
                _meteorController.DrawTimecodeOverlay = true;

                var fileSource = new FileVideoCapture(path, startTime);
                fileSource.FrameReceived += VideoSource_FrameReceived;
                fileSource.ErrorOccurred += VideoSource_ErrorOccurred;
                fileSource.ProgressChanged += FileVideoSource_ProgressChanged;
                fileSource.PlaybackFinished += FileVideoSource_PlaybackFinished;
                _videoSource = fileSource;

                // ファイルモードではNDIへの送出は行わない(_ndiSenderは生成しない)。
                _videoSource.Start();

                System.Threading.Thread.Sleep(200);

                int width = fileSource.ActualWidth > 0 ? fileSource.ActualWidth : 1920;
                int height = fileSource.ActualHeight > 0 ? fileSource.ActualHeight : 1080;
                double fps = fileSource.ActualFrameRate > 0 ? fileSource.ActualFrameRate : 30.0;

                labelCaptureInfo.Text = $"ファイル: {width}x{height} @ {fps:F1} fps (開始 {startTime:yyyy/MM/dd HH:mm:ss})";
                _captureFps = fps;
                _captureHeight = height;

                _meteorDetectionActive = checkBoxMeteorDetection.IsChecked == true;
                if (_meteorDetectionActive)
                {
                    _meteorController.Exposure = comboBoxExposure.SelectedIndex + 1;
                    _meteorController.OutputDirectory = string.IsNullOrWhiteSpace(textBoxMeteorOutputDir.Text)
                        ? "meteor_output" : textBoxMeteorOutputDir.Text;
                    _meteorController.Reset();
                    labelMeteorCount.Text = "検出: 0件";
                }
                _overlayRenderer.Clear();

                labelStatus.Text = "● 処理中";
                statusBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)); // Green
                buttonStart.IsEnabled = false;
                buttonStop.IsEnabled = true;
                checkBoxMeteorDetection.IsEnabled = false;
                comboBoxExposure.IsEnabled = false;
                buttonSelectOutputDir.IsEnabled = false;
                radioModeCapture.IsEnabled = false;
                radioModeFile.IsEnabled = false;
                radioModeStreaming.IsEnabled = false;
                buttonSelectSourceFile.IsEnabled = false;
                datePickerStartTime.IsEnabled = false;
                textBoxStartTimeOfDay.IsEnabled = false;

                _frameCount = 0;
                _lastFpsUpdate = DateTime.Now;
                _lastPreviewUpdate = DateTime.MinValue;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"エラーが発生しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                Cleanup();
                _isFileMode = false;
            }
        }

        // ATOM Camの録画フォルダ(yyyyMMdd/hh/mm.mp4)配下を、時刻順に連続した映像として
        // 読み進めながら流星検出を行う。ファイルモードと同様に実時間を待たず高速に
        // 読み進めるため_isFileMode=trueとして同期処理・プレビュー間引きの経路を共有する。
        // 一方で、RTSPモードと同じく映像内に時刻がすでに写り込んでいるため、
        // 検出動画への日時タイムコードの焼き込みは行わない。
        private void StartAtomCamMode()
        {
            string folder = textBoxAtomCamFolder.Text;
            if (string.IsNullOrWhiteSpace(folder) || !System.IO.Directory.Exists(folder))
            {
                MessageBox.Show("ATOM Camの録画フォルダを選択してください", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _isFileMode = true;
                _meteorController.DrawTimecodeOverlay = false;

                var atomSource = new AtomCamVideoCapture(folder);
                atomSource.FrameReceived += VideoSource_FrameReceived;
                atomSource.ErrorOccurred += VideoSource_ErrorOccurred;
                atomSource.ProgressChanged += AtomCamVideoSource_ProgressChanged;
                atomSource.PlaybackFinished += FileVideoSource_PlaybackFinished;
                _videoSource = atomSource;

                // ATOMCamモードでもNDIへの送出は行わない(_ndiSenderは生成しない)。
                _videoSource.Start();

                System.Threading.Thread.Sleep(200);

                int width = atomSource.ActualWidth > 0 ? atomSource.ActualWidth : 1920;
                int height = atomSource.ActualHeight > 0 ? atomSource.ActualHeight : 1080;
                double fps = atomSource.ActualFrameRate > 0 ? atomSource.ActualFrameRate : 15.0;

                labelCaptureInfo.Text = $"ATOM Cam: {width}x{height} @ {fps:F1} fps ({atomSource.FileCount}ファイル)";
                _captureFps = fps;
                _captureHeight = height;

                _meteorDetectionActive = checkBoxMeteorDetection.IsChecked == true;
                if (_meteorDetectionActive)
                {
                    _meteorController.Exposure = comboBoxExposure.SelectedIndex + 1;
                    _meteorController.OutputDirectory = string.IsNullOrWhiteSpace(textBoxMeteorOutputDir.Text)
                        ? "meteor_output" : textBoxMeteorOutputDir.Text;
                    _meteorController.Reset();
                    labelMeteorCount.Text = "検出: 0件";
                }
                _overlayRenderer.Clear();

                labelStatus.Text = "● 処理中";
                statusBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)); // Green
                buttonStart.IsEnabled = false;
                buttonStop.IsEnabled = true;
                checkBoxMeteorDetection.IsEnabled = false;
                comboBoxExposure.IsEnabled = false;
                buttonSelectOutputDir.IsEnabled = false;
                radioModeCapture.IsEnabled = false;
                radioModeFile.IsEnabled = false;
                radioModeStreaming.IsEnabled = false;
                radioModeAtomCam.IsEnabled = false;
                buttonSelectAtomCamFolder.IsEnabled = false;

                _frameCount = 0;
                _lastFpsUpdate = DateTime.Now;
                _lastPreviewUpdate = DateTime.MinValue;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"エラーが発生しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                Cleanup();
                _isFileMode = false;
            }
        }

        private void AtomCamVideoSource_ProgressChanged(object? sender, AtomCamProgressEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                string pct = e.FileCount > 0 ? $" ({100.0 * e.FileIndex / e.FileCount:F1}%)" : "";
                labelInfo.Text = $"処理中: ファイル {e.FileIndex}/{e.FileCount}{pct}  " +
                    $"映像内時刻 {e.VideoTime:yyyy/MM/dd HH:mm:ss}  ({System.IO.Path.GetFileName(e.CurrentFile)})";
            });
        }

        private void StartStreamingMode()
        {
            string url = textBoxRtspUrl.Text.Trim();
            if (string.IsNullOrWhiteSpace(url) || !url.Contains("://"))
            {
                MessageBox.Show("RTSPのURLを入力してください (例: rtsp://192.168.1.215:8554/unicast)", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _isFileMode = false;
                // ストリーミングモード(RTSP)は映像ソース側に既に時刻が写り込んでいることが
                // 多いため、検出動画への日時タイムコードの焼き込みは行わない。
                _meteorController.DrawTimecodeOverlay = false;

                var streamSource = new StreamingVideoCapture(url);
                streamSource.FrameReceived += VideoSource_FrameReceived;
                streamSource.ErrorOccurred += VideoSource_ErrorOccurred;
                _videoSource = streamSource;

                // RTSP接続(初回)はStart()内で同期的に行われ、失敗時は例外を投げる。
                _videoSource.Start();

                int width = streamSource.ActualWidth > 0 ? streamSource.ActualWidth : 1920;
                int height = streamSource.ActualHeight > 0 ? streamSource.ActualHeight : 1080;
                double declaredFps = streamSource.ActualFrameRate > 0 ? streamSource.ActualFrameRate : 30.0;

                // キャプチャモードと同様、申告FPSを鵜呑みにせず実測して標準フレームレートに丸める。
                double fps = MeasureInputFps(_videoSource, declaredFps);

                // ストリーミングモードもキャプチャモードと同様にNDIへ送出する。
                string ndiName = textBoxNDIName.Text;
                if (string.IsNullOrWhiteSpace(ndiName))
                    ndiName = "HDMI Capture";

                _ndiSender = new NdiSender(ndiName, width, height, fps);
                labelCaptureInfo.Text = $"ストリーミング: {width}x{height} @ {fps:F1} fps (申告値 {declaredFps:F1} fps)";
                _captureFps = fps;
                _captureHeight = height;

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

                labelStatus.Text = "● 動作中";
                statusBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)); // Green
                buttonStart.IsEnabled = false;
                buttonStop.IsEnabled = true;
                checkBoxMeteorDetection.IsEnabled = false;
                comboBoxExposure.IsEnabled = false;
                buttonSelectOutputDir.IsEnabled = false;
                textBoxNDIName.IsReadOnly = true;
                radioModeCapture.IsEnabled = false;
                radioModeFile.IsEnabled = false;
                radioModeStreaming.IsEnabled = false;
                textBoxRtspUrl.IsEnabled = false;

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
            StopAndResetUi();
        }

        // ファイルモードがファイル終端まで読み切って自然終了したときに、バックグラウンド
        // (ファイル読み込み)スレッドから呼ばれる。露出時間に満たず残っている端数フレームを
        // 同期処理してから、UIスレッドへ切り替えて後片付け・完了通知を行う。
        private void FileVideoSource_PlaybackFinished(object? sender, EventArgs e)
        {
            if (_meteorDetectionActive)
            {
                try { _meteorController.Flush(synchronous: true); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"検出処理の終端フラッシュに失敗: {ex.Message}"); }
            }

            Dispatcher.BeginInvoke(() =>
            {
                int count = _meteorController.DetectCount;
                StopAndResetUi();
                MessageBox.Show($"ファイル処理が完了しました。検出件数: {count}件", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        private void FileVideoSource_ProgressChanged(object? sender, FileProcessProgressEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                string pct = e.TotalFrames > 0 ? $" ({100.0 * e.CurrentFrame / e.TotalFrames:F1}%)" : "";
                string total = e.TotalFrames > 0 ? e.TotalFrames.ToString("N0") : "?";
                labelInfo.Text = $"処理中: {e.CurrentFrame:N0}/{total}フレーム{pct}  映像内時刻 {e.VideoTime:yyyy/MM/dd HH:mm:ss}";
            });
        }

        private void StopAndResetUi()
        {
            try
            {
                _videoSource?.Stop();
                Cleanup();

                // Clear preview
                imagePreview.Source = null;
                labelInfo.Text = "フレームレート: 0 FPS";
                labelCaptureInfo.Text = "映像ソース: 待機中";

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
                radioModeCapture.IsEnabled = true;
                radioModeFile.IsEnabled = true;
                radioModeStreaming.IsEnabled = true;
                radioModeAtomCam.IsEnabled = true;
                buttonSelectSourceFile.IsEnabled = true;
                datePickerStartTime.IsEnabled = true;
                textBoxStartTimeOfDay.IsEnabled = true;
                textBoxRtspUrl.IsEnabled = true;
                buttonSelectAtomCamFolder.IsEnabled = true;

                _isFileMode = false;
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

            // Update FPS display (ファイルモードではlabelInfoは処理進捗表示に使うため対象外)
            var now = DateTime.Now;
            if (!_isFileMode && (now - _lastFpsUpdate).TotalSeconds >= 1.0)
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

            // 流星検出(ONの場合のみ)。ライブキャプチャでは重い処理をコントローラ内で
            // Task.Run実行し、キャプチャループをブロックしない。ファイルモードでは逆に
            // synchronous:trueで呼び出し元(ファイル読み込みスレッド)を検出処理の完了まで
            // 待たせることで、ファイルを読み進める速度を検出処理の速度に自然に合わせ、
            // 大きなファイルでも未処理フレームがメモリ上に溜まり続けるのを防ぐ。
            // オーバーレイ焼き込みより前に行う: 検出アルゴリズムには
            // オーバーレイの写っていない生のフレームを渡すため。
            if (_meteorDetectionActive && e.FrameData.Length > 0)
            {
                try
                {
                    var frameTime = new DateTime(e.Timestamp);
                    _meteorController.AddFrame(e.FrameData, e.Width, e.Height, _captureFps, frameTime, synchronous: _isFileMode);
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
            // ファイルモードは実時間を無視して高速に読み進めるため、毎フレームUIスレッドへ
            // 転送するとDispatcherキューが詰まる。プレビューは200ms間隔に間引く
            // (検出処理自体は間引かず全フレームに対して行う)。
            try
            {
                bool updatePreviewNow = true;
                if (_isFileMode)
                {
                    updatePreviewNow = (now - _lastPreviewUpdate).TotalMilliseconds >= 200;
                    if (updatePreviewNow) _lastPreviewUpdate = now;
                }

                if (updatePreviewNow && e.Width > 0 && e.Height > 0 && e.FrameData.Length > 0)
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
                    // 一覧を作り直さず差分更新し、操作中の選択を維持する。
                    SyncReviewItems(_reviewFolder);
                }
            });

            // 連続した露出で検出が続く場合は飛行機の可能性が高いため、黄色で表示する。
            _overlayRenderer.UpdateText(
                $"Last detected:  {e.Record.ExposureStartTime:yyyy/MM/dd HH:mm:ss}  No.{e.Count}",
                _captureHeight,
                e.IsAircraftSuspected);

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

            string folder = !string.IsNullOrWhiteSpace(_reviewFolder)
                ? _reviewFolder
                : string.IsNullOrWhiteSpace(textBoxMeteorOutputDir.Text)
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
            {
                LoadReviewFolder(dlg.FolderName);

                _appSettings.ReviewFolder = dlg.FolderName;
                AppConfig.Save(_appSettings);
            }
        }

        private void ButtonReviewRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (_reviewFolder != null) LoadReviewFolder(_reviewFolder);
        }

        private void LoadReviewFolder(string folder)
        {
            bool folderChanged = !string.Equals(_reviewFolder, folder, StringComparison.OrdinalIgnoreCase);
            _reviewFolder = folder;
            labelReviewFolder.Text = folder;

            // 別のフォルダに切り替えたときだけ一覧を空にする。同じフォルダの再読み込みでは
            // 選択を保つため、差分更新のみ行う。
            if (folderChanged)
            {
                _reviewItems.Clear();
                _reviewPrimary = null;
                _reviewSelected = null;
                StopMovie();
                UpdateReviewView();
            }

            SyncReviewItems(folder);
        }

        private static string RecordKey(MeteorDetectionRecord r) => $"{r.DetectedTime.Ticks}|{r.PhotoPath}";

        // 保存済みリストの内容を、表示項目へ差分反映する。既存項目は使い回すため、
        // 新しい検出の追加や他の項目の削除があっても、選択中の項目は変わらない。
        private void SyncReviewItems(string folder)
        {
            // 検出日時 = 露出開始時刻(処理完了時刻ではない)を基準に、昇順・No.1からの連番で表示する。
            // 番号はリスト内の位置から都度算出するため、削除後も自動的に振り直される。
            var records = MeteorDetectionListStore.Load(folder)
                .OrderBy(r => r.ExposureStartTime)
                .ToList();
            _reviewRecords = records;

            var keys = new HashSet<string>(records.Select(RecordKey));
            foreach (var gone in _reviewItems.Where(i => !keys.Contains(RecordKey(i.Record))).ToList())
                _reviewItems.Remove(gone);

            var byKey = _reviewItems.ToDictionary(i => RecordKey(i.Record));
            for (int i = 0; i < records.Count; i++)
            {
                string key = RecordKey(records[i]);
                DetectionListItem? item;

                if (i < _reviewItems.Count && RecordKey(_reviewItems[i].Record) == key)
                {
                    item = _reviewItems[i];
                }
                else if (byKey.TryGetValue(key, out item))
                {
                    _reviewItems.Move(_reviewItems.IndexOf(item), i);
                }
                else
                {
                    item = new DetectionListItem(records[i]);
                    byKey[key] = item;
                    _reviewItems.Insert(i, item);
                }

                item.Record = records[i];
                item.IsConfirmed = records[i].IsConfirmed;
            }

            var suspected = FindAircraftSuspectedIndices(records);
            for (int i = 0; i < records.Count; i++)
            {
                _reviewItems[i].Text = $"No.{i + 1}  {records[i].ExposureStartTime:yyyy/MM/dd HH:mm:ss}";
                _reviewItems[i].IsAircraftSuspected = suspected.Contains(i);
            }

            if (_reviewPrimary != null && _reviewItems.Contains(_reviewPrimary))
                _reviewSelected = _reviewPrimary.Record;

            buttonExportNumbered.IsEnabled = _reviewRecords.Count > 0;
        }

        // 露出開始時刻が連続している検出が3つ以上続く範囲を、飛行機の疑いとして返す。
        private HashSet<int> FindAircraftSuspectedIndices(List<MeteorDetectionRecord> sorted)
        {
            var result = new HashSet<int>();
            int runStart = 0;

            for (int i = 1; i <= sorted.Count; i++)
            {
                bool continues = i < sorted.Count && MeteorDetectionController.AreConsecutive(
                    sorted[i - 1].ExposureStartTime,
                    sorted[i].ExposureStartTime,
                    sorted[i - 1].ExposureSeconds > 0 ? sorted[i - 1].ExposureSeconds : _appSettings.ExposureSeconds);
                if (continues) continue;

                if (i - runStart >= MeteorDetectionController.AircraftStreakThreshold)
                    for (int k = runStart; k < i; k++) result.Add(k);
                runStart = i;
            }

            return result;
        }

        // プレビューに表示する項目は、新しく選択に加わったものを優先する(複数選択の途中で
        // 先頭の項目へ戻らないように)。選択に変化が無い場合はプレビューを更新しない。
        private void ListBoxDetections_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var primary = e.AddedItems.OfType<DetectionListItem>().LastOrDefault();
            if (primary == null)
            {
                if (_reviewPrimary != null && listBoxDetections.SelectedItems.Contains(_reviewPrimary)) return;
                primary = listBoxDetections.SelectedItems.OfType<DetectionListItem>().FirstOrDefault();
            }

            if (ReferenceEquals(primary, _reviewPrimary) && primary != null) return;

            _reviewPrimary = primary;
            _reviewSelected = primary?.Record;
            UpdateReviewView();
        }

        // 選択中と同じ項目を再クリックした場合はSelectionChangedが発火しないため、
        // 明示的に再生し直す(動画再生を最初からやり直したい場合の入口)。
        // Ctrl/Shiftによる複数選択の操作や、チェックボックスの操作では再生し直さない。
        private void ListBoxDetections_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.None) return;

            if (e.OriginalSource is DependencyObject src && !IsInside<CheckBox>(src))
            {
                var item = ItemsControl.ContainerFromElement(listBoxDetections, src) as ListBoxItem;
                if (item != null && item.IsSelected)
                    UpdateReviewView();
            }
        }

        private static bool IsInside<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T) return true;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        // 右クリックした項目が選択外なら、その項目だけを選択する(複数選択中の項目上なら選択を保つ)。
        private void ListBoxDetections_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject src &&
                ItemsControl.ContainerFromElement(listBoxDetections, src) is ListBoxItem item &&
                !item.IsSelected)
            {
                listBoxDetections.SelectedItems.Clear();
                item.IsSelected = true;
            }
        }

        private void ListBoxDetections_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                DeleteSelectedDetections();
                e.Handled = true;
            }
        }

        private void DetectionConfirmed_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: DetectionListItem clicked }) return;

            // 複数選択中の項目のチェックを操作した場合は、選択中のすべてへ同じ状態を適用する。
            var selected = listBoxDetections.SelectedItems.OfType<DetectionListItem>().ToList();
            var targets = selected.Count > 1 && selected.Contains(clicked) ? selected : new List<DetectionListItem> { clicked };
            ApplyConfirmed(targets, clicked.IsConfirmed);
        }

        private void MenuConfirmSelected_Click(object sender, RoutedEventArgs e) =>
            ApplyConfirmed(listBoxDetections.SelectedItems.OfType<DetectionListItem>().ToList(), true);

        private void MenuUnconfirmSelected_Click(object sender, RoutedEventArgs e) =>
            ApplyConfirmed(listBoxDetections.SelectedItems.OfType<DetectionListItem>().ToList(), false);

        private void MenuDeleteSelected_Click(object sender, RoutedEventArgs e) => DeleteSelectedDetections();

        private void ApplyConfirmed(List<DetectionListItem> targets, bool confirmed)
        {
            if (_reviewFolder == null || targets.Count == 0) return;

            var previous = targets.Select(t => t.IsConfirmed).ToList();
            foreach (var t in targets) t.IsConfirmed = confirmed;

            try
            {
                MeteorDetectionListStore.SetConfirmed(_reviewFolder, targets.Select(t => t.Record), confirmed);
            }
            catch (Exception ex)
            {
                for (int i = 0; i < targets.Count; i++) targets[i].IsConfirmed = previous[i];
                MessageBox.Show($"確認済みの保存に失敗しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
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
            if (buttonEditMask != null) buttonEditMask.IsEnabled = false;

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

                if (buttonEditMask != null)
                    buttonEditMask.IsEnabled = radioViewDiff.IsChecked == true && imageReview.Source != null;
            }

            UpdateEnlargedPreview();
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

        // プレビュー(画像/動画)をクリックした際に、内容確認用として同じものを
        // 大きな別ウィンドウで一時的に表示する。既に開いていれば再利用して
        // 最新の選択内容に更新する(ウィンドウが増殖しないように)。
        private void BorderReviewPreview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_reviewSelected == null) return;

            if (_enlargedPreviewWindow == null)
            {
                _enlargedPreviewImage = new Image { Stretch = Stretch.Uniform };
                _enlargedPreviewMedia = new MediaElement
                {
                    Stretch = Stretch.Uniform,
                    LoadedBehavior = MediaState.Manual,
                    UnloadedBehavior = MediaState.Manual,
                    Visibility = Visibility.Collapsed
                };
                _enlargedPreviewMedia.MediaEnded += (s, ev) =>
                {
                    _enlargedPreviewMedia.Position = TimeSpan.Zero;
                    _enlargedPreviewMedia.Play();
                };

                var grid = new Grid { Background = Brushes.Black };
                grid.Children.Add(_enlargedPreviewImage);
                grid.Children.Add(_enlargedPreviewMedia);

                var workArea = SystemParameters.WorkArea;
                _enlargedPreviewWindow = new Window
                {
                    Title = "プレビュー拡大",
                    Background = Brushes.Black,
                    Content = grid,
                    Owner = this,
                    Width = workArea.Width * 0.8,
                    Height = workArea.Height * 0.8,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                _enlargedPreviewWindow.KeyDown += (s, ev) =>
                {
                    if (ev.Key == Key.Escape) _enlargedPreviewWindow!.Close();
                };
                _enlargedPreviewWindow.Closed += (s, ev) =>
                {
                    _enlargedPreviewMedia?.Stop();
                    _enlargedPreviewWindow = null;
                    _enlargedPreviewImage = null;
                    _enlargedPreviewMedia = null;
                };

                _enlargedPreviewWindow.Show();
            }
            else
            {
                _enlargedPreviewWindow.Activate();
            }

            UpdateEnlargedPreview();
        }

        // メイン側のプレビュー(imageReview/mediaReview)の表示内容を、拡大ウィンドウ側にも反映する。
        private void UpdateEnlargedPreview()
        {
            if (_enlargedPreviewWindow == null || _enlargedPreviewImage == null || _enlargedPreviewMedia == null) return;
            if (_reviewSelected == null) return;

            _enlargedPreviewMedia.Stop();

            if (radioViewMovie.IsChecked == true)
            {
                _enlargedPreviewImage.Visibility = Visibility.Collapsed;
                _enlargedPreviewMedia.Visibility = Visibility.Visible;

                if (System.IO.File.Exists(_reviewSelected.MoviePath))
                {
                    _enlargedPreviewMedia.Source = new Uri(_reviewSelected.MoviePath, UriKind.Absolute);
                    _enlargedPreviewMedia.Play();
                }
            }
            else
            {
                _enlargedPreviewMedia.Visibility = Visibility.Collapsed;
                _enlargedPreviewImage.Visibility = Visibility.Visible;

                string path = radioViewDiff.IsChecked == true ? _reviewSelected.DiffPath : _reviewSelected.PhotoPath;
                if (System.IO.File.Exists(path))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    _enlargedPreviewImage.Source = bmp;
                }
            }
        }

        // 現在選択中の差分画像(赤線入り)から赤色部分を抽出し、現在のマスク画像
        // (未設定なら黒画像)に白色で加算した新しいマスク画像を作る。
        // 保存後、そのままそれを流星検出用の新しいマスクとして適用する。
        private void ButtonEditMask_Click(object sender, RoutedEventArgs e)
        {
            if (_reviewSelected == null || !System.IO.File.Exists(_reviewSelected.DiffPath))
            {
                MessageBox.Show("差分画像を表示した状態で実行してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "マスク画像を保存",
                Filter = "PNG画像|*.png|すべてのファイル|*.*",
                DefaultExt = ".png",
                FileName = "mask.png"
            };

            string? initialDir = !string.IsNullOrWhiteSpace(_appSettings.MaskFilePath)
                ? System.IO.Path.GetDirectoryName(_appSettings.MaskFilePath)
                : _reviewFolder;
            if (!string.IsNullOrWhiteSpace(initialDir))
                dlg.InitialDirectory = initialDir;

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    MaskEditor.CreateAugmentedMask(_appSettings.MaskFilePath, _reviewSelected.DiffPath, dlg.FileName);

                    _meteorController.LoadMask(dlg.FileName);
                    labelMaskStatus.Text = System.IO.Path.GetFileName(dlg.FileName);

                    _appSettings.MaskFilePath = dlg.FileName;
                    AppConfig.Save(_appSettings);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"マスクの作成に失敗しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // 検出一覧の全データについて、一覧表示と同じ並び順(No.1, No.2, ...)の番号を
        // 比較明合成・差分・動画に焼き込み、「_num」サブフォルダへ書き出す設定ダイアログを開く。
        private void ButtonExportNumbered_Click(object sender, RoutedEventArgs e)
        {
            if (_reviewFolder == null || _reviewRecords.Count == 0)
            {
                MessageBox.Show("出力対象がありません。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var window = new NumberedExportWindow(_reviewRecords, _reviewFolder, _appSettings) { Owner = this };
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"採番出力ダイアログを開けませんでした:\n{ex}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ButtonMarkFalsePositive_Click(object sender, RoutedEventArgs e) => DeleteSelectedDetections();

        // 選択中の項目(複数可)を誤検知として削除する。確認済みの項目は削除しない。
        private void DeleteSelectedDetections()
        {
            if (_reviewFolder == null) return;

            var targets = listBoxDetections.SelectedItems.OfType<DetectionListItem>().ToList();
            if (targets.Count == 0) return;

            var deletable = targets.Where(t => !t.IsConfirmed).ToList();
            int skipped = targets.Count - deletable.Count;
            if (deletable.Count == 0)
            {
                MessageBox.Show("選択された項目はすべて確認済みのため、削除できません。\n(チェックを外すと削除できます)",
                    "確認", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string message = $"選択した {deletable.Count} 件を誤検知として削除しますか?\n(画像・動画ファイルは _del フォルダへ移動されます)";
            if (skipped > 0) message += $"\n\n確認済みの {skipped} 件は削除されません。";
            if (MessageBox.Show(message, "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            // 削除後は、削除した先頭の位置にある項目を選択して、続けて作業できるようにする。
            int firstIndex = deletable.Min(t => _reviewItems.IndexOf(t));

            try
            {
                MeteorDetectionListStore.MarkAsFalsePositive(_reviewFolder, deletable.Select(t => t.Record));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"削除に失敗しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            SyncReviewItems(_reviewFolder);

            if (_reviewItems.Count > 0 && listBoxDetections.SelectedItems.Count == 0)
            {
                int next = Math.Clamp(firstIndex, 0, _reviewItems.Count - 1);
                listBoxDetections.SelectedItem = _reviewItems[next];
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
