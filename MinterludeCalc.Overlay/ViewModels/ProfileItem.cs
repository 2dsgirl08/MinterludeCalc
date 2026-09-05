namespace MinterludeCalc.Overlay.ViewModels;

/// <summary>A profile as the picker sees it. Id is what the store keys on; Name is what's displayed.</summary>
public class ProfileItem
{
    public string Id { get; }
    public string Name { get; }

    public ProfileItem(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public override string ToString() => Name;
}
