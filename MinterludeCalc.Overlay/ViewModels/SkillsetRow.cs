using Avalonia.Media;

namespace MinterludeCalc.Overlay.ViewModels;

public class SkillsetRow : ObservableObject
{
    public string Name { get; }

    private double _value;
    public double Value
    {
        get => _value;
        set
        {
            SetField(ref _value, value);
            Raise(nameof(DisplayValue));
            Raise(nameof(Color));
        }
    }

    public string DisplayValue => Value.ToString("F2");
    public IBrush Color => MsdColor.ForValue(Value);

    public SkillsetRow(string name, double value = 0.0)
    {
        Name = name;
        _value = value;
    }
}
