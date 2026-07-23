using PalPanel.Auth;
using PalPanel.Data;

namespace PalPanel.Tests;

public class PasswordServiceTests
{
    static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    readonly IPasswordService _svc = new PasswordService();

    [Fact] public void Hash_Then_Verify_Roundtrips()
    { var h = _svc.Hash("Test-Passw0rd!"); Assert.NotEqual("Test-Passw0rd!", h); Assert.True(_svc.Verify(h, "Test-Passw0rd!")); Assert.False(_svc.Verify(h, "wrong")); }

    [Fact] public void Verify_NullOrEmptyHash_False()
    { Assert.False(_svc.Verify(null!, "x")); Assert.False(_svc.Verify("", "x")); }

    [Fact] public void CheckPassword_Success_ResetsCounters()
    {
        var u = new PanelUser { Email="a@b.c", PasswordHash=_svc.Hash("pw"), FailedLoginCount=3 };
        var r = _svc.CheckPassword(u, "pw", T0);
        Assert.Equal(LoginOutcome.Success, r.Outcome); Assert.Equal(0, u.FailedLoginCount); Assert.Null(u.LockedUntil);
    }

    [Fact] public void CheckPassword_FifthFailure_Locks()
    {
        var u = new PanelUser { Email="a@b.c", PasswordHash=_svc.Hash("pw"), FailedLoginCount=4 };
        var r = _svc.CheckPassword(u, "wrong", T0);
        Assert.Equal(LoginOutcome.Locked, r.Outcome); Assert.Equal(T0.AddMinutes(15), u.LockedUntil);
    }

    [Fact] public void CheckPassword_FailureBelowThreshold_IncrementsNoLock()
    {
        var u = new PanelUser { Email="a@b.c", PasswordHash=_svc.Hash("pw"), FailedLoginCount=1 };
        var r = _svc.CheckPassword(u, "wrong", T0);
        Assert.Equal(LoginOutcome.BadCredentials, r.Outcome); Assert.Equal(2, u.FailedLoginCount); Assert.Null(u.LockedUntil); Assert.True(r.MutatedUser);
    }

    [Fact] public void CheckPassword_WhileLocked_ReturnsLocked_NoMutation()
    {
        var u = new PanelUser { Email="a@b.c", PasswordHash=_svc.Hash("pw"), LockedUntil=T0.AddMinutes(5) };
        var r = _svc.CheckPassword(u, "pw", T0);
        Assert.Equal(LoginOutcome.Locked, r.Outcome); Assert.False(r.MutatedUser);
    }

    [Fact] public void CheckPassword_NoPasswordHash_ReturnsNoPassword()
    { var u = new PanelUser { Email="a@b.c", PasswordHash=null }; Assert.Equal(LoginOutcome.NoPassword, _svc.CheckPassword(u, "pw", T0).Outcome); }
}
