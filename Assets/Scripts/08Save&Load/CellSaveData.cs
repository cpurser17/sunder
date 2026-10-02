/// <summary>
/// Serialisable snapshot of a single grid cell.
/// Moved out of GridManager2D so LevelData and SaveData can both reference it
/// without a dependency on the MonoBehaviour.
/// </summary>
[System.Serializable]
public class CellSaveData
{
    public TileType  tileType;
    public TileType  underlyingType; // Water/Lava preserved beneath a Bridge
    public FactionID owner;

    public CellSaveData() { }

    public CellSaveData(TileType tile, TileType underlying, FactionID owner)
    {
        this.tileType       = tile;
        this.underlyingType = underlying;
        this.owner          = owner;
    }

    public static CellSaveData From(GridCell cell) =>
        new(cell.TileType, cell.UnderlyingType, cell.Owner);
}
