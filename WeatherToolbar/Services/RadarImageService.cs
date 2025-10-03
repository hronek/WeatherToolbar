using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Drawing.Imaging;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WeatherToolbar.Services
{
    // Builds a static radar image using RainViewer public tiles
    public class RadarImageService
    {
        private readonly HttpClient _http;
        private readonly object _cacheLock = new();
        private DateTime _lastFetch = DateTime.MinValue;
        private long _lastTimestamp = 0;
        private Image _lastImage = null;
        private double _lastLat, _lastLon;
        private int _lastW, _lastH, _lastZ;
        private Bitmap _lastBaseMap;

        public RadarImageService(HttpClient http)
        {
            _http = http;
        }

        public async Task<Image> GetStaticAsync(double lat, double lon, int width, int height, int zoom = 6, int cacheMinutes = 3, float overlayAlpha = 0.7f, CancellationToken ct = default)
        {
            // Return cached if still valid and parameters match
            lock (_cacheLock)
            {
                if (_lastImage != null && (DateTime.UtcNow - _lastFetch).TotalMinutes < cacheMinutes
                    && Math.Abs(_lastLat - lat) < 1e-6 && Math.Abs(_lastLon - lon) < 1e-6
                    && _lastW == width && _lastH == height && _lastZ == zoom)
                {
                    return (Image)_lastImage.Clone();
                }
            }

            // Get latest radar timestamps
            var mapsJson = await _http.GetStringAsync("https://api.rainviewer.com/public/maps.json", ct);
            using var doc = JsonDocument.Parse(mapsJson);
            long timestamp = 0;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var arr = doc.RootElement;
                if (arr.GetArrayLength() == 0) return null;
                var last = arr[arr.GetArrayLength() - 1];
                if (last.ValueKind == JsonValueKind.Number && last.TryGetInt64(out var ts1)) timestamp = ts1;
            }
            else if (doc.RootElement.TryGetProperty("radar", out var radarArr) && radarArr.ValueKind == JsonValueKind.Array && radarArr.GetArrayLength() > 0)
            {
                var lastEntry = radarArr[radarArr.GetArrayLength() - 1];
                if (lastEntry.TryGetProperty("time", out var timeEl) && timeEl.TryGetInt64(out var ts2)) timestamp = ts2;
            }
            if (timestamp == 0) return null;
            Interlocked.Exchange(ref _lastTimestamp, timestamp);

            // Build composite from tiles
            var bmp = new Bitmap(width, height);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Black);

            // Calculate center tile from lat/lon
            (double cx, double cy) = LatLonToTile(lat, lon, zoom);
            // Determine how many tiles we need to cover width/height
            const int tile = 256;
            double pxCenterX = cx * tile;
            double pxCenterY = cy * tile;

            int halfW = width / 2;
            int halfH = height / 2;

            // Determine pixel bounds in tile space
            double leftPx = pxCenterX - halfW;
            double topPx = pxCenterY - halfH;

            int startTileX = (int)Math.Floor(leftPx / tile);
            int startTileY = (int)Math.Floor(topPx / tile);
            int endTileX = (int)Math.Ceiling((pxCenterX + halfW) / tile);
            int endTileY = (int)Math.Ceiling((pxCenterY + halfH) / tile);

            // Draw OSM base map first
            for (int ty = startTileY; ty <= endTileY; ty++)
            {
                for (int tx = startTileX; tx <= endTileX; tx++)
                {
                    string urlBg = $"https://tile.openstreetmap.org/{zoom}/{tx}/{ty}.png";
                    try
                    {
                        using var stream = await _http.GetStreamAsync(urlBg, ct);
                        using var tileImg = Image.FromStream(stream);
                        int destX = (int)Math.Round(tx * tile - leftPx);
                        int destY = (int)Math.Round(ty * tile - topPx);
                        g.DrawImage(tileImg, new Rectangle(destX, destY, tile, tile));
                    }
                    catch { }
                }
            }

            // Prepare radar overlay alpha
            using var attribs = new ImageAttributes();
            var matrix = new ColorMatrix
            {
                Matrix00 = 1, Matrix11 = 1, Matrix22 = 1, Matrix33 = overlayAlpha, Matrix44 = 1
            };
            attribs.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);

            // Draw radar overlay tiles (semi-transparent)
            for (int ty = startTileY; ty <= endTileY; ty++)
            {
                for (int tx = startTileX; tx <= endTileX; tx++)
                {
                    // Tile URL with color scheme 2 and smooth=1_1
                    string url = $"https://tilecache.rainviewer.com/v2/radar/{timestamp}/256/{zoom}/{tx}/{ty}/2/1_1.png";
                    try
                    {
                        using var stream = await _http.GetStreamAsync(url, ct);
                        using var tileImg = Image.FromStream(stream);
                        // Destination position in output bitmap
                        int destX = (int)Math.Round(tx * tile - leftPx);
                        int destY = (int)Math.Round(ty * tile - topPx);
                        var destRect = new Rectangle(destX, destY, tile, tile);
                        g.DrawImage(tileImg, destRect, 0, 0, tile, tile, GraphicsUnit.Pixel, attribs);
                    }
                    catch
                    {
                        // ignore missing tiles
                    }
                }
            }

            lock (_cacheLock)
            {
                _lastImage?.Dispose();
                _lastImage = (Image)bmp.Clone();
                _lastFetch = DateTime.UtcNow;
                _lastLat = lat; _lastLon = lon; _lastW = width; _lastH = height; _lastZ = zoom;
            }
            return bmp;
        }

        private static (double tx, double ty) LatLonToTile(double lat, double lon, int zoom)
        {
            double latRad = lat * Math.PI / 180.0;
            double n = Math.Pow(2.0, zoom);
            double tx = (lon + 180.0) / 360.0 * n;
            double ty = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n;
            return (tx, ty);
        }

        public async Task<List<RadarFrame>> GetAnimatedAsync(double lat, double lon, int width, int height, int zoom = 7, int pastMinutes = 60, int futureMinutes = 120, float overlayAlpha = 0.7f, CancellationToken ct = default)
        {
            // Fetch weather-maps.json to get past and nowcast frames
            var mapsJson = await _http.GetStringAsync("https://api.rainviewer.com/public/weather-maps.json", ct);
            using var doc = JsonDocument.Parse(mapsJson);
            if (!doc.RootElement.TryGetProperty("radar", out var radarObj)) return null;
            var frames = new List<long>();
            if (radarObj.TryGetProperty("past", out var pastArr) && pastArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in pastArr.EnumerateArray())
                {
                    if (item.TryGetProperty("time", out var t) && t.TryGetInt64(out var ts)) frames.Add(ts);
                }
            }
            if (radarObj.TryGetProperty("nowcast", out var nowArr) && nowArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in nowArr.EnumerateArray())
                {
                    if (item.TryGetProperty("time", out var t) && t.TryGetInt64(out var ts)) frames.Add(ts);
                }
            }
            if (frames.Count == 0) return null;

            // Limit past to last ~pastMinutes and future to ~futureMinutes based on 10-min cadence
            frames.Sort();
            var nowTs = frames[^1];
            var result = new List<RadarFrame>();

            // Precompute base map once
            var baseMap = await BuildBaseMapAsync(lat, lon, width, height, zoom, ct);
            if (baseMap == null) return null;

            foreach (var ts in frames)
            {
                var ageMin = (nowTs - ts) / 60; // timestamps are seconds
                bool isPast = ts <= nowTs;
                if (isPast && ageMin > pastMinutes) continue;
                if (!isPast && ((ts - nowTs) / 60) > futureMinutes) continue;

                var frameBmp = (Bitmap)baseMap.Clone();
                using (var g = Graphics.FromImage(frameBmp))
                {
                    await DrawRadarOverlayAsync(g, lat, lon, width, height, zoom, ts, overlayAlpha, ct);
                    // Draw bottom caption with time and past/future label
                    var dt = DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().DateTime;
                    string label = isPast ? "minulost" : "předpověď";
                    string text = $"{dt:HH:mm} · {label}";
                    int barH = 20;
                    using var barBrush = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
                    g.FillRectangle(barBrush, new Rectangle(0, height - barH, width, barH));
                    using var txtBrush = new SolidBrush(Color.White);
                    using var font = SystemFonts.MessageBoxFont;
                    var size = g.MeasureString(text, font);
                    g.DrawString(text, font, txtBrush, new PointF(6, height - barH + (barH - size.Height) / 2f));
                    // Highlight current (now) frame with bright border
                    if (ts == nowTs)
                    {
                        using var pen = new Pen(Color.Lime, 2);
                        g.DrawRectangle(pen, new Rectangle(1, 1, width - 3, height - 3));
                    }
                }
                result.Add(new RadarFrame { Image = frameBmp, Timestamp = ts, IsPast = isPast });
            }
            // Cache last base map for potential reuse
            lock (_cacheLock)
            {
                _lastBaseMap?.Dispose();
                _lastBaseMap = (Bitmap)baseMap.Clone();
            }
            baseMap.Dispose();
            return result;
        }

        private async Task<Bitmap> BuildBaseMapAsync(double lat, double lon, int width, int height, int zoom, CancellationToken ct)
        {
            const int tile = 256;
            (double cx, double cy) = LatLonToTile(lat, lon, zoom);
            double pxCenterX = cx * tile;
            double pxCenterY = cy * tile;
            int halfW = width / 2;
            int halfH = height / 2;
            double leftPx = pxCenterX - halfW;
            double topPx = pxCenterY - halfH;
            int startTileX = (int)Math.Floor(leftPx / tile);
            int startTileY = (int)Math.Floor(topPx / tile);
            int endTileX = (int)Math.Ceiling((pxCenterX + halfW) / tile);
            int endTileY = (int)Math.Ceiling((pxCenterY + halfH) / tile);

            var bmp = new Bitmap(width, height);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Black);
            for (int ty = startTileY; ty <= endTileY; ty++)
            {
                for (int tx = startTileX; tx <= endTileX; tx++)
                {
                    string urlBg = $"https://tile.openstreetmap.org/{zoom}/{tx}/{ty}.png";
                    try
                    {
                        using var stream = await _http.GetStreamAsync(urlBg, ct);
                        using var tileImg = Image.FromStream(stream);
                        int destX = (int)Math.Round(tx * tile - leftPx);
                        int destY = (int)Math.Round(ty * tile - topPx);
                        g.DrawImage(tileImg, new Rectangle(destX, destY, tile, tile));
                    }
                    catch { }
                }
            }
            return bmp;
        }

        private async Task DrawRadarOverlayAsync(Graphics g, double lat, double lon, int width, int height, int zoom, long timestamp, float overlayAlpha, CancellationToken ct)
        {
            const int tile = 256;
            (double cx, double cy) = LatLonToTile(lat, lon, zoom);
            double pxCenterX = cx * tile;
            double pxCenterY = cy * tile;
            int halfW = width / 2;
            int halfH = height / 2;
            double leftPx = pxCenterX - halfW;
            double topPx = pxCenterY - halfH;
            int startTileX = (int)Math.Floor(leftPx / tile);
            int startTileY = (int)Math.Floor(topPx / tile);
            int endTileX = (int)Math.Ceiling((pxCenterX + halfW) / tile);
            int endTileY = (int)Math.Ceiling((pxCenterY + halfH) / tile);

            using var attribs = new ImageAttributes();
            var matrix = new ColorMatrix { Matrix00 = 1, Matrix11 = 1, Matrix22 = 1, Matrix33 = overlayAlpha, Matrix44 = 1 };
            attribs.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);

            for (int ty = startTileY; ty <= endTileY; ty++)
            {
                for (int tx = startTileX; tx <= endTileX; tx++)
                {
                    string url = $"https://tilecache.rainviewer.com/v2/radar/{timestamp}/256/{zoom}/{tx}/{ty}/2/1_1.png";
                    try
                    {
                        using var stream = await _http.GetStreamAsync(url, ct);
                        using var tileImg = Image.FromStream(stream);
                        int destX = (int)Math.Round(tx * tile - leftPx);
                        int destY = (int)Math.Round(ty * tile - topPx);
                        var destRect = new Rectangle(destX, destY, tile, tile);
                        g.DrawImage(tileImg, destRect, 0, 0, tile, tile, GraphicsUnit.Pixel, attribs);
                    }
                    catch { }
                }
            }
        }
    }

    public class RadarFrame
    {
        public Image Image { get; set; }
        public long Timestamp { get; set; }
        public bool IsPast { get; set; }
    }
}
