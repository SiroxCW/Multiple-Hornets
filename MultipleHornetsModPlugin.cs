using BepInEx;

namespace MultipleHornetsMod;

[BepInAutoPlugin(id: "io.github.siroxcw.multiplehornetsmod")]
public partial class MultipleHornetsModPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        // Put your initialization logic here
        Logger.LogInfo($"Plugin {Name} ({Id}) has loaded!");
    }
}
