using NewTek.NDI;
using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace HDMICapture.Core
{
    public class NdiSender : IDisposable
    {
        private Sender? _sender;
        private VideoFrame? _videoFrame;
        private AudioFrame? _audioFrame;
        private bool _disposed;
        private readonly object _lock = new();

        public string SourceName { get; set; } = "HDMI Capture";
        public bool IsInitialized { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public double FrameRate { get; private set; }

        public NdiSender(string sourceName, int width, int height, double frameRate, double frameRateMultiplier = 1.0)
        {
            SourceName = sourceName;
            Width = width;
            Height = height;
            FrameRate = frameRate;

            // Initialization is deferred to SendVideoFrame to handle missing NDI SDK gracefully
            _frameRateMultiplier = frameRateMultiplier;
        }

        private double _frameRateMultiplier = 1.0;

        private void Initialize(int width, int height, double frameRate, double frameRateMultiplier)
        {
            try
            {
                lock (_lock)
                {
                    // Calculate numerator and denominator from frame rate
                    int frameRateNum = (int)(frameRate * frameRateMultiplier * 1000);
                    int frameRateDen = 1000;

                    // Create NDI sender
                    string failoverName = $"{System.Net.Dns.GetHostName()} (Failover)";
                    _sender = new Sender(SourceName, true, false, null, failoverName);

                    // Create video frame
                    _videoFrame = new VideoFrame(width, height, (16.0f / 9.0f), frameRateNum, frameRateDen);

                    // Create audio frame (48kHz, stereo)
                    _audioFrame = new AudioFrame(1700, 48000, 2);

                    IsInitialized = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Warning: Failed to initialize NDI sender: {ex.Message}");
                IsInitialized = false;
            }
        }

        public void SendVideoFrame(byte[] frameData, int width, int height)
        {
            if (!IsInitialized)
                Initialize(width, height, FrameRate, _frameRateMultiplier);

            if (!IsInitialized || _videoFrame == null || _sender == null)
            {
                System.Diagnostics.Debug.WriteLine("Warning: NDI sender not available (SDK may not be installed)");
                return;
            }

            int expectedSize = width * height * 4;
            if (frameData.Length < expectedSize)
            {
                System.Diagnostics.Debug.WriteLine($"Warning: Frame data size mismatch. Expected {expectedSize}, got {frameData.Length}");
                return;
            }

            try
            {
                lock (_lock)
                {
                    // Copy frame data to NDI video frame buffer
                    int copySize = Math.Min(frameData.Length, width * height * 4);
                    Marshal.Copy(frameData, 0, _videoFrame.BufferPtr, copySize);

                    // Send the frame
                    _sender.Send(_videoFrame);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Warning: Failed to send video frame: {ex.Message}");
            }
        }

        public void SendVideoFrameWithBitmap(Bitmap bitmap)
        {
            if (!IsInitialized)
                Initialize(bitmap.Width, bitmap.Height, FrameRate, _frameRateMultiplier);

            if (!IsInitialized || _videoFrame == null || _sender == null)
            {
                System.Diagnostics.Debug.WriteLine("Warning: NDI sender not available (SDK may not be installed)");
                return;
            }

            if (bitmap.Width != _videoFrame.Width || bitmap.Height != _videoFrame.Height)
                throw new ArgumentException("Bitmap size does not match video frame size");

            try
            {
                lock (_lock)
                {
                    // Convert bitmap to byte array and send
                    System.Drawing.Imaging.BitmapData? bmpData = null;
                    try
                    {
                        bmpData = bitmap.LockBits(
                            new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                            System.Drawing.Imaging.ImageLockMode.ReadOnly,
                            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                        int dataSize = bitmap.Width * bitmap.Height * 4;
                        byte[] buffer = new byte[dataSize];
                        Marshal.Copy(bmpData.Scan0, buffer, 0, dataSize);
                        Marshal.Copy(buffer, 0, _videoFrame.BufferPtr, dataSize);
                    }
                    finally
                    {
                        if (bmpData != null)
                            bitmap.UnlockBits(bmpData);
                    }

                    // Send the frame
                    _sender.Send(_videoFrame);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Warning: Failed to send video frame: {ex.Message}");
            }
        }

        public void SendAudioFrame(float[] audioData, int sampleRate, int channels)
        {
            if (!IsInitialized || _audioFrame == null || _sender == null)
                throw new InvalidOperationException("NDI sender not initialized");

            try
            {
                lock (_lock)
                {
                    _audioFrame.SampleRate = sampleRate;
                    _audioFrame.NumChannels = channels;
                    _audioFrame.NumSamples = audioData.Length / channels;
                    _audioFrame.ChannelStride = _audioFrame.NumSamples * sizeof(float);

                    // Copy audio data
                    Marshal.Copy(audioData, 0, _audioFrame.AudioBuffer, audioData.Length);

                    // Send the frame
                    _sender.Send(_audioFrame);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to send audio frame: {ex.Message}", ex);
            }
        }

        public int GetConnectionCount()
        {
            if (_sender == null)
                return 0;

            try
            {
                return _sender.GetConnections(10000);
            }
            catch
            {
                return 0;
            }
        }

        public string GetTallyState()
        {
            if (_sender == null)
                return "Offline";

            try
            {
                var tally = _sender.Tally;
                if (tally.on_program)
                    return "On Program";
                else if (tally.on_preview)
                    return "On Preview";
                else
                    return "Standby";
            }
            catch
            {
                return "Unknown";
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            lock (_lock)
            {
                _videoFrame?.Dispose();
                _audioFrame?.Dispose();
                _sender?.Dispose();

                _videoFrame = null;
                _audioFrame = null;
                _sender = null;
                IsInitialized = false;
            }

            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
