namespace RunWise.Admin.Services;

// The signed-in admin for one browser tab (one Blazor circuit). Lives only in server memory,
// so a page refresh signs out. Nothing about the session is sent to the browser.
public sealed class AdminSession
{
    public string? UserId { get; private set; }
    public string? Email { get; private set; }
    public bool IsSignedIn => UserId != null;

    public event Action? Changed;

    public void SignIn(string userId, string email)
    {
        UserId = userId;
        Email = email;
        Changed?.Invoke();
    }

    public void SignOut()
    {
        UserId = null;
        Email = null;
        Changed?.Invoke();
    }
}
