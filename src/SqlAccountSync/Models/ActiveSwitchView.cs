namespace SqlAccountSync.Models;

public sealed class ActiveSwitchView
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
    public bool CanToggle { get; set; }
    public bool ConfirmSubs { get; set; }
    public int ActiveSubCount { get; set; }
    public string? FormPage { get; set; }
}
