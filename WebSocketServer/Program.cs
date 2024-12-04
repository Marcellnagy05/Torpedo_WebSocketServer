﻿using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Net;
using System.Text.Json;
using System.Text;

class WebSocketServer
{
    private readonly HttpListener _httpListener;
    private readonly ConcurrentDictionary<WebSocket, int> _connectedClients = new ConcurrentDictionary<WebSocket, int>();
    private char[,] playerMap1 = new char[10, 10];
    private char[,] playerMap2 = new char[10, 10];
    private int _currentTurn = 1;
    private readonly HashSet<int> readyPlayers = new HashSet<int>();
    private readonly Dictionary<WebSocket, bool> _playerReadyStates = new Dictionary<WebSocket, bool>();
    private readonly ConcurrentDictionary<int, List<Ship>> _playerShips = new ConcurrentDictionary<int, List<Ship>>();

    public WebSocketServer(string uri)
    {
        _httpListener = new HttpListener();
        _httpListener.Prefixes.Add(uri);
    }

    public async Task Start()
    {
        _httpListener.Start();
        Console.WriteLine("Server started...");

        while (true)
        {
            var listenerContext = await _httpListener.GetContextAsync();
            if (listenerContext.Request.IsWebSocketRequest)
            {
                var webSocketContext = await listenerContext.AcceptWebSocketAsync(null);
                var clientSocket = webSocketContext.WebSocket;

                if (_connectedClients.Count < 2)
                {
                    int playerNumber = _connectedClients.Count + 1;
                    _connectedClients[clientSocket] = playerNumber;

                    Console.WriteLine($"Player {playerNumber} connected.");
                    _ = HandleClient(clientSocket, playerNumber);

                    if (_connectedClients.Count == 2)
                    {
                        Console.WriteLine("Both players connected. Waiting for ready signals...");
                    }
                }
                else
                {
                    Console.WriteLine("Server full. New connection denied.");
                    listenerContext.Response.StatusCode = 403;
                    listenerContext.Response.Close();
                }
            }
        }
    }

