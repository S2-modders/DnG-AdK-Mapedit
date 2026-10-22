using System;
using System.Collections.Generic;
using System.Linq;

# nullable enable

namespace DnG_AdK_Mapedit
{
    using HexPath = List<(HexCoord Coord, int Heading)>;

    public readonly struct HexCoord(int r, int c) : IEquatable<HexCoord>
    {
        public int R { get; } = r;
        public int C { get; } = c;

        public bool Equals(HexCoord other) => R == other.R && C == other.C;
        public override bool Equals(object? obj) => obj is HexCoord other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(R, C);
        public override string ToString() => $"({R}, {C})";

        public static bool operator ==(HexCoord left, HexCoord right) => left.Equals(right);
        public static bool operator !=(HexCoord left, HexCoord right) => !left.Equals(right);
    }

    /// <summary>
    /// Multi-path hex pathfinder that minimizes the TOTAL cost of all connections.
    ///
    /// Rules
    ///  - Land (height >= LandThreshold) is impassable. Hexes merely adjacent to land are passable (extra cost).
    ///  - The start/target hexes of other connections are impassable.
    ///  - Paths may not share hexes.
    ///
    /// Costs (per entered hex)
    ///  +1  base
    ///  +1  turning (heading differs from the previous step)
    ///  +2  height above ShallowWaterThreshold, or adjacent to land
    ///  +1  additional, if adjacent to land
    ///  +2  near the map edge
    ///  +12 within 1 hex of another connection's start/target (never the connection's own)
    ///
    /// Strategy
    ///  1. Greedy sequential routing with several connection orderings (original, shortest-first,
    ///     longest-first, random restarts, failed-first) - keeps the best.
    ///  2. Negotiated congestion (PathFinder): all paths are routed allowing overlap with a rising
    ///     present-congestion cost plus accumulated history cost until no overlaps remain. If it does not
    ///     converge, the result is legalized by keeping a conflict-free subset and re-routing the rest.
    ///  3. Polish the better result with single-path rip-up-and-reroute, then pairwise rip-up-and-reroute
    ///     (re-route two interacting paths together in both orders, accept if their combined cost drops).
    ///
    /// Candidates are compared by (number of failed connections, total path cost).
    /// </summary>
    public class HexPathfinder
    {
        private const int LandThreshold = -100;
        private const int ShallowWaterThreshold = -4000;
        private const int ForeignEndpointPenalty = 12;
        private const long FailurePenalty = 1_000_000;

        // Negotiated congestion tuning
        private const double InitialPresentFactor = 0.5;
        private const double PresentFactorGrowth = 1.5;
        private const double HistoryIncrement = 1.0;

        // Direction offsets for Odd-R Hex Grid (0: East, 1: SE, 2: SW, 3: West, 4: NW, 5: NE)
        private static readonly (int dr, int dc)[][] OddRDirections =
        [
            // Even rows (R % 2 == 0)
            [(0, 1), (1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0)],
            // Odd rows (R % 2 != 0)
            [(0, 1), (1, 1), (1, 0), (0, -1), (-1, 0), (-1, 1)]
        ];

        public static HexCoord GetNeighbor(HexCoord hex, int dir)
        {
            int rowParity = Math.Abs(hex.R) % 2;
            var (dr, dc) = OddRDirections[rowParity][dir];
            return new HexCoord(hex.R + dr, hex.C + dc);
        }

        public static int GetDistance(HexCoord a, HexCoord b)
        {
            int qA = a.C - (a.R - (Math.Abs(a.R) % 2)) / 2;
            int rA = a.R;
            int sA = -qA - rA;

            int qB = b.C - (b.R - (Math.Abs(b.R) % 2)) / 2;
            int rB = b.R;
            int sB = -qB - rB;

            return (Math.Abs(qA - qB) + Math.Abs(rA - rB) + Math.Abs(sA - sB)) / 2;
        }

        /// <summary>
        /// A hex is land if its height is at or above the land threshold.
        /// Hexes merely adjacent to land are NOT land (they only get a cost penalty).
        /// </summary>
        private static bool IsLand(int[,] heights, HexCoord hex) => heights[hex.R, hex.C] >= LandThreshold;

        // ------------------------------------------------------------------------------------------
        // Public entry point
        // ------------------------------------------------------------------------------------------

