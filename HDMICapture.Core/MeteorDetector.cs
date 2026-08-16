using OpenCvSharp;
using System;
using System.Collections.Generic;

namespace HDMICapture.Core
{
    // Python版 atomcam.py の brightest()/diff()/detect() を移植したコア検出ロジック。
    // MeteorDetectTest プロジェクトで動作検証済み。
    public class MeteorDetector : IDisposable
    {
        public int MinLineLength { get; set; } = 30;

        // 映像周囲の除外幅(px)。安価なキャプチャデバイスでは映像の端に
        // 固定的な黒帯やノイズが乗ることがあり、誤検出の原因になるため
        // マスクの有無に関わらず常時これを除外する。
        public int BorderExclusionPixels { get; set; } = 5;

        private Mat? _mask;

        public bool HasMask => _mask != null;

        public void LoadMask(string path)
        {
            var loaded = Cv2.ImRead(path, ImreadModes.Color);
            if (loaded.Empty())
            {
                loaded.Dispose();
                throw new InvalidOperationException($"マスク画像を読み込めません: {path}");
            }

            _mask?.Dispose();
            _mask = loaded;
        }

        public void ClearMask()
        {
            _mask?.Dispose();
            _mask = null;
        }

        // 比較明合成: 複数フレームの画素ごとの最大値を取る
        public static Mat Brightest(IReadOnlyList<Mat> images)
        {
            Mat output = images[0].Clone();
            for (int i = 1; i < images.Count; i++)
            {
                Cv2.Max(output, images[i], output);
            }
            return output;
        }

        // 連続フレーム間の差分画像リストを作成する。
        // bitwise_or で除外領域(ユーザー指定マスク + 映像周囲の外周)を白飛びさせて
        // から差分を取り、ノイズ源として現れないようにする。
        //
        // Python版は zip(img_list[:-2], img_list[1:]) を使っており、
        // リスト末尾の1枚は比較に含めない(N枚ならN-2組の差分ペア)。
        // ペア数が増えるほど比較明合成(max)でノイズのピークを拾いやすくなり
        // 差分画像が明るくなるため、Python版と同じ範囲に合わせる。
        public List<Mat> Diff(IReadOnlyList<Mat> images)
        {
            var result = new List<Mat>();

            using var effectiveMask = BuildEffectiveMask(images[0].Size());

            for (int i = 0; i < images.Count - 2; i++)
            {
                Mat img1 = images[i];
                Mat img2 = images[i + 1];

                using var m1 = new Mat();
                using var m2 = new Mat();
                Cv2.BitwiseOr(img1, effectiveMask, m1);
                Cv2.BitwiseOr(img2, effectiveMask, m2);

                var d = new Mat();
                Cv2.Subtract(m1, m2, d);
                result.Add(d);
            }

            return result;
        }

        // 外周除外マスクを作成し、ユーザー指定マスクがあれば合成する。
        private Mat BuildEffectiveMask(Size size)
        {
            var border = new Mat(size, MatType.CV_8UC3, Scalar.All(0));
            if (BorderExclusionPixels > 0)
            {
                Cv2.Rectangle(
                    border,
                    new Point(0, 0),
                    new Point(size.Width - 1, size.Height - 1),
                    Scalar.All(255),
                    BorderExclusionPixels);
            }

            if (_mask == null)
                return border;

            var combined = new Mat();
            Cv2.BitwiseOr(border, _mask, combined);
            border.Dispose();
            return combined;
        }

        // 画像上の直線パターン(流星候補)を検出する。
        // GaussianBlur -> Canny -> HoughLinesP という原典と同じ処理順。
        public LineSegmentPoint[]? Detect(Mat img)
        {
            using var blur = new Mat();
            Cv2.GaussianBlur(img, blur, new Size(5, 5), 0);

            using var canny = new Mat();
            Cv2.Canny(blur, canny, 100, 200, 3);

            var lines = Cv2.HoughLinesP(canny, 1, Math.PI / 180, 25, MinLineLength, 5);
            return lines.Length > 0 ? lines : null;
        }

        public void Dispose()
        {
            _mask?.Dispose();
            _mask = null;
            GC.SuppressFinalize(this);
        }
    }
}
