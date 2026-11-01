using System;

namespace HDMICapture.Core
{
    // 1件の流星検出記録。リストファイル(JSON)にシリアライズされる。
    public class MeteorDetectionRecord
    {
        // その検出のもとになった露出(バッファリング)を開始した時刻
        public DateTime ExposureStartTime { get; set; }

        // 検出処理が完了した時刻
        public DateTime DetectedTime { get; set; }

        public string PhotoPath { get; set; } = string.Empty;
        public string DiffPath { get; set; } = string.Empty;
        public string MoviePath { get; set; } = string.Empty;

        // 検出時の露出時間(秒)。飛行機の疑い(連続した露出での検出)の判定に使う。
        // 旧バージョンの記録には無い(0)ため、利用側で既定値へフォールバックする。
        public double ExposureSeconds { get; set; }

        // 操作者が目視で流星と確認済みのもの。確認済みは誤検知としての削除を受け付けない。
        public bool IsConfirmed { get; set; }
    }
}
