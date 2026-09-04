using MySqlConnector;
using UniFlow.Common.Models;

namespace UniFlow.Common.Services;

public class MySqlConnectionFactory
{
    private readonly string _connectionString;

    public MySqlConnectionFactory(AptioDatabaseConfig cfg)
    {
        _connectionString = $"Server={cfg.Host};Port={cfg.Port};Database={cfg.Database};" +
                            $"User={cfg.User};Password={cfg.Password};CharSet=utf8mb4;AllowUserVariables=True;";
    }

    public MySqlConnectionFactory(string host, int port, string db, string user, string password)
    {
        _connectionString = $"Server={host};Port={port};Database={db};" +
                            $"User={user};Password={password};CharSet=utf8mb4;AllowUserVariables=True;";
    }

    public MySqlConnection Create() => new(_connectionString);
}