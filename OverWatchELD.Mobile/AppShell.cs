using OverWatchELD.Mobile.Views;

namespace OverWatchELD.Mobile;

public sealed class AppShell : Shell
{
    public AppShell()
    {
        Title = "OverWatch ELD Mobile";
        FlyoutBehavior = FlyoutBehavior.Flyout;

        Items.Add(MakeItem("Login", typeof(LoginPage)));
        Items.Add(MakeItem("Dashboard", typeof(DashboardPage)));
        Items.Add(MakeItem("Duty Buttons", typeof(DutyPage)));
        Items.Add(MakeItem("Dispatch", typeof(DispatchPage)));
        Items.Add(MakeItem("Fleet", typeof(FleetPage)));
        Items.Add(MakeItem("Truck Approvals", typeof(TruckApprovalsPage)));
        Items.Add(MakeItem("Live Map", typeof(LiveMapPage)));
        Items.Add(MakeItem("Settings", typeof(SettingsPage)));
    }

    private static FlyoutItem MakeItem(string title, Type pageType)
    {
        return new FlyoutItem
        {
            Title = title,
            Items =
            {
                new ShellContent
                {
                    Title = title,
                    ContentTemplate = new DataTemplate(pageType)
                }
            }
        };
    }
}
