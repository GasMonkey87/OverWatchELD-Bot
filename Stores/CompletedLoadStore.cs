using System.Text.Json;
using OverWatchELD.VtcBot.Models;

namespace OverWatchELD.VtcBot.Stores
{
    public sealed class CompletedLoadStore
    {
        private readonly string _path;
        private readonly JsonSerializerOptions _jsonWriteOpts;
        private readonly JsonSerializerOptions _jsonReadOpts;

        public CompletedLoadStore(string path, JsonSerializerOptions jsonReadOpts, JsonSerializerOptions jsonWriteOpts)
        {
            _path = path;
            _jsonReadOpts = jsonReadOpts;
            _jsonWriteOpts = jsonWriteOpts;

            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
        }

        public List<LoadDto> LoadAll()
        {
            try
            {
                if (!File.Exists(_path))
                    return new List<LoadDto>();

                var raw = File.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(raw))
                    return new List<LoadDto>();

                return JsonSerializer.Deserialize<List<LoadDto>>(raw, _jsonReadOpts) ?? new List<LoadDto>();
            }
            catch
            {
                return new List<LoadDto>();
            }
        }

        public void SaveCompleted(LoadDto dto)
        {
            var list = LoadAll();
            list.Add(dto);
            File.WriteAllText(_path, JsonSerializer.Serialize(list, _jsonWriteOpts));
        }
    }
}
