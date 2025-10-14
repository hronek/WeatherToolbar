using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Windows.Forms;

namespace WeatherToolbar.Services
{
    public static class MeteogramCaptureService
    {
        public static async Task<bool> CaptureAsync(double lat, double lon, int width, int height, string outPath, int durationHours = 96)
        {
            try
            {
                // Ensure output directory
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

                using var form = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Bounds = new System.Drawing.Rectangle(-2000, -2000, width, height), // off-screen
                    Opacity = 0.0,
                    TopMost = false
                };

                using var web = new WebView2
                {
                    Dock = DockStyle.Fill
                };
                form.Controls.Add(web);

                // Initialize WebView2 with explicit environment so UserDataFolder is in AppData (not next to EXE)
                string userData = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeatherToolbar", "WebView2");
                Directory.CreateDirectory(userData);
                var opts = new CoreWebView2EnvironmentOptions();
                // Limit disk cache to ~30 MB and keep media cache tiny
                opts.AdditionalBrowserArguments = "--disk-cache-size=31457280 --media-cache-size=1048576";
                var env = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: userData, options: opts);
                await web.EnsureCoreWebView2Async(env);
                web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                web.CoreWebView2.Settings.IsZoomControlEnabled = false;
                web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                web.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
                // Deny geolocation to prevent site from overriding to default location
                try
                {
                    web.CoreWebView2.PermissionRequested += (s, e) =>
                    {
                        try
                        {
                            if (e.PermissionKind == CoreWebView2PermissionKind.Geolocation)
                            {
                                e.State = CoreWebView2PermissionState.Deny;
                                e.Handled = true;
                            }
                        }
                        catch { }
                    };
                }
                catch { }

                // Inject global script to accept cookies and hide banners in all future navigations/frames
                string cookieAutoScript = @"
                  (function(){
                    function walkShadow(root, fn){
                      try{
                        fn(root);
                        var tree = (root.querySelectorAll? Array.from(root.querySelectorAll('*')): []);
                        for (var el of tree){
                          if (el.shadowRoot){ walkShadow(el.shadowRoot, fn); }
                        }
                      }catch(e){}
                    }
                    try {
                      const style = document.createElement('style');
                      style.textContent = `
                        #onetrust-banner-sdk, #onetrust-consent-sdk, .ot-sdk-container,
                        [id*='cookie'][class], [class*='cookie'], [id*='consent'], [class*='consent'],
                        [aria-label*='cookie' i], [aria-label*='consent' i],
                        .cc-window, .cookie, .cookies, .consent, .cmp-container, .cmp-banner,
                        [role='dialog'], .modal, [class*='backdrop'], [class*='dialog'], [class*='overlay'],
                        .MuiDialog-root, .MuiBackdrop-root,
                        .qc-cmp2-container, .qc-cmp2-ui, .qc-cmp2-summary, .qc-cmp2-footer
                        { display: none !important; visibility: hidden !important; opacity: 0 !important; pointer-events: none !important; }
                        body { overflow: auto !important; }
                      `;
                      document.documentElement.appendChild(style);
                    } catch(e) {}
                    try {
                      function tryClick(node){try{node.click(); return true;}catch(e){return false;}}
                      const texts = ['accept','agree','souhlas','souhlasím','přijmout','přijímám','ok','rozumím','zavřít','zavrit','accept all','allow all','allow','close','dismiss'];
                      // standard DOM
                      const nodes = Array.from(document.querySelectorAll('button, [role=button], input[type=button], input[type=submit]'));
                      for (let n of nodes) {
                        const t = ((n.innerText||n.value||'')+'' ).toLowerCase();
                        if (texts.some(x=>t.indexOf(x)>=0)) { if (tryClick(n)) return; }
                      }
                      // shadow DOM
                      walkShadow(document, function(root){
                        try{
                          const btns = root.querySelectorAll ? Array.from(root.querySelectorAll('button, [role=button], input[type=button], input[type=submit]')): [];
                          for (let n of btns){
                            const t = ((n.innerText||n.value||'')+'' ).toLowerCase();
                            if (texts.some(x=>t.indexOf(x)>=0)) { if (tryClick(n)) return; }
                          }
                        }catch(e){}
                      });
                      const ids=['onetrust-accept-btn-handler','didomi-notice-agree-button','acceptAllButton','accept-all'];
                      for (let id of ids){ const el=document.getElementById(id); if (el && tryClick(el)) return; }
                      // Set common consent flags
                      try{ localStorage.setItem('cookieconsent_status','allow'); }catch(e){}
                      try{ document.cookie = 'cookieconsent_status=allow; path=/; max-age=' + (3600*24*365); }catch(e){}
                    } catch(e) {}
                  })();
                ";
                try { await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(cookieAutoScript); } catch {}
                try {
                    web.CoreWebView2.FrameCreated += (sender, args) =>
                    {
                        try { args.Frame.ExecuteScriptAsync(cookieAutoScript); } catch { }
                    };
                } catch { }

