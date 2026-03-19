using Newtonsoft.Json;
using RCAAS.Core.Data;
using RCAAS.Core.Helpers;
using RCAAS.Core.Interfaces;
using RCAAS.Core.Util;
using RCAAS.Core.Wrappers.Minecraft.Mojang;
using System.Diagnostics;
using System.Text;


namespace RCAAS.Wrappers.Minecraft;

public sealed class MinecraftWrapperExt : BaseWrapper
{

    #region Properties

    /// <summary>
    /// World folder for Minecraft.
    /// </summary>
    public static string WorldFolder(int CmdAppId) => 
        Path.Combine(FilesAndFoldersHelper.CmdAppRootFolder(CmdAppId), "World");

    /// <summary>
    /// Server properties file.
    /// </summary>
    public static string PropertyFile(int id) => 
        Path.Combine(FilesAndFoldersHelper.CmdAppRootFolder(id), "server.properties");

    /// <summary>
    /// Path to the EULA file that we have to save for the server to start.
    /// </summary>
    public static string EULAFile(int id) => 
        Path.Combine(FilesAndFoldersHelper.CmdAppRootFolder(id), "eula.txt");

    public static string DefaultPropertyFile => Path.Combine(FilesAndFoldersHelper.PluginFolder, "minecraft.default.properties.json");

    private Dictionary<string, string> LOGINUSERCATCH { get; set; } = [];

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
        var memory = MinecraftSettings.AssignedMemory;
        var str = new StringBuilder();

        

        str.Append($"-server -Xmx{memory}M -Xms{memory}M");
        str.Append(" -XX:+UseG1GC");
        str.Append(" -XX:+ParallelRefProcEnabled");
        str.Append(" -XX:MaxGCPauseMillis=200");
        str.Append(" -XX:+UnlockExperimentalVMOptions");
        str.Append(" -XX:+DisableExplicitGC");
        str.Append(" -XX:+AlwaysPreTouch");

