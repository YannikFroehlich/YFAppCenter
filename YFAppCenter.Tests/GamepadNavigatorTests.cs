using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace YFAppCenter.Tests;

[TestClass]
public sealed class GamepadNavigatorTests
{
    [TestMethod]
    public void Buttons_fire_once_per_press()
    {
        var pad = new GamepadNavigator();

        CollectionAssert.AreEqual(new[] { PadAction.Activate }, pad.Update(GamepadNavigator.A, 0, 0, 0));
        Assert.IsEmpty(pad.Update(GamepadNavigator.A, 0, 0, 1000), "Gedrückt halten löst nicht erneut aus.");
        Assert.IsEmpty(pad.Update(0, 0, 0, 1100));
        CollectionAssert.AreEqual(new[] { PadAction.Activate }, pad.Update(GamepadNavigator.A, 0, 0, 1200));
    }

    [TestMethod]
    public void Held_direction_repeats_after_delay()
    {
        var pad = new GamepadNavigator();

        CollectionAssert.AreEqual(new[] { PadAction.Down }, pad.Update(GamepadNavigator.DPadDown, 0, 0, 0));
        Assert.IsEmpty(pad.Update(GamepadNavigator.DPadDown, 0, 0, 399));
        CollectionAssert.AreEqual(new[] { PadAction.Down }, pad.Update(GamepadNavigator.DPadDown, 0, 0, 400));
        Assert.IsEmpty(pad.Update(GamepadNavigator.DPadDown, 0, 0, 500));
        CollectionAssert.AreEqual(new[] { PadAction.Down }, pad.Update(GamepadNavigator.DPadDown, 0, 0, 510));
    }

    [TestMethod]
    public void Stick_uses_dominant_axis_and_ignores_dead_zone()
    {
        var pad = new GamepadNavigator();

        Assert.IsEmpty(pad.Update(0, 8000, -8000, 0));
        CollectionAssert.AreEqual(new[] { PadAction.Up }, pad.Update(0, 5000, 30000, 100));
        Assert.IsEmpty(pad.Update(0, 0, 0, 200));
        CollectionAssert.AreEqual(new[] { PadAction.Left }, pad.Update(0, -30000, 20000, 300));
    }
}
