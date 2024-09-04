using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System.Text;

namespace Oxide.Plugins
{
    [Info("PayNow", "PayNow Services Inc", "0.0.10")]
    [Description("Official plugin for the PayNow.gg store integration.")]
    internal class PayNow : CovalencePlugin
    {
        const string COMMAND_QUEUE_URL = "https://api.paynow.gg/v1/delivery/command-queue/";
        const string GS_LINK_URL = "https://api.paynow.gg/v1/delivery/gameserver/link";

        PluginConfig _config;

        readonly Dictionary<string, string> _headers = new Dictionary<string, string>();
        readonly CommandHistory _executedCommands = new CommandHistory(25);
        readonly StringBuilder _cachedStringBuilder = new StringBuilder();
        readonly List<string> _successfulCommandsList = new List<string>(1000);
        Timer _pendingCommandsTimer;

        #region Oxide

        [HookMethod("Loaded")]
        void Loaded()
        {
            if (string.IsNullOrEmpty(_config.ApiToken))
                PrintWarning("No API token set! Use the 'paynow.token <token>' command to set it.");

            UpdateHeaders();
        }

        [HookMethod("OnServerInitialized")]
        void OnServerInitialized() => ValidateToken();

        [Command("paynow.token")]
        void CommandToken(IPlayer player, string command, string[] args)
        {
            if (!player.IsServer && !player.IsAdmin)
                return;

            if (args.Length != 1)
            {
                player.Reply("Usage: paynow.token <token>");
                return;
            }

            StopPendingCommandsLoop();
            _config.ApiToken = args[0];
            SaveConfig();
            UpdateHeaders();

            player.Reply("Successfully saved the PayNow API token! Attempting to validate it...");
            ValidateToken();
        }

        void ValidateToken() => LinkGameServer(StartPendingCommandsLoop);

        void StartPendingCommandsLoop()
        {
            if (_pendingCommandsTimer != null) return;
            Puts("Started checking for pending commands");
            GetPendingCommands();
            timer.Every(_config.ApiCheckIntervalSeconds, GetPendingCommands);
        }

        void StopPendingCommandsLoop() => timer.Destroy(ref _pendingCommandsTimer);

        #endregion

        #region WebRequests

        void LinkGameServer(Action continuationCallback)
        {
            // Don't make the API call if we don't have a token
            if (string.IsNullOrEmpty(_config.ApiToken))
                return;

            var data = new LinkGameServerRequest
            {
                Ip = server.Address + ":" + server.Port,
                Hostname = server.Name,
                Platform = game,
                Version = Version.ToString()
            };

            try
            {
                // Make the API call
                webrequest.Enqueue(GS_LINK_URL, JsonConvert.SerializeObject(data), (code, responseString) => HandleLinkGameServerResponse(code, responseString, continuationCallback), this, RequestMethod.POST, _headers);
            }
            catch (Exception ex)
            {
                PrintException("Failure initiate game server link request to PayNow, trying again in 5 seconds...", ex);
                timer.In(5, () => LinkGameServer(continuationCallback));
                return;
            }
        }

        void HandleLinkGameServerResponse(int code, string responseString, Action continuationCallback)
        {
            // Check we are authorised to be here...
            if (code == 401 || code == 403)
            {
                PrintError("Failure linking game server to PayNow, invalid token supplied. Please update your token and try again");
                return;
            }

            // Check if we got a valid response code....
            if (code >= 500)
            {
                PrintWarning("Failure linking game server to PayNow, trying again in 5 seconds...");
                timer.In(5, () => LinkGameServer(continuationCallback));
                return;
            }

            LinkGameServerResponse response;
            try
            {
                response = JsonConvert.DeserializeObject<LinkGameServerResponse>(responseString);
            }
            catch (Exception ex)
            {
                PrintException("Failure whilst deserializing link game server response, trying again in 5 seconds...", ex);
                timer.In(5, () => LinkGameServer(continuationCallback));
                return;
            }

            if (response == null)
            {
                PrintError("PayNow API returned a null link game server response, trying again in 5 seconds...");
                timer.In(5, () => LinkGameServer(continuationCallback));
                return;
            }

            if (response.UpdateAvailable)
            {
                Puts("Update available! latest version: {0}, current version: {1}", response.LatestVersion, Version.ToString());
            }

            if (response.PreviouslyLinked != null)
            {
                PrintWarning("This token has been previously used on \"{0}\" ({1}), ensure you have removed this token from the previous server.", response.PreviouslyLinked.HostName, response.PreviouslyLinked.IP);
            }

            if (response.GameServer == null)
            {
                PrintError("PayNow API did not return a GameServer object, this may be a transient issue, please try again or contact support.");
                return;
            }

            Puts("Connected to PayNow using the token for \"{0}\" ({1}) successfully!", response.GameServer.Name, response.GameServer.Id);
            continuationCallback?.Invoke();
        }

