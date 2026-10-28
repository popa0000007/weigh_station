using WeighStation.Core.Models;

namespace WeighStation.Core.Security;

/// <summary>The signed-in user of this running application. Screens ask it what is allowed.</summary>
public sealed class CurrentUser
{
    private readonly object _gate = new();
    private User? _user;

    public User? User
    {
        get { lock (_gate) { return _user; } }
    }

    public bool IsSignedIn => User is not null;

    /// <summary>Name for histories and the status bar; "—" when nobody is signed in.</summary>
    public string DisplayName => User?.DisplayName ?? "—";

    public event EventHandler? Changed;

    public bool Can(Permission permission) => User is { } u && RolePermissions.Has(u.Role, permission);

    public void SignIn(User user)
    {
        lock (_gate)
        {
            _user = user;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SignOut()
    {
        lock (_gate)
        {
            _user = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
