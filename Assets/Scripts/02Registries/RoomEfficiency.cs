using System;
using System.Collections.Generic;

/// <summary>
/// How well-built a room is, as one number that scales what the room does:
/// a Treasury's gold capacity, how fast minions sleep in a Lair or train in a
/// Training Room, and so on (see DungeonRoom.Efficiency / Capacity).
///
///   efficiency = base + shapeWeight × Shape + wallWeight × WallCoverage
///
/// with base and the two weights set per room type in the RoomData workbook.
///
/// Shape (0–1) — compact AND rectangular scores 1:
///   Compactness    = least perimeter any n-tile room could have / actual perimeter
///                    (a 3x3 is 1; a 1x9 corridor is 0.6; a snake is lower still)
///   Rectangularity = tiles / bounding-box area
///                    (a 3x3 is 1; a 3x3 missing a corner is 0.89)
///   Shape          = Compactness × Rectangularity
///
/// WallCoverage (0–1) — share of the room's outer edges that are the owner's
/// own reinforced Wall. Edges onto tunnel, other rooms, rock or anything else
/// don't count. (Doors will count as wall once they exist.)
///
/// Pure arithmetic over tile coordinates, recomputed whenever rooms rebake —
/// i.e. whenever any tile changes, walls included. Must stay in step with the
/// RoomData workbook's EfficiencyFormula sheet.
/// </summary>
public static class RoomEfficiency
{
    public readonly struct Measurement
    {
        public readonly int Tiles;
        /// <summary>Edges between a room tile and a non-room tile.</summary>
        public readonly int Perimeter;
        /// <summary>Least perimeter any room of this many tiles can have: 2⌈2√n⌉.</summary>
        public readonly int MinPerimeter;
        public readonly int BoundsArea;
        /// <summary>Perimeter edges that face the owner's reinforced wall.</summary>
        public readonly int WallEdges;

        public Measurement(int tiles, int perimeter, int minPerimeter, int boundsArea, int wallEdges)
        {
            Tiles        = tiles;
            Perimeter    = perimeter;
            MinPerimeter = minPerimeter;
            BoundsArea   = boundsArea;
            WallEdges    = wallEdges;
        }

        public float Compactness    => Perimeter  > 0 ? Math.Min(1f, MinPerimeter / (float)Perimeter) : 1f;
        public float Rectangularity => BoundsArea > 0 ? Tiles / (float)BoundsArea : 1f;
        public float Shape          => Compactness * Rectangularity;
        public float WallCoverage   => Perimeter  > 0 ? WallEdges / (float)Perimeter : 0f;

        public override string ToString() =>
            $"{Tiles} tiles, perimeter {Perimeter} (min {MinPerimeter}), bounds {BoundsArea}, " +
            $"{WallEdges} wall edges → shape {Shape:0.##}, walls {WallCoverage:0.##}";
    }

    private static readonly (int dx, int dy)[] Neighbours = { (0, 1), (0, -1), (1, 0), (-1, 0) };

    /// <summary>
    /// Measures a room given its tiles, what counts as part of it, and what
    /// counts as its wall. Coordinates outside the grid should report false
    /// from both predicates.
    /// </summary>
    public static Measurement Measure(IReadOnlyCollection<(int x, int y)> tiles,
                                      Func<int, int, bool> isRoomTile,
                                      Func<int, int, bool> isOwnWall)
    {
        int n = tiles.Count;
        if (n == 0) return new Measurement(0, 0, 0, 0, 0);

        int perimeter = 0, wallEdges = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        foreach (var (x, y) in tiles)
        {
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;

            foreach (var (dx, dy) in Neighbours)
            {
                int nx = x + dx, ny = y + dy;
                if (isRoomTile(nx, ny)) continue;
                perimeter++;
                if (isOwnWall(nx, ny)) wallEdges++;
            }
        }

        int boundsArea = (maxX - minX + 1) * (maxY - minY + 1);
        return new Measurement(n, perimeter, MinPerimeter(n), boundsArea, wallEdges);
    }

    /// <summary>The smallest perimeter any n-tile room can have: 2⌈2√n⌉ (exact, integer-only).</summary>
    public static int MinPerimeter(int tiles)
    {
        if (tiles <= 0) return 0;
        int k = (int)Math.Ceiling(2.0 * Math.Sqrt(tiles));
        while ((long)(k - 1) * (k - 1) >= 4L * tiles) k--;   // guard against float error
        while ((long)k * k < 4L * tiles) k++;
        return 2 * k;
    }

    /// <summary>base + shapeWeight × Shape + wallWeight × WallCoverage, never below 0.</summary>
    public static float Efficiency(in Measurement m, float baseEfficiency, float shapeWeight, float wallWeight) =>
        Math.Max(0f, baseEfficiency + shapeWeight * m.Shape + wallWeight * m.WallCoverage);
}