                // Navigate to meteograms URL
                string latStr = lat.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
                string lonStr = lon.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
                string url = $"https://meteograms.com/#/{latStr},{lonStr},12/{durationHours}/";
                string targetHash = $"#/{latStr},{lonStr},12/{durationHours}/";
                try {
                    if (WeatherToolbar.Services.ConfigService.IsLoggingEnabled()) {
                        string dir = WeatherToolbar.Services.ConfigService.ConfigDir; System.IO.Directory.CreateDirectory(dir);
                        File.AppendAllText(System.IO.Path.Combine(dir, "app.log"), DateTime.Now.ToString("s") + $": Capture URL: {url}\n");
                    }
                } catch {}

                var tcsNav = new TaskCompletionSource<bool>();
                web.NavigationCompleted += (_, e) =>
                {
                    if (!tcsNav.Task.IsCompleted)
                        tcsNav.TrySetResult(e.IsSuccess);
                };

                web.Source = new Uri(url);

                // Pump messages while hidden to allow navigation
                form.Load += async (_, __) =>
                {
                    await Task.Delay(1800); // initial delay to allow content render
                    if (!tcsNav.Task.IsCompleted)
                    {
                        // Wait up to 8s for navigation
                        var completed = await Task.WhenAny(tcsNav.Task, Task.Delay(8000));
                    }

                    // Enforce and wait for the target hash to stick (avoid fallback to London)
                    try
                    {
                        int stable = 0;
                        for (int i = 0; i < 50 && stable < 3; i++) // up to ~20s
                        {
                            string hJson = await web.CoreWebView2.ExecuteScriptAsync("(function(){return window.location.hash||'';})()");
                            string h = string.Empty; try { h = System.Text.Json.JsonSerializer.Deserialize<string>(hJson) ?? string.Empty; } catch { }
                            if (!string.Equals(h, targetHash, StringComparison.Ordinal))
                            {
                                // Set desired hash and dispatch event
                                string js = $"(function(){{try{{ if (window.location.hash!=='{targetHash}') window.location.hash='{targetHash}'; try{{ window.dispatchEvent(new HashChangeEvent('hashchange')); }}catch(e){{}} }}catch(_ ){{}} }})();";
                                await web.CoreWebView2.ExecuteScriptAsync(js);
                                stable = 0;
                            }
                            else
                            {
                                stable++;
                            }
                            await Task.Delay(400);
                        }
                    }
                    catch { }

                    // Fast-path: try to grab meteogram image directly within ~6s total
                    try
                    {
                        bool savedFast = false;
                        for (int i = 0; i < 30 && !savedFast; i++) // up to ~9s
                        {
                            // Only proceed if current hash matches our target
                            try
                            {
                                string hJson2 = await web.CoreWebView2.ExecuteScriptAsync("(function(){return window.location.hash||'';})()");
                                string h2 = string.Empty; try { h2 = System.Text.Json.JsonSerializer.Deserialize<string>(hJson2) ?? string.Empty; } catch { }
                                if (!string.Equals(h2, targetHash, StringComparison.Ordinal))
                                {
                                    // Try to enforce again and continue waiting
                                    string js = $"(function(){{try{{ if (window.location.hash!=='{targetHash}') window.location.hash='{targetHash}'; try{{ window.dispatchEvent(new HashChangeEvent('hashchange')); }}catch(e){{}} }}catch(_ ){{}} }})();";
                                    await web.CoreWebView2.ExecuteScriptAsync(js);
                                    await Task.Delay(400);
                                    continue;
                                }
                            }
                            catch { }
                            var imgSrcJson = await web.CoreWebView2.ExecuteScriptAsync("(function(){var img=document.querySelector('#meteogram img'); return img? img.src : ''; })();");
                            string imgSrc = string.Empty;
                            try { imgSrc = System.Text.Json.JsonSerializer.Deserialize<string>(imgSrcJson) ?? string.Empty; } catch { imgSrc = string.Empty; }
                            if (!string.IsNullOrWhiteSpace(imgSrc) && (imgSrc.StartsWith("http://") || imgSrc.StartsWith("https://")))
                            {
                                // Validate that IMG corresponds to our coords (if nodeserver URL with encoded JSON)
                                bool coordsMatch = true;
                                try
                                {
                                    int idx = imgSrc.IndexOf("/getMeteogram/");
                                    if (idx > 0)
                                    {
                                        string enc = imgSrc.Substring(idx + "/getMeteogram/".Length);
                                        string json = Uri.UnescapeDataString(enc);
                                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                                        var root = doc.RootElement;
                                        double? jLat = null, jLon = null;
                                        if (root.TryGetProperty("latitude", out var latEl))
                                        {
                                            // latitude may be string
                                            if (latEl.ValueKind == System.Text.Json.JsonValueKind.String)
                                            {
                                                if (double.TryParse(latEl.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) jLat = d;
                                            }
                                            else if (latEl.ValueKind == System.Text.Json.JsonValueKind.Number && latEl.TryGetDouble(out var d2)) jLat = d2;
                                        }
                                        if (root.TryGetProperty("longitude", out var lonEl))
                                        {
                                            if (lonEl.ValueKind == System.Text.Json.JsonValueKind.String)
                                            {
                                                if (double.TryParse(lonEl.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) jLon = d;
                                            }
                                            else if (lonEl.ValueKind == System.Text.Json.JsonValueKind.Number && lonEl.TryGetDouble(out var d2)) jLon = d2;
                                        }
                                        if (jLat.HasValue && jLon.HasValue)
                                        {
                                            double tLat = double.Parse(latStr, System.Globalization.CultureInfo.InvariantCulture);
                                            double tLon = double.Parse(lonStr, System.Globalization.CultureInfo.InvariantCulture);
                                            double diff = Math.Max(Math.Abs(jLat.Value - tLat), Math.Abs(jLon.Value - tLon));
                                            coordsMatch = diff <= 0.001; // ~4.1 arcsec tolerance
                                        }
                                    }
                                }
                                catch { coordsMatch = false; }

                                if (coordsMatch)
                                {
                                    try
                                    {
                                        using var http = new System.Net.Http.HttpClient();
                                        var bytes = await http.GetByteArrayAsync(imgSrc);
                                        await System.IO.File.WriteAllBytesAsync(outPath, bytes);
                                        savedFast = bytes != null && bytes.Length > 0;
                                        if (savedFast)
                                        {
                                            try
                                            {
                                                if (WeatherToolbar.Services.ConfigService.IsLoggingEnabled()) {
                                                    string dir = WeatherToolbar.Services.ConfigService.ConfigDir; System.IO.Directory.CreateDirectory(dir);
                                                    System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "app.log"), DateTime.Now.ToString("s") + $": Meteogram saved via direct image (fast-path); url={imgSrc}\n");
                                                }
                                            }
                                            catch { }
                                            form.Close();
                                            return; // done quickly
                                        }
                                    }
                                    catch { }
                                }
                            }
                            await Task.Delay(300);
                        }
                    }
                    catch { }

                    try
                    {
                        // Repeat attempts to accept/hide overlays (incl. welcome tip) and force-set coordinates
                        string jsTry = @"
                          (function(){
                            function clickIf(el){ try{ el.click(); return true; }catch(e){ return false; } }
                            function walkShadow(root, fn){
                              try{
                                fn(root);
                                var tree = (root.querySelectorAll? Array.from(root.querySelectorAll('*')): []);
                                for (var el of tree){ if (el.shadowRoot){ walkShadow(el.shadowRoot, fn); } }
                              }catch(e){}
                            }
                            function norm(s){ try{ return (s||'').toString().toLowerCase().normalize('NFD').replace(/[\u0300-\u036f]/g,''); }catch(e){ return (s||'').toString().toLowerCase(); } }
                            var texts = ['accept','agree','souhlas','souhlasim','prijmout','prijimam','ok','rozumim','zavrit','zavrit','allow','accept all','close','dismiss'];
                            var nodes = Array.from(document.querySelectorAll('button, [role=button], input[type=button], input[type=submit]'));
                            for (var i=0;i<nodes.length;i++){
                              var t = norm(nodes[i].innerText||nodes[i].value||'');
                              for (var j=0;j<texts.length;j++){
                                if (t.indexOf(texts[j])>=0){ if(clickIf(nodes[i])) return 'clicked'; }
                              }
                            }
                            walkShadow(document, function(root){
                              try{
                                var btns = root.querySelectorAll ? Array.from(root.querySelectorAll('button, [role=button], input[type=button], input[type=submit]')): [];
                                for (var b of btns){
                                  var t=norm(b.innerText||b.value||'');
                                  for (var j=0;j<texts.length;j++){
                                    if (t.indexOf(texts[j])>=0){ if(clickIf(b)) return 'clicked-shadow'; }
                                  }
                                }
                              }catch(e){}
                            });
                            var ids = ['onetrust-accept-btn-handler','didomi-notice-agree-button','acceptAllButton','accept-all'];
                            for (var k=0;k<ids.length;k++){
                              var el = document.getElementById(ids[k]); if (el && clickIf(el)) return 'clicked';
                            }
                            try{ localStorage.setItem('cookieconsent_status','allow'); }catch(e){}
                            try{ document.cookie = 'cookieconsent_status=allow; path=/; max-age=' + (3600*24*365); }catch(e){}
                            // Force-set location from desired lat/lon (parsed from current hash)
                            try{
                              var h = window.location.hash || '';
                              var m = h.match(/#\/(.*?),(.*?),/);
                              if (m && m[1] && m[2]){
                                var lat = parseFloat(m[1]); var lon = parseFloat(m[2]);
                                var newHash = '#/' + lat.toFixed(4) + ',' + lon.toFixed(4) + ',12/DURATION_PLACEHOLDER/';
                                if (window.location.hash !== newHash){ window.location.hash = newHash; }
                                try{ window.dispatchEvent(new HashChangeEvent('hashchange')); }catch(e){}
                                try{ var map = window.map || window.MAP || window.leafletMap; if (map && map.setView) map.setView([lat,lon], map.getZoom()); }catch(e){}
                              }
                            }catch(e){}
                            // Hide welcome infobar (blue tip) if present
                            try{
                              Array.from(document.querySelectorAll('*')).forEach(function(n){
                                try{
                                  var t=norm(n.innerText||'');
                                  if (t.indexOf('welcome to meteograms.com')>=0) { n.remove(); }
                                  // Czech consent modal phrase removal
                                  if (t.indexOf('tento web zada o souhlas s pouzivanim vasich udaju')>=0){
                                     var p=n; for (var r=0;r<5;r++){ if (p && p.parentElement) p=p.parentElement; }
                                     if (p && p.remove) p.remove(); else n.remove();
                                  }
                                }catch(e){}
                              });
                            }catch(e){}
                            // force-hide overlays
                            try {
                              const style = document.createElement('style');
                              style.textContent = `
                                #onetrust-banner-sdk, #onetrust-consent-sdk, .ot-sdk-container,
                                [id*='cookie'][class], [class*='cookie'], [id*='consent'], [class*='consent'],
                                [aria-label*='cookie' i], [aria-label*='consent' i],
                                .cc-window, .cookie, .cookies, .consent, .cmp-container, .cmp-banner,
                                [role='dialog'], [aria-modal='true'], .modal, [class*='backdrop'], [class*='dialog'], [class*='overlay'],
                                .MuiDialog-root, .MuiBackdrop-root,
                                .qc-cmp2-container, .qc-cmp2-ui, .qc-cmp2-summary, .qc-cmp2-footer
                                { display: none !important; visibility: hidden !important; opacity: 0 !important; pointer-events: none !important; }
                                body { overflow: auto !important; }
                              `;
                              document.documentElement.appendChild(style);
                            } catch(e) {}
                            return 'done';
                          })();".Replace("DURATION_PLACEHOLDER", durationHours.ToString());
                        for (int i = 0; i < 18; i++)
                        {
                            await web.CoreWebView2.ExecuteScriptAsync(jsTry);
                            await Task.Delay(500);
                        }
                        // Explicitly force our exact coordinates regardless of page state
                        try
                        {
                            string jsSet = $"(function(){{" +
                                "try{{" +
                                    "var lat={latStr}; var lon={lonStr}; var dur={durationHours};" +
                                    "if (navigator && navigator.geolocation){{" +
                                        "try{{" +
                                            "navigator.geolocation.getCurrentPosition = function(succ, err){{ succ({{ coords: {{ latitude: lat, longitude: lon, accuracy: 10 }} }}); }};" +
                                            "navigator.geolocation.watchPosition = function(succ, err){{ var id=setInterval(function(){{ succ({{ coords: {{ latitude: lat, longitude: lon, accuracy: 10 }} }}); }}, 5000); return id; }};" +
                                            "navigator.geolocation.clearWatch = function(id){{ try{{ clearInterval(id); }}catch(_ ){{}} }};" +
                                        "}}catch(_ ){{}}" +
                                    "}}" +
                                    "var apply=function(){{" +
                                        "try{{ var nh = '#/'+lat.toFixed(4)+','+lon.toFixed(4)+',12/'+dur+'/'; if (window.location.hash!==nh) window.location.hash=nh; try{{ window.dispatchEvent(new HashChangeEvent('hashchange')); }}catch(e){{}} }}" +
                                        "try{{ var map=window.map||window.MAP||window.leafletMap; if(map&&map.setView) map.setView([lat,lon], map.getZoom?map.getZoom():12); }}catch(_ ){{}}" +
                                    "}};" +
                                    "apply();" +
                                    "try{{ var t=0; var id=setInterval(function(){{ apply(); t+=1; if (t>12) clearInterval(id); }}, 500); }}catch(_ ){{}}" +
                                "}}catch(_ ){{}}" +
                            "}})();";
                            await web.CoreWebView2.ExecuteScriptAsync(jsSet);
                        }
                        catch {}
                        // Final hard purge: remove obvious modal elements
                        string jsPurge = @"
                          (function(){
                            try{
                              var sels = ['#onetrust-banner-sdk','#onetrust-consent-sdk','.ot-sdk-container','.cc-window','.cmp-container','.cmp-banner','.cookie','.cookies','.consent','.modal','.MuiDialog-root','.qc-cmp2-container'];
                              sels.forEach(function(s){ document.querySelectorAll(s).forEach(function(n){ try{ n.remove(); }catch(e){} }); });
                            }catch(e){}
                          })();";
                        await web.CoreWebView2.ExecuteScriptAsync(jsPurge);
                        // Log final hash/coords for diagnostics
                        try
                        {
                            string hash = await web.CoreWebView2.ExecuteScriptAsync("(function(){return window.location.hash||'';})()");
                            if (WeatherToolbar.Services.ConfigService.IsLoggingEnabled()) {
                                string dir = WeatherToolbar.Services.ConfigService.ConfigDir; System.IO.Directory.CreateDirectory(dir);
                                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "app.log"), DateTime.Now.ToString("s") + $": Final hash: {hash}\n");
                            }
                        }
                        catch {}
                    }
                    catch { }

