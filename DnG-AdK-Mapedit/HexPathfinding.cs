using System;
using System.Collections.Generic;
using System.Linq;

# nullable enable

namespace DnG_AdK_Mapedit
{
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

    public class HexPathfinder
    {
        private const int LandThreshold = -100;
        private const int ShallowWaterThreshold = -4000;

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

        public static List<List<(HexCoord Coord, int Heading)>> SolveMultiPathfinding(
            List<(HexCoord Source, HexCoord Target)> connections,
            int[,] heights,
            int maxRerouteIterations = 100)
        {
            int rows = heights.GetLength(0);
            int cols = heights.GetLength(1);

            int[,] staticCellCosts = PrecomputeStaticCellCosts(heights, connections, rows, cols);

            List<List<(HexCoord Coord, int Heading)>> currentPaths = [];
            HashSet<HexCoord> reservedHexes = [];

            foreach (var (Source, Target) in connections)
            {
                reservedHexes.Add(Source);
                reservedHexes.Add(Target);
            }

            for (int i = 0; i < connections.Count; i++)
            {
                var (Source, Target) = connections[i];
                reservedHexes.Remove(Source);
                reservedHexes.Remove(Target);

                var path = FindSinglePath(Source, Target, staticCellCosts, reservedHexes, rows, cols);
                currentPaths.Add(path);

                foreach (var (Coord, Heading) in path)
                {
                    reservedHexes.Add(Coord);
                }
            }

            bool improved = true;
            int iter = 0;

            while (improved && iter < maxRerouteIterations)
            {
                improved = false;
                iter++;

                for (int i = 0; i < connections.Count; i++)
                {
                    var (Source, Target) = connections[i];
                    var oldPath = currentPaths[i];

                    foreach (var (Coord, Heading) in oldPath)
                    {
                        if (!connections.Any(c => c.Source == Coord || c.Target == Coord))
                        {
                            reservedHexes.Remove(Coord);
                        }
                    }

                    var newPath = FindSinglePath(Source, Target, staticCellCosts, reservedHexes, rows, cols);

                    int oldCost = CalculatePathCost(oldPath, staticCellCosts);
                    int newCost = CalculatePathCost(newPath, staticCellCosts);

                    if (newPath.Count > 0 && newCost < oldCost)
                    {
                        currentPaths[i] = newPath;
                        improved = true;
                        foreach (var (Coord, Heading) in newPath) reservedHexes.Add(Coord);
                    }
                    else
                    {
                        foreach (var (Coord, Heading) in oldPath) reservedHexes.Add(Coord);
                    }
                }
            }

            return currentPaths;
        }

        private static List<(HexCoord Coord, int Heading)> FindSinglePath(
            HexCoord source,
            HexCoord target,
            int[,] staticCosts,
            HashSet<HexCoord> reservedHexes,
            int rows,
            int cols)
        {
            var openSet = new PriorityQueue<(HexCoord Coord, int Heading), int>();
            var gScore = new Dictionary<(HexCoord Coord, int Heading), int>();
            var parent = new Dictionary<(HexCoord Coord, int Heading), (HexCoord Coord, int Heading)>();

            var startState = (source, -1);
            gScore[startState] = 0;
            openSet.Enqueue(startState, GetDistance(source, target));

            (HexCoord Coord, int Heading) endState = (target, -1);
            bool found = false;

            while (openSet.Count > 0)
            {
                var current = openSet.Dequeue();

                if (current.Coord == target)
                {
                    endState = current;
                    found = true;
                    break;
                }

                for (int dir = 0; dir < 6; dir++)
                {
                    HexCoord neighbor = GetNeighbor(current.Coord, dir);

                    if (neighbor.R < 0 || neighbor.R >= rows || neighbor.C < 0 || neighbor.C >= cols)
                        continue;

                    if (neighbor != target && reservedHexes.Contains(neighbor))
                        continue;

                    int moveCost = 1 + staticCosts[neighbor.R, neighbor.C];
                    if (current.Heading != -1 && current.Heading != dir)
                    {
                        moveCost += 1;
                    }

                    int tentativeG = gScore[current] + moveCost;
                    var nextState = (neighbor, dir);

                    if (!gScore.TryGetValue(nextState, out int existingG) || tentativeG < existingG)
                    {
                        gScore[nextState] = tentativeG;
                        parent[nextState] = current;
                        int fScore = tentativeG + GetDistance(neighbor, target);
                        openSet.Enqueue(nextState, fScore);
                    }
                }
            }

            List<(HexCoord Coord, int Heading)> path = [];
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

        private static int[,] PrecomputeStaticCellCosts(
            int[,] heights,
            List<(HexCoord Source, HexCoord Target)> connections,
            int rows,
            int cols)
        {
            int[,] costs = new int[rows, cols];

            HashSet<HexCoord> otherEndpoints = [];
            foreach (var (Source, Target) in connections)
            {
                otherEndpoints.Add(Source);
                otherEndpoints.Add(Target);
            }

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

                    bool nearOtherEndpoint = false;
                    foreach (var ep in otherEndpoints)
                    {
                        if (GetDistance(current, ep) <= 1)
                        {
                            nearOtherEndpoint = true;
                            break;
                        }
                    }
                    if (nearOtherEndpoint)
                    {
                        cellCost += 12;
                    }

                    costs[r, c] = cellCost;
                }
            }

            return costs;
        }

        private static int CalculatePathCost(List<(HexCoord Coord, int Heading)> path, int[,] staticCosts)
        {
            if (path == null || path.Count < 2) return int.MaxValue;

            int totalCost = 0;
            for (int i = 1; i < path.Count; i++)
            {
                var (Coord, Heading) = path[i];
                var prevStep = path[i - 1];

                int moveCost = 1 + staticCosts[Coord.R, Coord.C];
                if (prevStep.Heading != -1 && prevStep.Heading != Heading)
                {
                    moveCost += 1;
                }
                totalCost += moveCost;
            }
            return totalCost;
        }
    }
}