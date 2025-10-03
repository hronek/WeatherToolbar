
using System;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using WeatherToolbar.Services;

namespace WeatherToolbar
{
    public class LocationForm : Form
    {
        private readonly TextBox _txtCity = new TextBox();
        private readonly TextBox _txtLat = new TextBox();
        private readonly TextBox _txtLon = new TextBox();
        private readonly Button _btnSearch = new Button();
        private readonly Button _btnOk = new Button();
        private readonly Button _btnCancel = new Button();
        private readonly Button _btnClear = new Button();
        private readonly Label _lblStatus = new Label();

        private readonly ReverseGeoService _geo = new ReverseGeoService();

        public string City => _txtCity.Text.Trim();
        public double? Latitude { get; private set; }
        public double? Longitude { get; private set; }

        public LocationForm(AppConfig current)
        {
            Text = "Nastavit polohu";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(360, 190);

            var lblCity = new Label { Text = "Město:", AutoSize = true, Location = new Point(12, 15) };
            _txtCity.SetBounds(100, 12, 240, 24);

            var lblLat = new Label { Text = "Šířka (lat):", AutoSize = true, Location = new Point(12, 48) };
            _txtLat.SetBounds(100, 45, 100, 24);
            var lblLon = new Label { Text = "Délka (lon):", AutoSize = true, Location = new Point(210, 48) };
            _txtLon.SetBounds(280, 45, 60, 24);

            _btnSearch.Text = "Vyhledat";
            _btnSearch.SetBounds(100, 75, 80, 28);
            _btnSearch.Click += async (_, __) => await DoSearch();

            _btnClear.Text = "Zrušit pevnou polohu";
            _btnClear.SetBounds(190, 75, 150, 28);
            _btnClear.Click += (_, __) => { _txtCity.Text = string.Empty; _txtLat.Text = string.Empty; _txtLon.Text = string.Empty; Latitude = null; Longitude = null; _lblStatus.Text = ""; };

            _lblStatus.AutoSize = false;
            _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            _lblStatus.SetBounds(12, 110, 328, 24);
            _lblStatus.ForeColor = Color.DimGray;

            _btnOk.Text = "OK";
            _btnOk.SetBounds(190, 140, 70, 28);
            _btnOk.Click += (_, __) =>
            {
                if (TryParseLatLon(_txtLat.Text, _txtLon.Text, out var lat, out var lon))
                {
                    Latitude = lat;
                    Longitude = lon;
                }
                else
                {
                    Latitude = null;
                    Longitude = null;
                }
                DialogResult = DialogResult.OK;
                Close();
            };

            _btnCancel.Text = "Zrušit";
            _btnCancel.SetBounds(270, 140, 70, 28);
            _btnCancel.Click += (_, __) => { DialogResult = DialogResult.Cancel; Close(); };

            Controls.AddRange(new Control[] { lblCity, _txtCity, lblLat, _txtLat, lblLon, _txtLon, _btnSearch, _btnClear, _lblStatus, _btnOk, _btnCancel });

            // preset
            if (!string.IsNullOrWhiteSpace(current?.City)) _txtCity.Text = current.City;
            if (current?.Latitude is double clat && current?.Longitude is double clon)
            {
                _txtLat.Text = clat.ToString(CultureInfo.InvariantCulture);
                _txtLon.Text = clon.ToString(CultureInfo.InvariantCulture);
                Latitude = clat; Longitude = clon;
            }
        }

        private async Task DoSearch()
        {
            _lblStatus.ForeColor = Color.DimGray;
            _lblStatus.Text = "Hledám…";
            try
            {
                var q = _txtCity.Text.Trim();
                var res = await _geo.GetCoordsAsync(q, "cs", "CZ");
                if (res == null)
                {
                    _lblStatus.ForeColor = Color.Firebrick;
                    _lblStatus.Text = "Nenalezeno";
                    return;
                }
                Latitude = res?.lat;
                Longitude = res?.lon;
                _txtLat.Text = Latitude?.ToString(CultureInfo.InvariantCulture);
                _txtLon.Text = Longitude?.ToString(CultureInfo.InvariantCulture);
                _lblStatus.ForeColor = Color.ForestGreen;
                _lblStatus.Text = res?.display;
            }
            catch (Exception ex)
            {
                _lblStatus.ForeColor = Color.Firebrick;
                _lblStatus.Text = ex.Message;
            }
        }

        private static bool TryParseLatLon(string latStr, string lonStr, out double lat, out double lon)
        {
            bool ok1 = double.TryParse(latStr, NumberStyles.Float, CultureInfo.InvariantCulture, out lat);
            bool ok2 = double.TryParse(lonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out lon);
            return ok1 && ok2;
        }
    }
}
