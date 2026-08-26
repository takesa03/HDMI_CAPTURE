using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace HDMICapture.Core
{
    public class NumberedExportOptions
    {
        // System.Drawing.FontFamilyの名前(例: "Meiryo UI")。OSにインストールされた
        // 実フォントを使う。見つからない場合はGenericSansSerifにフォールバックする。
        public string FontFamilyName { get; set; } = "Segoe UI";
        public double FontScaleMultiplier { get; set; } = 1.0;
        public int MarginX { get; set; } = 20;
        public int MarginY { get; set; } = 20;
    }

    // 検出一覧の各項目(比較明合成/差分/動画)に、一覧の並び順(No.1, No.2, ...)と
    // 同じ通し番号を右上に焼き込んで別フォルダへ書き出す機能。
    //
    // OpenCVのCv2.PutTextは内蔵のHersheyベクターフォントしか描画できず、OSに
    // インストールされた実フォント(游ゴシック等)を使えない。そのため、番号は
    // GDI+(System.Drawing)でアウトライン付きの透過ビットマップとして描画し、
    // それをMatへアルファ合成する方式にしている。
    // GDI+はスレッド制約が無いため、動画の一括書き出し(バックグラウンドスレッド)でも
    // そのまま使える(WPFのRenderTargetBitmapはSTAスレッドが必要なため不採用)。
    public static class NumberedExporter
    {
        // 画面右上に番号(数字のみ)を焼き込む。フレームへ直接描画する(破壊的)。
        public static void DrawNumber(Mat frame, int number, NumberedExportOptions options)
        {
            string text = number.ToString();
            float fontSizePx = (float)(frame.Height / 1080.0 * 56.0 * options.FontScaleMultiplier);

            var (glyph, glyphW, glyphH) = RenderGlyphBitmap(text, options.FontFamilyName, fontSizePx);
            if (glyphW <= 0 || glyphH <= 0) return;

            int destX = frame.Width - options.MarginX - glyphW;
            int destY = options.MarginY;
            CompositeGlyph(frame, glyph, glyphW, glyphH, destX, destY);
        }

        // 番号(白文字+黒縁取り)をアウトライン付きで透過ビットマップに描画する。
        // 先に実寸を測ってから正しいサイズのビットマップへ描き直す2パス構成。
        private static (byte[] bgra, int width, int height) RenderGlyphBitmap(string text, string fontFamilyName, float fontSizePx)
        {
            using var fontFamily = ResolveFontFamily(fontFamilyName);

            using var path = new GraphicsPath();
            path.AddString(text, fontFamily, (int)FontStyle.Bold, fontSizePx, new PointF(0, 0), StringFormat.GenericTypographic);

            float outlineWidth = Math.Max(1.5f, fontSizePx * 0.08f);
            using var measurePen = new Pen(Color.Black, outlineWidth) { LineJoin = LineJoin.Round };
            var bounds = path.GetBounds(new Matrix(), measurePen);
            if (bounds.Width <= 0 || bounds.Height <= 0) return (Array.Empty<byte>(), 0, 0);

            const int pad = 2;
            int w = (int)Math.Ceiling(bounds.Width) + pad * 2;
            int h = (int)Math.Ceiling(bounds.Height) + pad * 2;

            using var m = new Matrix();
            m.Translate(pad - bounds.X, pad - bounds.Y);
            path.Transform(m);

            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var pen = new Pen(Color.Black, outlineWidth) { LineJoin = LineJoin.Round };
                g.DrawPath(pen, path);
                g.FillPath(Brushes.White, path);
            }

            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] buffer = new byte[data.Stride * h];
                Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
                return (buffer, w, h);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private static FontFamily ResolveFontFamily(string name)
        {
            try
            {
                return new FontFamily(name);
            }
            catch (ArgumentException)
            {
                return FontFamily.GenericSansSerif;
            }
        }

        // BGRA(ストレートアルファ)のグリフビットマップを、フレーム(BGR)の指定位置へ
        // アルファ合成する。フレーム範囲外へはみ出す部分は自動的に切り詰める。
        private static void CompositeGlyph(Mat frame, byte[] glyphBgra, int glyphW, int glyphH, int destX, int destY)
        {
            int srcX0 = Math.Max(0, -destX);
            int srcY0 = Math.Max(0, -destY);
            int dstX0 = Math.Max(0, destX);
            int dstY0 = Math.Max(0, destY);
            int copyW = Math.Min(glyphW - srcX0, frame.Width - dstX0);
            int copyH = Math.Min(glyphH - srcY0, frame.Height - dstY0);
            if (copyW <= 0 || copyH <= 0) return;

            using var roi = new Mat(frame, new Rect(dstX0, dstY0, copyW, copyH));
            int roiStep = (int)roi.Step();
            int roiChannels = roi.Channels();
            byte[] roiBytes = new byte[roiStep * copyH];
            Marshal.Copy(roi.Data, roiBytes, 0, roiBytes.Length);

            for (int row = 0; row < copyH; row++)
            {
                int glyphRowOffset = ((srcY0 + row) * glyphW + srcX0) * 4;
                int roiRowOffset = row * roiStep;

                for (int col = 0; col < copyW; col++)
                {
                    int gi = glyphRowOffset + col * 4;
                    byte alpha = glyphBgra[gi + 3];
                    if (alpha == 0) continue;

                    double a = alpha / 255.0;
                    int ri = roiRowOffset + col * roiChannels;
                    roiBytes[ri] = (byte)(glyphBgra[gi] * a + roiBytes[ri] * (1 - a));
                    roiBytes[ri + 1] = (byte)(glyphBgra[gi + 1] * a + roiBytes[ri + 1] * (1 - a));
                    roiBytes[ri + 2] = (byte)(glyphBgra[gi + 2] * a + roiBytes[ri + 2] * (1 - a));
                }
            }

            Marshal.Copy(roiBytes, 0, roi.Data, roiBytes.Length);
        }

        // 設定ダイアログのプレビュー用。画像があればそれを下敷きに、無ければ黒画面に
        // 番号を焼き込み、PNGエンコードしたバイト列を返す。
        public static byte[] RenderPreviewPng(string? sourceImagePath, int number, NumberedExportOptions options,
            int fallbackWidth = 1920, int fallbackHeight = 1080)
        {
            using Mat img = (!string.IsNullOrWhiteSpace(sourceImagePath) && File.Exists(sourceImagePath))
                ? Cv2.ImRead(sourceImagePath, ImreadModes.Color)
                : new Mat(fallbackHeight, fallbackWidth, MatType.CV_8UC3, Scalar.All(0));

            DrawNumber(img, number, options);
            return img.ImEncode(".png");
        }

        // 1枚の画像に番号を焼き込んで別ファイルへ保存する。
        public static void ExportImage(string sourcePath, string destPath, int number, NumberedExportOptions options)
        {
            using var img = Cv2.ImRead(sourcePath, ImreadModes.Color);
            if (img.Empty())
                throw new InvalidOperationException($"画像を読み込めません: {sourcePath}");

            DrawNumber(img, number, options);
            Cv2.ImWrite(destPath, img);
        }

        // 動画の全フレームに番号を焼き込んで別ファイルへ保存する。
        public static void ExportMovie(string sourcePath, string destPath, int number, NumberedExportOptions options)
        {
            using var capture = new VideoCapture(sourcePath);
            if (!capture.IsOpened())
                throw new InvalidOperationException($"動画を読み込めません: {sourcePath}");

            int width = (int)capture.Get(VideoCaptureProperties.FrameWidth);
            int height = (int)capture.Get(VideoCaptureProperties.FrameHeight);
            double fps = capture.Get(VideoCaptureProperties.Fps);
            if (fps <= 0) fps = 30;

            int fourcc = VideoWriter.FourCC('m', 'p', '4', 'v');
            using var writer = new VideoWriter(destPath, fourcc, fps, new OpenCvSharp.Size(width, height));

            using var frame = new Mat();
            while (capture.Read(frame))
            {
                if (frame.Empty()) break;
                DrawNumber(frame, number, options);
                writer.Write(frame);
            }
        }

        // 検出一覧全件について、一覧の並び順(1始まり)を番号として
        // 比較明合成・差分・動画の3種を出力先フォルダへ書き出す。
        // 元ファイルが存在しないものはスキップする。
        public static void ExportAll(
            IReadOnlyList<MeteorDetectionRecord> records,
            string outputDir,
            NumberedExportOptions options,
            Action<int, int>? onProgress = null)
        {
            Directory.CreateDirectory(outputDir);

            for (int i = 0; i < records.Count; i++)
            {
                var record = records[i];
                int number = i + 1;

                if (!string.IsNullOrWhiteSpace(record.PhotoPath) && File.Exists(record.PhotoPath))
                    ExportImage(record.PhotoPath, Path.Combine(outputDir, Path.GetFileName(record.PhotoPath)), number, options);

                if (!string.IsNullOrWhiteSpace(record.DiffPath) && File.Exists(record.DiffPath))
                    ExportImage(record.DiffPath, Path.Combine(outputDir, Path.GetFileName(record.DiffPath)), number, options);

                if (!string.IsNullOrWhiteSpace(record.MoviePath) && File.Exists(record.MoviePath))
                    ExportMovie(record.MoviePath, Path.Combine(outputDir, Path.GetFileName(record.MoviePath)), number, options);

                onProgress?.Invoke(number, records.Count);
            }
        }
    }
}