        void GetPendingCommands()
        {
            // Don't make the API call if we don't have a token
            if (string.IsNullOrEmpty(_config.ApiToken))
                return;

            try
            {
                // Make the API call
                webrequest.Enqueue(COMMAND_QUEUE_URL, BuildOnlinePlayersJson(), HandlePendingCommands, this, RequestMethod.POST, _headers);
            }
            catch (Exception ex)
            {
                PrintException("Failed retrieve get pending commands", ex);
            }
        }

        void HandlePendingCommands(int code, string response)
        {
            try
            {
                // Check if we got a valid response
                if (code != 200 || response == null)
                    throw new Exception($"Server sent an invalid response: ({code}) ({response})");

                // Deserialize the response
                QueuedCommand[] data = JsonConvert.DeserializeObject<QueuedCommand[]>(response);
                if (data == null)
                    throw new Exception($"Response deserialized to null: ({response})");

                // Process the data
                ProcessPendingCommands(data);
            }
            catch (Exception ex)
            {
                PrintException("Failed handle pending commands", ex);
            }
        }

        void AcknowledgeCommands(List<string> commandsIds)
        {
            // Check if we have any order ids to acknowledge
            if (commandsIds.Count == 0) return;

            try
            {
                // Make the API call to acknowledge the commands
                webrequest.Enqueue(COMMAND_QUEUE_URL, BuildAcknowledgeJson(commandsIds), HandleAcknowledgeCommands, this, RequestMethod.DELETE, _headers);
            }
            catch (Exception ex)
            {
                PrintException("Failed to acknowledge commands", ex);
            }
        }

        void HandleAcknowledgeCommands(int code, string response)
        {
            // Check if we got a valid response
            if (code >= 200 && code < 300) return;

            // Log an error if we didn't get a 204 response
            PrintError(
                $"Command acknowledgement resulted in an unexpected response code: ({code.ToString()}) ({response})");
        }

        #endregion

        #region Command Processing

        void ProcessPendingCommands(QueuedCommand[] queuedCommands)
        {
            // Check if we got any data
            if (queuedCommands.Length == 0)
                return;

            _successfulCommandsList.Clear();
            for (int i = 0; i < queuedCommands.Length; i++)
            {
                QueuedCommand command = queuedCommands[i];

                // Make sure we don't execute the same command twice
                if (_executedCommands.Contains(command.AttemptId))
                    continue;

                try
                {
                    if (command.OnlineOnly && players.Connected.All(x => x.Id != command.SteamId))
                        continue;

                    // Try executing the command
                    if (ExecuteCommand(command.Command))
                    {
                        // Add the order id to the list of acknowledged orders
                        _successfulCommandsList.Add(command.AttemptId);
                        _executedCommands.Add(command.AttemptId);
                    }
                    else
                    {
                        // Log an error if the command failed
                        PrintWarning($"Failed to run command {command.Command} ({command.AttemptId})!");
                    }
                }
                catch (Exception ex)
                {
                    // Log an error if an exception occurs
                    PrintException("Failed to execute command", ex);
                }
            }

            // Log the amount of commands we executed
            if (_config.LogCommandExecutions)
                Puts(
                    $"Received {queuedCommands.Length.ToString()} and executed {_successfulCommandsList.Count.ToString()} commands!");

            // Acknowledge the commands
            AcknowledgeCommands(_successfulCommandsList);
        }

        bool ExecuteCommand(string command)
        {
            // Run the command
            server.Command(command);

            // TODO: Fetch Command Response, currently not possible when using oxide covalence libraries

            return true;
        }

        #endregion

        #region Api DTOs

        [Serializable]
        public class QueuedCommand
        {
            [JsonProperty("attempt_id")] public string AttemptId;

            [JsonProperty("steam_id")] public string SteamId;

            [JsonProperty("command")] public string Command;

            [JsonProperty("online_only")] public bool OnlineOnly;

