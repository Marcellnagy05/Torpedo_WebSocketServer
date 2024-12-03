using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

class WebSocketServer
{
    private readonly HttpListener _httpListener;
    private readonly ConcurrentDictionary<WebSocket, int> _connectedClients = new ConcurrentDictionary<WebSocket, int>();
    private char[,] playerMap1 = new char[10, 10];
    private char[,] playerMap2 = new char[10, 10];
    private int _currentTurn = 1;
    private readonly HashSet<int> readyPlayers = new HashSet<int>();

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

    private void PrintMap(char[,] map)
    {
        for (int row = 0; row < map.GetLength(0); row++)
        {
            for (int col = 0; col < map.GetLength(1); col++)
            {
                Console.Write(map[row, col] == '\0' ? 'E' : map[row, col]);
            }
            Console.WriteLine();
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
                    await HandleShotMessage(message.Substring(5), playerNumber);
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

            // Deserialize the incoming map data (JSON string to List<string>)
            var mapMessage = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(serializedMap);

            if (mapMessage == null)
            {
                Console.WriteLine("[ERROR] Failed to deserialize map message.");
                return;
            }

            // Extract the "Map" field and deserialize it to a List<string>
            var mapData = mapMessage["Map"].Deserialize<List<string>>();

            if (mapData == null || mapData.Count != 10 || mapData.Any(row => row.Length != 10))
            {
                Console.WriteLine($"[ERROR] Invalid map format received from Player {playerNumber}. Map has {mapData?.Count} rows and expected 10.");
                return;
            }

            Console.WriteLine("[DEBUG] Map data after deserialization:");
            foreach (var row in mapData)
            {
                Console.WriteLine(row);  // Print each row of the map to debug
            }

            // Determine which player's map to update
            char[,] targetMap = playerNumber == 1 ? playerMap1 : playerMap2;

            // Populate the target map with the data
            for (int row = 0; row < 10; row++)
            {
                for (int col = 0; col < 10; col++)
                {
                    targetMap[row, col] = mapData[row][col];  // Set each cell in the map
                }
            }

            // Log the map after it's been populated
            Console.WriteLine($"[DEBUG] Player {playerNumber}'s map initialized:");
            PrintMap(targetMap);
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

        Console.WriteLine($"[DEBUG] Player {playerNumber} fired at ({row}, {col}).");

        string result;
        if (targetMap[row, col] == '1')  // Ship
        {
            result = "HIT";
            targetMap[row, col] = 'H';  // Mark as hit
        }
        else if (targetMap[row, col] == 'E')  // Water
        {
            result = "MISS";
            targetMap[row, col] = 'M';  // Mark as miss
        }
        else
        {
            result = "INVALID"; // Already hit or invalid
        }

        Console.WriteLine($"[DEBUG] Shot result: {result}");
        Console.WriteLine($"[DEBUG] Target cell after shot: '{targetMap[row, col]}'");

        // Broadcast the shot result
        string resultMessage = $"SHOT_RESULT:{row},{col},{result}";
        await BroadcastToAll(resultMessage);

        // Check if the game is over (opponent's ships are all sunk)
        if (AreAllShipsSunk(targetMap))
        {
            // Send game over message to both players
            string gameOverMessage = "Game Over!";

            await BroadcastToAll(gameOverMessage);

            // End the game (you can stop further game logic or leave as a flag for the client to handle)
            return;
        }

        // Only switch turns if the shot was a miss or invalid
        if (result != "HIT")
        {
            _currentTurn = playerNumber == 1 ? 2 : 1;
            await BroadcastTurn();
        }
    }


    private bool AreAllShipsSunk(char[,] map)
    {
        // Count how many ships are still left on the map (i.e., 'S')
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
