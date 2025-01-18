namespace OSBase.Modules;

public interface IModule {
    string ModuleName { get; }
    void Load(OSBase osbase, ConfigModule config);
}