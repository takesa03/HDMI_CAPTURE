using System;

namespace HDMICapture.Core
{
    public interface IVideoSource : IDisposable
    {
        event EventHandler<VideoFrameEventArgs>? FrameReceived;
        event EventHandler<string>? ErrorOccurred;

        string DeviceName { get; }
        bool IsRunning { get; }
        int Width { get; }
        int Height { get; }
        double FrameRate { get; }

        void Start();
        void Stop();
    }

    public class VideoFrameEventArgs : EventArgs
    {
        public byte[] FrameData { get; set; } = Array.Empty<byte>();
        public int Width { get; set; }
        public int Height { get; set; }
        public long Timestamp { get; set; }
    }
}
