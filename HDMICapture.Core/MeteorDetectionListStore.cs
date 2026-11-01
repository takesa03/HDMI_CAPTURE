using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HDMICapture.Core
{
    // 出力フォルダ単位で検出リスト(detections.json)を読み書きするヘルパー。
    // 複数セッションにまたがって同じ出力フォルダを使う場合は追記されていく。
    public static class MeteorDetectionListStore
    {
        private const string ListFileName = "detections.json";
        private static readonly object FileLock = new();

        public static string GetListFilePath(string outputDirectory) =>
            Path.Combine(outputDirectory, ListFileName);

        public static List<MeteorDetectionRecord> Load(string outputDirectory)
        {
            string path = GetListFilePath(outputDirectory);

            lock (FileLock)
            {
                try
                {
                    if (!File.Exists(path)) return new List<MeteorDetectionRecord>();

                    string json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<List<MeteorDetectionRecord>>(json)
                        ?? new List<MeteorDetectionRecord>();
                }
                catch
                {
                    return new List<MeteorDetectionRecord>();
                }
            }
        }

        public static void Append(string outputDirectory, MeteorDetectionRecord record)
        {
            lock (FileLock)
            {
                var list = LoadUnlocked(outputDirectory);
                list.Add(record);
                SaveUnlocked(outputDirectory, list);
            }
        }

        private static bool IsSameRecord(MeteorDetectionRecord a, MeteorDetectionRecord b) =>
            a.DetectedTime == b.DetectedTime && a.PhotoPath == b.PhotoPath;

        // 誤検知としてマークする: 画像/動画ファイルを _del フォルダへ移動し、リストから削除する。
        public static void MarkAsFalsePositive(string outputDirectory, MeteorDetectionRecord record) =>
            MarkAsFalsePositive(outputDirectory, new[] { record });

        // 複数件をまとめて誤検知として削除する(リストの読み書きは1回)。
        // 確認済み(IsConfirmed)のものは、呼び出し側の状態ではなく保存されている状態を見て
        // 削除せず残す。実際に削除した件数を返す。
        public static int MarkAsFalsePositive(string outputDirectory, IEnumerable<MeteorDetectionRecord> records)
        {
            string delDir = Path.Combine(outputDirectory, "_del");
            int removed = 0;

            lock (FileLock)
            {
                var list = LoadUnlocked(outputDirectory);
                try
                {
                    foreach (var record in records)
                    {
                        var stored = list.FirstOrDefault(r => IsSameRecord(r, record));
                        if (stored == null || stored.IsConfirmed) continue;

                        Directory.CreateDirectory(delDir);
                        MoveIfExists(stored.PhotoPath, delDir);
                        MoveIfExists(stored.DiffPath, delDir);
                        MoveIfExists(stored.MoviePath, delDir);

                        list.Remove(stored);
                        removed++;
                    }
                }
                finally
                {
                    // 途中で失敗しても、ここまでに移動したファイル分はリストへ反映する。
                    if (removed > 0) SaveUnlocked(outputDirectory, list);
                }
            }

            return removed;
        }

        // 確認済みフラグを保存する。
        public static void SetConfirmed(string outputDirectory, IEnumerable<MeteorDetectionRecord> records, bool confirmed)
        {
            lock (FileLock)
            {
                var list = LoadUnlocked(outputDirectory);
                bool changed = false;
                foreach (var record in records)
                {
                    var stored = list.FirstOrDefault(r => IsSameRecord(r, record));
                    if (stored != null && stored.IsConfirmed != confirmed)
                    {
                        stored.IsConfirmed = confirmed;
                        changed = true;
                    }
                }
                if (changed) SaveUnlocked(outputDirectory, list);
            }
        }

        private static void MoveIfExists(string filePath, string destDir)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

            string dest = Path.Combine(destDir, Path.GetFileName(filePath));
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(filePath, dest);
        }

        private static List<MeteorDetectionRecord> LoadUnlocked(string outputDirectory)
        {
            string path = GetListFilePath(outputDirectory);
            try
            {
                if (!File.Exists(path)) return new List<MeteorDetectionRecord>();

                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<List<MeteorDetectionRecord>>(json)
                    ?? new List<MeteorDetectionRecord>();
            }
            catch
            {
                return new List<MeteorDetectionRecord>();
            }
        }

        private static void SaveUnlocked(string outputDirectory, List<MeteorDetectionRecord> records)
        {
            Directory.CreateDirectory(outputDirectory);
            string path = GetListFilePath(outputDirectory);
            string json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }
}
