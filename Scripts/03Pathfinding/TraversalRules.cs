/// <summary>
/// What a minion is able to move over.
///
/// Each capability has two distinct rule sets:
///   CanPathOn  — tiles A* will intentionally route through.
///   CanOccupy  — tiles the minion can physically be on, including hazardous
///                ones it would never choose. Used only for escape routing
///                when a minion has been pushed somewhere unsafe.
/// </summary>
public enum TraversalCapability
{
    LandOnly,       // Land only
    LandWater,      // Land + Water
    LandLava,       // Land + Lava
    LandWaterLava,  // Land + Water + Lava
    Flying,         // Flies over everything non-solid; takes no hazard damage
}

/// <summary>
/// Single source of truth for movement rules.
///
/// GridPathfinder and ConnectivityRegistry BOTH call into here. That is the
/// point: if connectivity says two cells are connected, the pathfinder is
/// guaranteed to find a route, because they are applying identical rules to
/// identical data. Never duplicate these checks elsewhere.
///
/// Diagonal movement uses strict no-corner-cutting — see IsDiagonalLegal.
/// </summary>
public static class TraversalRules
{
    // ── Neighbour offsets ──────────────────────────────────────────────

    /// <summary>Four orthogonal directions. Used by room/territory grouping.</summary>
    public static readonly (int dx, int dy)[] Orthogonal =
    {
        (0, 1), (0, -1), (1, 0), (-1, 0),
    };

    /// <summary>
    /// Eight directions (orthogonal + diagonal). Used by movement and
    /// connectivity. Diagonals are subject to IsDiagonalLegal.
    /// </summary>
    public static readonly (int dx, int dy)[] AllDirections =
    {
        (0, 1), (0, -1), (1, 0), (-1, 0),
        (1, 1), (1, -1), (-1, 1), (-1, -1),
    };

    // ── Passability ────────────────────────────────────────────────────

    /// <summary>
    /// True if a minion of this capability will intentionally route onto the tile.
    ///
    /// faction is accepted for forward compatibility with doors, which will be
    /// passable to their owner and blocked to others. Currently unused.
    /// </summary>
    public static bool CanPathOn(TileType type, TraversalCapability capability,
                                 FactionID faction = FactionID.Unaligned)
    {
        // Solid rock is impassable to everything, including flyers.
        if (IsSolid(type)) return false;

        bool isWater = type == TileType.Water;
        bool isLava  = type == TileType.Lava;

        // Land (including Bridge, which spans liquid) is open to all capabilities.
        if (!isWater && !isLava) return true;

        return capability switch
        {
            TraversalCapability.LandOnly      => false,
            TraversalCapability.LandWater     => isWater,
            TraversalCapability.LandLava      => isLava,
            TraversalCapability.LandWaterLava => true,
            TraversalCapability.Flying        => true,
            _                                 => false,
        };
    }

    /// <summary>
    /// True if a minion can physically be on the tile, regardless of whether it
    /// would choose to. Liquids are occupiable by everyone — a LandOnly minion
    /// knocked into water must be able to wade out, otherwise it is stuck taking
    /// damage with no legal move.
    /// </summary>
    public static bool CanOccupy(TileType type, TraversalCapability capability,
                                 FactionID faction = FactionID.Unaligned)
    {
        return !IsSolid(type);
    }

    /// <summary>
    /// True if standing here damages a minion of this capability.
    /// Flyers are never harmed; anything that cannot path onto the tile is.
    /// </summary>
    public static bool IsHazardousFor(TileType type, TraversalCapability capability)
    {
        if (capability == TraversalCapability.Flying) return false;
        if (type != TileType.Water && type != TileType.Lava) return false;
        return !CanPathOn(type, capability);
    }

    /// <summary>Solid rock — never occupiable by anything.</summary>
    public static bool IsSolid(TileType type) =>
        type == TileType.Bedrock ||
        type == TileType.Stone   ||
        type == TileType.Wall    ||
        type == TileType.Gold    ||
        type == TileType.Gem;

    // ── Diagonal legality ──────────────────────────────────────────────

    /// <summary>
    /// Strict no-corner-cutting.
    ///
    /// A diagonal step is legal only if BOTH orthogonal intermediates are
    /// passable. Minions are smaller than their tile, so they move freely
    /// through open diagonal space, but a wall occupies its whole tile and
    /// cannot be clipped past.
    ///
    /// Given walkable = 1, wall = 0 arranged [1,0 ; 0,1], both intermediates
    /// are wall, so the two walkable cells are NOT diagonally connected —
    /// rounding an obstacle takes three orthogonal steps.
    ///
    /// Pass the same predicate used for the move (path or occupy) so escape
    /// routing and normal routing stay consistent.
    /// </summary>
    public static bool IsDiagonalLegal(int dx, int dy,
                                       TileType orthoA, TileType orthoB,
                                       TraversalCapability capability,
                                       bool useOccupyRules,
                                       FactionID faction = FactionID.Unaligned)
    {
        // Orthogonal steps are always legal — nothing to cut.
        if (dx == 0 || dy == 0) return true;

        bool aOk = useOccupyRules
            ? CanOccupy(orthoA, capability, faction)
            : CanPathOn(orthoA, capability, faction);

        bool bOk = useOccupyRules
            ? CanOccupy(orthoB, capability, faction)
            : CanPathOn(orthoB, capability, faction);

        return aOk && bOk;
    }
}
