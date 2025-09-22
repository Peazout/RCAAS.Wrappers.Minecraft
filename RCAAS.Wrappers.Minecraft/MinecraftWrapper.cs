using Newtonsoft.Json;
using RCAAS.Core.Data;
using RCAAS.Core.Helpers;
using RCAAS.Core.Interfaces;
using RCAAS.Core.Util;
using RCAAS.Core.Wrappers.Minecraft.Mojang;
using System.Diagnostics;
using System.Text;


namespace RCAAS.Wrappers.Minecraft
{
    public sealed class MinecraftWrapperExt : BaseWrapper
    {

        #region Properties

        /// <summary>
        /// World folder for Minecraft.
        /// </summary>
        public static string WorldFolder(int CmdAppId) { return Path.Combine(FilesAndFoldersHelper.CmdAppRootFolder(CmdAppId), "World"); }
        /// <summary>
        /// Server properties file.
        /// </summary>
        public static string PropertyFile(int id) { return Path.Combine(FilesAndFoldersHelper.CmdAppRootFolder(id), "server.properties"); }
        /// <summary>
        /// Path to the EULA file that we have to save for the server to start.
        /// </summary>
        public static string EULAFile(int id) { return Path.Combine(FilesAndFoldersHelper.CmdAppRootFolder(id), "eula.txt"); }

        public static string DefaultPropertyFile => Path.Combine(FilesAndFoldersHelper.PluginFolder, "minecraft.default.properties.json");
        public override string PathMainBin => JavaHelper.GetPreferredJavaBin();
        private Dictionary<string, string> LOGINUSERCATCH { get; set; }


        public MinecraftArgsExt MinecraftSettings
        {

            get
            {
                if ((Config == null) || (Config.CmdArgs == null)) return new MinecraftArgsExt();
                return JsonConvert.DeserializeObject<MinecraftArgsExt>(Config.CmdArgs);
            }
            set
            {
                Config.CmdArgs = value.ToString();
            }

        }


        #endregion


        #region Interface

        public override bool Initalize(IRCAASContext host)
        {
            if (!base.Initalize(host)) return false;
            LoadProperties();
            return true;

        }

        protected override string CreateProcessArgs()
        {
            var str = new StringBuilder();
            str.Append("-server");
            str.Append(" -Xmx" + MinecraftSettings.AssignedMemory + "M -Xms" + MinecraftSettings.AssignedMemory + @"M ");
            str.Append(" -XX:+UseG1GC");
            str.Append(" -XX:+ParallelRefProcEnabled");
            str.Append(" -XX:MaxGCPauseMillis=200");
            str.Append(" -XX:+UnlockExperimentalVMOptions");
            str.Append(" -XX:+DisableExplicitGC");
            str.Append(" -XX:+AlwaysPreTouch");

            if (MinecraftSettings.AssignedMemory >= 12288)
            {
                str.Append(" -XX:G1NewSizePercent=40");
                str.Append(" -XX:G1MaxNewSizePercent=50");
                str.Append(" -XX:G1HeapRegionSize=16M");
                str.Append(" -XX:G1ReservePercent=15");
                str.Append(" -XX:InitiatingHeapOccupancyPercent=20");
            }
            else
            {
                str.Append(" -XX:G1NewSizePercent=30");
                str.Append(" -XX:G1MaxNewSizePercent=40");
                str.Append(" -XX:G1HeapRegionSize=8M");
                str.Append(" -XX:G1ReservePercent=20");
                str.Append(" -XX:InitiatingHeapOccupancyPercent=15");
            }

            str.Append(" -XX:G1HeapWastePercent=5");
            str.Append(" -XX:G1MixedGCCountTarget=4");
            str.Append(" -XX:G1MixedGCLiveThresholdPercent=90");
            str.Append(" -XX:G1RSetUpdatingPauseTimePercent=5");
            str.Append(" -XX:SurvivorRatio=32");
            str.Append(" -XX:+PerfDisableSharedMem");
            str.Append(" -XX:MaxTenuringThreshold=1");

            str.Append(" -jar ");
            str.Append("\"" + MinecraftPluginHelperExt.JarFile(MinecraftSettings.ServerType, Config.ExternalId) + "\"");
            str.Append(" nogui");

            return str.ToString();
        }

