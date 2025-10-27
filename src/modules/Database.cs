using System;
using System.Data;
using System.IO;
using System.Collections.Generic;
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

    public Database ( OSBase inOsbase, Config inConfig ) {
        osbase = inOsbase;
        config = inConfig;
        createCustomConfigs();
        LoadConfig();
        connectionString = buildConnectionString();
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
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
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Database connection string: {dbhost}:{dbuser}:{dbpass}:{dbname}:{dbport}");
        return $"server={dbhost};user id={dbuser};password={dbpass};database={dbname};port={dbport};" +
               $"pooling=true;minimumpoolsize=5;maximumpoolsize=50;connectionidletimeout=1200;";
    }

    public void Initialize ( ) {
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Initialized with connection string: {connectionString}");
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
}