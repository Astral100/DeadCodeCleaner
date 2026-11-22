namespace Fixture.Domain;

// Partial-method trap: FormatWidget has a definition part and an implementation part. When it
// orphans, BOTH must be deleted, or the leftover half cannot compile.
public partial class WidgetFormatter
{
    public partial string FormatWidget();

    public string Plain()
    {
        return "plain";
    }
}

public partial class WidgetFormatter
{
    public partial string FormatWidget()
    {
        return "widget!";
    }
}
