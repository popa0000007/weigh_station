using WeighStation.Core.Models;

namespace WeighStation.Core.Security;

public enum Permission
{
    /// <summary>Capture or cancel a weighing.</summary>
    Weigh,
    /// <summary>Open and close the barrier by hand.</summary>
    ControlBarrier,
    ViewJournal,
    /// <summary>Correct or cancel recorded weighings.</summary>
    EditJournal,
    /// <summary>Save journal and reports to files.</summary>
    ExportData,
    ViewReports,
    ViewReferences,
    EditReferences,
    ViewDevices,
    /// <summary>Change connection parameters and use the simulator controls.</summary>
    EditDevices,
    EditSettings,
    ManageUsers,
    /// <summary>Load reference books and journal from Excel/CSV files.</summary>
    ImportData,
}

/// <summary>Who may do what. One table, so "what can a manager do?" has a single answer.</summary>
public static class RolePermissions
{
    private static readonly IReadOnlyDictionary<UserRole, IReadOnlySet<Permission>> Table = Build();

    public static bool Has(UserRole role, Permission permission) => Table[role].Contains(permission);

    public static IReadOnlySet<Permission> Of(UserRole role) => Table[role];

    public static string Name(UserRole role) => role switch
    {
        UserRole.Operator => "Оператор",
        UserRole.Manager => "Менеджер",
        UserRole.Administrator => "Администратор",
        _ => role.ToString(),
    };

    public static string Describe(UserRole role) => role switch
    {
        UserRole.Operator => "Взвешивание, управление шлагбаумом, просмотр журнала и печать талонов.",
        UserRole.Manager => "Всё, что оператор, плюс правка и отмена записей, справочники, отчёты, экспорт, просмотр устройств.",
        UserRole.Administrator => "Полный доступ: пользователи, настройки, устройства, импорт данных.",
        _ => "",
    };

    private static IReadOnlyDictionary<UserRole, IReadOnlySet<Permission>> Build()
    {
        var op = new HashSet<Permission> { Permission.Weigh, Permission.ControlBarrier, Permission.ViewJournal };
        var manager = new HashSet<Permission>(op)
        {
            Permission.EditJournal, Permission.ExportData, Permission.ViewReports,
            Permission.ViewReferences, Permission.EditReferences, Permission.ViewDevices,
        };
        var admin = new HashSet<Permission>(Enum.GetValues<Permission>());
        return new Dictionary<UserRole, IReadOnlySet<Permission>>
        {
            [UserRole.Operator] = op,
            [UserRole.Manager] = manager,
            [UserRole.Administrator] = admin,
        };
    }
}
