namespace DesktopApp.Models;

// Minimal models to deserialize Xtream Codes player_api.php response
// Sample JSON structure:
// {
//   "user_info": { "auth": true, "status":"Active", "exp_date":"1700000000", ... },
//   "server_info": { "url":"example.com", "port":"8080", "https_port":"443", ... }
// }

public sealed class PlayerInfo
{
    public UserInfo? user_info { get; set; }
    public ServerInfo? server_info { get; set; }
}

public sealed class UserInfo
{
    public bool auth { get; set; }
    public string? status { get; set; }
    public string? message { get; set; }
    public string? exp_date { get; set; }
    public string? is_trial { get; set; }
    public string? max_connections { get; set; }
    public string? created_at { get; set; }
    public string? active_cons { get; set; }
    public string? username { get; set; }
    public string? password { get; set; }
}

public sealed class ServerInfo
{
    public string? url { get; set; }
    public string? port { get; set; }
    public string? https_port { get; set; }
    public string? time_now { get; set; }
}
