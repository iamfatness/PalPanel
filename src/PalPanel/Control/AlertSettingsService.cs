using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PalPanel.Auth;
using PalPanel.Data;

namespace PalPanel.Control;

// Panel email-delivery settings, DB-backed and UI-managed. The SMTP password is stored encrypted
// via ISecretProtector (same as server admin passwords). On first read the single row is seeded
// from the legacy "Alerts" config section (appsettings.Local.json) so an existing file-based setup
// keeps working — after that the DB is the source of truth and the file can be removed.
public class AlertSettingsService(
    IDbContextFactory<PanelDb> dbf, ISecretProtector protector, IOptions<AlertOptions> seed)
{
    // Plaintext view for the notifier and the settings form (password decrypted).
    public record Resolved(bool EmailEnabled, string SmtpHost, int SmtpPort, string SmtpUser,
        string SmtpPassword, string From, string To)
    {
        public bool HasPassword => !string.IsNullOrEmpty(SmtpPassword);
    }

    public async Task<AlertSettings> GetRowAsync(CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var row = await db.AlertSettings.FirstOrDefaultAsync(ct);
        if (row is not null) return row;

        // Seed once from config (encrypting any file-provided password), so the row exists to edit.
        var s = seed.Value;
        row = new AlertSettings
        {
            EmailEnabled = s.EmailEnabled,
            SmtpHost = string.IsNullOrWhiteSpace(s.SmtpHost) ? "smtp.gmail.com" : s.SmtpHost,
            SmtpPort = s.SmtpPort == 0 ? 587 : s.SmtpPort,
            SmtpUser = s.SmtpUser,
            SmtpPasswordEnc = string.IsNullOrEmpty(s.SmtpPassword) ? "" : protector.Protect(s.SmtpPassword),
            From = s.From,
            To = s.To,
        };
        db.AlertSettings.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<Resolved> ResolveAsync(CancellationToken ct = default)
    {
        var r = await GetRowAsync(ct);
        var pw = string.IsNullOrEmpty(r.SmtpPasswordEnc) ? "" : SafeUnprotect(r.SmtpPasswordEnc);
        return new Resolved(r.EmailEnabled, r.SmtpHost, r.SmtpPort, r.SmtpUser, pw, r.From, r.To);
    }

    // Save from the form. A blank newPassword KEEPS the existing encrypted secret (the form never
    // round-trips the plaintext), so an operator editing other fields doesn't wipe the password.
    public async Task SaveAsync(AlertSettings form, string? newPassword, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var row = await db.AlertSettings.FirstOrDefaultAsync(ct);
        if (row is null) { row = new AlertSettings(); db.AlertSettings.Add(row); }

        row.EmailEnabled = form.EmailEnabled;
        row.SmtpHost = string.IsNullOrWhiteSpace(form.SmtpHost) ? "smtp.gmail.com" : form.SmtpHost.Trim();
        row.SmtpPort = form.SmtpPort <= 0 ? 587 : form.SmtpPort;
        row.SmtpUser = form.SmtpUser.Trim();
        row.From = form.From.Trim();
        row.To = form.To.Trim();
        if (!string.IsNullOrWhiteSpace(newPassword))
            // Gmail app passwords are shown as four space-separated groups ("abcd efgh ijkl mnop"),
            // but SMTP AUTH needs the literal 16 chars — strip whitespace so a copy-paste with the
            // display spacing still authenticates instead of failing with 5.7.0.
            row.SmtpPasswordEnc = protector.Protect(new string(newPassword.Where(c => !char.IsWhiteSpace(c)).ToArray()));
        await db.SaveChangesAsync(ct);
    }

    private string SafeUnprotect(string enc)
    {
        // A keyring change (rare) would make old ciphertext undecryptable; treat as "no password"
        // rather than throwing into the notifier/UI.
        try { return protector.Unprotect(enc); } catch { return ""; }
    }
}
