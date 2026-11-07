using System;
using System.IO;
using System.Text;

namespace Zantetsu.Core
{
    /// <summary>
    /// The Windows launch preference, shared verbatim with the small desktop launcher.
    /// It selects the API of the NEXT launch; changing it never touches a running graphics device.
    /// No Unity dependency: the launcher runs before Unity or XR is initialized.
    /// </summary>
    public static class WindowsGraphicsPreference
    {
        public const string Auto = "auto";
        public const string Direct3D11 = "d3d11";
        public const string Direct3D12 = "d3d12";
        public const string SourceEnvironment = "ZANTETSU_GRAPHICS_SOURCE";
        public const string ReasonEnvironment = "ZANTETSU_GRAPHICS_REASON";

        public static string SettingsPath
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local))
                    throw new IOException("The local application data folder is not available.");
                return Path.Combine(local, "Zantetsu", "Launch", "graphics-api.txt");
            }
        }

        public static bool IsValid(string mode)
        {
            return mode == Auto || mode == Direct3D11 || mode == Direct3D12;
        }

        public static string Load() { return Load(SettingsPath); }

        public static string Load(string path)
        {
            // Only absence means the default. An unreadable or corrupt preference must not silently
            // turn a previously chosen compatibility launch back into Direct3D 12.
            try
            {
                using (var reader = new StreamReader(path, Encoding.UTF8, true))
                {
                    var buffer = new char[65];
                    int length = reader.ReadBlock(buffer, 0, buffer.Length);
                    string value = new string(buffer, 0, length).Trim();
                    if (length == buffer.Length || !IsValid(value))
                        throw new InvalidDataException("The graphics preference is invalid. Open launch settings and save a valid choice.");
                    return value;
                }
            }
            catch (FileNotFoundException) { return Auto; }
            catch (DirectoryNotFoundException) { return Auto; }
        }

        public static void Save(string mode) { Save(SettingsPath, mode); }

        public static void Save(string path, string mode)
        {
            if (!IsValid(mode)) throw new ArgumentException("Unknown graphics preference.", "mode");
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, mode + Environment.NewLine, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
