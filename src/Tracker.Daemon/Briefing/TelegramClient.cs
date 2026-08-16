using System.Net.Http;
using System.Text;
using System.Text.Json;
using Tracker.Shared.Logging;

namespace Tracker.Daemon.Briefing;

/// <summary>
/// Trimiterea unui mesaj către botul personal de Telegram. Singurul apel spre exterior din daemon
/// care conține date despre tine — de aceea merge doar către api.telegram.org și doar cu token-ul
/// din config.
///
/// Fără DI pentru HTTP în daemon: un HttpClient de lungă durată cu timeout explicit, ca în
/// AwClient/StorageEndpoints. Nu aruncă niciodată — un Telegram picat nu are voie să omoare
/// serviciul care îl folosește.
/// </summary>
public sealed class TelegramClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>true = livrat. Orice eșec e logat și raportat ca false, niciodată aruncat.</summary>
    public async Task<bool> SendAsync(string botToken, string chatId, string html, CancellationToken ct)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                chat_id = chatId,
                text = html,
                parse_mode = "HTML",
                disable_web_page_preview = true,
            });

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync(
                $"https://api.telegram.org/bot{botToken}/sendMessage", content, ct);

            if (res.IsSuccessStatusCode) return true;

            // Telegram pune motivul real în corp ("chat not found", "unauthorized"), nu în status
            var detail = await res.Content.ReadAsStringAsync(ct);
            Log.Warn($"Telegram send failed ({(int)res.StatusCode}): {Trim(detail)}");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Telegram send failed: " + ex.Message);
            return false;
        }
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300];

    public void Dispose() => _http.Dispose();
}
