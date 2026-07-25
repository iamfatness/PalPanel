using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PalPanel.Data;

namespace PalPanel.Control;

// Seed values for email delivery, bound from the optional "Alerts" config section. Used ONLY to
// seed the DB row the first time (see AlertSettingsService); after that, settings are edited in the
// UI and stored (password encrypted) in the DB. Keeping a config seed means an existing file-based
// setup keeps working across the upgrade.
public class AlertOptions
{
    public bool EmailEnabled { get; set; }
    public string SmtpHost { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public interface IAlertNotifier
{
    Task SendAsync(Alert alert, CancellationToken ct = default);
    // Sends a test message and THROWS on failure so the settings UI can show why. Throws
    // InvalidOperationException when email is disabled/unconfigured.
    Task SendTestAsync(CancellationToken ct = default);
}

// Emails Warning+Critical alerts (Info is in-panel only, so recoveries/routine notices don't spam);
// reads live settings from the DB each send, so UI changes apply without a restart. A disabled or
// unconfigured setup is a silent no-op for real alerts — the in-panel feed is unaffected.
public class SmtpAlertNotifier(AlertSettingsService settings, ILogger<SmtpAlertNotifier>? log = null) : IAlertNotifier
{
    private readonly ILogger _log = log ?? NullLogger<SmtpAlertNotifier>.Instance;

    public async Task SendAsync(Alert alert, CancellationToken ct = default)
    {
        if (alert.Severity < AlertSeverity.Warning) return;   // Info = in-panel only
        var cfg = await settings.ResolveAsync(ct);
        if (!Configured(cfg)) return;                          // email off / not set up
        try { await SendMessageAsync(cfg, Subject(alert), Body(alert), ct); }
        catch (Exception ex) { _log.LogError(ex, "Alert email send failed for '{Title}'", alert.Title); }
    }

    public async Task SendTestAsync(CancellationToken ct = default)
    {
        var cfg = await settings.ResolveAsync(ct);
        if (!Configured(cfg))
            throw new InvalidOperationException("Email is disabled or incomplete — enable it and fill in SMTP user, password, and recipient first.");
        await SendMessageAsync(cfg,
            "[PalPanel] Test alert",
            "This is a test from PalPanel. If you got this, crash and health alerts will reach you.", ct);
    }

    private static bool Configured(AlertSettingsService.Resolved c) =>
        c.EmailEnabled && !string.IsNullOrWhiteSpace(c.SmtpHost) && !string.IsNullOrWhiteSpace(c.SmtpUser)
        && !string.IsNullOrWhiteSpace(c.SmtpPassword) && !string.IsNullOrWhiteSpace(c.To);

    private static string Subject(Alert a) => $"[PalPanel] {a.Severity.ToString().ToUpperInvariant()} — {a.Title}";
    private static string Body(Alert a) =>
        $"{a.Title}\n\n{a.Detail}\n\nServer: {(string.IsNullOrEmpty(a.ServerName) ? "host" : a.ServerName)}\nTime: {a.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";

    private static async Task SendMessageAsync(AlertSettingsService.Resolved c, string subject, string body, CancellationToken ct)
    {
        var from = string.IsNullOrWhiteSpace(c.From) ? c.SmtpUser : c.From;
        using var msg = new MailMessage { From = new MailAddress(from, "PalPanel"), Subject = subject, Body = body };
        foreach (var to in c.To.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            msg.To.Add(to);
        using var client = new SmtpClient(c.SmtpHost, c.SmtpPort)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(c.SmtpUser, c.SmtpPassword),
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        await client.SendMailAsync(msg, ct);
    }
}
