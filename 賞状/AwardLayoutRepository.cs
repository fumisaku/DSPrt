using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DSPrt.賞状
{
    public sealed class AwardLayoutRepository
    {
        private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly string _layoutDirectory;

        public AwardLayoutRepository(string? layoutDirectory = null)
        {
            _layoutDirectory = Path.GetFullPath(layoutDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Layouts", "Award"));
        }

        public string LayoutDirectory => _layoutDirectory;

        public AwardLayout Load(string path)
        {
            string json = File.ReadAllText(path);
            var layout = JsonSerializer.Deserialize<AwardLayout>(json, ReadOptions);
            return layout ?? new AwardLayout();
        }

        public AwardLayout LoadDefault()
        {
            string? path = AppSettings.Instance.PrintSettings.AwardLayoutPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                string resolved = ResolvePath(path);
                if (File.Exists(resolved))
                    return Load(resolved);
            }

            return new AwardLayout();
        }

        public string Save(AwardLayout layout, string? path = null)
        {
            Directory.CreateDirectory(_layoutDirectory);
            string targetPath = string.IsNullOrWhiteSpace(path)
                ? Path.Combine(_layoutDirectory, SanitizeFileName(layout.DisplayFileName) + ".json")
                : ResolvePath(path);

            string json = JsonSerializer.Serialize(layout, WriteOptions);
            File.WriteAllText(targetPath, json);
            return targetPath;
        }

        public string ResolvePath(string path)
        {
            if (Path.IsPathRooted(path)) return path;
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path));
        }

        public string GetRelativePath(string path)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.GetRelativePath(baseDir, path);
        }

        private static string SanitizeFileName(string fileName)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(fileName) ? "layout" : fileName;
        }
    }
}
