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
    }
}
