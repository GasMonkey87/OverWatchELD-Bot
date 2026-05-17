namespace OverWatchELD.Mobile.Views;

public sealed class DutyPage : ContentPage
{
    public DutyPage()
    {
        Title = "Duty Status";
        BackgroundColor = Color.FromArgb("#0B1220");

        Content = new VerticalStackLayout
        {
            Padding = 24,
            Spacing = 16,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                MakeButton("OFF DUTY", "#374151"),
                MakeButton("SLEEPER", "#7C3AED"),
                MakeButton("ON DUTY", "#D97706"),
                MakeButton("DRIVING", "#16A34A")
            }
        };
    }

    private static Button MakeButton(string text, string color)
    {
        return new Button
        {
            Text = text,
            BackgroundColor = Color.FromArgb(color),
            TextColor = Colors.White,
            FontSize = 22,
            FontAttributes = FontAttributes.Bold,
            HeightRequest = 72,
            CornerRadius = 20
        };
    }
}
