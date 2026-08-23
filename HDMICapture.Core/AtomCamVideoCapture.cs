using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HDMICapture.Core
{
    public class AtomCamProgressEventArgs : EventArgs
    {
        public int FileIndex { get; set; }
        public int FileCount { get; set; }
        public string CurrentFile { get; set; } = "";
        public DateTime VideoTime { get; set; }
    }

    // ATOM Cam形式(yyyyMMdd/hh/mm.mp4)のフォルダ階層に保存された録画ファイル群を、
    // 1本の連続した映像として順番に読み進める。FileVideoCaptureと同様にIVideoSourceを
    // 実装することで、プレビュー表示・流星検出への経路をキャプチャモードと共通化できる。
    //
    // 各フレームのタイムスタンプは、実時間の経過ではなくファイルパスから求めた
    // 「そのファイルの録画開始時刻(yyyyMMdd/hh/mmの分の00秒)+ フレーム番号/fps」を使う。
    // ファイルが切り替わるたびに次ファイルの実時刻へ算出し直すため、録画の欠落
    // (対応する分のファイルが存在しない)があっても、そこだけ露出ウィンドウが自然に
    // 区切られるだけで済み、タイムスタンプが実際の経過時間からずれ続けることはない。
    public class AtomCamVideoCapture : IVideoSource
    {
        private readonly string _rootFolder;
        private readonly List<(string Path, DateTime StartTime)> _files;
        private volatile bool _isRunning;
        private bool _disposed;
        private readonly object _lock = new();
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _captureTask;

        public event EventHandler<VideoFrameEventArgs>? FrameReceived;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<AtomCamProgressEventArgs>? ProgressChanged;
        // ユーザーによるStop()ではなく、全ファイルを読み切って自然に終了したときのみ発火する。
        public event EventHandler? PlaybackFinished;

        public string DeviceName => _rootFolder;
        public bool IsRunning => _isRunning;
        public int Width => ActualWidth;
        public int Height => ActualHeight;
        public double FrameRate => ActualFrameRate;
        public int ActualWidth { get; private set; }
        public int ActualHeight { get; private set; }
        public double ActualFrameRate { get; private set; }
        public int FileCount => _files.Count;

        public AtomCamVideoCapture(string rootFolder)
        {
            _rootFolder = rootFolder;
            _files = ScanFiles(rootFolder);
            if (_files.Count == 0)
            {
                throw new InvalidOperationException(
                    $"yyyyMMdd\\hh\\mm.mp4 形式の動画ファイルが見つかりませんでした: {rootFolder}");
            }
        }

        // rootFolder/yyyyMMdd/hh/mm.mp4 を走査し、日時の昇順に並べる。
        private static List<(string, DateTime)> ScanFiles(string rootFolder)
        {
            var results = new List<(string, DateTime)>();

            foreach (var dateDir in Directory.GetDirectories(rootFolder))
            {
                string dateName = Path.GetFileName(dateDir);
                if (!DateTime.TryParseExact(dateName, "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date))
                    continue;

                foreach (var hourDir in Directory.GetDirectories(dateDir))
                {
                    string hourName = Path.GetFileName(hourDir);
                    if (!Regex.IsMatch(hourName, @"^\d{2}$") || !int.TryParse(hourName, out int hour) || hour > 23)
                        continue;

                    foreach (var file in Directory.GetFiles(hourDir))
                    {
                        if (!string.Equals(Path.GetExtension(file), ".mp4", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string minuteName = Path.GetFileNameWithoutExtension(file);
                        if (!Regex.IsMatch(minuteName, @"^\d{2}$") || !int.TryParse(minuteName, out int minute) || minute > 59)
                            continue;

                        results.Add((file, date.AddHours(hour).AddMinutes(minute)));
                    }
                }
            }

            results.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            return results;
        }

        public void Start()
        {
            if (_isRunning) return;

            lock (_lock)
            {
                // 映像サイズ・フレームレートは先頭ファイルの値を全体で使い回す
                // (同一カメラの連続録画である前提のため、ファイルごとの再チェックはしない)。
                using (var probe = new OpenCvSharp.VideoCapture(_files[0].Path))
                {
                    if (!probe.IsOpened())
                        throw new InvalidOperationException($"ファイルを開けませんでした: {_files[0].Path}");

                    ActualWidth = (int)probe.Get(VideoCaptureProperties.FrameWidth);
                    ActualHeight = (int)probe.Get(VideoCaptureProperties.FrameHeight);
                    ActualFrameRate = probe.Get(VideoCaptureProperties.Fps);
                    if (ActualFrameRate <= 0) ActualFrameRate = 15.0; // ATOM Camの一般的な既定値
                }

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
                DateTime lastProgressReport = DateTime.MinValue;

                for (int fileIndex = 0; fileIndex < _files.Count; fileIndex++)
                {
                    if (token.IsCancellationRequested || !_isRunning) break;

                    var (path, fileStartTime) = _files[fileIndex];

                    using var capture = new OpenCvSharp.VideoCapture(path);
                    if (!capture.IsOpened())
                    {
                        ErrorOccurred?.Invoke(this, $"ファイルを開けませんでした(スキップします): {path}");
                        continue;
                    }

                    double fps = capture.Get(VideoCaptureProperties.Fps);
                    if (fps <= 0) fps = ActualFrameRate;

                    int frameIndex = 0;
                    while (!token.IsCancellationRequested && _isRunning)
                    {
                        if (!capture.Read(frame) || frame.Empty())
                            break;

                        Cv2.CvtColor(frame, bgraFrame, ColorConversionCodes.BGR2BGRA);
                        byte[] frameData = new byte[bgraFrame.Total() * bgraFrame.ElemSize()];
                        System.Runtime.InteropServices.Marshal.Copy(bgraFrame.Data, frameData, 0, frameData.Length);

                        DateTime frameTime = fileStartTime.AddSeconds(frameIndex / fps);

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
                            ProgressChanged?.Invoke(this, new AtomCamProgressEventArgs
                            {
                                FileIndex = fileIndex + 1,
                                FileCount = _files.Count,
                                CurrentFile = path,
                                VideoTime = frameTime
                            });
                        }
                    }

                    if (fileIndex == _files.Count - 1 && !token.IsCancellationRequested && _isRunning)
                        reachedEnd = true;
                }
            }
            catch (Exception ex)
            {
                if (_isRunning)
                    ErrorOccurred?.Invoke(this, $"ATOM Camファイル処理エラー: {ex.Message}");
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
            }
            catch { }

            _cancellationTokenSource = null;
            _captureTask = null;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
