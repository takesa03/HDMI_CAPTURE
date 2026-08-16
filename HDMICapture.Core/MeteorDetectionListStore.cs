using System.Collections.Generic;
using System.IO;
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

        // 誤検知としてマークする: 画像/動画ファイルを _del フォルダへ移動し、リストから削除する。
        public static void MarkAsFalsePositive(string outputDirectory, MeteorDetectionRecord record)
        {
            string delDir = Path.Combine(outputDirectory, "_del");
            Directory.CreateDirectory(delDir);

            MoveIfExists(record.PhotoPath, delDir);
            MoveIfExists(record.DiffPath, delDir);
            MoveIfExists(record.MoviePath, delDir);

            lock (FileLock)
            {
                var list = LoadUnlocked(outputDirectory);
                list.RemoveAll(r =>
                    r.DetectedTime == record.DetectedTime &&
                    r.PhotoPath == record.PhotoPath);
                SaveUnlocked(outputDirectory, list);
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
