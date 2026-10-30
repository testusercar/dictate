using System.Diagnostics;
using System.Text.RegularExpressions;
using Interop.UIAutomationClient;

namespace FlowKey;

/// <summary>
/// Puts the cursor in the app's text box (Grok Bot's "Prompt") once its window is in front, through
/// UI Automation. It only ever moves focus: it never clicks or types. If no suitable text box turns
/// up in time, nothing happens.
/// </summary>
internal static partial class TextBoxFocus
{
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_NamePropertyId = 30005;
    private const int UIA_IsKeyboardFocusablePropertyId = 30009;
    private const int UIA_IsEnabledPropertyId = 30010;
    private const int UIA_IsPasswordPropertyId = 30019;
    private const int UIA_ValueIsReadOnlyPropertyId = 30046;
    private const int UIA_EditControlTypeId = 50004;

    private static readonly object Gate = new();
    private static IUIAutomation? _uia;
    // The text box found last time, per window, so a later press doesn't search the page again.
    private static readonly Dictionary<IntPtr, IUIAutomationElement> Cache = new();

    [GeneratedRegex("prompt|message|ask|chat|search|type", RegexOptions.IgnoreCase)]
    private static partial Regex LikelyInputName();

    /// <summary>
    /// Focuses the window's text box, trying for up to <paramref name="patience"/> (an app that is
    /// still starting shows its text box late). Says what happened, for the log.
    /// </summary>
    public static string Focus(Func<IntPtr> window, TimeSpan patience)
    {
        var clock = Stopwatch.StartNew();
        lock (Gate)
        {
            try
            {
                _uia ??= new CUIAutomation8();
                while (true)
                {
                    var hwnd = window();
                    if (hwnd != IntPtr.Zero)
                    {
                        if (AlreadyInTextBox(hwnd)) return $"cursor already in the text box ({clock.ElapsedMilliseconds} ms)";
                        if (Find(hwnd) is { } box)
                        {
                            box.SetFocus();
                            return $"cursor in '{box.CurrentName}' in {clock.ElapsedMilliseconds} ms";
                        }
                    }
                    if (clock.Elapsed >= patience) return $"no text box found in {clock.ElapsedMilliseconds} ms; left focus alone";
                    Thread.Sleep(100);
                }
            }
            catch (Exception e)
            {
                return $"couldn't reach the text box: {e.Message}";
            }
        }
    }

    private static bool AlreadyInTextBox(IntPtr hwnd)
    {
        var focused = _uia!.GetFocusedElement();
        if (focused is null || focused.CurrentControlType != UIA_EditControlTypeId) return false;
        var window = _uia.ElementFromHandle(hwnd);
        return focused.CurrentProcessId == window.CurrentProcessId && IsUsable(focused);
    }

    private static IUIAutomationElement? Find(IntPtr hwnd)
    {
        if (Cache.TryGetValue(hwnd, out var cached))
        {
            try
            {
                if (IsUsable(cached)) return cached;
            }
            catch (Exception)
            {
                // Gone (the page changed); search again.
            }
            Cache.Remove(hwnd);
        }

        var window = _uia!.ElementFromHandle(hwnd);
        var condition = _uia.CreateAndCondition(
            _uia.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_EditControlTypeId),
            _uia.CreatePropertyCondition(UIA_IsKeyboardFocusablePropertyId, true));
        var all = window.FindAll(TreeScope.TreeScope_Descendants, condition);

        // A box named like a chat input wins; otherwise the lowest one on screen, where chat inputs sit.
        IUIAutomationElement? best = null;
        var bestScore = double.MinValue;
        for (var i = 0; i < all.Length; i++)
        {
            var element = all.GetElement(i);
            if (!IsUsable(element)) continue;
            var rect = element.CurrentBoundingRectangle;
            var score = rect.bottom + (LikelyInputName().IsMatch(element.CurrentName ?? "") ? 100_000 : 0);
            if (score <= bestScore) continue;
            best = element;
            bestScore = score;
        }
        if (best is not null) Cache[hwnd] = best;
        return best;
    }

    private static bool IsUsable(IUIAutomationElement element) =>
        element.GetCurrentPropertyValue(UIA_IsEnabledPropertyId) is true
        && element.GetCurrentPropertyValue(UIA_IsPasswordPropertyId) is not true
        && element.GetCurrentPropertyValue(UIA_ValueIsReadOnlyPropertyId) is not true
        && element.CurrentBoundingRectangle is var r && r.right > r.left && r.bottom > r.top;
}
