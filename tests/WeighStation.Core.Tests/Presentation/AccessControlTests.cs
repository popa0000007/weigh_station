using WeighStation.Core.Models;
using WeighStation.Core.Security;
using WeighStation.Presentation;
using WeighStation.Presentation.Pages;
using WeighStation.Presentation.Security;

namespace WeighStation.Core.Tests.Presentation;

public class PagePermissionTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    // ---- journal ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Operator_can_look_at_the_journal_and_print_but_not_edit_cancel_or_export()
    {
        await _app.AddWeighing();
        _app.SignInAs(UserRole.Operator);
        var vm = _app.NewJournal();
        await vm.ActivateAsync();
        vm.SelectedRow = vm.Rows[0];
        await TestApp.Eventually(() => vm.History.Count > 0, "history");

        Assert.False(vm.CanEdit);
        Assert.True(vm.PrintCommand.CanExecute(null));
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.False(vm.CancelWeighingCommand.CanExecute(null));

        vm.EditCompany = "Подмена";
        Assert.False(vm.IsDirty);
        Assert.False(vm.SaveEditCommand.CanExecute(null));
    }

    [Fact]
    public async Task Commands_refuse_even_when_called_directly_without_permission()
    {
        var w = await _app.AddWeighing(company: "Север");
        _app.SignInAs(UserRole.Manager);
        var vm = _app.NewJournal();
        await vm.ActivateAsync();
        vm.SelectedRow = vm.Rows[0];
        vm.EditCompany = "Новый";
        Assert.True(vm.SaveEditCommand.CanExecute(null));

        _app.SignInAs(UserRole.Operator); // permission removed behind the screen's back
        await vm.SaveEditCommand.ExecuteAsync(null);
        await vm.CancelWeighingCommand.ExecuteAsync(null);
        _app.Dialogs.SavePath = Path.Combine(_app.Dir, "x.csv");
        vm.ExportCommand.Execute(null);

        var stored = (await _app.RealRepo.GetAsync(w.Id))!;
        Assert.Equal("Север", stored.Counterparty);
        Assert.Equal(WeighingStatus.Completed, stored.Status);
        Assert.False(File.Exists(_app.Dialogs.SavePath));
    }

    [Fact]
    public async Task Manager_gets_edit_cancel_and_export_the_moment_they_sign_in()
    {
        await _app.AddWeighing();
        _app.SignInAs(UserRole.Operator);
        var vm = _app.NewJournal();
        await vm.ActivateAsync();
        vm.SelectedRow = vm.Rows[0];
        Assert.False(vm.CanEdit);

        _app.SignInAs(UserRole.Manager);

        Assert.True(vm.CanEdit);
        Assert.True(vm.ExportCommand.CanExecute(null));
        Assert.True(vm.CancelWeighingCommand.CanExecute(null));
    }

    [Fact]
    public async Task Edits_are_attributed_to_the_signed_in_user()
    {
        var w = await _app.AddWeighing(company: "Старый");
        _app.SignInAs(UserRole.Manager, "Петрова М.И.");
        var vm = _app.NewJournal();
        await vm.ActivateAsync();
        vm.SelectedRow = vm.Rows[0];
        vm.EditCompany = "Новый";

        await vm.SaveEditCommand.ExecuteAsync(null);

        Assert.Equal("Петрова М.И.", (await _app.RealRepo.GetHistoryAsync(w.Id))[^1].User);
    }

    // ---- references, reports, settings, devices ------------------------------------------------------------------------

    [Fact]
    public async Task References_are_read_only_without_the_edit_permission()
    {
        await _app.Companies.SaveAsync(new Company { Name = "Север" });
        _app.SignInAs(UserRole.Operator);
        var vm = _app.NewReferences();
        await vm.ActivateAsync();
        var companies = vm.Companies;
        companies.SelectedItem = companies.Items[0];

        Assert.False(companies.CanModify);
        Assert.False(companies.SaveCommand.CanExecute(null));
        Assert.False(companies.NewCommand.CanExecute(null));
        Assert.False(companies.DeleteCommand.CanExecute(null));

        companies.Name = "Взлом";
        await companies.SaveCommand.ExecuteAsync(null);
        _app.Dialogs.ConfirmAnswer = true;
        await companies.DeleteCommand.ExecuteAsync(null);

        Assert.Equal("Север", (await _app.Companies.GetAllAsync()).Single().Name);
        Assert.Equal("Недостаточно прав для изменения справочников.", companies.ErrorMessage);

        _app.SignInAs(UserRole.Manager);
        Assert.True(companies.CanModify);
        Assert.True(companies.SaveCommand.CanExecute(null));
        Assert.True(companies.DeleteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Report_export_needs_the_export_permission()
    {
        await _app.AddWeighing();
        _app.SignInAs(UserRole.Manager);
        var vm = _app.NewReports();
        await vm.ActivateAsync();
        Assert.True(vm.ExportCommand.CanExecute(null));

        _app.SignInAs(UserRole.Operator);
        Assert.False(vm.ExportCommand.CanExecute(null));
        _app.Dialogs.SavePath = Path.Combine(_app.Dir, "r.csv");
        vm.ExportCommand.Execute(null);
        Assert.False(File.Exists(_app.Dialogs.SavePath));
    }

    [Fact]
    public async Task Settings_and_backup_are_refused_for_non_administrators()
    {
        var vm = _app.NewSettings();
        vm.MinWeightKg = "1000";

        _app.SignInAs(UserRole.Manager);
        vm.SaveCommand.Execute(null);

        Assert.Equal("Недостаточно прав для изменения настроек.", vm.ErrorMessage);
        Assert.Equal(500, _app.Session.Options.MinWeightKg);

        _app.Dialogs.SavePath = Path.Combine(_app.Dir, "b.db");
        await vm.BackupCommand.ExecuteAsync(null);
        Assert.False(File.Exists(_app.Dialogs.SavePath));

        _app.SignInAs(UserRole.Administrator);
        vm.SaveCommand.Execute(null);
        Assert.Equal(1000, _app.Session.Options.MinWeightKg);
    }

    [Fact]
    public void Devices_page_connection_settings_and_simulator_switches_are_for_administrators()
    {
        _app.SignInAs(UserRole.Manager);
        using var vm = _app.NewDevices();
        Assert.False(vm.CanEditDevices);
        Assert.False(vm.SaveConnectionCommand.CanExecute(null));

        vm.SimScaleConnected = false;
        vm.SaveConnectionCommand.Execute(null);

        Assert.True(_app.Sim.ScaleConnected);
        Assert.Equal(new DeviceSettings(), _app.SettingsStore.Saved.Devices);
        Assert.Contains("Недостаточно прав", vm.ErrorMessage);

        _app.SignInAs(UserRole.Administrator);
        Assert.True(vm.CanEditDevices);
        vm.SimScaleConnected = false;
        Assert.False(_app.Sim.ScaleConnected);
    }

    [Fact]
    public void Manual_barrier_control_follows_the_permission_everywhere()
    {
        using var devices = _app.NewDevices();
        using var weighing = _app.NewWeighing();
        _app.Dialogs.ConfirmAnswer = true;

        _app.User.SignOut();
        Assert.False(devices.OpenBarrierCommand.CanExecute(null));
        devices.OpenBarrierCommand.Execute(null);
        weighing.OpenBarrierCommand.Execute(null);
        Assert.Equal(BarrierPosition.Closed, _app.Dev.Barrier);
        Assert.Equal("Недостаточно прав для этого действия.", weighing.ErrorMessage);

        _app.SignInAs(UserRole.Operator);
        weighing.OpenBarrierCommand.Execute(null);
        Assert.Equal(BarrierPosition.Open, _app.Dev.Barrier);
    }

    // ---- weighing --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Nobody_signed_in_means_nobody_can_capture()
    {
        using var vm = _app.NewWeighing();
        _app.SettledTruck();
        Assert.True(vm.CanCapture);

        _app.User.SignOut();
        Assert.False(vm.CanCapture);
        Assert.False(vm.CaptureCommand.CanExecute(null));

        await vm.CaptureCommand.ExecuteAsync(null); // a direct call must not get around the disabled button

        Assert.Empty(await _app.RealRepo.GetRecentAsync(5));
        Assert.Equal("Недостаточно прав для этого действия.", vm.ErrorMessage);
    }

    [Fact]
    public async Task Capture_is_recorded_under_the_signed_in_operator_and_follows_a_change_of_user()
    {
        using var vm = _app.NewWeighing();
        _app.SignInAs(UserRole.Operator, "Сидоров П.П.");
        _app.SettledTruck();

        _app.SignInAs(UserRole.Operator, "Кузнецова А.А.");
        await vm.CaptureCommand.ExecuteAsync(null);

        var stored = (await _app.RealRepo.GetRecentAsync(1)).Single();
        Assert.Equal("Кузнецова А.А.", stored.Operator);
        var history = await _app.RealRepo.GetHistoryAsync(stored.Id);
        Assert.All(history, h => Assert.Equal("Кузнецова А.А.", h.User));
    }

    // ---- shell -----------------------------------------------------------------------------------------------------

    private sealed class Page(string title, Permission required) : PageViewModel
    {
        public int Activations { get; private set; }
        public override string Title => title;
        public override string IconData => "M0 0";
        public override Permission Required => required;

        public override Task ActivateAsync()
        {
            Activations++;
            return Task.CompletedTask;
        }
    }

    private ShellViewModel Shell(params PageViewModel[] pages) =>
        new(pages, _app.Session, _app.Sim, _app.User, _app.Users, _app.Dialogs, _app.Time, _app.Ui);

    [Fact]
    public void Navigation_lists_only_the_pages_the_user_may_open_and_follows_sign_in()
    {
        var weigh = new Page("Взвешивание", Permission.Weigh);
        var reports = new Page("Отчёты", Permission.ViewReports);
        var settings = new Page("Настройки", Permission.EditSettings);
        _app.SignInAs(UserRole.Operator);
        using var shell = Shell(weigh, reports, settings);
        Assert.Equal(["Взвешивание"], shell.Pages.Select(p => p.Title));

        _app.SignInAs(UserRole.Manager);
        Assert.Equal(["Взвешивание", "Отчёты"], shell.Pages.Select(p => p.Title));

        _app.SignInAs(UserRole.Administrator);
        Assert.Equal(3, shell.Pages.Count);
    }

    [Fact]
    public void Selected_page_that_is_no_longer_allowed_falls_back_to_the_first_one()
    {
        var weigh = new Page("Взвешивание", Permission.Weigh);
        var settings = new Page("Настройки", Permission.EditSettings);
        using var shell = Shell(weigh, settings);
        shell.SelectedPage = settings;
        Assert.Same(settings, shell.SelectedPage);

        _app.SignInAs(UserRole.Operator);

        Assert.Same(weigh, shell.SelectedPage);
    }

    [Fact]
    public void Signed_out_shows_no_pages()
    {
        using var shell = Shell(new Page("Взвешивание", Permission.Weigh));
        _app.User.SignOut();
        Assert.Empty(shell.Pages);
        Assert.Null(shell.SelectedPage);
        Assert.Equal("Не выполнен вход", shell.UserText);
    }

    [Fact]
    public void Status_bar_shows_name_and_role()
    {
        _app.SignInAs(UserRole.Manager, "Петрова М.И.");
        using var shell = Shell(new Page("Взвешивание", Permission.Weigh));
        Assert.Equal("Петрова М.И. · Менеджер", shell.UserText);
    }

    [Fact]
    public async Task Logout_signs_out_writes_the_audit_trail_and_tells_the_application()
    {
        var admin = await _app.Users.CreateFirstAdministratorAsync("boss", "Начальник", "Weigh-Pass-1");
        _app.User.SignIn(admin);
        using var shell = Shell(new Page("Взвешивание", Permission.Weigh));
        var loggedOut = 0;
        shell.LoggedOut += (_, _) => loggedOut++;

        await shell.LogoutCommand.ExecuteAsync(null);

        Assert.False(_app.User.IsSignedIn);
        Assert.Equal(1, loggedOut);
        Assert.Contains("logout", (await _app.Audit.GetRecentAsync(10)).Select(a => a.Action));
    }

    [Fact]
    public void Change_password_command_opens_the_dialog()
    {
        using var shell = Shell(new Page("Взвешивание", Permission.Weigh));
        shell.ChangePasswordCommand.Execute(null);
        Assert.Equal(1, _app.Dialogs.PasswordDialogs);
    }

    [Fact]
    public void Every_real_page_declares_a_sensible_permission()
    {
        var pages = new PageViewModel[]
        {
            _app.NewWeighing(), _app.NewJournal(), _app.NewReferences(), _app.NewReports(), _app.NewDevices(), _app.NewSettings(),
            new UsersViewModel(_app.Users, _app.Audit, _app.User, _app.Dialogs), _app.NewImport(),
        };
        var byTitle = pages.ToDictionary(p => p.Title, p => p.Required);

        Assert.Equal(Permission.Weigh, byTitle["Взвешивание"]);
        Assert.Equal(Permission.ViewJournal, byTitle["Журнал"]);
        Assert.Equal(Permission.ViewReferences, byTitle["Справочники"]);
        Assert.Equal(Permission.ViewReports, byTitle["Отчёты"]);
        Assert.Equal(Permission.ViewDevices, byTitle["Устройства"]);
        Assert.Equal(Permission.EditSettings, byTitle["Настройки"]);
        Assert.Equal(Permission.ManageUsers, byTitle["Пользователи"]);
        Assert.Equal(Permission.ImportData, byTitle["Импорт"]);
    }
}

public class LoginViewModelTests : IDisposable
{
    private readonly TestApp _app = new();

    public LoginViewModelTests() => _app.User.SignOut();

    public void Dispose() => _app.Dispose();

    private LoginViewModel NewVm() => new(_app.Users, _app.User);

    [Fact]
    public async Task First_run_asks_to_create_the_administrator_then_signs_in()
    {
        var vm = NewVm();
        await vm.InitializeAsync();
        Assert.True(vm.IsSetup);
        Assert.Equal("Создать и войти", vm.SubmitText);

        vm.Login = "boss";
        vm.FullName = "Начальник весовой";
        vm.Password = "Weigh-Pass-1";
        vm.ConfirmPassword = "Different-Pass-1";
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.Equal("Пароль и подтверждение не совпадают.", vm.ErrorMessage);
        Assert.False(_app.User.IsSignedIn);

        var succeeded = 0;
        vm.Succeeded += (_, _) => succeeded++;
        vm.ConfirmPassword = "Weigh-Pass-1";
        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(1, succeeded);
        Assert.Equal(UserRole.Administrator, _app.User.User!.Role);
        Assert.True(await _app.Users.HasUsersAsync());
        Assert.Equal("", vm.Password);
    }

    [Fact]
    public async Task Setup_reports_validation_problems_in_plain_words()
    {
        var vm = NewVm();
        await vm.InitializeAsync();
        vm.Login = "b";
        vm.FullName = "X";
        vm.Password = vm.ConfirmPassword = "Weigh-Pass-1";
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.Contains("Логин", vm.ErrorMessage);

        vm.Login = "boss";
        vm.Password = vm.ConfirmPassword = "short";
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.Contains("не короче", vm.ErrorMessage);
        Assert.False(await _app.Users.HasUsersAsync());
    }

    private async Task<User> CreateAdminAndOperatorAsync()
    {
        var admin = await _app.Users.CreateFirstAdministratorAsync("boss", "Начальник", "Weigh-Pass-1");
        return await _app.Users.CreateAsync(admin, "ivan", "Иван Иванов", UserRole.Operator, "Temp-Pass-123");
    }

    [Fact]
    public async Task Existing_users_get_the_normal_login()
    {
        await CreateAdminAndOperatorAsync();
        var vm = NewVm();
        await vm.InitializeAsync();
        Assert.True(vm.IsSignIn);
        Assert.Equal("Войти", vm.SubmitText);
    }

    [Fact]
    public async Task Wrong_password_gives_a_generic_message_and_clears_the_password()
    {
        await CreateAdminAndOperatorAsync();
        var vm = NewVm();
        await vm.InitializeAsync();
        vm.Login = "boss";
        vm.Password = "wrong";

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("Неверный логин или пароль.", vm.ErrorMessage);
        Assert.Equal("", vm.Password);
        Assert.False(_app.User.IsSignedIn);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_and_the_message_says_until_when()
    {
        await CreateAdminAndOperatorAsync();
        var vm = NewVm();
        await vm.InitializeAsync();
        vm.Login = "boss";
        for (var i = 0; i < 5; i++)
        {
            vm.Password = "wrong";
            await vm.SubmitCommand.ExecuteAsync(null);
        }

        Assert.StartsWith("Учётная запись заблокирована до", vm.ErrorMessage);
        vm.Password = "Weigh-Pass-1";
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.StartsWith("Учётная запись заблокирована", vm.ErrorMessage);
        Assert.False(_app.User.IsSignedIn);
    }

    [Fact]
    public async Task Disabled_account_is_told_to_contact_the_administrator()
    {
        var op = await CreateAdminAndOperatorAsync();
        var admin = (await _app.UserStore.FindByLoginAsync("boss"))!.User;
        await _app.Users.UpdateAsync(admin, op.Id, op.FullName, op.Role, false);
        var vm = NewVm();
        await vm.InitializeAsync();
        vm.Login = "ivan";
        vm.Password = "Temp-Pass-123";

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("Учётная запись отключена. Обратитесь к администратору.", vm.ErrorMessage);
    }

    [Fact]
    public async Task Temporary_password_forces_a_new_one_before_entering()
    {
        await CreateAdminAndOperatorAsync();
        var vm = NewVm();
        await vm.InitializeAsync();
        vm.Login = "ivan";
        vm.Password = "Temp-Pass-123";

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.True(vm.IsChangePassword);
        Assert.False(_app.User.IsSignedIn);

        vm.NewPassword = "Brand-New-Pass-1";
        vm.ConfirmPassword = "Mismatch-Pass-1";
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.Equal("Новый пароль и подтверждение не совпадают.", vm.ErrorMessage);

        vm.ConfirmPassword = "Brand-New-Pass-1";
        var succeeded = 0;
        vm.Succeeded += (_, _) => succeeded++;
        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(1, succeeded);
        Assert.Equal("ivan", _app.User.User!.Login);
        Assert.False(_app.User.User.MustChangePassword);
        Assert.Equal(LoginStatus.Success, (await _app.Users.LoginAsync("ivan", "Brand-New-Pass-1")).Status);
    }

    [Fact]
    public async Task Forced_change_rejects_a_weak_password_and_keeps_the_user_out()
    {
        await CreateAdminAndOperatorAsync();
        var vm = NewVm();
        await vm.InitializeAsync();
        vm.Login = "ivan";
        vm.Password = "Temp-Pass-123";
        await vm.SubmitCommand.ExecuteAsync(null);

        vm.NewPassword = vm.ConfirmPassword = "123";
        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.Contains("не короче", vm.ErrorMessage);
        Assert.False(_app.User.IsSignedIn);
        Assert.True(vm.IsChangePassword);
    }
}

public class PasswordChangeViewModelTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private async Task<PasswordChangeViewModel> StartAsync()
    {
        var admin = await _app.Users.CreateFirstAdministratorAsync("boss", "Начальник", "Weigh-Pass-1");
        _app.User.SignIn(admin);
        return new PasswordChangeViewModel(_app.Users, _app.User);
    }

    [Fact]
    public async Task Changes_the_password_and_closes()
    {
        var vm = await StartAsync();
        var completed = 0;
        vm.Completed += (_, _) => completed++;
        vm.OldPassword = "Weigh-Pass-1";
        vm.NewPassword = vm.ConfirmPassword = "Fresh-Pass-22";

        await vm.ChangeCommand.ExecuteAsync(null);

        Assert.Equal(1, completed);
        Assert.Equal("", vm.ErrorMessage);
        Assert.Equal(LoginStatus.Success, (await _app.Users.LoginAsync("boss", "Fresh-Pass-22")).Status);
        Assert.Equal("Начальник", vm.UserName);
    }

    [Fact]
    public async Task Shows_why_it_did_not_work()
    {
        var vm = await StartAsync();
        vm.OldPassword = "wrong";
        vm.NewPassword = vm.ConfirmPassword = "Fresh-Pass-22";
        await vm.ChangeCommand.ExecuteAsync(null);
        Assert.Contains("Текущий пароль", vm.ErrorMessage);

        vm.OldPassword = "Weigh-Pass-1";
        vm.ConfirmPassword = "Other-Pass-22";
        await vm.ChangeCommand.ExecuteAsync(null);
        Assert.Contains("не совпадают", vm.ErrorMessage);

        vm.ConfirmPassword = vm.NewPassword = "abc";
        await vm.ChangeCommand.ExecuteAsync(null);
        Assert.Contains("не короче", vm.ErrorMessage);
    }

    [Fact]
    public async Task Refuses_when_nobody_is_signed_in()
    {
        var vm = await StartAsync();
        _app.User.SignOut();
        await vm.ChangeCommand.ExecuteAsync(null);
        Assert.Equal("Сначала войдите в программу.", vm.ErrorMessage);
    }
}

public class UsersViewModelTests : IDisposable
{
    private readonly TestApp _app = new();
    private User _admin = null!;
    private UsersViewModel _vm = null!;

    public void Dispose() => _app.Dispose();

    private async Task StartAsync()
    {
        _admin = await _app.Users.CreateFirstAdministratorAsync("boss", "Начальник", "Weigh-Pass-1");
        _app.User.SignIn(_admin);
        _vm = new UsersViewModel(_app.Users, _app.Audit, _app.User, _app.Dialogs);
        await _vm.ActivateAsync();
    }

    [Fact]
    public async Task Lists_users_with_status_and_the_security_log()
    {
        await StartAsync();

        var row = Assert.Single(_vm.Users);
        Assert.Equal("boss", row.Login);
        Assert.Equal("Администратор", row.RoleName);
        Assert.Equal("Активен", row.Status);
        Assert.Contains(_vm.SecurityLog, a => a.Action == "Создан пользователь");
    }

    [Fact]
    public async Task Creates_a_user_who_has_to_change_the_temporary_password()
    {
        await StartAsync();
        _vm.NewCommand.Execute(null);
        Assert.True(_vm.IsNew);

        _vm.Login = "ivan";
        _vm.FullName = "Иван Иванов";
        _vm.SelectedRole = _vm.RoleOptions.Single(r => r.Role == UserRole.Manager);
        _vm.Password = "Temp-Pass-123";
        await _vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains("создан", _vm.InfoMessage);
        Assert.Equal(2, _vm.Users.Count);
        Assert.Equal("ivan", _vm.SelectedUser!.Login);
        Assert.Equal("Ждёт смены пароля", _vm.SelectedUser.Status);
        Assert.Equal("Менеджер", _vm.SelectedUser.RoleName);
        Assert.False(_vm.IsNew);
        Assert.Equal("", _vm.Password);
    }

    [Fact]
    public async Task Creation_problems_are_shown_not_thrown()
    {
        await StartAsync();
        _vm.NewCommand.Execute(null);
        _vm.Login = "boss";
        _vm.FullName = "Дубль";
        _vm.Password = "Temp-Pass-123";
        await _vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("уже занят", _vm.ErrorMessage);

        _vm.Login = "new.user";
        _vm.Password = "123";
        await _vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("не короче", _vm.ErrorMessage);
        Assert.Single(_vm.Users);
    }

    [Fact]
    public async Task Editing_changes_role_and_state_and_the_last_admin_is_protected()
    {
        await StartAsync();
        _vm.SelectedUser = _vm.Users[0];
        _vm.SelectedRole = _vm.RoleOptions.Single(r => r.Role == UserRole.Operator);

        await _vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Нельзя отключить или понизить последнего администратора.", _vm.ErrorMessage);
        Assert.Equal(UserRole.Administrator, _app.User.User!.Role);

        _vm.NewCommand.Execute(null);
        _vm.Login = "ivan";
        _vm.FullName = "Иван";
        _vm.Password = "Temp-Pass-123";
        await _vm.SaveCommand.ExecuteAsync(null);
        _vm.SelectedRole = _vm.RoleOptions.Single(r => r.Role == UserRole.Manager);
        _vm.IsActive = false;
        await _vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Отключён", _vm.SelectedUser!.Status);
        Assert.Equal("Менеджер", _vm.SelectedUser.RoleName);
    }

    [Fact]
    public async Task Changing_your_own_role_takes_effect_for_the_running_session()
    {
        await StartAsync();
        _vm.NewCommand.Execute(null);
        _vm.Login = "second";
        _vm.FullName = "Второй админ";
        _vm.SelectedRole = _vm.RoleOptions.Single(r => r.Role == UserRole.Administrator);
        _vm.Password = "Temp-Pass-123";
        await _vm.SaveCommand.ExecuteAsync(null);

        _vm.SelectedUser = _vm.Users.Single(u => u.Login == "boss");
        _vm.SelectedRole = _vm.RoleOptions.Single(r => r.Role == UserRole.Manager);
        await _vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(UserRole.Manager, _app.User.User!.Role);
        Assert.False(_app.User.Can(Permission.ManageUsers));
    }

    [Fact]
    public async Task Reset_password_asks_for_confirmation_and_forces_a_change()
    {
        await StartAsync();
        var op = await _app.Users.CreateAsync(_admin, "ivan", "Иван", UserRole.Operator, "Temp-Pass-123");
        await _app.Users.ChangePasswordAsync(op.Id, "Temp-Pass-123", "Own-Pass-4567");
        await _vm.ActivateAsync();
        _vm.SelectedUser = _vm.Users.Single(u => u.Login == "ivan");
        _vm.Password = "Another-Temp-77";

        _app.Dialogs.ConfirmAnswer = false;
        await _vm.ResetPasswordCommand.ExecuteAsync(null);
        Assert.Equal(LoginStatus.Success, (await _app.Users.LoginAsync("ivan", "Own-Pass-4567")).Status);

        _app.Dialogs.ConfirmAnswer = true;
        await _vm.ResetPasswordCommand.ExecuteAsync(null);

        Assert.Equal("Временный пароль задан.", _vm.InfoMessage);
        Assert.Equal("Ждёт смены пароля", _vm.SelectedUser!.Status);
        var login = await _app.Users.LoginAsync("ivan", "Another-Temp-77");
        Assert.True(login.User!.MustChangePassword);
    }

    [Fact]
    public async Task Weak_temporary_password_is_rejected_on_reset()
    {
        await StartAsync();
        await _app.Users.CreateAsync(_admin, "ivan", "Иван", UserRole.Operator, "Temp-Pass-123");
        await _vm.ActivateAsync();
        _vm.SelectedUser = _vm.Users.Single(u => u.Login == "ivan");
        _vm.Password = "123";
        _app.Dialogs.ConfirmAnswer = true;

        await _vm.ResetPasswordCommand.ExecuteAsync(null);

        Assert.Contains("не короче", _vm.ErrorMessage);
    }

    [Fact]
    public async Task Locked_accounts_are_marked_and_can_be_unlocked()
    {
        await StartAsync();
        await _app.Users.CreateAsync(_admin, "ivan", "Иван", UserRole.Operator, "Temp-Pass-123");
        for (var i = 0; i < 5; i++)
        {
            await _app.Users.LoginAsync("ivan", "bad");
        }

        await _vm.ActivateAsync();
        _vm.SelectedUser = _vm.Users.Single(u => u.Login == "ivan");
        Assert.StartsWith("Заблокирован до", _vm.SelectedUser.Status);

        await _vm.UnlockCommand.ExecuteAsync(null);

        Assert.Equal("Учётная запись разблокирована.", _vm.InfoMessage);
        Assert.Equal("Ждёт смены пароля", _vm.SelectedUser!.Status);
    }

    [Fact]
    public async Task Reset_and_unlock_need_a_selected_user()
    {
        await StartAsync();
        Assert.False(_vm.ResetPasswordCommand.CanExecute(null));
        Assert.False(_vm.UnlockCommand.CanExecute(null));
        _vm.SelectedUser = _vm.Users[0];
        Assert.True(_vm.ResetPasswordCommand.CanExecute(null));
    }

    [Fact]
    public async Task Without_the_permission_nothing_is_loaded_or_changed()
    {
        await StartAsync();
        _app.SignInAs(UserRole.Manager);

        await _vm.ActivateAsync();
        _vm.NewCommand.Execute(null);
        _vm.Login = "sneaky";
        _vm.FullName = "Sneaky";
        _vm.Password = "Temp-Pass-123";
        await _vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Недостаточно прав для управления пользователями.", _vm.ErrorMessage);
        Assert.Single(await _app.Users.GetUsersAsync(_admin));
    }
}
