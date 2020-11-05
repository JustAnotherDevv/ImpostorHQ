﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fleck;
using Impostor.Api.Games.Managers;
using Microsoft.Extensions.Logging;

namespace Impostor.Commands.Core.DashBoard
{
    public class WebApiServer
    {
        #pragma warning disable IDE0044 // Add readonly modifier
        //  this shows if the server is running.
        public bool Running { get; private set; }
        // A list of authenticated clients.
        List<IWebSocketConnection> Clients = new List<IWebSocketConnection>();
        // A message that will be sent to all clients connected.
        private Structures.BaseMessage GlobalMessage { get; set; }
        //  we need to store our commands for the handlers.
        public Dictionary<string,string> Commands { get; private set; }
        //  This is used in the parallel command parser.
        public ParallelOptions Options { get; private set; }
        // A list of accepted keys for authentication.
        private List<string> ApiKeys { get; set; }
        // The web socket server.
        private WebSocketServer Server { get; set; }
        // The global logger, to write warnings and errors to the console.
        private ILogger<Class> Logger { get; set; }
        #pragma warning restore IDE0044 // Add readonly modifier
        //  The global game manager. Here, we use it to get statistics.
        private IGameManager GameManager { get; set; }
        public Thread HeartbeatThread { get; private set; }
        //  This is used to calculate the uptime.
        public DateTime StartTime { get; private set; }
        /// <summary>
        /// This will host an API server, that can be accessed with the given API keys.
        /// </summary>
        /// <param name="port">The port to host the server on.</param>
        /// <param name="listenInterface">The interface to bind the socket to.</param>
        /// <param name="keys">The accepted API keys.</param>
        /// <param name="logger">The global logger.</param>
        public WebApiServer(ushort port, string listenInterface,string[] keys,ILogger<Class> logger, IGameManager manager)
        {
            this.StartTime = DateTime.UtcNow;
            this.Running = true;
            this.Commands = new Dictionary<string, string>();
            Options = new ParallelOptions();
            Options.MaxDegreeOfParallelism = Environment.ProcessorCount;
            this.Logger = logger;
            this.GameManager = manager;
            //we initialize our objects.
            GlobalMessage = new Structures.BaseMessage();
            ApiKeys = new List<string>();
            GlobalMessage.Type = Structures.MessageFlag.ConsoleLogMessage;
            ApiKeys.AddRange(keys);
            Server = new WebSocketServer($"ws://{listenInterface}:{port}");
            //we start the listener.
            Server.Start(socket =>
            {
                //a client connects.
                socket.OnOpen += () => OnOpen(socket);
            });
            HeartbeatThread = new Thread(DoHeartbeat);
            HeartbeatThread.Start();
        }

        /// <summary>
        /// This is used to register a command to the parser, and add documentation for the dashboard.. The command must start with '/'. Warning: It will be automatically lowercased. Please provide proper documentation!
        /// </summary>
        /// <param name="command">The command to register. If it is already registered, it will not be duplicated.</param>
        /// <param name="docs">Command documentation. Ideally, it should be a phrase describing the function of the command to the dashboard admin.</param>
        public void RegisterCommand(string command, string docs)
        {
            if (!command.StartsWith("/")) throw new Structures.Exceptions.CommandPrefixException();
            if(String.IsNullOrEmpty(docs)||String.IsNullOrWhiteSpace(docs)||docs.Length<5) throw new Structures.Exceptions.PleaseProvideDocsException();
            lock (Commands)
            {
                if (!Commands.ContainsKey(command))
                {
                    Commands.Add(command.ToLower(),docs);
                }
            }
        }
        /// <summary>
        /// Will push a final status update to the API clients and shut down the API server.
        /// </summary>
        public void Shutdown()
        {
            this.Running = false;
            Push("Impostor server shutting down...",Structures.ServerSources.DebugSystemCritical, Structures.MessageFlag.DoKickOrDisconnect);
            Server.Dispose();
            ApiKeys.Clear();
        }
        
        /// <summary>
        /// A client has connected to the websocket server.
        /// </summary>
        /// <param name="conn">The client to process.</param>
        private void OnOpen(IWebSocketConnection conn)
        {
            conn.OnMessage = message =>
            {
                //we will handle AUTH and commands here.
                try
                {
                    var msg = JsonSerializer.Deserialize<Structures.BaseMessage>(message);
                    if (msg != null)
                    {
                        if (msg.Type.Equals(Structures.MessageFlag.LoginApiRequest))
                        {
                            //the client has entered an invalid key.
                            lock(ApiKeys)if (!ApiKeys.Contains(msg.Text))
                            {
                                msg.Name = "reject";
                                msg.Date = GetTime();
                                msg.Type = Structures.MessageFlag.LoginApiRejected;
                                conn.Send(JsonSerializer.Serialize(msg));
                                conn.Close();
                                //we log the issue.
                                Logger.LogWarning($"Failed log-in attempt : {conn.ConnectionInfo.ClientIpAddress} - key : {msg.Text}");
                                return;
                            }
                            lock (Clients)
                            {
                                Clients.Add(conn);
                                Logger.LogWarning($"ImpostorHQ : New web admin client : {conn.ConnectionInfo.ClientIpAddress}");
                                msg.Text = "You have successfully connected to ImpostorHQ!";
                                msg.Type = Structures.MessageFlag.LoginApiAccepted;
                                msg.Name = "welcome";
                                msg.Date = GetTime();
                                conn.Send(JsonSerializer.Serialize(msg));
                                conn.OnClose += () =>
                                {
                                    //we handle the client disconnecting.
                                    lock (Clients)
                                    {
                                        if (Clients.Contains(conn)) Clients.Remove(conn);
                                    }
                                };
                            }
                        }
                        else if (msg.Type.Equals(Structures.MessageFlag.ConsoleCommand))
                        {
                            lock (Clients)
                            {
                                if (!Clients.Contains(conn))
                                {
                                    //we are being attacked.
                                    //the client is sending commands without being logged in.
                                    conn.Close();
                                    Logger.LogWarning($"Break-in attempt from : {conn.ConnectionInfo.ClientIpAddress}");
                                    return;
                                }
                            }

                            MessageReceived(msg,conn);
                        }
                        else
                        {
                            //invalid API call.
                            //probably not a client.
                            conn.Close();
                        }
                    }
                }
                catch (Exception ex)
                {
                    //not JSON.
                    Console.WriteLine($"Fatal error occured : {ex}");
                    return;
                }

            };
        }

