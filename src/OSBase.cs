using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules;

namespace OSBase;
public class OSBase : BasePlugin
{
    public override string ModuleName => "OSBase";
    public override string ModuleVersion => "0.0.4";
    public override string ModuleAuthor => "Pintuz";
    public override string ModuleDescription => "Base plugin for handling server events";
    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);

        Console.WriteLine("OSBase loaded!");
    }
 
    private void OnMapStart ( string mapName ) 
    {
        Console.WriteLine("Map started!");
    }

    private void OnMapEnd ( )
    {
        Console.WriteLine("Map ended!");
    }




//    [GameEventHandler]
 //   private HookResult OnMatchStart(  ) 
  //  {
   //     Console.WriteLine("Match started!");
    //    return HookResult.Continue;
    //}

 //   [GameEventHandler]
 //   private void OnMatchEnd()
 //   {
 //       Console.WriteLine("Match ended!");
 //   }

}