using Newtonsoft.Json.Linq;
using RCAAS.Core.Data;
using RCAAS.Core.Helpers;
using RCAAS.Core.Interfaces;
using RCAAS.Core.Wrappers.Minecraft.Mojang;
using RCAAS.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;


namespace RCAAS.Wrappers.Minecraft
{
    public class MinecraftPluginHelperExt : BasePluginHelper
    {

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
        public static string HttpMojangManifest => HttpMojangMeta + "mc/game/version_manifest.json";
        public static string JarFile(MinecraftServerType servertype, int id) { return Path.Combine(JarFolder, servertype.ToString() + "_" + id.ToString() + ".jar"); }

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
            var json = File.ReadAllText(MinecraftWrapperExt.DefaultPropertyFile);
            var properties = JObject.Parse(json);

            foreach (var row in properties["options"])
            {
                switch ((string)row["key"])
                {
                    case "server-name": row["default"] = "RCAAS Minecraft server"; break;
                    case "server-port": row["default"] = "25565"; break;
                }
                result.Add((string)row["key"], (string)row["default"]);
            }

            return result;

        }

        public async override Task<IAppWrapperConfig> GetDefaultCmdAppItemAsync()
        {
            var item = await base.GetDefaultCmdAppItemAsync();

            item.Name = "RCAAS Minecraft Server anno " + DateTime.Now.ToString("yyyy");
            item.WrapperName = "Minecraft";

            var port = int.Parse(item.Parameters["server-port"]);
            port = await EthernetHelper.FindNextFreePortAsync(port);
            item.Port = port;
            item.Parameters["server-port"] = port.ToString();

            var rcon = int.Parse(item.Parameters["rcon.port"]);
            rcon = await EthernetHelper.FindNextFreePortAsync(rcon);
            item.Parameters["rcon.port"] = rcon.ToString();

            return item;

        }

        #region Versions

        public async override Task<List<AppVersionFile>> GetNewVersionsAsync()
        {

            var resultV = await CheckForNewVersionAsync(MinecraftServerType.release);
            var resultS = await CheckForNewVersionAsync(MinecraftServerType.snapshot);

            if (resultS.Count > 0) resultV.AddRange(resultS);
            return resultV;

        }
        public static async Task<List<AppVersionFile>> CheckForNewVersionAsync(MinecraftServerType servertype)
        {
            MyLog.Info("Checking for update to Minecraft => " + servertype.ToString());
            var result = new List<AppVersionFile>();

            // Checking for updates Mojang
            var manifest = await WebHelper.DownloadFileAsync<MojangManifest>(HttpMojangManifest);
            // var manifest = web.GetMojangManifestFile(FilesAndFoldersHelper.HttpMojangManifest);
            // var release = manifest.GetLatestRelease(servertype);

            /* Take the latest released versions */
            var latest = manifest.versions.Where(v => v.type == servertype.ToString()).Take(10).ToList();

            if (latest.Count == 0) return result;
            if (!Directory.Exists(AppFolder)) Directory.CreateDirectory(AppFolder);
            if (!Directory.Exists(JarFolder)) Directory.CreateDirectory(JarFolder);

            /* But sort it so that we add the oldest version first. */
            latest.Sort(delegate (MojangVersion a, MojangVersion b)
            {
                return a.releaseTime.CompareTo(b.releaseTime);
            });
            foreach (var release in latest)
            {
                if (await GetMinecraftVersionAsync(servertype, release.id) == null)
                {
                    var download = await WebHelper.DownloadFileAsync<MojangDownloadFile>(release.Url);
                    var row = await SetMinecraftVersionAsync(servertype, release.id, release.releaseTime);
                    result.Add(row);

                    var filename = JarFile(servertype, row.Id);
                    await WebHelper.DownloadFileAsync(download.downloads.server.url, filename);
                    MyLog.Info("Downloaded a new minecraft version " + row.VersionName + " db ID: " + row.Id);
                }
            }

            return result;

        }


        /// <summary>
        /// Request file from db with wrappername + servertype 
        /// </summary>
        public static async Task<AppVersionFile> GetMinecraftVersionAsync(MinecraftServerType typeofserver, string releaseid = null, int? versionid = null)
        {

            List<AppVersionFile> result;

            if (versionid != null)
            {
                result = await DBHelper.GetAppVersionsFilesAsync(MinecraftPluginHelperExt.WrapperName, customdata: typeofserver.ToString(), appversionfileid: versionid ?? -1);
            }
            else
            {
                result = await DBHelper.GetAppVersionsFilesAsync(MinecraftPluginHelperExt.WrapperName, customdata: typeofserver.ToString(), versionname: releaseid, appversionfileid: null);
            }

            if (result.Count > 0)
            {
                result.Sort(delegate (AppVersionFile a, AppVersionFile b)
                {
                    return b.Id.CompareTo(a.Id);
                });
                return result[0];
            }
            return null;

        }

        /// <summary>
        /// Save to db with our custom naming.
        /// </summary>
        public static async Task<AppVersionFile> SetMinecraftVersionAsync(MinecraftServerType typeofserver, string versionid, DateTime releasedate)
        {
            return await DBHelper.SetAppVersionFileAsync(WrapperName, versionid, typeofserver.ToString(), releasedate);

        }

#endregion

    }

}
