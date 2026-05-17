namespace OverWatchELD.Mobile.Views;

public sealed class LoginPage : ContentPage
{
    public LoginPage()
    {
        Title = "Login";
        BackgroundColor = Color.FromArgb("#0B1220");

        var title = new Label
        {
            Text = "OverWatch ELD Mobile",
            FontSize = 28,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
            HorizontalOptions = LayoutOptions.Center
        };

        var codeBox = new Entry
        {
            Placeholder = "Enter Discord Link Code",
            TextColor = Colors.White,
            PlaceholderColor = Colors.Gray
        };

        var button = new Button
        {
            Text = "Connect",
            BackgroundColor = Color.FromArgb("#2563EB"),
            TextColor = Colors.White,
            CornerRadius = 16,
            HeightRequest = 56
        };

        Content = new VerticalStackLayout
        {
            Padding = 30,
            Spacing = 20,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                title,
                codeBox,
                button
            }
        };
    }
}
