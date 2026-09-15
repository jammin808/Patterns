namespace Patterns.Core.Model;

/// <summary>
/// The verbs that belong to a role's module (round 64): the arcade's and the room's. Asked
/// before either module is touched, so a role without the module answers "not on this node"
/// without ever compiling a reference to it — a timer's process never loads the arcade or the
/// room. The modules' own predicates delegate here, so each list exists once.
/// </summary>
public static class RoleVerbs
{
    public static bool IsArcade(ShowActionKind kind) => kind is ShowActionKind.ArcadeStart or ShowActionKind.ArcadeStop or ShowActionKind.ArcadePause
        or ShowActionKind.ArcadeResume or ShowActionKind.ArcadeAttract or ShowActionKind.ArcadeKey or ShowActionKind.ArcadeSize or ShowActionKind.ArcadeNdi
        or ShowActionKind.ArcadeName or ShowActionKind.ArcadeWindow;

    public static bool IsPlay(ShowActionKind kind) => kind is ShowActionKind.PlayAdd or ShowActionKind.PlayOpen or ShowActionKind.PlayClose or ShowActionKind.PlayReveal
        or ShowActionKind.PlayShow or ShowActionKind.PlayMessage or ShowActionKind.PlayApprove or ShowActionKind.PlayReject or ShowActionKind.PlayAuto
        or ShowActionKind.PlayPath or ShowActionKind.PlayDraughts or ShowActionKind.PlayRoom or ShowActionKind.PlayExport;
}
