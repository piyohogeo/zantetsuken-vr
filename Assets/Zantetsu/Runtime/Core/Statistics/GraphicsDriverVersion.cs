using System;
using System.Collections.Generic;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System.Runtime.InteropServices;
using System.Text;
#endif

namespace Zantetsu.Core.Statistics
{
    /// <summary>
    /// The version of the driver of the GPU that draws (DESIGN 21.18), read once at start-up: on Windows, from the
    /// display adapters' driver entries in the registry (the class the system keeps one entry an installed adapter
    /// in, each with the hardware id it was matched to and its driver's version), by the PCI vendor and device ids of
    /// the device the engine draws with. The version is given only where the entries of that device agree on it:
    /// no entry of that device, or entries of it with different versions, gives null -- "not obtained" -- and never
    /// another adapter's version nor the graphics API's level in its place. Elsewhere (a Quest): null.
    /// </summary>
    public static class GraphicsDriverVersion
    {
        /// <summary>One display adapter's driver entry: the hardware id its driver was matched to, and the driver's version.</summary>
        public struct Entry
        {
            public string MatchingDeviceId;   // e.g. "pci\ven_10de&dev_2204"
            public string DriverVersion;      // e.g. "32.0.15.9159"
        }

        /// <summary>
        /// The driver version of the device with these PCI ids among the entries: the one version its entries carry,
        /// or null where it has no entry, an entry without a version, or entries that disagree.
        /// </summary>
        public static string Choose(int vendorId, int deviceId, IReadOnlyList<Entry> entries)
        {
            if (entries == null || vendorId <= 0 || deviceId <= 0)
            {
                return null;
            }

            string vendor = "ven_" + vendorId.ToString("x4"), device = "dev_" + deviceId.ToString("x4");
            string found = null;
            for (int i = 0; i < entries.Count; i++)
            {
                string id = entries[i].MatchingDeviceId;
                if (string.IsNullOrEmpty(id)) continue;
                string lower = id.ToLowerInvariant();
                if (!HasToken(lower, vendor) || !HasToken(lower, device)) continue;
                string version = entries[i].DriverVersion;
                if (string.IsNullOrEmpty(version)) return null;        // that device's entry says nothing: not obtained
                if (found != null && found != version) return null;    // two entries of that device disagree: not obtained
                found = version;
            }

            return found;
        }

        // The id holds the token as a whole field ("ven_10de" is not found in "ven_10de1").
        private static bool HasToken(string id, string token)
        {
            int at = id.IndexOf(token, StringComparison.Ordinal);
            while (at >= 0)
            {
                int end = at + token.Length;
                if (end >= id.Length || !Uri.IsHexDigit(id[end])) return true;
                at = id.IndexOf(token, end, StringComparison.Ordinal);
            }

            return false;
        }

        /// <summary>How often this machine's entries were read: once an agreement, never before one.</summary>
        public static int Reads { get; private set; }

        /// <summary>The driver version for the device with these PCI ids on this machine, or null (see the class).</summary>
        public static string Read(int vendorId, int deviceId)
        {
            Reads++;
            try
            {
                return Choose(vendorId, deviceId, ReadEntries());
            }
            catch (Exception)
            {
                return null;   // whatever could not be read: not obtained
            }
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        private static readonly IntPtr HKeyLocalMachine = new IntPtr(unchecked((int)0x80000002));
        private const int KeyRead = 0x20019;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegOpenKeyExW(IntPtr key, string subKey, int options, int access, out IntPtr result);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegEnumKeyExW(IntPtr key, int index, StringBuilder name, ref int nameLength, IntPtr reserved, IntPtr cls, IntPtr clsLength, IntPtr lastWrite);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegQueryValueExW(IntPtr key, string valueName, IntPtr reserved, out int type, byte[] data, ref int dataLength);

        [DllImport("advapi32.dll")]
        private static extern int RegCloseKey(IntPtr key);

        /// <summary>The display adapters' driver entries of this machine (read-only; an entry that cannot be opened is left out).</summary>
        public static List<Entry> ReadEntries()
        {
            var entries = new List<Entry>(4);
            if (RegOpenKeyExW(HKeyLocalMachine, DisplayClass, 0, KeyRead, out IntPtr cls) != 0)
            {
                return entries;
            }

            try
            {
                var name = new StringBuilder(64);
                for (int index = 0; index < 256; index++)
                {
                    int length = name.Capacity;
                    name.Length = 0;
                    if (RegEnumKeyExW(cls, index, name, ref length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0) break;
                    if (RegOpenKeyExW(cls, name.ToString(), 0, KeyRead, out IntPtr adapter) != 0) continue;   // e.g. "Properties": not ours to open
                    try
                    {
                        string id = ReadString(adapter, "MatchingDeviceId");
                        if (id != null) entries.Add(new Entry { MatchingDeviceId = id, DriverVersion = ReadString(adapter, "DriverVersion") });
                    }
                    finally
                    {
                        RegCloseKey(adapter);
                    }
                }
            }
            finally
            {
                RegCloseKey(cls);
            }

            return entries;
        }

        private static string ReadString(IntPtr key, string valueName)
        {
            var data = new byte[512];
            int length = data.Length;
            if (RegQueryValueExW(key, valueName, IntPtr.Zero, out int type, data, ref length) != 0 || type != 1 || length < 2)
            {
                return null;   // absent, or not a string
            }

            return Encoding.Unicode.GetString(data, 0, length).TrimEnd('\0');
        }
#else
        public static List<Entry> ReadEntries() => new List<Entry>(0);
#endif
    }
}
