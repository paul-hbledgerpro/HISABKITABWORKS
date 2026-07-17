using System;
using System.Collections.Concurrent;
using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.UI.Services;

/// <summary>
/// Manages database connections for multi-store support.
/// When a store has its own ConnectionString, creates DbContext instances
/// that connect to that store's database instead of the default one.
/// </summary>
public class StoreConnectionService
{
    private readonly IDbContextFactory<AppDbContext> _defaultFactory;
    private readonly string _defaultConnectionString;
    private readonly bool _useSqlServer;
    // Cache of store-specific connection strings (StoreId -> ConnectionString)
    private readonly ConcurrentDictionary<int, string> _storeConnections = new();

    // Current active store ID
    public int CurrentStoreId { get; set; } = 1;

    public StoreConnectionService(
        IDbContextFactory<AppDbContext> defaultFactory,
        string defaultConnectionString,
        bool useSqlServer)
    {
        _defaultFactory = defaultFactory;
        _defaultConnectionString = defaultConnectionString;
        _useSqlServer = useSqlServer;
    }

    /// <summary>
    /// Register a store's connection string. Call this when loading stores from DB.
    /// </summary>
    public void RegisterStore(int storeId, string? connectionString)
    {
        if (!string.IsNullOrEmpty(connectionString))
            _storeConnections[storeId] = connectionString;
        else
            _storeConnections.TryRemove(storeId, out _);
    }

    /// <summary>
    /// Atomically refresh the complete licensed store-to-database map after a
    /// replacement license is installed while the desktop app is still open.
    /// </summary>
    public void ReplaceStoreConnections(IReadOnlyDictionary<int, string> connections)
    {
        _storeConnections.Clear();
        foreach (var pair in connections)
            RegisterStore(pair.Key, pair.Value);
    }

    /// <summary>
    /// Get the connection string for a specific store.
    /// Returns the store-specific one if registered, otherwise the default.
    /// </summary>
    public string GetConnectionString(int storeId)
    {
        return _storeConnections.TryGetValue(storeId, out var connStr)
            ? connStr
            : _defaultConnectionString;
    }

    /// <summary>
    /// Get the connection string for the current store.
    /// </summary>
    public string GetCurrentConnectionString()
    {
        return GetConnectionString(CurrentStoreId);
    }

    /// <summary>
    /// Create a DbContext for the current store.
    /// If the current store has its own connection string, creates a context
    /// connected to that database. Otherwise uses the default factory.
    /// </summary>
    public AppDbContext CreateDbContext()
    {
        return CreateDbContext(CurrentStoreId);
    }

    /// <summary>
    /// Create a DbContext for a specific store.
    /// </summary>
    public AppDbContext CreateDbContext(int storeId)
    {
        if (_storeConnections.TryGetValue(storeId, out var connStr))
        {
            // Store has its own connection — create a new context with that connection
            var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
            if (_useSqlServer)
                optionsBuilder.UseSqlServer(connStr);
            else
                optionsBuilder.UseSqlite(connStr);

            return new AppDbContext(optionsBuilder.Options);
        }

        // Use default factory (original database)
        return _defaultFactory.CreateDbContext();
    }

    /// <summary>
    /// Check if a store uses a different database than the default.
    /// </summary>
    public bool HasCustomConnection(int storeId)
    {
        return _storeConnections.ContainsKey(storeId);
    }
}
