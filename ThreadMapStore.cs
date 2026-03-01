using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OverWatchELD.VtcBot.Threads
{
    /// <summary>
    /// Persistent mapping: string key -> threadChannelId
    /// Key format we use: "{guildId}:{discordUserId}"
    /// </summary>
    public sealed class ThreadMapStore
    {
        private readonly string _path;
        private Dictionary<string, ulong> _map;

        public ThreadMapStore(string path)
        {
            _path = path;
            _map = LoadInternal(path);
        }

        public bool TryGetThread(string key, out ulong threadId)
            => _map.TryGetValue((key ?? "").Trim(), out threadId);

        public void SetThread(string key, ulong threadId)
        {
            key = (key ?? "").Trim();
            if (string.IsNullOrWhiteSpace(key)) return;

            _map[key] = threadId;
            Save();
        }

        public void Remove(string key)
        {
            key = (key ?? "").Trim();
            if (string.IsNullOrWhiteSpace(key)) return;

            _map.Remove(key);
            Save();
        }

        private void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json);
            }
            catch { }
        }

        private static Dictionary<string, ulong> LoadInternal(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return new Dictionary<string, ulong>(StringComparer.Ordinal);

                var json = File.ReadAllText(path);
                var dict = JsonSerializer.Deserialize<Dictionary<string, ulong>>(json);

                return dict != null
                    ? new Dictionary<string, ulong>(dict, StringComparer.Ordinal)
                    : new Dictionary<string, ulong>(StringComparer.Ordinal);
            }
            catch
            {
                return new Dictionary<string, ulong>(StringComparer.Ordinal);
            }
        }
    }
}
