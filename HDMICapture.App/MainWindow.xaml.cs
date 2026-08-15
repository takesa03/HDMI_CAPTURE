using System;
using System.Windows;
using System.Windows.Controls;
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

        public MainWindow()
        {
            InitializeComponent();
            _ = RefreshDevicesAsync();
        }

        private async void ButtonRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDevicesAsync();
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
                textBoxNDIName.IsReadOnly = false;
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
            }
            catch { }
            base.OnClosed(e);
        }
    }
}
