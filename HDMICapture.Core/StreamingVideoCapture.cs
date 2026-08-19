using OpenCvSharp;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace HDMICapture.Core
{
    // RTSPストリーム(例: ATOM Cam2のatomcam_tools/メーカ公式RTSP)を流星検出のソースとして
    // 読み込む。meteor-detect(Python版, atomcam.py)のAtomCamクラスの接続・再接続方式を
    // 踏襲している: cv2.VideoCapture(url)でRTSPを開き、フレーム取得に失敗したら
    // 再接続を試みながら読み込みを継続する(ネットワーク要因での瞬断を復旧するため)。
    // フレームは実時間で流れてくるため、タイムスタンプ・ウィンドウ処理は
    // ライブキャプチャ(OpenCvVideoCapture)と同じ扱いでよい。
    public class StreamingVideoCapture : IVideoSource
    {
        private const int ReconnectDelayMs = 5000;

        private OpenCvSharp.VideoCapture? _capture;
        private volatile bool _isRunning;
        private bool _disposed;
        private readonly object _lock = new();
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _captureTask;
        private readonly string _url;

        public event EventHandler<VideoFrameEventArgs>? FrameReceived;
        public event EventHandler<string>? ErrorOccurred;

        public string DeviceName => _url;
        public bool IsRunning => _isRunning;
        public int Width => ActualWidth;
        public int Height => ActualHeight;
        public double FrameRate => ActualFrameRate;
        public int ActualWidth { get; private set; }
        public int ActualHeight { get; private set; }
        public double ActualFrameRate { get; private set; }

        public StreamingVideoCapture(string url)
        {
            _url = url;
        }

        public void Start()
        {
            if (_isRunning) return;

            lock (_lock)
            {
                Connect();

                _isRunning = true;
                _cancellationTokenSource = new CancellationTokenSource();
                var token = _cancellationTokenSource.Token;
                _captureTask = Task.Run(() => CaptureFrames(token), token);
            }
        }

        // 初回接続・再接続の両方から呼ばれる。失敗時は例外を投げる(初回はStart()の
        // 呼び出し元へ、再接続時はCaptureFrames内でキャッチしてリトライする)。
        private void Connect()
        {
            _capture?.Release();
            _capture?.Dispose();

            _capture = new OpenCvSharp.VideoCapture(_url, VideoCaptureAPIs.FFMPEG);
            if (!_capture.IsOpened())
            {
                _capture.Dispose();
                _capture = null;
                throw new InvalidOperationException($"RTSPストリームに接続できませんでした: {_url}");
            }

            ActualWidth = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
            ActualHeight = (int)_capture.Get(VideoCaptureProperties.FrameHeight);

            // atomcam.py同様、環境によってはFPSプロパティが異常値(例: 9000)を
            // 返すことがあるため上限を設けてクランプする。
            double fps = _capture.Get(VideoCaptureProperties.Fps);
            ActualFrameRate = (fps > 0 && fps <= 120) ? fps : 30.0;
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

        private void CaptureFrames(CancellationToken token)
        {
            using var frame = new Mat();
            using var bgraFrame = new Mat();

            while (!token.IsCancellationRequested && _isRunning)
            {
                try
                {
                    if (_capture == null || !_capture.Read(frame) || frame.Empty())
                    {
                        if (!_isRunning) break;

                        // RTSPはネットワーク要因で一時的に切断されることがあるため、
                        // atomcam.pyと同様にすぐ諦めず再接続して読み込みを継続する。
                        ErrorOccurred?.Invoke(this, "RTSP接続が切断されました。再接続します...");
                        try
                        {
                            Connect();
                        }
                        catch (Exception connectEx)
                        {
                            ErrorOccurred?.Invoke(this, $"再接続に失敗しました: {connectEx.Message}");
                        }

                        WaitCancellable(token, ReconnectDelayMs);
                        continue;
                    }

                    Cv2.CvtColor(frame, bgraFrame, ColorConversionCodes.BGR2BGRA);
                    byte[] frameData = new byte[bgraFrame.Total() * bgraFrame.ElemSize()];
                    System.Runtime.InteropServices.Marshal.Copy(bgraFrame.Data, frameData, 0, frameData.Length);

                    FrameReceived?.Invoke(this, new VideoFrameEventArgs
                    {
                        FrameData = frameData,
                        Width = bgraFrame.Width,
                        Height = bgraFrame.Height,
                        Timestamp = DateTime.Now.Ticks
                    });
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                        ErrorOccurred?.Invoke(this, $"RTSP読み込みエラー: {ex.Message}");
                }
            }
        }

        private static void WaitCancellable(CancellationToken token, int totalMs)
        {
            const int stepMs = 100;
            for (int waited = 0; waited < totalMs && !token.IsCancellationRequested; waited += stepMs)
                Thread.Sleep(stepMs);
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
