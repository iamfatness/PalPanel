using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PalPanel.Data;

namespace PalPanel.Control;

// Email delivery config. Lives in the gitignored appsettings.Local.json under "Alerts" — the SMTP
// password is a secret and must never be committed. Defaults target Gmail SMTP (STARTTLS on 587);
// supply an app password (not your account password) as SmtpPassword.
public class AlertOptions
{
    public bool EmailEnabled { get; set; }
    public string SmtpHost { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = "";
    public string SmtpPassword { get; set; } = "";   // Gmail APP password, local secrets only
    public string From { get; set; } = "";            // defaults to SmtpUser when blank
    public string To { get; set; } = "";              // comma-separated recipients
}

public interface IAlertNotifier
{
    Task SendAsync(Alert alert, CancellationToken ct = default);
}

// Emails Warning+Critical alerts; Info alerts are in-panel only (no email) so recoveries and
// routine auto-restart notices don't spam the inbox. A disabled/unconfigured notifier is a silent
// no-op — alerting still populates the in-panel feed regardless.
public class SmtpAlertNotifier(IOptions<AlertOptions> options, ILogger<SmtpAlertNotifier>? log = null) : IAlertNotifier
{
    private readonly AlertOptions _o = options.Value;
    private readonly ILogger _log = log ?? NullLogger<SmtpAlertNotifier>.Instance;

    private bool Configured =>
        _o.EmailEnabled && !string.IsNullOrWhiteSpace(_o.SmtpHost) && !string.IsNullOrWhiteSpace(_o.SmtpUser)
        && !string.IsNullOrWhiteSpace(_o.SmtpPassword) && !string.IsNullOrWhiteSpace(_o.To);

    public async Task SendAsync(Alert alert, CancellationToken ct = default)
    {
        if (alert.Severity < AlertSeverity.Warning) return;   // Info = in-panel only
        if (!Configured) return;                               // email off / not set up

        var from = string.IsNullOrWhiteSpace(_o.From) ? _o.SmtpUser : _o.From;
        using var msg = new MailMessage
        {
            From = new MailAddress(from, "PalPanel"),
            Subject = $"[PalPanel] {alert.Severity.ToString().ToUpperInvariant()} — {alert.Title}",
            Body = $"{alert.Title}\n\n{alert.Detail}\n\nServer: {(string.IsNullOrEmpty(alert.ServerName) ? "host" : alert.ServerName)}\nTime: {alert.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
        };
        foreach (var to in _o.To.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            msg.To.Add(to);

        using var client = new SmtpClient(_o.SmtpHost, _o.SmtpPort)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(_o.SmtpUser, _o.SmtpPassword),
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        try { await client.SendMailAsync(msg, ct); }
        catch (Exception ex) { _log.LogError(ex, "Alert email send failed for '{Title}'", alert.Title); }
    }
}