        /// <param name="connections">Source/target hex of every connection.</param>
        /// <param name="heights">Height map [row, col].</param>
        /// <param name="maxRerouteIterations">Max passes of single-path rip-up-and-reroute polishing.</param>
        /// <param name="randomRestarts">Number of extra random connection orderings for the greedy stage.</param>
        /// <param name="maxNegotiationIterations">Max iterations of the negotiated congestion stage.</param>
        /// <param name="maxPairwisePasses">Max passes of pairwise rip-up-and-reroute (0 disables it).</param>
        /// <param name="seed">Seed for the random orderings (deterministic results).</param>
        /// <returns>One path per connection (same order). An empty list means no route was found.</returns>
        public static List<List<(HexCoord Coord, int Heading)>> SolveMultiPathfinding(
            List<(HexCoord Source, HexCoord Target)> connections,
            int[,] heights,
            int maxRerouteIterations = 100,
            int randomRestarts = 8,
            int maxNegotiationIterations = 40,
            int maxPairwisePasses = 3,
            int seed = 12345)
        {
            int n = connections.Count;
            if (n == 0) return [];

            var ctx = new RoutingContext(connections, heights);

            // ---- Stage 1: greedy routing with several orderings ----
            var orders = BuildOrders(ctx, randomRestarts, seed);

            Candidate best = MakeCandidate(ctx, GreedyRoute(ctx, orders[0]));
            for (int k = 1; k < orders.Count; k++)
            {
                var candidate = MakeCandidate(ctx, GreedyRoute(ctx, orders[k]));
                if (candidate.Score < best.Score) best = candidate;
            }

            // Failed connections are moved to the front of the order and the greedy pass is repeated.
            for (int k = 0; k < 3 && best.Failed > 0; k++)
            {
                var previous = best;
                int[] failedFirst = Enumerable.Range(0, n)
                    .OrderBy(i => previous.Paths[i].Count == 0 ? 0 : 1)
                    .ToArray();

                var candidate = MakeCandidate(ctx, GreedyRoute(ctx, failedFirst));
                if (candidate.Score >= best.Score) break;
                best = candidate;
            }

            // ---- Stage 2: negotiated congestion ----
            HexPath[] negotiated = NegotiatedRoute(ctx, maxNegotiationIterations);
            Candidate negotiatedCandidate = MakeCandidate(ctx, Legalize(ctx, negotiated));

            // ---- Stage 3: polish ----
            Polish(ctx, best.Paths, maxRerouteIterations, 0);
            best = MakeCandidate(ctx, best.Paths);

            Polish(ctx, negotiatedCandidate.Paths, maxRerouteIterations, 0);
            negotiatedCandidate = MakeCandidate(ctx, negotiatedCandidate.Paths);

            if (negotiatedCandidate.Score < best.Score) best = negotiatedCandidate;

            Polish(ctx, best.Paths, maxRerouteIterations, maxPairwisePasses);

            return [.. best.Paths];
        }

        // ------------------------------------------------------------------------------------------
        // Shared data
        // ------------------------------------------------------------------------------------------

        private sealed class RoutingContext
        {
            public List<(HexCoord Source, HexCoord Target)> Connections { get; }
            public int[,] Heights { get; }
            public int Rows { get; }
            public int Cols { get; }
            public int[,] StaticCosts { get; }

            /// <summary>Start/target hexes of every connection.</summary>
            public HashSet<HexCoord> AllEndpoints { get; } = [];

            /// <summary>Per connection: hexes (start/target of OTHER connections + their neighbors) with +12.</summary>
            public HashSet<HexCoord>[] ForeignPenaltyHexes { get; }

            public RoutingContext(List<(HexCoord Source, HexCoord Target)> connections, int[,] heights)
            {
                Connections = connections;
                Heights = heights;
                Rows = heights.GetLength(0);
                Cols = heights.GetLength(1);
                StaticCosts = PrecomputeStaticCellCosts(heights, Rows, Cols);

                foreach (var (source, target) in connections)
                {
                    AllEndpoints.Add(source);
                    AllEndpoints.Add(target);
                }

                ForeignPenaltyHexes = new HashSet<HexCoord>[connections.Count];
                for (int i = 0; i < connections.Count; i++)
                {
                    ForeignPenaltyHexes[i] = BuildForeignPenaltyHexes(
                        connections[i].Source, connections[i].Target, AllEndpoints, Rows, Cols);
                }
            }

            public bool InBounds(HexCoord hex) => hex.R >= 0 && hex.R < Rows && hex.C >= 0 && hex.C < Cols;
        }

