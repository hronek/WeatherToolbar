
using System;

namespace WeatherToolbar.Models
{
    public class OpenMeteoResponse
    {
        public CurrentWeather current { get; set; }
        public DailyBlock daily { get; set; }
    }

    public class CurrentWeather
    {
        public DateTime time { get; set; }
        public int interval { get; set; }
        public double temperature_2m { get; set; }
        public int weather_code { get; set; }
        public double? wind_speed_10m { get; set; }
        public double? wind_direction_10m { get; set; }
        public double? apparent_temperature { get; set; }
    }

    public class DailyBlock
    {
        public DateTime[] time { get; set; }
        public int[] weather_code { get; set; }
        public double[] temperature_2m_max { get; set; }
        public double[] temperature_2m_min { get; set; }
    }

    public class DailyForecastDay
    {
        public DateTime Date { get; set; }
        public int WeatherCode { get; set; }
        public double Tmin { get; set; }
        public double Tmax { get; set; }
    }
}
