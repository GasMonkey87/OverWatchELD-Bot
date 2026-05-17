using Microsoft.Extensions.Logging;
using OverWatchELD.Mobile.Services;
using OverWatchELD.Mobile.Views;

namespace OverWatchELD.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<MobileSessionStore>();
        builder.Services.AddSingleton<OverWatchApiClient>();
        builder.Services.AddSingleton<RealtimePollingService>();

        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<DashboardPage>();
        builder.Services.AddTransient<DutyPage>();
        builder.Services.AddTransient<DispatchPage>();
        builder.Services.AddTransient<LiveMapPage>();
        builder.Services.AddTransient<FleetPage>();
        builder.Services.AddTransient<TruckApprovalsPage>();
        builder.Services.AddTransient<SettingsPage>();

        return builder.Build();
    }
}
