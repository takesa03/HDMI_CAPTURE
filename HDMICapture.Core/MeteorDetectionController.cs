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
        public MeteorDetectionRecord Record { get; set; } = new();
    }

    // ライブキャプチャのBGRAフレームを受け取り、露出時間分バッファリングして
    // 流星検出(MeteorDetector)を非同期に実行するコントローラ。
    // 重い検出処理(diff/Canny/HoughLinesP/画像・動画保存)はキャプチャスレッドを
    // ブロックしないよう Task.Run でバックグラウンド実行する。
    public class MeteorDetectionController : IDisposable
    {
        private class FrameWindow
        {
            public readonly List<Mat> Frames = new();
            public DateTime StartTime;
        }

        private readonly MeteorDetector _detector = new();
        private readonly object _lock = new();
        private FrameWindow _window = new();
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

        // 保存する動画に焼き込むタイムコードのフォント。ライブオーバーレイの設定と揃える。
        public HersheyFonts TimecodeFontFace { get; set; } = HersheyFonts.HersheySimplex;
        public double TimecodeFontScaleMultiplier { get; set; } = 1.0;

        public event EventHandler<MeteorDetectedEventArgs>? MeteorDetected;
        public event EventHandler<string>? ErrorOccurred;

        public void LoadMask(string path) => _detector.LoadMask(path);
        public void ClearMask() => _detector.ClearMask();

        // キャプチャ開始時に呼び出し、内部状態をリセットする。
        public void Reset()
        {
            lock (_lock)
            {
                foreach (var m in _window.Frames) m.Dispose();
                _window = new FrameWindow();
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

            FrameWindow? snapshot = null;

            lock (_lock)
            {
                if (_window.Frames.Count == 0) _window.StartTime = DateTime.Now;
                _window.Frames.Add(bgr);

                if ((DateTime.Now - _window.StartTime).TotalSeconds >= Exposure)
                {
                    snapshot = _window;
                    _window = new FrameWindow();
                }
            }

            if (snapshot != null)
            {
                var w = snapshot;
                Task.Run(() => ProcessWindow(w));
            }
        }

        private void ProcessWindow(FrameWindow window)
        {
            var images = window.Frames;
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

                        // ファイル名・記録上の「検出日時」は処理完了時刻(now)ではなく
                        // 露出(バッファリング)を開始した時刻を使う。
                        Directory.CreateDirectory(OutputDirectory);
                        string timestamp = window.StartTime.ToString("yyyyMMdd_HHmmss");
                        string jpgPath = Path.Combine(OutputDirectory, timestamp + ".jpg");
                        Cv2.ImWrite(jpgPath, composite);

                        // 差分画像には、検出された線分(流星と判定した位置)を赤線で重ねて保存する。
                        // MeteorDetectTestの--test-samplesで実装した目視確認用のマーキングと同じ。
                        using var diffMarked = new Mat();
                        Cv2.CvtColor(diffComposite, diffMarked, ColorConversionCodes.GRAY2BGR);
                        foreach (var l in lines)
                            Cv2.Line(diffMarked, l.P1, l.P2, new Scalar(0, 0, 255), 2, LineTypes.AntiAlias);

                        string diffPath = Path.Combine(OutputDirectory, timestamp + "_diff.jpg");
                        Cv2.ImWrite(diffPath, diffMarked);

                        string mp4Path = Path.Combine(OutputDirectory, timestamp + "_movie.mp4");
                        SaveMovie(images, mp4Path, window.StartTime);

                        var record = new MeteorDetectionRecord
                        {
                            ExposureStartTime = window.StartTime,
                            DetectedTime = now,
                            PhotoPath = jpgPath,
                            DiffPath = diffPath,
                            MoviePath = mp4Path
                        };
                        MeteorDetectionListStore.Append(OutputDirectory, record);

                        MeteorDetected?.Invoke(this, new MeteorDetectedEventArgs
                        {
                            Count = _detectCount,
                            Record = record
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

        private void SaveMovie(List<Mat> frames, string path, DateTime startTime)
        {
            int fourcc = VideoWriter.FourCC('m', 'p', '4', 'v');
            var size = new OpenCvSharp.Size(frames[0].Width, frames[0].Height);
            using var writer = new VideoWriter(path, fourcc, _fps, size);

            int framesPerSecond = Math.Max(1, (int)Math.Round(_fps));
            for (int i = 0; i < frames.Count; i++)
            {
                DateTime frameTime = startTime.AddSeconds(i / _fps);
                int frameNumber = i % framesPerSecond;
                DrawTimecode(frames[i], frameTime, frameNumber);
                writer.Write(frames[i]);
            }
        }

        // 露出開始時刻を基準に、そのフレームの実時刻(秒単位)+その秒内での通し番号を
        // 「yyyy/MM/dd HH:mm:ss.フレーム番号」の形式で映像下段中央に焼き込む。
        // フレーム番号があることで、露出開始からの経過時間を正確に把握できる。
        // フォント種類・サイズはライブプレビュー/NDI側のオーバーレイ設定に合わせる。
        private void DrawTimecode(Mat frame, DateTime frameTime, int frameNumber)
        {
            string text = $"{frameTime:yyyy/MM/dd HH:mm:ss}.{frameNumber:D2}";

            double fontScale = Math.Max(0.3, frame.Height / 1080.0 * 0.9 * TimecodeFontScaleMultiplier);
            int thickness = Math.Max(1, (int)(frame.Height / 1080.0 * 2 * TimecodeFontScaleMultiplier));

            var textSize = Cv2.GetTextSize(text, TimecodeFontFace, fontScale, thickness, out _);
            int x = (frame.Width - textSize.Width) / 2;
            int y = frame.Height - Math.Max(10, (int)(frame.Height * 0.02));

            // 背景の映像内容によらず読めるよう、黒縁取り+白文字で描画する。
            Cv2.PutText(frame, text, new Point(x, y), TimecodeFontFace, fontScale, new Scalar(0, 0, 0), thickness + 2, LineTypes.AntiAlias);
            Cv2.PutText(frame, text, new Point(x, y), TimecodeFontFace, fontScale, new Scalar(255, 255, 255), thickness, LineTypes.AntiAlias);
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
