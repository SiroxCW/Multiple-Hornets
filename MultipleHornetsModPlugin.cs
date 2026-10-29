using BepInEx;

namespace MultipleHornetsMod;

// TODO - adjust the plugin guid as needed
[BepInAutoPlugin(id: "io.github.siroxcw.multiplehornetsmod")]
public partial class MultipleHornetsModPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        // Put your initialization logic here
        Logger.LogInfo($"Plugin {Name} ({Id}) has loaded!");
    }
}