                    // Do not scroll: keep the default viewport where the meteogram is rendered by the site

                    // Targeted consent-hide routine: run at t=0s, ~5s, ~9.5s and capture at ~10s
                    bool consentPass = false;
                    try
                    {
                        string jsConsent = @"
                          (function(){
                            try{ localStorage.setItem('cookieconsent_status','allow'); }catch(e){}
                            try{ document.cookie = 'cookieconsent_status=allow; path=/; max-age=' + (3600*24*365); }catch(e){}
                            try{
                              // Only known CMP selectors to avoid nuking content
                              var style = document.createElement('style');
                              style.textContent = `
                                #onetrust-banner-sdk, #onetrust-consent-sdk, .ot-sdk-container,
                                .cc-window, .qc-cmp2-container, .qc-cmp2-ui,
                                [id^='sp_message_container_'], [class^='sp_'][class*='_message'],
                                [data-testid='consent-banner'], [aria-label*='consent' i],
                                /* Google Funding Choices / privacy chips */
                                .fc-consent-root, .fc-consent, .fc-dialog-overlay, .fc-dialog-container, #fc-consent-root, .fc-iframe-root,
                                [aria-label*='soukrom' i],
                                /* Hide consent iframes */
                                iframe[src*='fundingchoicesmessages.google.com'], iframe[id^='sp_message'], iframe[name^='fc-'],
                                /* Hide Google Maps corner controls/credits that overlap */
                                .gm-style .gm-style-cc, .gmnoprint,
                                /* Ensure meteogram floats on top */
                                #meteogram { z-index: 2147483647 !important; position: relative !important; }
                                { display: none !important; visibility: hidden !important; opacity: 0 !important; pointer-events: none !important; }
                              `;
                              document.documentElement.appendChild(style);
                            }catch(e){}
                            // Observe and hide newly injected consent iframes/elements
                            try{
                              var hideNode = function(node){
                                try{
                                  if (!node) return;
                                  var tag = (node.tagName||'').toLowerCase();
                                  if (tag==='iframe'){
                                    var src=(node.src||'').toLowerCase();
                                    var id=(node.id||'').toLowerCase();
                                    var name=(node.name||'').toLowerCase();
                                    if (src.indexOf('fundingchoicesmessages.google.com')>=0 || id.indexOf('sp_message')===0 || name.indexOf('fc-')===0){
                                      node.style.setProperty('display','none','important'); node.style.setProperty('visibility','hidden','important'); node.style.setProperty('opacity','0','important'); node.style.setProperty('pointer-events','none','important');
                                    }
                                  } else {
                                    var cls=(node.className||'')+''; var aria=(node.getAttribute? (node.getAttribute('aria-label')||''): '');
                                    var t=(node.innerText||'').toLowerCase();
                                    if (cls.indexOf('fc-consent')>=0 || (aria+'' ).toLowerCase().indexOf('soukrom')>=0 || t.indexOf('soukrom')>=0){
                                      node.style.setProperty('display','none','important'); node.style.setProperty('visibility','hidden','important'); node.style.setProperty('opacity','0','important'); node.style.setProperty('pointer-events','none','important');
                                    }
                                  }
                                }catch(e){}
                              };
                              var mo = new MutationObserver(function(muts){
                                try{
                                  muts.forEach(function(m){
                                    if (m.addedNodes){ m.addedNodes.forEach(function(n){ hideNode(n); if (n.querySelectorAll){ n.querySelectorAll('iframe,div,section,aside').forEach(hideNode); } }); }
                                  });
                                }catch(e){}
                              });
                              mo.observe(document.documentElement||document.body, {subtree:true, childList:true});
                            }catch(e){}
                            // Fallback: hide any fixed overlays overlapping the meteogram area
                            try{
                              function boxesOverlap(r1, r2){
                                return !(r2.left > r1.right || r2.right < r1.left || r2.top > r1.bottom || r2.bottom < r1.top);
                              }
                              function isFixed(el){
                                try{ return getComputedStyle(el).position === 'fixed'; }catch(e){ return false; }
                              }
                              function hideObstructors(){
                                try{
                                  var mg = document.querySelector('#meteogram');
                                  if(!mg) return;
                                  var mr = mg.getBoundingClientRect();
                                  if(!mr || mr.width===0 || mr.height===0) return;
                                  var nodes = Array.from(document.querySelectorAll('iframe,div,section,aside'));
                                  for (var i=0;i<nodes.length;i++){
                                    var n = nodes[i];
                                    if (!n || n===mg || mg.contains(n)) continue;
                                    var r; try{ r = n.getBoundingClientRect(); }catch(e){ r = null; }
                                    if (!r || r.width===0 || r.height===0) continue;
                                    if (!isFixed(n) && n.tagName!=='IFRAME') continue;
                                    if (!boxesOverlap(mr, r)) continue;
                                    // do not hide the map container itself
                                    if (n.id==='map' || (document.getElementById('map') && document.getElementById('map').contains(n))) continue;
                                    n.style.setProperty('display','none','important'); n.style.setProperty('visibility','hidden','important'); n.style.setProperty('opacity','0','important'); n.style.setProperty('pointer-events','none','important');
                                  }
                                }catch(e){}
                              }
                              hideObstructors();
                              if (!window.__meteogramHideTicker){ window.__meteogramHideTicker = setInterval(hideObstructors, 400); }
                            }catch(e){}
                            // Try to close via buttons if present (best-effort, non-fatal)
                            try{
                              function norm(s){try{return (s||'').toString().toLowerCase().normalize('NFD').replace(/[\u0300-\u036f]/g,'');}catch(e){return (s||'').toString().toLowerCase();}}
                              var btns = Array.from(document.querySelectorAll('button, [role=button], input[type=button], input[type=submit]'));
                              for (var i=0;i<btns.length;i++){
                                var t = norm(btns[i].innerText||btns[i].value||'');
                                if (t.indexOf('souhlas')>=0 || t.indexOf('accept')>=0 || t.indexOf('allow')>=0 || t.indexOf('odmitnout')>=0 || t.indexOf('reject')>=0){ try{btns[i].click();}catch(e){} }
                              }
                            }catch(e){}
                            // Specific: hide CZ privacy chip with known phrase
                            try{
                              function hidePhrase(root){
                                var nodes = Array.from(root.querySelectorAll('div,section,aside,article,span,p'));
                                for (var n of nodes){
                                  try{
                                    var tx = (n.innerText||'').toString().toLowerCase().normalize('NFD').replace(/[\u0300-\u036f]/g,'');
                                    if (tx.indexOf('nastaveni ochrany soukromi a souboru cookie')>=0 || tx.indexOf('nastaveni ochrany soukromi')>=0){
                                      n.style.setProperty('display','none','important');
                                      n.style.setProperty('visibility','hidden','important');
                                      n.style.setProperty('opacity','0','important');
                                      n.style.setProperty('pointer-events','none','important');
                                    }
                                  }catch(e){}
                                }
                              }
                              hidePhrase(document);
                              var ifr = Array.from(document.querySelectorAll('iframe'));
                              for (var k=0;k<ifr.length;k++){
                                try{
                                  var d = ifr[k].contentDocument || ifr[k].contentWindow && ifr[k].contentWindow.document; if (!d) continue;
                                  hidePhrase(d);
                                }catch(e){}
                              }
                            }catch(e){}
                            return !!document.querySelector('#onetrust-banner-sdk, #onetrust-consent-sdk, .ot-sdk-container, .cc-window, .qc-cmp2-container, .qc-cmp2-ui, [id^=\'sp_message_container_\'], [class^=\'sp_\'][class*=\'_message\'], [data-testid=\'consent-banner\'], [aria-label*=\'consent\' i]') ? false : true;
                          })();";
                        var r1 = await web.CoreWebView2.ExecuteScriptAsync(jsConsent);
                        await Task.Delay(12000);
                        try
                        {
                            if (WeatherToolbar.Services.ConfigService.IsLoggingEnabled()) {
                                // Dump current DOM for diagnostics at ~6s
                                var htmlJson = await web.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML");
                                string html = string.Empty;
                                try { html = System.Text.Json.JsonSerializer.Deserialize<string>(htmlJson) ?? string.Empty; } catch { html = string.Empty; }
                                if (!string.IsNullOrEmpty(html))
                                {
                                    string dir = WeatherToolbar.Services.ConfigService.ConfigDir; System.IO.Directory.CreateDirectory(dir);
                                    string dumpPath = System.IO.Path.Combine(dir, "meteograms_page_dump.html");
                                    System.IO.File.WriteAllText(dumpPath, html, System.Text.Encoding.UTF8);
                                    System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "app.log"), DateTime.Now.ToString("s") + ": Wrote DOM dump: " + dumpPath + "\n");
                                }
                            }
                        }
                        catch { }
                        var r2 = await web.CoreWebView2.ExecuteScriptAsync(jsConsent);
                        await Task.Delay(12000);
                        var r3 = await web.CoreWebView2.ExecuteScriptAsync(jsConsent);
                        consentPass = (r3 ?? "").Contains("true") || (r2 ?? "").Contains("true") || (r1 ?? "").Contains("true");
                    }
                    catch { }

                    // Prefer direct meteogram image download to avoid any overlays entirely
                    bool savedDirect = false;
                    try
                    {
                        var imgSrcJson = await web.CoreWebView2.ExecuteScriptAsync("(function(){var img=document.querySelector('#meteogram img'); return img? img.src : ''; })();");
                        string imgSrc = string.Empty;
                        try { imgSrc = System.Text.Json.JsonSerializer.Deserialize<string>(imgSrcJson) ?? string.Empty; } catch { imgSrc = string.Empty; }
                        if (!string.IsNullOrWhiteSpace(imgSrc) && (imgSrc.StartsWith("http://") || imgSrc.StartsWith("https://")))
                        {
                            using var http = new HttpClient();
                            var bytes = await http.GetByteArrayAsync(imgSrc);
                            await File.WriteAllBytesAsync(outPath, bytes);
                            savedDirect = bytes != null && bytes.Length > 0;
                            if (savedDirect)
                            {
                                try
                                {
                                    if (WeatherToolbar.Services.ConfigService.IsLoggingEnabled()) {
                                        string dir = WeatherToolbar.Services.ConfigService.ConfigDir;
                                        System.IO.Directory.CreateDirectory(dir);
                                        File.AppendAllText(System.IO.Path.Combine(dir, "app.log"), DateTime.Now.ToString("s") + $": Meteogram saved via direct image; url={imgSrc}\n");
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }

                    if (!savedDirect)
                    {
                        // Capture at ~25s total wait (fallback)
                        await Task.Delay(1000);
                        using var ms = new MemoryStream();
                        await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                        ms.Position = 0;
                        using var fs = File.Open(outPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                        await ms.CopyToAsync(fs);
                        try
                        {
                            if (WeatherToolbar.Services.ConfigService.IsLoggingEnabled()) {
                                string dir = WeatherToolbar.Services.ConfigService.ConfigDir;
                                System.IO.Directory.CreateDirectory(dir);
                                File.AppendAllText(System.IO.Path.Combine(dir, "app.log"), DateTime.Now.ToString("s") + $": Meteogram saved via simple-viewport; consent_hidden={(consentPass?"true":"false")}; wait_ms=25000\n");

                                // Diagnostic logging: probe bottom-left fixed elements (text/id/class/rect) before capture
                                string jsProbe = @"
                                  (function(){
                                    var els = document.querySelectorAll('body > *:not(script):not(style):not/template):not(noscript)');
                                    var blEls = [];
                                    els.forEach(function(el){
                                      var rect = el.getBoundingClientRect();
                                      if (rect.top < 50 && rect.left < 50 && rect.width > 50 && rect.height > 50){
                                        blEls.push({
                                          text: el.innerText || '',
                                          id: el.id || '',
                                          class: el.className || '',
                                          rect: rect
                                        });
                                      }
                                    });
                                    return blEls;
                                  })();";
                                var probeResult = await web.CoreWebView2.ExecuteScriptAsync(jsProbe);
                                File.AppendAllText(System.IO.Path.Combine(dir, "app.log"), DateTime.Now.ToString("s") + $": Bottom-left fixed elements:\n{probeResult}\n");
                            }
                        }
                        catch { }
                    }

                    form.Close();
                };

                // Show the form off-screen so WebView2 is created and can render
                form.Show();

                // Run a local message loop until form closes
                while (form.Visible)
                {
                    Application.DoEvents();
                    await Task.Delay(50);
                }

                return File.Exists(outPath);
            }
            catch (Exception ex)
            {
                try
                {
                    if (WeatherToolbar.Services.ConfigService.IsLoggingEnabled()) {
                        string dir = WeatherToolbar.Services.ConfigService.ConfigDir;
                        System.IO.Directory.CreateDirectory(dir);
                        File.AppendAllText(System.IO.Path.Combine(dir, "app.log"), DateTime.Now.ToString("s") + ": Capture failed: " + ex + Environment.NewLine);
                    }
                }
                catch { }
                return false;
            }
        }
    }
}
