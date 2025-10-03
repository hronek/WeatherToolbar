
# WeatherToolbar

Lehká Windows aplikace do oznamovací oblasti (tray), která zobrazuje aktuální teplotu a ikonu počasí. Umí otevřít náhled meteogramu na jedno kliknutí.

## Funkce
- Zjištění polohy (IP geolokace) a reverzní geocoding pro název místa.
- Aktuální data z Open‑Meteo (bez API klíče): teplota, počasí, vítr, pocitová teplota.
- Tooltip nad ikonou obsahuje: teplotu, pocitovou teplotu, vítr (m/s + světová strana) a místo.
- Levý klik na ikonu: otevře/zavře náhled meteogramu.
  - Pokud je k dispozici cache `meteogram.png`, zobrazí ji.
  - Pokud ne, zobrazí placeholder „Načítám meteogram…“ a proběhne zachycení na pozadí.
- Periodické zachytávání meteogramu (výchozí každých 15 minut; konfigurovatelné v `config.json`).
- Kontextové menu:
  - Aktualizovat
  - Nastavit polohu…
  - Nastavit písmo… (velikost, font, obrys číslic)
  - Zobrazit symbol počasí (zap/vyp)
  - Velikost obrysu (0–3)
  - Otevřít předpověď / Otevřít radar
  - Zapnout logování (výchozí vypnuto)
  - Ukončit

## Požadavky
- Windows 10/11.
- .NET Desktop Runtime (moderní .NET, projekt je WinForms).
- WebView2 Runtime (pro zachycení meteogramu z webu). Na běžných systémech bývá již nainstalováno.

## Sestavení / Publikování
- Visual Studio 2022 nebo `dotnet` CLI.

Příklad publish (single file, win‑x64, bez self‑contained):

```powershell
dotnet publish WeatherToolbar/WeatherToolbar.csproj -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=false
```

Spuštění: po publikování spusťte vygenerovaný `.exe`. V tray se objeví ikona s teplotou.

Alternativy buildů (only‑Evergreen):


- Minimalistický framework‑dependent build (nejmenší výstup, vyžaduje .NET Desktop Runtime v OS):

  ```powershell
  dotnet publish WeatherToolbar/WeatherToolbar.csproj -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishSingleFile=false -p:SelfContained=false           
  ```

  Požadavky: v OS musí být nainstalován kompatibilní .NET Desktop Runtime a WebView2 Evergreen Runtime.

## Konfigurace
Soubor `config.json` se ukládá do:
`%LOCALAPPDATA%\WeatherToolbar\config.json`

Klíčové položky:
- `Latitude`, `Longitude`: souřadnice (volitelné). Pokud chybí, použije se IP geolokace.
- `City`, `Country`: volitelný popisek místa.
- `RefreshMinutes`: interval obnovy (výchozí 1).
- `FontFamily`, `FontSize`, `OutlineRadius`, `ShowGlyph`: vzhled ikonky.
- `EnableLogging`: zap/vyp logování (výchozí `false`). Lze přepínat i v menu.

## Cesty a soubory
- Cache meteogramu: `%LOCALAPPDATA%\WeatherToolbar\meteogram.png`
- Log (pokud je zapnut): `%LOCALAPPDATA%\WeatherToolbar\app.log`
- Diagnostické HTML dumpy (pokud je zapnut log): `%LOCALAPPDATA%\WeatherToolbar\meteograms_page_dump.html`

## Jak to funguje
- Ikona a tooltip: data z Open‑Meteo (`current`), vítr v m/s, směr na 8‑bodové růžici.
- Náhled meteogramu: preferuje přímé stažení obrázku meteogramu z webu; při neúspěchu použije fallback (snímek pomocí WebView2). Aplikace se snaží skrýt cookie bannery a překryvy jen cíleně.

## Odinstalace / ukončení
- Aplikace neběží jako služba ani se nespouští po startu systému. Pro zavření použijte pravé tlačítko na ikoně a `Ukončit`.

## Poznámky
- Open‑Meteo nemá API klíč; prosím respektujte jejich limity.
- WebView2 je vyžadováno pouze pro zachycení meteogramu.
