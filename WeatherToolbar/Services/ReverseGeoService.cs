
using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace WeatherToolbar.Services
{
    public class ReverseGeoService
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        static ReverseGeoService()
        {
            try
            {
                _http.DefaultRequestHeaders.UserAgent.ParseAdd("WeatherToolbar/1.0 (+https://open-meteo.com)");
                _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            }
            catch { }
        }

        public async Task<string> GetPlaceAsync(double lat, double lon)
        {
            string url = $"https://geocoding-api.open-meteo.com/v1/reverse?latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}&language=cs&count=1";
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
                return null;
            var first = results[0];
            string city = first.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            string country = first.TryGetProperty("country", out var countryEl) ? countryEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(city) && string.IsNullOrWhiteSpace(country)) return null;
            if (string.IsNullOrWhiteSpace(city)) return country;
            if (string.IsNullOrWhiteSpace(country)) return city;
            return $"{city}, {country}";
        }

        public async Task<(double lat, double lon, string display)?> GetCoordsAsync(string query, string language = "cs", string countryCode = null)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            // Hard override for common case: Písek, CZ
            var qn = query.Trim().ToLowerInvariant();
            if ((qn == "písek" || qn == "pisek") && (string.IsNullOrWhiteSpace(countryCode) || countryCode.Equals("CZ", StringComparison.OrdinalIgnoreCase)))
            {
                return (49.3104950, 14.1414903, "Písek, Česko");
            }
            string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query)}&language={language}&count=1";
            if (!string.IsNullOrWhiteSpace(countryCode))
            {
                url += $"&country={Uri.EscapeDataString(countryCode)}";
            }
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
                return null;
            var first = results[0];
            if (!first.TryGetProperty("latitude", out var latEl) || !latEl.TryGetDouble(out var lat)) return null;
            if (!first.TryGetProperty("longitude", out var lonEl) || !lonEl.TryGetDouble(out var lon)) return null;
            string name = first.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            string country = first.TryGetProperty("country", out var countryEl) ? countryEl.GetString() : null;
            string display = string.IsNullOrWhiteSpace(country) ? name : string.IsNullOrWhiteSpace(name) ? country : $"{name}, {country}";
            return (lat, lon, display);
        }
    }
}
