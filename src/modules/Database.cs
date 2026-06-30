using System;
using System.Data;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using MySqlConnector;

namespace OSBase.Modules;

public class Database {
    private const string ModuleName = "database";
    private readonly string connectionString;
    private OSBase? osbase;
    private Config? config;
    private string dbhost = "";
    private string dbuser = "";
    private string dbpass = "";
    private string dbname = "";
    private string dbport = "";

    private readonly object writeQueueLock = new();
    private readonly Queue<QueuedChange> writeQueue = new();
    private readonly AutoResetEvent writeSignal = new(false);
    private readonly Thread writeWorkerThread;
    private volatile bool writeWorkerRunning = true;
    private volatile bool autoDrainEnabled = true;
    private volatile bool forceDrainRequested = false;
    private int pendingWrites = 0;
    private static int globalPendingWrites = 0;

    private sealed class QueuedChange {
        public string Query { get; init; } = string.Empty;
        public MySqlParameter[] Parameters { get; init; } = Array.Empty<MySqlParameter>();
    }

    public Database ( OSBase inOsbase, Config inConfig ) {
        osbase = inOsbase;
        config = inConfig;
        createCustomConfigs();
        LoadConfig();
        connectionString = buildConnectionString();

        writeWorkerThread = new Thread(WriteWorkerLoop) {
            IsBackground = true,
            Name = "OSBase-DBWriter"
        };
        writeWorkerThread.Start();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    ~Database() {
        try {
            ShutdownWriteWorker();
        } catch {
            // Ignore finalizer exceptions.
        }
    }

    private void createCustomConfigs ( ) {
        if (config == null) return;
        config.CreateCustomConfig($"{ModuleName}.cfg",
            "// Database Configuration\n" +
            "dbhost=localhost\n" +
            "dbuser=root\n" +
            "dbpass=\n" +
            "dbname=database\n" +
            "dbport=3306\n");
    }

    private void LoadConfig ( ) {
        List<string> dbcfg = config?.FetchCustomConfig($"{ModuleName}.cfg") ?? new List<string>();
        foreach (var line in dbcfg) {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//")) continue;
            var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: Failed to parse database cfg for {parts[0]}");
                continue;
            }
            switch (parts[0]) {
                case "dbhost": dbhost = parts[1]; break;
                case "dbuser": dbuser = parts[1]; break;
                case "dbpass": dbpass = parts[1]; break;
                case "dbname": dbname = parts[1]; break;
                case "dbport": dbport = parts[1]; break;
                default:
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: Failed to parse database cfg for {parts[0]}:{parts[1]}");
                    break;
            }
        }
    }

    private string buildConnectionString ( ) {
        if (config == null) throw new InvalidOperationException($"[DEBUG] OSBase[{ModuleName}]: Config cannot be null");
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Database connection to {dbhost}:{dbport}, database={dbname} (credentials hidden for security)");
        return $"server={dbhost};user id={dbuser};password={dbpass};database={dbname};port={dbport};" +
               $"pooling=true;minimumpoolsize=5;maximumpoolsize=50;connectionidletimeout=1200;";
    }

    public void Initialize ( ) {
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Database initialized successfully (connection string hidden for security)");
    }

    // ----- Public wrappers (verb-smart) -----
    public DataTable select ( string query, params MySqlParameter[] parameters ) {
        return exeSelect(NormalizeVerb(query, "SELECT"), parameters);
    }
    public int create ( string query, params MySqlParameter[] parameters ) {
        return exeChange(NormalizeVerb(query, "CREATE"), parameters);
    }
    public int insert ( string query, params MySqlParameter[] parameters ) {
        return exeChange(NormalizeVerb(query, "INSERT"), parameters);
    }
    public int update ( string query, params MySqlParameter[] parameters ) {
        return exeChange(NormalizeVerb(query, "UPDATE"), parameters);
    }
    public int delete ( string query, params MySqlParameter[] parameters ) {
        return exeChange(NormalizeVerb(query, "DELETE"), parameters);
    }

    // ----- Async write wrappers (queue-backed, non-blocking for game thread) -----
    public void createAsync ( string query, params MySqlParameter[] parameters ) {
        enqueueChange(NormalizeVerb(query, "CREATE"), parameters);
    }
    public void insertAsync ( string query, params MySqlParameter[] parameters ) {
        enqueueChange(NormalizeVerb(query, "INSERT"), parameters);
    }
    public void updateAsync ( string query, params MySqlParameter[] parameters ) {
        enqueueChange(NormalizeVerb(query, "UPDATE"), parameters);
    }
    public void deleteAsync ( string query, params MySqlParameter[] parameters ) {
        enqueueChange(NormalizeVerb(query, "DELETE"), parameters);
    }

