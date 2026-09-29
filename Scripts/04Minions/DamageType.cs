/// <summary>
/// Every damage type an ability can deal. One entry per row of the
/// MinionData workbook's DamageTypes sheet, and one Dmg_&lt;name&gt; column
/// per entry on each faction's _Data sheet — names must match exactly.
///
/// Physical types are reduced by the target's Fortitude, Magical ones by
/// its Resistance (see IsPhysical). To add a type: add the DamageTypes row,
/// the Dmg_ column, and an entry here — appended at the end, since assets
/// store these as numbers.
/// </summary>
public enum DamageType
{
    Slash,
    Pierce,
    Blunt,
    Fire,
    Cold,
    Lightning,
    Poison,
    Holy,
    Unholy,
}

public static class DamageTypeExtensions
{
    /// <summary>Mirrors the Category column on the DamageTypes sheet.</summary>
    public static bool IsPhysical(this DamageType type) =>
        type == DamageType.Slash || type == DamageType.Pierce || type == DamageType.Blunt;
}
