using OpenCvSharp;
using System;

namespace HDMICapture.Core
{
    // 映像右下に検出件数・検出時刻をオーバーレイ表示する。
    // テキスト内容が変わった時だけ文字を再描画してキャッシュし、
    // 毎フレームはキャッシュ画素の矩形コピーのみで済むよう軽量化している。
    //
    // 注意: Cv2.PutTextが使うOpenCV内蔵フォント(Hershey系)は日本語を描画できない
    // (文字化けする)ため、オーバーレイ文字列は英数字のみを想定している。
    public class OverlayRenderer
    {
        private readonly object _lock = new();
        private byte[]? _overlayPixels;
        private int _overlayWidth;
        private int _overlayHeight;
        private string _cachedText = string.Empty;
        private bool _cachedHighlight;
        private int _lastFrameHeight;

        // 微調整用パラメータ。基準サイズ(1080p)に対する倍率と、右下からの余白(px)。
        public double FontScaleMultiplier { get; private set; } = 1.0;
        public int MarginX { get; private set; } = 12;
        public int MarginY { get; private set; } = 12;
        public HersheyFonts FontFace { get; private set; } = HersheyFonts.HersheySimplex;

        // highlight=true の間は文字を黄色で表示する(飛行機の疑いを示す)。
        public void UpdateText(string text, int frameHeight, bool highlight = false)
        {
            lock (_lock)
            {
                _lastFrameHeight = frameHeight;
                if (text == _cachedText && highlight == _cachedHighlight) return;
                _cachedText = text;
                _cachedHighlight = highlight;
                Render(text, frameHeight);
            }
        }

        // 現在の文字列はそのままで、色だけを切り替える。
        public void SetHighlight(bool highlight)
        {
            lock (_lock)
            {
                if (highlight == _cachedHighlight) return;
                _cachedHighlight = highlight;

                if (!string.IsNullOrEmpty(_cachedText) && _lastFrameHeight > 0)
                    Render(_cachedText, _lastFrameHeight);
            }
        }

        // フォントサイズ倍率・余白・フォント種類を変更する。直ちに再描画される。
        public void UpdateStyle(double fontScaleMultiplier, int marginX, int marginY, HersheyFonts fontFace)
        {
            lock (_lock)
            {
                FontScaleMultiplier = fontScaleMultiplier;
                MarginX = marginX;
                MarginY = marginY;
                FontFace = fontFace;

                if (!string.IsNullOrEmpty(_cachedText) && _lastFrameHeight > 0)
                    Render(_cachedText, _lastFrameHeight);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _overlayPixels = null;
                _cachedText = string.Empty;
                _cachedHighlight = false;
            }
        }

        private void Render(string text, int frameHeight)
        {
            HersheyFonts fontFace = FontFace;
            double fontScale = Math.Max(0.3, frameHeight / 1080.0 * 0.9 * FontScaleMultiplier);
            int thickness = Math.Max(1, (int)(frameHeight / 1080.0 * 2 * FontScaleMultiplier));

            var textSize = Cv2.GetTextSize(text, fontFace, fontScale, thickness, out int baseline);
            int paddingX = 10, paddingY = 8;
            int w = textSize.Width + paddingX * 2;
            int h = textSize.Height + baseline + paddingY * 2;

            // 不透明な黒背景 + 白文字。ROIをそのまま上書きコピーするだけなので
            // 半透明にすると背景側の元映像とではなくアプリ背景と混ざってしまう。
            using var mat = new Mat(h, w, MatType.CV_8UC4, new Scalar(0, 0, 0, 255));
            var origin = new Point(paddingX, h - paddingY - baseline);
            var textColor = _cachedHighlight ? new Scalar(0, 255, 255, 255) : new Scalar(255, 255, 255, 255);
            Cv2.PutText(mat, text, origin, fontFace, fontScale, textColor, thickness, LineTypes.AntiAlias);

            byte[] pixels = new byte[w * h * 4];
            System.Runtime.InteropServices.Marshal.Copy(mat.Data, pixels, 0, pixels.Length);

            _overlayPixels = pixels;
            _overlayWidth = w;
            _overlayHeight = h;
        }

        // BGRAフレームバッファに直接、右下へオーバーレイを焼き込む(その場で上書き)。
        public void ApplyToFrame(byte[] bgraData, int frameWidth, int frameHeight)
        {
            byte[]? overlay;
            int ow, oh, marginX, marginY;
            lock (_lock)
            {
                overlay = _overlayPixels;
                ow = _overlayWidth;
                oh = _overlayHeight;
                marginX = MarginX;
                marginY = MarginY;
            }
            if (overlay == null) return;

            int destX = Math.Max(0, frameWidth - ow - marginX);
            int destY = Math.Max(0, frameHeight - oh - marginY);
            int copyWidth = Math.Min(ow, frameWidth - destX);
            if (copyWidth <= 0) return;

            for (int row = 0; row < oh; row++)
            {
                int destYRow = destY + row;
                if (destYRow < 0 || destYRow >= frameHeight) continue;

                int srcOffset = row * ow * 4;
                int destOffset = (destYRow * frameWidth + destX) * 4;
                Buffer.BlockCopy(overlay, srcOffset, bgraData, destOffset, copyWidth * 4);
            }
        }
    }
}
