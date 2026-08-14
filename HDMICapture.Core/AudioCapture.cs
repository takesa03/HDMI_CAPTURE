using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;

namespace HDMICapture.Core
{
    public class AudioFrameEventArgs : EventArgs
    {
        // インターリーブ形式 (LRLRLR...) の float サンプル
        public float[] Samples { get; set; } = Array.Empty<float>();
        public int SampleRate { get; set; }
        public int Channels { get; set; }
    }

    public class AudioCapture : IDisposable
    {
        private WasapiCapture? _capture;
        private bool _disposed;

        public event EventHandler<AudioFrameEventArgs>? AudioFrameReceived;
        public event EventHandler<string>? ErrorOccurred;

        public bool IsRunning { get; private set; }
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public string DeviceName { get; private set; } = string.Empty;

        public static List<(string Name, string Id)> EnumerateAudioDevices()
        {
            var result = new List<(string, string)>();
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                result.Add((device.FriendlyName, device.ID));
            }
            return result;
        }

        // deviceNameContains に一致する録音デバイスを探して開始する。
        // 見つからない場合は既定の録音デバイスを使う。
        public void Start(string? deviceNameContains)
        {
            if (IsRunning) return;

            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

            MMDevice? targetDevice = null;
            if (!string.IsNullOrEmpty(deviceNameContains))
            {
                foreach (var d in devices)
                {
                    if (d.FriendlyName.Contains(deviceNameContains, StringComparison.OrdinalIgnoreCase))
                    {
                        targetDevice = d;
                        break;
                    }
                }
            }

            if (targetDevice == null)
            {
                System.Diagnostics.Debug.WriteLine($"[Audio] \"{deviceNameContains}\" に一致するデバイスが見つからないため、音声キャプチャは行いません");
                return;
            }

            DeviceName = targetDevice.FriendlyName;
            System.Diagnostics.Debug.WriteLine($"[Audio] デバイス使用: {DeviceName}");

            _capture = new WasapiCapture(targetDevice);
            SampleRate = _capture.WaveFormat.SampleRate;
            Channels = _capture.WaveFormat.Channels;

            System.Diagnostics.Debug.WriteLine($"[Audio] Format: {SampleRate}Hz, {Channels}ch, {_capture.WaveFormat.BitsPerSample}bit, Encoding={_capture.WaveFormat.Encoding}");

            _capture.DataAvailable += Capture_DataAvailable;
            _capture.RecordingStopped += (s, e) =>
            {
                IsRunning = false;
                if (e.Exception != null)
                    ErrorOccurred?.Invoke(this, e.Exception.Message);
            };

            _capture.StartRecording();
            IsRunning = true;
        }

        private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_capture == null || e.BytesRecorded == 0) return;

            var format = _capture.WaveFormat;
            int bytesPerSample = format.BitsPerSample / 8;
            if (bytesPerSample <= 0) return;

            int totalSamples = e.BytesRecorded / bytesPerSample;
            float[] samples = new float[totalSamples];

            if (format.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
            {
                Buffer.BlockCopy(e.Buffer, 0, samples, 0, totalSamples * 4);
            }
            else if (format.Encoding == WaveFormatEncoding.Pcm && bytesPerSample == 2)
            {
                for (int i = 0; i < totalSamples; i++)
                {
                    short s = BitConverter.ToInt16(e.Buffer, i * 2);
                    samples[i] = s / 32768f;
                }
            }
            else if (format.Encoding == WaveFormatEncoding.Pcm && bytesPerSample == 4)
            {
                for (int i = 0; i < totalSamples; i++)
                {
                    int s = BitConverter.ToInt32(e.Buffer, i * 4);
                    samples[i] = s / 2147483648f;
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[Audio] 未対応フォーマット: {format.Encoding} {bytesPerSample * 8}bit");
                return;
            }

            AudioFrameReceived?.Invoke(this, new AudioFrameEventArgs
            {
                Samples = samples,
                SampleRate = SampleRate,
                Channels = Channels
            });
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            try
            {
                _capture?.StopRecording();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Audio] Stop error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                Stop();
                _capture?.Dispose();
            }
            catch { }

            _capture = null;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
