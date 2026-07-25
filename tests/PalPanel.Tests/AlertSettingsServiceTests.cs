using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PalPanel.Auth;
using PalPanel.Control;
using PalPanel.Data;

public class AlertSettingsServiceTests
{
    // Reversible stand-in for ISecretProtector so tests can assert the stored value is NOT the
    // plaintext yet still round-trips (mirrors DataProtection without the keyring).
    private sealed class ReversibleProtector : ISecretProtector
    {
        public string Protect(string p) => "enc:" + p;
        public string Unprotect(string c) => c.StartsWith("enc:") ? c[4..] : c;
    }

    private static IDbContextFactory<PanelDb> NewDb()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PanelDb>(b => b.UseSqlite($"Data Source={Path.GetTempFileName()}"));
        var dbf = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<PanelDb>>();
        using (var db = dbf.CreateDbContext()) db.Database.EnsureCreated();
        return dbf;
    }

    private static AlertSettingsService Make(IDbContextFactory<PanelDb> dbf, AlertOptions? seed = null) =>
        new(dbf, new ReversibleProtector(), Options.Create(seed ?? new AlertOptions()));

    [Fact]
    public async Task Save_EncryptsPassword_AndResolveDecrypts()
    {
        var dbf = NewDb();
        var svc = Make(dbf);
        await svc.SaveAsync(new AlertSettings { EmailEnabled = true, SmtpUser = "u@x.com", To = "t@x.com" }, "s3cret");

        await using (var db = await dbf.CreateDbContextAsync())
        {
            var row = db.AlertSettings.Single();
            Assert.Equal("enc:s3cret", row.SmtpPasswordEnc);   // stored encrypted, not plaintext
            Assert.DoesNotContain("s3cret", row.SmtpPasswordEnc[..4]);
        }
        var r = await svc.ResolveAsync();
        Assert.True(r.EmailEnabled);
        Assert.Equal("s3cret", r.SmtpPassword);                // decrypted for the notifier
    }

    [Fact]
    public async Task Save_StripsWhitespaceFromAppPassword()
    {
        var dbf = NewDb();
        var svc = Make(dbf);
        // Gmail displays app passwords as four space-separated groups; a copy-paste must still work.
        await svc.SaveAsync(new AlertSettings { SmtpUser = "u", To = "t" }, "abcd efgh ijkl mnop");
        var r = await svc.ResolveAsync();
        Assert.Equal("abcdefghijklmnop", r.SmtpPassword);
    }

    [Fact]
    public async Task Save_BlankPassword_KeepsExistingSecret()
    {
        var svc = Make(NewDb());
        await svc.SaveAsync(new AlertSettings { SmtpUser = "u", To = "t" }, "keepme");
        // Edit another field, leave password blank.
        await svc.SaveAsync(new AlertSettings { SmtpUser = "u", To = "new@x.com" }, "");

        var r = await svc.ResolveAsync();
        Assert.Equal("keepme", r.SmtpPassword);   // untouched
        Assert.Equal("new@x.com", r.To);          // other field updated
    }

    [Fact]
    public async Task FirstRead_SeedsFromConfig_EncryptingSeedPassword()
    {
        var seed = new AlertOptions { EmailEnabled = true, SmtpUser = "cfg@x.com", SmtpPassword = "cfgpw", To = "to@x.com" };
        var dbf = NewDb();
        var svc = Make(dbf, seed);

        var r = await svc.ResolveAsync();          // no row yet -> seeds from config
        Assert.True(r.EmailEnabled);
        Assert.Equal("cfg@x.com", r.SmtpUser);
        Assert.Equal("cfgpw", r.SmtpPassword);

        await using var db = await dbf.CreateDbContextAsync();
        Assert.Equal("enc:cfgpw", db.AlertSettings.Single().SmtpPasswordEnc);   // seed persisted encrypted
    }

    [Fact]
    public async Task Defaults_WhenNothingConfigured_EmailDisabledNoPassword()
    {
        var r = await Make(NewDb()).ResolveAsync();
        Assert.False(r.EmailEnabled);
        Assert.Equal("", r.SmtpPassword);
        Assert.Equal("smtp.gmail.com", r.SmtpHost);   // sensible default
    }
}