    private async Task HandleClient(WebSocket clientSocket, int playerNumber)
    {
        var buffer = new byte[4096];

        while (clientSocket.State == WebSocketState.Open)
        {
            try
            {
                var result = await clientSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await clientSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    _connectedClients.TryRemove(clientSocket, out _);
                    Console.WriteLine($"Player {playerNumber} disconnected.");
                    return;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Console.WriteLine($"Player {playerNumber} sent: {message}");

                if (message.StartsWith("MAP:"))
                {
                    await HandleMapMessage(message.Substring(4), playerNumber);
                }
                else if (message.StartsWith("SHOT:"))
                {
                    if (readyPlayers.Count == 2)
                    {
                        await HandleShotMessage(message.Substring(5), playerNumber);
                    }
                    else
                    {
                        await clientSocket.SendAsync(
                            new ArraySegment<byte>(Encoding.UTF8.GetBytes("GAME_NOT_READY")),
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None
                        );
                    }
                }
                else if (message == "READY")
                {
                    readyPlayers.Add(playerNumber);
                    Console.WriteLine($"Player {playerNumber} is ready.");

                    if (readyPlayers.Count == 2)
                    {
                        Console.WriteLine("Both players are ready. Starting the game!");
                        _currentTurn = 1;
                        await BroadcastTurn();
                    }
                }
                else if (message == "REMATCH_REQUEST")
                {
                    BroadcastToOtherPlayer(playerNumber, "REMATCH_REQUEST");
                }
                else if (message == "REMATCH_ACCEPT")
                {
                    BroadcastToOtherPlayer(playerNumber, "REMATCH_ACCEPT");
                }
                else if (message == "REMATCH_REJECT")
                {
                    BroadcastToOtherPlayer(playerNumber, "REMATCH_REJECT");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling player {playerNumber}: {ex.Message}");
            }
        }
    }

    private async Task HandleMapMessage(string serializedMap, int playerNumber)
    {
        try
        {
            Console.WriteLine($"[DEBUG] Raw map received from Player {playerNumber}: {serializedMap}");

            var mapMessage = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(serializedMap);
            var mapData = mapMessage["Map"].Deserialize<List<string>>();

            char[,] targetMap = playerNumber == 1 ? playerMap1 : playerMap2;
            var playerShips = new List<Ship>();

            for (int row = 0; row < 10; row++)
            {
                for (int col = 0; col < 10; col++)
                {
                    targetMap[row, col] = mapData[row][col];

                    if (mapData[row][col] == '1') // Part of a ship
                    {
                        var existingShip = playerShips.FirstOrDefault(ship =>
                            ship.Positions.Any(pos =>
                                (pos.Row == row && Math.Abs(pos.Col - col) == 1) || // Horizontally adjacent
                                (pos.Col == col && Math.Abs(pos.Row - row) == 1))); // Vertically adjacent

                        if (existingShip != null)
                        {
                            existingShip.Positions.Add((row, col));
                        }
                        else
                        {
                            playerShips.Add(new Ship
                            {
                                Length = 1,
                                Positions = new List<(int Row, int Col)> { (row, col) }
                            });
                        }
                    }
                }
            }

            _playerShips[playerNumber] = playerShips;
            Console.WriteLine($"[DEBUG] Player {playerNumber}'s ships initialized.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to process map from Player {playerNumber}: {ex.Message}");
        }
    }

    private async Task HandleShotMessage(string shotCoordinates, int playerNumber)
    {
        if (_currentTurn != playerNumber)
        {
            await SendToPlayer(playerNumber, "NOT_YOUR_TURN");
            return;
        }

        var parts = shotCoordinates.Split(',');
        int row = int.Parse(parts[0]);
        int col = int.Parse(parts[1]);
        char[,] targetMap = playerNumber == 1 ? playerMap2 : playerMap1;
        var opponentShips = _playerShips[playerNumber == 1 ? 2 : 1];

        Console.WriteLine($"[DEBUG] Player {playerNumber} fired at ({row}, {col}).");

        string result;
        if (targetMap[row, col] == '1') // Hit a ship
        {
            result = "HIT";
            targetMap[row, col] = 'H'; // Mark as hit

            // Check if the hit ship is sunk
            var hitShip = opponentShips.FirstOrDefault(ship =>
                ship.Positions.Any(pos => pos.Row == row && pos.Col == col));

            if (hitShip != null && hitShip.CheckIfSunk(targetMap))
            {
                result = "SUNK";
                hitShip.IsSunk = true;

                // Mark adjacent tiles as unavailable
                MarkAdjacentTiles(targetMap, hitShip);

                // Send the positions of the sunk ship in a separate message
                string sunkShipPositions = string.Join(";", hitShip.Positions.Select(p => $"{p.Row},{p.Col}"));
                string sunkMessage = $"SUNK_SHIP:{sunkShipPositions}";
                await BroadcastToAll(sunkMessage);
            }
            else
            {
                result = "HIT";
                targetMap[row, col] = 'H'; // Mark as hit
            }
        }
        else if (targetMap[row, col] == 'E') // Water
        {
            result = "MISS";
            targetMap[row, col] = 'M'; // Mark as miss
        }
        else
        {
            result = "INVALID"; // Already hit or invalid
        }

        Console.WriteLine($"[DEBUG] Shot result: {result}");
        await BroadcastToAll($"SHOT_RESULT:{row},{col},{result}");

        // Check if all ships are sunk
        if (AreAllShipsSunk(targetMap))
        {
            await BroadcastToAll("Game Over!");
            return;
        }

        // Switch turns if the shot was not a hit
        if (result != "HIT")
        {
            _currentTurn = playerNumber == 1 ? 2 : 1;
            await BroadcastTurn();
        }
    }

    private void MarkAdjacentTiles(char[,] map, Ship ship)
    {
        foreach (var position in ship.Positions)
        {
            int startRow = position.Row;
            int startCol = position.Col;

            for (int dr = -1; dr <= 1; dr++)
            {
                for (int dc = -1; dc <= 1; dc++)
                {
                    int adjRow = startRow + dr;
                    int adjCol = startCol + dc;

                    if (adjRow >= 0 && adjRow < map.GetLength(0) && adjCol >= 0 && adjCol < map.GetLength(1))
                    {
                        if (map[adjRow, adjCol] == 'E') // Only mark empty tiles as unavailable
                        {
                            map[adjRow, adjCol] = 'X'; // Mark as unavailable
                        }
                    }
                }
            }
        }
    }

    private async Task BroadcastToOtherPlayer(int playerNumber, string message)
    {
        var otherPlayerSocket = GetOtherPlayerSocket(playerNumber);

        if (otherPlayerSocket != null && otherPlayerSocket.State == WebSocketState.Open)
        {
            try
            {
                await otherPlayerSocket.SendAsync(
                    new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None
                );
                Console.WriteLine($"Message sent to other player: {message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending message to other player: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("Other player's socket is not available or not open.");
        }
    }

    private WebSocket GetOtherPlayerSocket(int playerNumber)
    {
        foreach (var kvp in _connectedClients)
        {
            if (kvp.Value != playerNumber)
            {
                return kvp.Key;
            }
        }
        return null;
    }

    private bool AreAllShipsSunk(char[,] map)
    {
        int remainingShips = 0;
        for (int row = 0; row < 10; row++)
        {
            for (int col = 0; col < 10; col++)
            {
                if (map[row, col] == '1')
                {
                    remainingShips++;
                }
            }
        }

        return remainingShips == 0;
    }

    private async Task BroadcastToAll(string message)
    {
        foreach (var client in _connectedClients.Keys)
        {
            if (client.State == WebSocketState.Open)
            {
                await client.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
    }

    private async Task BroadcastTurn()
    {
        string turnMessage = $"TURN:{_currentTurn}";
        Console.WriteLine($"Broadcasting turn to Player {_currentTurn}");
        await BroadcastToAll(turnMessage);
    }

    private async Task SendToPlayer(int playerNumber, string message)
    {
        foreach (var kvp in _connectedClients)
        {
            if (kvp.Value == playerNumber && kvp.Key.State == WebSocketState.Open)
            {
                await kvp.Key.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)), WebSocketMessageType.Text, true, CancellationToken.None);
                return;
            }
        }
    }
}


internal class Program
{
    static async Task Main()
    {
        var server = new WebSocketServer("http://localhost:5000/");
        await server.Start();
    }
}
