using System.Windows;
using DynamicBay.Core;

namespace DynamicBay.Tests;

public class WindowToolsTests
{
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }

    [Theory]
    [InlineData(WindowState.Normal)]
    [InlineData(WindowState.Maximized)]
    public void BringBack_restores_a_minimized_window_to_its_previous_state(WindowState before)
    {
        OnSta(() =>
        {
            var w = new Window { Width = 200, Height = 150, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                w.Show();
                w.WindowState = before;
                w.WindowState = WindowState.Minimized;
                WindowTools.BringBack(w);
                Assert.Equal(before, w.WindowState);
                Assert.True(w.IsVisible);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void BringBack_shows_a_hidden_window()
    {
        OnSta(() =>
        {
            var w = new Window { Width = 200, Height = 150, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                w.Show();
                w.Hide();
                WindowTools.BringBack(w);
                Assert.True(w.IsVisible);
                Assert.False(w.Topmost);
            }
            finally { w.Close(); }
        });
    }
}
