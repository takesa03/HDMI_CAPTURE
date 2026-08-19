using OpenCvSharp;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace HDMICapture.Core
{
    public class FileProcessProgressEventArgs : EventArgs
    {
        public int CurrentFrame { get; set; }
        public int TotalFrames { get; set; }
        public DateTime VideoTime { get; set; }
    }

    // 既存の動画ファイルを流星検出のソースとして読み込む。ライブキャプチャ(OpenCvVideoCapture)と
    // 同じIVideoSourceを実装することで、プレビュー表示・流星検出へのフレーム受け渡し経路を
    // キャプチャモードと共通化できる。
    // ライブキャプチャとの違い:
    //   ・実時間を待たず、可能な限り高速にファイルを読み進める
    //   ・各フレームのタイムスタンプは実時刻ではなく「指定した撮影開始日時 + フレーム番号/fps」
    //     から算出する(MeteorDetectionController側のウィンドウ区切り判定に使われる)
    public class FileVideoCapture : IVideoSource
    {
        private OpenCvSharp.VideoCapture? _capture;
        private volatile bool _isRunning;
        private bool _disposed;
        private readonly object _lock = new();
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _captureTask;
        private readonly string _filePath;
        private readonly DateTime _startTime;

        public event EventHandler<VideoFrameEventArgs>? FrameReceived;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<FileProcessProgressEventArgs>? ProgressChanged;
        // ユーザーによるStop()ではなく、ファイル終端まで読み切って自然に終了したときのみ発火する。
        public event EventHandler? PlaybackFinished;

        public string DeviceName => _filePath;
        public bool IsRunning => _isRunning;
        public int Width => ActualWidth;
        public int Height => ActualHeight;
        public double FrameRate => ActualFrameRate;
        public int ActualWidth { get; private set; }
        public int ActualHeight { get; private set; }
        public double ActualFrameRate { get; private set; }
        public int TotalFrames { get; private set; }

        public FileVideoCapture(string filePath, DateTime startTime)
        {
            _filePath = filePath;
            _startTime = startTime;
        }

        public void Start()
        {
            if (_isRunning) return;

            lock (_lock)
            {
                _capture = new OpenCvSharp.VideoCapture(_filePath);
                if (!_capture.IsOpened())
                {
                    _capture.Dispose();
                    _capture = null;
                    throw new InvalidOperationException($"ファイルを開けませんでした: {_filePath}");
                }

                ActualWidth = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
                ActualHeight = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
                ActualFrameRate = _capture.Get(VideoCaptureProperties.Fps);
                if (ActualFrameRate <= 0) ActualFrameRate = 30.0;
                TotalFrames = (int)_capture.Get(VideoCaptureProperties.FrameCount);

                _isRunning = true;
                _cancellationTokenSource = new CancellationTokenSource();
                var token = _cancellationTokenSource.Token;
                _captureTask = Task.Run(() => ReadFrames(token), token);
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;

            lock (_lock)
            {
                _isRunning = false;
                _cancellationTokenSource?.Cancel();

                if (_captureTask != null)
                {
                    try { _captureTask.Wait(10000); }
                    catch (OperationCanceledException) { }
                    catch (AggregateException) { }
                }
            }
        }

        private void ReadFrames(CancellationToken token)
        {
            bool reachedEnd = false;
            try
            {
                using var frame = new Mat();
                using var bgraFrame = new Mat();
                int frameIndex = 0;
                DateTime lastProgressReport = DateTime.MinValue;

                while (!token.IsCancellationRequested && _isRunning && _capture != null)
                {
                    if (!_capture.Read(frame) || frame.Empty())
                    {
                        reachedEnd = true;
                        break;
                    }

                    Cv2.CvtColor(frame, bgraFrame, ColorConversionCodes.BGR2BGRA);
                    byte[] frameData = new byte[bgraFrame.Total() * bgraFrame.ElemSize()];
                    System.Runtime.InteropServices.Marshal.Copy(bgraFrame.Data, frameData, 0, frameData.Length);

                    DateTime frameTime = _startTime.AddSeconds(frameIndex / ActualFrameRate);

                    FrameReceived?.Invoke(this, new VideoFrameEventArgs
                    {
                        FrameData = frameData,
                        Width = bgraFrame.Width,
                        Height = bgraFrame.Height,
                        Timestamp = frameTime.Ticks
                    });

                    frameIndex++;

                    // 進捗イベントは毎フレームだと呼び出し側(UIスレッド)を圧迫するため間引く。
                    var nowReport = DateTime.Now;
                    if ((nowReport - lastProgressReport).TotalMilliseconds >= 200)
                    {
                        lastProgressReport = nowReport;
                        ProgressChanged?.Invoke(this, new FileProcessProgressEventArgs
                        {
                            CurrentFrame = frameIndex,
                            TotalFrames = TotalFrames,
                            VideoTime = frameTime
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                if (_isRunning)
                    ErrorOccurred?.Invoke(this, $"ファイル処理エラー: {ex.Message}");
            }
            finally
            {
                bool wasRunning = _isRunning;
                _isRunning = false;
                if (reachedEnd && wasRunning)
                    PlaybackFinished?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                Stop();
                _cancellationTokenSource?.Dispose();
                _capture?.Release();
                _capture?.Dispose();
            }
            catch { }

            _capture = null;
            _cancellationTokenSource = null;
            _captureTask = null;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
