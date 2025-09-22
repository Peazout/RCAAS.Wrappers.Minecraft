using RCAAS.Core.Data;
using RCAAS.Core.Wrappers;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics;
using System.Text.RegularExpressions;


namespace RCAAS.Wrappers.Minecraft
{

    public class MinecraftServerDataMessage : BaseRCAASConsoleMessage
    {
        // New logline format.
        private readonly Regex regLogrow = new Regex(@"^\[(?<time>\d{2}:\d{2}:\d{2})\] \[(?<thread>[\w\s#-]+)\/(?<loglevel>[A-Z]+)\]: (?<msg>.+)$");

        private readonly Regex regErrorLevel = new Regex(@"^\[([\w\s\#]+)/([A-Z]+)\]: {1}");
        private readonly Regex regPlayerChat = new Regex(@"^(\<([\w-~])+\>){1}");
        private readonly Regex regConsoleChat = new Regex(@"^(\[CONSOLE\]|\[Server\]|\<\*Console\>){1}");
        private readonly Regex regPlayerPM = new Regex(@"^(\[([\w])+\-\>(\w)+\]){1}");
        private readonly Regex regPlayerUUID = new Regex(@"^(UUID of player )([\w]+)( is )([\w\-]+)");
        // Do not match IPv6
        private readonly Regex regPlayerLoggedIn = new Regex(@"^([\w]+)(?:\s*)(?:\[\/[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\:[0-9]+\] logged in with entity id)");
        private readonly Regex regPlayerLoggedOut = new Regex(@"^([\w]+) ?(lost connection)");
        private readonly Regex regServerVersion = new Regex(@"^(?:Starting minecraft server version )");
        private readonly Regex regGameMode = new Regex(@"^(?:Default game type:) ([0-9])");
        private readonly Regex regEULA = new Regex(@" EULA (.*) eula\.txt ");

        public CmdAppLogLevel MessageLevel { get; set; }
        public string Message { get; set; }

        public MinecraftServerDataMessage(DataReceivedEventArgs args) : base(args)
        {
            Message = Received.Data;
            MessageLevel = CmdAppLogLevel.All;

            // If message is null, return.
            if (Received == null || Received.Data == null) return;
            // Check
            var matchLogRow = regLogrow.Match(Received.Data);

            // If not a match it is not a minecraft logline of intrest.
            if (!matchLogRow.Success) return;

            Message = matchLogRow.Groups["msg"].Value;

            // Set loglevel
            switch (matchLogRow.Groups["loglevel"].Value)
            {
                case "INFO": MessageLevel = CmdAppLogLevel.Info; break;
                case "WARN": MessageLevel = CmdAppLogLevel.Warn; break;
                default: MessageLevel = CmdAppLogLevel.Error; break;
            }
            if (MessageLevel == CmdAppLogLevel.Info)
            {
                if (regPlayerChat.Match(Message).Success || regPlayerPM.Match(Message).Success || regConsoleChat.Match(Message).Success) { MessageLevel = CmdAppLogLevel.Chat; }
            }
            
        }

        public MinecraftChatMessage ChatMessage
        {
            get
            {
                var msg = new MinecraftChatMessage();
                string str = Message;

                Match regMatch = regPlayerChat.Match(str);
                if (regMatch.Success)
                {
                    msg.Message = str.Replace(regMatch.Groups[0].Value, "").Trim();
                    msg.UserName = regMatch.Groups[0].Value;
                    msg.UserName = msg.UserName.Substring(1).Replace(">", "");
                    return msg;
                }

                regMatch = regConsoleChat.Match(str);
                if (regMatch.Success)
                {
                    msg.Message = str.Replace(regMatch.Groups[0].Value, "").Trim();
                    msg.UserName = regMatch.Groups[0].Value;
                    return msg;
                }


                regMatch = regPlayerPM.Match(str);
                if (regMatch.Success)
                {
                    msg.Message = str.Replace(regMatch.Groups[0].Value, "").Trim();
                    msg.UserName = regMatch.Groups[0].Value;
                    return msg;
                }

                return null;

            }

        }

        public bool IsLoggedIn
        {
            get
            {
                if (regPlayerLoggedIn.Match(Message).Success) return true;
                return false;
            }
        }

        public bool IsLoggedOut
        {
            get
            {
                if (regPlayerLoggedOut.Match(Message).Success) return true;
                return false;
            }
        }

        public string UserLoginName
        {
            get
            {
                return regPlayerLoggedIn.Match(Message).Groups[1].Value;
            }

        }
        public string UserLogoutName
        {
            get
            {
                return regPlayerLoggedOut.Match(Message).Groups[1].Value;
            }

        }
        public bool IsUUIDInMessage
        {
            get
            {
                if (regPlayerUUID.Match(Message).Success) return true;
                return false;
            }
        }
        public bool IsGamemode
        {
            get
            {
                if (regGameMode.Match(Message).Success) return true;
                return false;
            }

        }
        public bool IsEULA
        {
            get
            {
                if (regEULA.Match(Message).Success) return true;
                return false;
            }
        }
        public int Gamemode
        {
            get
            {
                return Convert.ToInt32(regGameMode.Match(Message).Groups[1].Value);
            }
        }

        public string UUID
        {
            get
            {
                return regPlayerUUID.Match(Message).Groups[4].Value;
            }

        }
        public string UUIDName
        {
            get
            {
                return regPlayerUUID.Match(Message).Groups[2].Value;
            }

        }

        public string Username
        {
            get
            {
                if (IsUUIDInMessage) return UUIDName;
                else if (IsLoggedOut) return UserLogoutName;
                return UserLoginName;
            }
        }

    }

    public class MinecraftChatMessage
    {
        public int UserId { get; set; }

        [NotMapped]
        public string UserName { get; set; }

        public DateTime Logged { get; set; }

        [StringLength(512)]
        public string Message { get; set; }

        public int? ServerId { get; set; }

    }

}