        private sealed class Candidate(HexPath[] paths, long score, int failed)
        {
            public HexPath[] Paths { get; } = paths;
            public long Score { get; } = score;
            public int Failed { get; } = failed;
        }

        private static Candidate MakeCandidate(RoutingContext ctx, HexPath[] paths)
        {
            long score = 0;
            int failed = 0;
            for (int i = 0; i < paths.Length; i++)
            {
                score += ScorePath(ctx, i, paths[i]);
                if (paths[i].Count == 0) failed++;
            }
            return new Candidate(paths, score, failed);
        }

        // ------------------------------------------------------------------------------------------
        // Stage 1: greedy sequential routing
        // ------------------------------------------------------------------------------------------

        private static List<int[]> BuildOrders(RoutingContext ctx, int randomRestarts, int seed)
        {
            int n = ctx.Connections.Count;
            int[] identity = Enumerable.Range(0, n).ToArray();

            int Dist(int i) => GetDistance(ctx.Connections[i].Source, ctx.Connections[i].Target);

            List<int[]> orders =
            [
                identity,
                identity.OrderBy(Dist).ToArray(),
                identity.OrderByDescending(Dist).ToArray()
            ];

            var rng = new Random(seed);
            for (int k = 0; k < randomRestarts; k++)
            {
                int[] order = (int[])identity.Clone();
                for (int i = n - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (order[i], order[j]) = (order[j], order[i]);
                }
                orders.Add(order);
            }

            return orders;
        }

        private static HexPath[] GreedyRoute(RoutingContext ctx, int[] order)
        {
            var paths = new HexPath[ctx.Connections.Count];
            HashSet<HexCoord> reserved = [];

            foreach (int i in order)
            {
                var path = FindPath(ctx, i, reserved, null, null, 0);
                paths[i] = path;
                Reserve(ctx, path, reserved);
            }

            return paths;
        }

        // ------------------------------------------------------------------------------------------
        // Stage 2: negotiated congestion (PathFinder)
        // ------------------------------------------------------------------------------------------

        private static HexPath[] NegotiatedRoute(RoutingContext ctx, int maxIterations)
        {
            int n = ctx.Connections.Count;
            var paths = new HexPath[n];
            for (int i = 0; i < n; i++) paths[i] = [];

            int[,] occupancy = new int[ctx.Rows, ctx.Cols];
            double[,] history = new double[ctx.Rows, ctx.Cols];
            double presentFactor = InitialPresentFactor;

            for (int iter = 0; iter < maxIterations; iter++)
            {
                for (int i = 0; i < n; i++)
                {
                    AddOccupancy(ctx, paths[i], occupancy, -1);
                    paths[i] = FindPath(ctx, i, null, occupancy, history, presentFactor);
                    AddOccupancy(ctx, paths[i], occupancy, +1);
                }

                // Find hexes used by more than one path and make them more expensive in the future.
                HashSet<HexCoord> overused = [];
                foreach (var path in paths)
                {
                    foreach (var (coord, _) in path)
                    {
                        if (!ctx.AllEndpoints.Contains(coord) && occupancy[coord.R, coord.C] > 1)
                            overused.Add(coord);
                    }
                }

                if (overused.Count == 0) break;

                foreach (var hex in overused)
                {
                    history[hex.R, hex.C] += HistoryIncrement * (occupancy[hex.R, hex.C] - 1);
                }
                presentFactor *= PresentFactorGrowth;
            }

            return paths;
        }

        /// <summary>
        /// Turns a possibly overlapping solution into a legal one: paths with the fewest conflicts are
        /// kept as long as they don't hit an already kept path, the others are re-routed with hard reservation.
        /// </summary>
        private static HexPath[] Legalize(RoutingContext ctx, HexPath[] paths)
        {
            int n = paths.Length;
            int[,] occupancy = new int[ctx.Rows, ctx.Cols];
            foreach (var path in paths) AddOccupancy(ctx, path, occupancy, +1);

            int ConflictCount(int i) => paths[i].Count(step =>
                !ctx.AllEndpoints.Contains(step.Coord) && occupancy[step.Coord.R, step.Coord.C] > 1);

            int[] order = Enumerable.Range(0, n).OrderBy(ConflictCount).ThenBy(i => i).ToArray();

            var result = new HexPath[n];
            HashSet<HexCoord> reserved = [];

            foreach (int i in order)
            {
                var path = paths[i];

                // Count == 0: no route even when overlap was allowed, so there is none at all.
                if (path.Count >= 2 && path.Any(step => reserved.Contains(step.Coord)))
                {
                    path = FindPath(ctx, i, reserved, null, null, 0);
                }

                result[i] = path;
                Reserve(ctx, path, reserved);
            }

            return result;
        }

