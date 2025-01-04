using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Events;

namespace OSBase;
public class OSBase : BasePlugin
{
    public override string ModuleName => "OSBase";

    public override string ModuleVersion => "0.0.2";

    public override void Load(bool hotReload)
    {
        Console.WriteLine("OSBase loaded!");
    }
 
    [GameEventHandler]
    private void OnMatchStart()
    {
        Console.WriteLine("Match started!");
    }

    [GameEventHandler]
    private void OnMatchEnd()
    {
        Console.WriteLine("Match ended!");
    }

}