            [JsonProperty("queued_at")] public string QueuedAt;
        }

        [Serializable]
        public class LinkGameServerRequest
        {
            [JsonProperty("ip")] public string Ip;

            [JsonProperty("hostname")] public string Hostname;

            [JsonProperty("platform")] public string Platform;

            [JsonProperty("version")] public string Version;
        }

        [Serializable]
        public class LinkGameServerResponse
        {
            [JsonProperty("update_available")] public bool UpdateAvailable { get; set; }

            [JsonProperty("latest_version")] public string LatestVersion { get; set; }

            [JsonProperty("previously_linked")] public PreviouslyLinkedData PreviouslyLinked { get; set; }

            [JsonProperty("gameserver")] public GameServerData GameServer { get; set; }

            public class PreviouslyLinkedData
            {
                [JsonProperty("ip")] public string IP { get; set; }
                [JsonProperty("host_name")] public string HostName { get; set; }
                [JsonProperty("last_linked_at")] public DateTime LastLinkedAt { get; set; }
            }

            public class GameServerData
            {
                [JsonProperty("id")] public long Id { get; set; }
                [JsonProperty("store_id")] public long StoreId { get; set; }
                [JsonProperty("name")] public string Name { get; set; }
                [JsonProperty("created_at")] public DateTime? CreatedAt { get; set; }
                [JsonProperty("updated_at")] public DateTime? UpdatedAt { get; set; }
            }
        }

        #endregion

        #region Configuration

        [Serializable]
        class PluginConfig
        {
            [JsonProperty("API token")] public string ApiToken = string.Empty;

            [JsonProperty("Time between API checks in seconds")]
            public float ApiCheckIntervalSeconds = 10;

            [JsonProperty("Log command executions")]
            public bool LogCommandExecutions = true;

            // Backwards compatibility
            [JsonProperty("ApiToken")]
            public string OldApiToken
            {
                set { ApiToken = value; }
            }

            [JsonProperty("ApiCheckIntervalSeconds")]
            public float OldApiCheckIntervalSeconds
            {
                set { ApiCheckIntervalSeconds = value; }
            }
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();

            _config = Config.ReadObject<PluginConfig>();

            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        #endregion

        #region Helpers

        void UpdateHeaders()
        {
            _headers["Content-Type"] = "application/json";
            _headers["Authorization"] = "Gameserver " + _config.ApiToken;
        }

        string BuildAcknowledgeJson(List<string> orderIds)
        {
            _cachedStringBuilder.Clear();

            // Json format [{"attempt_id": "123"}]
            _cachedStringBuilder.Append("[");
            for (int i = 0; i < orderIds.Count; i++)
            {
                _cachedStringBuilder.Append("{\"attempt_id\": \"");
                _cachedStringBuilder.Append(orderIds[i]);
                _cachedStringBuilder.Append("\"}");

                if (i < orderIds.Count - 1)
                {
                    _cachedStringBuilder.Append(",");
                }
            }

            _cachedStringBuilder.Append("]");

            return _cachedStringBuilder.ToString();
        }

        string BuildOnlinePlayersJson()
        {
            _cachedStringBuilder.Clear();

            // Json format {"steam_ids": ["123"]}
            _cachedStringBuilder.Append("{\"steam_ids\":[");
            var addedPlayers = false;
            foreach (var player in players.Connected)
            {
                addedPlayers = true;
                _cachedStringBuilder.Append("\"");
                _cachedStringBuilder.Append(player.Id);
                _cachedStringBuilder.Append("\"");
                _cachedStringBuilder.Append(",");
            }

            if (addedPlayers) _cachedStringBuilder.Remove(_cachedStringBuilder.Length - 1, 1);

            _cachedStringBuilder.Append("]}");

            return _cachedStringBuilder.ToString();
        }

        class CommandHistory
        {
            readonly Queue<string> _queue;
            readonly int _capacity;

            public CommandHistory(int capacity)
            {
                _capacity = capacity;
                _queue = new Queue<string>(capacity);
            }

            public void Add(string command)
            {
                if (_queue.Count >= _capacity)
                    _queue.Dequeue();

                _queue.Enqueue(command);
            }

            public bool Contains(string command) => _queue.Contains(command);
        }

        void PrintException(string message, Exception ex) => Interface.Oxide.LogException($"[{Title}] {message}", ex);

        #endregion
    }
}