        // ------------------------------------------------------------------------------------------
        // Stage 3: polishing
        // ------------------------------------------------------------------------------------------

        private static void Polish(RoutingContext ctx, HexPath[] paths, int maxRerouteIterations, int pairwisePasses)
        {
            HashSet<HexCoord> reserved = [];
            foreach (var path in paths) Reserve(ctx, path, reserved);

            RerouteSingles(ctx, paths, reserved, maxRerouteIterations);

            if (pairwisePasses <= 0 || paths.Length < 2) return;

            // Connections that cannot be routed even on an empty map are never worth retrying.
            bool[] impossible = new bool[paths.Length];
            for (int i = 0; i < paths.Length; i++)
            {
                if (paths[i].Count == 0)
                    impossible[i] = FindPath(ctx, i, null, null, null, 0).Count == 0;
            }

            for (int pass = 0; pass < pairwisePasses; pass++)
            {
                if (!ReroutePairs(ctx, paths, reserved, impossible)) break;
                RerouteSingles(ctx, paths, reserved, maxRerouteIterations);
            }
        }

        /// <summary>Rip up each path alone and re-route it; keep it if strictly cheaper.</summary>
        private static void RerouteSingles(RoutingContext ctx, HexPath[] paths, HashSet<HexCoord> reserved, int maxIterations)
        {
            bool improved = true;
            int iter = 0;

            while (improved && iter < maxIterations)
            {
                improved = false;
                iter++;

                for (int i = 0; i < paths.Length; i++)
                {
                    var oldPath = paths[i];
                    Release(ctx, oldPath, reserved);

                    var newPath = FindPath(ctx, i, reserved, null, null, 0);
                    if (ScorePath(ctx, i, newPath) < ScorePath(ctx, i, oldPath))
                    {
                        paths[i] = newPath;
                        improved = true;
                    }

                    Reserve(ctx, paths[i], reserved);
                }
            }
        }

        /// <summary>
        /// Rip up two interacting paths together and re-route them in both orders; accept the best
        /// arrangement if their combined cost drops. Returns true if anything improved.
        /// </summary>
        private static bool ReroutePairs(RoutingContext ctx, HexPath[] paths, HashSet<HexCoord> reserved, bool[] impossible)
        {
            bool any = false;

            for (int i = 0; i < paths.Length; i++)
            {
                for (int j = i + 1; j < paths.Length; j++)
                {
                    if (impossible[i] || impossible[j]) continue;

                    bool eligible = paths[i].Count == 0 || paths[j].Count == 0 || PathsInteract(paths[i], paths[j]);
                    if (!eligible) continue;

                    var oldI = paths[i];
                    var oldJ = paths[j];
                    long oldScore = ScorePath(ctx, i, oldI) + ScorePath(ctx, j, oldJ);

                    Release(ctx, oldI, reserved);
                    Release(ctx, oldJ, reserved);

                    // Order 1: i first, then j.
                    var (iFirst, jSecond) = RoutePair(ctx, i, j, reserved);
                    long score1 = ScorePath(ctx, i, iFirst) + ScorePath(ctx, j, jSecond);

                    // Order 2: j first, then i.
                    var (jFirst, iSecond) = RoutePair(ctx, j, i, reserved);
                    long score2 = ScorePath(ctx, i, iSecond) + ScorePath(ctx, j, jFirst);

                    if (score1 < oldScore && score1 <= score2)
                    {
                        paths[i] = iFirst;
                        paths[j] = jSecond;
                        any = true;
                    }
                    else if (score2 < oldScore)
                    {
                        paths[i] = iSecond;
                        paths[j] = jFirst;
                        any = true;
                    }

                    Reserve(ctx, paths[i], reserved);
                    Reserve(ctx, paths[j], reserved);
                }
            }

            return any;
        }

