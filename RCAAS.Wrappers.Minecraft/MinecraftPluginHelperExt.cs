using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RCAAS.Core.Data;
using RCAAS.Core.Helpers;
using RCAAS.Core.Interfaces;
using RCAAS.Core.Wrappers;
using RCAAS.Wrappers.Minecraft.Mojang;


namespace RCAAS.Wrappers.Minecraft;

public class MinecraftPluginHelperExt : BasePluginHelper
{
    // Static HttpClient for efficient connection pooling and resource management
    // In production with DI, prefer IHttpClientFactory injection
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    #region Properties


    /// <summary>
    /// Our wrapper class name.
    /// </summary>
    public static string WrapperName => "Minecraft";
    //
    // Web
    //
    public static string HttpMojangMeta => "https://launchermeta.mojang.com/";
    public static string HttpMojangJar => "https://launcher.mojang.com/";
    public static string HttpMojangManifest => $"{HttpMojangMeta}mc/game/version_manifest.json";
    public static string JarFile(MinecraftServerType servertype, int id) => Path.Combine(JarFolder, $"{servertype}_{id}.jar");

    /// <summary>
    /// Jar folder in the Minecarft app folder.
    /// </summary>
    public static string JarFolder => Path.Combine(AppFolder, "jar");
    public static string AppFolder => Path.Combine(FilesAndFoldersHelper.AppsFolder, "Minecraft");


    #endregion 


    public override BaseArgs GetDefaultArgs()
    {
        return new MinecraftArgsExt();
    }

    public override Dictionary<string, string> GetDefaultParameters()
    {
        var result = new Dictionary<string, string>();

        // Setup default properties config file
        // Note: Consider making this async in future refactoring
        var json = File.ReadAllText(MinecraftWrapperExt.DefaultPropertyFile);
        var properties = JObject.Parse(json);

        if (properties["options"] is not JArray options)
        {
            return result;
        }

        foreach (var row in options)
        {
            var key = (string?)row["key"];
            if (string.IsNullOrEmpty(key)) continue;

            switch (key)
            {
                case "server-name":
                    row["default"] = "RCAAS Minecraft server";
                    break;
                case "server-port":
                    row["default"] = "25565";
                    break;
            }

            var defaultValue = (string?)row["default"] ?? string.Empty;
            result.TryAdd(key, defaultValue);
        }

        return result;
    }

    public async override Task<IAppWrapperConfig> GetDefaultCmdAppItemAsync()
    {
        var item = await base.GetDefaultCmdAppItemAsync().ConfigureAwait(false);

        item.Name = $"RCAAS Minecraft Server anno {DateTime.Now:yyyy}";
        item.WrapperName = "Minecraft";

        if (item.Parameters.TryGetValue("server-port", out var serverPortStr) && 
            int.TryParse(serverPortStr, out var serverPort))
        {
            serverPort = await EthernetHelper.FindNextFreePortAsync(serverPort).ConfigureAwait(false);
            item.Port = serverPort;
            item.Parameters["server-port"] = serverPort.ToString();
        }

        if (item.Parameters.TryGetValue("rcon.port", out var rconPortStr) && 
            int.TryParse(rconPortStr, out var rconPort))
        {
            rconPort = await EthernetHelper.FindNextFreePortAsync(rconPort).ConfigureAwait(false);
            item.Parameters["rcon.port"] = rconPort.ToString();
        }

        return item;
    }

    #region Versions

    public async override Task<List<AppVersionFile>> GetNewVersionsAsync()
    {
        var resultV = await CheckForNewVersionAsync(MinecraftServerType.release).ConfigureAwait(false);
        var resultS = await CheckForNewVersionAsync(MinecraftServerType.snapshot).ConfigureAwait(false);

        if (resultS.Count > 0)
        {
            resultV.AddRange(resultS);
        }

        return resultV;
    }
    // Uses shared HttpClient for better resource management
    // Can accept custom HttpClient for testing purposes
    public static async Task<List<AppVersionFile>> CheckForNewVersionAsync(
        MinecraftServerType servertype, 
        HttpClient? httpClient = null)
    {
        MyLog.Info($"Checking for update to Minecraft => {servertype}");
        var result = new List<AppVersionFile>();

        // Use shared client if none provided (for testability)
        httpClient ??= SharedHttpClient;

        try
        {
            var manifestJson = await httpClient.GetStringAsync(HttpMojangManifest).ConfigureAwait(false);
            var manifest = JsonConvert.DeserializeObject<MojangManifest>(manifestJson);

            if (manifest?.versions == null)
            {
                MyLog.Warn("Failed to deserialize manifest or versions list is null");
                return result;
            }

            var latest = manifest.versions
                .Where(v => v.type == servertype.ToString())
                .Take(10)
                .OrderBy(v => v.releaseTime)
                .ToList();

            if (latest.Count == 0)
            {
                return result;
            }

            Directory.CreateDirectory(AppFolder);
            Directory.CreateDirectory(JarFolder);

            foreach (var release in latest)
            {
                if (await GetMinecraftVersionAsync(servertype, release.id).ConfigureAwait(false) == null)
                {
                    var downloadJson = await httpClient.GetStringAsync(release.Url).ConfigureAwait(false);
                    var download = JsonConvert.DeserializeObject<MojangDownloadFile>(downloadJson);

                    if (download?.downloads?.server?.url == null)
                    {
                        MyLog.Warn($"Invalid download metadata for version {release.id}");
                        continue;
                    }

                    var row = await SetMinecraftVersionAsync(servertype, release.id, release.releaseTime).ConfigureAwait(false);
                    result.Add(row);

                    var filename = JarFile(servertype, row.Id);
                    var jarBytes = await httpClient.GetByteArrayAsync(download.downloads.server.url).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(filename, jarBytes).ConfigureAwait(false);
                    MyLog.Info($"Downloaded new Minecraft version {row.VersionName} (DB ID: {row.Id})");
                }
            }
        }
        catch (Exception ex)
        {
            MyLog.Error($"Error checking for new Minecraft version: {ex.Message}");
        }

        return result;
    }


    /// <summary>
    /// Request file from db with wrappername + servertype 
    /// </summary>
    public static async Task<AppVersionFile?> GetMinecraftVersionAsync(
        MinecraftServerType typeofserver, 
        string? releaseid = null, 
        int? versionid = null)
    {
        List<AppVersionFile> result;

        if (versionid.HasValue)
        {
            result = await DBHelper.GetAppVersionsFilesAsync(
                WrapperName, 
                customdata: typeofserver.ToString(), 
                appversionfileid: versionid.Value)
                .ConfigureAwait(false);
        }
        else
        {
            result = await DBHelper.GetAppVersionsFilesAsync(
                WrapperName, 
                customdata: typeofserver.ToString(), 
                versionname: releaseid, 
                appversionfileid: null)
                .ConfigureAwait(false);
        }

        return result.Count > 0 
            ? result.OrderByDescending(x => x.Id).First() 
            : null;
    }

    /// <summary>
    /// Save to db with our custom naming.
    /// </summary>
    public static async Task<AppVersionFile> SetMinecraftVersionAsync(
        MinecraftServerType typeofserver, 
        string versionid, 
        DateTime releasedate)
    {
        return await DBHelper.SetAppVersionFileAsync(
            WrapperName, 
            versionid, 
            typeofserver.ToString(), 
            releasedate)
            .ConfigureAwait(false);
    }

#endregion

}
