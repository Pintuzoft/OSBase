using System;
using System.Collections.Generic;
using System.Linq;

namespace OSBase.Helpers;

/// <summary>
/// Validates and executes server commands safely.
/// Prevents command injection attacks by maintaining a whitelist of allowed commands.
/// </summary>
public class CommandValidator {
    private static readonly HashSet<string> AllowedCommandPrefixes = new(StringComparer.OrdinalIgnoreCase) {
        // Core CS2 commands (mp_ = map, sv_ = server, sm_ = SourceMod)
        "mp_", "sv_", "sm_", 
        
        // Common server management
        "say", "say_team",
        "changelevel", "map",
        "kick", "ban", "unban",
        "status", "stats",
        
        // Game state
        "mp_warmuptime", "mp_maxrounds", "mp_buytime",
        "mp_restartgame", "mp_pause_match",
        
        // Demo & logging
        "tv_", "demo_",
        
        // Server config
        "exec", "config",
    };

    private static readonly HashSet<string> DangerousPatterns = new(StringComparer.OrdinalIgnoreCase) {
        // Prevent shell injection
        "||", "&&", ";", "|", "&", "$", "`", "$(", 
        
        // Prevent file access
        "../", "..\\",
        
        // Prevent execution of external scripts
        "system", "shell",
    };

    private readonly string auditLogPrefix;
    private readonly bool enableAuditLog;

    public CommandValidator(bool enableAuditLog = true) {
        this.enableAuditLog = enableAuditLog;
        auditLogPrefix = "[AUDIT] Command Validator";
    }

    /// <summary>
    /// Validates a command and returns whether it's safe to execute.
    /// Logs denied commands for security auditing.
    /// </summary>
    public bool IsCommandSafe(string command, out string? reason) {
        reason = null;

        // Empty/whitespace check
        if (string.IsNullOrWhiteSpace(command)) {
            reason = "Command is empty";
            return false;
        }

        var trimmedCommand = command.Trim();

        // Check for dangerous patterns first
        if (DangerousPatterns.Any(pattern => trimmedCommand.Contains(pattern, StringComparison.OrdinalIgnoreCase))) {
            reason = "Command contains dangerous patterns (injection attempt)";
            LogDeniedCommand(command, reason);
            return false;
        }

        // Extract command prefix (first word before space)
        var commandPrefix = trimmedCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        if (string.IsNullOrEmpty(commandPrefix)) {
            reason = "Could not extract command prefix";
            return false;
        }

        // Check whitelist
        var isAllowed = AllowedCommandPrefixes.Any(allowed =>
            commandPrefix.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)
        );

        if (!isAllowed) {
            reason = $"Command prefix '{commandPrefix}' not in whitelist";
            LogDeniedCommand(command, reason);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Safely executes a command after validation.
    /// Returns true if executed, false if blocked.
    /// </summary>
    public bool ExecuteSafeCommand(string command, Action<string> executionCallback) {
        if (!IsCommandSafe(command, out var reason)) {
            Console.WriteLine($"{auditLogPrefix} [BLOCKED] {reason}: {command}");
            return false;
        }

        try {
            if (enableAuditLog) {
                Console.WriteLine($"{auditLogPrefix} [ALLOWED] Executing: {command}");
            }

            executionCallback(command);
            return true;
        } catch (Exception ex) {
            Console.WriteLine($"{auditLogPrefix} [ERROR] Failed to execute command: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets the current whitelist of allowed command prefixes.
    /// Useful for debugging/documentation.
    /// </summary>
    public IEnumerable<string> GetAllowedPrefixes() => AllowedCommandPrefixes.OrderBy(p => p);

    private void LogDeniedCommand(string command, string reason) {
        if (!enableAuditLog) return;

        Console.WriteLine($"{auditLogPrefix} [DENIED] {reason}");
        Console.WriteLine($"{auditLogPrefix} [COMMAND] {command}");
    }
}