        private void MessageReceived(Structures.BaseMessage message,IWebSocketConnection conn)
        {
            //the dashboard clients should not be sending something that does not start with '/'.
            if (message.Text.StartsWith("/"))
            {
                lock (Commands)
                {
                    Parallel.ForEach(Commands, Options, (prefix, state) =>
                    {
                        if (message.Text.StartsWith(prefix.Key))
                        {
                            
                            OnMessageReceived?.Invoke(message,conn);
                            state.Break();
                        }
                    });
                }
            }
        }

        /// <summary>
        /// This is used to send messages to all connected dashboards.
        /// </summary>
        /// <param name="message">The message to send.</param>
        public void Push(string message,string name,string type)
        {
            lock(GlobalMessage) lock (Clients)
            {
                if (Clients.Count == 0) return; //no connected dashboards.
                GlobalMessage.Text = message;
                GlobalMessage.Type = type;
                GlobalMessage.Date = GetTime();
                GlobalMessage.Name = name;
                var data = JsonSerializer.Serialize<Structures.BaseMessage>(GlobalMessage);
                Task[] sendTasks = new Task[Clients.Count];
                var index = 0;
                foreach (var client in Clients)
                {
                    sendTasks[index] = AsyncSend(client,data);
                    index++;
                }
                //if this is not working, we have an issue with the server.
                Task.WhenAny(sendTasks);
            }

        }

        /// <summary>
        /// Used to send a message to a specific dashboard API client.
        /// </summary>
        /// <param name="message">The text value of the BaseMessage.</param>
        /// <param name="name">The name of the system.</param>
        /// <param name="type">The message type.</param>
        /// <param name="connection">The target.</param>
        public void PushTo(string message, string name, string type, IWebSocketConnection connection)
        {
            try
            {
                var msg = new Structures.BaseMessage
                {
                    Type = type,
                    Name = name,
                    Text = message,
                    Date = GetTime()
                };
                connection.Send(JsonSerializer.Serialize(msg));
            }
            catch(Exception ex)
            {
                //we'd like all the dashboards to know that they have been betrayed.
                Push($"{ex.Message}",Structures.ServerSources.DebugSystemCritical,Structures.MessageFlag.ConsoleLogMessage);
                Logger.LogError(ex.Message);
            }
        }

        /// <summary>
        /// Used to send data asynchronously.
        /// </summary>
        /// <param name="conn">The target client.</param>
        /// <param name="data">The data JSON to send.</param>
        /// <returns></returns>
        private async Task AsyncSend(IWebSocketConnection conn, string data)
        {
            try
            {
                await conn.Send(data);
            }
            catch (TimeoutException)
            {
                lock (Clients)
                {
                    if (Clients.Contains(conn)) /*why shouldn't it...*/ Clients.Remove(conn);
                }
            }
            catch (Exception ex)
            {
                Push(ex.Message,Structures.ServerSources.DebugSystemCritical, Structures.MessageFlag.ConsoleLogMessage);
                Logger.LogError(ex.Message);
            }
        }

        /// <summary>
        /// Will get the UNIX time epoch.
        /// </summary>
        /// <returns></returns>
        public static ulong GetTime()
        {
            TimeSpan t = DateTime.UtcNow - new DateTime(1970, 1, 1);
            return (ulong)t.TotalMilliseconds;
        }

        public void DoHeartbeat()
        {
            while (Running)
            {
                lock (Clients)
                {
                    if (Clients.Count > 0)
                    {
                        Push(CompileNumbers(),string.Empty,Structures.MessageFlag.HeartbeatMessage);
                    }
                }
                Thread.Sleep(5000);
            }
        }

        public string CompileNumbers()
        {
            ulong players = 0, games = 0; //never going to need so much...
            foreach (var game in GameManager.Games)
            {
                games++;
                foreach (var player in game.Players)
                {
                    players++;
                }
            }

            TimeSpan t = StartTime - DateTime.UtcNow;
            return games + "-" + players + "-" + t.TotalMinutes;
        }

        public delegate void DelMessageReceived(Structures.BaseMessage message,IWebSocketConnection connection);

        public event DelMessageReceived OnMessageReceived;
    }
}