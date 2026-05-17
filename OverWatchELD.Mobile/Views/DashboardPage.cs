namespace OverWatchELD.Mobile.Views;

public sealed class DashboardPage : ContentPage
{
    public DashboardPage()
    {
        Title = "Dashboard";
        BackgroundColor = Color.FromArgb("#0B1220");

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 20,
                Spacing = 16,
                Children =
                {
                    Header("Driver Dashboard"),
                    ClockCard("Drive", "11:00", "Remaining drive time"),
                    ClockCard("Shift", "14:00", "Remaining shift time"),
                    ClockCard("Cycle", "70:00", "Remaining cycle time"),
                    InfoCard("Current Truck", "No truck connected yet"),
                    InfoCard("Current Load", "No active load"),
                    InfoCard("Connection", "Railway API: https://overwatcheld.up.railway.app")
                }
            }
        };
    }

    private static Label Header(string text) => new()
    {
        Text = text,
        FontSize = 26,
        FontAttributes = FontAttributes.Bold,
        TextColor = Colors.White
    };

    private static Border ClockCard(string title, string value, string subtitle) => new()
    {
        BackgroundColor = Color.FromArgb("#121A2B"),
        StrokeShape = new RoundRectangle { CornerRadius = 22 },
        Padding = 18,
        Content = new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                new Label { Text = title, TextColor = Color.FromArgb("#9CA3AF"), FontSize = 14 },
                new Label { Text = value, TextColor = Colors.White, FontSize = 38, FontAttributes = FontAttributes.Bold },
                new Label { Text = subtitle, TextColor = Color.FromArgb("#9CA3AF"), FontSize = 13 }
            }
        }
    };

    private static Border InfoCard(string title, string value) => new()
    {
        BackgroundColor = Color.FromArgb("#121A2B"),
        StrokeShape = new RoundRectangle { CornerRadius = 18 },
        Padding = 16,
        Content = new VerticalStackLayout
        {
            Children =
            {
                new Label { Text = title, TextColor = Color.FromArgb("#93C5FD"), FontAttributes = FontAttributes.Bold },
                new Label { Text = value, TextColor = Colors.White }
            }
        }
    };
}
