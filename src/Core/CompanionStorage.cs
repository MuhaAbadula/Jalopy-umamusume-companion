using System;
using System.IO;
using UnityEngine;

namespace UmamusumeCompanion
{
    internal static class CompanionStorage
    {
        internal const string ModId = "UmamusumeCompanion";
        static string SaveRoot => Path.Combine(Application.persistentDataPath, "ModSaves");
        internal static string SaveDirectory => Path.Combine(SaveRoot, ModId);
        static string SelectionFile => Path.Combine(SaveDirectory, "selected-companion.txt");

        internal static string ReadSelectedPack()
        {
            try { return File.Exists(SelectionFile) ? File.ReadAllText(SelectionFile).Trim() : null; }
            catch (IOException e) { Debug.LogWarning("[Umamusume companion] Reading companion choice: " + e.Message); }
            catch (UnauthorizedAccessException e) { Debug.LogWarning("[Umamusume companion] Reading companion choice: " + e.Message); }
            return null;
        }

        internal static void WriteSelectedPack(string pack)
        {
            string path = SelectionFile;
            string pending = path + ".tmp";
            Directory.CreateDirectory(SaveDirectory);
            File.WriteAllText(pending, pack);
            if (File.Exists(path)) File.Replace(pending, path, null);
            else File.Move(pending, path);
        }

        // JaLoader names both the settings folder and its JSON file after the mod ID.
        // Identify a previous companion installation by its feature-specific settings.
        internal static void ImportPreviousSave()
        {
            string destination = Path.Combine(SaveDirectory, ModId + "_save.json");
            if (File.Exists(destination) || !Directory.Exists(SaveRoot)) return;
            try
            {
                string source = null;
                foreach (string dir in Directory.GetDirectories(SaveRoot))
                {
                    if (string.Equals(dir, SaveDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                    string settings = Path.Combine(dir, Path.GetFileName(dir) + "_save.json");
                    if (!File.Exists(settings)) continue;
                    string json = File.ReadAllText(settings);
                    if (!json.Contains("\"WalkPace3_Slider\"") || !json.Contains("\"Fortune_Toggle\"") ||
                        !json.Contains("\"Morning_Toggle\"")) continue;
                    if (source != null)
                    {
                        Debug.LogWarning("[Umamusume companion] Multiple previous saves found. Skipping automatic import.");
                        return;
                    }
                    source = settings;
                }
                if (source == null) return;
                Directory.CreateDirectory(SaveDirectory);
                foreach (string file in Directory.GetFiles(Path.GetDirectoryName(source), "*.txt"))
                {
                    string target = Path.Combine(SaveDirectory, Path.GetFileName(file));
                    if (!File.Exists(target)) File.Copy(file, target);
                }
                File.Copy(source, destination);
            }
            catch (IOException e) { Debug.LogWarning("[Umamusume companion] Save import: " + e.Message); }
            catch (UnauthorizedAccessException e) { Debug.LogWarning("[Umamusume companion] Save import: " + e.Message); }
        }
    }
}
