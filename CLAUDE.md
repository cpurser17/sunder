# Notes for Claude

## Data workbooks are owned by the user

Never modify, regenerate, overwrite or commit these files. The user edits them
by hand in Excel; changes to them come only from the user's own commits.

- `Assets/Data/MinionData.xlsx`
- `Assets/Data/RoomData.xlsx`
- `Assets/Data/Announcements.xlsx`

If code needs a new column or sheet, change the parser/importer to tolerate its
absence and tell the user what to add, rather than editing the workbook.
