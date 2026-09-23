namespace WindowPosition.Core;

public enum WindowEdge
{
    Up,
    Down,
    Left,
    Right
}

/// <summary>Aligns the visible window frame without changing its outer size.</summary>
public static class WindowAlignment
{
    public static bool TryAlign(WindowBounds outer, WindowBounds visible, WindowBounds workArea,
        WindowEdge edge, out WindowBounds aligned, out string? error)
    {
        aligned = outer;
        error = null;
        if (!IsValid(outer) || !IsValid(visible) || !IsValid(workArea) || !Enum.IsDefined(edge)
            || visible.X < outer.X || visible.Y < outer.Y
            || Right(visible) > Right(outer) || Bottom(visible) > Bottom(outer))
        {
            error = "ウィンドウまたはモニターの位置とサイズを取得できませんでした。再度キャプチャしてください。";
            return false;
        }

        if (visible.Width > workArea.Width || visible.Height > workArea.Height)
        {
            error = "現在のウィンドウサイズがモニターの作業領域より大きいため、サイズを保ったまま端に寄せられません。ウィンドウを小さくしてから再度お試しください。";
            return false;
        }

        // Clamp the other axis as well, so a partly off-screen window ends up
        // fully visible. Sequential vertical/horizontal clicks retain the corner.
        var visibleX = Math.Clamp((long)visible.X, workArea.X, Right(workArea) - visible.Width);
        var visibleY = Math.Clamp((long)visible.Y, workArea.Y, Bottom(workArea) - visible.Height);
        switch (edge)
        {
            case WindowEdge.Up: visibleY = workArea.Y; break;
            case WindowEdge.Down: visibleY = Bottom(workArea) - visible.Height; break;
            case WindowEdge.Left: visibleX = workArea.X; break;
            case WindowEdge.Right: visibleX = Right(workArea) - visible.Width; break;
        }

        // GetWindowRect includes invisible resize borders. Keep those offsets
        // when converting the visible target back to SetWindowPos coordinates.
        var outerX = (long)outer.X + visibleX - visible.X;
        var outerY = (long)outer.Y + visibleY - visible.Y;
        if (outerX < int.MinValue || outerY < int.MinValue
            || outerX + outer.Width > int.MaxValue || outerY + outer.Height > int.MaxValue)
        {
            error = "移動先の座標が Windows の範囲外になるため、端に寄せられません。";
            return false;
        }

        aligned = new WindowBounds((int)outerX, (int)outerY, outer.Width, outer.Height);
        return true;
    }

    private static bool IsValid(WindowBounds bounds) => bounds.Width > 0 && bounds.Height > 0
        && Right(bounds) <= int.MaxValue && Bottom(bounds) <= int.MaxValue;

    private static long Right(WindowBounds bounds) => (long)bounds.X + bounds.Width;
    private static long Bottom(WindowBounds bounds) => (long)bounds.Y + bounds.Height;
}
