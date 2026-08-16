using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace HDMICapture.Core
{
    public class MeteorDetectedEventArgs : EventArgs
    {
        public int Count { get; set; }
        public string ImagePath { get; set; } = string.Empty;
        public string DiffImagePath { get; set; } = string.Empty;
        public string MoviePath { get; set; } = string.Empty;
    }

    // ライブキャプチャのBGRAフレームを受け取り、露出時間分バッファリングして
    // 流星検出(MeteorDetector)を非同期に実行するコントローラ。
    // 重い検出処理(diff/Canny/HoughLinesP/画像・動画保存)はキャプチャスレッドを
    // ブロックしないよう Task.Run でバックグラウンド実行する。
    public class MeteorDetectionController : IDisposable
    {
        private readonly MeteorDetector _detector = new();
        private readonly object _lock = new();
        private List<Mat> _window = new();
        private DateTime _windowStart;
        private int _detectCount;
        private double _fps = 30;
        private bool _disposed;

        public double Exposure { get; set; } = 1.0;

        public int MinLineLength
        {
            get => _detector.MinLineLength;
            set => _detector.MinLineLength = value;
        }

        public string OutputDirectory { get; set; } = ".";
        public bool HasMask => _detector.HasMask;
        public int DetectCount => _detectCount;

        public event EventHandler<MeteorDetectedEventArgs>? MeteorDetected;
        public event EventHandler<string>? ErrorOccurred;

        public void LoadMask(string path) => _detector.LoadMask(path);
        public void ClearMask() => _detector.ClearMask();

        // キャプチャ開始時に呼び出し、内部状態をリセットする。
        public void Reset()
        {
            lock (_lock)
            {
                foreach (var m in _window) m.Dispose();
                _window.Clear();
            }
            _detectCount = 0;
        }

        // BGRAのフレームデータを追加する。露出時間分たまったら非同期で検出処理を行う。
        public void AddFrame(byte[] bgraData, int width, int height, double fps)
        {
            _fps = fps > 0 ? fps : 30;

            using var bgra = new Mat(height, width, MatType.CV_8UC4);
            System.Runtime.InteropServices.Marshal.Copy(bgraData, 0, bgra.Data, bgraData.Length);
            var bgr = new Mat();
            Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);

            List<Mat>? snapshot = null;

            lock (_lock)
            {
                if (_window.Count == 0) _windowStart = DateTime.Now;
                _window.Add(bgr);

                if ((DateTime.Now - _windowStart).TotalSeconds >= Exposure)
                {
                    snapshot = _window;
                    _window = new List<Mat>();
                }
            }

            if (snapshot != null)
            {
                var frames = snapshot;
                Task.Run(() => ProcessWindow(frames));
            }
        }

        private void ProcessWindow(List<Mat> images)
        {
            try
            {
                if (images.Count > 2)
                {
                    using var composite = MeteorDetector.Brightest(images);
                    var diffs = _detector.Diff(images);

                    using var diffComposite = MeteorDetector.Brightest(diffs);
                    foreach (var d in diffs) d.Dispose();

                    var lines = _detector.Detect(diffComposite);
                    if (lines != null)
                    {
                        var now = DateTime.Now;
                        _detectCount++;

                        Directory.CreateDirectory(OutputDirectory);
                        string timestamp = now.ToString("yyyyMMdd_HHmmss");
                        string jpgPath = Path.Combine(OutputDirectory, timestamp + ".jpg");
                        Cv2.ImWrite(jpgPath, composite);

                        string diffPath = Path.Combine(OutputDirectory, timestamp + "_diff.jpg");
                        Cv2.ImWrite(diffPath, diffComposite);

                        string mp4Path = Path.Combine(OutputDirectory, timestamp + "_movie.mp4");
                        SaveMovie(images, mp4Path);

                        MeteorDetected?.Invoke(this, new MeteorDetectedEventArgs
                        {
                            Count = _detectCount,
                            ImagePath = jpgPath,
                            DiffImagePath = diffPath,
                            MoviePath = mp4Path
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, ex.Message);
            }
            finally
            {
                foreach (var m in images) m.Dispose();
            }
        }

        private void SaveMovie(List<Mat> frames, string path)
        {
            int fourcc = VideoWriter.FourCC('m', 'p', '4', 'v');
            var size = new OpenCvSharp.Size(frames[0].Width, frames[0].Height);
            using var writer = new VideoWriter(path, fourcc, _fps, size);
            foreach (var f in frames)
                writer.Write(f);
        }

        public void Dispose()
        {
            if (_disposed) return;
            Reset();
            _detector.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
