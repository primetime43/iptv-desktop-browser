namespace DesktopApp.Models;

public sealed class Category
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int ParentId { get; set; }
    public string? ImageUrl { get; set; }
}
