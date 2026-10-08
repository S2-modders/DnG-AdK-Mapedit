using System;
using System.Collections.Generic;
using System.IO;

namespace DnG_AdK_Mapedit
{
    //Map data that the game reads verbatim and never recomputes on load, so the exporter has to derive it
    internal static class S2mRules
    {
        public const uint Blocked = 0x1;
        public const uint Water = 0x4;
        public const uint MiningGround = 0x10;
        public const uint Cliff = 0x40;
        public const uint Deposit = 0x80;
        public const uint Buildable = 0x200;
        public const uint LogicObject = 0x400;
        public const uint Dock = 0x800;
        public const uint Harbour = 0x2000;
        public const uint HarbourExit = 0x4000;
        public const uint ShipRoute = 0x8000;
        public const uint ShipGround = 0x20000;

        //Pattern flags from patterns.lua
        private static readonly HashSet<uint> BlockedPatterns =
        [
            0x0FADE0FF, 0x680004E4, 0x680004E5, 0x76D31873, 0x7AC44C10, 0x7AC44C11, 0x7AC45E0E, 0x7AC45E0F,
            0x7AC45E10, 0x7AC45E11, 0x7AC45E12, 0x7AC45E13, 0xDECADE03, 0xDECADE08, 0xDECADE0A, 0xFE6BD1B3
        ];

        private static readonly HashSet<uint> BuildingPatterns =
        [
            0x4545FAC1, 0x4545FAC2, 0x4545FAC3, 0x4545FAC4, 0x4545FAC5, 0x4545FAC6, 0x4545FAC7, 0x4545FAC9,
            0x680004E6, 0x777FA8C0, 0x7AC44A02, 0x7AC44A03, 0x7AC44A04, 0x7AC44A05, 0x7AC44A06, 0x7AC44A07,
            0x7AC44C12, 0x7AC44D00, 0x7AC44D01, 0x7AC44D02, 0x7AC44E0B, 0x7AC44E0C, 0x7AC44E0D, 0x7AC44E0E,
            0x7AC44E0F, 0x7AC44E10, 0x7AC44E11, 0x7AC44E12, 0xBADEB00E, 0xBFE4E8E3, 0xCA56701A, 0xCAFECB05,
            0xDE5E1110, 0xDECADE02, 0xDECADE07, 0xDECADE09, 0xF67ADB70, 0xFA1CA560, 0xFA1CA561, 0xFA1CA562,
            0xFA1CA563, 0xFA1CA570, 0xFA1CA571, 0xFA1CA58A, 0xDECADE01
        ];

        private static readonly HashSet<uint> MiningPatterns =
        [
            0x7AC44B02, 0x7AC44B03, 0x7AC44B04, 0x7AC44B05, 0x7AC44B06, 0x7AC44B07, 0x7AC44B08, 0x7AC44B09,
            0x7AC44B0A, 0x7AC44B0B, 0x7AC44B0C, 0xCA87FAB0, 0xCAFECAFE, 0xCAFECAFF, 0xCAFECB00, 0xCAFECB01,
            0xCAFECB02, 0xCAFECB03, 0xCAFECB04, 0xD00FAFFE, 0xDEADBEEF, 0xDECADE04, 0xDECADE05, 0xDECADE06,
            0xFA1CA580, 0xFA1CA581, 0xFA1CA582, 0xFA1CA583, 0xFA1CA584, 0xFA1CA585, 0xFA1CA586, 0xFA1CA587,
            0xFA1CA588, 0xFA1CA589
        ];

        private static readonly HashSet<uint> ShipPatterns =
        [
            //Don't put "0xDECADE01" here as a ship building can only be below anhors
        ];

        private static readonly HashSet<uint> NoFlagPatterns =
        [
            0x013374E4, 0x013374E5, 0x013374E6, 0x013374E7, 0x013374E8, 0x7AC44B0D, 0x7AC44C02, 0x7AC44C03,
            0x7AC44C04, 0xBABEB00B, 0xBADEB00D, 0xBADEB00F, 0xBADEB010, 0xBADEB011, 0xBADEB012, 0xBADEB013,
            0xF1CABB70, 0xFA1CA590, 0xFA1CA591
        ];

        public static uint[] ReadUInts(byte[] data, int offset, int count)
        {
            uint[] values = new uint[count];
            Buffer.BlockCopy(data, offset, values, 0, count * 4);
            return values;
        }

        public static void WriteUInts(uint[] values, byte[] data, int offset) =>
            Buffer.BlockCopy(values, 0, data, offset, values.Length * 4);

        private static bool IsKnownPattern(uint pattern) =>
            BlockedPatterns.Contains(pattern) || BuildingPatterns.Contains(pattern) || MiningPatterns.Contains(pattern) ||
            ShipPatterns.Contains(pattern) || NoFlagPatterns.Contains(pattern);

        private static bool IsPatternBlocked(uint pattern, uint gridState) =>
            IsKnownPattern(pattern)
                ? BlockedPatterns.Contains(pattern)
                : (gridState & Blocked) != 0 && (gridState & Deposit) == 0;