        protected override void OutputMessageHandler(object sender, DataReceivedEventArgs e)
        {
            var msg = new MinecraftServerDataMessage(e);
            if (msg.IsNullMessage) return;

            switch (msg.MessageLevel)
            {
                case CmdAppLogLevel.Chat:
                    var chat = msg.ChatMessage;
                    if (chat != null)
                    {
                        MyLog.Log(GetLogEvent(NLog.LogLevel.Info, chat.UserName + " says: " + chat.Message, Id));
                    }
                    break;

                case CmdAppLogLevel.Error:
                    if (msg.Message.Contains("Invalid or corrupt jarfile "))
                    {
                        //We have downloaded a corrupt jar, clear the download cache and force a re-download now
                        StopAsync().Wait();
                        //TODO: Update rutin that can download a new jar.
                    }
                    else
                    {
                        var lines = msg.Message?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        var firstLine = (lines != null && lines.Length > 0) ? lines[0] : string.Empty;
                        MyLog.Log(GetLogEvent(NLog.LogLevel.Error, firstLine, Id));
                    }
                    break;

                case CmdAppLogLevel.Warn:
                    break;

                case CmdAppLogLevel.Info:
                default:
                    if (msg.IsLoggedIn || msg.IsUUIDInMessage) DoUserLoginAsync(msg.Username, 0, msg.UUID).Wait();
                    else if (msg.IsLoggedOut) DoUserLogoutAsync(msg.Username).Wait();
                    else if (msg.IsEULA)
                    {
                        MyLog.Log(GetLogEvent(NLog.LogLevel.Info, $"EULA not accepted for server, create the EULA file: {Config.Name}", Id));
                    }

                    break;

            }

        }

        /// <summary>
        /// Save before copy of files and disable automatic saving so we dont touch the files while making backups.
        /// </summary>
        protected override void BackupPrerequisites()
        {
            Save();
            DisableSaving();
        }

        protected override void BackupFilesToTempFolder(string tempfolder)
        {
            var worldDir = Path.Combine(FilesAndFoldersHelper.CmdAppRootFolder(Id), "World");
            var ignore = new List<string> { "session.lock", "usercache.json" };

            // Copy the "World" directory
            if (Directory.Exists(worldDir))
            {
                FilesAndFoldersHelper.Copy(worldDir, Path.Combine(tempfolder, "World"), ignore);
            }
            else { MyLog.Warn("No world directory found."); }

            // Copy all files in the root directory that are not in the ignore list
            var rootFiles = Directory.GetFiles(FilesAndFoldersHelper.CmdAppRootFolder(Id));
            foreach (var file in rootFiles)
            {
                var fileName = Path.GetFileName(file);
                if (!ignore.Contains(fileName))
                {
                    File.Copy(file, Path.Combine(tempfolder, fileName));
                }
            }

            MyLog.Info("Backup copy is done.");
        }

        /// <summary>
        /// Adding the activation of Minecraft automatic save function.
        /// </summary>
        /// <param name="tempfolder"></param>
        protected override void BackupCleanUp(string tempfolder)
        {
            base.BackupCleanUp(tempfolder);
            EnableSaving();

            if (Users.Count == 0) HasChanged = false;

        }
        /// <summary>
        /// Just so we can set the login catch.
        /// </summary>
        public override Task StartAsync()
        {
            LOGINUSERCATCH = new Dictionary<string, string>();
            return base.StartAsync();
        }

        /// <summary>
        /// Stop the server, if not force the use the /stop command.
        /// </summary>
        /// <param name="forcestop"></param>
        /// <returns></returns>
        public override async Task StopAsync(bool forcestop = false)
        {
            if (App == null) return;
            var appid = App.Id;

            if (!forcestop)
            {
                Send("/stop");
                App.WaitForExit(WaitTime);
            }

            if (!IsRunning) await RegisterProcessStopAsync(appid, forcestop);

        }

        /// <summary>
        /// Save changes to db and here.
        /// </summary>
        public override async Task ChangeConfigAsync(IAppWrapperConfig config)
        {
            await base.ChangeConfigAsync(config);
            /* Check properties */
            await SavePropertiesAsync();

        }

        protected override async Task DoUserLoginAsync(string username, int userid = 0, string externalid = null)
        {
            /* User UUID login should come before normal login. So if user is in list we can just abort */
            if (!string.IsNullOrWhiteSpace(externalid) && !LOGINUSERCATCH.ContainsKey(username))
            {
                LOGINUSERCATCH.Add(username, externalid);
                return;
            }
            if (LOGINUSERCATCH.ContainsKey(username))
            {
                externalid = LOGINUSERCATCH[username];
                LOGINUSERCATCH.Remove(username);
            }

            await base.DoUserLoginAsync(username, userid, externalid);

        }

        #endregion


        public void DisableSaving()
        {
            Send("save-off");
            //Generally this needs a long wait
            Thread.Sleep(WaitTime);
        }

        public void EnableSaving()
        {
            Send("save-on");
            //Generally this needs a long wait
            Thread.Sleep(WaitTime);
        }

        public void Save()
        {
            Send("save-all");
            //Generally this needs a long wait
            Thread.Sleep(WaitTime);

        }

        public void LoadProperties()
        {
            if (File.Exists(PropertyFile(Id)))
            {
                var pf = new iniConfigFile(PropertyFile(Id));
                pf.FileLoad();
                Config.Parameters = pf.Parameters;

            }

        }

