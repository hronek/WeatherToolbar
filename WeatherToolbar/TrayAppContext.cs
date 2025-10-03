using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using WeatherToolbar.Services;
using Microsoft.Web.WebView2.Core;
 
using System.Globalization;

namespace WeatherToolbar
{
    public class TrayAppContext : ApplicationContext
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly GeoService _geoService = new GeoService();
        private readonly WeatherService _weatherService = new WeatherService();
        private readonly ReverseGeoService _reverseGeo = new ReverseGeoService();
        private readonly System.Windows.Forms.Timer _timer;
        private readonly AppConfig _config;
        private Icon _currentIcon;
        private double _lat;
        private double _lon;
        private string _place;
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        // Quick forecast preview (on click)
        private Form _previewForm;
        private PictureBox _previewBox;
        private bool _suppressPreview;
        private System.Windows.Forms.Timer _meteogramTimer;
        private volatile bool _meteogramLoading;
        private volatile bool _meteogramHasFresh;
        private bool _webView2Missing;
        private bool _justClosedOverlay; // guard to suppress immediate double-click reopen
        private System.Windows.Forms.Timer _singleClickTimer;
        private bool _pendingSingleOpen;
        // Small radar overlay (RainViewer)
        private Form _radarForm;
        private Microsoft.Web.WebView2.WinForms.WebView2 _radarWeb;
        // Color legend overlay
        private Form _legendForm;
        // Icons legend overlay
        private Form _iconsLegendForm;
        

