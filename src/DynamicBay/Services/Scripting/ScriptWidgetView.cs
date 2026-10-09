using System.Windows;
using System.Windows.Controls;
using DynamicBay.Core;

namespace DynamicBay.Services.Scripting;

/// <summary>Shows the latest result of one script in one size; every island builds its own visuals.</summary>
public sealed class ScriptWidgetView : ContentControl
{
    public static readonly DependencyProperty ScriptIdProperty = DependencyProperty.Register(
        nameof(ScriptId), typeof(string), typeof(ScriptWidgetView), new PropertyMetadata(null, (d, _) => ((ScriptWidgetView)d).Refresh()));

    public static readonly DependencyProperty FamilyProperty = DependencyProperty.Register(
        nameof(Family), typeof(string), typeof(ScriptWidgetView), new PropertyMetadata(ScriptWidgetsService.Large, (d, _) => { var v = (ScriptWidgetView)d; v._shown = default; v.Refresh(); }));

    public static readonly DependencyProperty VerticalProperty = DependencyProperty.Register(
        nameof(Vertical), typeof(bool), typeof(ScriptWidgetView), new PropertyMetadata(false, (d, _) => { var v = (ScriptWidgetView)d; v._shown = default; v.Refresh(); }));

    public string? ScriptId { get => (string?)GetValue(ScriptIdProperty); set => SetValue(ScriptIdProperty, value); }
    /// <summary>Mini line in an island at a side edge.</summary>
    public bool Vertical { get => (bool)GetValue(VerticalProperty); set => SetValue(VerticalProperty, value); }
    public string Family { get => (string)GetValue(FamilyProperty); set => SetValue(FamilyProperty, value); }

    private DateTime _shown;

    public ScriptWidgetView()
    {
        Focusable = false;
        Loaded += (_, _) =>
        {
            if (ScriptWidgetsService.Instance is { } s) { s.Updated -= OnUpdated; s.Updated += OnUpdated; }
            Refresh();
        };
        Unloaded += (_, _) => { if (ScriptWidgetsService.Instance is { } s) s.Updated -= OnUpdated; };
    }

    private void OnUpdated(string id) { if (id == ScriptId) Refresh(); }

    private void Refresh()
    {
        if (ScriptId is null || ScriptWidgetsService.Instance is not { } s) return;
        var r = s.Get(ScriptId, Family);
        if (r is null)
        {
            if (Content is null && Family != ScriptWidgetsService.Mini)
                Content = new TextBlock { Text = Loc.German ? "Lädt…" : "Loading…", Foreground = System.Windows.Media.Brushes.Gray, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return;
        }
        if (r.RanAt == _shown && Content is not null) return;
        _shown = r.RanAt;
        // Hover: which script, when it last ran, or what went wrong.
        var cfg = s.ConfigOf(ScriptId);
        ToolTip = cfg is null ? null : $"{cfg.Name} · {s.StatusOf(cfg)}";
        try
        {
            Content = r.Widget is not null ? ScriptRenderer.Build(r.Widget, Family, Vertical)
                : Family == ScriptWidgetsService.Mini ? null : ScriptRenderer.Error(r.Error ?? "");
        }
        catch (Exception ex)
        {
            Log.Error("ScriptRender", ex);
            Content = Family == ScriptWidgetsService.Mini ? null : ScriptRenderer.Error(ex.Message);
        }
    }
}
