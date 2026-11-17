Campaign mission levels — one folder per Underworld campaign (01U … 26U).

Put each mission's level here as  <CampaignID>/<LevelId>.json,
where LevelId is the mission's LevelId in Assets/Data/Campaigns.xlsx
(e.g. 01U/01U_M01.json, 04U/04U_C01.json).

Make a level in the level editor, save it, then copy the saved .json here
under the mission's name (from StreamingAssets/Levels, or your CustomLevels
folder if you used Save). Then run Sunder > Import Campaigns: it links the
level to its mission and checks it (its id, and that the human player plays
this campaign's faction).

These levels are packed with the game. The campaign only ever plays these —
a custom level with the same id never replaces them, and a finished game's
level editor won't save under a campaign mission's id.