        public TrayAppContext()
        {
            _config = ConfigService.Load();
            try { _http.DefaultRequestHeaders.UserAgent.ParseAdd("WeatherToolbar/1.0"); } catch {}
            // If city suggests CZ but coords look wrong (e.g., US Pisek), fix immediately
            TryFixCzechCityCoordinates();
            // If user previously had only City without coords, resolve once on startup
            if ((!_config.Latitude.HasValue || !_config.Longitude.HasValue) && !string.IsNullOrWhiteSpace(_config.City))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var res = await _reverseGeo.GetCoordsAsync(_config.City, "cs", "CZ");
                        if (res.HasValue)
                        {
                            _config.Latitude = res.Value.lat;
                            _config.Longitude = res.Value.lon;
                            if (string.IsNullOrWhiteSpace(_config.Country))
                            {
                                // best-effort: keep country from reverse if needed
                                var place = await _reverseGeo.GetPlaceAsync(res.Value.lat, res.Value.lon);
                                _place = place;
                            }
                            ConfigService.Save(_config);
                            _lat = 0; _lon = 0; // force re-evaluation
                            await RefreshWeather(forceGeo:true);
                        }
                    }
                    catch (Exception ex) { Log("Startup forward geocode failed: " + ex.Message); }
                });
            }

            _notifyIcon = new NotifyIcon
            {
                Visible = true,
                Text = "Počasí: načítám..."
            };

            var menu = new ContextMenuStrip();
            menu.Opening += (_, __) => { _suppressPreview = true; HideForecastPreview(); };
            menu.Closed  += (_, __) => { _suppressPreview = false; };
            menu.Items.Add("Aktualizovat počasí", null, async (_, __) => {
                await RefreshWeather(forceGeo:true);
                try { _meteogramHasFresh = false; _meteogramLoading = true; var p = GetMeteogramPath(); if (System.IO.File.Exists(p)) System.IO.File.Delete(p); } catch {}
                await CaptureMeteogramIfPossibleAsync();
            });
            menu.Items.Add("Nastavit polohu…", null, (_, __) => OpenLocationDialog());
            menu.Items.Add("Nastavit písmo…", null, (_, __) => OpenFontDialog());
            // Logging toggle
            var miLogging = new ToolStripMenuItem("Zapnout logování") { CheckOnClick = true, Checked = _config.EnableLogging ?? false };
            miLogging.CheckedChanged += (_, __) => { _config.EnableLogging = miLogging.Checked; ConfigService.Save(_config); };
            menu.Items.Add(miLogging);
            // WebView2 installer (shown always for convenience)
            menu.Items.Add("Instalovat WebView2…", null, (_, __) => {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "https://developer.microsoft.com/en-us/microsoft-edge/webview2/#download-section",
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(psi);
                }
                catch (Exception ex) { Log("Open WebView2 installer failed: " + ex.Message); }
            });
            
            // Outline thickness submenu
            var outlineMenu = new ToolStripMenuItem("Velikost obrysu");
            void AddOutlineItem(int val)
            {
                var mi = new ToolStripMenuItem(val.ToString()) { Checked = (_config.OutlineRadius ?? 2) == val };
                mi.Click += (_, __) => { _config.OutlineRadius = val; ConfigService.Save(_config); _ = RefreshWeather(); };
                outlineMenu.DropDownItems.Add(mi);
            }
            AddOutlineItem(0);
            AddOutlineItem(1);
            AddOutlineItem(2);
            AddOutlineItem(3);
            menu.Items.Add(outlineMenu);
            var miGlyph = new ToolStripMenuItem("Zobrazit symbol počasí (glyph)") { Checked = _config.ShowGlyph ?? true, CheckOnClick = true };
            miGlyph.CheckedChanged += (_, __) => { _config.ShowGlyph = miGlyph.Checked; ConfigService.Save(_config); _ = RefreshWeather(); };
            menu.Items.Add(miGlyph);

            // Refresh interval submenu
            var intervalMenu = new ToolStripMenuItem("Interval obnovení");
            void ApplyRefreshMinutes(int minutes)
            {
                try
                {
                    _config.RefreshMinutes = minutes;
                    ConfigService.Save(_config);
                    var ms = Math.Max(1, minutes) * 60 * 1000;
                    _timer.Interval = ms;
                    _timer.Stop(); _timer.Start();
                }
                catch { }
            }
            void UpdateIntervalChecks()
            {
                foreach (ToolStripItem it in intervalMenu.DropDownItems)
                {
                    if (it is ToolStripMenuItem mi && mi.Tag is int m)
                        mi.Checked = (_config.RefreshMinutes ?? 1) == m;
                }
            }
            void AddIntervalItem(string text, int minutes)
            {
                var mi = new ToolStripMenuItem(text) { CheckOnClick = false, Tag = minutes };
                mi.Click += (_, __) => { ApplyRefreshMinutes(minutes); UpdateIntervalChecks(); };
                intervalMenu.DropDownItems.Add(mi);
            }
            AddIntervalItem("1 min", 1);
            AddIntervalItem("5 min", 5);
            AddIntervalItem("15 min", 15);
            AddIntervalItem("30 min", 30);
            AddIntervalItem("1 h", 60);
            UpdateIntervalChecks();
            menu.Items.Add(intervalMenu);
            // Legend submenu (placed before penultimate radar link so radar remains penultimate)
            var legendMenu = new ToolStripMenuItem("Legenda");
            legendMenu.DropDownItems.Add("Ikona počasí", null, (_, __) => ShowIconsLegendOverlay());
            legendMenu.DropDownItems.Add("Pozadí počasí", null, (_, __) => ShowLegendOverlay());
            menu.Items.Add(legendMenu);
            menu.Items.Add("Otevřít meteogram", null, (_, __) => OpenForecast());
            // place radar site as the penultimate item
            menu.Items.Add("Otevřít www.pocasiaradar.cz", null, (_, __) => OpenRadar());
            menu.Items.Add("Ukončit", null, (_, __) => ExitThread());
            _notifyIcon.ContextMenuStrip = menu;

            // Set placeholder icon immediately so the tray shows up
            try
            {
                var placeholder = RenderIcon(0, 3); // 0°C, cloudy
                SwapIcon(placeholder);
            }
            catch (Exception ex)
            {
                Log("Init placeholder icon failed: " + ex.Message);
            }

            int minutes = _config.RefreshMinutes.HasValue && _config.RefreshMinutes.Value >= 1 ? _config.RefreshMinutes.Value : 1;
            _timer = new System.Windows.Forms.Timer { Interval = minutes * 60 * 1000 }; // default 10 minutes
            _timer.Tick += async (_, __) => await RefreshWeather();
            _timer.Start();

            // initial load (fire and forget)
            _ = RefreshWeather(forceGeo:true);
            
            // Detect WebView2 Evergreen runtime availability
            try
            {
                var ver = CoreWebView2Environment.GetAvailableBrowserVersionString();
                _webView2Missing = string.IsNullOrEmpty(ver);
                if (_webView2Missing)
                {
                    // Jednorázová informace pro uživatele
                    try
                    {
                        _notifyIcon.BalloonTipTitle = "Chybí WebView2";
                        _notifyIcon.BalloonTipText = "Pro náhled meteogramu nainstalujte WebView2 Runtime (viz menu).";
                        _notifyIcon.ShowBalloonTip(5000);
                    }
                    catch { }
                }
            }
            catch { _webView2Missing = false; }

            // Init delayed single-click timer (to allow double-click to take precedence)
            _singleClickTimer = new System.Windows.Forms.Timer { Interval = Math.Max(200, SystemInformation.DoubleClickTime) };
            _singleClickTimer.Tick += async (_, __) =>
            {
                try
                {
                    _singleClickTimer.Stop();
                    if (_pendingSingleOpen && !_justClosedOverlay && !_webView2Missing && (_radarForm == null || _radarForm.IsDisposed || !_radarForm.Visible) && (_previewForm == null || !_previewForm.Visible))
                    {
                        await ShowRadarOverlayAsync();
                    }
                }
                catch (Exception ex) { Log("SingleClickTimer error: " + ex.Message); }
                finally { _pendingSingleOpen = false; }
            };

            // Click-to-close-if-open (single click): if preview OR radar is visible, close it; otherwise schedule opening radar (allowing double-click to cancel)
            _notifyIcon.MouseClick += async (_, e) => {
                try
                {
                    if (e.Button == MouseButtons.Left)
                    {
                        // If preview (meteogram) is open, close it
                        if (_previewForm != null && _previewForm.Visible) { HideForecastPreview(); return; }
                        // Else if radar is open, close it
                        if (_radarForm != null && !_radarForm.IsDisposed && _radarForm.Visible) { _radarForm.Hide(); MarkJustClosedOverlay(); return; }
                        if (_justClosedOverlay) { return; }
                        if (_webView2Missing)
                        {
                            MessageBox.Show("Pro zobrazení radaru je nutné mít nainstalovaný WebView2 Runtime. Otevři menu a zvol \"Instalovat WebView2…\".", "WebView2 není nainstalováno", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            return;
                        }
                        // Delay the open to give DoubleClick a chance to arrive and cancel
                        _pendingSingleOpen = true;
                        _singleClickTimer.Stop();
                        _singleClickTimer.Interval = Math.Max(200, SystemInformation.DoubleClickTime);
                        _singleClickTimer.Start();
                    }
                }
                catch (Exception ex) { Log("MouseClick action error: " + ex.Message); }
            };

            // Double-click: toggle meteogram preview (cancels pending single-click open)
            _notifyIcon.MouseDoubleClick += async (_, __) =>
            {
                try
                {
                    if (_justClosedOverlay) { _justClosedOverlay = false; return; }
                    _pendingSingleOpen = false; _singleClickTimer.Stop();
                    if (_previewForm != null && _previewForm.Visible) { HideForecastPreview(); return; }
                    if (_webView2Missing)
                    {
                        MessageBox.Show("Pro zobrazení meteogramu je nutné mít nainstalovaný WebView2 Runtime. Otevři menu a zvol \"Instalovat WebView2…\".", "WebView2 není nainstalováno", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    await ShowForecastPreviewAsync();
                }
                catch (Exception ex) { Log("MouseDoubleClick preview error: " + ex.Message); }
            };

            // Meteogram periodic capture every 15 minutes (only if WebView2 is available)
            _meteogramTimer = new System.Windows.Forms.Timer { Interval = 15 * 60 * 1000 };
            _meteogramTimer.Tick += async (_, __) => { if (!_webView2Missing) await CaptureMeteogramIfPossibleAsync(); };
            _meteogramTimer.Start();
            _meteogramHasFresh = false; _meteogramLoading = true;
            if (!_webView2Missing)
                _ = CaptureMeteogramIfPossibleAsync(); // initial capture at startup
        }
        
        private void EnsureForecastPreviewForm()
        {
            if (_previewForm != null && !_previewForm.IsDisposed) return;
            _previewBox = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
            _previewForm = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                TopMost = true,
                BackColor = Color.Black
            };
            _previewForm.Controls.Add(_previewBox);
            _previewForm.Deactivate += (_, __) => { HideForecastPreview(); };
            _previewForm.MouseLeave += (_, __) => { HideForecastPreview(); };
            _previewForm.KeyPreview = true;
            _previewForm.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) HideForecastPreview(); };
        }

        private async Task ShowForecastPreviewAsync()
        {
            try
            {
                EnsureForecastPreviewForm();
                int w = 820; int h = 420; // even taller to fully reveal the bottom axis
                string path = GetMeteogramPath();
                // Always prefer cached image if present; otherwise show placeholder (even if not loading) so user sees feedback
                if (!_meteogramHasFresh)
                {
                    if (System.IO.File.Exists(path))
                    {
                        try
                        {
                            var bytesCached = System.IO.File.ReadAllBytes(path);
                            using var msCached = new System.IO.MemoryStream(bytesCached);
                            _previewBox.Image?.Dispose();
                            _previewBox.Image = Image.FromStream(msCached);
                        }
                        catch
                        {
                            _previewBox.Image?.Dispose();
                            _previewBox.Image = CreatePlaceholder(w, h, "Načítám meteogram…");
                        }
                    }
                    else
                    {
                        _previewBox.Image?.Dispose();
                        _previewBox.Image = CreatePlaceholder(w, h, "Načítám meteogram…");
                    }
                    _previewForm.Size = new Size(w, h);
                    var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                    int x = wa.Right - w - 8; int y = wa.Bottom - h - 8; if (x < wa.Left + 2) x = wa.Left + 2; if (y < wa.Top + 2) y = wa.Top + 2;
                    _previewForm.Location = new Point(x, y);
                    if (!_previewForm.Visible) _previewForm.Show();
                    EnsureTopMost(_previewForm);
                    try { _previewForm.Activate(); } catch { }
                    Log("Preview shown (no fresh), using cache/placeholder");
                    return;
                }
                // If file is too old (> 20 min), refresh in background (will mark loading but keep current visible until fresh ready)
                try
                {
                    var age = DateTime.Now - System.IO.File.GetLastWriteTime(path);
                    if (age.TotalMinutes > 20)
                    {
                        _meteogramLoading = true; _meteogramHasFresh = false;
                        _ = CaptureMeteogramIfPossibleAsync();
                    }
                }
                catch { }
                try
                {
                    if (System.IO.File.Exists(path))
                    {
                        var bytes = System.IO.File.ReadAllBytes(path);
                        using var ms = new System.IO.MemoryStream(bytes);
                        _previewBox.Image?.Dispose();
                        _previewBox.Image = Image.FromStream(ms);
                    }
                }
                catch
                {
                    _previewBox.Image?.Dispose();
                    _previewBox.Image = CreatePlaceholder(w, h, "Nelze načíst meteogram");
                }
                _previewForm.Size = new Size(w, h);
                var wa2 = Screen.FromPoint(Cursor.Position).WorkingArea;
                int x2 = wa2.Right - w - 8; int y2 = wa2.Bottom - h - 8; if (x2 < wa2.Left + 2) x2 = wa2.Left + 2; if (y2 < wa2.Top + 2) y2 = wa2.Top + 2;
                _previewForm.Location = new Point(x2, y2);
                if (!_previewForm.Visible) _previewForm.Show();
                EnsureTopMost(_previewForm);
                try { _previewForm.Activate(); } catch { }
                Log("Preview shown (fresh or loaded)");
            }
            catch (Exception ex)
            {
                Log("ShowForecastPreview failed: " + ex.Message);
            }
        }

        private void TryRefreshPreviewFromCacheIfVisible(int w, int h)
        {
            try
            {
                if (_previewForm == null || _previewForm.IsDisposed || !_previewForm.Visible) return;
                string path = GetMeteogramPath();
                if (!System.IO.File.Exists(path)) return;
                var bytes = System.IO.File.ReadAllBytes(path);
                using var ms = new System.IO.MemoryStream(bytes);
                _previewBox.Image?.Dispose();
                _previewBox.Image = Image.FromStream(ms);
                _previewForm.Size = new Size(w, h);
            }
            catch (Exception ex) { Log("TryRefreshPreviewFromCacheIfVisible failed: " + ex.Message); }
        }

        private string GetMeteogramPath()
        {
            try
            {
                string dir = WeatherToolbar.Services.ConfigService.ConfigDir;
                return System.IO.Path.Combine(dir, "meteogram.png");
            }
            catch
            {
                return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meteogram.png");
            }
        }

        private Image CreatePlaceholder(int w, int h, string text)
        {
            var bmp = new Bitmap(w, h);
            try
            {
                using var g = Graphics.FromImage(bmp);
                g.Clear(Color.Black);
                using var br = new SolidBrush(Color.White);
                using var f = new Font(FontFamily.GenericSansSerif, 14, FontStyle.Bold);
                var sz = g.MeasureString(text, f);
                g.DrawString(text, f, br, new PointF((w - sz.Width)/2f, (h - sz.Height)/2f));
            }
            catch { }
            return bmp;
        }

        private async Task CaptureMeteogramIfPossibleAsync()
        {
            try
            {
                double lat = _lat; double lon = _lon;
                if (lat == 0 && lon == 0)
                {
                    if (_config.Latitude.HasValue && _config.Longitude.HasValue)
                    { lat = _config.Latitude.Value; lon = _config.Longitude.Value; }
                    else return;
                }
                string path = GetMeteogramPath();
                // Capture at 1200x560 for better clarity; preview will scale down
                Log("CaptureMeteogram start");
                _meteogramLoading = true; _meteogramHasFresh = false;
                var ok = await MeteogramCaptureService.CaptureAsync(lat, lon, 1200, 560, path);
                Log("CaptureMeteogram done: " + ok);
                _meteogramLoading = false; _meteogramHasFresh = ok && System.IO.File.Exists(path);
                if (_meteogramHasFresh)
                {
                    // If preview is open, refresh it now
                    TryRefreshPreviewFromCacheIfVisible(820, 420);
                }
            }
            catch (Exception ex)
            {
                Log("CaptureMeteogram failed: " + ex.Message);
                _meteogramLoading = false; // avoid lock state
            }
        }

        private Image RenderForecastPreview(WeatherToolbar.Models.DailyForecastDay[] days, int w, int h)
        {
            var bmp = new Bitmap(w, h);
            try
            {
                using var g = Graphics.FromImage(bmp);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Black);
                using var white = new SolidBrush(Color.White);
                using var light = new SolidBrush(Color.FromArgb(230, 235, 235, 235));
                using var faint = new SolidBrush(Color.FromArgb(210, 220, 220, 220));
                using var pen = new Pen(Color.FromArgb(80, 255, 255, 255), 1f);

                string fam = string.IsNullOrWhiteSpace(_config.FontFamily) ? FontFamily.GenericSansSerif.Name : _config.FontFamily;
                string famGlyph = "Segoe UI Emoji";
                try { var _ = new FontFamily(famGlyph); } catch { famGlyph = fam; }
                int baseSize = _config.FontSize.HasValue && _config.FontSize.Value >= 8 && _config.FontSize.Value <= 18 ? _config.FontSize.Value : 14;
                using var fDay = new Font(fam, Math.Max(11, baseSize), FontStyle.Bold);
                using var fGlyph = new Font(famGlyph, Math.Max(20, baseSize + 8), FontStyle.Bold);
                using var fTemp = new Font(fam, Math.Max(12, baseSize + 1), FontStyle.Bold);

                int n = Math.Min(6, days.Length);
                float colW = w / (float)n;
                for (int i = 0; i < n; i++)
                {
                    float x0 = i * colW;
                    // separator
                    if (i > 0) g.DrawLine(pen, x0, 10, x0, h - 10);
                    var d = days[i];
                    // day name
                    string day = d.Date.ToString("ddd d.M.", CultureInfo.CurrentUICulture);
                    var sizeDay = g.MeasureString(day, fDay);
                    DrawOutlinedString(g, day, fDay, light, Color.Black, x0 + (colW - sizeDay.Width)/2f, 8, 2);

                    // background circle colored by weather
                    var bg = WeatherService.ColorFor(d.WeatherCode);
                    using (var bgBrush = new SolidBrush(bg))
                    {
                        float r = Math.Min(colW, h) * 0.42f;
                        float cx = x0 + colW / 2f;
                        float cy = h * 0.52f;
                        g.FillEllipse(bgBrush, cx - r, cy - r, r * 2, r * 2);
                    }

                    // glyph (center) with outline for contrast
                    string glyph = WeatherService.Glyph(d.WeatherCode);
                    var sizeGlyph = g.MeasureString(glyph, fGlyph);
                    DrawOutlinedString(g, glyph, fGlyph, white, Color.Black, x0 + (colW - sizeGlyph.Width)/2f, (h - sizeGlyph.Height)/2f - 8, 2);

                    // temps bottom with outline
                    string tline = Math.Round(d.Tmin).ToString() + "° / " + Math.Round(d.Tmax).ToString() + "°";
                    var sizeTemp = g.MeasureString(tline, fTemp);
                    DrawOutlinedString(g, tline, fTemp, faint, Color.Black, x0 + (colW - sizeTemp.Width)/2f, h - sizeTemp.Height - 10, 2);
                }
            }
            catch
            {
            }
            return bmp;
        }

        private void DrawOutlinedString(Graphics g, string text, Font font, Brush fill, Color outlineColor, float x, float y, int radius)
        {
            using var outline = new SolidBrush(outlineColor);
            for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                g.DrawString(text, font, outline, new PointF(x + dx, y + dy));
            }
            g.DrawString(text, font, fill, new PointF(x, y));
        }

        private void HideForecastPreview()
        {
            try
            {
                if (_previewForm != null && !_previewForm.IsDisposed && _previewForm.Visible)
                {
                    _previewForm.Hide();
                    _previewBox.Image?.Dispose();
                    _previewBox.Image = null;
                    MarkJustClosedOverlay();
                }
            }
            catch { }
        }

        private void MarkJustClosedOverlay()
        {
            _justClosedOverlay = true;
            _ = Task.Run(async () => { try { await Task.Delay(500); } catch { } _justClosedOverlay = false; });
        }

        private void EnsureIconsLegendForm()
        {
            if (_iconsLegendForm != null && !_iconsLegendForm.IsDisposed) return;
            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                TopMost = true,
                BackColor = Color.Black
            };
            form.Padding = new Padding(8);
            form.KeyPreview = true;
            form.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { try { form.Hide(); } catch { } } };
            form.Deactivate += (s, e) => { try { form.Hide(); } catch { } };

            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 11,
                BackColor = Color.Black,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };
            panel.Padding = new Padding(6, 6, 6, 6);
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var glyphFont = new Font("Segoe UI Emoji", 14, FontStyle.Regular);
            var textFont = new Font("Segoe UI", 9, FontStyle.Regular);

            void AddRow(string glyph, string text)
            {
                var gl = new Label { Text = glyph, ForeColor = Color.White, AutoSize = true, Margin = new Padding(0, 0, 10, 0), Font = glyphFont };
                var lbl = new Label { Text = text, ForeColor = Color.White, AutoSize = true, Margin = new Padding(0, 6, 0, 4), Font = textFont };
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                int r = panel.RowStyles.Count - 1;
                panel.Controls.Add(gl, 0, r);
                panel.Controls.Add(lbl, 1, r);
            }

            // Use the same glyphs as rendering by calling WeatherService.Glyph(code)
            AddRow(WeatherService.Glyph(0), "0 (jasno)");
            AddRow(WeatherService.Glyph(1), "1–2 (polojasno)");
            AddRow(WeatherService.Glyph(3), "3 (zataženo)");
            AddRow(WeatherService.Glyph(45), "45–48 (mlha)");
            AddRow(WeatherService.Glyph(51), "51–67 (mrholení / mrznoucí déšť)");
            AddRow(WeatherService.Glyph(61), "61–65 (déšť)");
            AddRow(WeatherService.Glyph(71), "71–77 (sněžení)");
            AddRow(WeatherService.Glyph(80), "80–82 (přeháňky)");
            AddRow(WeatherService.Glyph(85), "85–86 (sněhové přeháňky)");
            AddRow(WeatherService.Glyph(95), "95–99 (bouřky)");
            AddRow(WeatherService.Glyph(999), "jiné (výchozí)");

            form.Controls.Add(panel);
            _iconsLegendForm = form;
        }

        private void ShowIconsLegendOverlay()
        {
            try
            {
                EnsureIconsLegendForm();
                _iconsLegendForm.AutoSize = true;
                _iconsLegendForm.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                _iconsLegendForm.PerformLayout();
                var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                int w = _iconsLegendForm.Width;
                int h = _iconsLegendForm.Height;
                int x = wa.Right - w - 8;
                int y = wa.Bottom - h - 8;
                if (x < wa.Left + 2) x = wa.Left + 2;
                if (y < wa.Top + 2) y = wa.Top + 2;
                _iconsLegendForm.Location = new Point(x, y);
                if (!_iconsLegendForm.Visible) _iconsLegendForm.Show();
                EnsureTopMost(_iconsLegendForm);
                try { _iconsLegendForm.Activate(); } catch { }
            }
            catch { }
        }

        private void EnsureLegendForm()
        {
            if (_legendForm != null && !_legendForm.IsDisposed) return;
            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                TopMost = true,
                BackColor = Color.Black
            };
            form.Padding = new Padding(8);
            form.KeyPreview = true;
            form.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { try { form.Hide(); } catch { } } };
            form.Deactivate += (s, e) => { try { form.Hide(); } catch { } };

            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 10,
                BackColor = Color.Black,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };
            panel.Padding = new Padding(6, 6, 6, 6);
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            void AddRow(Color c, string text)
            {
                var swatch = new Panel { BackColor = c, Width = 18, Height = 18, Margin = new Padding(4, 3, 10, 3) };
                var lbl = new Label { Text = text, ForeColor = Color.White, AutoSize = true, Margin = new Padding(0, 4, 0, 2) };
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                int r = panel.RowCount - 10 + panel.RowStyles.Count - 1; // computed row index as we add
                panel.Controls.Add(swatch, 0, r);
                panel.Controls.Add(lbl, 1, r);
            }

            AddRow(Color.Orange, "0 (jasno)");
            AddRow(Color.Goldenrod, "1–2 (polojasno)");
            AddRow(Color.SteelBlue, "3 (zataženo)");
            AddRow(Color.SlateGray, "45–48 (mlha)");
            AddRow(Color.DodgerBlue, "51–67 (mrholení/mrznoucí déšť)");
            AddRow(Color.RoyalBlue, "61–65 (déšť)");
            AddRow(Color.LightSlateGray, "71–77 (sněžení)");
            AddRow(Color.MediumBlue, "80–86 (přeháňky)");
            AddRow(Color.DarkSlateBlue, "95–99 (bouřky)");
            AddRow(Color.Gray, "jiné (výchozí)");

            form.Controls.Add(panel);
            _legendForm = form;
        }

        private void ShowLegendOverlay()
        {
            try
            {
                EnsureLegendForm();
                // Measure to set a reasonable size and position
                _legendForm.AutoSize = true;
                _legendForm.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                _legendForm.PerformLayout();
                var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                int w = _legendForm.Width;
                int h = _legendForm.Height;
                int x = wa.Right - w - 8;
                int y = wa.Bottom - h - 8;
                if (x < wa.Left + 2) x = wa.Left + 2;
                if (y < wa.Top + 2) y = wa.Top + 2;
                _legendForm.Location = new Point(x, y);
                if (!_legendForm.Visible) _legendForm.Show();
                EnsureTopMost(_legendForm);
                try { _legendForm.Activate(); } catch { }
            }
            catch { }
        }

        private async Task EnsureRadarFormAsync()
        {
            if (_radarForm != null && !_radarForm.IsDisposed) return;
            _radarForm = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                TopMost = true,
                BackColor = Color.Black
            };
            _radarForm.Deactivate += (_, __) => { try { _radarForm.Hide(); MarkJustClosedOverlay(); } catch { } };
            _radarForm.KeyPreview = true;
            _radarForm.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { try { _radarForm.Hide(); MarkJustClosedOverlay(); } catch { } } };
            _radarWeb = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black, TabStop = true };
            _radarWeb.PreviewKeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { try { _radarForm.Hide(); MarkJustClosedOverlay(); } catch { } } };
            _radarForm.Controls.Add(_radarWeb);
            await _radarWeb.EnsureCoreWebView2Async();
            // Inject JS to forward Esc from the web content to host via postMessage
            try
            {
                string escBridge = @"(function(){try{document.addEventListener('keydown',function(e){try{if(e.key==='Escape'){ if (window.chrome && chrome.webview && chrome.webview.postMessage){ chrome.webview.postMessage('esc'); } } }catch(_){}}, true);}catch(_){}})();";
                await _radarWeb.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(escBridge);
                _radarWeb.CoreWebView2.WebMessageReceived += (s, e) => {
                    try { var msg = e.TryGetWebMessageAsString(); if (string.Equals(msg, "esc", StringComparison.OrdinalIgnoreCase)) { _radarForm?.Hide(); MarkJustClosedOverlay(); } } catch { }
                };
            }
            catch { }
        }

        private async Task ShowRadarOverlayAsync()
        {
            try
            {
                await EnsureRadarFormAsync();
                int w = 560, h = 420;
                _radarForm.Size = new Size(w, h);
                var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                int x = wa.Right - w - 8; int y = wa.Bottom - h - 8; if (x < wa.Left + 2) x = wa.Left + 2; if (y < wa.Top + 2) y = wa.Top + 2;
                _radarForm.Location = new Point(x, y);

                // Build custom HTML (Leaflet + RainViewer tiles) with autoplay of last 60 minutes and current position marker
                double lat = Math.Abs(_lat) < double.Epsilon ? 49.310495 : _lat;
                double lon = Math.Abs(_lon) < double.Epsilon ? 14.1414903 : _lon;
                string latStr = lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string lonStr = lon.ToString(System.Globalization.CultureInfo.InvariantCulture);
                int zoom = 8;
                string html = $@"<!DOCTYPE html>
<html>
<head>
  <meta charset='utf-8'/>
  <meta http-equiv='X-UA-Compatible' content='IE=edge'/>
  <meta name='viewport' content='width=device-width, initial-scale=1'/>
  <link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css'/>
  <style>
    html,body,#map{{height:100%;margin:0;background:#000}}
    .leaflet-control-attribution{{display:none}}
    #ts{{position:absolute;right:8px;bottom:8px;color:#fff;background:rgba(0,0,0,0.5);padding:4px 6px;border-radius:4px;font:12px/14px Segoe UI,Arial,sans-serif;z-index:1000}}
  </style>
  <script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script>
  <script>const LAT={latStr}, LON={lonStr}, Z={zoom};</script>
</head>
<body>
  <div id='map'></div>
  <div id='ts'>--:--</div>
  <script>
    const map = L.map('map', {{ zoomControl:false }}).setView([LAT, LON], Z);
    // Dark basemap
    L.tileLayer('https://{{s}}.basemaps.cartocdn.com/dark_all/{{z}}/{{x}}/{{y}}{{r}}.png', {{ subdomains:['a','b','c','d'], maxZoom: 14 }}).addTo(map);
    // Scale control (metric only)
    L.control.scale({{imperial:false, position:'bottomleft'}}).addTo(map);
    // marker of current position
    L.circleMarker([LAT, LON], {{ radius:5, color:'#fff', weight:2, fillColor:'#00aaff', fillOpacity:0.9 }}).addTo(map);
    // Load RainViewer frames JSON (radar past + nowcast)
    fetch('https://api.rainviewer.com/public/weather-maps.json').then(r=>r.json()).then(data=>{{
      const past = (data.radar && data.radar.past) ? data.radar.past : [];
      const nowc = (data.radar && data.radar.nowcast) ? data.radar.nowcast : [];
      const all = past.concat(nowc);
      const cutoff = Date.now()/1000 - 3600; // last 60 minutes
      let frames = all.filter(f=>f.time>=cutoff);
      if(frames.length===0) frames = past.slice(-4);
      if(frames.length===0) return; // nothing to show
      const layers = [];
      let idx = 0;
      function urlFor(t){{ return `https://tilecache.rainviewer.com/v2/radar/${{t}}/256/{{z}}/{{x}}/{{y}}/2/1_1.png`; }}
      function show(i){{
        const t = frames[i].time || frames[i];
        const layer = L.tileLayer(urlFor(t), {{ opacity:0.0, zIndex: 400 }}).addTo(map);
        layers.push(layer);
        let op = 0.0;
        const fad = setInterval(()=>{{ op+=0.2; layer.setOpacity(Math.min(0.95,op)); if(op>=0.95) clearInterval(fad); }}, 40);
        // remove previous layer after fade-in
        if (layers.length>2) {{ const old = layers.shift(); map.removeLayer(old); }}
        // update timestamp label (local time HH:MM)
        try{{
          var dt = new Date(t*1000);
          var hh = dt.getHours().toString().padStart(2,'0');
          var mm = dt.getMinutes().toString().padStart(2,'0');
          document.getElementById('ts').textContent = hh+':'+mm;
        }}catch(e){{}}
      }}
      show(0);
      setInterval(()=>{{ idx=(idx+1)%frames.length; show(idx); }}, 500);
    }}).catch(()=>{{}});
  </script>
</body>
</html>";
                _radarWeb.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _radarWeb.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _radarWeb.CoreWebView2.Settings.IsZoomControlEnabled = false;
                _radarWeb.CoreWebView2.NavigateToString(html);

                if (!_radarForm.Visible) _radarForm.Show();
                EnsureTopMost(_radarForm);
                try { _radarForm.Activate(); _radarWeb.Focus(); } catch { }
            }
            catch (Exception ex)
            {
                Log("ShowRadarOverlay failed: " + ex.Message);
            }
        }

        // No hover-based preview anymore; tooltip handled by NotifyIcon.Text automatically

        


        private void TryFixCzechCityCoordinates()
        {
            try
            {
                if (_config == null) return;
                bool cityIsPisek = !string.IsNullOrWhiteSpace(_config.City) && _config.City.Trim().ToLowerInvariant().Contains("písek".ToLowerInvariant().Replace("í","i")) ||
                                   (!string.IsNullOrWhiteSpace(_config.City) && _config.City.Trim().ToLowerInvariant().Contains("pisek"));
                bool countryIsCz = !string.IsNullOrWhiteSpace(_config.Country) && (_config.Country.ToLowerInvariant().Contains("čes") || _config.Country.ToLowerInvariant().Contains("czech"));

                bool hasCoords = _config.Latitude.HasValue && _config.Longitude.HasValue;
                bool coordsInCz = hasCoords && IsInCz(_config.Latitude!.Value, _config.Longitude!.Value);

                if ((cityIsPisek || countryIsCz) && (!hasCoords || !coordsInCz))
                {
                    var res = _reverseGeo.GetCoordsAsync(_config.City ?? "Písek", "cs", "CZ").GetAwaiter().GetResult();
                    if (res.HasValue)
                    {
                        _config.Latitude = res.Value.lat;
                        _config.Longitude = res.Value.lon;
                        if (string.IsNullOrWhiteSpace(_config.City)) _config.City = res.Value.display;
                        if (string.IsNullOrWhiteSpace(_config.Country)) _config.Country = "Česko";
                        ConfigService.Save(_config);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("TryFixCzechCityCoordinates failed: " + ex.Message);
            }
        }

        private static bool IsInCz(double lat, double lon)
        {
            // Approximate CZ bounds
            return lat >= 48.5 && lat <= 51.2 && lon >= 12.0 && lon <= 19.0;
        }

        private void OpenFontDialog()
        {
            try
            {
                using (var dlg = new FontDialog())
                {
                    string family = string.IsNullOrWhiteSpace(_config.FontFamily) ? FontFamily.GenericSansSerif.Name : _config.FontFamily;
                    int size = _config.FontSize.HasValue && _config.FontSize.Value >= 8 && _config.FontSize.Value <= 18 ? _config.FontSize.Value : 11;
                    dlg.Font = new Font(family, size, FontStyle.Bold);
                    dlg.ShowEffects = false;
                    dlg.AllowScriptChange = false;
                    dlg.MinSize = 8;
                    dlg.MaxSize = 18;
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        _config.FontFamily = dlg.Font.FontFamily.Name;
                        _config.FontSize = (int)Math.Round(dlg.Font.SizeInPoints);
                        ConfigService.Save(_config);
                        // Re-render immediately
                        _ = RefreshWeather();
                    }
                }
            }
            catch (Exception ex)
            {
                Log("OpenFontDialog failed: " + ex.Message);
            }
        }

        protected override void ExitThreadCore()
        {
            _timer?.Stop();
            _timer?.Dispose();
            if (_currentIcon != null)
            {
                _notifyIcon.Icon = null;
                _currentIcon.Dispose();
                _currentIcon = null;
            }
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            base.ExitThreadCore();
        }

        private async Task RefreshWeather(bool forceGeo = false)
        {
            try
            {
                if (_config.Latitude.HasValue && _config.Longitude.HasValue)
                {
                    _lat = _config.Latitude.Value;
                    _lon = _config.Longitude.Value;
                    // try get place from config first
                    if (!string.IsNullOrWhiteSpace(_config.City) || !string.IsNullOrWhiteSpace(_config.Country))
                    {
                        _place = string.IsNullOrWhiteSpace(_config.Country) ? _config.City :
                            string.IsNullOrWhiteSpace(_config.City) ? _config.Country : $"{_config.City}, {_config.Country}";
                    }
                    else
                    {
                        _place = null;
                    }
                }
                else if (forceGeo || Math.Abs(_lat) < double.Epsilon || Math.Abs(_lon) < double.Epsilon)
                {
                    var loc = await _geoService.GetLocationAsync();
                    if (loc != null)
                    {
                        _lat = loc.Latitude;
                        _lon = loc.Longitude;
                        if (!string.IsNullOrWhiteSpace(loc.City) || !string.IsNullOrWhiteSpace(loc.Country))
                        {
                            _place = string.IsNullOrWhiteSpace(loc.Country) ? loc.City :
                                string.IsNullOrWhiteSpace(loc.City) ? loc.Country : $"{loc.City}, {loc.Country}";
                        }
                    }
                }

                if (Math.Abs(_lat) < double.Epsilon && Math.Abs(_lon) < double.Epsilon)
                {
                    SetTextOnly("Nelze zjistit polohu");
                    return;
                }

                // Reverse geocode if we have coords but no place text yet
                if (string.IsNullOrWhiteSpace(_place))
                {
                    try
                    {
                        _place = await _reverseGeo.GetPlaceAsync(_lat, _lon);
                    }
                    catch (Exception ex)
                    {
                        Log("Reverse geocoding failed: " + ex.Message);
                    }
                }

                var current = await _weatherService.GetCurrentAsync(_lat, _lon);
                if (current == null)
                {
                    SetTextOnly("Nelze načíst počasí");
                    return;
                }

                string tempText = Math.Round(current.temperature_2m).ToString() + "°C";
                string feels = current.apparent_temperature.HasValue ? ($" (pocitově {Math.Round(current.apparent_temperature.Value)}°C)") : string.Empty;
                string windStr = string.Empty;
                if (current.wind_speed_10m.HasValue || current.wind_direction_10m.HasValue)
                {
                    string dir = current.wind_direction_10m.HasValue ? ToCardinal(current.wind_direction_10m.Value) : "--";
                    string spd = current.wind_speed_10m.HasValue ? Math.Round(current.wind_speed_10m.Value, 1).ToString("0.0") : "--";
                    windStr = $" | Vítr {spd} m/s {dir}";
                }
                string place = _place ?? string.Empty;
                string tooltip = string.IsNullOrWhiteSpace(place) ?
                    $"{tempText}{feels} • {WeatherService.Describe(current.weather_code)}{windStr}" :
                    $"{tempText}{feels} • {WeatherService.Describe(current.weather_code)}{windStr} — {place}";
                _notifyIcon.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;

                var icon = RenderIcon((int)Math.Round(current.temperature_2m), current.weather_code);
                SwapIcon(icon);

                // success: ensure normal interval
                int normalMs = (_config.RefreshMinutes.HasValue && _config.RefreshMinutes.Value >= 1 ? _config.RefreshMinutes.Value : 1) * 60 * 1000;
                if (_timer.Interval != normalMs)
                    _timer.Interval = normalMs;
            }
            catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                SetTextOnly("Limit API – další pokus za 2 min");
                Log("Rate limited (429): " + ex.Message);
                _timer.Interval = 2 * 60 * 1000; // retry sooner
            }
            catch (Exception ex)
            {
                SetTextOnly("Chyba: " + ex.Message);
                Log("RefreshWeather error: " + ex);
            }
        }

        private void OpenForecast()
        {
            if (Math.Abs(_lat) < double.Epsilon && Math.Abs(_lon) < double.Epsilon)
                return;
            // Meteograms with current coords
            string url = $"https://meteograms.com/#/{_lat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{_lon.ToString(System.Globalization.CultureInfo.InvariantCulture)},12/96/";
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log("OpenForecast failed: " + ex.Message);
            }
        }

        private void OpenRadar()
        {
            if (Math.Abs(_lat) < double.Epsilon && Math.Abs(_lon) < double.Epsilon)
                return;
            // Dynamic meteoradar URL with current coords
            string latStr = _lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string lonStr = _lon.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string url = $"https://www.pocasiaradar.cz/meteoradar/pisek/3429501?center={latStr},{lonStr}&placemark={latStr},{lonStr}&tz=Europe%2FPrague";
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log("OpenRadar failed: " + ex.Message);
            }
        }

        private void OpenLocationDialog()
        {
            try
            {
                using (var dlg = new LocationForm(_config))
                {
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        // Update config from dialog
                        if (dlg.Latitude.HasValue && dlg.Longitude.HasValue)
                        {
                            _config.Latitude = dlg.Latitude.Value;
                            _config.Longitude = dlg.Longitude.Value;
                        }
                        else
                        {
                            _config.Latitude = null;
                            _config.Longitude = null;
                        }

                        _config.City = string.IsNullOrWhiteSpace(dlg.City) ? null : dlg.City;

                        // If City is provided but coords missing, auto forward-geocode now
                        if (_config.City != null && (!_config.Latitude.HasValue || !_config.Longitude.HasValue))
                        {
                            try
                            {
                                string cc = null;
                                if (!string.IsNullOrWhiteSpace(_config.Country))
                                {
                                    var c = _config.Country.ToLowerInvariant();
                                    if (c.Contains("čes") || c.Contains("czech")) cc = "CZ";
                                }
                                var res = _reverseGeo.GetCoordsAsync(_config.City, "cs", cc ?? "CZ").GetAwaiter().GetResult();
                                if (res.HasValue)
                                {
                                    _config.Latitude = res.Value.lat;
                                    _config.Longitude = res.Value.lon;
                                    _place = res.Value.display;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log("Dialog forward geocode failed: " + ex.Message);
                                _place = _config.City; // fallback label
                            }
                        }

                        ConfigService.Save(_config);

                        // Apply immediately
                        _lat = 0; _lon = 0; // force re-fetch geo if now auto mode
                        _ = RefreshWeather(forceGeo:true);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("OpenLocationDialog failed: " + ex.Message);
            }
        }

        private void SetTextOnly(string text)
        {
            _notifyIcon.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        private static string ToCardinal(double degrees)
        {
            // 8-point compass: N, NE, E, SE, S, SW, W, NW localized as S, SV, V, JV, J, JZ, Z, SZ
            string[] dirs = new[] { "S", "SV", "V", "JV", "J", "JZ", "Z", "SZ" };
            // Open-Meteo direction: 0 = North, 90 = East
            double idx = ((degrees + 22.5) % 360) / 45.0; // 0..8
            int i = (int)Math.Floor(idx);
            if (i < 0) i = 0; if (i >= dirs.Length) i = dirs.Length - 1;
            return dirs[i];
        }

        private void EnsureTopMost(Form f)
        {
            try
            {
                if (f == null || f.IsDisposed) return;
                f.TopMost = true;
                f.ShowInTaskbar = false;
                f.BringToFront();
                // Force Z-order to TOPMOST without changing size/position
                SetWindowPos(f.Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // NOMOVE | NOSIZE | SHOWWINDOW
            }
            catch { }
        }

        private void SwapIcon(Icon newIcon)
        {
            if (newIcon == null) return;
            var old = _currentIcon;
            _currentIcon = newIcon;
            _notifyIcon.Icon = newIcon;
            if (old != null)
            {
                old.Dispose();
            }
        }

        private Icon RenderIcon(int temperatureC, int weatherCode)
        {
            // Draw a 32x32 bitmap then convert to icon
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                // Background circle based on condition
                var bg = WeatherService.ColorFor(weatherCode);
                using (var brush = new SolidBrush(bg))
                {
                    g.FillEllipse(brush, 0, 0, 32, 32);
                }

                // Resolve font from config with sane defaults (default 16)
                int baseSize = _config.FontSize.HasValue && _config.FontSize.Value >= 8 && _config.FontSize.Value <= 18 ? _config.FontSize.Value : 16;
                string fam = string.IsNullOrWhiteSpace(_config.FontFamily) ? FontFamily.GenericSansSerif.Name : _config.FontFamily;

                // Temperature text (drawn last, with outline) - original style (no backdrop)
                using (var font = new Font(fam, baseSize, FontStyle.Bold))
                {
                    string num = temperatureC.ToString();
                    // Measure number width to place degree sign tightly
                    var numSize = g.MeasureString(num, font);
                    // Compute center position for number within 32x32, slight downward shift to avoid glyph
                    float nx = (32 - numSize.Width) / 2f;
                    float ny = (32 - numSize.Height) / 2f + 2;
                    if (nx < -2) nx = -2; // clamp
                    if (ny < -2) ny = -2;
                    // Auto-contrast: choose white or black based on background luminance
                    var lum = 0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B;
                    Color fg = lum < 140 ? Color.White : Color.Black;
                    int r = _config.OutlineRadius.HasValue ? Math.Max(0, Math.Min(3, _config.OutlineRadius.Value)) : 2;
                    using (var textBrush = new SolidBrush(fg))
                    using (var outline = new SolidBrush(fg == Color.White ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255)))
                    {
                        // Outline around number for readability, configurable radius
                        for (int dx = -r; dx <= r; dx++)
                        for (int dy = -r; dy <= r; dy++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            g.DrawString(num, font, outline, new PointF(nx + dx, ny + dy));
                        }
                        g.DrawString(num, font, textBrush, new PointF(nx, ny));
                    }

                    // Degree sign (small) placed at right-top of number
                    int degSize = Math.Max(7, baseSize - 3);
                    using (var degFont = new Font(fam, degSize, FontStyle.Bold))
                    {
                        float dx = nx + numSize.Width - Math.Min(6f, numSize.Width * 0.25f);
                        float dy = ny - 1;
                        // keep inside bounds
                        if (dx > 24) dx = 24;
                        if (dy < 0) dy = 0;
                        // Outline for degree as well
                        var lum2 = 0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B;
                        Color fg2 = lum2 < 140 ? Color.White : Color.Black;
                        using (var textBrush2 = new SolidBrush(fg2))
                        using (var outline2 = new SolidBrush(fg2 == Color.White ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255)))
                        {
                            for (int ox = -r; ox <= r; ox++)
                            for (int oy = -r; oy <= r; oy++)
                            {
                                if (ox == 0 && oy == 0) continue;
                                g.DrawString("°", degFont, outline2, new PointF(dx + ox, dy + oy));
                            }
                            g.DrawString("°", degFont, textBrush2, new PointF(dx, dy));
                        }
                    }
                }

                // Draw glyph LAST (original behavior), moved to top-left
                if (_config.ShowGlyph ?? true)
                {
                    var glyph = WeatherService.Glyph(weatherCode);
                    int glyphSize = Math.Max(9, baseSize + 1); // slightly larger glyph
                    using (var font2 = new Font(fam, glyphSize, FontStyle.Bold))
                    using (var sf2 = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near })
                    using (var textBrush2 = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                    {
                        // push glyph further into the corner; allow partial clipping, larger box
                        g.DrawString(glyph, font2, textBrush2, new RectangleF(-5, -10, 24, 20), sf2);
                    }
                }

                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    return Icon.FromHandle(hIcon).Clone() as Icon;
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }

        private void Log(string message)
        {
            try
            {
                if (!(ConfigService.IsLoggingEnabled())) return;
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeatherToolbar");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "app.log");
                System.IO.File.AppendAllText(path, DateTime.Now.ToString("s") + ": " + message + Environment.NewLine);
            }
            catch { }
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        extern static bool DestroyIcon(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    }
}
