using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace DpsInServerBar.UI;

/// <summary>
/// Screenshot of a window (main window, settings, compact meter, opener practice), saved as PNG in
/// Pictures\Raid Companion. The window is brought onto the game screen, made to ignore the mouse (no hover, no
/// tooltip in the picture), optionally grown to its whole content (captured in slices when taller than the screen),
/// then put back where it was.
/// </summary>
public sealed class WindowShot
{
    private enum Stage { None, Pending, Grow, Slice, Capturing, Saving, Restore }

    private readonly Window window;
    private Stage stage;
    private int frames;
    private string name = string.Empty;
    private Func<float?>? fullHeight;
    private Action? started;
    private bool grow;
    private bool hadNoInputs;
    private bool release;
    private Vector2 savedPos, savedSize;
    private float height;
    private int slice;
    private readonly List<WindowScreenshot.Slice> slices = new();
    private Task<IDalamudTextureWrap>? capture;
    private Task<string>? saving;

    public WindowShot(Window window) => this.window = window;

    /// <summary>No screenshot of this window under way.</summary>
    public bool Ready => stage == Stage.None;

    /// <summary>The window is held in place (grown, mouse ignored) while its picture is taken.</summary>
    public bool Holding => stage is Stage.Grow or Stage.Slice or Stage.Capturing or Stage.Saving;

    /// <summary>Takes a picture of the window in a few frames (once the menu that asked for it is closed).</summary>
    /// <param name="view">Name in the file name.</param>
    /// <param name="contentHeight">Height of the whole content (pixels, from the last frame), to grow the window to it;
    /// null to take the window as it is. The function may return null too: the window as it is.</param>
    /// <param name="onStart">Called once the menu is closed, just before the content height is first read.</param>
    public void Request(string view, Func<float?>? contentHeight = null, Action? onStart = null)
    {
        if (stage != Stage.None)
            return;
        name = view;
        fullHeight = contentHeight;
        started = onStart;
        stage = Stage.Pending;
        frames = 0;
    }

    /// <summary>A camera button in the window's title bar.</summary>
    public TitleBarButton TitleBarButton(Func<string> view, Func<float?>? contentHeight = null) => new()
    {
        Icon = Dalamud.Interface.FontAwesomeIcon.Camera,
        IconOffset = new Vector2(1.5f, 1),
        Click = button =>
        {
            if (button == ImGuiMouseButton.Left)
                Request(view(), contentHeight);
        },
        ShowTooltip = () => ImGui.SetTooltip(Loc.T("Screenshot: saves a picture of this window in Pictures\\Raid Companion.")),
        Priority = -1,
    };

    /// <summary>Call at the end of the window's PreDraw: holds the window in place during the screenshot.</summary>
    public void PreDraw()
    {
        if (Holding)
        {
            var screen = ImGui.GetMainViewport();
            ImGuiHelpers.ForceNextWindowMainViewport();
            window.Flags |= ImGuiWindowFlags.NoInputs;
            var x = Math.Clamp(savedPos.X, screen.Pos.X, Math.Max(screen.Pos.X, screen.Pos.X + screen.Size.X - savedSize.X));
            window.Position = new Vector2(MathF.Floor(x), screen.Pos.Y + SliceTop(slice, screen.Size.Y));
            window.PositionCondition = ImGuiCond.Always;
            if (grow)
            {
                window.Size = new Vector2(savedSize.X, height) / ImGuiHelpers.GlobalScale;
                window.SizeCondition = ImGuiCond.Always;
                window.SizeConstraints = new WindowSizeConstraints
                {
                    MinimumSize = window.SizeConstraints?.MinimumSize ?? Vector2.Zero,
                    MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
                };
            }

            return;
        }

        if (stage == Stage.Restore)
        {
            if (!hadNoInputs)
                window.Flags &= ~ImGuiWindowFlags.NoInputs;
            window.Position = savedPos;
            window.PositionCondition = ImGuiCond.Always;
            if (grow)
            {
                window.Size = savedSize / ImGuiHelpers.GlobalScale;
                window.SizeCondition = ImGuiCond.Always;
            }

            stage = Stage.None;
            release = true;
            return;
        }

        // The frame after: the window is free to move again.
        if (release)
        {
            window.Position = null;
            release = false;
        }
    }

    /// <summary>Call at the start of the window's Draw (the window itself, not a child, must be the current one).</summary>
    public void Step()
    {
        if (stage is Stage.None or Stage.Restore)
            return;

        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var screen = ImGui.GetMainViewport();
        switch (stage)
        {
            case Stage.Pending:
                // A few frames for the menu to close.
                if (++frames < 3)
                    return;
                savedPos = pos;
                savedSize = size;
                hadNoInputs = window.Flags.HasFlag(ImGuiWindowFlags.NoInputs);
                started?.Invoke();
                var full = fullHeight?.Invoke();
                grow = full is not null;
                height = MathF.Ceiling(full ?? size.Y);
                slice = 0;
                stage = grow ? Stage.Grow : Stage.Slice;
                frames = 0;
                return;

            case Stage.Grow:
                // The layout settles at the new height (what the content needs may change once).
                height = Math.Max(height, MathF.Ceiling(fullHeight?.Invoke() ?? 0));
                if (++frames >= 4)
                {
                    stage = Stage.Slice;
                    frames = 0;
                }

                return;

            case Stage.Slice:
                // The window in place for this slice: capture the whole game screen.
                if (++frames < 3)
                    return;
                slices.Add(new WindowScreenshot.Slice(null!, pos - screen.Pos));
                capture = WindowScreenshot.CaptureScreen(screen.ID);
                stage = Stage.Capturing;
                return;

            case Stage.Capturing when capture is { IsCompleted: true } done:
                capture = null;
                if (!done.IsCompletedSuccessfully)
                {
                    WindowScreenshot.ReportError(done.Exception?.GetBaseException());
                    foreach (var s in slices)
                        s.Screen?.Dispose();
                    slices.Clear();
                    stage = Stage.Restore;
                    return;
                }

                slices[^1] = slices[^1] with { Screen = done.Result };
                if (++slice < SliceCount(screen.Size.Y))
                {
                    stage = Stage.Slice;
                    frames = 0;
                    return;
                }

                saving = WindowScreenshot.StitchAndSaveAsync([.. slices], size, screen.Size, ImGui.GetStyle().WindowRounding, name);
                slices.Clear();
                stage = Stage.Saving;
                return;

            case Stage.Saving when saving is { IsCompleted: true } task:
                if (task.IsCompletedSuccessfully)
                    WindowScreenshot.Report(task.Result);
                else
                    WindowScreenshot.ReportError(task.Exception?.GetBaseException());
                saving = null;
                stage = Stage.Restore;
                return;
        }
    }

    // Slices of the screen's height, the last one aligned on the window's bottom. Top of slice k, from the screen top.
    private int SliceCount(float screenHeight) => height <= screenHeight ? 1 : (int)Math.Ceiling(height / screenHeight);

    private float SliceTop(int k, float screenHeight)
    {
        if (SliceCount(screenHeight) == 1)
        {
            var screen = ImGui.GetMainViewport();
            return MathF.Floor(Math.Clamp(savedPos.Y - screen.Pos.Y, 0, Math.Max(0, screenHeight - height)));
        }

        return k < SliceCount(screenHeight) - 1 ? -k * screenHeight : screenHeight - height;
    }
}
