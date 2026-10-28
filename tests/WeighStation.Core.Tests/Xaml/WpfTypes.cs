using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace WeighStation.Core.Tests.Xaml;

/// <summary>
/// A read-only view of WPF's API, built from the reference assemblies with MetadataLoadContext, so XAML files can be
/// checked against real control and property definitions on any OS (no Windows, no WPF runtime).
/// </summary>
internal sealed class WpfTypes : IDisposable
{
    // The default xmlns is a union of these CLR namespaces.
    private static readonly string[] PresentationNamespaces =
    [
        "System.Windows", "System.Windows.Controls", "System.Windows.Controls.Primitives", "System.Windows.Documents",
        "System.Windows.Shapes", "System.Windows.Media", "System.Windows.Media.Animation", "System.Windows.Media.Effects",
        "System.Windows.Data", "System.Windows.Input", "System.Windows.Navigation", "System.Windows.Automation",
    ];

    private readonly MetadataLoadContext _context;
    private readonly Assembly[] _assemblies;

    public WpfTypes()
    {
        var wpfPath = typeof(WpfTypes).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "WpfReferencePath").Value!;
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var paths = Directory.GetFiles(runtimeDir, "*.dll").Concat(Directory.GetFiles(wpfPath, "*.dll"));
        _context = new MetadataLoadContext(new PathAssemblyResolver(paths), typeof(object).Assembly.GetName().Name);
        _assemblies = new[] { "PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml" }
            .Select(n => _context.LoadFromAssemblyPath(Path.Combine(wpfPath, n + ".dll"))).ToArray();
    }

    public void Dispose() => _context.Dispose();

    /// <summary>Finds a control/element type by its XAML name in the default presentation namespace.</summary>
    public Type? Find(string name)
    {
        foreach (var ns in PresentationNamespaces)
        {
            foreach (var assembly in _assemblies)
            {
                if (assembly.GetType($"{ns}.{name}") is { } type)
                {
                    return type;
                }
            }
        }

        return null;
    }

    public Type? Core(string fullName) => _context.CoreAssembly!.GetType(fullName);

    /// <summary>The CLR type of a property, or of an attached property ("Grid.Row"). Null if no such property.</summary>
    public Type? PropertyType(Type owner, string property)
    {
        var dot = property.IndexOf('.');
        if (dot > 0)
        {
            var host = Find(property[..dot]);
            var name = property[(dot + 1)..];
            if (host is null)
            {
                return null;
            }

            var setter = Static(host, "Set" + name);
            return setter is { } m && m.GetParameters().Length == 2 ? m.GetParameters()[1].ParameterType : Getter(host, name);
        }

        for (var t = owner; t is not null; t = t.BaseType)
        {
            var p = t.GetProperty(property, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (p is not null)
            {
                return p.PropertyType;
            }
        }

        return null;
    }

    public bool HasEvent(Type owner, string name)
    {
        for (var t = owner; t is not null; t = t.BaseType)
        {
            if (t.GetEvent(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly) is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static MethodInfo? Static(Type host, string name) =>
        host.GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
        ?? host.BaseType?.GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

    private static Type? Getter(Type host, string name) => Static(host, "Get" + name)?.ReturnType;

    /// <summary>Returns an error text if the literal can't possibly convert to the property type, otherwise null.</summary>
    public string? CheckLiteral(Type type, string value)
    {
        value = value.Trim();
        var underlying = type.IsGenericType && type.GetGenericTypeDefinition().FullName == "System.Nullable`1"
            ? type.GetGenericArguments()[0]
            : type;

        if (underlying.IsEnum)
        {
            // XAML enum conversion is case-insensitive and accepts comma-separated flags.
            var names = Enum.GetNames(underlying);
            var bad = value.Split(',').Select(s => s.Trim()).FirstOrDefault(s => !names.Contains(s, StringComparer.OrdinalIgnoreCase));
            return bad is null ? null : $"'{bad}' is not a member of {underlying.Name} ({string.Join(", ", names)})";
        }

        switch (underlying.FullName)
        {
            case "System.Boolean":
                return value is "True" or "False" or "true" or "false" ? null : $"'{value}' is not a boolean";
            case "System.Int32" or "System.Int64" or "System.Int16" or "System.Byte":
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? null : $"'{value}' is not an integer";
            case "System.Double" or "System.Single":
                return value is "Auto" or "NaN" || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                    ? null
                    : $"'{value}' is not a number";
            case "System.Windows.Thickness" or "System.Windows.CornerRadius":
                return Regex.IsMatch(value, @"^\s*-?[\d.]+(\s*,\s*-?[\d.]+){0,3}\s*$") || Regex.IsMatch(value, @"^-?[\d.]+(\s+-?[\d.]+){0,3}$")
                    ? null
                    : $"'{value}' is not a {underlying.Name}";
            case "System.Windows.GridLength":
                return Regex.IsMatch(value, @"^(Auto|\*|[\d.]+\*?)$") ? null : $"'{value}' is not a GridLength";
            case "System.Windows.Media.Brush" or "System.Windows.Media.SolidColorBrush" or "System.Windows.Media.Color":
                return IsColor(value) ? null : $"'{value}' is not a colour";
            case "System.Windows.FontWeight":
                return FontWeights.Contains(value, StringComparer.OrdinalIgnoreCase) ? null : $"'{value}' is not a FontWeight";
            case "System.Windows.Media.Geometry":
                return Regex.IsMatch(value, @"^[MmLlHhVvCcSsQqTtAaZzFf0-9eE\s,.\-+]+$") ? null : $"'{value}' is not path data";
            default:
                return null;
        }
    }

    private static readonly string[] FontWeights =
        ["Thin", "ExtraLight", "UltraLight", "Light", "Normal", "Regular", "Medium", "DemiBold", "SemiBold", "Bold", "ExtraBold", "UltraBold", "Black", "Heavy", "ExtraBlack", "UltraBlack"];

    private static readonly string[] NamedColors =
    [
        "Transparent", "White", "Black", "Red", "Green", "Blue", "Gray", "DimGray", "LightGray", "Orange", "Yellow", "Navy",
        "DarkRed", "DarkGreen", "SteelBlue", "Silver", "Gold", "Brown", "Purple", "Pink", "Cyan", "Magenta", "WhiteSmoke", "Gainsboro",
    ];

    private static bool IsColor(string value) =>
        Regex.IsMatch(value, "^#([0-9A-Fa-f]{3,4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")
        || NamedColors.Contains(value, StringComparer.OrdinalIgnoreCase);
}
