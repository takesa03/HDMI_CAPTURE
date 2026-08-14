using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Drawing;
using HDMICapture.Core;

namespace HDMICapture.App
{
    public partial class MainWindow : Window
    {
        private IVideoSource? _videoSource;
        private NdiSender? _ndiSender;
        private int _frameCount;
        private DateTime _lastFpsUpdate;

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

                labelStatus.Text = "● 動作中";
                statusBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)); // Green
                buttonStart.IsEnabled = false;
                buttonStop.IsEnabled = true;
                comboBoxDevices.IsEnabled = false;
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
                textBoxNDIName.IsReadOnly = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"停止中にエラーが発生しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void VideoSource_FrameReceived(object? sender, VideoFrameEventArgs e)
        {
            _frameCount++;

            // Update FPS display
            var now = DateTime.Now;
            if ((now - _lastFpsUpdate).TotalSeconds >= 1.0)
            {
                Dispatcher.Invoke(() =>
                {
                    labelInfo.Text = $"フレームレート: {_frameCount} FPS";
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
                    Dispatcher.Invoke(() =>
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
                    Dispatcher.Invoke(() =>
                    {
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

        private void DisplayFrame(byte[] frameData, int width, int height)
        {
            try
            {
                // frameData is in BGRA format from OpenCV (B G R A)
                // Create Bitmap directly with BGRA data
                using (var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    var bmpData = bitmap.LockBits(
                        new Rectangle(0, 0, width, height),
                        System.Drawing.Imaging.ImageLockMode.WriteOnly,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                    // WPF Format32bppArgb actually stores BGRA in memory, which matches OpenCV BGRA
                    System.Runtime.InteropServices.Marshal.Copy(frameData, 0, bmpData.Scan0, frameData.Length);
                    bitmap.UnlockBits(bmpData);

                    // Convert Bitmap to BitmapImage for WPF
                    using (var memoryStream = new System.IO.MemoryStream())
                    {
                        bitmap.Save(memoryStream, System.Drawing.Imaging.ImageFormat.Bmp);
                        memoryStream.Seek(0, System.IO.SeekOrigin.Begin);

                        var bitmapImage = new BitmapImage();
                        bitmapImage.BeginInit();
                        bitmapImage.StreamSource = memoryStream;
                        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                        bitmapImage.EndInit();
                        bitmapImage.Freeze();

                        imagePreview.Source = bitmapImage;
                    }
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
            _ndiSender?.Dispose();
            _videoSource = null;
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
