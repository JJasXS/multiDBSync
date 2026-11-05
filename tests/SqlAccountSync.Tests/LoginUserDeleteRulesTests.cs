using SqlAccountSync.Authorization;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Auth;

namespace SqlAccountSync.Tests;

public class LoginUserDeleteRulesTests
{
    [Fact]
    public void Viewer_Is_Deletable()
    {
        var user = new AppUser { UserId = 10, Email = "viewer@example.com", Role = AppRoles.Viewer };
        Assert.True(LoginUserDeleteRules.IsDeletableViewer(user));
        LoginUserDeleteRules.EnsureCanDelete(user, currentUserId: 1, currentEmail: "admin@example.com");
    }

    [Fact]
    public void Admin_Is_Not_Deletable()
    {
        var user = new AppUser { UserId = 2, Email = "admin2@example.com", Role = AppRoles.Admin };
        Assert.False(LoginUserDeleteRules.IsDeletableViewer(user));
        var ex = Assert.Throws<InvalidOperationException>(() =>
            LoginUserDeleteRules.EnsureCanDelete(user, currentUserId: 1, currentEmail: "admin@example.com"));
        Assert.Contains("VIEWER", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cannot_Delete_Self_By_Id()
    {
        var user = new AppUser { UserId = 7, Email = "viewer@example.com", Role = AppRoles.Viewer };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            LoginUserDeleteRules.EnsureCanDelete(user, currentUserId: 7, currentEmail: "other@example.com"));
        Assert.Contains("your own", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cannot_Delete_Self_By_Email()
    {
        var user = new AppUser { UserId = 7, Email = "viewer@example.com", Role = AppRoles.Viewer };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            LoginUserDeleteRules.EnsureCanDelete(user, currentUserId: 1, currentEmail: "VIEWER@example.com"));
        Assert.Contains("your own", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
