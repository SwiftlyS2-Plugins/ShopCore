using SwiftlyS2.Shared.Players;

namespace Cookies.Contract;

public interface IPlayerCookiesAPIv1
{
    /// <summary>
    /// Gets a variable from the player's data storage.
    /// </summary>
    /// <typeparam name="T">The type of the variable.</typeparam>
    /// <param name="key">The key of the variable.</param>
    /// <returns>The value of the variable, or null if it doesn't exist.</returns>
    public T? Get<T>(IPlayer player, string key);

    /// <summary>
    /// Gets a variable from the player's data storage by steamid.
    /// </summary>
    /// <typeparam name="T">The type of the variable.</typeparam>
    /// <param name="steamid">The steamid of the player.</param>
    /// <param name="key">The key of the variable.</param>
    /// <returns>The value of the variable, or null if it doesn't exist.</returns>
    public T? Get<T>(long steamid, string key);

    /// <summary>
    /// Gets a variable from the player's data storage, or returns a default value if it doesn't exist.
    /// </summary>
    /// <typeparam name="T">The type of the variable.</typeparam>
    /// <param name="player">The player object.</param>
    /// <param name="key">The key of the variable.</param>
    /// <param name="defaultValue">The default value to return if the variable doesn't exist.</param>
    /// <returns>The value of the variable, or the default value if it doesn't exist.</returns>
    public T? GetOrDefault<T>(IPlayer player, string key, T defaultValue);

    /// <summary>
    /// Gets a variable from the player's data storage by steamid, or returns a default value if it doesn't exist.
    /// </summary>
    /// <typeparam name="T">The type of the variable.</typeparam>
    /// <param name="steamid">The steamid of the player.</param>
    /// <param name="key">The key of the variable.</param>
    /// <param name="defaultValue">The default value to return if the variable doesn't exist.</param>
    /// <returns>The value of the variable, or the default value if it doesn't exist.</returns>
    public T? GetOrDefault<T>(long steamid, string key, T defaultValue);

    /// <summary>
    /// Checks if a variable exists in the player's data storage.
    /// </summary>
    /// <param name="key">The key of the variable.</param>
    /// <returns>True if the variable exists, false otherwise.</returns>
    public bool Has(IPlayer player, string key);

    /// <summary>
    /// Checks if a variable exists in the player's data storage by steamid.
    /// </summary>
    /// <param name="steamid">The steamid of the player.</param>
    /// <param name="key">The key of the variable.</param>
    /// <returns>True if the variable exists, false otherwise.</returns>
    public bool Has(long steamid, string key);

    /// <summary>
    /// Sets a variable in the player's data storage.
    /// </summary>
    /// <typeparam name="T">The type of the variable.</typeparam>
    /// <param name="key">The key of the variable.</param>
    /// <param name="value">The value of the variable.</param>
    public void Set<T>(IPlayer player, string key, T value);

    /// <summary>
    /// Sets a variable in the player's data storage by steamid.
    /// </summary>
    /// <typeparam name="T">The type of the variable.</typeparam>
    /// <param name="steamid">The steamid of the player.</param>
    /// <param name="key">The key of the variable.</param>
    /// <param name="value">The value of the variable.</param>
    public void Set<T>(long steamid, string key, T value);

    /// <summary>
    /// Clears all stored data for the player.
    /// </summary>
    public void Clear(IPlayer player);

    /// <summary>
    /// Clears all stored data for the player by steamid.
    /// </summary>
    /// <param name="steamid">The steamid of the player.</param>
    public void Clear(long steamid);

    /// <summary>
    /// Unsets a variable from the player's data storage.
    /// </summary>
    /// <param name="key">The key of the variable.</param>
    public void Unset(IPlayer player, string key);

    /// <summary>
    /// Unsets a variable from the player's data storage by steamid.
    /// </summary>
    /// <param name="steamid">The steamid of the player.</param>
    /// <param name="key">The key of the variable.</param>
    public void Unset(long steamid, string key);

    /// <summary>
    /// Loads the player's cookies into memory.
    /// </summary>
    /// <param name="player">The player whose cookies should be loaded.</param>
    public void Load(IPlayer player);

    /// <summary>
    /// Saves the player's cookies to the database.
    /// </summary>
    /// <param name="player">The player whose cookies should be saved.</param>
    public void Save(IPlayer player);

    /// <summary>
    /// Saves the player's cookies to the database by steamid.
    /// </summary>
    /// <param name="steamid">The steamid of the player.</param>
    public void Save(long steamid);
}