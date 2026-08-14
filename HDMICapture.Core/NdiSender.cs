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
        public bool IsVideoInitialized { get; private set; }
        public bool IsAudioInitialized { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public double FrameRate { get; private set; }

        private double _frameRateMultiplier = 1.0;
        private const int AudioMaxSamplesPerChunk = 1700;
        private int _audioChannels;

        public NdiSender(string sourceName, int width, int height, double frameRate, double frameRateMultiplier = 1.0)
        {
            SourceName = sourceName;
            Width = width;
            Height = height;
            FrameRate = frameRate;

            // Initialization is deferred to Send*Frame to handle missing NDI SDK gracefully
            _frameRateMultiplier = frameRateMultiplier;
        }

        private void EnsureSender()
        {
            if (_sender != null) return;
            string failoverName = $"{System.Net.Dns.GetHostName()} (Failover)";
            _sender = new Sender(SourceName, true, false, null, failoverName);
        }

        private void InitializeVideo(int width, int height, double frameRate, double frameRateMultiplier)
        {
            try
            {
                lock (_lock)
                {
                    EnsureSender();

                    int frameRateNum = (int)(frameRate * frameRateMultiplier * 1000);
                    int frameRateDen = 1000;

                    _videoFrame = new VideoFrame(width, height, (16.0f / 9.0f), frameRateNum, frameRateDen);

                    IsVideoInitialized = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Warning: Failed to initialize NDI video sender: {ex.Message}");
                IsVideoInitialized = false;
            }
        }

        private void InitializeAudio(int sampleRate, int channels)
        {
            try
            {
                lock (_lock)
                {
                    EnsureSender();

                    _audioFrame = new AudioFrame(AudioMaxSamplesPerChunk, sampleRate, channels);
                    _audioChannels = channels;

                    IsAudioInitialized = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Warning: Failed to initialize NDI audio sender: {ex.Message}");
                IsAudioInitialized = false;
            }
        }

        public void SendVideoFrame(byte[] frameData, int width, int height)
        {
            if (!IsVideoInitialized)
                InitializeVideo(width, height, FrameRate, _frameRateMultiplier);

            if (!IsVideoInitialized || _videoFrame == null || _sender == null)
            {
                System.Diagnostics.Debug.WriteLine("Warning: NDI video sender not available (SDK may not be installed)");
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
            if (!IsVideoInitialized)
                InitializeVideo(bitmap.Width, bitmap.Height, FrameRate, _frameRateMultiplier);

            if (!IsVideoInitialized || _videoFrame == null || _sender == null)
            {
                System.Diagnostics.Debug.WriteLine("Warning: NDI video sender not available (SDK may not be installed)");
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

        // audioData はインターリーブ形式 (LRLRLR...)。NDIはプレーナー形式を要求するため内部で変換する。
        // 事前確保したバッファ (AudioMaxSamplesPerChunk サンプル/チャンネル分) を超える場合は分割して送信する。
        public void SendAudioFrame(float[] audioData, int sampleRate, int channels)
        {
            if (channels <= 0 || audioData.Length == 0) return;

            if (!IsAudioInitialized || _audioChannels != channels)
                InitializeAudio(sampleRate, channels);

            if (!IsAudioInitialized || _audioFrame == null || _sender == null)
            {
                System.Diagnostics.Debug.WriteLine("Warning: NDI audio sender not available (SDK may not be installed)");
                return;
            }

            int totalSamples = audioData.Length / channels;
            int offset = 0;

            try
            {
                while (offset < totalSamples)
                {
                    int chunkSamples = Math.Min(AudioMaxSamplesPerChunk, totalSamples - offset);

                    lock (_lock)
                    {
                        // インターリーブ -> プレーナーへ変換
                        float[] planar = new float[chunkSamples * channels];
                        for (int ch = 0; ch < channels; ch++)
                        {
                            for (int i = 0; i < chunkSamples; i++)
                            {
                                planar[ch * chunkSamples + i] = audioData[(offset + i) * channels + ch];
                            }
                        }

                        _audioFrame.SampleRate = sampleRate;
                        _audioFrame.NumChannels = channels;
                        _audioFrame.NumSamples = chunkSamples;
                        _audioFrame.ChannelStride = chunkSamples * sizeof(float);

                        Marshal.Copy(planar, 0, _audioFrame.AudioBuffer, planar.Length);

                        _sender.Send(_audioFrame);
                    }

                    offset += chunkSamples;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Warning: Failed to send audio frame: {ex.Message}");
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
                IsVideoInitialized = false;
                IsAudioInitialized = false;
            }

            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
