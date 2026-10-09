using Velopack;

namespace YFAppCenter;

public static class Program
{
    [STAThread]
    private static void Main()
    {
        // Muss vor allem anderen laufen: Velopack startet die App bei Installation und Update mit eigenen Argumenten.
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
