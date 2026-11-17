# Notes for Claude

## Data workbooks are owned by the user

Never modify, regenerate, overwrite or commit these files. The user edits them
by hand in Excel; changes to them come only from the user's own commits.

- `Assets/Data/MinionData.xlsx`
- `Assets/Data/RoomData.xlsx`
- `Assets/Data/Announcements.xlsx`
- `Assets/Data/SpellData.xlsx`
- `Assets/Data/Campaigns.xlsx`

Only edit one when the user explicitly asks for that edit in the conversation.
If code needs a new column or sheet, change the parser/importer to tolerate its
absence and tell the user what to add, rather than editing the workbook.

## Compile check

Unity isn't available in cloud sessions. Before pushing script changes, run

    Tools/CompileCheck/check.sh

It compiles all of Assets/Scripts against Unity 2021.3 reference assemblies
(NuGet) plus stand-ins for packages and the editor API (Tools/CompileCheck/Stubs).
Needs the .NET 8 SDK. If it reports a missing package member that the real
package has, add it to the stubs rather than changing game code.
