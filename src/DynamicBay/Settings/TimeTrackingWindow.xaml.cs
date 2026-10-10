using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DynamicBay.Core;
using DynamicBay.Services;

namespace DynamicBay.Settings;

/// <summary>
/// Time tracking in full, apart from the island: the entries of a day (change, delete), time added by hand with a
/// description, the week per project, and the projects (rename, remove, start). The island widget stays small.
/// </summary>
public partial class TimeTrackingWindow : Window
{
    public sealed record WeekRow(string Project, string Hours);

    private readonly TimeTrackingService _time;
    private readonly AppSettings _settings;
    private DateTime _day = DateTime.Today;
    private TimeEntry? _editing;

    public TimeTrackingWindow(TimeTrackingService time, AppSettings settings)
    {
        InitializeComponent();
        Resources.MergedDictionaries.Add(SettingsTheme.Create(SettingsTheme.IsLight()));
        _time = time;
        _settings = settings;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = SettingsTheme.IsLight() ? 0 : 1, mica = 2;
            Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            if (Environment.OSVersion.Version.Build >= 22000) Native.DwmSetWindowAttribute(hwnd, 38, ref mica, sizeof(int));
            else Background = (Brush)FindResource("S.Window");
        };
        Action changed = () => Dispatcher.BeginInvoke(Refresh);
        System.ComponentModel.PropertyChangedEventHandler projects = (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.TimeProjects)) Dispatcher.BeginInvoke(Refresh);
        };
        _time.Changed += changed;
        _settings.PropertyChanged += projects;
        Closed += (_, _) => { _time.Changed -= changed; _settings.PropertyChanged -= projects; };
        ResetForm();
        Refresh();
    }

    public void ShowDay(DateTime day) { _day = day.Date; ResetForm(); Refresh(); }

    private static readonly CultureInfo Culture = Loc.German ? new CultureInfo("de-DE") : CultureInfo.CurrentCulture;

    private void Refresh()
    {
        // ---- day ----
        DayTitle.Text = _day == DateTime.Today ? (Loc.German ? "Heute" : "Today")
                      : _day == DateTime.Today.AddDays(-1) ? (Loc.German ? "Gestern" : "Yesterday")
                      : _day.ToString("dddd, d. MMMM", Culture);
        if (_day.Year != DateTime.Today.Year) DayTitle.Text += _day.ToString(" yyyy", Culture);
        TodayButton.Visibility = _day == DateTime.Today ? Visibility.Collapsed : Visibility.Visible;
        var entries = _time.EntriesOn(_day).ToList();
        EntryList.ItemsSource = entries;
        EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DayTotal.Text = TimeTrackingService.Format(_time.Total(_day, _day.AddDays(1))) + " h";

        // ---- week (Monday to Sunday of the shown day) ----
        var monday = _day.AddDays(-(((int)_day.DayOfWeek + 6) % 7));
        WeekTitle.Text = (Loc.German ? "Woche vom " : "Week of ") + monday.ToString("d. MMM", Culture);
        var week = _time.Entries.Where(e => e.Start >= monday && e.Start < monday.AddDays(7)).Select(e => e.Project).Distinct()
            .Select(p => (p, _time.Total(monday, monday.AddDays(7), p)))
            .OrderByDescending(x => x.Item2)
            .Select(x => new WeekRow(x.p, TimeTrackingService.Format(x.Item2) + " h")).ToList();
        WeekList.ItemsSource = week;
        WeekTotal.Text = TimeTrackingService.Format(_time.Total(monday, monday.AddDays(7))) + " h";

        // ---- projects ----
        BuildProjectRows();
        BuildProjectChips();
        FilePathText.Text = _time.FilePath;
        FilePathText.ToolTip = _time.FilePath;
    }

    // ---------- day navigation ----------

    private void PrevDay_Click(object sender, RoutedEventArgs e) => ShowDay(_day.AddDays(-1));
    private void NextDay_Click(object sender, RoutedEventArgs e) => ShowDay(_day.AddDays(1));
    private void Today_Click(object sender, RoutedEventArgs e) => ShowDay(DateTime.Today);

    // ---------- entries ----------

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TimeEntry entry) return;
        _editing = entry;
        FormTitle.Text = Loc.German ? "Eintrag bearbeiten" : "Edit entry";
        FormSave.Content = Loc.German ? "Speichern" : "Save";
        CancelEdit.Visibility = Visibility.Visible;
        FormProject.Text = entry.Project;
        FormStart.Text = entry.Start.ToString("HH:mm");
        FormEnd.Text = entry.End?.ToString("HH:mm") ?? "";
        FormEnd.IsEnabled = !entry.IsRunning;
        FormDescription.Text = entry.Description;
        FormError.Visibility = Visibility.Collapsed;
        FormDescription.Focus();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TimeEntry entry) return;
        if (_editing == entry) ResetForm();
        _time.DeleteEntry(entry);
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e) => ResetForm();

    /// <summary>The form starts where the day's last entry ended (or 9:00), one hour long.</summary>
    private void ResetForm()
    {
        _editing = null;
        FormTitle.Text = Loc.German ? "Zeit nachtragen" : "Add time";
        FormSave.Content = Loc.German ? "Hinzufügen" : "Add";
        CancelEdit.Visibility = Visibility.Collapsed;
        FormEnd.IsEnabled = true;
        var last = _time.EntriesOn(_day).Where(e => e.End is not null).Select(e => e.End!.Value).DefaultIfEmpty(_day.AddHours(9)).Max();
        if (_day == DateTime.Today && last > DateTime.Now) last = TimeTrackingService.RoundQuarter(DateTime.Now.AddHours(-1));
        FormStart.Text = last.ToString("HH:mm");
        FormEnd.Text = last.AddHours(1).ToString("HH:mm");
        FormDescription.Text = "";
        FormError.Visibility = Visibility.Collapsed;
        if (FormProject.Text.Length == 0) FormProject.Text = _settings.TimeProjects.FirstOrDefault() ?? "";
    }

    /// <summary>"9", "930", "9:30", "9.30" → 9:30.</summary>
    public static TimeSpan? ParseTime(string text)
    {
        text = text.Trim().Replace('.', ':').Replace(',', ':').Replace(" ", "");
        if (text.Length == 0) return null;
        if (!text.Contains(':'))
        {
            if (!int.TryParse(text, out int n)) return null;
            text = n < 100 ? $"{n}:00" : $"{n / 100}:{n % 100:00}";
        }
        var parts = text.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int h) || !int.TryParse(parts[1], out int m)) return null;
        if (h < 0 || h > 24 || m < 0 || m > 59 || (h == 24 && m > 0)) return null;
        return new TimeSpan(h, m, 0);
    }

    private void Length_Click(object sender, RoutedEventArgs e)
    {
        if (!FormEnd.IsEnabled || ParseTime(FormStart.Text) is not { } start || ((Button)sender).Tag is not string tag) return;
        FormEnd.Text = (_day + start).AddMinutes(int.Parse(tag)).ToString("HH:mm");
    }

    private void Form_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { FormSave_Click(sender, e); e.Handled = true; }
    }

    private void FormSave_Click(object sender, RoutedEventArgs e)
    {
        string project = FormProject.Text.Trim();
        var start = ParseTime(FormStart.Text);
        var end = ParseTime(FormEnd.Text);
        string? error = project.Length == 0 ? (Loc.German ? "Bitte ein Projekt wählen oder eingeben." : "Please pick or type a project.")
                      : start is null ? (Loc.German ? "Die Startzeit verstehe ich nicht (z. B. 9:30)." : "Can't read the start time (e.g. 9:30).")
                      : FormEnd.IsEnabled && end is null ? (Loc.German ? "Die Endzeit verstehe ich nicht (z. B. 10:45)." : "Can't read the end time (e.g. 10:45).")
                      : null;
        if (error is not null) { FormError.Text = error; FormError.Visibility = Visibility.Visible; return; }
        var s = (_editing?.Start.Date ?? _day) + start!.Value;
        DateTime? en = FormEnd.IsEnabled ? s.Date + end!.Value : null;
        if (en is { } x && x <= s) en = x.AddDays(1); // past midnight
        if (_editing is { } entry) _time.UpdateEntry(entry, project, s, en, FormDescription.Text);
        else _time.AddEntry(project, s, en!.Value, FormDescription.Text);
        string keep = project;
        ResetForm();
        FormProject.Text = keep;
    }

    // ---------- projects ----------

    private void BuildProjectChips()
    {
        ProjectChips.Children.Clear();
        foreach (var name in _settings.TimeProjects)
        {
            var chip = new Button { Style = (Style)FindResource("T.Chip"), Content = name };
            chip.Click += (_, _) => FormProject.Text = name;
            ProjectChips.Children.Add(chip);
        }
    }

    private void BuildProjectRows()
    {
        ProjectRows.Children.Clear();
        foreach (var name in _settings.TimeProjects.ToList())
        {
            string project = name;
            bool running = _time.IsRunning && _time.CurrentProject == project;
            var row = new DockPanel { Margin = new Thickness(4, 2, 4, 2) };

            Button IconButton(string icon, string tip, Brush? brush = null)
            {
                var b = new Button { Style = (Style)FindResource("T.IconButton"), ToolTip = tip,
                    Content = new Controls.Icon { Data = (Geometry)FindResource(icon), Width = 13, Height = 13 } };
                if (brush is not null) ((Controls.Icon)b.Content).Foreground = brush;
                DockPanel.SetDock(b, Dock.Right);
                return b;
            }
            var remove = IconButton("Icon.Trash", Loc.German ? "Aus der Liste entfernen (die erfasste Zeit bleibt in der Datei)" : "Remove from the list (recorded time stays in the file)");
            remove.Click += (_, _) => _time.RemoveProject(project);
            var play = IconButton(running ? "Icon.Close" : "Icon.Timer", running ? (Loc.German ? "Stoppen" : "Stop") : (Loc.German ? "Jetzt starten" : "Start now"),
                running ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)) : null);
            play.Click += (_, _) => { if (running) _time.StopCommand.Execute(null); else _time.Start(project); Refresh(); };
            row.Children.Add(remove);
            row.Children.Add(play);

            // The name is a text field: change it and press Enter (or leave the field) to rename everywhere.
            var box = new TextBox { Style = (Style)FindResource("S.TextBox"), Text = project, Height = 30, ToolTip = Loc.German ? "Zum Umbenennen ändern" : "Change to rename" };
            if (running) box.FontWeight = FontWeights.SemiBold;
            void Commit() { if (box.Text.Trim().Length > 0 && box.Text.Trim() != project) _time.RenameProject(project, box.Text); else box.Text = project; }
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } else if (e.Key == Key.Escape) box.Text = project; };
            box.LostKeyboardFocus += (_, _) => Commit();
            row.Children.Add(box);
            ProjectRows.Children.Add(row);
        }
    }

    private void AddProject_Click(object sender, RoutedEventArgs e)
    {
        _time.AddProject(NewProject.Text);
        NewProject.Text = "";
    }

    private void NewProject_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddProject_Click(sender, e); e.Handled = true; }
    }

    private void ShowFile_Click(object sender, RoutedEventArgs e) => _time.ShowFileCommand.Execute(null);
}