        //Sets the blocked, mining ground, buildable and ship ground bits from each cell's pattern.
        //Returns the cells where mining ground, buildable or ship ground changed: the game clears their ground resource.
        public static List<int> RecomputePatternBits(uint[] grid, uint[] patterns)
        {
            List<int> changed = [];
            for (int i = 0; i < grid.Length; i++)
            {
                uint pattern = patterns[i];
                if (!IsKnownPattern(pattern))
                    continue;

                bool blocked = BlockedPatterns.Contains(pattern);
                bool mining = MiningPatterns.Contains(pattern);
                bool building = BuildingPatterns.Contains(pattern);
                bool ship = ShipPatterns.Contains(pattern);

                uint old = grid[i];
                uint v = old & ~(MiningGround | Buildable | ShipGround);

                //Blocking deposits also set the blocked bit
                if (!((old & Deposit) != 0 && (old & Blocked) != 0))
                    v &= ~Blocked;

                if (blocked) v |= Blocked;
                if (mining) v |= MiningGround;
                if (building && !mining && !blocked) v |= Buildable;
                if (ship && !building && !blocked) v |= ShipGround;

                grid[i] = v;
                if (((old ^ v) & (MiningGround | Buildable | ShipGround)) != 0)
                    changed.Add(i);
            }
            return changed;
        }

        //Odd rows are shifted half a cell right. Directions: E, NE, NW, W, SW, SE
        private static readonly int[][] StepX = [[1, 0, -1, -1, -1, 0], [1, 1, 0, -1, 0, 1]];
        private static readonly int[] StepY = [0, -1, -1, 0, 1, 1];

        public static int HexDistance(int x1, int y1, int x2, int y2)
        {
            int q1 = x1 - (y1 - (y1 & 1)) / 2;
            int q2 = x2 - (y2 - (y2 & 1)) / 2;
            int dq = q1 - q2, dr = y1 - y2;
            return (Math.Abs(dq) + Math.Abs(dr) + Math.Abs(dq + dr)) / 2;
        }

        public sealed class Continent
        {
            public int Id;
            public bool IsWater;
            public int CellCount;
            public int X0, Y0, X1, Y1;
            public List<int> Neighbours = [];
        }

        //Reimplementation of the game's continent partition (NMap::Continents::RecomputeAll).
        //Water and land are separate continents; cells with a logic object, and land cells with a cliff or a blocked pattern, get -1.
        public static int[] ComputeContinents(uint[] grid, uint[] patterns, int width, int height, out List<Continent> continents)
        {
            int[] ids = new int[width * height];
            Array.Fill(ids, -1);
            continents = [];

            bool Joinable(int i, bool water)
            {
                uint v = grid[i];
                if ((v & LogicObject) != 0 || ((v & Water) != 0) != water)
                    return false;
                return water || (!IsPatternBlocked(patterns[i], v) && (v & Cliff) == 0);
            }

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int seed = y * width + x;
                    bool water = (grid[seed] & Water) != 0;
                    if (ids[seed] != -1 || !Joinable(seed, water))
                        continue;

                    Continent c = new() { Id = continents.Count, IsWater = water, X0 = x, Y0 = y, X1 = x, Y1 = y };
                    continents.Add(c);
                    ids[seed] = c.Id;
                    c.CellCount = 1;
                    Stack<(int x, int y)> stack = new();
                    stack.Push((x, y));

                    while (stack.Count > 0)
                    {
                        var (cx, cy) = stack.Pop();
                        //Visits dir 4 (SW) first, then walks around the cell with dirs 0..4
                        int nx = cx + StepX[cy & 1][4], ny = cy + StepY[4];
                        for (int d = 0; d < 6; d++)
                        {
                            if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                            {
                                int j = ny * width + nx;
                                if (ids[j] == -1)
                                {
                                    if (Joinable(j, water))
                                    {
                                        ids[j] = c.Id;
                                        c.CellCount++;
                                        c.X0 = Math.Min(c.X0, nx); c.Y0 = Math.Min(c.Y0, ny);
                                        c.X1 = Math.Max(c.X1, nx); c.Y1 = Math.Max(c.Y1, ny);
                                        stack.Push((nx, ny));
                                    }
                                }
                                else if (ids[j] != c.Id && !c.Neighbours.Contains(ids[j]))
                                {
                                    c.Neighbours.Add(ids[j]);
                                }
                            }
                            nx += StepX[ny & 1][d];
                            ny += StepY[d];
                        }
                    }
                }
            }
            return ids;
        }

        private static readonly byte[] ContinentsHeader = [0x01, 0x00, 0x00, 0x00, 0x23, 0xAD, 0x4D, 0xB8, 0x0E, 0x00, 0x00, 0x00];
        private static readonly byte[] ContinentHeader = [0x04, 0x00, 0x00, 0x00, 0x62, 0x5C, 0x62, 0xFF, 0x0D, 0x00, 0x00, 0x00];

        //"Map Continents" v1 section with "Map Continent" v4 records
        public static byte[] SerializeContinents(int[] ids, List<Continent> continents, int width, int height)
        {
            using MemoryStream ms = new();
            using (BinaryWriter w = new(ms))
            {
                w.Write(ContinentsHeader);
                w.Write(1);
                w.Write(width);
                w.Write(height);
                foreach (int id in ids)
                    w.Write(id);

                w.Write(continents.Count);
                int landCells = 0;
                foreach (Continent c in continents)
                {
                    w.Write(ContinentHeader);
                    w.Write(c.CellCount);
                    w.Write(c.IsWater ? 1 : 0);
                    w.Write(c.Id);
                    w.Write(c.X0); w.Write(c.Y0); w.Write(c.X1); w.Write(c.Y1);
                    w.Write(c.Neighbours.Count);
                    foreach (int n in c.Neighbours)
                        w.Write(n);
                    if (!c.IsWater)
                        landCells += c.CellCount;
                }
                w.Write(landCells);
            }
            return ms.ToArray();
        }
    }
}
