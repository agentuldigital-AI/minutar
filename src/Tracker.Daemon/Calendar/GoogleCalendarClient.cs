using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tracker.Shared.Config;
using Tracker.Shared.Logging;

namespace Tracker.Daemon.Calendar;

/// <summary>
/// Citirea calendarului printr-un cont de serviciu, fără niciun pachet extern.
///
/// De ce nu Google.Apis.Calendar.v3: ar fi primul pachet NuGet extern din daemon și ar trage
/// după el un lanț întreg de dependențe, într-un proiect care azi are unul singur. Tot ce face
/// autentificarea unui cont de serviciu e un JWT semnat RS256 schimbat pe un token — vreo
/// patruzeci de linii cu <c>RSA</c> și <c>HttpClient</c> din .NET, fără nimic de actualizat.
///
/// Ca <see cref="TelegramClient"/>: nu aruncă niciodată în afara anulării. Un calendar picat
/// n-are voie să oprească briefingul — mesajul pleacă fără agendă, și scrie asta în log.
///
/// Confidențialitate: în log ajung NUMERE, niciodată titluri. Din linkurile de apel se
/// păstrează doar gazda, iar orice adresă de email dintr-un mesaj de eroare e ștearsă înainte
/// de scriere — un 404 de la Google conține adresa calendarului cerut.
/// </summary>
public sealed class GoogleCalendarClient : IDisposable
{
    private const string Scope = "https://www.googleapis.com/auth/calendar.readonly";
    private const int MaxResults = 250;

