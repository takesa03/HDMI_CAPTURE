using OpenCvSharp;
using System;
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
                    _captureTask = CaptureFramesAsync(_cancellationTokenSource.Token);
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
                // Try to open capture device (0 = default camera, try 1, 2, etc. for other devices)
                int deviceIndex = TryParseDeviceIndex(DeviceName);
                _capture = new OpenCvSharp.VideoCapture(deviceIndex, VideoCaptureAPIs.DSHOW);

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

                        // Delay based on frame rate (60fps = ~16.67ms per frame)
                        int delayMs = Math.Max(1, (int)(1000.0 / FrameRate / 2));
                        await Task.Delay(delayMs, cancellationToken);
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

        private static int TryParseDeviceIndex(string deviceName)
        {
            // If device name is a number, use it as device index
            if (int.TryParse(deviceName, out int index))
                return index;

            // Otherwise, try to find matching device
            // For now, default to 0 (will need more sophisticated device enumeration)
            return 0;
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
