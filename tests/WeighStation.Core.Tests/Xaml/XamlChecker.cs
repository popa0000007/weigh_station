using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using WeighStation.Presentation;

namespace WeighStation.Core.Tests.Xaml;

internal sealed record Finding(string File, int Line, string Message)
{
    public override string ToString() => $"{File}:{Line}: {Message}";
}

/// <summary>
/// Static analysis for the things WPF only reports at runtime (or never): a typo in a binding path is silently ignored,
/// a missing StaticResource key throws when the view opens, a bad enum value throws at load.
/// Checked here: control and property names, literal values by property type, resource keys, binding paths against the
/// real view-model classes, DisplayMemberPath, TemplateBinding and Setter/Trigger properties.
/// </summary>
internal sealed partial class XamlChecker(WpfTypes wpf)
{
    private const string PresentationNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly Assembly ViewModelAssembly = typeof(ShellViewModel).Assembly;

    private static readonly string[] TemplatePropertyNames =
        ["Columns", "ItemTemplate", "CellTemplate", "ContentTemplate", "ItemContainerStyle", "RowStyle", "CellStyle", "HeaderTemplate"];

    private readonly List<Finding> _findings = [];
    private HashSet<string> _resourceKeys = [];
    private string _file = "";

    private sealed record Context(Type? DataType, Type? ItemType, Type? TargetType, XElement? TemplateRoot);

    public IReadOnlyList<Finding> Check(IReadOnlyList<(string Name, XDocument Document)> files)
    {
        _findings.Clear();
        _resourceKeys = files.SelectMany(f => f.Document.Descendants())
            .Select(e => e.Attribute(XName.Get("Key", XamlNs))?.Value)
            .Where(k => k is not null).Select(k => k!).ToHashSet();

        foreach (var (name, doc) in files)
        {
            _file = name;
            var root = doc.Root!;
            var designType = root.Attributes().FirstOrDefault(a => a.Name.LocalName == "DataContext" && a.Name.NamespaceName.Contains("expression/blend"));
            var dataType = designType is null ? null : ResolveDesignType(designType.Value, root);
            Walk(root, new Context(dataType, null, null, null));
        }

        return [.. _findings];
    }