        /// <summary>Routes 'first' then 'second' with hard reservation. 'reserved' is left unchanged.</summary>
        private static (HexPath First, HexPath Second) RoutePair(
            RoutingContext ctx, int first, int second, HashSet<HexCoord> reserved)
        {
            var firstPath = FindPath(ctx, first, reserved, null, null, 0);
            Reserve(ctx, firstPath, reserved);
            var secondPath = FindPath(ctx, second, reserved, null, null, 0);
            Release(ctx, firstPath, reserved);
            return (firstPath, secondPath);
        }

        /// <summary>True if the two paths come within 2 hexes of each other.</summary>
        private static bool PathsInteract(HexPath a, HexPath b)
        {
            HashSet<HexCoord> near = [];
            foreach (var (coord, _) in b)
            {
                near.Add(coord);
                for (int d = 0; d < 6; d++) near.Add(GetNeighbor(coord, d));
            }

            foreach (var (coord, _) in a)
            {
                if (near.Contains(coord)) return true;
                for (int d = 0; d < 6; d++)
                {
                    if (near.Contains(GetNeighbor(coord, d))) return true;
                }
            }

            return false;
        }

        // ------------------------------------------------------------------------------------------
        // Reservation helpers (start/target hexes are never reserved: they are blocked for foreign paths anyway)
        // ------------------------------------------------------------------------------------------

        private static void Reserve(RoutingContext ctx, HexPath path, HashSet<HexCoord> reserved)
        {
            foreach (var (coord, _) in path)
            {
                if (!ctx.AllEndpoints.Contains(coord)) reserved.Add(coord);
            }
        }

        private static void Release(RoutingContext ctx, HexPath path, HashSet<HexCoord> reserved)
        {
            foreach (var (coord, _) in path)
            {
                if (!ctx.AllEndpoints.Contains(coord)) reserved.Remove(coord);
            }
        }

        private static void AddOccupancy(RoutingContext ctx, HexPath path, int[,] occupancy, int delta)
        {
            foreach (var (coord, _) in path)
            {
                if (!ctx.AllEndpoints.Contains(coord)) occupancy[coord.R, coord.C] += delta;
            }
        }

        // ------------------------------------------------------------------------------------------
        // Single path search
        // ------------------------------------------------------------------------------------------

        /// <summary>Cost of entering a hex, without turn cost: base + static cost + foreign endpoint penalty.</summary>
        private static int StepCost(RoutingContext ctx, int index, HexCoord hex)
        {
            int cost = 1 + ctx.StaticCosts[hex.R, hex.C];
            if (ctx.ForeignPenaltyHexes[index].Contains(hex)) cost += ForeignEndpointPenalty;
            return cost;
        }

        /// <summary>
        /// A* over (hex, heading) states.
        /// Hard mode: pass 'hardReserved' (hexes of other paths are blocked), occupancy/history = null.
        /// Soft mode (negotiated congestion): pass hardReserved = null plus occupancy/history; overlapping
        /// other paths is allowed but costs more.
        /// Land, other connections' start/target hexes and the path's own start are always blocked.
        /// </summary>
        private static HexPath FindPath(
            RoutingContext ctx,
            int index,
            HashSet<HexCoord>? hardReserved,
            int[,]? occupancy,
            double[,]? history,
            double presentFactor)
        {
            var (source, target) = ctx.Connections[index];

            var openSet = new PriorityQueue<(HexCoord Coord, int Heading), double>();
            var gScore = new Dictionary<(HexCoord Coord, int Heading), double>();
            var parent = new Dictionary<(HexCoord Coord, int Heading), (HexCoord Coord, int Heading)>();
            var closed = new HashSet<(HexCoord Coord, int Heading)>();

            (HexCoord Coord, int Heading) startState = (source, -1);
            gScore[startState] = 0;
            openSet.Enqueue(startState, GetDistance(source, target));

            (HexCoord Coord, int Heading) endState = (target, -1);
            bool found = false;

            while (openSet.TryDequeue(out var current, out _))
            {
                if (!closed.Add(current)) continue;

                if (current.Coord == target)
                {
                    endState = current;
                    found = true;
                    break;
                }

                double currentG = gScore[current];

                for (int dir = 0; dir < 6; dir++)
                {
                    HexCoord neighbor = GetNeighbor(current.Coord, dir);

                    if (!ctx.InBounds(neighbor))
                        continue;

                    // Never re-enter our own start.
                    if (neighbor == source)
                        continue;

                    // Our own target is always enterable (e.g. a port on the coast or on land).
                    if (neighbor != target)
                    {
                        // Land is impassable (hexes merely adjacent to land are fine).
                        if (IsLand(ctx.Heights, neighbor))
                            continue;

                        // Starts/exits of other connections are impassable.
                        if (ctx.AllEndpoints.Contains(neighbor))
                            continue;

                        // Hexes occupied by other paths are impassable (hard mode).
                        if (hardReserved != null && hardReserved.Contains(neighbor))
                            continue;
                    }

                    double moveCost = StepCost(ctx, index, neighbor);

                    if (history != null)
                    {
                        moveCost += history[neighbor.R, neighbor.C];
                    }
                    if (occupancy != null && !ctx.AllEndpoints.Contains(neighbor))
                    {
                        moveCost *= 1.0 + presentFactor * occupancy[neighbor.R, neighbor.C];
                    }

                    if (current.Heading != -1 && current.Heading != dir)
                    {
                        moveCost += 1;
                    }

                    double tentativeG = currentG + moveCost;
                    (HexCoord Coord, int Heading) nextState = (neighbor, dir);

                    if (closed.Contains(nextState))
                        continue;

                    if (!gScore.TryGetValue(nextState, out double existingG) || tentativeG < existingG)
                    {
                        gScore[nextState] = tentativeG;
                        parent[nextState] = current;
                        openSet.Enqueue(nextState, tentativeG + GetDistance(neighbor, target));
                    }
                }
            }

            HexPath path = [];
            if (!found) return path;

            var curr = endState;
            while (curr.Heading != -1)
            {
                path.Add(curr);
                curr = parent[curr];
            }
            path.Add((source, -1));
            path.Reverse();

            return path;
        }

