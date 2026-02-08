namespace Cookies.Contract;

public interface IServerCookiesAPIv1
{
    /// <summary>
    /// Gets a variable from the server's data storage.
    /// </summary>
    /// <typeparam name="T">The type of the variable.</typeparam>
    /// <param name="key">The key of the variable.</param>
    /// <returns>The value of the variable, or null if it doesn't exist.</returns>
    public T? Get<T>(string key);

    /// <summary>
    /// Gets a variable from the server's data storage, or returns a default value if it doesn't exist.
    /// </summary>
    /// <typeparam name="T">The type of the variable.</typeparam>
    /// <param name="key">The key of the variable.</param>
    /// <param name="defaultValue">The default value to return if the variable doesn't exist.</param>
    /// <returns>The value of the variable, or the default value if it doesn't exist.</returns>
    public T? GetOrDefault<T>(string key, T defaultValue);

    /// <summary>
    /// Checks if a variable exists in the server's data storage.
    /// </summary>
    /// <param name="key">The key of the variable.</param>
    /// <returns>True if the variable exists, false otherwise.</returns>
    public bool Has(string key);

    /// <summary>
    /// Sets a variable in the server's data storage.
    /// </summary>
    /// <typeparam name="T">The type of the variable.</typeparam>
    /// <param name="key">The key of the variable.</param>
    /// <param name="value">The value of the variable.</param>
    public void Set<T>(string key, T value);

    /// <summary>
    /// Clears all stored data for the server.
    /// </summary>
    public void Clear();

    /// <summary>
    /// Unsets a variable from the server's data storage.
    /// </summary>
    /// <param name="key">The key of the variable.</param>
    public void Unset(string key);

    /// <summary>
    /// Loads the server's cookies into memory.
    /// </summary>
    public void Load();

    /// <summary>
    /// Saves the server's cookies to the database.
    /// </summary>
    public void Save();
}