    /// <summary>Marjă înainte de expirare: un token care moare în zbor ar rata briefingul.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    private static readonly Regex EmailRx =
        new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);

    private readonly ConfigProvider _config;

    // Timeout-ul real vine din config la fiecare cerere (poate fi schimbat la cald), deci cel de
    // pe HttpClient e doar o plasă de siguranță — se fixează o dată, la prima cerere.
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(1) };

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpires;
    private string _tokenForKey = "";

    public GoogleCalendarClient(ConfigProvider config) => _config = config;

    /// <summary>Configurat = pornit, cu cheie existentă pe disc și cu un calendar de citit.</summary>
    public bool Configured()
    {
        var c = _config.Current.Calendar;
        return c.Enabled && KeyPath(c).Length > 0 && c.CalendarId.Trim().Length > 0;
    }

    private static string KeyPath(CalendarConfig c)
    {
        var p = c.KeyFile.Trim();
        return p.Length == 0 ? "" : Environment.ExpandEnvironmentVariables(p);
    }

    /// <summary>
    /// Evenimentele din interval, deja împărțite pe feluri. <c>null</c> = n-am putut citi
    /// (lipsește cheia, a picat rețeaua, a refuzat Google) — deosebit de lista goală, care
    /// înseamnă „am citit, n-ai nimic".
    /// </summary>
    public async Task<List<CalendarEvent>?> ListAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var cfg = _config.Current.Calendar;
        if (!Configured()) return null;

        try
        {
            var token = await TokenAsync(cfg, ct);
            if (token is null) return null;

            var url = "https://www.googleapis.com/calendar/v3/calendars/"
                      + Uri.EscapeDataString(cfg.CalendarId.Trim()) + "/events"
                      + "?timeMin=" + Uri.EscapeDataString(from.ToString("o"))
                      + "&timeMax=" + Uri.EscapeDataString(to.ToString("o"))
                      // singleEvents desface seriile recurente în apariții concrete; fără el,
                      // un eveniment săptămânal ar veni ca o singură regulă, nu ca ore reale
                      + "&singleEvents=true&orderBy=startTime&maxResults=" + MaxResults;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var timeout = Linked(cfg, ct);
            using var res = await _http.SendAsync(req, timeout.Token);
            var body = await res.Content.ReadAsStringAsync(timeout.Token);

            if (!res.IsSuccessStatusCode)
            {
                Log.Warn($"Calendar: citirea a eșuat ({(int)res.StatusCode}) {Scrub(body).Trim()[..Math.Min(200, Scrub(body).Trim().Length)]}");
                return null;
            }

            var list = Parse(body, cfg, out var truncated);
            Log.Info($"Calendar: {list.Count} evenimente citite" + (truncated ? " (lista e tăiată la limită)" : ""));
            return list;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Calendar: citirea a eșuat: " + Scrub(ex.Message));
            return null;
        }
    }

    // ------------------------------------------------------------------ parsare

    private static List<CalendarEvent> Parse(string body, CalendarConfig cfg, out bool truncated)
    {
        var outp = new List<CalendarEvent>();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        truncated = root.TryGetProperty("nextPageToken", out var np) && np.ValueKind == JsonValueKind.String;

        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return outp;

        foreach (var e in items.EnumerateArray())
        {
            if (string.Equals(Str(e, "status"), "cancelled", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryTime(e, "start", out var start, out var allDay)) continue;
            if (!TryTime(e, "end", out var end, out _)) continue;

            var hasConference = Str(e, "hangoutLink").Length > 0
                                || (e.TryGetProperty("conferenceData", out var cd)
                                    && cd.ValueKind == JsonValueKind.Object);

            var others = 0;
            if (e.TryGetProperty("attendees", out var at) && at.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in at.EnumerateArray())
                {
                    var self = a.TryGetProperty("self", out var s) && s.ValueKind == JsonValueKind.True;
                    var resource = a.TryGetProperty("resource", out var r) && r.ValueKind == JsonValueKind.True;
                    if (!self && !resource) others++;
                }
            }

            // Clasificarea se face pe titlul BRUT (mai mult semnal), dar se păstrează cel curățat:
            // din obiectul ăsta încolo, titlul e singurul lucru din calendar care mai există.
            var raw = Str(e, "summary");
            var kind = CalendarClassifier.Classify(raw, Str(e, "location"), hasConference, others, cfg);
            var title = CalendarClassifier.CleanTitle(raw);
            outp.Add(new CalendarEvent(
                title.Length > 0 ? title : "(fără titlu)", start, end, kind, allDay,
                Str(e, "id"), Str(e, "recurringEventId").Length > 0));
        }

        return outp;
    }

    /// <summary>
    /// „start"/„end" vin fie ca <c>dateTime</c> cu fus, fie ca <c>date</c> pentru evenimentele
    /// pe zi întreagă. Al doilea caz n-are oră, deci se așază la miezul nopții local.
    /// </summary>
    private static bool TryTime(JsonElement ev, string prop, out DateTimeOffset value, out bool allDay)
    {
        value = default;
        allDay = false;
        if (!ev.TryGetProperty(prop, out var node) || node.ValueKind != JsonValueKind.Object) return false;

        var dt = Str(node, "dateTime");
        if (dt.Length > 0 && DateTimeOffset.TryParse(dt, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out value))
            return true;

        var d = Str(node, "date");
        if (d.Length > 0 && DateOnly.TryParse(d, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var day))
        {
            allDay = true;
            value = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(
                day.ToDateTime(TimeOnly.MinValue)));
            return true;
        }
        return false;
    }

    // ----------------------------------------------------------- autentificare

    /// <summary>
    /// Tokenul contului de serviciu, ținut cât e valabil. Se ia semnând un JWT cu cheia privată
    /// din fișierul JSON și schimbându-l la Google — asta e tot ce face „autentificarea cu cont
    /// de serviciu", fără browser și fără nimic de reîmprospătat între sesiuni.
    /// </summary>
    private async Task<string?> TokenAsync(CalendarConfig cfg, CancellationToken ct)
    {
        var path = KeyPath(cfg);
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is not null && _tokenForKey == path && DateTimeOffset.UtcNow + Margin < _tokenExpires)
                return _token;

            if (!File.Exists(path))
            {
                Log.Warn("Calendar: fișierul cu cheia nu există la calea din calendar.key_file — sar peste.");
                return null;
            }

            string clientEmail, privateKey, tokenUri;
            using (var key = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct)))
            {
                clientEmail = Str(key.RootElement, "client_email");
                privateKey = Str(key.RootElement, "private_key");
                tokenUri = Str(key.RootElement, "token_uri");
                if (tokenUri.Length == 0) tokenUri = "https://oauth2.googleapis.com/token";
            }

            if (clientEmail.Length == 0 || privateKey.Length == 0)
            {
                Log.Warn("Calendar: fișierul cu cheia nu pare un cont de serviciu (lipsesc client_email / private_key).");
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var header = B64(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
            var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["iss"] = clientEmail,
                ["scope"] = Scope,
                ["aud"] = tokenUri,
                ["iat"] = now.ToUnixTimeSeconds(),
                ["exp"] = now.AddHours(1).ToUnixTimeSeconds(),
            }));

            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKey);
            var signature = B64(rsa.SignData(
                Encoding.ASCII.GetBytes($"{header}.{claims}"),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

            using var timeout = Linked(cfg, ct);
            using var res = await _http.PostAsync(tokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = $"{header}.{claims}.{signature}",
            }), timeout.Token);

            var body = await res.Content.ReadAsStringAsync(timeout.Token);
            if (!res.IsSuccessStatusCode)
            {
                Log.Warn($"Calendar: Google a refuzat cheia ({(int)res.StatusCode}) {Scrub(body).Trim()[..Math.Min(200, Scrub(body).Trim().Length)]}");
                return null;
            }

            using var tok = JsonDocument.Parse(body);
            var access = Str(tok.RootElement, "access_token");
            if (access.Length == 0) return null;

            var lifetime = tok.RootElement.TryGetProperty("expires_in", out var ex)
                           && ex.ValueKind == JsonValueKind.Number ? ex.GetInt32() : 3600;
            _token = access;
            _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(lifetime);
            _tokenForKey = path;
            return access;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Calendar: autentificarea a eșuat: " + Scrub(ex.Message));
            return null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // ------------------------------------------------------------------- mărunt

    private CancellationTokenSource Linked(CalendarConfig cfg, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, cfg.TimeoutSeconds)));
        return cts;
    }

    /// <summary>base64url: fără umplutură, cu „+/" înlocuite — cum cere JWT-ul.</summary>
    private static string B64(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Un 404 de la Google conține adresa calendarului cerut. În log n-are ce căuta.</summary>
    private static string Scrub(string s) => EmailRx.Replace(s, "<adresă>");

    private static string Str(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";

    public void Dispose()
    {
        _http.Dispose();
        _tokenLock.Dispose();
    }
}
