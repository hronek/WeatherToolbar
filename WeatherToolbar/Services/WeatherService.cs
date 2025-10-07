
using System;
using System.Drawing;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Net;
using WeatherToolbar.Models;
using System.Linq;

namespace WeatherToolbar.Services
{
    public class WeatherService
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        static WeatherService()
        {
            try
            {
                _http.DefaultRequestHeaders.UserAgent.ParseAdd("WeatherToolbar/1.0 (+https://open-meteo.com)");
                _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            }
            catch { }
        }

        public async Task<CurrentWeather> GetCurrentAsync(double lat, double lon)
        {
            string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m,apparent_temperature&wind_speed_unit=ms&timezone=auto";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                // Bubble up with status code for caller to adapt (e.g., 429)
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode}", null, resp.StatusCode);
            }
            var json = await resp.Content.ReadAsStringAsync();
            var root = JsonSerializer.Deserialize<OpenMeteoResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return root?.current;
        }

        public async Task<DailyForecastDay[]> GetDailyAsync(double lat, double lon, int days = 6)
        {
            string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}&daily=weather_code,temperature_2m_max,temperature_2m_min&forecast_days={Math.Max(1, Math.Min(10, days))}&timezone=auto";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode}", null, resp.StatusCode);
            }
            var json = await resp.Content.ReadAsStringAsync();
            var root = JsonSerializer.Deserialize<OpenMeteoResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (root?.daily?.time == null || root.daily.weather_code == null || root.daily.temperature_2m_max == null || root.daily.temperature_2m_min == null)
                return Array.Empty<DailyForecastDay>();
            int n = new[] { root.daily.time.Length, root.daily.weather_code.Length, root.daily.temperature_2m_max.Length, root.daily.temperature_2m_min.Length }.Min();
            n = Math.Min(n, Math.Max(1, Math.Min(10, days)));
            var arr = new DailyForecastDay[n];
            for (int i = 0; i < n; i++)
            {
                arr[i] = new DailyForecastDay
                {
                    Date = root.daily.time[i],
                    WeatherCode = root.daily.weather_code[i],
                    Tmax = root.daily.temperature_2m_max[i],
                    Tmin = root.daily.temperature_2m_min[i]
                };
            }
            return arr;
        }

        public static string Describe(int code)
        {
            // Minimal mapping based on Open-Meteo weather codes
            if (code == 0) return "Jasno";
            if (code == 1) return "Skoro jasno";
            if (code == 2) return "Oblačno";
            if (code == 3) return "Zataženo";
            if (code >= 45 && code <= 48) return "Mlha";
            if (code >= 51 && code <= 57) return "Mrholení";
            if (code >= 61 && code <= 65) return "Déšť";
            if (code >= 66 && code <= 67) return "Mrznoucí déšť";
            if (code >= 71 && code <= 77) return "Sněžení";
            if (code >= 80 && code <= 82) return "Přeháňky";
            if (code >= 85 && code <= 86) return "Sněhové přeháňky";
            if (code >= 95 && code <= 99) return "Bouřky";
            return "Počasí";
        }

        public static string Glyph(int code)
        {
            if (code == 0) return "☀";         // Clear
            if (code == 1) return "🌤";        // Mainly clear
            if (code == 2) return "⛅";        // Partly cloudy
            if (code == 3) return "☁";         // Cloudy
            if (code >= 45 && code <= 48) return "🌫"; // Fog
            if (code >= 51 && code <= 67) return "☔"; // Drizzle/Freezing rain
            if (code >= 61 && code <= 65) return "🌧"; // Rain
            if (code >= 71 && code <= 77) return "❄"; // Snow
            if (code >= 80 && code <= 82) return "🌦"; // Showers
            if (code >= 85 && code <= 86) return "🌨"; // Snow showers
            if (code >= 95 && code <= 99) return "⛈"; // Thunderstorm
            return "·";
        }

        public static Color ColorFor(int code)
        {
            if (code == 0) return Color.Orange;
            if (code == 1) return Color.Goldenrod;
            if (code == 2) return Color.LightSteelBlue;
            if (code == 3) return Color.SteelBlue;
            if (code >= 45 && code <= 48) return Color.SlateGray;
            if (code >= 51 && code <= 67) return Color.DodgerBlue;
            if (code >= 61 && code <= 65) return Color.RoyalBlue;
            if (code >= 71 && code <= 77) return Color.LightSlateGray;
            if (code >= 80 && code <= 86) return Color.MediumBlue;
            if (code >= 95 && code <= 99) return Color.DarkSlateBlue;
            return Color.Gray;
        }
    }
}
