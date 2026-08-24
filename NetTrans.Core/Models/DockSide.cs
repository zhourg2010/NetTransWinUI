namespace NetTrans.Models;

/// <summary>
/// Which edge of the task frame the inspector frame is bonded to -- the
/// handoff's `dock` state, and the side whose corners get squared off
/// (`.bond-r` / `.bond-l` / `.bond-t` / `.bond-b`).
/// </summary>
public enum DockSide
{
    Right,
    Left,
    Bottom,
    Top,
}
