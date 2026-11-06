using SqlAccountSync.Helpers;

namespace SqlAccountSync.Tests;

public class SeatReleaseRulesTests
{
    [Fact]
    public void Attached_user_window_is_logout_only_never_login_home()
    {
        Assert.Equal(
            SeatReleaseRules.Action.LogoutOnly,
            SeatReleaseRules.Decide(attachedToUserWindow: true, logoutAfterUse: true, releaseAfterUse: true));
        Assert.Equal(
            SeatReleaseRules.Action.LogoutOnly,
            SeatReleaseRules.Decide(attachedToUserWindow: true, logoutAfterUse: true, releaseAfterUse: false));
        Assert.Equal(
            SeatReleaseRules.Action.LogoutOnly,
            SeatReleaseRules.Decide(attachedToUserWindow: true, logoutAfterUse: false, releaseAfterUse: true));
    }

    [Fact]
    public void Attached_with_both_flags_off_is_none()
    {
        Assert.Equal(
            SeatReleaseRules.Action.None,
            SeatReleaseRules.Decide(attachedToUserWindow: true, logoutAfterUse: false, releaseAfterUse: false));
    }

    [Fact]
    public void Sync_owned_window_logout_and_close_when_both_enabled()
    {
        Assert.Equal(
            SeatReleaseRules.Action.LogoutAndClose,
            SeatReleaseRules.Decide(attachedToUserWindow: false, logoutAfterUse: true, releaseAfterUse: true));
    }

    [Fact]
    public void Sync_owned_logout_only_when_release_disabled()
    {
        Assert.Equal(
            SeatReleaseRules.Action.LogoutOnly,
            SeatReleaseRules.Decide(attachedToUserWindow: false, logoutAfterUse: true, releaseAfterUse: false));
    }

    [Fact]
    public void Sync_owned_close_when_logout_disabled_but_release_enabled()
    {
        Assert.Equal(
            SeatReleaseRules.Action.LogoutAndClose,
            SeatReleaseRules.Decide(attachedToUserWindow: false, logoutAfterUse: false, releaseAfterUse: true));
    }

    [Fact]
    public void Sync_owned_none_when_both_disabled()
    {
        Assert.Equal(
            SeatReleaseRules.Action.None,
            SeatReleaseRules.Decide(attachedToUserWindow: false, logoutAfterUse: false, releaseAfterUse: false));
    }
}
