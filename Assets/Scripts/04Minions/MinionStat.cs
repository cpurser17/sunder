/// <summary>
/// Every stat that grows with a minion's level. One entry per row of the
/// MinionData workbook's Stats sheet — the names must match exactly, since
/// the importer maps rows to entries by name.
///
/// To add a stat: add a row to the Stats sheet, add an entry here, then add
/// that stat's rows to each faction's _Levels sheet and re-import. Nothing
/// else in the data pipeline needs to change — stats are stored and looked
/// up by this enum, never as individual fields.
///
/// Append new entries at the end. Assets store these as numbers, so
/// inserting or reordering would silently remap every imported minion's
/// stats until the next import.
/// </summary>
public enum MinionStat
{
    Experience,
    Salary,
    Health,
    Strength,
    Accuracy,
    Dexterity,
    Fortitude,
    Speed,
    Magic,
    Resistance,
    Luck,
    Weight,
    WorkingCost,
    SkillResearch,
    SkillTrain,
    SkillBuild,
    SkillPray,
    SkillTorture,
    Anger,
}