        public async Task SavePropertiesAsync()
        {
            var pf = new iniConfigFile(PropertyFile(Id));
            pf.Header = "# A RCAAS Properties file.\r\n# " + DateTime.Now.ToString() + "\r\n";
            pf.Parameters = Config.Parameters;
            await pf.FileSaveAsync();

        }


        #region Install and Uninstall

        public override async Task<IAppWrapperConfig> InstallItemAsync(IAppWrapperConfig item)
        {
            item = await base.InstallItemAsync(item); // This will save the item to db and put it in config.

            if (!Directory.Exists(WorldFolder(Config.Id))) Directory.CreateDirectory(WorldFolder(Config.Id));

            /* Check the args */
            var args = JsonConvert.DeserializeObject<MinecraftArgsExt>(Config.CmdArgs);
            if (args.AssignedMemory < 512) throw new ArgumentException("Can not have CmdArps.AssignedMemory < 512");

            /* Check the Extention / properties file */
            var port = Config.Parameters["server-port"];
            if (string.IsNullOrWhiteSpace(port) || (port.Trim() == "0")) throw new ArgumentException("server-port can not be null or zero.");

            /* Save the properties files now to our folder */
            await SavePropertiesAsync();

            /* Now check if we have a minecraft jar selected, else just pick the latest one. */
            if (Config.ExternalId <= 0)
            {
                await CheckForNewVersionAsync();
                var version = await MinecraftPluginHelperExt.GetMinecraftVersionAsync(args.ServerType);

                if (version != null) item.ExternalId = version.Id;
                else throw new ArgumentException("Could not load a Minecraft version file for the server, db returned null.");

            }

            // Now save a EULA answer file.
            SaveEULA(Config.Id);



            return await DBHelper.UpdateCmdAppsAsync(Config);

        }


        /// <summary>
        /// Save the file with EULA agreement to the server root.
        /// </summary>
        public static void SaveEULA(int serverid)
        {
            var eula = new iniConfigFile();
            eula.Header = "# By changing the setting below to TRUE you are indicating your agreement to our EULA (https://account.mojang.com/documents/minecraft_eula). \r\n# " + DateTime.Now.ToString();
            eula.Parameters.Add("eula", "true");
            eula.FileSaveAsync(EULAFile(serverid)).Wait();

        }


        #endregion


        #region Updates

        /// <summary>
        /// Update to the latest version found for the config minecraft version.
        /// </summary>
        public override async Task ApplyUpdateAsync()
        {
            await CheckForNewVersionAsync();
            MyLog.Info(AppCoreHelper.i18t, "Checking if server {title} needs update.", Config.Name);
            await UpdateAsync(await MinecraftPluginHelperExt.GetMinecraftVersionAsync(MinecraftSettings.ServerType));

        }
        /// <summary>
        /// Update to specifid version.
        /// </summary>
        public override async Task ApplyUpdateAsync(int versionfileid)
        {
            var dbversion = await MinecraftPluginHelperExt.GetMinecraftVersionAsync(MinecraftSettings.ServerType, versionid: versionfileid);
            MinecraftServerType mst;
            Enum.TryParse(dbversion.CustomData, out mst);

            if (dbversion == null)
            {
                Debug.Assert(false);
                if (mst != MinecraftSettings.ServerType)
                {
                    GetLogEvent(NLog.LogLevel.Error, "Trying to update to a version of a diffrent MinecraftServerType. Requested => " + versionfileid, Config.Id);
                    throw new InvalidDataException("Invalid AppVersionFile requested, missmatch of MinecraftServerType, AppVersionFile #" + versionfileid + " is not type " + MinecraftSettings.ServerType.ToString());
                }
                else
                {
                    GetLogEvent(NLog.LogLevel.Error, "Could not find requested version. Requested => " + versionfileid, Config.Id);
                    throw new InvalidDataException("Invalid AppVersionFile requested, AppVersionFile #" + versionfileid);
                }

            }
            MyLog.Info(AppCoreHelper.i18t, "Upgrading server {title} to version {version}.", Config.Name, dbversion.VersionName);
            await UpdateAsync(dbversion);

        }



        public async Task UpdateAsync(AppVersionFile version)
        {
            var restart = false;
            // What version are we running?
            if (Config.ExternalId == version.Id) { MyLog.Info("Server is allready running version " + version.VersionName); return; }
            else
            {
                // Are we running?
                if (IsRunning)
                {
                    restart = true;
                    await StopAsync();
                }

                MyLog.Log(GetLogEvent(NLog.LogLevel.Warn, "Update jar version from " + Config.ExternalId + " to " + version.Id + ".", Id));
                Config.ExternalId = version.Id;
                await DBHelper.UpdateCmdAppsAsync(Config);

                // We are ready to go.
                if (restart) await StartAsync();
            }

        }

        #endregion


    }

}
