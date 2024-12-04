using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

    public class Ship
    {
        public int Length { get; set; } // Length of the ship
        public List<(int Row, int Col)> Positions { get; set; } = new List<(int Row, int Col)>();
        public bool IsSunk { get; set; } = false;
    public bool IsRow { get; set; } = false;  // Igaz, ha sorban
    public bool IsCol { get; set; } = false;
    public bool CheckIfSunk(char[,] map)
        {
            return Positions.All(pos => map[pos.Row, pos.Col] == 'H');
        }
    }
