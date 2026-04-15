namespace OSBase.Modules;

public interface IModule {
    string ModuleName { get; }
    void Load(OSBase osbase, Config config);
    void Unload() { }
    void ReloadConfig(Config config) { }
}