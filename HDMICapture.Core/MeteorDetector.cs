using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HDMICapture.Core
{
    // Python版 atomcam.py の brightest()/diff()/detect() を移植したコア検出ロジック。
    // MeteorDetectTest プロジェクトで動作検証済み。
    public class MeteorDetector : IDisposable
    {
        public int MinLineLength { get; set; } = 30;

        // 流星の流れはじめ・流れ終わりは光量が弱く、差分画像上のコントラストが低い。
        // 固定のCanny閾値(100,200)だけだと淡い部分のエッジが閾値に届かず、
        // 軌跡の明るい中心部しか検出できないことがある。
        // 検出前に差分画像の輝度を実際の最小-最大値でストレッチし、その回のコマ分に
        // 含まれる最も強い信号を基準にダイナミックレンジを使い切ることで、
        // 淡い部分のエッジも相対的に持ち上げてCanny閾値に届きやすくする。
        public bool NormalizeBeforeDetect { get; set; } = true;
        public double CannyThreshold1 { get; set; } = 60;
        public double CannyThreshold2 { get; set; } = 150;
        public int HoughThreshold { get; set; } = 20;

        // 淡い部分でCannyのエッジが途切れても1本の軌跡として繋げられるよう、
        // 原典(5px)より広めのギャップを許容する。
        public int MaxLineGap { get; set; } = 15;

        // 検出された線分群を「1本の直線的な光跡」とみなすための判定パラメータ。
        // HoughLinesPは元々のPython版と同じ設定でも、ノイズの多い映像では
        // 方向のバラバラな短い線分をいくつも拾ってしまうことがある。
        // 最も長い線分(主軸)と同一直線上とみなせる線分だけを集めた帯領域に、
        // 画像全体のエッジ画素がどれだけ集中しているかを見ることで、
        // 単一の軌跡なのか単なる散らばったノイズなのかを区別する。
        public double StraightnessRatio { get; set; } = 0.1;
        public double AngleToleranceDeg { get; set; } = 8.0;
        public int CorridorWidthPixels { get; set; } = 14;

        // 星の瞬き等の点状ノイズはCannyエッジとしては小さく丸い輪郭になる。
        // 濃度判定の前にこの最小サイズに満たない輪郭を除外し、画面内の星の数
        // (=ノイズ量)に判定が左右されないようにする。
        public int MinBlobExtentPixels { get; set; } = 8;

        // 建物の屋根の輪郭など、映像に常に写り込んでいる静止構造物は、圧縮ノイズや
        // 微振動によって「常にわずかに一定の直線」として毎フレームペアに現れてしまい、
        // IsStraightTrackの判定だけでは(実際に1本の直線であるがゆえに)誤検出を防げない。
        // 流星のような一時的に現れて消える物体は、露出内のごく一部のフレームペアにしか
        // 信号が現れないはずなので、その時間的な局在性で区別する。
        public byte TransientSignalThreshold { get; set; } = 25;
        public double MaxPersistenceRatio { get; set; } = 0.7;

        // 飛行機や人工衛星は、流星に比べて見かけの移動速度(角速度)が遅い傾向があるが、
        // 広角レンズで撮影した実データで比較したところ、遅い流星(25px/s程度)と
        // 飛行機(18〜28px/s程度)の速度域は重なってしまい、単独の閾値では区別できない
        // ことが分かった。そのため、これは極端に遅い物体だけを弾く安全網として使う。
        // (飛行機・衛星の誤検知は検出一覧から手動で削除する運用とする。)
        public double MinAngularSpeedPxPerSec { get; set; } = 15.0;

        // 映像周囲の除外幅(px)。安価なキャプチャデバイスでは映像の端に
        // 固定的な黒帯やノイズが乗ることがあり、誤検出の原因になるため
        // マスクの有無に関わらず常時これを除外する。
        public int BorderExclusionPixels { get; set; } = 5;

        // 除外領域(外周+ユーザーマスク)は、diffの時点で強制的に一定値にしているため、
        // その境界そのものが常に一定の直線として現れやすい(実映像のノイズが強いほど
        // 顕著になる)。Canny後のエッジ判定では、除外領域をこの分だけ外側に広げてから
        // 取り除くことで、境界線自体が誤検出されるのを防ぐ。
        public int EdgeExclusionMarginPixels { get; set; } = 8;

        private Mat? _mask;

        public bool HasMask => _mask != null;

        public void LoadMask(string path)
        {
            // 検出そのものを輝度(グレースケール)ベースで行うため、マスクも単一チャンネルで統一する。
            var loaded = Cv2.ImRead(path, ImreadModes.Grayscale);
            if (loaded.Empty())
            {
                loaded.Dispose();
                throw new InvalidOperationException($"マスク画像を読み込めません: {path}");
            }

            // マスク画像の輪郭がアンチエイリアス(半透明のグラデーション)になっていると、
            // bitwise_orで2フレームに適用した際、その中間値がフレームごとの実映像と
            // 混ざって毎回微妙に異なる結果になり、境界そのものが常時一定の直線状
            // ノイズとしてdiff画像に現れて誤検出の原因になる。
            // 二値化してマスクの内外を完全に固定することで、この境界ノイズを消す。
            Cv2.Threshold(loaded, loaded, 127, 255, ThresholdTypes.Binary);

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
        // カラーカメラ(実運用のSonyカメラ)とモノクロカメラ(検証用のATOM Cam2サンプル)とで
        // 検出精度が変わらないよう、色情報(色ノイズ・彩度のばらつき)を使わず、
        // 輝度成分のみで比較する。モノクロ映像はR=G=Bなので結果は変わらない。
        //
        // Python版は zip(img_list[:-2], img_list[1:]) を使っており、
        // リスト末尾の1枚は比較に含めない(N枚ならN-2組の差分ペア)。
        // ペア数が増えるほど比較明合成(max)でノイズのピークを拾いやすくなり
        // 差分画像が明るくなるため、Python版と同じ範囲に合わせる。
        public List<Mat> Diff(IReadOnlyList<Mat> images)
        {
            var result = new List<Mat>();

            using var effectiveMask = BuildEffectiveMask(images[0].Size());

            var gray = new Mat[images.Count];
            for (int i = 0; i < images.Count; i++)
            {
                gray[i] = new Mat();
                Cv2.CvtColor(images[i], gray[i], ColorConversionCodes.BGR2GRAY);
            }

            try
            {
                for (int i = 0; i < images.Count - 2; i++)
                {
                    using var m1 = new Mat();
                    using var m2 = new Mat();
                    Cv2.BitwiseOr(gray[i], effectiveMask, m1);
                    Cv2.BitwiseOr(gray[i + 1], effectiveMask, m2);

                    var d = new Mat();
                    Cv2.Subtract(m1, m2, d);
                    result.Add(d);
                }
            }
            finally
            {
                foreach (var g in gray) g.Dispose();
            }

            return result;
        }

        // 外周除外マスクを作成し、ユーザー指定マスクがあれば合成する(単一チャンネル)。
        private Mat BuildEffectiveMask(Size size)
        {
            var border = new Mat(size, MatType.CV_8UC1, Scalar.All(0));
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
        // 輝度ストレッチ(淡い部分の底上げ) -> GaussianBlur -> Canny -> HoughLinesP。
        public LineSegmentPoint[]? Detect(Mat img)
        {
            using var normalized = new Mat();
            if (NormalizeBeforeDetect)
            {
                Cv2.MinMaxLoc(img, out double minVal, out double maxVal);
                if (maxVal > minVal)
                {
                    double alpha = 255.0 / (maxVal - minVal);
                    double beta = -minVal * alpha;
                    img.ConvertTo(normalized, MatType.CV_8UC1, alpha, beta);
                }
                else
                {
                    img.CopyTo(normalized);
                }
            }
            else
            {
                img.CopyTo(normalized);
            }

            using var blur = new Mat();
            Cv2.GaussianBlur(normalized, blur, new Size(5, 5), 0);

            using var canny = new Mat();
            Cv2.Canny(blur, canny, CannyThreshold1, CannyThreshold2, 3);

            // 除外領域の境界線そのものが誤検出されるのを防ぐため、境界を少し広げてから
            // Cannyエッジ側で明示的に取り除く(diff計算時のマスクだけでは境界に沿った
            // 人工的なエッジが残ってしまうことがある)。
            using var exclusionMask = BuildEffectiveMask(canny.Size());
            using var dilatedExclusion = new Mat();
            if (EdgeExclusionMarginPixels > 0)
            {
                using var kernel = Cv2.GetStructuringElement(
                    MorphShapes.Rect,
                    new Size(EdgeExclusionMarginPixels * 2 + 1, EdgeExclusionMarginPixels * 2 + 1));
                Cv2.Dilate(exclusionMask, dilatedExclusion, kernel);
            }
            else
            {
                exclusionMask.CopyTo(dilatedExclusion);
            }
            using var allowedArea = new Mat();
            Cv2.BitwiseNot(dilatedExclusion, allowedArea);
            Cv2.BitwiseAnd(canny, allowedArea, canny);

            var lines = Cv2.HoughLinesP(canny, 1, Math.PI / 180, HoughThreshold, MinLineLength, MaxLineGap);
            if (lines.Length == 0) return null;

            return IsStraightTrack(canny, lines) ? lines : null;
        }

        // 検出された線分が、露出内のごく一部のフレームペアにしか現れていない
        // 「一時的な物体」(流星)かどうかを判定する。
        // 建物の輪郭等の静止構造物は圧縮ノイズ等でほぼ全フレームペアに信号が
        // 現れ続けるため、信号が現れたフレームペアの割合(persistenceRatio)が
        // 高すぎる場合は静止構造物とみなして棄却する。
        public bool IsTransient(IReadOnlyList<Mat> diffs, LineSegmentPoint[] lines)
            => ComputePersistenceRatio(diffs, lines) <= MaxPersistenceRatio;

        // 検出線分のcorridor内に信号が現れているフレームペアの割合(0.0〜1.0)を返す。
        // 診断・閾値調整用にpublicで公開している。
        public double ComputePersistenceRatio(IReadOnlyList<Mat> diffs, LineSegmentPoint[] lines)
        {
            if (diffs.Count == 0) return 1.0;

            using var corridor = new Mat(diffs[0].Size(), MatType.CV_8UC1, Scalar.All(0));
            foreach (var line in lines)
                Cv2.Line(corridor, line.P1, line.P2, Scalar.All(255), CorridorWidthPixels);

            int framesWithSignal = 0;
            foreach (var d in diffs)
            {
                Cv2.MinMaxLoc(d, out _, out double maxVal, out _, out _, corridor);
                if (maxVal >= TransientSignalThreshold) framesWithSignal++;
            }

            return (double)framesWithSignal / diffs.Count;
        }

        // 飛行機や人工衛星のような低速移動物体を、見かけの角速度(px/秒)で棄却する。
        // 流星は非常に高速なため、検出線の長さに対して信号が持続する時間(フレーム数)が短い。
        public bool IsFastEnough(IReadOnlyList<Mat> diffs, LineSegmentPoint[] lines, double fps)
            => ComputeAngularSpeed(diffs, lines, fps) >= MinAngularSpeedPxPerSec;

        // 検出線の見かけの移動速度(px/秒)を推定する。診断・閾値調整用にpublicで公開している。
        // corridor内に信号が現れ続けた期間(最初〜最後に信号があったフレームペアの範囲)を
        // 移動に要した時間とみなし、検出線の長さをその時間で割る。
        public double ComputeAngularSpeed(IReadOnlyList<Mat> diffs, LineSegmentPoint[] lines, double fps)
        {
            if (diffs.Count == 0 || fps <= 0) return 0;

            using var corridor = new Mat(diffs[0].Size(), MatType.CV_8UC1, Scalar.All(0));
            foreach (var line in lines)
                Cv2.Line(corridor, line.P1, line.P2, Scalar.All(255), CorridorWidthPixels);

            int firstIndex = -1, lastIndex = -1;
            for (int i = 0; i < diffs.Count; i++)
            {
                Cv2.MinMaxLoc(diffs[i], out _, out double maxVal, out _, out _, corridor);
                if (maxVal >= TransientSignalThreshold)
                {
                    if (firstIndex < 0) firstIndex = i;
                    lastIndex = i;
                }
            }

            if (firstIndex < 0) return 0;

            double durationSeconds = Math.Max(1.0 / fps, (lastIndex - firstIndex + 1) / fps);
            double primaryLength = lines.Max(LineLength);
            return primaryLength / durationSeconds;
        }

        // 最長の線分(主軸)を基準に、同一直線上とみなせる線分だけを集めた帯領域を作り、
        // 画像全体のエッジ画素のうちどれだけがその帯に収まるかで「1直線の軌跡らしさ」を判定する。
        // 流星はエッジが主軸に沿って集中するが、方向のバラバラなノイズはこの比率が低くなる。
        private bool IsStraightTrack(Mat canny, LineSegmentPoint[] lines)
        {
            var primary = lines.OrderByDescending(LineLength).First();

            using var corridor = new Mat(canny.Size(), MatType.CV_8UC1, Scalar.All(0));
            double primaryAngle = LineAngleDeg(primary);

            foreach (var line in lines)
            {
                double angleDiff = Math.Abs(LineAngleDeg(line) - primaryAngle);
                angleDiff = Math.Min(angleDiff, 180.0 - angleDiff);
                if (angleDiff > AngleToleranceDeg) continue;

                if (PerpendicularDistance(primary, line.P1) > CorridorWidthPixels ||
                    PerpendicularDistance(primary, line.P2) > CorridorWidthPixels)
                    continue;

                Cv2.Line(corridor, line.P1, line.P2, Scalar.All(255), CorridorWidthPixels);
            }

            using var significantEdges = FilterSmallBlobs(canny, MinBlobExtentPixels);

            int totalEdgePixels = Cv2.CountNonZero(significantEdges);
            if (totalEdgePixels == 0) return false;

            using var within = new Mat();
            Cv2.BitwiseAnd(significantEdges, corridor, within);
            int corridorEdgePixels = Cv2.CountNonZero(within);

            return (double)corridorEdgePixels / totalEdgePixels >= StraightnessRatio;
        }

        // 星の瞬き等による小さく丸い輪郭(点状ノイズ)を取り除き、
        // 流星の軌跡のように細長い輪郭だけを残したエッジマスクを作る。
        private static Mat FilterSmallBlobs(Mat canny, int minExtentPixels)
        {
            using var cannyClone = canny.Clone();
            Cv2.FindContours(cannyClone, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);

            var filtered = new Mat(canny.Size(), MatType.CV_8UC1, Scalar.All(0));
            foreach (var c in contours)
            {
                var rect = Cv2.BoundingRect(c);
                if (Math.Max(rect.Width, rect.Height) >= minExtentPixels)
                    Cv2.DrawContours(filtered, new[] { c }, -1, Scalar.All(255), -1);
            }
            return filtered;
        }

        private static double LineLength(LineSegmentPoint l)
        {
            double dx = l.P2.X - l.P1.X;
            double dy = l.P2.Y - l.P1.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // 直線の向き(0-180度)を返す。180度反転しても同じ直線とみなす。
        private static double LineAngleDeg(LineSegmentPoint l)
        {
            double dx = l.P2.X - l.P1.X;
            double dy = l.P2.Y - l.P1.Y;
            double a = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            a %= 180.0;
            if (a < 0) a += 180.0;
            return a;
        }

        // 点(pt)から、line(無限直線として延長したもの)までの垂直距離。
        private static double PerpendicularDistance(LineSegmentPoint line, Point pt)
        {
            double x1 = line.P1.X, y1 = line.P1.Y;
            double x2 = line.P2.X, y2 = line.P2.Y;
            double dx = x2 - x1, dy = y2 - y1;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-6) return double.MaxValue;
            return Math.Abs(dy * pt.X - dx * pt.Y + x2 * y1 - y2 * x1) / Math.Sqrt(lenSq);
        }

        public void Dispose()
        {
            _mask?.Dispose();
            _mask = null;
            GC.SuppressFinalize(this);
        }
    }
}
