using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HDMICapture.Core
{
    public class OpenCvVideoCapture : IVideoSource
    {
        private OpenCvSharp.VideoCapture? _capture;
        private bool _isRunning;
        private bool _disposed;
        private readonly object _lock = new();
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _captureTask;

        public event EventHandler<VideoFrameEventArgs>? FrameReceived;
        public event EventHandler<string>? ErrorOccurred;

        public string DeviceName { get; set; } = "Video Capture";
        public bool IsRunning => _isRunning;
        public int Width { get; private set; } = 1920;
        public int Height { get; private set; } = 1080;
        public double FrameRate { get; private set; } = 60.0;
        public int ActualWidth { get; private set; }
        public int ActualHeight { get; private set; }
        public double ActualFrameRate { get; private set; }

        public OpenCvVideoCapture(string deviceName)
        {
            DeviceName = deviceName;
        }

        public void Start()
        {
            if (_isRunning) return;

            try
            {
                lock (_lock)
                {
                    if (_capture == null)
                        InitializeCapture();

                    _isRunning = true;
                    _cancellationTokenSource = new CancellationTokenSource();
                    // Task.Run で明示的にバックグラウンドスレッドへ切り離す。
                    // これをしないと await の継続処理がUIスレッドのSynchronizationContextに
                    // 捕捉され、capture.Read()のブロッキング呼び出しがUIスレッドで走ってしまう。
                    var token = _cancellationTokenSource.Token;
                    _captureTask = Task.Run(() => CaptureFramesAsync(token), token);
                }
            }
            catch (Exception ex)
            {
                _isRunning = false;
                ErrorOccurred?.Invoke(this, $"Failed to start capture: {ex.Message}");
                Cleanup();
                throw;
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;

            try
            {
                lock (_lock)
                {
                    _isRunning = false;
                    _cancellationTokenSource?.Cancel();

                    // Wait for capture task to complete
                    if (_captureTask != null)
                    {
                        try
                        {
                            _captureTask.Wait(5000);
                        }
                        catch (OperationCanceledException) { }
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to stop capture: {ex.Message}");
            }
        }

        private void InitializeCapture()
        {
            try
            {
                int deviceIndex = TryParseDeviceIndex(DeviceName);
                _capture = new OpenCvSharp.VideoCapture(deviceIndex, VideoCaptureAPIs.MSMF);

                if (!_capture.IsOpened())
                    throw new InvalidOperationException($"Failed to open capture device {deviceIndex}");

                // Set resolution and FPS
                _capture.Set(VideoCaptureProperties.FrameWidth, Width);
                _capture.Set(VideoCaptureProperties.FrameHeight, Height);
                _capture.Set(VideoCaptureProperties.Fps, FrameRate);

                // Get actual resolution and FPS from device
                ActualWidth = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
                ActualHeight = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
                ActualFrameRate = _capture.Get(VideoCaptureProperties.Fps);

                System.Diagnostics.Debug.WriteLine($"Capture device initialized: {ActualWidth}x{ActualHeight} @ {ActualFrameRate} fps");
            }
            catch (Exception ex)
            {
                Cleanup();
                throw new InvalidOperationException($"Failed to initialize capture: {ex.Message}", ex);
            }
        }

        private async Task CaptureFramesAsync(CancellationToken cancellationToken)
        {
            try
            {
                using (var frame = new Mat())
                using (var rgbaFrame = new Mat())
                {
                    while (!cancellationToken.IsCancellationRequested && _isRunning && _capture != null)
                    {
                        if (!_capture.Read(frame))
                        {
                            ErrorOccurred?.Invoke(this, "Failed to read frame from capture device");
                            break;
                        }

                        if (frame.Empty())
                            continue;

                        // Keep BGR as-is and add alpha channel
                        Cv2.CvtColor(frame, rgbaFrame, ColorConversionCodes.BGR2BGRA);

                        // Convert Mat to byte array
                        byte[] frameData = new byte[rgbaFrame.Total() * rgbaFrame.ElemSize()];
                        System.Runtime.InteropServices.Marshal.Copy(rgbaFrame.Data, frameData, 0, frameData.Length);

                        var args = new VideoFrameEventArgs
                        {
                            FrameData = frameData,
                            Width = rgbaFrame.Width,
                            Height = rgbaFrame.Height,
                            Timestamp = DateTime.Now.Ticks
                        };

                        FrameReceived?.Invoke(this, args);

                        // capture.Read() 自体がデバイスのフレームレートでブロックされるため、
                        // 追加の待機は入れない（キャンセル確認のためのみ最小限yield）
                        await Task.Yield();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (_isRunning)
                    ErrorOccurred?.Invoke(this, $"Capture error: {ex.Message}");
            }
        }


        public struct DeviceInfo
        {
            public int Index;
            public int Width;
            public int Height;
            public double Fps;
        }

        public static Dictionary<string, DeviceInfo> DeviceCache = new();

        private static int TryParseDeviceIndex(string deviceName)
        {
            if (DeviceCache.TryGetValue(deviceName, out var info))
                return info.Index;

            if (int.TryParse(deviceName, out int numIndex))
                return numIndex;

            return 0;
        }

        // WinRT DeviceInformation の列挙順序は OpenCV の MSMF バックエンドの
        // デバイスインデックス順序と一致するため、名前解決にはこちらを使う。
        // (DSHOW バックエンドの順序とは一致しないことを確認済み)
        public static async Task<List<string>> EnumerateAvailableDevicesAsync()
        {
            var devices = new List<string>();
            DeviceCache.Clear();

            System.Diagnostics.Debug.WriteLine("\n========== Device Enumeration Start (WinRT + MSMF) ==========");

            var winrtDevices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                Windows.Devices.Enumeration.DeviceClass.VideoCapture);

            for (int i = 0; i < winrtDevices.Count; i++)
            {
                string deviceName = winrtDevices[i].Name;
                System.Diagnostics.Debug.WriteLine($"[WinRT {i}] Name=\"{deviceName}\" Id={winrtDevices[i].Id}");

                try
                {
                    using (var capture = new OpenCvSharp.VideoCapture(i, VideoCaptureAPIs.MSMF))
                    {
                        if (capture.IsOpened())
                        {
                            using (var frame = new Mat())
                            {
                                if (capture.Read(frame) && !frame.Empty())
                                {
                                    double fps = capture.Get(VideoCaptureProperties.Fps);
                                    if (fps <= 0) fps = 30.0;

                                    devices.Add(deviceName);
                                    DeviceCache[deviceName] = new DeviceInfo
                                    {
                                        Index = i,
                                        Width = frame.Width,
                                        Height = frame.Height,
                                        Fps = fps
                                    };

                                    System.Diagnostics.Debug.WriteLine($"[MSMF {i}] ✓ {deviceName}: {frame.Width}x{frame.Height} @ {fps:F2}fps");
                                }
                                else
                                {
                                    System.Diagnostics.Debug.WriteLine($"[MSMF {i}] ✗ Cannot read frame for \"{deviceName}\"");
                                }
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[MSMF {i}] ✗ Cannot open device for \"{deviceName}\"");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MSMF {i}] ✗ Exception for \"{deviceName}\": {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine($"========== Found {devices.Count} device(s) ==========\n");
            return devices;
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                Stop();
                Cleanup();
            }
            catch { }

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        private void Cleanup()
        {
            try
            {
                _cancellationTokenSource?.Dispose();
                _capture?.Release();
                _capture?.Dispose();
            }
            catch { }

            _capture = null;
            _cancellationTokenSource = null;
            _captureTask = null;
        }
    }
}
