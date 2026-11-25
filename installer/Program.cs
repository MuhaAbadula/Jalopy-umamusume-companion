using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UmamusumeCompanion.Installer
{
    internal static class Program
    {
        static readonly bool Russian = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
        static readonly string Title = Text("Umamusume companion for Jalopy", "Umamusume companion для Jalopy");

        static string Text(string english, string russian) { return Russian ? russian : english; }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            using (var single = new System.Threading.Mutex(false, "Local\\UmamusumeCompanionInstaller"))
            {
                try
                {
                    if (!single.WaitOne(0)) throw new IOException(Text("The installer is already open.", "Установщик уже открыт."));
                    string game = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Jalopy", "JalopyPath", null) as string;
                    if (game == null || !File.Exists(Path.Combine(game, "Jalopy.exe")))
                    {
                        using (var pick = new OpenFileDialog { Title = Text("Select Jalopy.exe", "Выберите Jalopy.exe"), Filter = "Jalopy|Jalopy.exe" })
                        {
                            if (pick.ShowDialog() != DialogResult.OK) return;
                            game = Path.GetDirectoryName(pick.FileName);
                        }
                    }
                    string mods = ModsFolder(game);
                    string confirmation = Text("Install the mod for this copy of Jalopy?\n\n{0}\n\nMods folder\n{1}",
                        "Установить мод в Jalopy?\n\n{0}\n\nПапка модов:\n{1}");
                    if (MessageBox.Show(string.Format(confirmation, game, mods),
                        Title, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                    if (Process.GetProcessesByName("Jalopy").Length > 0)
                        throw new IOException(Text("Close Jalopy and run the installer again.",
                            "Закройте Jalopy и снова запустите установщик."));
                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("package.zip"))
                    using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                        Install(archive, game, mods);
                    MessageBox.Show(Text("The mod is installed. Start Jalopy and hold V to open the command wheel.",
                        "Готово! Запустите Jalopy. Для команд спутнице удерживайте V."), Title);
                }
                catch (Exception e) { MessageBox.Show(e.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        internal static string ModsFolder(string game)
        {
            if (!File.Exists(Path.Combine(game, @"Jalopy_Data\Managed\JaLoader.dll")))
                throw new IOException(Text("Install JaLoader for this copy of Jalopy first.",
                    "Сначала установите JaLoader для этой копии Jalopy."));
            string config = Path.Combine(game, "JaSettings.json");
            string path = null;
            if (File.Exists(config))
            {
                var settings = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(config));
                object value;
                if (settings != null && settings.TryGetValue("ModsLocation", out value)) path = value as string;
            }
            else path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Jalopy", "ModsLocation", null) as string;
            if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !Directory.Exists(path))
                throw new IOException(Text("Check the mods folder in your JaLoader settings and run Jalopy once.",
                    "Проверьте папку модов в настройках JaLoader и один раз запустите игру."));
            return Path.GetFullPath(path);
        }

        internal static void Install(ZipArchive archive, string game, string mods)
        {
            var changes = new List<KeyValuePair<string, string>>();
            string id = Guid.NewGuid().ToString("N");
            try
            {
                foreach (var entry in archive.Entries)
                {
                    const string prefix = "Jalopy_Data/Mods/";
                    if (entry.Name.Length == 0) continue;
                    bool mod = entry.FullName.StartsWith(prefix, StringComparison.Ordinal);
                    if (!mod && !entry.FullName.StartsWith("Paintjobs/", StringComparison.Ordinal)) continue;
                    string root = mod ? mods : game;
                    string name = mod ? entry.FullName.Substring(prefix.Length) : entry.FullName;
                    string target = Path.GetFullPath(Path.Combine(root, name));
                    if (!target.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(Text("The archive contains an invalid file path.",
                            "Некорректный путь в архиве."));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    string stage = target + "." + id + ".tmp";
                    string backup = null;
                    try
                    {
                        using (Stream input = entry.Open())
                        using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write))
                            input.CopyTo(output);
                        if (File.Exists(target))
                        {
                            if (SameFile(stage, target)) continue;
                            backup = Path.Combine(root, "UmamusumeCompanion-backups", id, name);
                            Directory.CreateDirectory(Path.GetDirectoryName(backup));
                            File.Replace(stage, target, backup);
                        }
                        else File.Move(stage, target);
                        changes.Add(new KeyValuePair<string, string>(target, backup));
                    }
                    finally { if (File.Exists(stage)) File.Delete(stage); }
                }
            }
            catch
            {
                for (int i = changes.Count - 1; i >= 0; i--)
                {
                    var item = changes[i];
                    if (item.Value == null) File.Delete(item.Key);
                    else File.Replace(item.Value, item.Key, null);
                }
                throw;
            }
        }

        static bool SameFile(string a, string b)
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            using (var hash = System.Security.Cryptography.SHA256.Create())
            using (Stream left = File.OpenRead(a))
            using (Stream right = File.OpenRead(b))
                return Convert.ToBase64String(hash.ComputeHash(left)) == Convert.ToBase64String(hash.ComputeHash(right));
        }
    }
}
