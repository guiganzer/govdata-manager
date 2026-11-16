namespace GovData.Downloads.Desktop;
public partial class App : Application
{
 public App() { InitializeComponent(); UserAppTheme = AppTheme.Light; }
 protected override Window CreateWindow(IActivationState? activationState)
 {
  var page = new GuidedPage();
  var window = new Window(new NavigationPage(page)) { Title = "GovData • Assistente de dados públicos", Width = 1080, Height = 850, MinimumWidth = 650, MinimumHeight = 550 };
  window.Created += (_, _) => {
   if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window native)
    native.AppWindow.Closing += async (_, e) => { if (page.IsRunning) { e.Cancel = true; await page.RequestStopAsync(); } };
  };
  return window;
 }
}