    public int GetPendingWriteCount() {
        return Volatile.Read(ref pendingWrites);
    }

    public static int GetGlobalPendingWriteCount() {
        return Volatile.Read(ref globalPendingWrites);
    }

    public void SetAutoDrain ( bool enabled ) {
        autoDrainEnabled = enabled;

        if (enabled) {
            writeSignal.Set();
        }
    }

    public bool FlushPendingWrites ( int timeoutMs = 2000 ) {
        forceDrainRequested = true;
        writeSignal.Set();

        var started = Environment.TickCount;

        while (Volatile.Read(ref pendingWrites) > 0) {
            if (Environment.TickCount - started > timeoutMs) {
                forceDrainRequested = false;
                return false;
            }

            Thread.Sleep(10);
        }

        forceDrainRequested = false;

        return true;
    }

    private static string NormalizeVerb ( string q, string verb ) {
        string s = (q ?? "").TrimStart();
        if (s.Length == 0) return verb;
        if (s.StartsWith(verb, StringComparison.OrdinalIgnoreCase)) return s;
        return verb + " " + s;
    }

    // ----- Low-level executors -----
    private MySqlConnection Open ( ) {
        var conn = new MySqlConnection(connectionString);
        conn.Open();
        return conn;
    }

    private int exeChange ( string query, params MySqlParameter[] parameters ) {
        try {
            using var conn = Open();
            using var cmd = new MySqlCommand(query, conn);
            if (parameters != null && parameters.Length > 0) cmd.Parameters.AddRange(parameters);
            cmd.Prepare();
            return cmd.ExecuteNonQuery();
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: (exeChange): {ex.Message}");
            return 0;
        }
    }

    private int exeChangeNoCatch ( string query, params MySqlParameter[] parameters ) {
        using var conn = Open();
        using var cmd = new MySqlCommand(query, conn);
        if (parameters != null && parameters.Length > 0) cmd.Parameters.AddRange(parameters);
        cmd.Prepare();
        return cmd.ExecuteNonQuery();
    }

    private DataTable exeSelect ( string query, params MySqlParameter[] parameters ) {
        var table = new DataTable();
        try {
            using var conn = Open();
            using var cmd = new MySqlCommand(query, conn);
            if (parameters != null && parameters.Length > 0) cmd.Parameters.AddRange(parameters);
            cmd.Prepare();
            using var adapter = new MySqlDataAdapter(cmd);
            adapter.Fill(table);
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: (exeSelect): {ex.Message}");
        }
        return table;
    }

    private void enqueueChange ( string query, params MySqlParameter[] parameters ) {
        if (!writeWorkerRunning) {
            // Fallback to sync if worker is stopped.
            exeChange(query, parameters);
            return;
        }

        var snapshot = cloneParameters(parameters);

        lock (writeQueueLock) {
            writeQueue.Enqueue(new QueuedChange {
                Query = query,
                Parameters = snapshot
            });
            Interlocked.Increment(ref pendingWrites);
            Interlocked.Increment(ref globalPendingWrites);
        }

        writeSignal.Set();
    }

    private void WriteWorkerLoop() {
        while (writeWorkerRunning) {
            writeSignal.WaitOne(1000);

            while (true) {
                QueuedChange? next = null;

                lock (writeQueueLock) {
                    if (!autoDrainEnabled && !forceDrainRequested) {
                        break;
                    }

                    if (writeQueue.Count > 0) {
                        next = writeQueue.Dequeue();
                    }
                }

                if (next == null) {
                    break;
                }

                try {
                    exeChangeNoCatch(next.Query, next.Parameters);
                } catch (Exception ex) {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: async write failed: {ex.Message}");
                } finally {
                    Interlocked.Decrement(ref pendingWrites);
                    Interlocked.Decrement(ref globalPendingWrites);
                }
            }
        }
    }

    private void ShutdownWriteWorker() {
        writeWorkerRunning = false;
        writeSignal.Set();

        if (writeWorkerThread.IsAlive) {
            writeWorkerThread.Join(250);
        }
    }

    private static MySqlParameter[] cloneParameters ( MySqlParameter[] parameters ) {
        if (parameters == null || parameters.Length == 0) {
            return Array.Empty<MySqlParameter>();
        }

        var copy = new MySqlParameter[parameters.Length];

        for (int i = 0; i < parameters.Length; i++) {
            var p = parameters[i];
            copy[i] = new MySqlParameter(p.ParameterName, p.Value ?? DBNull.Value);
        }

        return copy;
    }
}