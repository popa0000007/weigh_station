using System.Reflection;
using System.Xml.Linq;
using WeighStation.Presentation;
using WeighStation.Presentation.Pages;

namespace WeighStation.Core.Tests.Xaml;

public sealed class XamlStaticChecksTests : IDisposable
{
    private readonly WpfTypes _wpf = new();

    public void Dispose() => _wpf.Dispose();

    private static string AppDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WeighStation.sln")))
            {
                return Path.Combine(dir.FullName, "src", "WeighStation.App");
            }
        }

        throw new InvalidOperationException("Repository root (WeighStation.sln) not found above the test binaries.");
    }

    private IReadOnlyList<Finding> CheckSnippet(string xaml, string extraResources = "")
    {
        var doc = XDocument.Parse(xaml, LoadOptions.SetLineInfo);
        var themes = new XDocument(XElement.Parse(
            "<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>"
            + "<SolidColorBrush x:Key='Known' Color='#FFFFFF' />" + extraResources + "</ResourceDictionary>"));
        return new XamlChecker(_wpf).Check([("snippet.xaml", doc), ("themes.xaml", themes)]);
    }

    private const string Header =
        "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' " +
        "xmlns:mc='http://schemas.openxmlformats.org/markup-compatibility/2006' xmlns:d='http://schemas.microsoft.com/expression/blend/2008' " +
        "xmlns:vm='clr-namespace:WeighStation.Presentation.Pages;assembly=WeighStation.Presentation' mc:Ignorable='d' ";

    // ---- the real application ------------------------------------------------------------------------------------

    [Fact]
    public void Application_xaml_has_no_findings()
    {
        var files = XamlChecker.LoadDirectory(AppDir());
        Assert.True(files.Count >= 10, $"expected the app's XAML files, found {files.Count}");

        var findings = new XamlChecker(_wpf).Check(files);

        Assert.True(findings.Count == 0, "XAML problems:\n" + string.Join("\n", findings));
    }

    [Fact]
    public void Every_page_view_model_and_every_reference_editor_has_a_view()
    {
        var templated = XamlChecker.LoadDirectory(AppDir())
            .SelectMany(f => f.Document.Descendants())
            .Where(e => e.Name.LocalName == "DataTemplate")
            .Select(e => e.Attribute("DataType")?.Value)
            .Where(v => v is not null)
            .Select(v => v!.Split(':').Last().TrimEnd('}', ' '))
            .ToHashSet();

        var assembly = typeof(ShellViewModel).Assembly;
        var pages = assembly.GetTypes().Where(t => t is { IsAbstract: false } && typeof(PageViewModel).IsAssignableFrom(t));
        var editors = assembly.GetTypes().Where(t =>
            t is { IsAbstract: false, BaseType.IsGenericType: true }
            && t.BaseType.GetGenericTypeDefinition() == typeof(ReferenceEditorViewModel<>));

        var missing = pages.Concat(editors).Select(t => t.Name).Where(n => !templated.Contains(n)).ToList();

        Assert.True(missing.Count == 0, "No DataTemplate (view) for: " + string.Join(", ", missing));
    }

    [Fact]
    public void Shell_shows_every_page_the_composition_root_creates()
    {
        // App.xaml.cs builds the page list by hand; make sure no PageViewModel is forgotten there.
        var source = File.ReadAllText(Path.Combine(AppDir(), "App.xaml.cs"));
        var pages = typeof(ShellViewModel).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false } && typeof(PageViewModel).IsAssignableFrom(t))
            .Select(t => t.Name);

        Assert.All(pages, name => Assert.Contains($"new {name}(", source));
    }

    [Fact]
    public void Rail_icons_are_plain_path_data_that_wpf_can_parse()
    {
        // WPF's path mini-language is stricter than SVG: no compressed arc flags like "a4 4 0 100 8".
        var assembly = typeof(ShellViewModel).Assembly;
        var pageTypes = assembly.GetTypes().Where(t => t is { IsAbstract: false } && typeof(PageViewModel).IsAssignableFrom(t));
        foreach (var type in pageTypes)
        {
            var icon = (string)type.GetProperty(nameof(PageViewModel.IconData))!.GetValue(RuntimeHelpers_GetUninitialized(type))!;
            Assert.Matches(@"^[MmLlHhVvAaZz0-9\s.\-,]+$", icon);
            Assert.DoesNotMatch(@"[Aa]\s*[\d.\-]+[\s,]+[\d.\-]+[\s,]+[\d.\-]+[\s,]+[01]{2}", icon);
        }
    }

    private static object RuntimeHelpers_GetUninitialized(Type type) =>
        System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);

    [Theory]
    [InlineData("Views/WeighingView.xaml", "{Binding StateText}", "{Binding StateTxt}", "StateTxt")]
    [InlineData("Views/JournalView.xaml", "{Binding SelectedRow, Mode=TwoWay}", "{Binding SelectedRw, Mode=TwoWay}", "SelectedRw")]
    [InlineData("Views/ReferencesView.xaml", "{Binding Plate}", "{Binding Platee}", "Platee")]
    [InlineData("Views/SettingsView.xaml", "{Binding BackupCommand}", "{Binding BackupComand}", "BackupComand")]
    [InlineData("MainWindow.xaml", "{Binding DeviceChips}", "{Binding DeviceChip}", "DeviceChip")]
    [InlineData("Views/ReportsView.xaml", "{StaticResource SlimBar}", "{StaticResource SlimBarr}", "SlimBarr")]
    [InlineData("Views/DevicesView.xaml", "FontWeight=\"SemiBold\"", "FontWeight=\"SemiBolt\"", "SemiBolt")]
    public void Checker_finds_a_typo_injected_into_a_real_view(string file, string original, string typo, string expectedInMessage)
    {
        var files = XamlChecker.LoadDirectory(AppDir()).ToList();
        var index = files.FindIndex(f => f.Name.Replace('\\', '/') == file);
        Assert.True(index >= 0, $"{file} not found");
        var text = File.ReadAllText(Path.Combine(AppDir(), file));
        Assert.Contains(original, text);
        files[index] = (files[index].Name, XDocument.Parse(text.Replace(original, typo), LoadOptions.SetLineInfo));

        var findings = new XamlChecker(_wpf).Check(files);

        Assert.Contains(findings, f => f.Message.Contains(expectedInMessage) && f.File.Replace('\\', '/') == file);
    }

    // ---- the checker itself: it must catch what it claims to catch --------------------------------------------------

    [Fact]
    public void Clean_snippet_passes()
    {
        var findings = CheckSnippet($@"<UserControl {Header} d:DataContext='{{d:DesignInstance Type=vm:JournalViewModel}}'>
            <StackPanel Margin='0,0,8,0'>
                <TextBlock Text='{{Binding Summary}}' FontWeight='SemiBold' Foreground='{{StaticResource Known}}' />
                <DataGrid ItemsSource='{{Binding Rows}}' SelectedItem='{{Binding SelectedRow, Mode=TwoWay}}'>
                    <DataGrid.Columns><DataGridTextColumn Header='x' Binding='{{Binding Plate}}' Width='*' /></DataGrid.Columns>
                </DataGrid>
                <ComboBox ItemsSource='{{Binding StatusOptions}}' DisplayMemberPath='Label' />
            </StackPanel></UserControl>");
        Assert.Empty(findings);
    }

    [Theory]
    [InlineData("<TextBlock FontWeight='Heavyy' />", "FontWeight")]
    [InlineData("<TextBlock Foreground='NotAColor' />", "colour")]
    [InlineData("<Grid Width='wide' />", "not a number")]
    [InlineData("<Border CornerRadius='x' />", "CornerRadius")]
    [InlineData("<StackPanel Orientation='Diagonal' />", "Orientation")]
    [InlineData("<Button Visibility='Hidden2' />", "Visibility")]
    [InlineData("<TextBlock Heightt='3' />", "no property 'Heightt'")]
    [InlineData("<Buttn />", "unknown element")]
    [InlineData("<TextBlock Foreground='{StaticResource Nope}' />", "'Nope' is not defined")]
    [InlineData("<TextBlock Text='{Binding Sumary}' />", "'Sumary' is not a public property of JournalViewModel")]
    [InlineData("<TextBlock Text='{Binding SelectedRow.Plat}' />", "'Plat' is not a public property of JournalRow")]
    [InlineData("<Grid Grid.Row='first' />", "Grid.Row")]
    [InlineData("<ScrollViewer ScrollViewer.VerticalScrollBarVisibility='Sometimes' />", "ScrollBarVisibility")]
    public void Checker_reports_known_mistakes(string element, string expectedFragment)
    {
        var findings = CheckSnippet($"<UserControl {Header} d:DataContext='{{d:DesignInstance Type=vm:JournalViewModel}}'>{element}</UserControl>");

        var finding = Assert.Single(findings);
        Assert.Contains(expectedFragment, finding.Message);
    }

    [Fact]
    public void Checker_reports_wrong_property_on_items_and_bad_display_member()
    {
        var findings = CheckSnippet($@"<UserControl {Header} d:DataContext='{{d:DesignInstance Type=vm:JournalViewModel}}'>
            <StackPanel>
                <DataGrid ItemsSource='{{Binding Rows}}'>
                    <DataGrid.Columns><DataGridTextColumn Binding='{{Binding Plat}}' /></DataGrid.Columns>
                </DataGrid>
                <ComboBox ItemsSource='{{Binding StatusOptions}}' DisplayMemberPath='Name' />
            </StackPanel></UserControl>");

        Assert.Equal(2, findings.Count);
        Assert.Contains("'Plat' is not a public property of JournalRow", findings[0].Message);
        Assert.Contains("'Name' is not a public property of StatusOption", findings[1].Message);
    }

    [Fact]
    public void Checker_follows_explicit_template_data_types()
    {
        var ok = CheckSnippet($@"<UserControl {Header}>
            <UserControl.Resources>
                <DataTemplate DataType='{{x:Type vm:JournalRow}}'><TextBlock Text='{{Binding Plate}}' /></DataTemplate>
            </UserControl.Resources></UserControl>");
        Assert.Empty(ok);

        var bad = CheckSnippet($@"<UserControl {Header}>
            <UserControl.Resources>
                <DataTemplate DataType='{{x:Type vm:JournalRow}}'><TextBlock Text='{{Binding Nope}}' /></DataTemplate>
            </UserControl.Resources></UserControl>");
        Assert.Single(bad);
    }

    [Fact]
    public void Checker_demands_a_known_data_context_for_bindings()
    {
        var findings = CheckSnippet($"<UserControl {Header}><TextBlock Text='{{Binding Anything}}' /></UserControl>");
        Assert.Contains("no known DataContext", Assert.Single(findings).Message);
    }

    [Fact]
    public void Checker_skips_element_name_and_relative_source_bindings()
    {
        var findings = CheckSnippet($@"<UserControl {Header}><StackPanel>
            <TextBlock x:Name='a' />
            <TextBlock Text='{{Binding Text, ElementName=a}}' />
            <TextBlock Text='{{Binding Text, RelativeSource={{RelativeSource Self}}}}' /></StackPanel></UserControl>");
        Assert.Empty(findings);
    }

    [Fact]
    public void Checker_validates_style_setters_and_control_templates()
    {
        var findings = CheckSnippet($@"<ResourceDictionary {Header}>
            <Style x:Key='A' TargetType='Button'>
                <Setter Property='Height' Value='tall' />
                <Setter Property='NoSuchThing' Value='1' />
                <Setter Property='Template'>
                    <Setter.Value>
                        <ControlTemplate TargetType='Button'>
                            <Border x:Name='Bd' Background='{{TemplateBinding Background}}' BorderBrush='{{TemplateBinding Nope}}' />
                            <ControlTemplate.Triggers>
                                <Trigger Property='IsMouseOver' Value='Yes'><Setter TargetName='Bd' Property='Opacity' Value='0.5' /></Trigger>
                                <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Missing' Property='Opacity' Value='0.5' /></Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style></ResourceDictionary>");

        var messages = findings.Select(f => f.Message).ToList();
        Assert.Contains(messages, m => m.Contains("'tall'"));
        Assert.Contains(messages, m => m.Contains("no property 'NoSuchThing'"));
        Assert.Contains(messages, m => m.Contains("TemplateBinding 'Nope'"));
        Assert.Contains(messages, m => m.Contains("'Yes' is not a boolean"));
        Assert.Contains(messages, m => m.Contains("TargetName 'Missing'"));
        Assert.True(findings.Count == 5, string.Join("\n", messages));
    }

    [Fact]
    public void Checker_accepts_setters_on_template_parts_by_target_name()
    {
        var findings = CheckSnippet($@"<ResourceDictionary {Header}>
            <Style x:Key='A' TargetType='Button'>
                <Setter Property='Template'>
                    <Setter.Value>
                        <ControlTemplate TargetType='Button'>
                            <Border x:Name='Bd' />
                            <ControlTemplate.Triggers>
                                <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='#FFFFFF' /></Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style></ResourceDictionary>");
        Assert.Empty(findings);
    }
}
