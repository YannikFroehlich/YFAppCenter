using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace YFAppCenter.Tests;

[TestClass]
public sealed class AppUpdaterTests
{
    // Tests laufen ohne Velopack-Installation: dann darf das Update nichts tun und die Version kommt aus der Assembly.
    [TestMethod]
    public async Task Without_velopack_install_no_update_is_offered_and_assembly_version_is_shown()
    {
        Assert.IsNull(await AppUpdater.DownloadUpdateAsync());
        AppUpdater.ApplyAndRestart();
        Assert.AreEqual(typeof(AppUpdater).Assembly.GetName().Version!.ToString(3), AppUpdater.CurrentVersion);
    }
}
