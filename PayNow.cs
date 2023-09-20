using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System.Text;

namespace Oxide.Plugins
{
    [Info( "PayNow", "Mr. Blue", "0.0.2" )]
    internal class PayNow : CovalencePlugin
    {
        #region Variables

        private const string API_URL = "https://api.paynow.gg/v1/delivery/command-queue/";

        private PluginConfig _config;
        
        private Dictionary<string, string> _headers = new Dictionary<string, string>();

        private CommandHistory _executedCommands = new CommandHistory( 25 );

        #endregion

        #region Configuration

        [Serializable]
        private class PluginConfig
        {
            public string ApiToken;
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject( new PluginConfig(), true );
        }

        #endregion

        #region Hooks

        [HookMethod( "Init" )]
        private void Init()
        {
            _config = Config.ReadObject<PluginConfig>();
            _headers = GetHeaders( _config.ApiToken );
        }

        [HookMethod( "Loaded" )]
        private void Loaded()
        {
            RetrieveApiData();
            timer.Every( 10f, RetrieveApiData );
        }

        #endregion

        #region Commands

        [Command( "paynow.token" )]
        private void CommandToken( IPlayer player, string command, string[] args )
        {
            if( !player.IsServer || !player.IsAdmin )
            {
                return;
            }

            if( args.Length != 1 )
            {
                player.Reply( "Usage: paynow.token <token>" );
                return;
            }

            //TODO: Validate token?
            
            _config.ApiToken = args[0];
            Config.WriteObject( _config, true );

            _headers = GetHeaders( _config.ApiToken );

            player.Reply( "Token set!" );
        }

        #endregion

        #region Api Logic

        private void RetrieveApiData()
        {
            try
            {
                // Make the API call
                webrequest.Enqueue( API_URL, null, HandleApiReceiveData, this, RequestMethod.GET, _headers );
            }
            catch( Exception ex )
            {
                PrintException( "Failed retrieve PayNow Donations! (RetrieveApiData Exception)", ex );
            }
        }

        private void HandleApiReceiveData( int code, string response )
        {
            try
            {
                // Check if we got a valid response
                if( code != 200 || response == null )
                {
                    throw new Exception( $"Failed retrieve PayNow Commands! (WebRequest failed!) ({code}) ({response})" );
                }

                // Deserialize the response
                QueuedCommand[] data = JsonConvert.DeserializeObject<QueuedCommand[]>( response );
                if( data == null )
                {
                    throw new Exception( $"Failed retrieve PayNow Commands! (JsonConvert failed!) ({response})" );
                }

                // Process the data
                HandleApiData( data );
            }
            catch( Exception ex )
            {
                PrintException( "Failed retrieve PayNow Commands! (JsonConvert Exception)", ex );
            }
        }

        private void AcknowledgeCommands( List<string> orderIds )
        {
            // Check if we have any order ids to acknowledge
            if( orderIds.Count == 0 )
            {
                return;
            }

            // Serialize the data
            string json = BuildAcknowledgeJson( orderIds );

            try
            {
                // Make the API call to acknowledge the commands
                webrequest.Enqueue( API_URL, json, HandleAcknowledgement, this, RequestMethod.DELETE, _headers );
            }
            catch( Exception ex )
            {
                PrintException( "Failed to acknowledge PayNow Donation! (AcknowledgeCommands Exception)", ex );
            }
        }

        private void HandleAcknowledgement( int code, string response )
        {
            // Check if we got a valid response
            if( code == 204 )
            {
                return;
            }

            // Log an error if we didn't get a 204 response
            PrintError( $"Failed to acknowledge PayNow Donation! (HandleAcknowledgement) ({code}) ({response})" );
        }

        #endregion

        #region Api Handling

        private void HandleApiData( QueuedCommand[] queuedCommands )
        {
            // Check if we got any data
            // if( queuedCommands.Length == 0 )
            // {
            //     return;
            // }

            List<string> successfulCommands = new List<string>();
            foreach( QueuedCommand command in queuedCommands )
            {
                // Make sure we don't execute the same command twice
                if( _executedCommands.Contains( command.AttemptId ) )
                {
                    continue;
                }

                try
                {
                    // Try executing the commands for the donation
                    if( ExcecuteDonation( command.Command ) )
                    {
                        // Add the order id to the list of acknowledged orders
                        successfulCommands.Add( command.AttemptId );
                        _executedCommands.Add( command.AttemptId );
                    }
                    else
                    {
                        // Log an error if the command failed
                        PrintWarning( $"Failed to run command {command.Command} ({command.AttemptId})!" );
                    }
                }
                catch( Exception ex )
                {
                    // Log an error if an exception occurs
                    PrintException( "Failed to execute donation!", ex );
                }
            }

            // Log the amount of commands we executed
            Puts( $"Received {queuedCommands.Length} and executed {successfulCommands.Count} commands!" );

            // Acknowledge the donations
            AcknowledgeCommands( successfulCommands );
        }

        private bool ExcecuteDonation( string command )
        {
            // Run the command
            server.Command( command );

            // TODO: Check if the command ran properly

            return true;
        }

        #endregion

        #region Api Classes

        [Serializable]
        public class QueuedCommand
        {
            [JsonProperty( "attempt_id" )] public string AttemptId;

            [JsonProperty( "command" )] public string Command;

            [JsonProperty( "queued_at" )] public string QueuedAt;
        }

        #endregion

        #region Helpers

        private string BuildAcknowledgeJson( List<string> orderIds )
        {
            StringBuilder sb = new StringBuilder();

            // Json format [{"attempt_id": "123"}]
            sb.Append( "[" );
            for( int i = 0; i < orderIds.Count; i++ )
            {
                sb.Append( "{\"attempt_id\": \"" );
                sb.Append( orderIds[i] );
                sb.Append( "\"}" );

                if( i < orderIds.Count - 1 )
                {
                    sb.Append( "," );
                }
            }
            sb.Append( "]" );

            return sb.ToString();
        }

        private Dictionary<string, string> GetHeaders( string token )
        {
            return new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["Authorization"] = "Gameserver " + token
            };
        }

        private class CommandHistory
        {
            private Queue<string> _queue;
            private int _capacity;

            public CommandHistory( int capacity )
            {
                _capacity = capacity;
                _queue = new Queue<string>( capacity );
            }

            public void Add( string command )
            {
                if( _queue.Count >= _capacity )
                {
                    _queue.Dequeue();
                }

                _queue.Enqueue( command );
            }

            public bool Contains( string command )
            {
                return _queue.Contains( command );
            }
        }

        #endregion

        #region Logging

        private void PrintException( string message, Exception ex )
        {
            Interface.Oxide.LogException( $"[{Title}] {message}", ex );
        }

        #endregion
    }
}
