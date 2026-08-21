namespace RazorLib.Components;

/// <summary>
/// Code-behind for Counter.razor. Overriding OnInitialized only compiles when the Razor
/// generator has produced the other half of this partial class (which derives from
/// ComponentBase). Without it the compiler reports CS0115/CS0117 on a project that builds
/// clean — the phantom-diagnostics symptom of issue #399.
/// </summary>
public partial class Counter
{
    private int _currentCount;

    protected override void OnInitialized() => base.OnInitialized();

    private void IncrementCount() => _currentCount++;
}
