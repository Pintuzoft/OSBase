using System.IO;
using CounterStrikeSharp.API.Core;

namespace OSBase;
public class OSBase : BasePlugin {
    public override string ModuleName => "OSBase";
    public override string ModuleVersion => "0.0.4";
    public override string ModuleAuthor => "Pintuz";
    public override string ModuleDescription => "Base plugin for handling server events";

    private const string ConfigDirectory = "cfg/OSBase";
    private const string MapStartConfig = "cfg/OSBase/mapstart.cfg";
    private const string MapEndConfig = "cfg/OSBase/mapend.cfg";


    public override void Load(bool hotReload) {
        Console.WriteLine("[DEBUG] OSBase is loading...");
        
        // Ensure default configs
        EnsureDefaultConfig(MapStartConfig, "// Default mapstart.cfg\n");
        EnsureDefaultConfig(MapEndConfig, "// Default mapend.cfg\n");

        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);

        Console.WriteLine("[INFO] OSBase loaded!");
}

    private async void OnMapStart ( string mapName ) 
    {
        await Task.Delay(5000);
        Console.WriteLine("Actions after 5-second delay!");
        Console.WriteLine("Map started!");
    }

    private void OnMapEnd ( ) {
        Console.WriteLine("Map ended!");
    }


    private void ExecuteConfig(string configPath) {
        // Check if the file exists; if not, create a default one
        EnsureDefaultConfig(configPath, $"// Default content for {Path.GetFileName(configPath)}\n");

        // Execute the config
        this.ExecuteConfig(configPath);
        Console.WriteLine($"Executed config: {configPath}");
    }

    private void EnsureDefaultConfig(string configPath, string defaultContent) {
        string? directory = Path.GetDirectoryName(configPath);

        if (!string.IsNullOrEmpty(directory)) {
            Console.WriteLine($"Ensuring directory exists: {directory}");
            Directory.CreateDirectory(directory);
        } else {
            Console.WriteLine($"Invalid directory path for config: {configPath}");
            return;
        }

        if (!File.Exists(configPath)) {
            Console.WriteLine($"Creating config file: {configPath}");
            File.WriteAllText(configPath, defaultContent);
            Console.WriteLine($"Default config created: {configPath}");
        } else {
            Console.WriteLine($"Config file already exists: {configPath}");
        }
    }
}