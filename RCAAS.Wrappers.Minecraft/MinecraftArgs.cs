using Newtonsoft.Json;
using RCAAS.Core.Wrappers.Minecraft.Mojang;
using RCAAS.Wrappers;


namespace RCAAS.Wrappers.Minecraft
{

    public class MinecraftArgsExt : BaseArgs
    {
        public MinecraftServerType ServerType { get; set; }
        public int AssignedMemory { get; set; }

        public int ClearoutLogfilesOlderThan { get; set; }

        public MinecraftArgsExt()
        {
            ServerType = MinecraftServerType.release;
            AssignedMemory = 2048;
            ClearoutLogfilesOlderThan = -1;
        }

    }

}