        /// <summary>
        /// Real cost of a path (no congestion terms). Empty path (no route) = FailurePenalty.
        /// </summary>
        private static long ScorePath(RoutingContext ctx, int index, HexPath path)
        {
            if (path.Count == 0) return FailurePenalty;

            long total = 0;
            for (int i = 1; i < path.Count; i++)
            {
                var (coord, heading) = path[i];
                int prevHeading = path[i - 1].Heading;

                long moveCost = StepCost(ctx, index, coord);
                if (prevHeading != -1 && prevHeading != heading)
                {
                    moveCost += 1;
                }
                total += moveCost;
            }
            return total;
        }

        // ------------------------------------------------------------------------------------------
        // Static cost precomputation
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// Hexes within 1 of the start/target of OTHER connections (excluding this connection's own
        /// start and target). These get +ForeignEndpointPenalty when entered.
        /// </summary>
        private static HashSet<HexCoord> BuildForeignPenaltyHexes(
            HexCoord ownSource,
            HexCoord ownTarget,
            HashSet<HexCoord> allEndpoints,
            int rows,
            int cols)
        {
            HashSet<HexCoord> result = [];
            foreach (var ep in allEndpoints)
            {
                if (ep == ownSource || ep == ownTarget) continue;

                result.Add(ep);
                for (int d = 0; d < 6; d++)
                {
                    HexCoord nbr = GetNeighbor(ep, d);
                    if (nbr.R >= 0 && nbr.R < rows && nbr.C >= 0 && nbr.C < cols)
                        result.Add(nbr);
                }
            }
            return result;
        }

        /// <summary>Connection-independent cell costs (everything except turning and the foreign endpoint penalty).</summary>
        private static int[,] PrecomputeStaticCellCosts(int[,] heights, int rows, int cols)
        {
            int[,] costs = new int[rows, cols];

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    HexCoord current = new(r, c);
                    int h = heights[r, c];
                    int cellCost = 0;

                    bool isAdjToLand = false;
                    for (int d = 0; d < 6; d++)
                    {
                        HexCoord nbr = GetNeighbor(current, d);
                        if (nbr.R >= 0 && nbr.R < rows && nbr.C >= 0 && nbr.C < cols)
                        {
                            if (heights[nbr.R, nbr.C] >= LandThreshold)
                            {
                                isAdjToLand = true;
                                break;
                            }
                        }
                    }

                    if (h > ShallowWaterThreshold || isAdjToLand)
                    {
                        cellCost += 2;
                    }

                    if (isAdjToLand)
                    {
                        cellCost += 1;
                    }

                    if (r <= 1 || r >= rows - 2 || c <= 1 || c >= cols - 2)
                    {
                        cellCost += 2;
                    }

                    costs[r, c] = cellCost;
                }
            }

            return costs;
        }
    }
}