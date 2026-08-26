using OpenCvSharp;
using System;
using System.IO;

namespace HDMICapture.Core
{
    // 検出一覧の「マスク追加」機能で使う画像処理。
    // 現在のマスク画像(未設定なら黒画像)を土台に、赤線入りの差分画像から
    // 赤色部分(検出された流星候補の軌跡)だけを抽出し、白色で加算した
    // 新しいマスク画像を作る。
    public static class MaskEditor
    {
        // 差分画像に描画された赤線(Scalar(0,0,255)、アンチエイリアス)は、
        // 背景(グレースケールをBGRに変換したもの、R=G=B)に対してRチャンネルだけが
        // 突出して高くなる。この差(R-G, R-B)がともに閾値を超える画素を
        // 「赤色部分」とみなす。
        private const int RedChannelDiffThreshold = 40;

        public static void CreateAugmentedMask(string? basePath, string diffImagePath, string outputPath)
        {
            using var diffImg = Cv2.ImRead(diffImagePath, ImreadModes.Color);
            if (diffImg.Empty())
                throw new InvalidOperationException($"差分画像を読み込めません: {diffImagePath}");

            Mat[] channels = Cv2.Split(diffImg);
            using var redMask = new Mat();
            try
            {
                using var rMinusG = new Mat();
                using var rMinusB = new Mat();
                Cv2.Subtract(channels[2], channels[1], rMinusG);
                Cv2.Subtract(channels[2], channels[0], rMinusB);

                using var maskG = new Mat();
                using var maskB = new Mat();
                Cv2.Threshold(rMinusG, maskG, RedChannelDiffThreshold, 255, ThresholdTypes.Binary);
                Cv2.Threshold(rMinusB, maskB, RedChannelDiffThreshold, 255, ThresholdTypes.Binary);

                Cv2.BitwiseAnd(maskG, maskB, redMask);
            }
            finally
            {
                foreach (var c in channels) c.Dispose();
            }

            using var baseMask = LoadBaseMask(basePath, diffImg.Size());

            using var combined = new Mat();
            Cv2.BitwiseOr(baseMask, redMask, combined);

            Cv2.ImWrite(outputPath, combined);
        }

        private static Mat LoadBaseMask(string? basePath, Size size)
        {
            if (!string.IsNullOrWhiteSpace(basePath) && File.Exists(basePath))
            {
                var loaded = Cv2.ImRead(basePath, ImreadModes.Grayscale);
                if (!loaded.Empty())
                {
                    if (loaded.Size() != size)
                    {
                        var resized = new Mat();
                        Cv2.Resize(loaded, resized, size, 0, 0, InterpolationFlags.Nearest);
                        loaded.Dispose();
                        return resized;
                    }
                    return loaded;
                }
                loaded.Dispose();
            }

            return new Mat(size, MatType.CV_8UC1, Scalar.All(0));
        }
    }
}
