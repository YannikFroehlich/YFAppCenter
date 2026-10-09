using System.Runtime.InteropServices;

namespace YFAppCenter;

public enum PadAction
{
    Up,
    Down,
    Left,
    Right,
    Activate,
    Back,
    Favorite,
    Menu,
    PreviousSection,
    NextSection
}

/// <summary>Liest Xbox-kompatible Controller über XInput.</summary>
public static class XInput
{
    [StructLayout(LayoutKind.Sequential)]
    public struct State
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int GetState(int userIndex, out State state);

    /// <summary>Zustand des ersten angeschlossenen Controllers, sonst null.</summary>
    public static State? ReadFirst()
    {
        for (var index = 0; index < 4; index++)
        {
            if (GetState(index, out var state) == 0)
            {
                return state;
            }
        }

        return null;
    }
}

/// <summary>
/// Übersetzt Controller-Zustände in Aktionen: Tasten lösen einmal pro Druck aus, Richtungen wiederholen
/// sich beim Gedrückthalten wie eine Pfeiltaste.
/// </summary>
public sealed class GamepadNavigator
{
    public const ushort DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008;
    public const ushort Start = 0x0010, LeftShoulder = 0x0100, RightShoulder = 0x0200;
    public const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    private const short StickDeadZone = 16000;
    private const long FirstRepeatMs = 400;
    private const long RepeatMs = 110;

    private static readonly (ushort Button, PadAction Action)[] ButtonActions =
    [
        (A, PadAction.Activate),
        (Start, PadAction.Activate),
        (B, PadAction.Back),
        (X, PadAction.Menu),
        (Y, PadAction.Favorite),
        (LeftShoulder, PadAction.PreviousSection),
        (RightShoulder, PadAction.NextSection)
    ];

    private ushort previousButtons;
    private PadAction? heldDirection;
    private long nextRepeatAt;

    public List<PadAction> Update(ushort buttons, short thumbX, short thumbY, long nowMs)
    {
        var actions = new List<PadAction>();
        foreach (var (button, action) in ButtonActions)
        {
            if ((buttons & button) != 0 && (previousButtons & button) == 0)
            {
                actions.Add(action);
            }
        }

        previousButtons = buttons;

        var direction = Direction(buttons, thumbX, thumbY);
        if (direction is null)
        {
            heldDirection = null;
        }
        else if (direction != heldDirection)
        {
            heldDirection = direction;
            nextRepeatAt = nowMs + FirstRepeatMs;
            actions.Add(direction.Value);
        }
        else if (nowMs >= nextRepeatAt)
        {
            nextRepeatAt = nowMs + RepeatMs;
            actions.Add(direction.Value);
        }

        return actions;
    }

    /// <summary>Vergisst gehaltene Tasten, etwa wenn das Fenster den Fokus verloren hatte.</summary>
    public void Reset(ushort buttons)
    {
        previousButtons = buttons;
        heldDirection = null;
    }

    private static PadAction? Direction(ushort buttons, short thumbX, short thumbY)
    {
        if ((buttons & DPadUp) != 0) return PadAction.Up;
        if ((buttons & DPadDown) != 0) return PadAction.Down;
        if ((buttons & DPadLeft) != 0) return PadAction.Left;
        if ((buttons & DPadRight) != 0) return PadAction.Right;

        // Stick: die stärker ausgelenkte Achse gewinnt. XInput zählt Y nach oben positiv.
        if (Math.Abs((int)thumbX) < StickDeadZone && Math.Abs((int)thumbY) < StickDeadZone)
        {
            return null;
        }

        return Math.Abs((int)thumbX) > Math.Abs((int)thumbY)
            ? thumbX > 0 ? PadAction.Right : PadAction.Left
            : thumbY > 0 ? PadAction.Up : PadAction.Down;
    }
}