        if (memory >= 12288)
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
        str.Append($" -jar \"{MinecraftPluginHelperExt.JarFile(MinecraftSettings.ServerType, Config.ExternalId)}\"");
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
                    MyLog.Log(GetLogEvent(NLog.LogLevel.Info, $"{chat.UserName} says: {chat.Message}", Id));
                }
                break;

            case CmdAppLogLevel.Error:
                if (msg.Message.Contains("Invalid or corrupt jarfile ", StringComparison.OrdinalIgnoreCase))
                {
                    // Handle corrupt jar with async re-download in fire-and-forget manner
                    ExecuteAsync(async () =>
                    {
                        await StopAsync().ConfigureAwait(false);
                        await RedownloadCorruptJarAsync().ConfigureAwait(false);
                    }, "HandleCorruptJar");
                }
                else
                {
                    var lines = msg.Message?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                    var firstLine = lines is { Length: > 0 } ? lines[0] : string.Empty;
                    MyLog.Log(GetLogEvent(NLog.LogLevel.Error, firstLine, Id));
                }
                break;

            case CmdAppLogLevel.Warn:
                break;

            case CmdAppLogLevel.Info:
            default:
                if (msg.IsLoggedIn || msg.IsUUIDInMessage)
                {
                    // Execute async user login in fire-and-forget manner
                    ExecuteAsync(() => DoUserLoginAsync(msg.Username, 0, msg.UUID), "UserLogin");
                }
                else if (msg.IsLoggedOut)
                {
                    // Execute async user logout in fire-and-forget manner
                    ExecuteAsync(() => DoUserLogoutAsync(msg.Username), "UserLogout");
                }
                else if (msg.IsEULA)
                {
                    MyLog.Log(GetLogEvent(NLog.LogLevel.Info, 
                        $"EULA not accepted for server, create the EULA file: {Config.Name}", Id));
                }
                break;
        }
    }

    /// <summary>
    /// Save before copy of files and disable automatic saving so we dont touch the files while making backups.
    /// </summary>
    private async Task BackupPrerequisites()
    {
        await SaveAsync().ConfigureAwait(false);
        await DisableSavingAsync().ConfigureAwait(false);
    }

    private async Task BackupFilesToTempFolder(string tempfolder)
    {
        var worldDir = Path.Combine(FilesAndFoldersHelper.CmdAppRootFolder(Id), "World");
        var ignore = new List<string> { "session.lock", "usercache.json" };

        // Copy the "World" directory
        if (Directory.Exists(worldDir))
        {
            FilesAndFoldersHelper.Copy(worldDir, Path.Combine(tempfolder, "World"), ignore);
        }
        else
        {
            MyLog.Warn("No world directory found.");
        }

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
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Adding the activation of Minecraft automatic save function.
    /// </summary>
    /// <param name="tempfolder"></param>
    protected async Task BackupCleanUp(string tempfolder)
    {
        await EnableSavingAsync().ConfigureAwait(false);

        if (Users.Count == 0)
        {
            HasChanged = false;
        }
    }
    /// <summary>
    /// Just so we can set the login catch.
    /// </summary>
    public override async Task StartAsync()
    {
        LOGINUSERCATCH = [];
        await base.StartAsync().ConfigureAwait(false);
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

        if (!IsRunning)
        {
            await RegisterProcessStopAsync(appid, forcestop).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Save changes to db and here.
    /// </summary>
    public override async Task ChangeConfigAsync(IAppWrapperConfig config)
    {
        await base.ChangeConfigAsync(config).ConfigureAwait(false);
        await SavePropertiesAsync().ConfigureAwait(false);
    }

    protected override async Task DoUserLoginAsync(string? username, int userid = 0, string? externalid = null)
    {
        // User UUID login should come before normal login. So if user is in list we can just abort
        if (!string.IsNullOrWhiteSpace(externalid) && !LOGINUSERCATCH.ContainsKey(username))
        {
            LOGINUSERCATCH.Add(username, externalid);
            return;
        }

        if (LOGINUSERCATCH.TryGetValue(username, out var cachedExternalId))
        {
            externalid = cachedExternalId;
            LOGINUSERCATCH.Remove(username);
        }

        await base.DoUserLoginAsync(username, userid, externalid).ConfigureAwait(false);
    }

    #endregion


    #region Helper Methods

    /// <summary>
    /// Execute async operation in fire-and-forget manner with proper error handling.
    /// Used for async operations triggered from synchronous event handlers.
    /// </summary>
    private void ExecuteAsync(Func<Task> asyncMethod, string operationName)
    {
        Task.Run(async () =>
        {
            try
            {
                await asyncMethod().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MyLog.Log(GetLogEvent(NLog.LogLevel.Error, 
                    $"Error in async operation '{operationName}': {ex.Message}", Id));
            }
        });
    }

    /// <summary>
    /// Re-download the Minecraft server jar file when corruption is detected.
    /// </summary>
    private async Task RedownloadCorruptJarAsync()
    {
        try
        {
            MyLog.Log(GetLogEvent(NLog.LogLevel.Warn, 
                "Corrupt jar file detected, attempting to re-download", Id));

            var jarPath = MinecraftPluginHelperExt.JarFile(MinecraftSettings.ServerType, Config.ExternalId);

            // Delete the corrupt jar file
            if (File.Exists(jarPath))
            {
                File.Delete(jarPath);
                MyLog.Info($"Deleted corrupt jar file: {jarPath}");
            }

            // Get the version info from database
            var version = await MinecraftPluginHelperExt.GetMinecraftVersionAsync(
                MinecraftSettings.ServerType, 
                versionid: Config.ExternalId)
                .ConfigureAwait(false);

            if (version == null)
            {
                MyLog.Log(GetLogEvent(NLog.LogLevel.Error, 
                    "Cannot re-download jar: version not found in database", Id));
                return;
            }

            // Trigger a fresh download
            await CheckForNewVersionAsync().ConfigureAwait(false);

            MyLog.Log(GetLogEvent(NLog.LogLevel.Info, 
                "Successfully re-downloaded jar file", Id));
        }
        catch (Exception ex)
        {
            MyLog.Log(GetLogEvent(NLog.LogLevel.Error, 
                $"Failed to re-download jar file: {ex.Message}", Id));
        }
    }

    #endregion


    public async Task DisableSavingAsync()
    {
        Send("save-off");
        // Allow time for the save operation to complete
        await Task.Delay(WaitTime).ConfigureAwait(false);
    }

    public async Task EnableSavingAsync()
    {
        Send("save-on");
        // Allow time for the save operation to complete
        await Task.Delay(WaitTime).ConfigureAwait(false);
    }

    public async Task SaveAsync()
    {
        Send("save-all");
        // Allow time for the save operation to complete
        await Task.Delay(WaitTime).ConfigureAwait(false);
    }

    // Note: This method is synchronous as it's likely called from non-async context
    // Consider refactoring to async in future if possible
    public void LoadProperties()
    {
        var propertyFile = PropertyFile(Id);
        if (!File.Exists(propertyFile))
        {
            return;
        }

        var pf = new iniConfigFile(propertyFile);
        pf.FileLoad();
        Config.Parameters = pf.Parameters;
    }

    public async Task SavePropertiesAsync()
    {
        var pf = new iniConfigFile(PropertyFile(Id))
        {
            Header = $"# A RCAAS Properties file.\r\n# {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n",
            Parameters = Config.Parameters
        };

        await pf.FileSaveAsync().ConfigureAwait(false);
    }


    #region Install and Uninstall

    public override async Task<IAppWrapperConfig> InstallItemAsync(IAppWrapperConfig item)
    {
        item = await base.InstallItemAsync(item).ConfigureAwait(false);

        var worldFolder = WorldFolder(Config.Id);
        if (!Directory.Exists(worldFolder))
        {
            Directory.CreateDirectory(worldFolder);
        }

        // Check the args
        var args = JsonConvert.DeserializeObject<MinecraftArgsExt>(Config.CmdArgs);
        if (args == null)
        {
            throw new InvalidOperationException("Failed to deserialize MinecraftArgsExt from CmdArgs");
        }

        if (args.AssignedMemory < 512)
        {
            throw new ArgumentException("Cannot have CmdArgs.AssignedMemory < 512", nameof(item));
        }

        // Check the Extension / properties file
        if (!Config.Parameters.TryGetValue("server-port", out var port) || 
            string.IsNullOrWhiteSpace(port) || 
            port.Trim() == "0")
        {
            throw new ArgumentException("server-port cannot be null or zero", nameof(item));
        }

        // Save the properties files now to our folder
        await SavePropertiesAsync().ConfigureAwait(false);

        // Now check if we have a minecraft jar selected, else just pick the latest one
        if (Config.ExternalId <= 0)
        {
            await CheckForNewVersionAsync().ConfigureAwait(false);
            var version = await MinecraftPluginHelperExt.GetMinecraftVersionAsync(args.ServerType)
                .ConfigureAwait(false);

            if (version != null)
            {
                item.ExternalId = version.Id;
            }
            else
            {
                throw new InvalidOperationException(
                    "Could not load a Minecraft version file for the server, database returned null");
            }
        }

        // Now save a EULA answer file
        await SaveEULAAsync(Config.Id).ConfigureAwait(false);

        return await DBHelper.UpdateCmdAppsAsync(Config).ConfigureAwait(false);
    }


    /// <summary>
    /// Save the file with EULA agreement to the server root.
    /// </summary>
    public static async Task SaveEULAAsync(int serverid)
    {
        var eula = new iniConfigFile
        {
            Header = $"# By changing the setting below to TRUE you are indicating your agreement to our EULA (https://account.mojang.com/documents/minecraft_eula).\r\n# {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
        };
        eula.Parameters.Add("eula", "true");
        await eula.FileSaveAsync(EULAFile(serverid)).ConfigureAwait(false);
    }


    #endregion


    #region Updates

    /// <summary>
    /// Update to the latest version found for the config minecraft version.
    /// </summary>
    public override async Task ApplyUpdateAsync()
    {
        await CheckForNewVersionAsync().ConfigureAwait(false);
        MyLog.Info($"Checking if server {Config.Name} needs update.");

        var version = await MinecraftPluginHelperExt.GetMinecraftVersionAsync(MinecraftSettings.ServerType)
            .ConfigureAwait(false);

        if (version != null)
        {
            await UpdateAsync(version).ConfigureAwait(false);
        }
        else
        {
            MyLog.Warn($"No version found for server type {MinecraftSettings.ServerType}");
        }
    }

    /// <summary>
    /// Update to specified version.
    /// </summary>
    public override async Task ApplyUpdateAsync(int versionfileid)
    {
        var dbversion = await MinecraftPluginHelperExt.GetMinecraftVersionAsync(
            MinecraftSettings.ServerType, 
            versionid: versionfileid)
            .ConfigureAwait(false);

        if (dbversion == null)
        {
            MyLog.Log(GetLogEvent(NLog.LogLevel.Error, 
                $"Could not find requested version. Requested => {versionfileid}", Config.Id));
            throw new InvalidDataException($"Invalid AppVersionFile requested, AppVersionFile #{versionfileid}");
        }

        if (Enum.TryParse<MinecraftServerType>(dbversion.CustomData, out var mst) && 
            mst != MinecraftSettings.ServerType)
        {
            MyLog.Log(GetLogEvent(NLog.LogLevel.Error, 
                $"Trying to update to a version of a different MinecraftServerType. Requested => {versionfileid}", 
                Config.Id));
            throw new InvalidDataException(
                $"Invalid AppVersionFile requested, mismatch of MinecraftServerType, " +
                $"AppVersionFile #{versionfileid} is not type {MinecraftSettings.ServerType}");
        }

        MyLog.Info($"Upgrading server {Config.Name} to version {dbversion.VersionName}.");

        await UpdateAsync(dbversion).ConfigureAwait(false);
    }



    public async Task UpdateAsync(AppVersionFile version)
    {
        ArgumentNullException.ThrowIfNull(version);

        // What version are we running?
        if (Config.ExternalId == version.Id)
        {
            MyLog.Info($"Server is already running version {version.VersionName}");
            return;
        }

        var restart = false;

        // Are we running?
        if (IsRunning)
        {
            restart = true;
            await StopAsync().ConfigureAwait(false);
        }

        MyLog.Log(GetLogEvent(NLog.LogLevel.Warn, 
            $"Update jar version from {Config.ExternalId} to {version.Id}.", Id));

        Config.ExternalId = version.Id;
        await DBHelper.UpdateCmdAppsAsync(Config).ConfigureAwait(false);

        // We are ready to go
        if (restart)
        {
            await StartAsync().ConfigureAwait(false);
        }
    }

    #endregion


}