    public static IReadOnlyList<(string Name, XDocument Document)> LoadDirectory(string dir) =>
        Directory.EnumerateFiles(dir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path.GetRelativePath(dir, f), XDocument.Load(f, LoadOptions.SetLineInfo)))
            .ToList();

    private void Walk(XElement el, Context ctx)
    {
        var local = el.Name.LocalName;

        if (local.Contains('.'))
        {
            // Property element, e.g. <DataGrid.Columns>: templates inside it describe one *item*.
            var propName = local[(local.IndexOf('.') + 1)..];
            var inner = TemplatePropertyNames.Contains(propName) && ctx.ItemType is not null ? ctx with { DataType = ctx.ItemType } : ctx;
            foreach (var child in el.Elements())
            {
                Walk(child, inner);
            }

            return;
        }

        Type? wpfType = null;
        if (el.Name.NamespaceName == PresentationNs)
        {
            wpfType = wpf.Find(local);
            if (wpfType is null)
            {
                Report(el, $"unknown element <{local}>");
            }
        }

        // Context for children
        var next = ctx;
        if (local is "Style" or "ControlTemplate" or "DataTemplate" or "ItemsPanelTemplate" or "HierarchicalDataTemplate")
        {
            var target = Attr(el, "TargetType");
            if (target is not null && TypeName(target) is { } tn)
            {
                next = next with { TargetType = wpf.Find(tn) };
                if (next.TargetType is null)
                {
                    Report(el, $"unknown TargetType '{target}'");
                }
            }

            if (local is "ControlTemplate" or "DataTemplate")
            {
                next = next with { TemplateRoot = el };
            }

            if (local is "DataTemplate" && Attr(el, "DataType") is { } dt)
            {
                next = next with { DataType = ResolveXType(dt, el), ItemType = null };
            }
        }

        if (Attr(el, "ItemsSource") is { } items && items.StartsWith("{Binding", StringComparison.Ordinal))
        {
            var bound = ResolveBinding(el, items, ctx);
            next = next with { ItemType = bound is null ? null : ElementTypeOf(bound) };
        }

        // Attributes (DisplayMemberPath on the same element refers to the item type, hence next)
        foreach (var attr in el.Attributes())
        {
            if (attr.IsNamespaceDeclaration || attr.Name.NamespaceName is XamlNs or "http://schemas.microsoft.com/expression/blend/2008"
                or "http://schemas.openxmlformats.org/markup-compatibility/2006")
            {
                continue;
            }

            CheckAttribute(el, attr, wpfType, ctx, next.ItemType);
        }

        foreach (var child in el.Elements())
        {
            Walk(child, next);
        }
    }

    private void CheckAttribute(XElement el, XAttribute attr, Type? wpfType, Context ctx, Type? ownItemType)
    {
        var name = attr.Name.LocalName;
        var value = attr.Value;

        foreach (Match m in ResourceReference().Matches(value))
        {
            if (!_resourceKeys.Contains(m.Groups[1].Value))
            {
                Report(el, $"resource '{m.Groups[1].Value}' is not defined in any XAML file");
            }
        }

        if (attr.Name.NamespaceName.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            // An attached property of our own (e.g. PasswordBoxAssist.Password): can't look it up, but its binding is still checked.
            if (value.StartsWith('{') && ExtensionName(value) == "Binding")
            {
                ResolveBinding(el, value, ctx);
            }

            return;
        }

        if (value.StartsWith('{'))
        {
            var ext = ExtensionName(value);
            if (ext == "Binding")
            {
                ResolveBinding(el, value, ctx);
            }
            else if (ext == "TemplateBinding" && ctx.TargetType is not null)
            {
                var prop = value["{TemplateBinding".Length..].TrimEnd('}').Trim();
                if (wpf.PropertyType(ctx.TargetType, prop) is null)
                {
                    Report(el, $"TemplateBinding '{prop}' is not a property of {ctx.TargetType.Name}");
                }
            }

            if (wpfType is not null && !IsKnownProperty(wpfType, name) && !wpf.HasEvent(wpfType, name))
            {
                Report(el, $"{wpfType.Name} has no property '{name}'");
            }

            return;
        }

        if (el.Name.LocalName is "Setter" or "Trigger" or "DataTrigger" && name is "Property" or "Value")
        {
            // Checked once per element, when the Property attribute is seen (Value is read from the same element).
            if (name == "Property")
            {
                CheckSetterOrTrigger(el, ctx);
            }

            return;
        }

        if (wpfType is null)
        {
            return;
        }

        if (name == "DisplayMemberPath" && ownItemType is not null)
        {
            CheckPath(el, value, ownItemType, "DisplayMemberPath");
            return;
        }

        if (wpf.HasEvent(wpfType, name))
        {
            return; // handler existence is verified by the C# compiler (generated code)
        }

        var type = wpf.PropertyType(wpfType, name);
        if (type is null)
        {
            Report(el, $"{wpfType.Name} has no property '{name}'");
            return;
        }

        if (wpf.CheckLiteral(type, value) is { } problem)
        {
            Report(el, $"{wpfType.Name}.{name}: {problem}");
        }
    }

    private void CheckSetterOrTrigger(XElement el, Context ctx)
    {
        var target = ctx.TargetType;
        var targetName = Attr(el, "TargetName");
        if (targetName is not null && ctx.TemplateRoot is not null)
        {
            var named = ctx.TemplateRoot.Descendants().FirstOrDefault(d => d.Attribute(XName.Get("Name", XamlNs))?.Value == targetName);
            target = named is null ? null : wpf.Find(named.Name.LocalName);
            if (named is null)
            {
                Report(el, $"TargetName '{targetName}' is not an x:Name in this template");
                return;
            }
        }

        if (target is null)
        {
            return;
        }

        var propertyText = Attr(el, "Property");
        if (propertyText is null)
        {
            return; // DataTrigger has no Property
        }

        var propType = wpf.PropertyType(target, propertyText);
        if (propType is null)
        {
            Report(el, $"{target.Name} has no property '{propertyText}'");
            return;
        }

        var value = Attr(el, "Value");
        if (value is not null && !value.StartsWith('{') && wpf.CheckLiteral(propType, value) is { } problem)
        {
            Report(el, $"Setter/Trigger {target.Name}.{propertyText}: {problem}");
        }
    }

    private bool IsKnownProperty(Type type, string name) => wpf.PropertyType(type, name) is not null;

    /// <summary>Resolves "{Binding path, ...}" against the current DataType. Returns the CLR type the path ends at, if known.</summary>
    private Type? ResolveBinding(XElement el, string text, Context ctx)
    {
        var body = text[1..^1];
        var space = body.IndexOf(' ');
        var args = SplitArguments(space < 0 ? "" : body[(space + 1)..]).ToList();
        if (args.Any(a => a.StartsWith("RelativeSource", StringComparison.Ordinal) || a.StartsWith("ElementName", StringComparison.Ordinal)
                          || a.StartsWith("Source=", StringComparison.Ordinal)))
        {
            return null;
        }

        var path = args.FirstOrDefault(a => !a.Contains('=') || a.StartsWith("Path=", StringComparison.Ordinal));
        path = path?.StartsWith("Path=", StringComparison.Ordinal) == true ? path[5..] : path;

        if (ctx.DataType is null)
        {
            Report(el, $"binding '{text}' has no known DataContext type (add d:DataContext or a DataType)");
            return null;
        }

        return string.IsNullOrWhiteSpace(path) || path == "." ? ctx.DataType : CheckPath(el, path.Trim(), ctx.DataType, text);
    }

    private Type? CheckPath(XElement el, string path, Type start, string what)
    {
        var current = start;
        foreach (var raw in path.Split('.'))
        {
            var segment = raw.Contains('[') ? raw[..raw.IndexOf('[')] : raw;
            if (segment.Length == 0)
            {
                continue;
            }

            if (current == typeof(object))
            {
                return null;
            }

            var property = current.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
            if (property is null)
            {
                Report(el, $"{what}: '{segment}' is not a public property of {current.Name}");
                return null;
            }

            current = property.PropertyType;
        }

        return current;
    }

    private static Type? ElementTypeOf(Type collection)
    {
        if (collection.IsArray)
        {
            return collection.GetElementType();
        }

        var enumerable = new[] { collection }.Concat(collection.GetInterfaces())
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    private Type? ResolveDesignType(string value, XElement scope)
    {
        var m = DesignType().Match(value);
        return m.Success ? ResolveTypeReference(m.Groups[1].Value, scope) : null;
    }

    private Type? ResolveXType(string value, XElement scope)
    {
        var inner = value.Trim('{', '}').Trim();
        return inner.StartsWith("x:Type", StringComparison.Ordinal) ? ResolveTypeReference(inner["x:Type".Length..].Trim(), scope) : null;
    }

    /// <summary>"vm:JournalViewModel" → the real type, using the prefix declared in the XAML.</summary>
    private Type? ResolveTypeReference(string reference, XElement scope)
    {
        var parts = reference.Split(':');
        if (parts.Length != 2)
        {
            return null;
        }

        var ns = scope.GetNamespaceOfPrefix(parts[0])?.NamespaceName;
        var clr = ns is null ? null : Regex.Match(ns, @"clr-namespace:([^;]+)").Groups[1].Value;
        var type = string.IsNullOrEmpty(clr) ? null : ViewModelAssembly.GetType($"{clr}.{parts[1]}");
        if (type is null && ns is not null && ns.StartsWith("clr-namespace:WeighStation.Presentation", StringComparison.Ordinal))
        {
            Report(scope, $"type '{reference}' not found in WeighStation.Presentation");
        }

        return type;
    }

    private static string? TypeName(string targetType) =>
        targetType.StartsWith("{x:Type", StringComparison.Ordinal) ? targetType[7..].Trim('}', ' ') : targetType;

    private static string? Attr(XElement el, string name) => el.Attribute(name)?.Value;

    private static string ExtensionName(string value) =>
        value.TrimStart('{').Split([' ', '}'], 2)[0];

    /// <summary>Splits "Path, Mode=OneWay, StringFormat='{}{0:N0}, кг'" on top-level commas (not inside quotes or braces).</summary>
    private static IEnumerable<string> SplitArguments(string text)
    {
        var depth = 0;
        var quote = false;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || (text[i] == ',' && depth == 0 && !quote))
            {
                var part = text[start..i].Trim();
                if (part.Length > 0)
                {
                    yield return part;
                }

                start = i + 1;
            }
            else if (text[i] == '\'')
            {
                quote = !quote;
            }
            else if (!quote && text[i] == '{')
            {
                depth++;
            }
            else if (!quote && text[i] == '}')
            {
                depth--;
            }
        }
    }

    private void Report(XElement el, string message) =>
        _findings.Add(new Finding(_file, ((IXmlLineInfo)el).LineNumber, message));

    [GeneratedRegex(@"\{(?:Static|Dynamic)Resource\s+([^\s{}]+)\}")]
    private static partial Regex ResourceReference();

    [GeneratedRegex(@"Type=([\w:]+)")]
    private static partial Regex DesignType();
}
