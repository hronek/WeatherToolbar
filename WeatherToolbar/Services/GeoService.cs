
using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Net;

namespace WeatherToolbar.Services
{
    public class GeoLocation
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string City { get; set; }
        public string Country { get; set; }
    }

    public class GeoService
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        static GeoService()
        {
            try
            {
                _http.DefaultRequestHeaders.UserAgent.ParseAdd("WeatherToolbar/1.0 (+https://open-meteo.com)");
            }
            catch { }
        }

        private static string CachePath
            => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeatherToolbar", "location.json");

        public async Task<GeoLocation> GetLocationAsync()
        {
            // Try cache first (valid 24h)
            try
            {
                if (System.IO.File.Exists(CachePath))
                {
                    var info = new System.IO.FileInfo(CachePath);
                    if (DateTime.Now - info.LastWriteTime < TimeSpan.FromHours(24))
                    {
                        var cached = await System.IO.File.ReadAllTextAsync(CachePath);
                        var loc = JsonSerializer.Deserialize<GeoLocation>(cached);
                        if (loc != null && loc.Latitude != 0 && loc.Longitude != 0) return loc;
                    }
                }
            }
            catch { }

            // Providers in order
            var providers = new Func<Task<GeoLocation>>[]
            {
                () => QueryIpApiCo(),
                () => QueryIpwhoIs(),
                () => QueryIpinfoIo()
            };

            foreach (var p in providers)
            {
                try
                {
                    var loc = await p();
                    if (loc != null)
                    {
                        try
                        {
                            var dir = System.IO.Path.GetDirectoryName(CachePath);
                            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                            await System.IO.File.WriteAllTextAsync(CachePath, JsonSerializer.Serialize(loc));
                        }
                        catch { }
                        return loc;
                    }
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    // try next provider
                }
                catch
                {
                    // ignore and try next
                }
            }

            return null;
        }

        private async Task<GeoLocation> QueryIpApiCo()
        {
            var url = "https://ipapi.co/json/";
            var json = await _http.GetStringAsync(url); // throws on non-success
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("latitude", out var latEl) || !latEl.TryGetDouble(out var lat)) return null;
            if (!root.TryGetProperty("longitude", out var lonEl) || !lonEl.TryGetDouble(out var lon)) return null;
            string city = root.TryGetProperty("city", out var cityEl) ? cityEl.GetString() : null;
            string country = root.TryGetProperty("country_name", out var cEl) ? cEl.GetString() : null;
            return new GeoLocation { Latitude = lat, Longitude = lon, City = city, Country = country };
        }

        private async Task<GeoLocation> QueryIpwhoIs()
        {
            var url = "https://ipwho.is/";
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("success", out var succ) && succ.ValueKind == JsonValueKind.False) return null;
            if (!root.TryGetProperty("latitude", out var latEl) || !latEl.TryGetDouble(out var lat)) return null;
            if (!root.TryGetProperty("longitude", out var lonEl) || !lonEl.TryGetDouble(out var lon)) return null;
            string city = root.TryGetProperty("city", out var cityEl) ? cityEl.GetString() : null;
            string country = root.TryGetProperty("country", out var cEl) ? cEl.GetString() : null;
            return new GeoLocation { Latitude = lat, Longitude = lon, City = city, Country = country };
        }

        private async Task<GeoLocation> QueryIpinfoIo()
        {
            var url = "https://ipinfo.io/json";
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            // loc: "lat,lon"
            if (!root.TryGetProperty("loc", out var locEl)) return null;
            var locStr = locEl.GetString();
            if (string.IsNullOrWhiteSpace(locStr)) return null;
            var parts = locStr.Split(',');
            if (parts.Length != 2) return null;
            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat)) return null;
            if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon)) return null;
            string city = root.TryGetProperty("city", out var cityEl) ? cityEl.GetString() : null;
            string country = root.TryGetProperty("country", out var cEl) ? cEl.GetString() : null;
            return new GeoLocation { Latitude = lat, Longitude = lon, City = city, Country = country };
        }
    }
}
