using System;
using System.Data;
using System.IO;
using System.Collections.Generic;
using MySqlConnector; // Using MySqlConnector package

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

    public Database(OSBase inOsbase, Config inConfig) {
        this.osbase = inOsbase;
        this.config = inConfig;

        // Register required global config values
        createCustomConfigs();
        LoadConfig();
        connectionString = buildConnectionString ( );
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    private void createCustomConfigs() {
        if (config == null) 
            return;
        config.CreateCustomConfig($"{ModuleName}.cfg", "// Database Configuration\ndbhost=localhost\ndbuser=root\ndbpass=\ndbname=database\ndbport=3306\n");
    }

        private void LoadConfig() {
            List<string> dbcfg = config?.FetchCustomConfig($"{ModuleName}.cfg") ?? new List<string>();

            foreach (var line in dbcfg) {
                string trimmedLine = line.Trim();
                if ( string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//") )
                    continue;
                var parts = trimmedLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if ( parts.Length == 2 ) {
                    switch (parts[0]) {
                        case "dbhost":
                            dbhost = parts[1];
                            break;
                        case "dbuser":
                            dbuser = parts[1];
                            break;
                        case "dbpass":
                            dbpass = parts[1];
                            break;
                        case "dbname":
                            dbname = parts[1];
                            break;
                        case "dbport":
                            dbport = parts[1];
                            break;
                        default:
                            Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: Failed to parse database cfg for {parts[0]}:{parts[1]}");
                            break;
                    }

                } else {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: Failed to parse database cfg for {parts[0]}");
                }
            }
        }


    // Build the connection string from config values
    private string buildConnectionString ( ) {
        if (config == null) {
            throw new InvalidOperationException($"[DEBUG] OSBase[{ModuleName}]: Config cannot be null");
        }
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Database connection string: {dbhost}:{dbuser}:{dbpass}:{dbname}:{dbport}");
        return $"server={dbhost};user id={dbuser};password={dbpass};database={dbname};port={dbport};pooling=true;minimumpoolsize=5;maximumpoolsize=50;connectionidletimeout=1200;";
    }

    public void Initialize() {
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Initialized with connection string: {connectionString}");
    }

    // SELECT method: returns a DataTable
    public DataTable select(string query, params MySqlParameter[] parameters) {
        return exeSelect("SELECT "+query, parameters);
    }

    // INSERT method: returns affected row count
    public int insert(string query, params MySqlParameter[] parameters) {
        return exeChange("INSERT "+query, parameters);
    }

    // UPDATE method: returns affected row count
    public int update(string query, params MySqlParameter[] parameters) {
        return exeChange("UPDATE "+query, parameters);
    }

    // DELETE method: returns affected row count
    public int delete(string query, params MySqlParameter[] parameters) {
        return exeChange("DELETE "+query, parameters);
    }
    // CREATE method: returns affected row count
    public void create(string query) {
        exeChange("CREATE "+query, []);
    }

    // Execute a SELECT query using a prepared statement
    private DataTable exeSelect(string query, params MySqlParameter[] parameters) {
        var table = new DataTable();
        try {
            using (var conn = new MySqlConnection(connectionString))
            using (var cmd = new MySqlCommand(query, conn)) {
                cmd.Parameters.AddRange(parameters);
                conn.Open();
                cmd.Prepare();
                using (var adapter = new MySqlDataAdapter(cmd)) {
                    adapter.Fill(table);
                }
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: (exeSelect): {ex.Message}");
        }
        return table;
    }

    // Execute INSERT/UPDATE/DELETE using a prepared statement
    private int exeChange(string query, params MySqlParameter[] parameters) {
        int affectedRows = 0;
        try {
            using (var conn = new MySqlConnection(connectionString))
            using (var cmd = new MySqlCommand(query, conn)) {
                cmd.Parameters.AddRange(parameters);
                conn.Open();
                cmd.Prepare();
                affectedRows = cmd.ExecuteNonQuery();
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: (exeChange): {ex.Message}");
        }
        return affectedRows;
    }
}
