using System;

namespace OSBase.Modules;

// Common module lifecycle skeleton so each module only implements its own setup, teardown
// and event wiring. Load -> gate -> OnLoad -> RegisterHandlers; Unload reverses it.
public abstract class ModuleBase : IModule {
    public abstract string ModuleName { get; }

    // Default enabled state on first config registration ("1" = on, "0" = off).
    protected virtual string DefaultEnabled => "1";

    protected OSBase? osbase;
    protected Config? config;
    protected bool isActive;
    protected bool handlersLoaded;

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;
        isActive = true;

        if (osbase == null || config == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] load failed (null deps).");
            isActive = false;
            return;
        }

        config.RegisterGlobalConfigValue(ModuleName, DefaultEnabled);

        if (config.GetGlobalConfigValue(ModuleName, "0") != "1") {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled in config.");
            isActive = false;
            return;
        }

        OnLoad();
        LoadHandlers();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    public void Unload() {
        isActive = false;

        if (handlersLoaded) {
            UnregisterHandlers();
            handlersLoaded = false;
        }

        OnUnload();

        config = null;
        osbase = null;

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] unloaded.");
    }

    public void ReloadConfig(Config inConfig) {
        config = inConfig;
        OnReloadConfig();
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] config reloaded.");
    }

    private void LoadHandlers() {
        if (osbase == null || handlersLoaded) {
            return;
        }

        RegisterHandlers();
        handlersLoaded = true;
    }

    // Setup after the enable-gate passes, before handlers are wired.
    protected virtual void OnLoad() { }

    // Teardown after handlers are unwired, before deps are released.
    protected virtual void OnUnload() { }

    // Config reload; the config field is already reassigned when this runs.
    protected virtual void OnReloadConfig() { }

    protected abstract void RegisterHandlers();
    protected abstract void UnregisterHandlers();
}
